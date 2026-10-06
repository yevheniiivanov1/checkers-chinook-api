using Microsoft.Extensions.Options;

namespace Checkers.Api.Engine;

/// <summary>
/// A fixed set of long-lived engine worker processes, created and warmed up when the app starts
/// (no process is ever spawned per request). Requests are routed round robin, skipping workers
/// that are busy or restarting; each worker serves one request at a time behind its async lock.
/// A worker whose process dies is restarted in the background with exponential back-off.
/// </summary>
internal sealed class EngineWorkerPool : IEnginePool, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly EngineOptions _options;
    private readonly ILogger<EngineWorkerPool> _logger;
    private readonly JobObject? _job = JobObject.TryCreate();
    private readonly CancellationTokenSource _stopping = new();
    private readonly EngineWorker[] _workers;
    private readonly int[] _restarting;
    private readonly Lock _stopLock = new();
    private Task? _stopTask;
    private int _next = -1;

    public EngineWorkerPool(IOptions<EngineOptions> options, ILogger<EngineWorkerPool> logger, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = logger;
        var hostPath = ResolveHostPath(_options.HostPath);
        var workerLogger = loggerFactory.CreateLogger<EngineWorker>();
        _workers = Enumerable.Range(1, _options.Workers)
            .Select(id => new EngineWorker(id, _options, hostPath, _job, workerLogger))
            .ToArray();
        _restarting = new int[_workers.Length];
        foreach (var worker in _workers)
        {
            worker.Faulted += OnWorkerFaulted;
        }
    }

    internal IReadOnlyList<EngineWorker> Workers => _workers;

    public EnginePoolStatus Status
    {
        get
        {
            var ready = _workers.Where(w => w.IsReady).ToList();
            var info = ready.FirstOrDefault()?.Info;
            var tablebasePieces = ready.Count == 0 ? 0 : Math.Min(_options.TablebaseMaxPieces, ready.Min(w => w.Info?.TablebasePieces ?? 0));
            return new EnginePoolStatus(ready.Count, _workers.Length, _options.Type.ToLowerInvariant(), info?.Name, tablebasePieces);
        }
    }

    /// <summary>
    /// Starts and warms every worker before the server begins accepting requests. A worker that
    /// fails to start does not stop the app: it keeps retrying in the background and /healthz
    /// reports the degraded state.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting {Workers} {EngineType} engine workers", _workers.Length, _options.Type);
        await Task.WhenAll(_workers.Select(async worker =>
        {
            if (!await TryStartAsync(worker, cancellationToken))
            {
                ScheduleRestart(worker);
            }
        }));
    }

    /// <summary>
    /// Closes every worker's stdin and waits for it to exit. Idempotent, and concurrent callers
    /// share one stop: the host stops hosted services while the DI container may already be
    /// disposing the pool, and closing the job object before the workers have exited would kill them.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopLock)
        {
            return _stopTask ??= StopWorkersAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _job?.Dispose(); // kill-on-close: anything that did not exit gracefully goes now
    }

    private async Task StopWorkersAsync()
    {
        await _stopping.CancelAsync();
        await Task.WhenAll(_workers.Select(w => w.DisposeAsync().AsTask()));
    }

    public async ValueTask<IEngineLease> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var start = (int)((uint)Interlocked.Increment(ref _next) % (uint)_workers.Length);

            // Round robin, but take the first idle ready worker rather than queueing behind a busy one.
            for (var i = 0; i < _workers.Length; i++)
            {
                var worker = _workers[(start + i) % _workers.Length];
                if (worker.IsReady && worker.Gate.Wait(0))
                {
                    if (worker.IsReady)
                    {
                        return new Lease(worker);
                    }

                    worker.Release();
                }
            }

            // Everyone is busy: queue on the round-robin choice (or the next ready one after it).
            var target = Enumerable.Range(0, _workers.Length)
                .Select(i => _workers[(start + i) % _workers.Length])
                .FirstOrDefault(w => w.IsReady)
                ?? throw new EngineUnavailableException("No engine worker is running; see /healthz.");

            await target.Gate.WaitAsync(cancellationToken);
            if (target.IsReady)
            {
                return new Lease(target);
            }

            target.Release(); // it failed while we waited; pick again
        }
    }

    private async Task<bool> TryStartAsync(EngineWorker worker, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        timeout.CancelAfter(_options.StartupTimeoutMs);
        var locked = false;
        try
        {
            // The worker may still be held by a request when its restart begins; that wait counts
            // against the start-up budget too, and timing out is a failed attempt, not the end of retries.
            await worker.Gate.WaitAsync(timeout.Token);
            locked = true;
            await worker.StartAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (!_stopping.IsCancellationRequested)
        {
            _logger.LogError(ex, "Engine worker {WorkerId} failed to start", worker.Id);
            return false;
        }
        finally
        {
            if (locked)
            {
                worker.Gate.Release();
            }
        }
    }

    private void OnWorkerFaulted(EngineWorker worker) => ScheduleRestart(worker);

    private void ScheduleRestart(EngineWorker worker)
    {
        if (_stopping.IsCancellationRequested || Interlocked.Exchange(ref _restarting[worker.Id - 1], 1) == 1)
        {
            return;
        }

        _ = Task.Run(() => RestartLoopAsync(worker));
    }

    private async Task RestartLoopAsync(EngineWorker worker)
    {
        try
        {
            var delay = FirstRetryDelay;
            while (!_stopping.IsCancellationRequested)
            {
                await Task.Delay(delay, _stopping.Token);
                if (await TryStartAsync(worker, CancellationToken.None))
                {
                    _logger.LogInformation("Engine worker {WorkerId} restarted", worker.Id);
                    return;
                }

                delay = delay * 2 > MaxRetryDelay ? MaxRetryDelay : delay * 2;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Volatile.Write(ref _restarting[worker.Id - 1], 0);

            // A fault reported while this loop still held the flag was ignored by ScheduleRestart.
            if (worker.State == WorkerState.Faulted)
            {
                ScheduleRestart(worker);
            }
        }
    }

    private static string ResolveHostPath(string? configured)
    {
        var defaultName = OperatingSystem.IsWindows() ? "Checkers.EngineHost.exe" : "Checkers.EngineHost.dll";
        var path = string.IsNullOrWhiteSpace(configured) ? defaultName : configured;
        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    private sealed class Lease(EngineWorker worker) : IEngineLease
    {
        private int _released;

        public IEngineAdapter Engine => worker;

        public int WorkerId => worker.Id;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                worker.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
