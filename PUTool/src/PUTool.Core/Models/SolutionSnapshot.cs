using PUTool.Core.Enums;

namespace PUTool.Core.Models;

public sealed record SolutionSnapshot
{
    public required string RootPath { get; init; }
    public string? SolutionFile { get; init; }
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
    public List<ProjectInfo> Projects { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public DotNetEnvironmentInfo Environment { get; init; } = new();
    public IEnumerable<PackageReferenceInfo> AllPackages => Projects.SelectMany(x => x.Packages);
    public IEnumerable<PackageReferenceInfo> DirectPackages => AllPackages.Where(x => !x.IsTransitive);

    public IEnumerable<PackageReferenceInfo> UpdateCandidates => AllPackages
        .Where(x => !x.IsTransitive || x.ManagementMode is PackageManagementMode.Central or PackageManagementMode.CentralOverride);

    public int UniquePackageCount => AllPackages.Select(x => x.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int PackageUsageCount => AllPackages.Count();
}