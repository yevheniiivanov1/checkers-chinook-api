using System.Diagnostics;
using System.Net;
using Checkers.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Checkers.Api.Tests;

/// <summary>
/// The app wired to KingsRow and the Chinook databases. Paths come from CHECKERS_ENGINE_PATH and
/// CHECKERS_DATABASES, defaulting to the locations used in appsettings.json.
/// </summary>
public sealed class ChinookApp : WebApplicationFactory<Program>
{
    public static string EnginePath { get; } =
        Environment.GetEnvironmentVariable("CHECKERS_ENGINE_PATH") ?? @"C:\engines\kingsrow\engines\Kingsrow64.dll";

    public static string Databases { get; } =
        Environment.GetEnvironmentVariable("CHECKERS_DATABASES") ?? @"D:\tb\chinook";

    public static bool Installed => OperatingSystem.IsWindows() && File.Exists(EnginePath) && Directory.Exists(Databases);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Engine:Type", "chinook");
        builder.UseSetting("Engine:Path", EnginePath);
        builder.UseSetting("Engine:Databases", Databases);
        builder.UseSetting("Engine:Workers", "2");
        builder.UseSetting("Serilog:MinimumLevel:Default", Environment.GetEnvironmentVariable("TEST_LOG_LEVEL") ?? "Warning");
        builder.UseSetting("LogFiles:Directory", "");
    }

    private HttpClient? _client;

    /// <summary>
    /// Created on first use, so nothing is launched where KingsRow is missing. One request warms
    /// the test side's own HTTP and JSON code, so the timings in the tests measure the service.
    /// </summary>
    public HttpClient WarmClient
    {
        get
        {
            if (_client is null)
            {
                var client = CreateClient();
                client.Suggest("B:W21-32:B1-12", "weak").GetAwaiter().GetResult().EnsureSuccessStatusCode();
                _client = client;
            }

            return _client;
        }
    }
}

/// <summary>The app pointed at a database folder KingsRow cannot use.</summary>
public sealed class ChinookAppWithDatabases(string databases) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Engine:Type", "chinook");
        builder.UseSetting("Engine:Path", ChinookApp.EnginePath);
        builder.UseSetting("Engine:Databases", databases);
        builder.UseSetting("Engine:Workers", "1");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Fatal");
        builder.UseSetting("LogFiles:Directory", "");
    }
}

/// <summary>
/// The four acceptance criteria of the task, against the real engine. Skipped where KingsRow and
/// the databases are not installed (e.g. CI).
/// </summary>
public sealed class ChinookAcceptanceTests(ChinookApp app) : IClassFixture<ChinookApp>
{
    private HttpClient Client => app.WarmClient;

    [Fact]
    public async Task Health_check_returns_ok_on_startup()
    {
        SkipUnlessInstalled();

        var response = await Client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(2, body.GetProperty("workers").GetInt32());
        Assert.True(body.GetProperty("tablebasePieces").GetInt32() >= 6);
    }

    [Theory]
    [InlineData("W:W18,22,25,30:B1,5,10", 1)]   // 7 pieces, White wins
    [InlineData("W:WK14,K22:BK1", 1)]           // 3 pieces, White wins
    [InlineData("B:W18,22,25:B1,5,10,14", 1)]   // capture pending
    [InlineData("W:WK10,K14:BK1,K3", 0)]        // database draw
    [InlineData("B:W18,22,31:B1,5,10", 0)]
    public async Task Tablebase_position_returns_under_50_ms_with_tablebaseHit(string pdn, int expectedWdl)
    {
        SkipUnlessInstalled();
        var client = Client; // starts the app on first use; not part of the measured time
        var clock = Stopwatch.StartNew();

        var response = await client.Suggest(pdn, "strong");
        var elapsed = clock.ElapsedMilliseconds;
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.Equal(expectedWdl, body.GetProperty("scoreOrWDL").GetInt32());
        Assert.True(elapsed < 50, $"took {elapsed} ms");
        AssertLegal(pdn, body.GetProperty("bestMove").GetString());
    }

    [Theory]
    [InlineData("B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16")]
    [InlineData("W:W17,21,22,23,25,26,27,28,30:B1,2,3,5,6,7,9,11,12,15")]
    [InlineData("B:W21-32:B1-12")]
    [InlineData("B:W18,21,23,24,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,12")] // after 11-15 22-18 15x22 25x18
    public async Task Midgame_strong_returns_under_600_ms_with_a_legal_move(string pdn)
    {
        SkipUnlessInstalled();
        // KingsRow answers a forced move at once, without a full-depth search.
        Assert.True(MoveGenerator.LegalMoves(Pdn.Parse(pdn)).Count > 1, "test position must not have a forced move");
        var client = Client; // starts the app on first use; not part of the measured time
        var clock = Stopwatch.StartNew();

        var response = await client.Suggest(pdn, "strong");
        var elapsed = clock.ElapsedMilliseconds;
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.InRange(body.GetProperty("depth").GetInt32(), 14, 18);
        Assert.True(elapsed < 600, $"took {elapsed} ms");
        AssertLegal(pdn, body.GetProperty("bestMove").GetString());
    }

    [Fact]
    public async Task Invalid_pdn_gets_422()
    {
        SkipUnlessInstalled();

        var response = await Client.Suggest("B:W18,19,22,25,27,28,30,33:B1,5,6,7,10,12,14,16", "strong");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Timeout_returns_504()
    {
        SkipUnlessInstalled();
        var ct = TestContext.Current.CancellationToken;

        // Two long searches occupy both workers; a third request cannot get one within its hard limit.
        var busy = Enumerable.Range(0, 2)
            .Select(i => Client.Suggest(i == 0 ? "B:W21-32:B1-12" : "W:W17,21,22,23,25,26,27,28,30:B1,2,3,5,6,7,9,11,12,15", null, new { softTimeMs = 1500, hardTimeMs = 3000 }, noCache: true))
            .ToList();
        await Task.Delay(100, ct);

        // No cache: other tests may have stored an answer for this position and level.
        var response = await Client.Suggest("B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16", "weak", new { hardTimeMs = 200 }, noCache: true);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await Task.WhenAll(busy);
    }

    /// <summary>
    /// "No randomness": nothing random takes part (no opening book, dither 0, one search thread, a
    /// depth limit rather than a time limit). What can still differ, through KingsRow's
    /// transposition table, is the choice between moves of equal score and the score itself by up
    /// to about a tenth of a man — measured, not assumed; see "Determinism" in the README.
    /// </summary>
    [Fact]
    public async Task Weak_answers_vary_only_between_equally_scored_moves()
    {
        SkipUnlessInstalled();
        var client = Client;
        var targets = new[] { "W:W21,22,23,24,25,26,27,28,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,15", "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16" };
        var others = new[] { "B:W21-32:B1-12", "W:W17,21,22,23,25,26,27,28,30:B1,2,3,5,6,7,9,11,12,15", "B:W18,21,23,24,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,12" };

        foreach (var target in targets)
        {
            var answers = new List<(string Move, int Score)>();
            // Alternating with deep searches that fill the transposition table, bypassing the cache.
            for (var i = 0; i < 8; i++)
            {
                await client.Suggest(others[i % others.Length], "strong", noCache: true);
                var body = await (await client.Suggest(target, "weak", noCache: true)).Body();
                answers.Add((body.GetProperty("bestMove").GetString()!, body.GetProperty("scoreOrWDL").GetInt32()));
            }

            var described = $"{target}: {string.Join("; ", answers)}";
            // Measured spread so far: up to 12 (a man is 100).
            Assert.True(answers.Max(a => a.Score) - answers.Min(a => a.Score) <= 25, described);
            // When more than one move is chosen, each was chosen at least once with the same score as
            // another: they are alternatives of equal value, not a better and a worse answer.
            var moves = answers.Select(a => a.Move).Distinct().ToList();
            Assert.True(
                moves.Count == 1 || moves.All(m => answers.Any(a => a.Move == m && answers.Any(b => b.Move != m && b.Score == a.Score))),
                described);
        }
    }

    [Theory]
    [InlineData("chinook-empty")]
    [InlineData("chinook-données")] // not ASCII: KingsRow's API would turn the é into '?'
    public async Task A_database_folder_kingsrow_cannot_use_leaves_the_service_unhealthy(string folder)
    {
        SkipUnlessInstalled();
        var databases = Directory.CreateTempSubdirectory(folder).FullName;
        await using var app = new ChinookAppWithDatabases(databases);

        var response = await app.CreateClient().GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, (await response.Body()).GetProperty("workers").GetInt32());
        Directory.Delete(databases);
    }

    private static void AssertLegal(string pdn, string? move) =>
        Assert.Single(MoveNotation.FindLegal(Pdn.Parse(pdn), move));

    private static void SkipUnlessInstalled()
    {
        if (!ChinookApp.Installed)
        {
            Assert.Skip($"KingsRow ({ChinookApp.EnginePath}) or the Chinook databases ({ChinookApp.Databases}) are not installed.");
        }
    }
}
