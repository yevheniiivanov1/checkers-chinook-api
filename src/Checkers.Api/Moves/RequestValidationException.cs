namespace Checkers.Api.Moves;

/// <summary>A well-formed request with semantically invalid content (bad PDN, unknown level, ...). Maps to 422.</summary>
public sealed class RequestValidationException(IDictionary<string, string[]> errors)
    : Exception(string.Join(" ", errors.SelectMany(e => e.Value)))
{
    public RequestValidationException(string field, params string[] messages)
        : this(new Dictionary<string, string[]> { [field] = messages })
    {
    }

    public IDictionary<string, string[]> Errors { get; } = errors;
}
