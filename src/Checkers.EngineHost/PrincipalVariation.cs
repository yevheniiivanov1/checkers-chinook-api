using Checkers.Core;

namespace Checkers.EngineHost;

internal static class PrincipalVariation
{
    /// <summary>
    /// Replays engine pv text through the rules, rewriting each move in canonical notation and
    /// stopping at the first token that is not a legal move. KingsRow's own help warns its pv is
    /// "not always accurate", so it is never passed on unchecked. A pv that does not start with
    /// the move actually played (the search was interrupted after a new best move was found)
    /// is replaced by that move alone.
    /// </summary>
    public static IReadOnlyList<string> Normalize(Position root, IEnumerable<string> tokens, Move? played)
    {
        var line = new List<string>();
        var position = root;
        foreach (var token in tokens)
        {
            var matches = MoveNotation.FindLegal(position, token);
            if (matches.Count == 0)
            {
                break;
            }

            // "14x23" can name several capture routes; at the root prefer the one actually played.
            var move = line.Count == 0 && played is not null && matches.Any(m => m.Notation == played.Notation)
                ? played
                : matches[0];
            line.Add(move.Notation);
            position = MoveGenerator.Apply(position, move);
        }

        if (played is null)
        {
            return line;
        }

        return line.Count > 0 && line[0] == played.Notation ? line : [played.Notation];
    }
}
