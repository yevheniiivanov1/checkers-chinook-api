using Checkers.Core;

namespace Checkers.Core.Tests;

public sealed class MoveGeneratorTests
{
    private static string[] Notations(string pdn) =>
        MoveGenerator.LegalMoves(Pdn.Parse(pdn)).Select(m => m.Notation).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Initial_position_has_the_seven_standard_openings() =>
        Assert.Equal(
            ["10-14", "10-15", "11-15", "11-16", "12-16", "9-13", "9-14"],
            Notations("B:W21-32:B1-12"));

    /// <summary>Published perft counts for English checkers from the starting position.</summary>
    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 49)]
    [InlineData(3, 302)]
    [InlineData(4, 1_469)]
    [InlineData(5, 7_361)]
    [InlineData(6, 36_768)]
    [InlineData(7, 179_740)]
    [InlineData(8, 845_931)]
    public void Perft_matches_known_counts(int depth, long expected) =>
        Assert.Equal(expected, MoveGenerator.Perft(Position.Initial, depth));

    [Fact]
    public void Capture_is_compulsory() =>
        Assert.Equal(["14x23", "16x23"], Notations("B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"));

    [Fact]
    public void A_multi_jump_must_be_completed()
    {
        var moves = MoveGenerator.LegalMoves(Pdn.Parse("B:W10,19:B6"));

        var move = Assert.Single(moves);
        Assert.Equal("6x15x24", move.Notation);
        Assert.Equal(Board.Bit(9) | Board.Bit(18), move.Captured);
    }

    [Fact]
    public void Crowning_ends_the_move_even_if_the_new_king_could_jump_on()
    {
        // As a king on 31 it could continue 31x24 over 27, but a man that crowns stops.
        var move = Assert.Single(MoveGenerator.LegalMoves(Pdn.Parse("B:W26,27:B22")));

        Assert.Equal("22x31", move.Notation);
        Assert.True(move.Crowns);
        Assert.Equal("W:W27:BK31", MoveGenerator.Apply(Pdn.Parse("B:W26,27:B22"), move).ToFen());
    }

    [Fact]
    public void Men_move_forward_only_and_kings_both_ways()
    {
        Assert.Equal(["18-14", "18-15"], Notations("W:W18:B1"));
        Assert.Equal(["18-14", "18-15", "18-22", "18-23"], Notations("W:WK18:B1"));
        Assert.Equal(["6-10", "6-9"], Notations("B:W32:B6"));
    }

    [Fact]
    public void Kings_jump_backwards()
    {
        var move = Assert.Single(MoveGenerator.LegalMoves(Pdn.Parse("W:WK14:B18")));

        Assert.Equal("14x23", move.Notation);
    }

    [Fact]
    public void A_piece_cannot_be_captured_twice()
    {
        // 14x23x16 takes 18 then 19; from 16 the king could jump 19 again back to 23, but may not.
        var move = Assert.Single(MoveGenerator.LegalMoves(Pdn.Parse("W:WK14:B18,19")));

        Assert.Equal("14x23x16", move.Notation);
        Assert.Equal(Board.Bit(17) | Board.Bit(18), move.Captured);
    }

    [Fact]
    public void A_blocked_side_has_no_moves() =>
        Assert.Empty(Notations("W:W5:B1"));

    [Fact]
    public void Apply_moves_the_piece_and_passes_the_turn()
    {
        var start = Position.Initial;
        var move = MoveNotation.FindLegal(start, "11-15").Single();

        Assert.Equal("W:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,12,15", MoveGenerator.Apply(start, move).ToFen());
    }

    [Fact]
    public void Apply_removes_captured_pieces()
    {
        var position = Pdn.Parse("B:W10,19:B6");
        var move = MoveGenerator.LegalMoves(position).Single();

        Assert.Equal("W:W:B24", MoveGenerator.Apply(position, move).ToFen());
    }
}
