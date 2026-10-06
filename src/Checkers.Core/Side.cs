namespace Checkers.Core;

/// <summary>
/// The two players. In English checkers Black starts on squares 1–12, moves first and moves
/// towards square 32; White starts on 21–32 and moves towards square 1.
/// </summary>
public enum Side
{
    Black,
    White,
}

public static class SideExtensions
{
    public static Side Opponent(this Side side) => side == Side.Black ? Side.White : Side.Black;

    public static char ToPdn(this Side side) => side == Side.Black ? 'B' : 'W';
}
