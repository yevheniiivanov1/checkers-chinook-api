using System.Text.RegularExpressions;

namespace Checkers.EngineHost.KingsRow;

/// <summary>
/// One snapshot of KingsRow's search status line, e.g.
/// <c>value=115,  depth 14/15.1/25,  0.0s,  7149 kN/s,  pv 14x23 27x18 16x23</c>.
/// </summary>
/// <remarks>
/// Format per KingsrowHelp.htm: the value is from Black's point of view (a man is 100, beyond
/// ±2000 is a seen win or loss, ±1 an endgame-database draw); the depth triple is nominal / average
/// leaf / maximum. <c>value&gt;</c> or <c>value&lt;</c> marks a bound inside an iteration still running;
/// <c>value=</c> with a pv marks a completed iteration. When the position itself is a database draw
/// the line lists candidate moves instead, e.g. <c>depth 6; 14-18* (0.222), 14-9* (0.222)</c>.
/// The string is read while the engine may be rewriting it, so every field is matched on its own
/// and a torn read just parses less. Digits are matched as [0-9]: <c>\d</c> would accept any
/// Unicode digit, which <c>int.Parse</c> then rejects.
/// </remarks>
internal sealed partial record KingsRowStatus(
    char? Bound,
    int? Value,
    int? Depth,
    int? KiloNodesPerSecond,
    IReadOnlyList<string> Pv,
    bool IsDatabaseDraw)
{
    /// <summary>KingsRow's scores beyond this magnitude mean a win or loss it can see.</summary>
    public const int DecisiveValue = 2000;

    public static KingsRowStatus Empty { get; } = new(null, null, null, null, [], false);

    public bool IsCompletedIteration => Bound == '=' && Depth is not null && Pv.Count > 0;

    public static KingsRowStatus Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return Empty;
        }

        var value = ValuePattern().Match(line);
        var depth = DepthPattern().Match(line);
        var speed = SpeedPattern().Match(line);
        var pv = PvPattern().Match(line);

        return new KingsRowStatus(
            value.Success ? value.Groups["bound"].Value[0] : null,
            value.Success ? int.Parse(value.Groups["value"].Value) : null,
            depth.Success ? int.Parse(depth.Groups["depth"].Value) : null,
            speed.Success ? int.Parse(speed.Groups["knps"].Value) : null,
            pv.Success
                ? pv.Groups["pv"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(t => MovePattern().IsMatch(t)).ToList()
                : [],
            DatabaseDrawPattern().IsMatch(line));
    }

    [GeneratedRegex(@"value(?<bound>[=<>])(?<value>-?[0-9]{1,6})")]
    private static partial Regex ValuePattern();

    [GeneratedRegex(@"depth (?<depth>[0-9]{1,4})[/;]")]
    private static partial Regex DepthPattern();

    [GeneratedRegex(@"(?<knps>[0-9]{1,9}) kN/s")]
    private static partial Regex SpeedPattern();

    [GeneratedRegex(@"\bpv (?<pv>.*)$")]
    private static partial Regex PvPattern();

    [GeneratedRegex(@"^[0-9]{1,2}([-x][0-9]{1,2})+$")]
    private static partial Regex MovePattern();

    [GeneratedRegex(@"^depth [0-9]+;\s*[0-9]{1,2}[-x][0-9]{1,2}\*?\s*\(")]
    private static partial Regex DatabaseDrawPattern();
}
