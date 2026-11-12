namespace PUTool.Infrastructure.Helpers;

internal sealed class TupleKeyComparer : IEqualityComparer<(string File, string Element, string Id, string Current)>
{
    public static readonly TupleKeyComparer Instance = new();

    public bool Equals((string File, string Element, string Id, string Current) x, (string File, string Element, string Id, string Current) y)
        => string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Element, y.Element, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Current, y.Current, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string File, string Element, string Id, string Current) obj)
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.File),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Element),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Current));
}