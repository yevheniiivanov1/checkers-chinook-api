using System.Text.RegularExpressions;

namespace Checkers.Core;

/// <summary>
/// Matches PDN move text against the legal moves of a position.
/// </summary>
/// <remarks>
/// A step is written <c>22-18</c>; a capture with <c>x</c> (or <c>:</c>), either as the full path of a
/// multi-jump (<c>22x15x6</c>) or as just its start and end (<c>22x6</c>). The separator must match the
/// move: <c>6-24</c> does not name the capture <c>6x15x24</c>, and mixed text such as <c>22-18x11</c> is
/// not a move. Only legal moves can match, so a step written while a capture is compulsory never
/// matches. A short capture form can fit several routes; <see cref="FindAmong"/> then returns all of them.
/// </remarks>
public static partial class MoveNotation
{
    /// <summary>
    /// Legal moves the text can mean. Empty when the text is malformed or names no legal move;
    /// more than one when a short capture form fits several routes.
    /// </summary>
    public static IReadOnlyList<Move> FindLegal(Position position, string? text) =>
        FindAmong(MoveGenerator.LegalMoves(position), text);

    public static IReadOnlyList<Move> FindAmong(IReadOnlyList<Move> legalMoves, string? text) =>
        TryParse(text, out var squares, out var capture)
            ? legalMoves.Where(move => move.IsCapture == capture && Matches(move, squares)).ToList()
            : [];

    /// <summary>
    /// Legal moves whose squares match the text when the separator is ignored, e.g. the capture
    /// <c>6x15x24</c> for <c>6-24</c>. Only used to explain why text did not match.
    /// </summary>
    public static IReadOnlyList<Move> FindIgnoringSeparator(IReadOnlyList<Move> legalMoves, string? text) =>
        TryParse(text, out var squares, out _)
            ? legalMoves.Where(move => Matches(move, squares)).ToList()
            : [];

    /// <summary>Parses move text into 0-based squares, e.g. "22x15x6" → [21, 14, 5].</summary>
    public static bool TryParseSquares(string? text, out IReadOnlyList<int> squares) =>
        TryParse(text, out squares, out _);

    private static bool TryParse(string? text, out IReadOnlyList<int> squares, out bool capture)
    {
        squares = [];
        capture = false;
        var trimmed = (text ?? string.Empty).Trim();
        var step = StepText().IsMatch(trimmed);
        if (!step && !CaptureText().IsMatch(trimmed))
        {
            return false;
        }

        var parsed = Separators().Split(trimmed).Select(int.Parse).ToList();
        if (parsed.Any(n => n is < 1 or > Board.SquareCount))
        {
            return false;
        }

        squares = parsed.Select(n => n - 1).ToList();
        capture = !step;
        return true;
    }

    private static bool Matches(Move move, IReadOnlyList<int> squares)
    {
        if (squares[0] != move.From || squares[^1] != move.To)
        {
            return false;
        }

        // "from-to" only: any route between the two squares fits.
        return squares.Count == 2 || squares.Skip(1).SequenceEqual(move.Landings);
    }

    // [0-9], not \d: \d matches any Unicode digit (e.g. Arabic-Indic), which int.Parse rejects.
    [GeneratedRegex("^[0-9]{1,2}-[0-9]{1,2}$")]
    private static partial Regex StepText();

    [GeneratedRegex("^[0-9]{1,2}([xX:][0-9]{1,2}){1,12}$")]
    private static partial Regex CaptureText();

    [GeneratedRegex("[-xX:]")]
    private static partial Regex Separators();
}
