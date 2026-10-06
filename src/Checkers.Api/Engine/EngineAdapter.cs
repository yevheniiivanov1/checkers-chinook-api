namespace Checkers.Api.Engine;

/// <summary>
/// The engine as the API sees it: set a position, then search it. Implemented by
/// <see cref="EngineWorker"/>, which forwards both calls to a long-lived worker process.
/// </summary>
public interface IEngineAdapter
{
    Task SetPositionAsync(string pdn, CancellationToken cancellationToken);

    Task<EngineSearchResult> SearchAsync(SearchLimits limits, CancellationToken cancellationToken);
}

/// <param name="SoftTimeMs">Enforced inside the worker: the search returns its best move so far.</param>
/// <param name="HardTimeMs">Worker-side backstop, set a little below the request's hard limit.</param>
/// <param name="Tablebase">Probe mode: a definite database result is reported as a tablebase hit.</param>
public sealed record SearchLimits(int MinDepth, int MaxDepth, int SoftTimeMs, int HardTimeMs, bool Tablebase);

/// <param name="ScoreOrWdl">The WDL value (1/0/-1) on a tablebase hit, otherwise the search score.</param>
/// <param name="Score">Engine score for the side to move; a man is 100.</param>
/// <param name="Wdl">Game value for the side to move when the engine knows it.</param>
/// <param name="StopReason">Which limit ended the search (<c>Checkers.Core.Worker.WorkerStopReasons</c>); null if the engine finished by itself.</param>
public sealed record EngineSearchResult(
    string BestMove,
    IReadOnlyList<string> Pv,
    int ScoreOrWdl,
    long Nodes,
    int Depth,
    bool TablebaseHit,
    int Score,
    int? Wdl,
    int EngineTimeMs,
    string? StopReason = null);

/// <summary>A worker borrowed from the pool. Disposing it hands the worker back.</summary>
public interface IEngineLease : IAsyncDisposable
{
    IEngineAdapter Engine { get; }

    int WorkerId { get; }
}

public interface IEnginePool
{
    EnginePoolStatus Status { get; }

    /// <summary>Waits for a worker, chosen round robin; throws <see cref="EngineUnavailableException"/> when none is running.</summary>
    ValueTask<IEngineLease> AcquireAsync(CancellationToken cancellationToken);
}

/// <param name="TablebasePieces">Largest piece count the running workers can probe; 0 without a database.</param>
public sealed record EnginePoolStatus(int ReadyWorkers, int ConfiguredWorkers, string EngineType, string? EngineName, int TablebasePieces);

/// <summary>No worker can take the request (all failed or still starting). Maps to 503.</summary>
public sealed class EngineUnavailableException(string message) : Exception(message);

/// <summary>The engine answered with an error or with no legal move. Maps to 500.</summary>
public sealed class EngineFailureException(string message) : Exception(message);
