using PUTool.Core.Enums;

namespace PUTool.Core.Models;

public sealed record ProjectInfo
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public List<string> TargetFrameworks { get; init; } = [];
    public bool UsesCentralPackageManagement { get; init; }
    public bool CentralPackageTransitivePinningEnabled { get; init; }
    public bool CentralPackageVersionOverrideEnabled { get; init; } = true;
    public PackageManagementMode ManagementMode { get; set; }
    public List<PackageReferenceInfo> Packages { get; init; } = [];
    public List<string> CentralPackageFiles { get; init; } = [];
    public string? EvaluationError { get; init; }
    public int PackageCount => Packages.Count;
    public string PackageCountLabel => $"Packages: {PackageCount}";
    public string FrameworkDisplay => TargetFrameworks.Count == 0 ? "TFM not defined" : string.Join(" · ", TargetFrameworks);
}