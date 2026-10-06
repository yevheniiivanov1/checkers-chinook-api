using Checkers.Core;
using Checkers.Core.Worker;

namespace Checkers.EngineHost;

/// <summary>An engine the worker can host. Every call arrives on the single engine thread.</summary>
internal interface IEngine : IDisposable
{
    WorkerEngineInfo Initialize(WorkerInit init);

    WorkerSearchResult Search(Position position, WorkerSearchLimits limits, SearchControl control);
}

/// <summary>
/// Stop signalling between the protocol thread, which receives <c>stop</c> requests, and the
/// engine thread. A stop names a search id, so a late stop can never cut short the next search.
/// </summary>
internal sealed class SearchControl
{
    private long _currentId;
    private long _stopId;

    public bool IsStopRequested
    {
        get
        {
            var current = Interlocked.Read(ref _currentId);
            return current != 0 && Interlocked.Read(ref _stopId) == current;
        }
    }

    public void Begin(long searchId) => Interlocked.Exchange(ref _currentId, searchId);

    public void End() => Interlocked.Exchange(ref _currentId, 0);

    /// <summary>
    /// Also accepted before the search begins: the API may cancel while the request is still
    /// queued behind another command.
    /// </summary>
    public void RequestStop(long searchId) => Interlocked.Exchange(ref _stopId, searchId);

    public void StopCurrent() => Interlocked.Exchange(ref _stopId, Interlocked.Read(ref _currentId));
}
