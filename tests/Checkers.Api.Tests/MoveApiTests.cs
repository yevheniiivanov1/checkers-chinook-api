using System.Net;
using System.Net.Http.Json;
using System.Text;
using Checkers.Core;

namespace Checkers.Api.Tests;

/// <summary>The HTTP contract, end to end through real worker processes (builtin engine).</summary>
public sealed class MoveApiTests(BuiltinEngineApp app) : IClassFixture<BuiltinEngineApp>
{
    private const string TaskExample = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16";
    private readonly HttpClient _client = app.CreateClient();

    [Fact]
    public async Task Health_is_ok_with_both_workers_after_startup()
    {
        var response = await _client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(2, body.GetProperty("workers").GetInt32());
        Assert.Equal("builtin", body.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Suggest_returns_a_legal_move_in_the_documented_shape()
    {
        var response = await _client.Suggest(TaskExample, "weak", new { maxDepth = 12, softTimeMs = 250, hardTimeMs = 1200 });
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("builtin", body.GetProperty("engine").GetString());
        Assert.Contains(body.GetProperty("bestMove").GetString(), new[] { "14x23", "16x23" }); // capture is compulsory
        Assert.Equal(body.GetProperty("bestMove").GetString(), body.GetProperty("pv")[0].GetString());
        Assert.InRange(body.GetProperty("depth").GetInt32(), 6, 8); // weak: depth 6 to 8
        Assert.True(body.GetProperty("nodes").GetInt64() > 0);
        Assert.Equal("pdn:" + TaskExample, body.GetProperty("positionKey").GetString());
        Assert.False(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.True(body.GetProperty("info").TryGetProperty("timeMs", out _));
        Assert.True(body.TryGetProperty("scoreOrWDL", out _));
    }

    [Fact]
    public async Task Pv_replays_legally_from_the_position()
    {
        var body = await (await _client.Suggest(Position.Initial.ToFen(), "medium")).Body();

        var position = Position.Initial;
        foreach (var move in body.GetProperty("pv").EnumerateArray())
        {
            var legal = Assert.Single(MoveNotation.FindLegal(position, move.GetString()));
            position = MoveGenerator.Apply(position, legal);
        }
    }

    [Fact]
    public async Task Same_position_and_level_is_served_from_the_cache()
    {
        const string Position = "W:W17,21,22,23,25,26,27,28,30:B1,2,3,5,6,7,9,11,12,15";

        var first = await (await _client.Suggest(Position, "weak")).Body();
        // Different spelling of the same position: the cache key is the canonical PDN.
        var second = await (await _client.Suggest("W:B15,12,11,9,7,6,5,3,2,1:W30,28,27,26,25,23,22,21,17", "weak")).Body();
        var otherLevel = await (await _client.Suggest(Position, "medium")).Body();

        Assert.False(first.GetProperty("info").GetProperty("cached").GetBoolean());
        Assert.True(second.GetProperty("info").GetProperty("cached").GetBoolean());
        Assert.Equal(first.GetProperty("bestMove").GetString(), second.GetProperty("bestMove").GetString());
        Assert.False(otherLevel.GetProperty("info").GetProperty("cached").GetBoolean());
    }

    [Theory]
    [InlineData("B:W18,19,33:B1,5")]       // square out of range
    [InlineData("B:W18,18:B1")]            // square used twice
    [InlineData("B:W5-17:B1")]             // 13 white pieces
    [InlineData("B:W18:B30")]              // uncrowned man on its crowning rank
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("B:W١٨,19:B1")] // Arabic-Indic digits once gave 500
    [InlineData("B:W１８:B1")]     // full-width digits
    public async Task Invalid_pdn_is_422_with_the_reasons(string position)
    {
        var response = await _client.Suggest(position, "weak");
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(body.GetProperty("errors").GetProperty("state.position").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"gameId":"chess","state":{"notation":"PDN","position":"B:W21-32:B1-12"}}""", "gameId")]
    [InlineData("""{"state":{"notation":"FEN","position":"B:W21-32:B1-12"}}""", "state.notation")]
    [InlineData("""{"state":{"position":"B:W21-32:B1-12"},"level":"godlike"}""", "level")]
    [InlineData("""{"state":{"position":"B:W21-32:B1-12"},"limits":{"softTimeMs":900,"hardTimeMs":500}}""", "limits.softTimeMs")]
    [InlineData("""{"state":{"position":"B:W21-32:B1-12"},"limits":{"maxDepth":0}}""", "limits.maxDepth")]
    [InlineData("""{"state":{"position":"W:W5:B1"}}""", "state.position")] // White is blocked: game over
    [InlineData("""{"level":"weak"}""", "state.position")]
    public async Task Semantically_invalid_requests_are_422(string json, string field)
    {
        var response = await _client.PostAsync("/v1/move/suggest", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), $"expected an error for {field}: {body}");
    }

    [Fact]
    public async Task No_cache_asks_the_engine_again()
    {
        const string Position = "B:W17,21,22,23,25,26,27,28,30:B1,2,3,5,6,7,9,11,12,15";
        await _client.Suggest(Position, "weak");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/move/suggest")
        {
            Content = JsonContent.Create(new { state = new { position = Position }, level = "weak" }),
        };
        request.Headers.CacheControl = new() { NoCache = true };
        var body = await (await _client.SendAsync(request, TestContext.Current.CancellationToken)).Body();

        Assert.False(body.GetProperty("info").GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task Malformed_json_is_400()
    {
        var response = await _client.PostAsync("/v1/move/suggest", new StringContent("{not json", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_id_is_echoed_and_reported_in_problems()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/move/suggest")
        {
            Content = JsonContent.Create(new { state = new { position = "B:W99:B1" } }),
        };
        request.Headers.Add("X-Request-Id", "test-123");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Body();

        Assert.Equal("test-123", response.Headers.GetValues("X-Request-Id").Single());
        Assert.Equal("test-123", body.GetProperty("requestId").GetString());
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("<script>")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 characters
    public async Task An_unsafe_request_id_is_replaced(string requestId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        var echoed = response.Headers.GetValues("X-Request-Id").Single();
        Assert.NotEqual(requestId, echoed);
        Assert.Matches("^[0-9a-f]{32}$", echoed);
    }

    [Theory]
    [InlineData(TaskExample, "14x23", true)]
    [InlineData(TaskExample, "16x23", true)]
    [InlineData(TaskExample, "10-15", false)] // a capture is compulsory
    [InlineData("B:W21-32:B1-12", "11-15", true)]
    [InlineData("B:W21-32:B1-12", "11-16", true)]
    [InlineData("B:W21-32:B1-12", "11-14", false)]
    [InlineData("B:W21-32:B1-12", "22-18", false)] // not Black's piece
    [InlineData("B:W10,19:B6", "6x24", true)]      // short form of 6x15x24
    [InlineData("B:W10,19:B6", "6x15", false)]     // must finish the jump
    [InlineData("B:W21-32:B1-12", "nonsense", false)]
    [InlineData("B:W21-32:B1-12", "١١-15", false)] // non-ASCII digits once gave 500
    [InlineData("B:W10,19:B6", "6-24", false)]                // a capture needs 'x'
    [InlineData("B:W21-32:B1-12", "11x15", false)]            // a step needs '-'
    public async Task Validate_reports_legality(string position, string move, bool legal)
    {
        var response = await _client.PostAsJsonAsync("/v1/move/validate", new { position, move }, TestContext.Current.CancellationToken);
        var body = await response.Body();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(legal, body.GetProperty("legal").GetBoolean());
        Assert.Equal(legal, body.TryGetProperty("resultPosition", out _));
        Assert.Equal(!legal, body.TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Validate_explains_a_wrong_separator()
    {
        var body = await (await _client.PostAsJsonAsync("/v1/move/validate", new { position = "B:W10,19:B6", move = "6-24" }, TestContext.Current.CancellationToken)).Body();

        Assert.Contains("did you mean 6x15x24", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Validate_reports_an_ambiguous_short_capture_instead_of_picking_a_route()
    {
        var body = await (await _client.PostAsJsonAsync("/v1/move/validate", new { position = "W:W22:B9,10,17,18", move = "22x6" }, TestContext.Current.CancellationToken)).Body();

        Assert.True(body.GetProperty("legal").GetBoolean());
        Assert.True(body.GetProperty("ambiguous").GetBoolean());
        Assert.Equal(["22x13x6", "22x15x6"], body.GetProperty("candidates").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.False(body.TryGetProperty("resultPosition", out _));
    }

    [Fact]
    public async Task Validate_returns_the_position_after_the_move()
    {
        var body = await (await _client.PostAsJsonAsync("/v1/move/validate", new { position = "B:W10,19:B6", move = "6x24" }, TestContext.Current.CancellationToken)).Body();

        Assert.Equal("6x15x24", body.GetProperty("move").GetString());
        Assert.Equal("W:W:B24", body.GetProperty("resultPosition").GetString());
    }

    [Fact]
    public async Task Validate_with_invalid_pdn_is_422()
    {
        var response = await _client.PostAsJsonAsync("/v1/move/validate", new { position = "B:W40:B1", move = "1-5" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Legal_moves_lists_paths_captures_and_results()
    {
        var body = await (await _client.PostAsJsonAsync("/v1/position/moves", new { position = "B:W10,19:B6" }, TestContext.Current.CancellationToken)).Body();

        var move = Assert.Single(body.GetProperty("moves").EnumerateArray());
        Assert.Equal("6x15x24", move.GetProperty("move").GetString());
        Assert.Equal([6, 15, 24], move.GetProperty("path").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal([10, 19], move.GetProperty("captures").EnumerateArray().Select(e => e.GetInt32()));
        Assert.False(body.GetProperty("gameOver").GetBoolean());
    }

    [Fact]
    public async Task The_test_board_is_served_at_the_root()
    {
        var html = await _client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("Checkers engine test board", html);
    }
}
