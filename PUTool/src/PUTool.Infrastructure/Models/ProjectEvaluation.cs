namespace PUTool.Infrastructure.Models;

public sealed class ProjectEvaluation
{
    public Dictionary<string, string> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<EvaluatedItem> PackageReferences { get; init; } = [];
    public List<EvaluatedItem> PackageVersions { get; init; } = [];
}