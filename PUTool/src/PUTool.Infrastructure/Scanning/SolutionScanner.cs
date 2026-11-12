using System.Collections.Concurrent;
using PUTool.Core.Enums;
using PUTool.Core.Models;
using PUTool.Infrastructure.Models;

namespace PUTool.Infrastructure.Scanning;

public sealed class SolutionScanner(MsBuildProjectEvaluator evaluator, DotNetEnvironmentService environmentService)
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "artifacts", ".nuget-upgrade-backups"
    };

    public async Task<SolutionSnapshot> ScanAsync(
        string selectedPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            throw new ArgumentException("A solution or directory path was not provided.", nameof(selectedPath));

        var fullPath = Path.GetFullPath(selectedPath);
        var root = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Directory not found: {root}");

        var solutionFile = File.Exists(fullPath) &&
                           (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                            fullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            ? fullPath
            : Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        var projectFiles = IsProjectFile(fullPath)
            ? new List<string> { fullPath }
            : EnumerateProjects(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        if (projectFiles.Count == 0)
            throw new InvalidOperationException("No .csproj files were found in the selected directory.");

        var warnings = new ConcurrentBag<string>();
        var environment = await environmentService.ReadAsync(root, cancellationToken);

        if (!environment.IsAvailable)
            warnings.Add(environment.Error ?? "Could not detect an installed .NET SDK.");

        var projects = new ProjectInfo[projectFiles.Count];
        var completed = 0;
        var parallelism = Math.Max(1, Environment.ProcessorCount);
        using var msbuildSlots = new SemaphoreSlim(parallelism, parallelism);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, projectFiles.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken
            },
            async (projectIndex, token) =>
            {
                var projectPath = projectFiles[projectIndex];
                try
                {
                    projects[projectIndex] = await AnalyzeProjectAsync(projectPath, warnings, msbuildSlots, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    warnings.Add($"{Path.GetRelativePath(root, projectPath)}: {ex.Message}");
                    projects[projectIndex] = new ProjectInfo
                    {
                        Name = Path.GetFileNameWithoutExtension(projectPath),
                        FullPath = projectPath,
                        EvaluationError = ex.Message,
                        ManagementMode = PackageManagementMode.Unknown
                    };
                }
                finally
                {
                    var current = Interlocked.Increment(ref completed);
                    progress?.Report($"Parallel analysis ({parallelism} threads): {current}/{projectFiles.Count} — {Path.GetFileName(projectPath)}");
                }
            });

        return new()
        {
            RootPath = root,
            SolutionFile = solutionFile,
            Projects = projects.ToList(),
            Warnings = warnings.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            Environment = environment
        };
    }

    private async Task<ProjectInfo> AnalyzeProjectAsync(
        string projectPath,
        ConcurrentBag<string> warnings,
        SemaphoreSlim msbuildSlots,
        CancellationToken cancellationToken)
    {
        var initial = await EvaluateBoundedAsync(projectPath, null, msbuildSlots, cancellationToken);
        var frameworks = SplitFrameworks(Get(initial.Properties, "TargetFrameworks"));

        if (frameworks.Count == 0)
        {
            var single = Get(initial.Properties, "TargetFramework");

            if (!string.IsNullOrWhiteSpace(single))
                frameworks.Add(single);
        }

        if (frameworks.Count == 0)
            frameworks.Add("unknown");

        var centralEnabled = IsTrue(Get(initial.Properties, "ManagePackageVersionsCentrally"));
        var transitivePinning = IsTrue(Get(initial.Properties, "CentralPackageTransitivePinningEnabled"));

        var versionOverrideEnabled = !string.Equals(
            Get(initial.Properties, "CentralPackageVersionOverrideEnabled"),
            "false", StringComparison.OrdinalIgnoreCase
        );

        var packages = new List<PackageReferenceInfo>();
        var assets = ProjectAssetsReader.Read(projectPath, Get(initial.Properties, "ProjectAssetsFile"));

        var frameworkEvaluations = await Task.WhenAll(frameworks.Select(async framework =>
        {
            var evaluation = framework == "unknown"
                ? initial
                : await EvaluateBoundedAsync(projectPath, framework, msbuildSlots, cancellationToken);

            return (Framework: framework, Evaluation: evaluation);
        }));

        foreach (var frameworkEvaluation in frameworkEvaluations)
        {
            var framework = frameworkEvaluation.Framework;
            var evaluation = frameworkEvaluation.Evaluation;
            var centralVersions = evaluation.PackageVersions
                .GroupBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            var directIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var reference in evaluation.PackageReferences)
            {
                var id = reference.Identity.Trim();

                if (id.Length == 0)
                    continue;

                directIds.Add(id);

                var localVersion = FirstNonEmpty(reference.Get("Version"), reference.Get("VersionOverride"));
                var hasOverride = !string.IsNullOrWhiteSpace(reference.Get("VersionOverride"));

                EvaluatedItem? centralItem = null;

                var hasCore = centralEnabled && centralVersions.TryGetValue(id, out centralItem);
                var centralVersion = hasCore ? centralItem!.Get("Version") : null;
                var requested = hasOverride ? reference.Get("VersionOverride") :
                    !string.IsNullOrWhiteSpace(localVersion) ? localVersion : centralVersion;

                var mode = hasOverride
                    ? centralEnabled && versionOverrideEnabled ? PackageManagementMode.CentralOverride : PackageManagementMode.Unknown
                    : !string.IsNullOrWhiteSpace(localVersion) ? PackageManagementMode.Local :
                    hasCore ? PackageManagementMode.Central :
                    centralEnabled ? PackageManagementMode.Unknown : PackageManagementMode.Local;

                var source = mode switch
                {
                    PackageManagementMode.Central => ResolveDefiningFile(centralItem!, projectPath),
                    PackageManagementMode.CentralOverride or PackageManagementMode.Local => ResolveDefiningFile(reference, projectPath),
                    _ => null
                };

                var element = mode == PackageManagementMode.Central ? "PackageVersion" : "PackageReference";
                var resolved = LookupResolved(assets, id, framework);

                packages.Add(new()
                {
                    PackageId = id,
                    ProjectName = Path.GetFileName(projectPath),
                    ProjectPath = projectPath,
                    TargetFramework = framework,
                    RequestedVersion = CleanVersion(requested),
                    ResolvedVersion = resolved,
                    VersionSourceFile = source,
                    VersionElementName = element,
                    Condition = reference.Get("Condition"),
                    ManagementMode = mode
                });
            }

            foreach (var item in assets.Where(x =>
                string.Equals(x.Key.Framework, framework, StringComparison.OrdinalIgnoreCase) &&
                !directIds.Contains(x.Key.PackageId)))
            {
                EvaluatedItem? transitivePin = null;

                var isCentrallyPinned = centralEnabled && transitivePinning &&
                    centralVersions.TryGetValue(item.Key.PackageId, out transitivePin);

                packages.Add(new()
                {
                    PackageId = item.Key.PackageId,
                    ProjectName = Path.GetFileName(projectPath),
                    ProjectPath = projectPath,
                    TargetFramework = framework,
                    RequestedVersion = isCentrallyPinned ? CleanVersion(transitivePin!.Get("Version")) : null,
                    ResolvedVersion = item.Value,
                    VersionSourceFile = isCentrallyPinned ? ResolveDefiningFile(transitivePin!, projectPath) : null,
                    VersionElementName = isCentrallyPinned ? "PackageVersion" : "PackageReference",
                    ManagementMode = isCentrallyPinned ? PackageManagementMode.Central : PackageManagementMode.Transitive,
                    IsTransitive = true
                });
            }
        }

        var direct = packages.Where(x => !x.IsTransitive).ToList();
        var hasCentral = packages.Any(x => x.ManagementMode is PackageManagementMode.Central or PackageManagementMode.CentralOverride);
        var hasLocal = direct.Any(x => x.ManagementMode is PackageManagementMode.Local or PackageManagementMode.Unknown);
        var managementMode = hasCentral && hasLocal ? PackageManagementMode.Mixed :
            hasCentral && centralEnabled ? PackageManagementMode.Central :
            centralEnabled && hasLocal ? PackageManagementMode.Mixed :
            centralEnabled ? PackageManagementMode.Unknown : PackageManagementMode.Local;

        if (centralEnabled && !hasCentral && direct.Count > 0)
            warnings.Add($"{Path.GetFileName(projectPath)}: CPM is enabled, but no matching PackageVersion item was found for PackageReference. Check MSBuild imports and conditions.");

        if (!versionOverrideEnabled && direct.Any(x => x.ManagementMode == PackageManagementMode.Unknown))
            warnings.Add($"{Path.GetFileName(projectPath)}: unknown or disabled version overrides were detected. Check CentralPackageVersionOverrideEnabled.");

        var assetsFile = Get(initial.Properties, "ProjectAssetsFile");

        if (string.IsNullOrWhiteSpace(assetsFile))
            assetsFile = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
        else if (!Path.IsPathRooted(assetsFile))
            assetsFile = Path.GetFullPath(assetsFile, Path.GetDirectoryName(projectPath)!);

        if (!File.Exists(assetsFile))
            warnings.Add($"{Path.GetFileName(projectPath)}: project.assets.json was not found. Run Restore to display resolved and transitive package versions.");

        return new()
        {
            Name = Path.GetFileName(projectPath),
            FullPath = projectPath,
            TargetFrameworks = frameworks,
            UsesCentralPackageManagement = centralEnabled,
            CentralPackageTransitivePinningEnabled = transitivePinning,
            CentralPackageVersionOverrideEnabled = versionOverrideEnabled,
            ManagementMode = managementMode,
            Packages = packages,
            CentralPackageFiles = packages
                .Where(x => x.ManagementMode is PackageManagementMode.Central or PackageManagementMode.CentralOverride)
                .Select(x => x.VersionSourceFile)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private async Task<ProjectEvaluation> EvaluateBoundedAsync(
        string projectPath,
        string? targetFramework,
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken);

        try
        {
            return await evaluator.EvaluateAsync(projectPath, targetFramework, cancellationToken);
        }
        finally
        {
            slots.Release();
        }
    }

    private static IEnumerable<string> EnumerateProjects(string root)
    {
        var pending = new Stack<string>();

        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;

            try
            {
                directories = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(directory)))
                    pending.Push(directory);
            }

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(current, "*.csproj").ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    private static bool IsProjectFile(string path)
        => File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitFrameworks(string? value)
        => (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? Get(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    private static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private static string? CleanVersion(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveDefiningFile(EvaluatedItem item, string fallback)
    {
        var path = item.Get("DefiningProjectFullPath");

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return Path.GetFullPath(path);

        return File.Exists(fallback) ? Path.GetFullPath(fallback) : null;
    }

    private static string? LookupResolved(
        Dictionary<(string PackageId, string Framework), string> assets,
        string packageId,
        string framework)
    {
        if (assets.TryGetValue((packageId.ToLowerInvariant(), framework.ToLowerInvariant()), out var version))
            return version;

        return null;
    }
}