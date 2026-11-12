namespace PUTool.Core.Models;

public sealed record PackageDependencyInfo
{
    public required string PackageId { get; init; }
    public string VersionRange { get; init; } = "*";
    public string TargetFramework { get; init; } = string.Empty;
}
