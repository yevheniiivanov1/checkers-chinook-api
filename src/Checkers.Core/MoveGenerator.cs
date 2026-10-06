namespace Checkers.Core;

/// <summary>
/// Legal move generation for English (American) checkers: men step and jump forward only,
/// kings move one square in any diagonal direction, capturing is compulsory, a capture must be
/// continued while further jumps exist (any capture route may be chosen, no majority rule), and a
/// man that reaches the far rank is crowned and the move ends there.
/// </summary>
public static class MoveGenerator
{
    private const int MaxJumps = Position.MaxPiecesPerSide;

    public static IReadOnlyList<Move> LegalMoves(Position position)
    {
        var moves = new List<Move>();
        var own = position.PiecesOf(position.SideToMove);

        for (var square = 0; square < Board.SquareCount; square++)
        {
            if ((own & Board.Bit(square)) != 0)
            {
                AddCaptures(position, square, moves);
            }
        }

        if (moves.Count > 0)
        {
            return moves;
        }

        for (var square = 0; square < Board.SquareCount; square++)
        {
            if ((own & Board.Bit(square)) != 0)
            {
                AddSteps(position, square, moves);
            }
        }

        return moves;
    }

    public static bool HasCapture(Position position) =>
        LegalMoves(position) is [{ IsCapture: true }, ..];

    /// <summary>Plays a move generated for <paramref name="position"/> and passes the turn.</summary>
    public static Position Apply(Position position, Move move)
    {
        var side = position.SideToMove;
        var fromBit = Board.Bit(move.From);
        var toBit = Board.Bit(move.To);
        var wasKing = (position.Kings & fromBit) != 0;

        var own = (position.PiecesOf(side) & ~fromBit) | toBit;
        var opponent = position.PiecesOf(side.Opponent()) & ~move.Captured;
        var kings = position.Kings & ~fromBit & ~move.Captured;
        if (wasKing || move.Crowns)
        {
            kings |= toBit;
        }

        return side == Side.Black
            ? new Position(own, opponent, kings, Side.White)
            : new Position(opponent, own, kings, Side.Black);
    }

    /// <summary>Number of leaf positions at <paramref name="depth"/> plies; the standard movegen check.</summary>
    public static long Perft(Position position, int depth)
    {
        if (depth == 0)
        {
            return 1;
        }

        var moves = LegalMoves(position);
        if (depth == 1)
        {
            return moves.Count;
        }

        long nodes = 0;
        foreach (var move in moves)
        {
            nodes += Perft(Apply(position, move), depth - 1);
        }

        return nodes;
    }

    private static void AddSteps(Position position, int from, List<Move> moves)
    {
        var side = position.SideToMove;
        var king = position.IsKing(from);
        foreach (var direction in Board.DirectionsFor(side, king))
        {
            var to = Board.Neighbour(from, direction);
            if (to < 0 || (position.Occupied & Board.Bit(to)) != 0)
            {
                continue;
            }

            var crowns = !king && (Board.KingRow(side) & Board.Bit(to)) != 0;
            moves.Add(new Move(from, [to], 0, crowns));
        }
    }

    private static void AddCaptures(Position position, int from, List<Move> moves)
    {
        var context = new JumpContext(
            position.SideToMove,
            position.IsKing(from),
            position.PiecesOf(position.SideToMove.Opponent()),
            // The moving piece has left its square, so a king may land on it again mid-route.
            position.Occupied & ~Board.Bit(from),
            from,
            moves);
        Extend(context, from, captured: 0, path: new int[MaxJumps], depth: 0);
    }

    private static void Extend(JumpContext context, int current, uint captured, int[] path, int depth)
    {
        var extended = false;
        foreach (var direction in Board.DirectionsFor(context.Side, context.King))
        {
            var landing = Board.JumpTarget(current, direction);
            if (landing < 0)
            {
                continue;
            }

            var jumped = Board.Bit(Board.Neighbour(current, direction));
            if ((context.Opponent & jumped) == 0
                || (captured & jumped) != 0
                || (context.Occupied & Board.Bit(landing)) != 0)
            {
                continue;
            }

            extended = true;
            path[depth] = landing;
            var nowCaptured = captured | jumped;

            if (!context.King && (Board.KingRow(context.Side) & Board.Bit(landing)) != 0)
            {
                context.Moves.Add(new Move(context.From, path[..(depth + 1)], nowCaptured, crowns: true));
                continue;
            }

            Extend(context, landing, nowCaptured, path, depth + 1);
        }

        if (!extended && depth > 0)
        {
            context.Moves.Add(new Move(context.From, path[..depth], captured, crowns: false));
        }
    }

    private sealed record JumpContext(Side Side, bool King, uint Opponent, uint Occupied, int From, List<Move> Moves);
}
