using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Checkers.Core;
using Checkers.Core.Worker;

namespace Checkers.EngineHost.KingsRow;

/// <summary>
/// Drives KingsRow (Ed Gilbert) through its native CheckerBoard-API DLL. KingsRow reads the
/// Chinook WLD endgame databases itself once pointed at them with <c>set dbpath</c>, so the
/// tablebase probe and the search are both a <c>getmove()</c> call; what differs is the budget.
/// </summary>
/// <remarks>
/// The API has no depth limit, so depth and time limits are enforced from outside: a monitor
/// thread watches the status line KingsRow keeps rewriting during the search and raises the
/// <c>playnow</c> flag, which makes KingsRow return its best move so far.
/// </remarks>
internal sealed unsafe partial class KingsRowEngine : IEngine
{
    private const int StatusBytes = 1024;
    private const int CommandBytes = 256;
    private const int ReplyBytes = 1024;

    // CBmove is ~270 bytes; KingsRow ignores it for English checkers, but it must be writable.
    private const int MoveBytes = 1024;

    // A tiny won database endgame. Searching it makes KingsRow open its endgame drivers during
    // start-up, which takes a few hundred milliseconds, instead of on the first real request.
    private const string WarmUpPosition = "W:WK14,K22:BK1";

    private nint _library;
    private delegate* unmanaged[Stdcall]<int*, int, double, byte*, int*, int, int, void*, int> _getMove;
    private delegate* unmanaged[Stdcall]<byte*, byte*, int> _engineCommand;
    private int* _board;
    private byte* _status;
    private int* _playNow;
    private void* _move;
    private int _tablebasePieces;

    public WorkerEngineInfo Initialize(WorkerInit init)
    {
        var dllPath = Path.GetFullPath(init.EnginePath ?? throw new InvalidOperationException("Engine:Path is not set."));
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"KingsRow engine DLL not found at '{dllPath}'.", dllPath);
        }

        Load(dllPath);

        // KingsRow keeps its settings in the registry (HKCU), so whatever CheckerBoard last set on
        // this machine would apply. Everything that could add randomness or change the status line
        // format is set explicitly.
        Command("set book 0");          // the opening book picks randomly among book moves
        Command("set dither 0");        // undocumented; by its name, noise added to vary play
        Command("set dither-offset 0");
        Command("set allscores 0");     // would replace the status line with a list of all moves
        Command("set solve 0");
        Command("set searchthreads 1"); // one thread per worker: reproducible, predictable CPU use
        Command($"set hashsize {init.HashMb}");
        Command($"set dbmbytes {init.DbCacheMb}"); // default is most of the RAM; there are several workers
        if (string.IsNullOrWhiteSpace(init.DatabasePath))
        {
            Command("set enable_wld 0");
        }
        else
        {
            var databasePath = Path.GetFullPath(init.DatabasePath);
            if (!Directory.Exists(databasePath))
            {
                throw new DirectoryNotFoundException($"Endgame database directory '{databasePath}' not found.");
            }

            // enginecommand() takes an 8-bit string; any other character would reach KingsRow as '?'
            // and the database would silently not load.
            if (!Ascii.IsValid(databasePath))
            {
                throw new InvalidOperationException($"Engine:Databases must be an ASCII path for KingsRow: '{databasePath}'.");
            }

            Command($"set dbpath {databasePath}");
            Command("set enable_wld 1");
        }

        // A second KingsRow instance on the machine calls itself "Kingsrow(x64)[1] 1.20".
        var name = InstanceNumber().Replace(Command("name"), string.Empty);
        Search(
            Pdn.Parse(WarmUpPosition),
            new WorkerSearchLimits(MinDepth: 0, MaxDepth: 64, SoftTimeMs: 20, HardTimeMs: 5_000, Tablebase: false),
            new SearchControl());

        var about = Command("about");
        _tablebasePieces = ParseTablebasePieces(about);
        if (_tablebasePieces == 0 && !string.IsNullOrWhiteSpace(init.DatabasePath))
        {
            // Asked for a database but running without one would quietly lose every tablebase answer.
            throw new InvalidOperationException(
                $"KingsRow loaded no endgame database from '{init.DatabasePath}' ({about.ReplaceLineEndings(" ").Trim()}). " +
                "Check Engine:Databases, or leave it empty to run without a database.");
        }

        var description = _tablebasePieces > 0 ? $"{name} + Chinook {_tablebasePieces}-piece WLD" : $"{name} (no endgame database)";
        WorkerHost.Log($"loaded {description}");
        return new WorkerEngineInfo(description, _tablebasePieces, about);
    }

    public WorkerSearchResult Search(Position position, WorkerSearchLimits limits, SearchControl control)
    {
        var legal = MoveGenerator.LegalMoves(position);
        if (legal.Count == 0)
        {
            return new WorkerSearchResult(string.Empty, [], Score: -10_000, Wdl: -1, 0, 0, false, 0, null);
        }

        CheckerBoardApi.WriteBoard(position, new Span<int>(_board, CheckerBoardApi.Cells));
        Command($"set gamehist {position.ToFen()}"); // no game history is known beyond this position
        NativeMemory.Clear(_status, StatusBytes);
        Volatile.Write(ref *_playNow, 0);

        var stopwatch = Stopwatch.StartNew();
        var monitor = new SearchMonitor(this, limits, control, stopwatch);
        var monitorThread = new Thread(monitor.Run) { Name = "kingsrow-monitor", IsBackground = true };
        monitorThread.Start();

        int outcome;
        try
        {
            outcome = _getMove(
                _board,
                CheckerBoardApi.ColorOf(position.SideToMove),
                limits.HardTimeMs / 1000.0,
                _status,
                _playNow,
                // "Reset moves": each request is a new game, so no move history carries over.
                CheckerBoardApi.InfoExactTime | CheckerBoardApi.InfoResetMoves,
                0,
                _move);
        }
        finally
        {
            stopwatch.Stop();
            monitor.Finish();
            monitorThread.Join();
        }

        var final = KingsRowStatus.Parse(ReadStatus());
        var iteration = final.IsCompletedIteration && final.Depth >= (monitor.LastCompleted.Depth ?? 0)
            ? final
            : monitor.LastCompleted;

        var after = CheckerBoardApi.ReadBoard(new ReadOnlySpan<int>(_board, CheckerBoardApi.Cells), position.SideToMove.Opponent());
        var played = legal.FirstOrDefault(move => MoveGenerator.Apply(position, move) == after);
        if (played is null)
        {
            WorkerHost.Log($"engine board {after.ToFen()} matches no legal move from {position.ToFen()}");
        }

        var blackScore = iteration.Value ?? final.Value ?? 0;
        var score = position.SideToMove == Side.Black ? blackScore : -blackScore;
        var wdl = GameValue(outcome, position.PieceCount <= _tablebasePieces, final, score);
        var elapsedMs = (int)stopwatch.ElapsedMilliseconds;
        var speed = final.KiloNodesPerSecond ?? monitor.LastSpeed ?? 0;

        return new WorkerSearchResult(
            BestMove: played?.Notation ?? string.Empty,
            Pv: PrincipalVariation.Normalize(position, iteration.Pv, played),
            Score: score,
            Wdl: wdl,
            Depth: iteration.Depth ?? final.Depth ?? 0,
            // KingsRow reports speed, not a node count: kN/s times milliseconds is nodes.
            Nodes: (long)speed * elapsedMs,
            TablebaseHit: limits.Tablebase && wdl is not null && position.PieceCount <= _tablebasePieces,
            TimeMs: elapsedMs,
            StopReason: monitor.StopReason);
    }

    /// <summary>
    /// Win/draw/loss for the side to move, or null when unknown. getmove()'s return value is the
    /// primary source, but for database draws KingsRow often returns "unknown" (it only claims a
    /// draw when it would offer one), so for positions inside the loaded database the status line
    /// decides: the draw-candidates format or a ±1 value is a database draw, and a decisive value
    /// is a database win or loss.
    /// </summary>
    internal static int? GameValue(int outcome, bool inDatabase, KingsRowStatus final, int score) => outcome switch
    {
        CheckerBoardApi.ResultWin => 1,
        CheckerBoardApi.ResultDraw => 0,
        CheckerBoardApi.ResultLoss => -1,
        _ when !inDatabase => null,
        _ when final.IsDatabaseDraw || (final.Value is not null && Math.Abs(score) <= 1) => 0,
        _ when Math.Abs(score) > KingsRowStatus.DecisiveValue => Math.Sign(score),
        _ => null,
    };

    public void Dispose()
    {
        NativeMemory.Free(_board);
        NativeMemory.Free(_status);
        NativeMemory.Free(_playNow);
        NativeMemory.Free(_move);
        _board = null;
        _status = null;
        _playNow = null;
        _move = null;
        // The DLL stays loaded: KingsRow has no shutdown entry point and the process is about to exit.
    }

    internal static int ParseTablebasePieces(string about)
    {
        var match = TablebaseLine().Match(about);
        return match.Success ? int.Parse(match.Groups["pieces"].Value) : 0;
    }

    private void Load(string dllPath)
    {
        var directory = Path.GetDirectoryName(dllPath)!;

        // KingsRow looks for its weights and book relative to the working directory, and its
        // endgame driver egdb64.dll sits one level up (the CheckerBoard install layout).
        Directory.SetCurrentDirectory(directory);
        var parent = Path.GetDirectoryName(directory);
        Environment.SetEnvironmentVariable("PATH", $"{directory};{parent};{Environment.GetEnvironmentVariable("PATH")}");

        TimerResolution.RequestOneMillisecond();
        _library = NativeLibrary.Load(dllPath);
        _getMove = (delegate* unmanaged[Stdcall]<int*, int, double, byte*, int*, int, int, void*, int>)NativeLibrary.GetExport(_library, "getmove");
        _engineCommand = (delegate* unmanaged[Stdcall]<byte*, byte*, int>)NativeLibrary.GetExport(_library, "enginecommand");

        _board = (int*)NativeMemory.AllocZeroed(CheckerBoardApi.Cells * sizeof(int));
        _status = (byte*)NativeMemory.AllocZeroed(StatusBytes);
        _playNow = (int*)NativeMemory.AllocZeroed(sizeof(int));
        _move = NativeMemory.AllocZeroed(MoveBytes);
    }

    private string Command(string command)
    {
        var commandBuffer = stackalloc byte[CommandBytes];
        var replyBuffer = stackalloc byte[ReplyBytes];
        new Span<byte>(commandBuffer, CommandBytes).Clear();
        new Span<byte>(replyBuffer, ReplyBytes).Clear();
        Encoding.ASCII.GetBytes(command.AsSpan(0, Math.Min(command.Length, CommandBytes - 1)), new Span<byte>(commandBuffer, CommandBytes));

        var handled = _engineCommand(commandBuffer, replyBuffer);
        var reply = ReadAnsi(replyBuffer, ReplyBytes);
        if (handled == 0)
        {
            WorkerHost.Log($"KingsRow did not handle '{command}': {reply}");
        }

        return reply;
    }

    private string ReadStatus() => ReadAnsi(_status, StatusBytes);

    private static string ReadAnsi(byte* buffer, int capacity)
    {
        var bytes = new ReadOnlySpan<byte>(buffer, capacity);
        var end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
    }

    [GeneratedRegex(@"(?<pieces>[0-9]{1,2})-piece WLD")]
    private static partial Regex TablebaseLine();

    [GeneratedRegex(@"\[[0-9]+\]")]
    private static partial Regex InstanceNumber();

    /// <summary>
    /// Watches the status line during one getmove() call and raises playnow when a limit is hit.
    /// </summary>
    private sealed class SearchMonitor(KingsRowEngine engine, WorkerSearchLimits limits, SearchControl control, Stopwatch stopwatch)
    {
        // Spin during the first milliseconds (the weak level is done in ~2 ms) and just before a
        // deadline; in between poll every millisecond (see TimerResolution).
        private const int SpinMilliseconds = 20;

        private volatile bool _finished;
        private int _observedDepth;
        private bool _sawDepth;

        public KingsRowStatus LastCompleted { get; private set; } = KingsRowStatus.Empty;

        public int? LastSpeed { get; private set; }

        /// <summary>Which limit made the monitor raise playnow; null if KingsRow returned by itself.</summary>
        public string? StopReason { get; private set; }

        public void Finish() => _finished = true;

        public void Run()
        {
            var lastLine = string.Empty;
            var spinner = new SpinWait();
            while (!_finished)
            {
                var line = engine.ReadStatus();
                if (line != lastLine)
                {
                    lastLine = line;
                    Observe(KingsRowStatus.Parse(line));
                }

                if (StopReason is null && StopFor() is { } reason)
                {
                    Volatile.Write(ref *engine._playNow, 1);
                    StopReason = reason;
                }

                var elapsed = stopwatch.ElapsedMilliseconds;
                var deadline = elapsed < limits.SoftTimeMs ? limits.SoftTimeMs : limits.HardTimeMs;
                if (elapsed < SpinMilliseconds || deadline - elapsed <= SpinMilliseconds)
                {
                    spinner.SpinOnce(sleep1Threshold: -1);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }

        private void Observe(KingsRowStatus status)
        {
            LastSpeed = status.KiloNodesPerSecond ?? LastSpeed;
            if (status.Depth is not { } depth)
            {
                return;
            }

            _sawDepth = true;
            _observedDepth = Math.Max(_observedDepth, depth);
            if (status.IsCompletedIteration && depth >= (LastCompleted.Depth ?? 0))
            {
                LastCompleted = status;
            }
        }

        private string? StopFor()
        {
            var elapsed = stopwatch.ElapsedMilliseconds;
            // A status line without depth information (e.g. a database draw) cannot prove the
            // minimum depth, so it must not hold the search until the hard limit.
            var minDepthDone = (LastCompleted.Depth ?? 0) >= limits.MinDepth || !_sawDepth;

            if (control.IsStopRequested)
            {
                return WorkerStopReasons.Request;
            }

            if (elapsed >= limits.HardTimeMs)
            {
                return WorkerStopReasons.HardTime;
            }

            // KingsRow deepens in steps of two; stop as soon as an iteration beyond the limit starts.
            if (_observedDepth > limits.MaxDepth && minDepthDone)
            {
                return WorkerStopReasons.Depth;
            }

            return elapsed >= limits.SoftTimeMs && minDepthDone ? WorkerStopReasons.SoftTime : null;
        }
    }
}
