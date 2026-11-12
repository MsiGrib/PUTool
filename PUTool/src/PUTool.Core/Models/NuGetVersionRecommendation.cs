using PUTool.Core.Enums;

namespace PUTool.Core.Models;

public sealed record NuGetVersionRecommendation
{
    public required string PackageId { get; init; }
    public string? LatestStable { get; init; }
    public string? LatestPrerelease { get; init; }
    public string? LatestCompatible { get; init; }
    public CompatibilityStatus Compatibility { get; init; }
    public string Note { get; init; } = string.Empty;
    public List<PackageDependencyInfo> Dependencies { get; init; } = [];
}