using PUTool.Core.Enums;

namespace PUTool.Core.Models;

public sealed record PackageReferenceInfo
{
    public required string PackageId { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public required string TargetFramework { get; init; }
    public string? RequestedVersion { get; init; }
    public string? ResolvedVersion { get; init; }
    public string? LatestStableVersion { get; set; }
    public string? LatestPrereleaseVersion { get; set; }
    public string? LatestCompatibleVersion { get; set; }
    public string? VersionSourceFile { get; init; }
    public string VersionElementName { get; init; } = "PackageReference";
    public string? Condition { get; init; }
    public PackageManagementMode ManagementMode { get; init; }
    public CompatibilityStatus Compatibility { get; set; } = CompatibilityStatus.Unknown;
    public string? CompatibilityNote { get; set; }
    public List<PackageDependencyInfo> Dependencies { get; set; } = [];
    public bool IsTransitive { get; init; }

    public string DisplayVersion => ResolvedVersion ?? RequestedVersion ?? "not defined";

    public string DisplayMode => ManagementMode switch
    {
        PackageManagementMode.Central when IsTransitive => "Central transitive pin",
        PackageManagementMode.Central => "Central",
        PackageManagementMode.CentralOverride => "Override",
        PackageManagementMode.Mixed => "Mixed",
        PackageManagementMode.Transitive => "Transitive",
        PackageManagementMode.Local => "Local",
        _ => "Unknown"
    };
}