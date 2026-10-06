using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Checkers.Core;
using Checkers.Core.Worker;

namespace Checkers.Api.Engine;

public enum WorkerState
{
    Stopped,
    Starting,
    Ready,
    Faulted,
}

/// <summary>
/// One long-lived engine worker process and the client side of its stdin/stdout protocol.
/// </summary>
/// <remarks>
/// <para><see cref="Gate"/> is the per-worker async lock: the pool hands a worker to one request at a
/// time, and that request's <c>SetPositionAsync</c> + <c>SearchAsync</c> run back to back on it.</para>
/// <para>A search cancelled by the request's hard deadline is told to stop, but the worker stays
/// locked until it has actually answered, so a late reply can never be mistaken for the next
/// request's. If it does not answer within <see cref="EngineOptions.StopGraceMs"/> the process is
/// killed and the pool restarts it.</para>
/// </remarks>
internal sealed class EngineWorker : IEngineAdapter, IAsyncDisposable
{
    private static readonly SearchLimits WarmUpLimits = new(MinDepth: 0, MaxDepth: 64, SoftTimeMs: 50, HardTimeMs: 2_000, Tablebase: false);

    private readonly EngineOptions _options;
    private readonly string _hostPath;
    private readonly JobObject? _job;
    private readonly ILogger _logger;
    private long _nextRequestId;
    private Channel? _channel;
    private Task<WorkerResponse>? _unfinishedSearch;
    private int _state = (int)WorkerState.Stopped;
    private volatile bool _disposed;

    public EngineWorker(int id, EngineOptions options, string hostPath, JobObject? job, ILogger logger)
    {
        Id = id;
        _options = options;
        _hostPath = hostPath;
        _job = job;
        _logger = logger;
    }

    /// <summary>Raised when a ready worker's process dies; the pool restarts it.</summary>
    public event Action<EngineWorker>? Faulted;

    public int Id { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public WorkerState State => (WorkerState)Volatile.Read(ref _state);

    public bool IsReady => State == WorkerState.Ready;

    public WorkerEngineInfo? Info { get; private set; }

    internal int? ProcessId => _channel?.Process.Id;

    /// <summary>(Re)starts the process, initialises the engine and runs a warm-up search. Caller holds <see cref="Gate"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(WorkerState.Starting);
        try
        {
            _channel?.Kill();
            _channel = StartProcess();

            var init = new WorkerInit(_options.Type, _options.Path, _options.Databases, _options.HashMb, _options.DbCacheMb);
            var response = await RequestAsync(new WorkerRequest(NextId(), WorkerCommands.Init) { Init = init }, cancellationToken);
            Info = response.Engine;

            // JIT, hash table and KingsRow's endgame drivers are warm before the first real request.
            var stopwatch = Stopwatch.StartNew();
            await SetPositionAsync(Position.Initial.ToFen(), cancellationToken);
            var warmUp = await SearchAsync(WarmUpLimits, cancellationToken);
            _logger.LogInformation(
                "Engine worker {WorkerId} ready: {EngineName}, tablebase pieces {TablebasePieces}, warm-up depth {Depth} in {TimeMs} ms",
                Id, Info?.Name, Info?.TablebasePieces, warmUp.Depth, stopwatch.ElapsedMilliseconds);

            SetState(WorkerState.Ready);
        }
        catch
        {
            SetState(WorkerState.Faulted);
            _channel?.Kill();
            throw;
        }
    }

    public async Task SetPositionAsync(string pdn, CancellationToken cancellationToken) =>
        await RequestAsync(new WorkerRequest(NextId(), WorkerCommands.SetPosition) { Position = pdn }, cancellationToken);

    public async Task<EngineSearchResult> SearchAsync(SearchLimits limits, CancellationToken cancellationToken)
    {
        var request = new WorkerRequest(NextId(), WorkerCommands.Search)
        {
            Limits = new WorkerSearchLimits(limits.MinDepth, limits.MaxDepth, limits.SoftTimeMs, limits.HardTimeMs, limits.Tablebase),
        };

        var channel = CurrentChannel();
        var reply = await channel.SendAsync(request);
        WorkerResponse response;
        try
        {
            response = await reply.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _unfinishedSearch = reply;
            await channel.TryWriteAsync(new WorkerRequest(request.Id, WorkerCommands.Stop));
            throw;
        }

        var result = EnsureOk(response).Result ?? throw new EngineFailureException($"Engine worker {Id} returned no search result.");
        return new EngineSearchResult(
            result.BestMove,
            result.Pv,
            ScoreOrWdl: result.TablebaseHit && result.Wdl is { } wdl ? wdl : result.Score,
            result.Nodes,
            result.Depth,
            result.TablebaseHit,
            result.Score,
            result.Wdl,
            result.TimeMs,
            result.StopReason);
    }

    /// <summary>Gives the worker back to the pool, after any cancelled search has wound down.</summary>
    public void Release()
    {
        var unfinished = _unfinishedSearch;
        _unfinishedSearch = null;
        if (unfinished is null || unfinished.IsCompleted)
        {
            Gate.Release();
            return;
        }

        _ = ReleaseWhenStoppedAsync(unfinished);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        SetState(WorkerState.Stopped);
        if (_channel is { } channel)
        {
            await channel.CloseAsync(TimeSpan.FromSeconds(2));
        }
    }

    private async Task ReleaseWhenStoppedAsync(Task<WorkerResponse> unfinished)
    {
        try
        {
            await unfinished.WaitAsync(TimeSpan.FromMilliseconds(_options.StopGraceMs));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Engine worker {WorkerId} did not stop within {GraceMs} ms; restarting it", Id, _options.StopGraceMs);
            Fault(); // before the gate is released, so the next request cannot be routed to a dead worker
        }
        catch (Exception)
        {
            // The worker failed or exited while stopping; the exit path already handles that.
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken) =>
        EnsureOk(await (await CurrentChannel().SendAsync(request)).WaitAsync(cancellationToken));

    private Channel CurrentChannel() =>
        _channel ?? throw new EngineUnavailableException($"Engine worker {Id} is not running.");

    private WorkerResponse EnsureOk(WorkerResponse response) =>
        response.Ok ? response : throw new EngineFailureException($"Engine worker {Id}: {response.Error}");

    private long NextId() => Interlocked.Increment(ref _nextRequestId);

    private Channel StartProcess()
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        if (_hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            start.ArgumentList.Add(_hostPath);
        }
        else
        {
            start.FileName = _hostPath;
        }

        var process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _logger.LogInformation("Engine worker {WorkerId}: {WorkerOutput}", Id, e.Data);
            }
        };

        if (!process.Start())
        {
            throw new EngineUnavailableException($"Could not start engine worker '{_hostPath}'.");
        }

        if (_job is not null && !_job.TryAssign(process))
        {
            _logger.LogWarning("Engine worker {WorkerId} could not join the kill-on-close job object", Id);
        }

        process.StandardInput.NewLine = "\n";
        process.BeginErrorReadLine();
        var channel = new Channel(process);
        _ = Task.Run(() => ReadLoopAsync(channel));
        _logger.LogInformation("Engine worker {WorkerId} started as process {ProcessId}", Id, process.Id);
        return channel;
    }

    private async Task ReadLoopAsync(Channel channel)
    {
        try
        {
            while (await channel.Process.StandardOutput.ReadLineAsync() is { } line)
            {
                WorkerResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerResponse);
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Engine worker {WorkerId} wrote a non-protocol line: {Line}", Id, line);
                    continue;
                }

                if (response is not null && !channel.Complete(response))
                {
                    _logger.LogWarning("Engine worker {WorkerId} sent an unexpected reply #{RequestId}: {Error}", Id, response.Id, response.Error);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        // Only a ready worker that dies by itself needs the pool to step in. A failed start is
        // retried by whoever started it, a replaced or disposed channel is expected to end, and a
        // worker killed by Fault() has been reported already. Marked faulted before the waiting
        // request learns of the failure, because that request may retry on another worker at once.
        var unexpected = !_disposed && ReferenceEquals(channel, _channel) && TryChangeState(WorkerState.Ready, WorkerState.Faulted);
        channel.FailPending(new EngineUnavailableException($"Engine worker {Id} exited."));
        if (!unexpected)
        {
            return;
        }

        await channel.WaitForExitAsync(TimeSpan.FromSeconds(2));
        _logger.LogError("Engine worker {WorkerId} (process {ProcessId}) exited unexpectedly with code {ExitCode}", Id, channel.Process.Id, channel.ExitCode);
        Faulted?.Invoke(this);
    }

    /// <summary>Takes a ready worker out of rotation, kills its process and has the pool restart it.</summary>
    private void Fault()
    {
        if (!TryChangeState(WorkerState.Ready, WorkerState.Faulted))
        {
            return; // already faulted, starting or stopping
        }

        _channel?.Kill();
        Faulted?.Invoke(this);
    }

    private void SetState(WorkerState state) => Volatile.Write(ref _state, (int)state);

    private bool TryChangeState(WorkerState from, WorkerState to) =>
        Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;

    /// <summary>One process and the requests waiting for its replies; replaced on every restart.</summary>
    private sealed class Channel(Process process)
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<WorkerResponse>> _pending = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Process Process { get; } = process;

        public int? ExitCode => Process.HasExited ? Process.ExitCode : null;

        /// <summary>Also drains stderr, so the worker's last log lines come before its exit is reported.</summary>
        public async Task WaitForExitAsync(TimeSpan timeout)
        {
            try
            {
                await Process.WaitForExitAsync().WaitAsync(timeout);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
            }
        }

        public async Task<Task<WorkerResponse>> SendAsync(WorkerRequest request)
        {
            var reply = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[request.Id] = reply;
            if (Process.HasExited || !await TryWriteAsync(request))
            {
                _pending.TryRemove(request.Id, out _);
                throw new EngineUnavailableException("Engine worker is not running.");
            }

            return reply.Task;
        }

        public async Task<bool> TryWriteAsync(WorkerRequest request)
        {
            var line = JsonSerializer.Serialize(request, WorkerJsonContext.Default.WorkerRequest);
            // Never cancelled mid-write: half a line would corrupt the channel for every later request.
            await _writeLock.WaitAsync();
            try
            {
                await Process.StandardInput.WriteLineAsync(line);
                await Process.StandardInput.FlushAsync();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public bool Complete(WorkerResponse response)
        {
            if (!_pending.TryRemove(response.Id, out var reply))
            {
                return false;
            }

            reply.TrySetResult(response);
            return true;
        }

        public void FailPending(Exception error)
        {
            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var reply))
                {
                    reply.TrySetException(error);
                }
            }
        }

        /// <summary>Closing stdin asks the worker to exit; it is killed if it has not done so in time.</summary>
        public async Task CloseAsync(TimeSpan grace)
        {
            try
            {
                await _writeLock.WaitAsync(grace);
                Process.StandardInput.Close();
                await Process.WaitForExitAsync().WaitAsync(grace);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            Kill();
        }

        public void Kill()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}
