using System.Numerics;
using Checkers.Api.Contracts;
using Checkers.Api.Moves;
using Checkers.Core;
using Microsoft.AspNetCore.Mvc;

namespace Checkers.Api.Controllers;

/// <summary>Rules helpers for clients such as the test board; they do not touch the engine.</summary>
[ApiController]
[Route("v1/position")]
[Produces("application/json")]
public sealed class PositionController : ControllerBase
{
    /// <summary>All legal moves with their full paths, captures and resulting positions.</summary>
    [HttpPost("moves")]
    [ProducesResponseType<LegalMovesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public ActionResult<LegalMovesResponse> Moves(LegalMovesRequest request)
    {
        var position = PositionInput.Parse(request.Position, "position");
        var moves = MoveGenerator.LegalMoves(position)
            .Select(m => new LegalMoveDto(
                m.Notation,
                m.Landings.Prepend(m.From).Select(s => s + 1).ToList(),
                Squares(m.Captured),
                m.Crowns,
                MoveGenerator.Apply(position, m).ToFen()))
            .ToList();

        return new LegalMovesResponse(position.ToFen(), position.SideToMove.ToPdn().ToString(), moves.Count == 0, moves);
    }

    private static List<int> Squares(uint mask)
    {
        var squares = new List<int>(BitOperations.PopCount(mask));
        for (var square = 0; square < Board.SquareCount; square++)
        {
            if ((mask & Board.Bit(square)) != 0)
            {
                squares.Add(square + 1);
            }
        }

        return squares;
    }
}
