using Checkers.Core;

namespace Checkers.EngineHost.KingsRow;

/// <summary>
/// Constants and board conversion for the CheckerBoard engine API (Martin Fierz), which KingsRow
/// implements: <c>int getmove(int board[8][8], int color, double maxtime, char str[1024],
/// int *playnow, int info, int moreinfo, CBmove *move)</c> and
/// <c>int enginecommand(char command[256], char reply[1024])</c>, both <c>__stdcall</c>.
/// </summary>
internal static class CheckerBoardApi
{
    public const int Free = 0;
    public const int White = 1;
    public const int Black = 2;
    public const int Man = 4;
    public const int King = 8;

    // getmove() return values, from the side to move's point of view.
    public const int ResultDraw = 0;
    public const int ResultWin = 1;
    public const int ResultLoss = 2;
    public const int ResultUnknown = 3;

    /// <summary>getmove() info flag: a new game; the engine resets its move history.</summary>
    public const int InfoResetMoves = 1;

    /// <summary>getmove() info flag: never exceed maxtime.</summary>
    public const int InfoExactTime = 2;

    public const int Cells = 64;

    public static int ColorOf(Side side) => side == Side.Black ? Black : White;

    /// <summary>Writes a position into <c>int board[8][8]</c>, laid out as <c>board[x][y]</c>.</summary>
    public static void WriteBoard(Position position, Span<int> board)
    {
        board[..Cells].Clear();
        for (var square = 0; square < Board.SquareCount; square++)
        {
            if (position.OwnerOf(square) is not { } owner)
            {
                continue;
            }

            var (x, y) = ToCoordinates(square);
            board[x * 8 + y] = ColorOf(owner) | (position.IsKing(square) ? King : Man);
        }
    }

    /// <summary>Reads <c>board[8][8]</c> back; <paramref name="sideToMove"/> is not stored in the array.</summary>
    public static Position ReadBoard(ReadOnlySpan<int> board, Side sideToMove)
    {
        uint black = 0, white = 0, kings = 0;
        for (var square = 0; square < Board.SquareCount; square++)
        {
            var (x, y) = ToCoordinates(square);
            var cell = board[x * 8 + y];
            var bit = Board.Bit(square);
            if ((cell & Black) != 0)
            {
                black |= bit;
            }
            else if ((cell & White) != 0)
            {
                white |= bit;
            }

            if ((cell & King) != 0)
            {
                kings |= bit;
            }
        }

        return new Position(black, white, kings, sideToMove);
    }

    /// <summary>numbertocoors() from cb_interface.h for English checkers (0-based square).</summary>
    public static (int X, int Y) ToCoordinates(int square)
    {
        var y = square / 4;
        var x = 2 * (3 - square % 4);
        if (y % 2 == 1)
        {
            x++;
        }

        return (x, y);
    }
}
