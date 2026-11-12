namespace PUTool.Models;

public sealed record PackageListEntry
{
    public required string PackageId { get; init; }
    public required string RequestedVersion { get; init; }
    public required string ResolvedVersion { get; init; }
    public required string TargetFramework { get; init; }
    public required string ManagementMode { get; init; }
    public required string LatestCompatibleVersion { get; init; }
    public required string LatestStableVersion { get; init; }
    public int UsageCount { get; init; }
    public int ProjectCount { get; init; }
    public string UsageCountLabel => $"{UsageCount} usage(s)";
    public string ProjectCountLabel => $"{ProjectCount} project(s)";
    public string SummaryDetails => $"TFM: {TargetFramework} · Latest stable: {LatestStableVersion} · {UsageCountLabel}";
}