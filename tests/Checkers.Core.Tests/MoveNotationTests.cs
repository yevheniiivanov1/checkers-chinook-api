using Checkers.Core;

namespace Checkers.Core.Tests;

public sealed class MoveNotationTests
{
    [Theory]
    [InlineData("B:W21-32:B1-12", "11-15", "11-15")]
    [InlineData("B:W21-32:B1-12", " 11-15 ", "11-15")]
    [InlineData("B:W10,19:B6", "6x15x24", "6x15x24")]
    [InlineData("B:W10,19:B6", "6x24", "6x15x24")]      // short form of a multi-jump
    [InlineData("B:W10,19:B6", "6:15:24", "6x15x24")]
    [InlineData("B:W18,22:B14", "14x23", "14x23")]
    public void Finds_the_legal_move_meant(string pdn, string text, string expected) =>
        Assert.Equal(expected, Assert.Single(MoveNotation.FindLegal(Pdn.Parse(pdn), text)).Notation);

    [Theory]
    [InlineData("B:W21-32:B1-12", "11-14")]   // not adjacent
    [InlineData("B:W21-32:B1-12", "22-18")]   // opponent's piece
    [InlineData("B:W10,19:B6", "6x15")]       // stops a multi-jump half way
    [InlineData("B:W10,19:B6", "6x19x24")]    // wrong route
    [InlineData("B:W18,22:B14,1", "1-5")]     // a capture is compulsory
    [InlineData("B:W21-32:B1-12", "11-15-19")]
    [InlineData("B:W21-32:B1-12", "abc")]
    [InlineData("B:W21-32:B1-12", "")]
    [InlineData("B:W21-32:B1-12", "0-4")]
    [InlineData("B:W21-32:B1-12", "١١-15")] // non-ASCII digits
    [InlineData("B:W10,19:B6", "6-24")]               // a capture written as a step
    [InlineData("B:W21-32:B1-12", "11x15")]            // a step written as a capture
    [InlineData("B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16", "22-18x11-7")] // mixed separators
    public void Matches_nothing_for_illegal_or_malformed_text(string pdn, string text) =>
        Assert.Empty(MoveNotation.FindLegal(Pdn.Parse(pdn), text));

    [Fact]
    public void A_short_capture_form_can_fit_several_routes()
    {
        // From 22 to 6 over 17 and 10, or over 18 and 9: both legal, different results.
        var moves = MoveNotation.FindLegal(Pdn.Parse("W:W22:B9,10,17,18"), "22x6");

        Assert.Equal(["22x13x6", "22x15x6"], moves.Select(m => m.Notation).Order());
    }

    [Fact]
    public void The_separator_can_be_ignored_to_explain_a_mismatch()
    {
        var legal = MoveGenerator.LegalMoves(Pdn.Parse("B:W10,19:B6"));

        Assert.Equal("6x15x24", Assert.Single(MoveNotation.FindIgnoringSeparator(legal, "6-24")).Notation);
    }

    [Fact]
    public void Parses_squares_zero_based()
    {
        Assert.True(MoveNotation.TryParseSquares("22x15x6", out var squares));
        Assert.Equal([21, 14, 5], squares);
    }
}
