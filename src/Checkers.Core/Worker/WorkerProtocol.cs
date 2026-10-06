using System.Text.Json.Serialization;

namespace Checkers.Core.Worker;

/// <summary>
/// Line-delimited JSON protocol between the API and an engine worker process: one request per
/// line on the worker's stdin, one response per line on its stdout. Requests are answered in
/// order; <see cref="WorkerCommands.Stop"/> is the exception — it is handled immediately, is never
/// answered, and only interrupts the search whose id it names.
/// </summary>
public static class WorkerCommands
{
    public const string Init = "init";
    public const string SetPosition = "position";
    public const string Search = "search";
    public const string Stop = "stop";
    public const string Ping = "ping";
}

public sealed record WorkerRequest(long Id, string Type)
{
    public WorkerInit? Init { get; init; }

    /// <summary>PDN FEN for <see cref="WorkerCommands.SetPosition"/>.</summary>
    public string? Position { get; init; }

    public WorkerSearchLimits? Limits { get; init; }
}

/// <param name="Engine"><c>chinook</c> (KingsRow DLL probing the Chinook databases) or <c>builtin</c>.</param>
/// <param name="EnginePath">Path to the CheckerBoard-API engine DLL, e.g. Kingsrow64.dll.</param>
/// <param name="DatabasePath">Directory with the Chinook WLD endgame database files.</param>
public sealed record WorkerInit(string Engine, string? EnginePath, string? DatabasePath, int HashMb, int DbCacheMb);

/// <summary>
/// The worker stops at the first of: an iteration deeper than <paramref name="MaxDepth"/> starting,
/// <paramref name="SoftTimeMs"/> elapsing once <paramref name="MinDepth"/> is complete, or
/// <paramref name="HardTimeMs"/>. With <paramref name="Tablebase"/> the result may be reported
/// as a tablebase hit.
/// </summary>
public sealed record WorkerSearchLimits(int MinDepth, int MaxDepth, int SoftTimeMs, int HardTimeMs, bool Tablebase);

public sealed record WorkerResponse(long Id, bool Ok)
{
    public string? Error { get; init; }

    public WorkerEngineInfo? Engine { get; init; }

    public WorkerSearchResult? Result { get; init; }
}

/// <param name="TablebasePieces">Largest piece count covered by the loaded endgame database; 0 when none.</param>
public sealed record WorkerEngineInfo(string Name, int TablebasePieces, string? Details);

/// <param name="BestMove">Canonical notation of the move the engine played; empty if its board matched no legal move.</param>
/// <param name="Pv">Principal variation replayed through the rules and cut at the first move that does not fit.</param>
/// <param name="Score">Engine score from the side to move's point of view; a man is worth 100.</param>
/// <param name="Wdl">1 win, 0 draw, -1 loss for the side to move when the engine knows the game value; otherwise null.</param>
/// <param name="Depth">Nominal depth of the last completed iteration.</param>
/// <param name="StopReason">Why the worker interrupted the search (<see cref="WorkerStopReasons"/>), or null when the engine finished by itself.</param>
public sealed record WorkerSearchResult(
    string BestMove,
    IReadOnlyList<string> Pv,
    int Score,
    int? Wdl,
    int Depth,
    long Nodes,
    bool TablebaseHit,
    int TimeMs,
    string? StopReason);

public static class WorkerStopReasons
{
    /// <summary>An iteration deeper than MaxDepth started.</summary>
    public const string Depth = "depth";

    /// <summary>SoftTimeMs passed with MinDepth complete.</summary>
    public const string SoftTime = "soft-time";

    /// <summary>HardTimeMs passed: the answer may be shallower than the limits asked for.</summary>
    public const string HardTime = "hard-time";

    /// <summary>The API sent a stop.</summary>
    public const string Request = "request";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerResponse))]
public sealed partial class WorkerJsonContext : JsonSerializerContext;
