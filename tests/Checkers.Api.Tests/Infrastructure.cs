using System.Net.Http.Json;
using System.Text.Json;
using Checkers.Api.Engine;
using Checkers.Api.Moves;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Checkers.Api.Tests;

/// <summary>The real app with real worker processes running the builtin engine — no KingsRow needed.</summary>
public class BuiltinEngineApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Engine:Type", "builtin");
        builder.UseSetting("Engine:Path", "");
        builder.UseSetting("Engine:Databases", "");
        builder.UseSetting("Engine:Workers", "2");
        builder.UseSetting("Serilog:MinimumLevel:Default", Environment.GetEnvironmentVariable("TEST_LOG_LEVEL") ?? "Warning");
        builder.UseSetting("LogFiles:Directory", "");
    }
}

/// <summary>The app with the worker pool replaced by an in-memory double.</summary>
public sealed class StubEngineApp(StubPool pool) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Engine:Type", "builtin");
        builder.UseSetting("Serilog:MinimumLevel:Default", Environment.GetEnvironmentVariable("TEST_LOG_LEVEL") ?? "Warning");
        builder.UseSetting("LogFiles:Directory", "");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEnginePool>();
            services.AddSingleton<IEnginePool>(pool);
            services.RemoveAll(typeof(IHostedService), typeof(StartupWarmUp));
        });
    }
}

public sealed class StubPool(int readyWorkers, Func<SearchLimits, CancellationToken, Task<EngineSearchResult>> search) : IEnginePool
{
    public EnginePoolStatus Status => new(readyWorkers, 2, "builtin", "stub engine", 0);

    private int _acquisitions;

    public int Acquisitions => Volatile.Read(ref _acquisitions);

    public ValueTask<IEngineLease> AcquireAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _acquisitions);
        return readyWorkers == 0
            ? throw new EngineUnavailableException("No engine worker is running.")
            : ValueTask.FromResult<IEngineLease>(new Lease(new Adapter(search)));
    }

    public static EngineSearchResult Result(string bestMove, params string[] pv) =>
        new(bestMove, pv, 0, 100, 4, false, 0, null, 1);

    public static EngineSearchResult Stopped(string stopReason, string bestMove = "11-15") =>
        new(bestMove, [bestMove], 0, 100, 4, false, 0, null, 1, stopReason);

    private sealed class Lease(IEngineAdapter engine) : IEngineLease
    {
        public IEngineAdapter Engine => engine;

        public int WorkerId => 1;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Adapter(Func<SearchLimits, CancellationToken, Task<EngineSearchResult>> search) : IEngineAdapter
    {
        public Task SetPositionAsync(string pdn, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<EngineSearchResult> SearchAsync(SearchLimits limits, CancellationToken cancellationToken) =>
            search(limits, cancellationToken);
    }
}

internal static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<HttpResponseMessage> Suggest(this HttpClient client, string position, string? level = null, object? limits = null, bool noCache = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/move/suggest")
        {
            Content = JsonContent.Create(new { gameId = "checkers-8x8", state = new { notation = "PDN", position }, level, limits }),
        };
        if (noCache)
        {
            request.Headers.CacheControl = new() { NoCache = true };
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<JsonElement> Body(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
}

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll(this IServiceCollection services, Type serviceType, Type implementationType)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == serviceType && d.ImplementationType == implementationType).ToList())
        {
            services.Remove(descriptor);
        }
    }
}
