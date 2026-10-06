using System.Diagnostics;
using Checkers.Core;
using Checkers.Core.Worker;

namespace Checkers.EngineHost.Builtin;

/// <summary>
/// A small deterministic alpha-beta engine used when KingsRow is not installed (CI, a reviewer's
/// machine). It exercises the whole worker pipeline but is far weaker than KingsRow and has no
/// endgame database, so it never reports a tablebase hit. The API labels its answers "builtin".
/// </summary>
internal sealed class BuiltinEngine : IEngine
{
    public WorkerEngineInfo Initialize(WorkerInit init) =>
        new("builtin alpha-beta (no endgame database)", 0, "Material and advancement evaluation; captures searched past the horizon.");

    public WorkerSearchResult Search(Position position, WorkerSearchLimits limits, SearchControl control) =>
        new Searcher(limits, control).Run(position);

    public void Dispose()
    {
    }

    private sealed class Searcher(WorkerSearchLimits limits, SearchControl control)
    {
        private const int Win = 10_000;
        private const int MaxPly = 64;
        private const int ManValue = 100;
        private const int KingValue = 150;

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Move?[,] _pv = new Move?[MaxPly + 1, MaxPly + 1];
        private readonly int[] _pvLength = new int[MaxPly + 1];
        private long _nodes;
        private bool _aborted;
        private string? _stopReason;
        private int _completedDepth;

        public WorkerSearchResult Run(Position root)
        {
            var legal = MoveGenerator.LegalMoves(root);
            if (legal.Count == 0)
            {
                return new WorkerSearchResult(string.Empty, [], -Win, -1, 0, 0, false, 0, null);
            }

            IReadOnlyList<Move> bestLine = [legal[0]];
            var bestScore = 0;
            for (var depth = 1; depth <= limits.MaxDepth; depth++)
            {
                var score = Negamax(root, depth, 0, -Win - 1, Win + 1, bestLine[0]);
                if (_aborted)
                {
                    break;
                }

                _completedDepth = depth;
                bestScore = score;
                bestLine = Enumerable.Range(0, _pvLength[0]).Select(i => _pv[0, i]!).ToList();

                var decided = Math.Abs(score) > Win - MaxPly;
                if (depth >= limits.MinDepth && (decided || legal.Count == 1))
                {
                    break;
                }

                if (depth >= limits.MinDepth && _stopwatch.ElapsedMilliseconds >= limits.SoftTimeMs)
                {
                    _stopReason = WorkerStopReasons.SoftTime;
                    break;
                }

                if (depth == limits.MaxDepth)
                {
                    _stopReason = WorkerStopReasons.Depth;
                }
            }

            int? wdl = Math.Abs(bestScore) > Win - MaxPly ? Math.Sign(bestScore) : null;
            return new WorkerSearchResult(
                bestLine[0].Notation,
                bestLine.Select(m => m.Notation).ToList(),
                bestScore,
                wdl,
                _completedDepth,
                _nodes,
                TablebaseHit: false,
                (int)_stopwatch.ElapsedMilliseconds,
                _stopReason);
        }

        private int Negamax(Position position, int depth, int ply, int alpha, int beta, Move? first)
        {
            if ((++_nodes & 1023) == 0)
            {
                CheckLimits();
            }

            if (_aborted)
            {
                return 0;
            }

            _pvLength[ply] = ply;
            var moves = MoveGenerator.LegalMoves(position);
            if (moves.Count == 0)
            {
                return -(Win - ply);
            }

            // Captures are compulsory, so a position with one pending is not quiet: keep searching
            // captures past the nominal depth instead of evaluating mid-exchange.
            if ((depth <= 0 && !moves[0].IsCapture) || ply >= MaxPly)
            {
                return Evaluate(position);
            }

            foreach (var move in Ordered(moves, first))
            {
                var score = -Negamax(MoveGenerator.Apply(position, move), depth - 1, ply + 1, -beta, -alpha, null);
                if (_aborted)
                {
                    return 0;
                }

                if (score > alpha)
                {
                    alpha = score;
                    _pv[ply, ply] = move;
                    for (var i = ply + 1; i < _pvLength[ply + 1]; i++)
                    {
                        _pv[ply, i] = _pv[ply + 1, i];
                    }

                    _pvLength[ply] = Math.Max(_pvLength[ply + 1], ply + 1);
                }

                if (alpha >= beta)
                {
                    break;
                }
            }

            return alpha;
        }

        private static IEnumerable<Move> Ordered(IReadOnlyList<Move> moves, Move? first)
        {
            var preferred = first is null ? null : moves.FirstOrDefault(m => m.Notation == first.Notation);
            if (preferred is not null)
            {
                yield return preferred;
            }

            foreach (var move in moves)
            {
                if (!ReferenceEquals(move, preferred))
                {
                    yield return move;
                }
            }
        }

        private void CheckLimits()
        {
            var elapsed = _stopwatch.ElapsedMilliseconds;
            _stopReason = control.IsStopRequested ? WorkerStopReasons.Request
                : elapsed >= limits.HardTimeMs ? WorkerStopReasons.HardTime
                : elapsed >= limits.SoftTimeMs && _completedDepth >= limits.MinDepth ? WorkerStopReasons.SoftTime
                : null;
            _aborted = _stopReason is not null;
        }

        /// <summary>Material, plus a little for advanced men and for men guarding the back rank.</summary>
        private static int Evaluate(Position position)
        {
            var score = 0;
            for (var square = 0; square < Board.SquareCount; square++)
            {
                if (position.OwnerOf(square) is not { } owner)
                {
                    continue;
                }

                var row = square / 4;
                int value;
                if (position.IsKing(square))
                {
                    value = KingValue;
                }
                else
                {
                    var advanced = owner == Side.Black ? row : 7 - row;
                    value = ManValue + 3 * advanced + (advanced == 0 ? 5 : 0);
                }

                score += owner == Side.Black ? value : -value;
            }

            return position.SideToMove == Side.Black ? score : -score;
        }
    }
}
