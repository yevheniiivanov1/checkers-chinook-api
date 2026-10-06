using System.Diagnostics;
using System.Net;
using Checkers.Api.Engine;

namespace Checkers.Api.Tests;

/// <summary>Status codes for engine trouble, driven through an in-memory pool double.</summary>
public sealed class ErrorMappingTests
{
    private const string Initial = "B:W21-32:B1-12";

    private static HttpClient Client(StubPool pool) => new StubEngineApp(pool).CreateClient();

    [Fact]
    public async Task Hard_time_limit_is_504()
    {
        var client = Client(new StubPool(2, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // an engine that never answers
            throw new UnreachableException();
        }));
        var clock = Stopwatch.StartNew();

        var response = await client.Suggest(Initial, "weak", new { hardTimeMs = 300 });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(clock.ElapsedMilliseconds, 250, 2000);
        Assert.Equal("Engine timeout", (await response.Body()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task No_running_worker_is_503_and_health_says_so()
    {
        var client = Client(new StubPool(0, (_, _) => throw new UnreachableException()));

        var suggest = await client.Suggest(Initial, "weak");
        var health = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, suggest.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.False((await health.Body()).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task An_illegal_engine_move_falls_back_to_the_first_pv_move()
    {
        var client = Client(new StubPool(2, (_, _) => Task.FromResult(StubPool.Result("21-17", "9-13", "22-18"))));

        var response = await client.Suggest(Initial, "weak");
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("9-13", body.GetProperty("bestMove").GetString());
        Assert.Equal(["9-13", "22-18"], body.GetProperty("pv").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Later_pv_moves_are_not_root_moves_so_they_are_not_tried()
    {
        // pv[1] is the opponent's reply and pv[2] a move two plies later; 9-13 being legal at the
        // root is a coincidence, not a choice the engine made for this position.
        var client = Client(new StubPool(2, (_, _) => Task.FromResult(StubPool.Result("21-17", "22-18", "9-13"))));

        var response = await client.Suggest(Initial, "weak");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("The engine failed.", (await response.Body()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_worker_dying_mid_request_is_retried_once_on_another()
    {
        var calls = 0;
        var pool = new StubPool(2, (_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new EngineUnavailableException("Engine worker 1 exited.")
            : Task.FromResult(StubPool.Result("11-15")));

        var response = await Client(pool).Suggest(Initial, "weak");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, pool.Acquisitions);
    }

    [Theory]
    [InlineData("hard-time", false)]
    [InlineData("depth", true)]
    [InlineData(null, true)] // the engine finished by itself (forced move, database)
    public async Task Answers_cut_by_the_hard_limit_are_not_cached(string? stopReason, bool cached)
    {
        var client = Client(new StubPool(2, (_, _) => Task.FromResult(StubPool.Stopped(stopReason!))));

        await client.Suggest(Initial, null, new { softTimeMs = 40 });
        var second = await (await client.Suggest(Initial, null, new { softTimeMs = 40 })).Body();

        Assert.Equal(cached, second.GetProperty("info").GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task A_soft_limit_shortened_by_the_deadline_is_not_cached()
    {
        // weak means 100 ms, but hardTimeMs 60 leaves the worker 35 ms: a shallower answer than the key says.
        SearchLimits? seen = null;
        var client = Client(new StubPool(2, (limits, _) =>
        {
            seen = limits;
            return Task.FromResult(StubPool.Stopped("soft-time"));
        }));

        await client.Suggest(Initial, "weak", new { hardTimeMs = 60 });
        var second = await (await client.Suggest(Initial, "weak", new { hardTimeMs = 60 })).Body();

        Assert.True(seen!.SoftTimeMs < 100);
        Assert.False(second.GetProperty("info").GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task One_client_cannot_hold_more_than_its_share_of_engine_requests()
    {
        var release = new TaskCompletionSource<EngineSearchResult>();
        var client = Client(new StubPool(2, async (_, ct) => await release.Task.WaitAsync(ct)));

        // Default limit: 4 per client, no queue. Different positions, so the cache plays no part.
        var positions = new[] { Initial, "W:W21-32:B1-12", "B:W21-32:B1-11", "B:W21-32:B2-12", "B:W22-32:B1-12", "B:W21-31:B1-12" };
        var requests = positions.Select(p => client.Suggest(p, null, new { hardTimeMs = 3000 })).ToList();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        release.SetResult(StubPool.Result("11-15"));
        var statuses = (await Task.WhenAll(requests)).Select(r => r.StatusCode).ToList();

        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        var rejected = (await Task.WhenAll(requests)).First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("1", rejected.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task Long_searches_without_a_level_are_capped()
    {
        var client = Client(new StubPool(2, (_, _) => Task.FromResult(StubPool.Result("11-15"))));

        var response = await client.Suggest(Initial, null, new { softTimeMs = 2500, hardTimeMs = 3000 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await response.Body()).GetProperty("errors").TryGetProperty("limits.softTimeMs", out _));
    }

    [Fact]
    public async Task Level_limits_reach_the_engine_clamped_to_the_level()
    {
        SearchLimits? seen = null;
        var client = Client(new StubPool(2, (limits, _) =>
        {
            seen = limits;
            return Task.FromResult(StubPool.Result("11-15"));
        }));

        await client.Suggest(Initial, "weak", new { maxDepth = 12, softTimeMs = 250, hardTimeMs = 1200 });

        Assert.NotNull(seen);
        Assert.Equal(6, seen.MinDepth);
        Assert.Equal(8, seen.MaxDepth);      // weak caps depth at 8...
        Assert.Equal(100, seen.SoftTimeMs);  // ...and time at 100 ms
        Assert.InRange(seen.HardTimeMs, 1000, 1200); // a little under the request's hard limit
        Assert.False(seen.Tablebase);
    }
}
