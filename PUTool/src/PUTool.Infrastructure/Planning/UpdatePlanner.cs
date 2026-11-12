using System.Xml.Linq;
using NuGet.Versioning;
using PUTool.Core.Enums;
using PUTool.Core.Models;

namespace PUTool.Infrastructure.Planning;

public sealed class UpdatePlanner
{
    public List<PackageUpdate> Build(SolutionSnapshot snapshot)
    {
        var direct = snapshot.UpdateCandidates
            .Where(x => !string.IsNullOrWhiteSpace(x.LatestStableVersion))
            .ToList();

        var candidates = direct.GroupBy(x => (
            File: x.VersionSourceFile ?? "",
            Element: x.VersionElementName,
            Id: x.PackageId,
            Current: x.RequestedVersion ?? x.ResolvedVersion ?? ""), TupleKeyComparer.Instance);

        var updates = new List<PackageUpdate>();

        foreach (var group in candidates)
        {
            var usages = group.ToList();
            var versions = usages.Select(x => x.LatestCompatibleVersion)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var recommended = versions.FirstOrDefault() ??
                usages
                    .Select(x => x.LatestStableVersion)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (string.IsNullOrWhiteSpace(recommended))
                continue;

            var current = group.Key.Current;

            if (string.Equals(current, recommended, StringComparison.OrdinalIgnoreCase))
                continue;

            var allCompatible = usages.All(x => x.Compatibility == CompatibilityStatus.Compatible);
            var sourceExists = !string.IsNullOrWhiteSpace(group.Key.File) && File.Exists(group.Key.File);
            var noVersionExpression = !current.Contains("$", StringComparison.Ordinal) &&
                                      !current.Contains("@(", StringComparison.Ordinal) &&
                                      !current.Contains("%(", StringComparison.Ordinal) &&
                                      current != "(not specified)";
            var targetIsLiteral = !recommended.Contains("$", StringComparison.Ordinal) &&
                                  !recommended.Contains("@(", StringComparison.Ordinal) &&
                                  !recommended.Contains("%(", StringComparison.Ordinal);
            var sourceIsLiteral = sourceExists && noVersionExpression &&
                                  HasUniqueUnconditionalLiteralDeclaration(group.Key.File, group.Key.Element, group.Key.Id, current);
            var canApply = allCompatible && sourceIsLiteral && targetIsLiteral && versions.Count <= 1;

            var reason = !sourceExists ? "Could not determine the file where the version is declared."
                : !noVersionExpression ? "The current version is computed through an MSBuild property/item expression or is not explicitly declared. Manual editing is required."
                : !sourceIsLiteral ? "The source declaration could not be matched unambiguously. A Condition or multiple matching items may be involved. Automatic editing is blocked."
                : !targetIsLiteral ? "The recommended version is not a literal value."
                : versions.Count > 1 ? "Different recommendations were returned for the same declaration across TFMs."
                : usages.Any(x => x.Compatibility == CompatibilityStatus.Incompatible)
                    ? "No matching framework assets were found for the selected TFM in the checked version sample."
                : !allCompatible ? "Compatibility has not been confirmed. The update is shown, but automatic application is blocked."
                : null;

            updates.Add(new()
            {
                PackageId = group.Key.Id,
                SourceFile = group.Key.File,
                ElementName = group.Key.Element,
                CurrentVersion = string.IsNullOrWhiteSpace(current) ? "(not specified)" : current,
                TargetVersion = recommended,
                Projects = usages.Select(x => x.ProjectName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ProjectPaths = usages.Select(x => x.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Compatibility = allCompatible
                    ? CompatibilityStatus.Compatible
                    : usages.Any(x => x.Compatibility == CompatibilityStatus.Incompatible)
                        ? CompatibilityStatus.Incompatible
                        : CompatibilityStatus.Unknown,
                CanApply = canApply,
                BlockReason = reason
            });
        }

        CheckDependencyVersionRanges(snapshot, updates);

        return OrderByDependencies(snapshot, updates);
    }

    private static void CheckDependencyVersionRanges(
        SolutionSnapshot snapshot,
        List<PackageUpdate> updates)
    {
        foreach (var consumerUpdate in updates)
        {
            var usages = snapshot.UpdateCandidates
                .Where(x =>
                    string.Equals(x.PackageId, consumerUpdate.PackageId, StringComparison.OrdinalIgnoreCase) &&
                        consumerUpdate.ProjectPaths.Contains(x.ProjectPath, StringComparer.OrdinalIgnoreCase));

            var dependencies = usages.SelectMany(x => x.Dependencies)
                .GroupBy(x => (x.PackageId, x.VersionRange), StringTupleComparer.Instance)
                .Select(x => x.First());

            foreach (var dependency in dependencies)
            {
                var dependencyUpdates = updates.Where(x =>
                    string.Equals(x.PackageId, dependency.PackageId, StringComparison.OrdinalIgnoreCase) &&
                    x.ProjectPaths.Intersect(consumerUpdate.ProjectPaths, StringComparer.OrdinalIgnoreCase).Any());

                foreach (var dependencyUpdate in dependencyUpdates)
                {
                    if (ReferenceEquals(dependencyUpdate, consumerUpdate))
                        continue;

                    if (!VersionRange.TryParse(dependency.VersionRange, out var range) ||
                        !NuGetVersion.TryParse(dependencyUpdate.TargetVersion, out var targetVersion) ||
                        range is null || targetVersion is null)
                    {
                        consumerUpdate.DependencyConflicts.Add($"Could not verify the dependency requirement {dependency.PackageId} {dependency.VersionRange}.");

                        continue;
                    }

                    if (!range.Satisfies(targetVersion))
                    {
                        consumerUpdate.DependencyConflicts.Add(
                            $"The new version of {consumerUpdate.PackageId} requires {dependency.PackageId} {dependency.VersionRange}, " +
                            $"but the plan proposes {dependencyUpdate.TargetVersion}.");
                    }
                }
            }

            if (consumerUpdate.DependencyConflicts.Count > 0)
            {
                consumerUpdate.CanApply = false;

                var conflictText = string.Join(" ", consumerUpdate.DependencyConflicts.Distinct(StringComparer.OrdinalIgnoreCase));

                consumerUpdate.BlockReason = string.IsNullOrWhiteSpace(consumerUpdate.BlockReason)
                    ? conflictText
                    : consumerUpdate.BlockReason + " " + conflictText;
            }
        }
    }

    private static List<PackageUpdate> OrderByDependencies(SolutionSnapshot snapshot, List<PackageUpdate> updates)
    {
        var successors = updates.ToDictionary(x => x, _ => new HashSet<PackageUpdate>());
        var indegree = updates.ToDictionary(x => x, _ => 0);
        var updatesById = updates
            .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var packageUpdate in updates)
        {
            var usages = snapshot.UpdateCandidates
                .Where(x => string.Equals(x.PackageId, packageUpdate.PackageId, StringComparison.OrdinalIgnoreCase)
                    && packageUpdate.ProjectPaths.Contains(x.ProjectPath, StringComparer.OrdinalIgnoreCase));

            foreach (var dependencyId in usages
                .SelectMany(x => x.Dependencies)
                .Select(x => x.PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!updatesById.TryGetValue(dependencyId, out var dependencyUpdates))
                    continue;

                foreach (var dependencyUpdate in dependencyUpdates)
                {
                    if (ReferenceEquals(dependencyUpdate, packageUpdate))
                        continue;

                    if (!dependencyUpdate.ProjectPaths.Intersect(packageUpdate.ProjectPaths, StringComparer.OrdinalIgnoreCase).Any())
                        continue;

                    if (successors[dependencyUpdate].Add(packageUpdate))
                        indegree[packageUpdate]++;
                }
            }
        }

        static int Compare(PackageUpdate left, PackageUpdate right)
        {
            var result = StringComparer.OrdinalIgnoreCase.Compare(left.PackageId, right.PackageId);

            if (result != 0)
                return result;

            result = StringComparer.OrdinalIgnoreCase.Compare(left.SourceFile, right.SourceFile);

            if (result != 0)
                return result;

            result = StringComparer.OrdinalIgnoreCase.Compare(left.CurrentVersion, right.CurrentVersion);

            if (result != 0)
                return result;

            return StringComparer.OrdinalIgnoreCase.Compare(left.TargetVersion, right.TargetVersion);
        }

        var ready = new SortedSet<PackageUpdate>(Comparer<PackageUpdate>.Create(Compare));

        foreach (var item in updates.Where(x => indegree[x] == 0))
            ready.Add(item);

        var ordered = new List<PackageUpdate>(updates.Count);

        while (ready.Count > 0)
        {
            var next = ready.Min!;

            ready.Remove(next);
            ordered.Add(next);

            foreach (var dependent in successors[next])
            {
                indegree[dependent]--;

                if (indegree[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        var cycleItems = updates
            .Except(ordered)
            .OrderBy(x => x, Comparer<PackageUpdate>.Create(Compare))
            .ToList();

        ordered.AddRange(cycleItems);

        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].ExecutionOrder = index + 1;
            ordered[index].HasDependencyCycle = cycleItems.Contains(ordered[index]);
        }

        return ordered;
    }

    private static bool HasUniqueUnconditionalLiteralDeclaration(string file, string elementName, string packageId, string currentVersion)
    {
        try
        {
            var document = XDocument.Load(file, LoadOptions.PreserveWhitespace);
            var declarations = document.Descendants()
                .Where(element => element.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase))
                .Where(element => string.Equals(
                    (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"),
                    packageId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (declarations.Count != 1)
                return false;

            var declaration = declarations[0];
            var raw = (string?)declaration.Attribute("VersionOverride") ?? (string?)declaration.Attribute("Version");

            if (string.IsNullOrWhiteSpace(raw))
                raw = declaration.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase))?.Value;

            var hasCondition = declaration.AncestorsAndSelf()
                .Any(x => x.Attribute("Condition") is not null);

            return !hasCondition &&
                string.Equals(raw?.Trim(), currentVersion, StringComparison.OrdinalIgnoreCase) &&
                raw is not null &&
                !raw.Contains("$", StringComparison.Ordinal) &&
                !raw.Contains("@(", StringComparison.Ordinal) &&
                !raw.Contains("%(", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}