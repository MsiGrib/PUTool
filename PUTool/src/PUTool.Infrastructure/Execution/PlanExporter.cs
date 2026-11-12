using System.Text;
using PUTool.Core.Models;

namespace PUTool.Infrastructure.Execution;

public sealed class PlanExporter
{
    public async Task<string> ExportMarkdownAsync(
        SolutionSnapshot snapshot,
        IReadOnlyCollection<PackageUpdate> updates,
        string targetFramework,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(snapshot.RootPath, "PUTool-Update-Plan.md")
            : Path.GetFullPath(outputPath);

        var text = new StringBuilder();

        text.AppendLine("# PUTool Update Plan");
        text.AppendLine();
        text.AppendLine($"- Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"- Root: `{snapshot.RootPath}`");
        text.AppendLine($"- Recommendation TFM: `{targetFramework}`");
        text.AppendLine($"- Active SDK: `{snapshot.Environment.ActiveSdk ?? "unknown"}`");
        text.AppendLine();
        text.AppendLine("## Project inventory");
        text.AppendLine();

        foreach (var project in snapshot.Projects)
            text.AppendLine($"- `{project.Name}` - `{string.Join("; ", project.TargetFrameworks)}` - {project.ManagementMode}");

        text.AppendLine();
        text.AppendLine("## Package changes");
        text.AppendLine();

        if (updates.Count == 0)
        {
            text.AppendLine("No package updates were proposed.");
        }
        else
        {
            text.AppendLine("| Order | Package | Current | Target | Source file | Apply | Note |");
            text.AppendLine("|---:|---|---:|---:|---|---|---|");

            foreach (var item in updates)
            {
                var source = string.IsNullOrWhiteSpace(item.SourceFile)
                    ? "(source not located)"
                    : Path.GetRelativePath(snapshot.RootPath, item.SourceFile).Replace("|", "\\|");

                var note = item.CanApply ? "Compatible metadata; Restore/Build still required" : item.BlockReason ?? "Manual review";

                if (item.HasDependencyCycle)
                    note += "; dependency cycle detected — ordering is advisory";

                text.AppendLine($"| {item.ExecutionOrder} | `{item.PackageId}` | `{item.CurrentVersion}` | `{item.TargetVersion}` | `{source}` | {(item.CanApply ? "Yes" : "No")} | {note.Replace("|", "\\|")} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Validation commands");
        text.AppendLine();

        var targets = snapshot.SolutionFile is not null
            ? [ snapshot.SolutionFile ]
            : snapshot.Projects
                .Select(x => x.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (targets.Length > 0)
        {
            text.AppendLine("```bash");

            foreach (var target in targets)
            {
                var relative = Path.GetRelativePath(snapshot.RootPath, target);

                text.AppendLine($"dotnet restore \"{relative}\"");
                text.AppendLine($"dotnet build \"{relative}\" --no-restore");
            }

            text.AppendLine("```");
        }

        text.AppendLine();
        text.AppendLine("> The displayed order is a topological order based on dependencies from the selected package releases where metadata is available. Updates are applied as a batch; NuGet Restore performs the final dependency resolution.");
        text.AppendLine("> Compatibility is a metadata-based recommendation. A successful NuGet Restore and project Build are the final validation steps.");
        text.AppendLine("> The plan does not change TargetFramework values automatically. Update those in the authoritative MSBuild property file and rescan before applying package updates.");

        await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false), cancellationToken);

        return path;
    }
}