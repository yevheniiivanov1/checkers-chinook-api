using System.Diagnostics;
using Checkers.Api.Engine;
using Checkers.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Checkers.Api.Tests;

/// <summary>The process pool itself: routing, cancellation and crash recovery with real worker processes.</summary>
public sealed class WorkerPoolTests(BuiltinEngineApp app) : IClassFixture<BuiltinEngineApp>
{
    private readonly EngineWorkerPool _pool = (EngineWorkerPool)app.Services.GetRequiredService<IEnginePool>();

    [Fact]
    public async Task Concurrent_requests_get_different_workers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = await _pool.AcquireAsync(ct);
        await using var second = await _pool.AcquireAsync(ct);

        Assert.NotEqual(first.WorkerId, second.WorkerId);
    }

    [Fact]
    public async Task A_third_request_waits_for_a_free_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _pool.AcquireAsync(ct);
        var second = await _pool.AcquireAsync(ct);

        var third = _pool.AcquireAsync(ct).AsTask();
        await Task.Delay(100, ct);
        Assert.False(third.IsCompleted); // both workers busy: no third process is spawned, the request queues

        await first.DisposeAsync();
        await second.DisposeAsync();
        await using var lease = await third.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Contains(lease.WorkerId, new[] { first.WorkerId, second.WorkerId });
    }

    [Fact]
    public async Task A_cancelled_search_stops_and_the_worker_stays_usable()
    {
        var ct = TestContext.Current.CancellationToken;
        int workerId;
        int? processId;
        var clock = Stopwatch.StartNew();

        await using (var lease = await _pool.AcquireAsync(ct))
        {
            workerId = lease.WorkerId;
            processId = _pool.Workers[workerId - 1].ProcessId;
            await lease.Engine.SetPositionAsync(Position.Initial.ToFen(), ct);
            using var deadline = new CancellationTokenSource(150);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                lease.Engine.SearchAsync(new SearchLimits(0, 64, 60_000, 60_000, false), deadline.Token));
        }

        Assert.True(clock.ElapsedMilliseconds < 2000, $"cancellation took {clock.ElapsedMilliseconds} ms");

        // The stop was honoured, so the same process serves the next search.
        await WaitUntil(() => _pool.Workers[workerId - 1].Gate.CurrentCount == 1, ct);
        Assert.Equal(processId, _pool.Workers[workerId - 1].ProcessId);
        Assert.True(_pool.Workers[workerId - 1].IsReady);
    }

    [Fact]
    public async Task A_killed_worker_is_restarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var worker = _pool.Workers[0];
        await WaitUntil(() => worker.IsReady, ct);
        var before = worker.ProcessId!.Value;

        Process.GetProcessById(before).Kill();

        await WaitUntil(() => worker.IsReady && worker.ProcessId != before, ct, TimeSpan.FromSeconds(30));
        Assert.Equal(2, _pool.Status.ReadyWorkers);

        await using var lease = await _pool.AcquireAsync(ct);
        await lease.Engine.SetPositionAsync(Position.Initial.ToFen(), ct);
        var result = await lease.Engine.SearchAsync(new SearchLimits(0, 4, 100, 1000, false), ct);
        Assert.NotEmpty(result.BestMove);
    }

    [Fact]
    public async Task Dispose_waits_for_a_stop_already_in_progress()
    {
        // The host stops hosted services while the container may already be disposing the pool.
        // Disposing closes the kill-on-close job object, so it must not overtake a graceful stop.
        var ct = TestContext.Current.CancellationToken;
        var pool = new EngineWorkerPool(
            Options.Create(new EngineOptions { Type = "builtin", Workers = 2 }),
            NullLogger<EngineWorkerPool>.Instance,
            NullLoggerFactory.Instance);
        await pool.StartAsync(ct);
        Assert.Equal(2, pool.Status.ReadyWorkers);
        var processes = pool.Workers.Select(w => Process.GetProcessById(w.ProcessId!.Value)).ToList();

        var stop = pool.StopAsync(ct);
        await pool.DisposeAsync();

        Assert.True(stop.IsCompleted);
        Assert.All(processes, p => Assert.True(p.HasExited));
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(50, ct);
        }
    }
}
