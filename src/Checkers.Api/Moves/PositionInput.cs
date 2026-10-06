using Checkers.Core;

namespace Checkers.Api.Moves;

internal static class PositionInput
{
    /// <summary>Parses a PDN position from a request field; any problem becomes a 422 listing all errors.</summary>
    public static Position Parse(string? pdn, string field)
    {
        if (string.IsNullOrWhiteSpace(pdn))
        {
            throw new RequestValidationException(field, "A PDN position is required, e.g. \"B:W21-32:B1-12\".");
        }

        return Pdn.TryParse(pdn, out var position, out var errors)
            ? position
            : throw new RequestValidationException(field, [.. errors]);
    }
}
