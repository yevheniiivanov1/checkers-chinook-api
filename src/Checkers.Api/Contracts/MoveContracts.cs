namespace Checkers.Api.Contracts;

public sealed record SuggestMoveRequest
{
    /// <summary>Only <c>checkers-8x8</c> (English/American checkers) is supported.</summary>
    public string? GameId { get; init; }

    public BoardState? State { get; init; }

    /// <summary><c>weak</c>, <c>medium</c> or <c>strong</c>; without it the request's limits (or the configured defaults) apply as given.</summary>
    public string? Level { get; init; }

    public SearchLimitsRequest? Limits { get; init; }
}

public sealed record BoardState
{
    /// <summary>Only <c>PDN</c>.</summary>
    public string? Notation { get; init; }

    /// <summary>PDN FEN, e.g. <c>B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16</c>.</summary>
    public string? Position { get; init; }
}

public sealed record SearchLimitsRequest
{
    public int? MaxDepth { get; init; }

    public int? SoftTimeMs { get; init; }

    public int? HardTimeMs { get; init; }
}

/// <param name="Engine">Configured engine type: <c>chinook</c> (KingsRow + Chinook databases) or <c>builtin</c>.</param>
/// <param name="ScoreOrWDL">On a tablebase hit 1/0/-1 (win/draw/loss for the side to move); otherwise the search score, a man = 100.</param>
public sealed record SuggestMoveResponse(
    string Engine,
    string BestMove,
    IReadOnlyList<string> Pv,
    int ScoreOrWDL,
    int Depth,
    long Nodes,
    string PositionKey,
    SuggestMoveInfo Info);

public sealed record SuggestMoveInfo(bool TablebaseHit, int TimeMs)
{
    public bool Cached { get; init; }

    public string? Level { get; init; }

    public string? EngineName { get; init; }

    /// <summary>Game value for the side to move when the engine knows it.</summary>
    public int? Wdl { get; init; }

    public int Score { get; init; }

    public int EngineTimeMs { get; init; }

    public int? Worker { get; init; }
}

public sealed record ValidateMoveRequest
{
    public string? Position { get; init; }

    public string? Move { get; init; }
}

public sealed record ValidateMoveResponse(bool Legal)
{
    /// <summary>The matched move in canonical notation (full path for multi-jumps).</summary>
    public string? Move { get; init; }

    /// <summary>The position after the move, so a client can continue from it.</summary>
    public string? ResultPosition { get; init; }

    /// <summary>True when a short capture form (<c>22x6</c>) fits several legal routes; see <see cref="Candidates"/>.</summary>
    public bool? Ambiguous { get; init; }

    /// <summary>The full paths an ambiguous move could mean.</summary>
    public IReadOnlyList<string>? Candidates { get; init; }

    public string? Reason { get; init; }
}

public sealed record LegalMovesRequest
{
    public string? Position { get; init; }
}

public sealed record LegalMovesResponse(string Position, string SideToMove, bool GameOver, IReadOnlyList<LegalMoveDto> Moves);

/// <param name="Path">1-based squares from the starting square through every landing square.</param>
/// <param name="Captures">1-based squares of the captured pieces.</param>
public sealed record LegalMoveDto(string Move, IReadOnlyList<int> Path, IReadOnlyList<int> Captures, bool Crowns, string ResultPosition);

public sealed record HealthResponse(bool Ok, int Workers)
{
    public int ConfiguredWorkers { get; init; }

    public string? Engine { get; init; }

    public string? EngineName { get; init; }

    public int TablebasePieces { get; init; }

    public int CacheEntries { get; init; }
}
