namespace Checkers.Core;

/// <summary>
/// A legal move: a step, or a capture with every landing square of a multi-jump.
/// Squares are 0-based internally; <see cref="Notation"/> is the 1-based PDN form.
/// </summary>
public sealed class Move
{
    public Move(int from, IReadOnlyList<int> landings, uint captured, bool crowns)
    {
        if (landings.Count == 0)
        {
            throw new ArgumentException("A move needs at least one landing square.", nameof(landings));
        }

        From = from;
        Landings = landings;
        Captured = captured;
        Crowns = crowns;
        Notation = BuildNotation();
    }

    public int From { get; }

    /// <summary>Squares the piece lands on, in order; the last one is <see cref="To"/>.</summary>
    public IReadOnlyList<int> Landings { get; }

    public int To => Landings[^1];

    /// <summary>Bitmask of the captured pieces.</summary>
    public uint Captured { get; }

    public bool IsCapture => Captured != 0;

    /// <summary>True when a man reaches the far rank; in English checkers that ends the move.</summary>
    public bool Crowns { get; }

    /// <summary>
    /// PDN notation: <c>22-18</c> for a step, <c>22x15</c> for a single jump and the full path
    /// <c>22x15x6</c> for a multi-jump, so the notation always identifies exactly one capture route.
    /// </summary>
    public string Notation { get; }

    public override string ToString() => Notation;

    private string BuildNotation()
    {
        if (!IsCapture)
        {
            return $"{From + 1}-{To + 1}";
        }

        return string.Join('x', Landings.Select(s => s + 1).Prepend(From + 1));
    }
}
