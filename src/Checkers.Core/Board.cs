namespace Checkers.Core;

/// <summary>
/// Geometry of the 32 playable squares. Squares are numbered 1–32 in PDN; internally they are
/// indexed 0–31 and used as bit positions in <see cref="Position"/>.
/// </summary>
/// <remarks>
/// With White at the bottom, row 0 is the top rank: square 1 is b8, square 4 is h8, square 5 is
/// a7 and square 29 is a1. A square's column is <c>2*i+1</c> on even rows and <c>2*i</c> on odd
/// rows, where <c>i</c> is its index within the row.
/// </remarks>
public static class Board
{
    public const int SquareCount = 32;

    /// <summary>Squares where a Black man is crowned (29–32).</summary>
    public const uint BlackKingRow = 0xF000_0000u;

    /// <summary>Squares where a White man is crowned (1–4).</summary>
    public const uint WhiteKingRow = 0x0000_000Fu;

    /// <summary>Diagonal directions as (row delta, column delta). Black men use the first two.</summary>
    private static readonly (int Row, int Col)[] Directions = [(1, -1), (1, 1), (-1, -1), (-1, 1)];

    // [square, direction] -> adjacent square or -1, and the square two steps away or -1.
    private static readonly int[,] Step = new int[SquareCount, 4];
    private static readonly int[,] Jump = new int[SquareCount, 4];

    static Board()
    {
        for (var square = 0; square < SquareCount; square++)
        {
            var (row, col) = Coordinates(square);
            for (var d = 0; d < 4; d++)
            {
                Step[square, d] = SquareAt(row + Directions[d].Row, col + Directions[d].Col);
                Jump[square, d] = SquareAt(row + 2 * Directions[d].Row, col + 2 * Directions[d].Col);
            }
        }
    }

    /// <summary>Directions a piece may move in: men only forward, kings both ways.</summary>
    public static ReadOnlySpan<int> DirectionsFor(Side side, bool king) =>
        king ? [0, 1, 2, 3] : side == Side.Black ? [0, 1] : [2, 3];

    public static int Neighbour(int square, int direction) => Step[square, direction];

    public static int JumpTarget(int square, int direction) => Jump[square, direction];

    public static uint KingRow(Side side) => side == Side.Black ? BlackKingRow : WhiteKingRow;

    /// <summary>Row (0 = top, Black's back rank) and column (0 = file a) of a square index.</summary>
    public static (int Row, int Col) Coordinates(int square)
    {
        var row = square / 4;
        var indexInRow = square % 4;
        return (row, row % 2 == 0 ? 2 * indexInRow + 1 : 2 * indexInRow);
    }

    /// <summary>Square index at a row/column, or -1 when off the board or on a light square.</summary>
    public static int SquareAt(int row, int col)
    {
        if (row is < 0 or > 7 || col is < 0 or > 7 || (row + col) % 2 == 0)
        {
            return -1;
        }

        return row * 4 + col / 2;
    }

    public static uint Bit(int square) => 1u << square;
}
