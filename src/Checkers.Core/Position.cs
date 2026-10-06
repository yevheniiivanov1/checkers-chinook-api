using System.Numerics;
using System.Text;

namespace Checkers.Core;

/// <summary>
/// An immutable checkers position: three 32-bit boards (bit <c>i</c> is PDN square <c>i+1</c>)
/// and the side to move.
/// </summary>
public readonly record struct Position(uint Black, uint White, uint Kings, Side SideToMove)
{
    public const int MaxPiecesPerSide = 12;

    /// <summary>The standard starting position, Black to move.</summary>
    public static Position Initial { get; } = new(0x0000_0FFFu, 0xFFF0_0000u, 0, Side.Black);

    public uint Occupied => Black | White;

    public uint Empty => ~Occupied;

    public int PieceCount => BitOperations.PopCount(Occupied);

    public uint PiecesOf(Side side) => side == Side.Black ? Black : White;

    public bool IsKing(int square) => (Kings & Board.Bit(square)) != 0;

    public Side? OwnerOf(int square)
    {
        var bit = Board.Bit(square);
        if ((Black & bit) != 0)
        {
            return Side.Black;
        }

        return (White & bit) != 0 ? Side.White : null;
    }

    /// <summary>
    /// Canonical PDN FEN: turn first, then White and Black lists in ascending square order with
    /// a <c>K</c> prefix for kings, e.g. <c>B:W18,19,K22:B1,5</c>. Two equal positions always
    /// produce the same string, which makes it usable as a cache key.
    /// </summary>
    public string ToFen()
    {
        var sb = new StringBuilder(96);
        sb.Append(SideToMove.ToPdn()).Append(":W");
        AppendSquares(sb, White);
        sb.Append(":B");
        AppendSquares(sb, Black);
        return sb.ToString();
    }

    public override string ToString() => ToFen();

    private void AppendSquares(StringBuilder sb, uint pieces)
    {
        var first = true;
        for (var square = 0; square < Board.SquareCount; square++)
        {
            if ((pieces & Board.Bit(square)) == 0)
            {
                continue;
            }

            if (!first)
            {
                sb.Append(',');
            }

            if (IsKing(square))
            {
                sb.Append('K');
            }

            sb.Append(square + 1);
            first = false;
        }
    }
}
