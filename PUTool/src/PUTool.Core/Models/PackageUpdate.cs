using PUTool.Core.Enums;

namespace PUTool.Core.Models;

public sealed record PackageUpdate
{
    public required string PackageId { get; init; }
    public required string SourceFile { get; init; }
    public required string ElementName { get; init; }
    public required string CurrentVersion { get; init; }
    public required string TargetVersion { get; init; }
    public List<string> Projects { get; init; } = [];
    public List<string> ProjectPaths { get; init; } = [];
    public CompatibilityStatus Compatibility { get; init; }
    public bool CanApply { get; set; }
    public int ExecutionOrder { get; set; }
    public bool HasDependencyCycle { get; set; }
    public List<string> DependencyConflicts { get; init; } = [];
    public string? BlockReason { get; set; }
    public string ManagementLabel => ElementName == "PackageVersion" ? "Central" : "Local";
    public string OrderLabel => HasDependencyCycle ? $"#{ExecutionOrder} · cycle" : $"#{ExecutionOrder}";
    public string ProjectCountLabel => $"Projects: {Projects.Count}";
    public string CanApplyLabel => CanApply ? "Applicable" : "Needs review";
}