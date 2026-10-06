using System.Diagnostics;
using Checkers.Api.Caching;
using Checkers.Api.Contracts;
using Checkers.Api.Engine;
using Checkers.Core;
using Checkers.Core.Worker;
using Microsoft.Extensions.Options;

namespace Checkers.Api.Moves;

/// <summary>
/// The per-request flow of <c>POST /v1/move/suggest</c> after the request has been validated:
/// cache, tablebase probe, search, legality check of the engine's answer, cache store.
/// </summary>
public sealed class MoveSuggestionService(
    IEnginePool pool,
    LruCache<string, SuggestMoveResponse> cache,
    IOptions<EngineOptions> engineOptions,
    ILogger<MoveSuggestionService> logger)
{
    private const int TablebaseProbeMaxDepth = 64;

    /// <summary>
    /// The worker gets a slightly smaller hard limit than the request, so that it returns its best
    /// move so far before the request deadline (504) is reached.
    /// </summary>
    private const int MinHardTimeMarginMs = 25;

    /// <summary>Below this much time left, a request whose worker died is not retried on another.</summary>
    private const int MinRetryBudgetMs = 50;

    private readonly EngineOptions _engine = engineOptions.Value;

    /// <param name="clock">Started when the request arrived; timeouts and timeMs are measured from it.</param>
    /// <param name="bypassCache">Do not answer from the cache (<c>Cache-Control: no-cache</c>); the fresh answer is still stored.</param>
    public async Task<SuggestMoveResponse> SuggestAsync(
        Position position, ResolvedLimits limits, Stopwatch clock, CancellationToken cancellationToken, bool bypassCache = false)
    {
        var fen = position.ToFen();
        var positionKey = "pdn:" + fen;
        // The canonical PDN plus the limits that shape the answer: a weak and a strong search of
        // the same position are different answers.
        var cacheKey = $"{positionKey}|{limits.Level}|{limits.MinDepth}-{limits.MaxDepth}|{limits.SoftTimeMs}";
        if (!bypassCache && cache.TryGet(cacheKey, out var cached))
        {
            return cached with { Info = cached.Info with { Cached = true, TimeMs = (int)clock.ElapsedMilliseconds, Worker = null } };
        }

        var legal = MoveGenerator.LegalMoves(position);
        if (legal.Count == 0)
        {
            throw new RequestValidationException("state.position", $"{position.SideToMove} has no legal moves: the game is over.");
        }

        var status = pool.Status;
        Answer answer;
        try
        {
            answer = await AskEngineAsync(position, fen, status, limits, clock, cancellationToken);
        }
        catch (EngineUnavailableException ex) when (Remaining(limits, clock) >= MinRetryBudgetMs && pool.Status.ReadyWorkers > 0)
        {
            // The worker died under this request; its restart is already scheduled. Another one can answer.
            logger.LogWarning("Engine worker failed during the request ({Error}); retrying once on another worker", ex.Message);
            answer = await AskEngineAsync(position, fen, status, limits, clock, cancellationToken);
        }

        var result = answer.Result;
        var (move, pv) = ChooseLegalMove(position, legal, result);
        var response = new SuggestMoveResponse(
            status.EngineType,
            move.Notation,
            pv,
            result.ScoreOrWdl,
            result.Depth,
            result.Nodes,
            positionKey,
            new SuggestMoveInfo(result.TablebaseHit, (int)clock.ElapsedMilliseconds)
            {
                Level = limits.Level,
                EngineName = status.EngineName,
                Wdl = result.Wdl,
                Score = result.Score,
                EngineTimeMs = result.EngineTimeMs,
                Worker = answer.WorkerId,
            });

        // An answer cut short by the deadline is served but not cached: the next identical request,
        // with its full budget, should get the full search.
        if (!answer.Truncated)
        {
            cache.Set(cacheKey, response);
        }

        return response;
    }

    private async Task<Answer> AskEngineAsync(
        Position position, string fen, EnginePoolStatus status, ResolvedLimits limits, Stopwatch clock, CancellationToken cancellationToken)
    {
        await using var lease = await pool.AcquireAsync(cancellationToken);
        await lease.Engine.SetPositionAsync(fen, cancellationToken);

        if (position.PieceCount <= status.TablebasePieces)
        {
            var probeLimits = Budget(new SearchLimits(0, TablebaseProbeMaxDepth, _engine.TablebaseTimeMs, 0, Tablebase: true), limits, clock);
            var probe = await lease.Engine.SearchAsync(probeLimits, cancellationToken);
            if (probe.TablebaseHit)
            {
                return new Answer(probe, lease.WorkerId, IsTruncated(probe, probeLimits, _engine.TablebaseTimeMs));
            }
        }

        var searchLimits = Budget(new SearchLimits(limits.MinDepth, limits.MaxDepth, limits.SoftTimeMs, 0, Tablebase: false), limits, clock);
        var result = await lease.Engine.SearchAsync(searchLimits, cancellationToken);
        return new Answer(result, lease.WorkerId, IsTruncated(result, searchLimits, limits.SoftTimeMs));
    }

    /// <summary>
    /// Stopped by the hard limit, or by a soft limit the remaining budget had shortened: the engine
    /// had less time than the limits in the cache key promise.
    /// </summary>
    private static bool IsTruncated(EngineSearchResult result, SearchLimits sent, int wantedSoftTimeMs) =>
        result.StopReason == WorkerStopReasons.HardTime
        || (result.StopReason == WorkerStopReasons.SoftTime && sent.SoftTimeMs < wantedSoftTimeMs);

    /// <summary>
    /// The engine's move is only trusted after the rules engine confirms it is legal here. If it is
    /// not, the first pv move is tried — the only other move the engine proposed for this side in
    /// this position (later pv moves belong to later positions). Otherwise the request fails with 500.
    /// </summary>
    private (Move Move, IReadOnlyList<string> Pv) ChooseLegalMove(Position position, IReadOnlyList<Move> legal, EngineSearchResult result)
    {
        if (MoveNotation.FindAmong(legal, result.BestMove) is [var best, ..])
        {
            return (best, result.Pv is [var first, ..] && first == best.Notation ? result.Pv : [best.Notation]);
        }

        if (result.Pv is [var pvFirst, ..] && MoveNotation.FindAmong(legal, pvFirst) is [var fallback, ..])
        {
            logger.LogWarning("Engine move '{BestMove}' is not legal in {Position}; using the pv move {Move}", result.BestMove, position.ToFen(), fallback.Notation);
            return (fallback, result.Pv);
        }

        throw new EngineFailureException($"The engine returned no legal move for {position.ToFen()} (best move '{result.BestMove}', pv '{string.Join(' ', result.Pv)}').");
    }

    private static int Remaining(ResolvedLimits limits, Stopwatch clock)
    {
        var margin = Math.Max(MinHardTimeMarginMs, limits.HardTimeMs / 10);
        return limits.HardTimeMs - margin - (int)clock.ElapsedMilliseconds;
    }

    private static SearchLimits Budget(SearchLimits search, ResolvedLimits limits, Stopwatch clock)
    {
        var remaining = Math.Max(1, Remaining(limits, clock));
        return search with { SoftTimeMs = Math.Min(search.SoftTimeMs, remaining), HardTimeMs = remaining };
    }

    private sealed record Answer(EngineSearchResult Result, int WorkerId, bool Truncated);
}
