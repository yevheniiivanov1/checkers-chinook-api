using System.Diagnostics;
using Checkers.Core;
using Checkers.Core.Worker;
using Checkers.EngineHost;
using Checkers.EngineHost.Builtin;

namespace Checkers.Core.Tests;

public sealed class BuiltinEngineTests
{
    private static WorkerSearchResult Search(string pdn, WorkerSearchLimits limits, SearchControl? control = null)
    {
        using var engine = new BuiltinEngine();
        engine.Initialize(new WorkerInit("builtin", null, null, 0, 0));
        return engine.Search(Pdn.Parse(pdn), limits, control ?? new SearchControl());
    }

    [Fact]
    public void Returns_a_legal_move_with_a_consistent_pv()
    {
        var result = Search("B:W21-32:B1-12", new WorkerSearchLimits(0, 6, 1000, 5000, false));

        Assert.Single(MoveNotation.FindLegal(Position.Initial, result.BestMove));
        Assert.Equal(result.BestMove, result.Pv[0]);
        Assert.Equal(6, result.Depth);
        Assert.True(result.Nodes > 0);
        Assert.False(result.TablebaseHit);
    }

    [Fact]
    public void Reports_which_limit_ended_the_search()
    {
        Assert.Equal(WorkerStopReasons.Depth, Search("B:W21-32:B1-12", new WorkerSearchLimits(0, 3, 10_000, 20_000, false)).StopReason);
        Assert.Equal(WorkerStopReasons.HardTime, Search("B:W21-32:B1-12", new WorkerSearchLimits(64, 64, 10_000, 50, false)).StopReason);
        Assert.Null(Search("B:W18,22,25:B1,5,10,14", new WorkerSearchLimits(0, 64, 10_000, 20_000, false)).StopReason); // forced move
    }

    [Fact]
    public void Takes_a_free_piece() =>
        Assert.Equal("14x23", Search("B:W18,22,25:B1,5,10,14", new WorkerSearchLimits(0, 4, 1000, 5000, false)).BestMove);

    [Fact]
    public void Is_deterministic()
    {
        var limits = new WorkerSearchLimits(0, 5, 1000, 5000, false);
        const string Pdn = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16";

        Assert.Equal(Search(Pdn, limits).Pv, Search(Pdn, limits).Pv);
    }

    [Fact]
    public void A_stop_request_ends_the_search_with_a_legal_move()
    {
        var control = new SearchControl();
        control.RequestStop(1);
        control.Begin(1);
        var clock = Stopwatch.StartNew();

        var result = Search("B:W21-32:B1-12", new WorkerSearchLimits(0, 64, 60_000, 60_000, false), control);

        Assert.True(clock.ElapsedMilliseconds < 1000);
        Assert.Equal(WorkerStopReasons.Request, result.StopReason);
        Assert.Single(MoveNotation.FindLegal(Position.Initial, result.BestMove));
    }

    [Fact]
    public void A_stop_for_another_search_is_ignored()
    {
        var control = new SearchControl();
        control.RequestStop(1);
        control.Begin(2);

        Assert.False(control.IsStopRequested);
    }
}
