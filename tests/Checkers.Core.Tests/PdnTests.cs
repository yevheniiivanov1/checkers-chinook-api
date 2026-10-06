using Checkers.Core;

namespace Checkers.Core.Tests;

public sealed class PdnTests
{
    private const string TaskExample = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16";

    [Fact]
    public void Parses_the_task_example_and_round_trips_it()
    {
        var position = Pdn.Parse(TaskExample);

        Assert.Equal(Side.Black, position.SideToMove);
        Assert.Equal(16, position.PieceCount);
        Assert.Equal(TaskExample, position.ToFen());
    }

    [Theory]
    [InlineData("""[FEN "b:b16,14,12,10,7,6,5,1:w32,30,28,27,25,22,19,18."]""")]
    [InlineData("  B : W 32,30,28,27,25,22,19,18 : B 16,14,12,10,7,6,5,1  ")]
    [InlineData("\"B:B1,5,6,7,10,12,14,16:W18,19,22,25,27,28,30,32\"")]
    public void Normalises_tags_case_order_and_whitespace(string text) =>
        Assert.Equal(TaskExample, Pdn.Parse(text).ToFen());

    [Fact]
    public void Expands_square_ranges() =>
        Assert.Equal(Position.Initial, Pdn.Parse("B:W21-32:B1-12"));

    [Fact]
    public void Reads_kings()
    {
        var position = Pdn.Parse("W:WK14,k22:BK1");

        Assert.True(position.IsKing(13));
        Assert.True(position.IsKing(21));
        Assert.True(position.IsKing(0));
        Assert.Equal("W:WK14,K22:BK1", position.ToFen());
    }

    [Fact]
    public void Accepts_a_side_with_no_pieces() =>
        Assert.Equal("W:W5:B", Pdn.Parse("W:W5").ToFen());

    [Theory]
    [InlineData("", "empty")]
    [InlineData("X:W1:B2", "Side to move")]
    [InlineData("B:W1:B2:W3", "Expected")]
    [InlineData("B:W33:B1", "out of range")]
    [InlineData("B:W0:B1", "out of range")]
    [InlineData("B:W5,5:B1", "occupied twice")]
    [InlineData("B:W5:B5", "occupied twice")]
    [InlineData("B:W5,,6:B1", "Bad square")]
    [InlineData("B:W5-3:B1", "Bad range")]
    [InlineData("B:Z1:B2", "must start with 'W' or 'B'")]
    [InlineData("B:W5:W6", "appears twice")]
    [InlineData("B:W:B", "no pieces")]
    [InlineData("B:W5-17:B1", "White has 13 pieces")]
    [InlineData("B:W5:B29", "should have been crowned")]
    [InlineData("B:W2:B9", "should have been crowned")]
    [InlineData("B:W١٨,19:B1", "Bad square")] // Arabic-Indic digits: \d would match, int.Parse would throw
    [InlineData("B:W１８:B1", "Bad square")]   // full-width digits
    [InlineData("B:W१८:B1", "Bad square")]   // Devanagari digits
    public void Rejects_invalid_positions(string text, string expectedError)
    {
        Assert.False(Pdn.TryParse(text, out _, out var errors));
        Assert.Contains(errors, e => e.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Collects_every_error_at_once()
    {
        var exception = Assert.Throws<PdnFormatException>(() => Pdn.Parse("B:W33,5:B5,40"));

        Assert.Equal(3, exception.Errors.Count);
    }

    [Fact]
    public void Kings_may_stand_on_the_crowning_rank() =>
        Assert.True(Pdn.TryParse("B:WK2:BK29", out _, out _));
}
