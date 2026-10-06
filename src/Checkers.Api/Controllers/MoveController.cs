using System.Diagnostics;
using Checkers.Api.Contracts;
using Checkers.Api.Logging;
using Checkers.Api.Moves;
using Checkers.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Checkers.Api.Controllers;

[ApiController]
[Route("v1/move")]
[Produces("application/json")]
public sealed class MoveController(MoveSuggestionService suggestions, LevelPolicy levels) : ControllerBase
{
    private const string SupportedGame = "checkers-8x8";
    private const string SupportedNotation = "PDN";

    /// <summary>
    /// Best move for a PDN position. 422 for an invalid request, 504 when hardTimeMs passes first.
    /// <c>Cache-Control: no-cache</c> skips the cached answer (the fresh one is still stored).
    /// </summary>
    [HttpPost("suggest")]
    [EnableRateLimiting(RateLimitPolicies.Engine)]
    [ProducesResponseType<SuggestMoveResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<SuggestMoveResponse>> Suggest(SuggestMoveRequest request)
    {
        var clock = Stopwatch.StartNew();
        var position = ParseSuggestRequest(request);
        var limits = levels.Resolve(request.Level, request.Limits);

        // hardTimeMs covers everything: waiting for a free worker, the tablebase probe and the search.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(limits.HardTimeMs);
        try
        {
            var bypassCache = Request.GetTypedHeaders().CacheControl?.NoCache == true;
            var response = await suggestions.SuggestAsync(position, limits, clock, deadline.Token, bypassCache);
            HttpContext.Features.Set(new EngineRequestMetrics(
                response.Depth, response.Nodes, response.Info.TablebaseHit, response.Info.Cached, response.Info.Level, response.Info.Worker, response.BestMove));
            return response;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            HttpContext.Features.Set(new EngineRequestMetrics(null, null, null, false, limits.Level, null, null));
            return Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Engine timeout",
                detail: $"No move within hardTimeMs = {limits.HardTimeMs} ms.");
        }
    }

    /// <summary>Is the move legal in the position? Also returns the position after it.</summary>
    [HttpPost("validate")]
    [ProducesResponseType<ValidateMoveResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public ActionResult<ValidateMoveResponse> Validate(ValidateMoveRequest request)
    {
        var position = PositionInput.Parse(request.Position, "position");
        if (string.IsNullOrWhiteSpace(request.Move))
        {
            throw new RequestValidationException("move", "move is required, e.g. \"22-18\" or \"15x22\".");
        }

        var legal = MoveGenerator.LegalMoves(position);
        switch (MoveNotation.FindAmong(legal, request.Move))
        {
            case [var move]:
                return new ValidateMoveResponse(true)
                {
                    Move = move.Notation,
                    ResultPosition = MoveGenerator.Apply(position, move).ToFen(),
                };

            case [_, _, ..] routes:
                // Legal, but the short form fits several capture routes with different results.
                var candidates = routes.Select(m => m.Notation).ToList();
                return new ValidateMoveResponse(true)
                {
                    Ambiguous = true,
                    Candidates = candidates,
                    Reason = $"'{request.Move}' fits {candidates.Count} capture routes; give the full path: {string.Join(" or ", candidates)}.",
                };

            default:
                return new ValidateMoveResponse(false) { Reason = ExplainIllegal(position, legal, request.Move) };
        }
    }

    private static Position ParseSuggestRequest(SuggestMoveRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.GameId is { } gameId && !gameId.Equals(SupportedGame, StringComparison.OrdinalIgnoreCase))
        {
            errors["gameId"] = [$"Only '{SupportedGame}' is supported."];
        }

        if (request.State?.Notation is { } notation && !notation.Equals(SupportedNotation, StringComparison.OrdinalIgnoreCase))
        {
            errors["state.notation"] = [$"Only '{SupportedNotation}' notation is supported."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        return PositionInput.Parse(request.State?.Position, "state.position");
    }

    private static string ExplainIllegal(Position position, IReadOnlyList<Move> legal, string text)
    {
        if (!MoveNotation.TryParseSquares(text, out _))
        {
            return $"'{text}' is not move notation; expected e.g. \"22-18\", \"15x22\" or \"15x22x31\".";
        }

        if (legal.Count == 0)
        {
            return $"{position.SideToMove} has no legal moves: the game is over.";
        }

        if (MoveNotation.FindIgnoringSeparator(legal, text) is [var meant, ..])
        {
            return $"Steps are written with '-' and captures with 'x'; did you mean {meant.Notation}?";
        }

        var options = string.Join(", ", legal.Select(m => m.Notation));
        return legal[0].IsCapture
            ? $"A capture is compulsory. Legal moves: {options}."
            : $"Not a legal move for {position.SideToMove}. Legal moves: {options}.";
    }
}
