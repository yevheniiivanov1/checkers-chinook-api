using Checkers.Api.Contracts;
using Microsoft.Extensions.Options;

namespace Checkers.Api.Moves;

/// <param name="Level">Level name, or <c>custom</c> when the request gave none.</param>
public sealed record ResolvedLimits(string Level, int MinDepth, int MaxDepth, int SoftTimeMs, int HardTimeMs);

/// <summary>
/// Turns a request's level and limits into the search limits actually used.
/// </summary>
/// <remarks>
/// A level is a band: search to <c>MaxDepth</c> or for <c>MoveTimeMs</c>, whichever comes first,
/// and never stop before <c>MinDepth</c>. Request limits can only narrow it — <c>maxDepth</c> is
/// clamped into the band and <c>softTimeMs</c> cannot exceed the level's move time — so a "weak"
/// request cannot be made strong by its limits. Without a level the request's limits apply as given,
/// falling back to the <c>Limits</c> section. <c>hardTimeMs</c> is always the request's (or the default).
/// </remarks>
public sealed class LevelPolicy
{
    public const string CustomLevel = "custom";
    private const int MaxSearchDepth = 64;

    private static readonly Dictionary<string, LevelOptions> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weak"] = new() { MinDepth = 6, MaxDepth = 8, MoveTimeMs = 100 },
        ["medium"] = new() { MinDepth = 10, MaxDepth = 12, MoveTimeMs = 250 },
        ["strong"] = new() { MinDepth = 14, MaxDepth = 18, MoveTimeMs = 500 },
    };

    private readonly Dictionary<string, LevelOptions> _levels;
    private readonly LimitsOptions _limits;

    public LevelPolicy(IOptions<Dictionary<string, LevelOptions>> levels, IOptions<LimitsOptions> limits)
    {
        _levels = new Dictionary<string, LevelOptions>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, level) in levels.Value)
        {
            _levels[name] = level;
        }

        _limits = limits.Value;
    }

    public IReadOnlyCollection<string> Levels => _levels.Keys;

    public ResolvedLimits Resolve(string? level, SearchLimitsRequest? requested)
    {
        var errors = new Dictionary<string, string[]>();
        var hard = requested?.HardTimeMs ?? _limits.DefaultHardTimeMs;

        if (hard < 1 || hard > _limits.MaxHardTimeMs)
        {
            errors["limits.hardTimeMs"] = [$"hardTimeMs must be between 1 and {_limits.MaxHardTimeMs}."];
        }

        if (requested?.SoftTimeMs is < 1)
        {
            errors["limits.softTimeMs"] = ["softTimeMs must be positive."];
        }
        else if (requested?.SoftTimeMs > _limits.MaxSoftTimeMs)
        {
            errors["limits.softTimeMs"] = [$"softTimeMs must not exceed {_limits.MaxSoftTimeMs}."];
        }
        else if (requested?.SoftTimeMs > hard)
        {
            errors["limits.softTimeMs"] = ["softTimeMs must not exceed hardTimeMs."];
        }

        if (requested?.MaxDepth is < 1 or > MaxSearchDepth)
        {
            errors["limits.maxDepth"] = [$"maxDepth must be between 1 and {MaxSearchDepth}."];
        }

        LevelOptions? band = null;
        if (level is not null && !_levels.TryGetValue(level, out band))
        {
            errors["level"] = [$"level must be one of: {string.Join(", ", _levels.Keys)}."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        if (band is null)
        {
            var soft = Math.Min(requested?.SoftTimeMs ?? _limits.DefaultSoftTimeMs, hard);
            return new ResolvedLimits(CustomLevel, 0, requested?.MaxDepth ?? _limits.DefaultMaxDepth, soft, hard);
        }

        var maxDepth = Math.Clamp(requested?.MaxDepth ?? band.MaxDepth, band.MinDepth, band.MaxDepth);
        var softTime = Math.Min(Math.Min(requested?.SoftTimeMs ?? band.MoveTimeMs, band.MoveTimeMs), hard);
        return new ResolvedLimits(level!.ToLowerInvariant(), band.MinDepth, maxDepth, softTime, hard);
    }
}
