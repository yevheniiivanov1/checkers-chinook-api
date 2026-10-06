using Checkers.Core;
using Checkers.EngineHost;
using Checkers.EngineHost.KingsRow;

namespace Checkers.Core.Tests;

/// <summary>KingsRow adapter pieces that do not need the DLL; the lines are real KingsRow 1.20 output.</summary>
public sealed class KingsRowTests
{
    [Fact]
    public void Parses_a_completed_iteration()
    {
        var status = KingsRowStatus.Parse("value=115,  depth 14/15.1/25,  0.0s,  7149 kN/s,  pv 14x23 27x18 16x23 28-24 5-9");

        Assert.Equal('=', status.Bound);
        Assert.Equal(115, status.Value);
        Assert.Equal(14, status.Depth);
        Assert.Equal(7149, status.KiloNodesPerSecond);
        Assert.Equal(["14x23", "27x18", "16x23", "28-24", "5-9"], status.Pv);
        Assert.True(status.IsCompletedIteration);
        Assert.False(status.IsDatabaseDraw);
    }

    [Theory]
    [InlineData("value>84,  depth 7/8.3/12,  0.0s,  832 kN/s,  ", '>', 84, 7)]
    [InlineData("value<-2647,  depth 25/22.3/35,  0.3s,  4170 kN/s,  pv 6-9 25-21", '<', -2647, 25)]
    [InlineData("value<8,  depth 7//5.9/14,  0.0s,  354 kN/s,  pv 11-15 22-18", '<', 8, 7)] // torn read
    public void Iterations_in_progress_are_not_complete(string line, char bound, int value, int depth)
    {
        var status = KingsRowStatus.Parse(line);

        Assert.Equal(bound, status.Bound);
        Assert.Equal(value, status.Value);
        Assert.Equal(depth, status.Depth);
        Assert.False(status.IsCompletedIteration);
    }

    [Fact]
    public void Recognises_the_database_draw_format()
    {
        var status = KingsRowStatus.Parse("depth 6; 14-18* (0.222), 14-9* (0.222), 14-17 (0.278), 10-15 (0.444), ");

        Assert.True(status.IsDatabaseDraw);
        Assert.Equal(6, status.Depth);
        Assert.Null(status.Value);
        Assert.Empty(status.Pv);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("searching...")]
    [InlineData("value=١٢,  depth ٧/8.3/12")] // non-ASCII digits are not numbers here
    public void Unknown_lines_parse_to_nothing(string? line)
    {
        var status = KingsRowStatus.Parse(line);

        Assert.Null(status.Depth);
        Assert.Null(status.Value);
        Assert.False(status.IsDatabaseDraw);
    }

    [Theory]
    [InlineData(CheckerBoardApi.ResultWin, false, 0, 1)]
    [InlineData(CheckerBoardApi.ResultLoss, false, 0, -1)]
    [InlineData(CheckerBoardApi.ResultDraw, false, 0, 0)]
    [InlineData(CheckerBoardApi.ResultUnknown, false, 3979, null)] // outside the database: no claim
    [InlineData(CheckerBoardApi.ResultUnknown, true, 3979, 1)]
    [InlineData(CheckerBoardApi.ResultUnknown, true, -2700, -1)]
    [InlineData(CheckerBoardApi.ResultUnknown, true, 1, 0)]       // ±1 is KingsRow's database draw
    [InlineData(CheckerBoardApi.ResultUnknown, true, 50, null)]
    public void Game_value_from_result_and_status(int outcome, bool inDatabase, int score, int? expected)
    {
        var status = KingsRowStatus.Parse($"value={score},  depth 9/9.0/12,  0.0s,  900 kN/s,  pv 1-5");

        Assert.Equal(expected, KingsRowEngine.GameValue(outcome, inDatabase, status, score));
    }

    [Fact]
    public void A_database_draw_line_is_a_draw_even_when_getmove_says_unknown() =>
        Assert.Equal(0, KingsRowEngine.GameValue(CheckerBoardApi.ResultUnknown, true, KingsRowStatus.Parse("depth 6; 1-5* (0.333), 3-8 (0.417), "), 0));

    [Theory]
    [InlineData("Using the Chinook 7-piece WLD endgame database and 256mb for database buffers.", 7)]
    [InlineData("Using the Kingsrow 10-piece WLD endgame database", 10)]
    [InlineData("Not using an endgame database.", 0)]
    public void Reads_the_loaded_database_size_from_about(string about, int pieces) =>
        Assert.Equal(pieces, KingsRowEngine.ParseTablebasePieces(about));

    [Fact]
    public void The_log_folder_kingsrow_needs_exists_before_it_is_loaded()
    {
        // KingsRow ends the process with 0xC0000409 when it cannot open this log (seen under IIS).
        var folder = KingsRowEngine.EnsureLogDirectory();

        Assert.True(Directory.Exists(folder));
        Assert.EndsWith(Path.Combine("Ed Gilbert", "Kingsrow"), folder);
    }

    /// <summary>numbertocoors() in cb_interface.h for English checkers.</summary>
    [Theory]
    [InlineData(1, 6, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(5, 7, 1)]
    [InlineData(29, 7, 7)]
    [InlineData(32, 1, 7)]
    public void Square_numbers_map_to_checkerboard_coordinates(int square, int x, int y) =>
        Assert.Equal((x, y), CheckerBoardApi.ToCoordinates(square - 1));

    [Fact]
    public void Board_conversion_round_trips()
    {
        var position = Pdn.Parse("W:W18,19,K22,25:B1,K5,6,26");
        Span<int> board = stackalloc int[CheckerBoardApi.Cells];

        CheckerBoardApi.WriteBoard(position, board);

        Assert.Equal(CheckerBoardApi.Black | CheckerBoardApi.Man, board[6 * 8 + 0]);   // square 1
        Assert.Equal(CheckerBoardApi.White | CheckerBoardApi.King, board[CheckerBoardApi.ToCoordinates(21).X * 8 + CheckerBoardApi.ToCoordinates(21).Y]);
        Assert.Equal(position, CheckerBoardApi.ReadBoard(board, Side.White));
    }

    [Fact]
    public void Pv_is_replayed_in_canonical_notation_and_cut_at_the_first_illegal_move()
    {
        var root = Position.Initial;
        var played = MoveNotation.FindLegal(root, "11-15").Single();

        Assert.Equal(["11-15", "22-18", "15x22", "25x18"], PrincipalVariation.Normalize(root, ["11-15", "22-18", "15x22", "25x18"], played));
        Assert.Equal(["11-15", "22-18"], PrincipalVariation.Normalize(root, ["11-15", "22-18", "9-14", "25x18"], played));
    }

    [Fact]
    public void Pv_that_does_not_start_with_the_played_move_is_replaced_by_it()
    {
        var root = Position.Initial;
        var played = MoveNotation.FindLegal(root, "9-13").Single();

        Assert.Equal(["9-13"], PrincipalVariation.Normalize(root, ["11-15", "22-18"], played));
    }
}
