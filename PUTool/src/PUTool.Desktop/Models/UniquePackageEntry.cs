namespace PUTool.Models;

public sealed class UniquePackageEntry
{
    public required string PackageId { get; init; }
    public int UsageCount { get; init; }
    public int ProjectCount { get; init; }
    public string Details => $"{UsageCount} usage(s) across {ProjectCount} project(s)";
}