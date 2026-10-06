using System.Numerics;
using System.Text.RegularExpressions;

namespace Checkers.Core;

/// <summary>Thrown for a PDN position that cannot be parsed or describes an impossible board.</summary>
public sealed class PdnFormatException(IReadOnlyList<string> errors)
    : FormatException("Invalid PDN position: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Parser for PDN FEN positions such as <c>B:W18,19,K22:B1,5</c>.
/// </summary>
/// <remarks>
/// Accepted beyond the minimal form: a <c>[FEN "..."]</c> tag wrapper, surrounding quotes, a
/// trailing period, lower case, whitespace, the two colour sections in either order, and square
/// ranges (<c>W21-32</c>). The result is validated: squares 1–32, no square used twice, at most
/// 12 pieces per side, no uncrowned man on its own crowning rank, and at least one piece.
/// </remarks>
public static partial class Pdn
{
    public static Position Parse(string? text) =>
        TryParse(text, out var position, out var errors) ? position : throw new PdnFormatException(errors);

    public static bool TryParse(string? text, out Position position, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        errors = problems;
        position = default;

        var fen = Unwrap(text);
        if (fen.Length == 0)
        {
            problems.Add("Position is empty.");
            return false;
        }

        var parts = fen.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            problems.Add($"Expected '<turn>:W<squares>:B<squares>', got {parts.Length} ':'-separated part(s).");
            return false;
        }

        Side? turn = parts[0].ToUpperInvariant() switch
        {
            "B" => Side.Black,
            "W" => Side.White,
            _ => null,
        };
        if (turn is null)
        {
            problems.Add($"Side to move must be 'B' or 'W', got '{parts[0]}'.");
        }

        uint black = 0, white = 0, kings = 0;
        var seenSections = new HashSet<char>();
        foreach (var section in parts.Skip(1))
        {
            if (section.Length == 0 || char.ToUpperInvariant(section[0]) is not ('W' or 'B'))
            {
                problems.Add($"Each piece list must start with 'W' or 'B', got '{section}'.");
                continue;
            }

            var colour = char.ToUpperInvariant(section[0]);
            if (!seenSections.Add(colour))
            {
                problems.Add($"Piece list for '{colour}' appears twice.");
                continue;
            }

            ref var pieces = ref colour == 'W' ? ref white : ref black;
            ParsePieces(section[1..], colour, ref pieces, ref kings, black | white, problems);
        }

        if (problems.Count > 0)
        {
            return false;
        }

        Validate(black, white, kings, problems);
        if (problems.Count > 0)
        {
            return false;
        }

        position = new Position(black, white, kings, turn!.Value);
        return true;
    }

    private static string Unwrap(string? text)
    {
        var fen = (text ?? string.Empty).Trim();
        var tag = FenTag().Match(fen);
        if (tag.Success)
        {
            fen = tag.Groups["fen"].Value;
        }

        fen = fen.Trim().Trim('"').TrimEnd('.');
        return Whitespace().Replace(fen, string.Empty);
    }

    private static void ParsePieces(string list, char colour, ref uint pieces, ref uint kings, uint alreadyPlaced, List<string> problems)
    {
        if (list.Length == 0)
        {
            return; // A side with no pieces left is a legal (finished) position.
        }

        foreach (var token in list.Split(','))
        {
            var match = PieceToken().Match(token);
            if (!match.Success)
            {
                problems.Add($"Bad square '{token}' in the {colour} list.");
                continue;
            }

            var king = match.Groups["king"].Success;
            var first = int.Parse(match.Groups["from"].Value);
            var last = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value) : first;
            if (last < first)
            {
                problems.Add($"Bad range '{token}' in the {colour} list.");
                continue;
            }

            for (var number = first; number <= last; number++)
            {
                if (number is < 1 or > Board.SquareCount)
                {
                    problems.Add($"Square {number} is out of range 1-32.");
                    break;
                }

                var bit = Board.Bit(number - 1);
                if (((pieces | alreadyPlaced) & bit) != 0)
                {
                    problems.Add($"Square {number} is occupied twice.");
                    continue;
                }

                pieces |= bit;
                if (king)
                {
                    kings |= bit;
                }
            }
        }
    }

    private static void Validate(uint black, uint white, uint kings, List<string> problems)
    {
        if ((black | white) == 0)
        {
            problems.Add("The board has no pieces.");
        }

        if (BitOperations.PopCount(black) > Position.MaxPiecesPerSide)
        {
            problems.Add($"Black has {BitOperations.PopCount(black)} pieces; the maximum is {Position.MaxPiecesPerSide}.");
        }

        if (BitOperations.PopCount(white) > Position.MaxPiecesPerSide)
        {
            problems.Add($"White has {BitOperations.PopCount(white)} pieces; the maximum is {Position.MaxPiecesPerSide}.");
        }

        if ((black & ~kings & Board.BlackKingRow) != 0)
        {
            problems.Add("A black man stands on squares 29-32; it should have been crowned.");
        }

        if ((white & ~kings & Board.WhiteKingRow) != 0)
        {
            problems.Add("A white man stands on squares 1-4; it should have been crowned.");
        }
    }

    [GeneratedRegex("""^\[\s*FEN\s+"(?<fen>[^"]*)"\s*\]$""", RegexOptions.IgnoreCase)]
    private static partial Regex FenTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // [0-9], not \d: \d matches any Unicode digit (e.g. Arabic-Indic), which int.Parse rejects.
    [GeneratedRegex(@"^(?<king>[Kk])?(?<from>[0-9]{1,2})(-(?<to>[0-9]{1,2}))?$")]
    private static partial Regex PieceToken();
}
