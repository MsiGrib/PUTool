namespace PUTool.Infrastructure.Helpers
{
    internal sealed class StringTupleComparer : IEqualityComparer<(string PackageId, string VersionRange)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string PackageId, string VersionRange) x, (string PackageId, string VersionRange) y)
            => string.Equals(x.PackageId, y.PackageId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.VersionRange, y.VersionRange, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string PackageId, string VersionRange) obj)
            => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PackageId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.VersionRange));
    }
}