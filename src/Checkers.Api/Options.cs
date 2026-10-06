using System.ComponentModel.DataAnnotations;

namespace Checkers.Api;

/// <summary>The <c>Engine</c> configuration section.</summary>
public sealed class EngineOptions
{
    public const string Section = "Engine";

    /// <summary><c>chinook</c>: KingsRow DLL with the Chinook databases. <c>builtin</c>: the fallback engine.</summary>
    [Required]
    [RegularExpression("^(?i)(chinook|builtin)$", ErrorMessage = "Engine:Type must be 'chinook' or 'builtin'.")]
    public string Type { get; set; } = "chinook";

    /// <summary>Path to the CheckerBoard-API engine DLL (Kingsrow64.dll). Required for <c>chinook</c>.</summary>
    public string? Path { get; set; }

    /// <summary>Directory holding the Chinook WLD database files (DB6, DB7.0, ... with their .idx).</summary>
    public string? Databases { get; set; }

    [Range(1, 32)]
    public int Workers { get; set; } = 2;

    /// <summary>Worker executable. Defaults to Checkers.EngineHost.exe next to the API.</summary>
    public string? HostPath { get; set; }

    [Range(8, 4096)]
    public int HashMb { get; set; } = 64;

    [Range(16, 65536)]
    public int DbCacheMb { get; set; } = 256;

    /// <summary>Positions with at most this many pieces go to the tablebase first (capped by what is installed).</summary>
    [Range(0, 10)]
    public int TablebaseMaxPieces { get; set; } = 8;

    /// <summary>Search budget for a tablebase probe; the acceptance target is a 50 ms response.</summary>
    [Range(1, 1000)]
    public int TablebaseTimeMs { get; set; } = 10;

    /// <summary>Budget for starting, initialising and warming up one worker.</summary>
    [Range(1000, 300_000)]
    public int StartupTimeoutMs { get; set; } = 60_000;

    /// <summary>How long a cancelled search may take to stop before its worker is restarted.</summary>
    [Range(10, 10_000)]
    public int StopGraceMs { get; set; } = 500;
}

/// <summary>The <c>Cache</c> configuration section.</summary>
public sealed class CacheOptions
{
    public const string Section = "Cache";

    [Range(0, 10_000_000)]
    public int Capacity { get; set; } = 20_000;

    [Range(0, 24 * 60)]
    public int TtlMinutes { get; set; } = 15;
}

/// <summary>The <c>Limits</c> configuration section: defaults when a request gives no level or limits.</summary>
public sealed class LimitsOptions
{
    public const string Section = "Limits";

    [Range(1, 60_000)]
    public int DefaultSoftTimeMs { get; set; } = 300;

    [Range(1, 60_000)]
    public int DefaultHardTimeMs { get; set; } = 1200;

    /// <summary>Upper bound accepted for a request's <c>hardTimeMs</c>.</summary>
    [Range(1, 600_000)]
    public int MaxHardTimeMs { get; set; } = 5_000;

    /// <summary>
    /// Upper bound for a request's <c>softTimeMs</c>. Levels stay within their own move times; this
    /// keeps a request without a level from holding a worker for long.
    /// </summary>
    [Range(1, 600_000)]
    public int MaxSoftTimeMs { get; set; } = 2_000;

    /// <summary>Depth used when neither a level nor <c>maxDepth</c> is given.</summary>
    [Range(1, 64)]
    public int DefaultMaxDepth { get; set; } = 64;
}

/// <summary>
/// The <c>RateLimit</c> section: how many engine requests one client (by IP address) may have in
/// flight. Beyond that a request is refused at once with 429, rather than queueing for workers that
/// a single client would otherwise be able to occupy.
/// </summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimit";

    [Range(1, 1000)]
    public int PermitLimitPerClient { get; set; } = 4;

    [Range(0, 1000)]
    public int QueueLimitPerClient { get; set; }
}

/// <summary>
/// The <c>LogFiles</c> section: JSON log files with daily and size-based rolling. Under IIS this
/// replaces the ASP.NET Core Module's stdout log, which never rolls and is meant for start-up problems.
/// </summary>
public sealed class LogFileOptions
{
    public const string Section = "LogFiles";

    /// <summary>Relative to the content root (the site folder under IIS); empty disables file logging.</summary>
    public string? Directory { get; set; } = "logs";

    public int RetainedFiles { get; set; } = 14;

    public int FileSizeLimitMb { get; set; } = 100;
}

/// <summary>One strength level: search until <see cref="MaxDepth"/> or <see cref="MoveTimeMs"/>, whichever comes first.</summary>
public sealed class LevelOptions
{
    [Range(0, 64)]
    public int MinDepth { get; set; }

    [Range(1, 64)]
    public int MaxDepth { get; set; }

    [Range(1, 60_000)]
    public int MoveTimeMs { get; set; }
}
