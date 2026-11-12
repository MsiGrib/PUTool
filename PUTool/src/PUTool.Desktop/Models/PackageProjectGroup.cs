using System.Collections.ObjectModel;

namespace PUTool.Models;

public sealed class PackageProjectGroup
{
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public required string FrameworkDisplay { get; init; }
    public ObservableCollection<PackageListEntry> Packages { get; init; } = [];
    public string PackageCountLabel => $"{Packages.Count} unique package(s)";
}