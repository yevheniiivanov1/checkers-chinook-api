using Checkers.Api.Caching;
using Checkers.Api.Contracts;
using Checkers.Api.Moves;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Checkers.Api.Tests;

public sealed class LruCacheTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Evicts_the_least_recently_used_entry()
    {
        var cache = new LruCache<string, int>(2, TimeSpan.FromMinutes(15), _time);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGet("a", out _)); // "a" is now the most recent

        cache.Set("c", 3);

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Entries_expire_after_the_ttl_even_if_read()
    {
        var cache = new LruCache<string, int>(10, TimeSpan.FromMinutes(15), _time);
        cache.Set("a", 1);

        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.True(cache.TryGet("a", out _));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Count_leaves_out_expired_entries()
    {
        var cache = new LruCache<string, int>(10, TimeSpan.FromMinutes(15), _time);
        cache.Set("old", 1);
        _time.Advance(TimeSpan.FromMinutes(10));
        cache.Set("new", 2);
        _time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Overwriting_refreshes_value_and_expiry()
    {
        var cache = new LruCache<string, int>(10, TimeSpan.FromMinutes(15), _time);
        cache.Set("a", 1);
        _time.Advance(TimeSpan.FromMinutes(10));
        cache.Set("a", 2);
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(2, value);
    }

    [Fact]
    public void Capacity_zero_disables_caching()
    {
        var cache = new LruCache<string, int>(0, TimeSpan.FromMinutes(15), _time);
        cache.Set("a", 1);

        Assert.False(cache.TryGet("a", out _));
    }
}

public sealed class LevelPolicyTests
{
    private static readonly LevelPolicy Policy = new(
        Options.Create(new Dictionary<string, LevelOptions>()),
        Options.Create(new LimitsOptions { DefaultSoftTimeMs = 300, DefaultHardTimeMs = 1200 }));

    [Theory]
    [InlineData("weak", 6, 8, 100)]
    [InlineData("medium", 10, 12, 250)]
    [InlineData("STRONG", 14, 18, 500)]
    public void Levels_define_depth_and_time_bands(string level, int minDepth, int maxDepth, int moveTime)
    {
        var limits = Policy.Resolve(level, null);

        Assert.Equal((minDepth, maxDepth, moveTime, 1200), (limits.MinDepth, limits.MaxDepth, limits.SoftTimeMs, limits.HardTimeMs));
    }

    [Fact]
    public void Request_limits_only_narrow_a_level()
    {
        Assert.Equal(8, Policy.Resolve("weak", new SearchLimitsRequest { MaxDepth = 12 }).MaxDepth);
        Assert.Equal(14, Policy.Resolve("strong", new SearchLimitsRequest { MaxDepth = 4 }).MaxDepth);
        Assert.Equal(100, Policy.Resolve("weak", new SearchLimitsRequest { SoftTimeMs = 250 }).SoftTimeMs);
        Assert.Equal(50, Policy.Resolve("weak", new SearchLimitsRequest { SoftTimeMs = 50 }).SoftTimeMs);
    }

    [Fact]
    public void Without_a_level_the_request_or_defaults_apply()
    {
        var defaults = Policy.Resolve(null, null);
        var custom = Policy.Resolve(null, new SearchLimitsRequest { MaxDepth = 5, SoftTimeMs = 40, HardTimeMs = 90 });

        Assert.Equal((LevelPolicy.CustomLevel, 300, 1200), (defaults.Level, defaults.SoftTimeMs, defaults.HardTimeMs));
        Assert.Equal((5, 40, 90), (custom.MaxDepth, custom.SoftTimeMs, custom.HardTimeMs));
    }

    [Fact]
    public void Soft_time_never_exceeds_the_hard_limit() =>
        Assert.Equal(80, Policy.Resolve("strong", new SearchLimitsRequest { HardTimeMs = 80 }).SoftTimeMs);

    [Fact]
    public void Contradictory_limits_are_rejected()
    {
        var error = Assert.Throws<RequestValidationException>(() =>
            Policy.Resolve("expert", new SearchLimitsRequest { SoftTimeMs = 500, HardTimeMs = 100, MaxDepth = 99 }));

        Assert.Equal(["level", "limits.maxDepth", "limits.softTimeMs"], error.Errors.Keys.Order());
    }
}
