using System.Diagnostics;
using System.Text;
using Checkers.Api.Engine;
using Checkers.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Checkers.Api.Moves;

/// <summary>
/// Moves first-call costs (JIT, JSON metadata, MVC model binding) off the first client requests:
/// a tablebase answer must come back in under 50 ms, and a cold first POST took ~70 ms.
/// </summary>
/// <remarks>
/// Two phases. Before the server listens, the suggestion path is run directly (engine round trip,
/// legality check). Once the server is up, a few real requests are sent to the app's own address,
/// which warms routing, model binding and problem responses too; they send <c>Cache-Control:
/// no-cache</c> so the suggestions go through the engine again, and appear in the request log with
/// ids <c>warm-up-*</c>. Warm-up requests use limits no client is likely to send, so they never put
/// answers in the cache that a client would then receive.
/// </remarks>
internal sealed class StartupWarmUp(
    MoveSuggestionService suggestions,
    IEnginePool pool,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<StartupWarmUp> logger) : IHostedService
{
    private const string TablebasePosition = "W:W17,22,26:B1,6";
    private const string MidgamePosition = "W:W18,19,22,24,26,27,28,30,31:B1,2,3,5,6,7,9,11,12";
    private const string WarmUpLimits = """{"maxDepth":7,"softTimeMs":19,"hardTimeMs":997}""";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (pool.Status.ReadyWorkers == 0)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        try
        {
            foreach (var pdn in new[] { TablebasePosition, MidgamePosition })
            {
                var limits = new ResolvedLimits(LevelPolicy.CustomLevel, 0, 7, 19, 997);
                await suggestions.SuggestAsync(Pdn.Parse(pdn), limits, Stopwatch.StartNew(), cancellationToken);
            }

            logger.LogInformation("Suggestion path warmed up in {TimeMs} ms", clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Warm-up failed; the first requests may be slower");
            return;
        }

        lifetime.ApplicationStarted.Register(() => _ = Task.Run(WarmUpHttpAsync));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmUpHttpAsync()
    {
        // Kestrel and IIS report their bindings here; an in-memory test server reports none.
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address is null || !Uri.TryCreate(LocalAddress(address), UriKind.Absolute, out var baseAddress))
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        try
        {
            using var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
            var requests = new (string Path, string Body)[]
            {
                ("/v1/move/suggest", Suggest(TablebasePosition)),
                ("/v1/move/suggest", Suggest(MidgamePosition)),
                ("/v1/move/suggest", Suggest("B:W40:B1")), // 422 path
                ("/v1/move/validate", $$"""{"position":"{{MidgamePosition}}","move":"22-17"}"""),
                ("/v1/position/moves", $$"""{"position":"{{MidgamePosition}}"}"""),
            };

            for (var i = 0; i < requests.Length; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, requests[i].Path)
                {
                    Content = new StringContent(requests[i].Body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Request-Id", $"warm-up-{i + 1}");
                request.Headers.CacheControl = new() { NoCache = true }; // through the engine, not the answer phase one cached
                using var response = await http.SendAsync(request, lifetime.ApplicationStopping);
            }

            logger.LogInformation("HTTP pipeline warmed up via {Address} in {TimeMs} ms", baseAddress, clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("HTTP warm-up via {Address} failed: {Error}", baseAddress, ex.Message);
        }

        static string Suggest(string pdn) =>
            $$"""{"gameId":"checkers-8x8","state":{"notation":"PDN","position":"{{pdn}}"},"limits":{{WarmUpLimits}}}""";
    }

    /// <summary>A wildcard binding such as <c>http://*:8080</c> or <c>http://+:80</c> is reached via localhost.</summary>
    private static string LocalAddress(string address) =>
        address.Replace("://*", "://localhost").Replace("://+", "://localhost")
               .Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost");
}
