using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PUTool.Core.Enums;
using PUTool.Core.Models;
using PUTool.Infrastructure.Execution;
using PUTool.Infrastructure.NuGet;
using PUTool.Infrastructure.Planning;
using PUTool.Infrastructure.Scanning;
using PUTool.Models;

namespace PUTool.Desktop.ViewModels;

public partial class ShellViewModel(
    SolutionScanner scanner,
    NuGetFlatContainerClient nuget,
    UpdatePlanner planner,
    UpdateExecutor executor,
    PlanExporter exporter
) : ObservableObject
{
    private SolutionSnapshot? _snapshot;

    public ObservableCollection<ProjectInfo> Projects { get; } = [];
    public ObservableCollection<PackageReferenceInfo> Packages { get; } = [];
    public ObservableCollection<PackageListEntry> PackageSummaries { get; } = [];
    public ObservableCollection<PackageProjectGroup> ProjectPackageGroups { get; } = [];
    public ObservableCollection<UniquePackageEntry> UniquePackageEntries { get; } = [];
    public ObservableCollection<PackageUpdate> Updates { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public IReadOnlyList<string> PackageViewModes { get; } =
    [
        "All usages",
        "Package catalog",
        "By project",
        "Unique package IDs"
    ];

    public Func<Task<string?>>? PickFolderAsync { get; set; }
    public Func<string, bool, Task<bool>>? ConfirmApplyAsync { get; set; }
    public Func<string?, Task<string?>>? SaveMarkdownPathAsync { get; set; }

    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private string _selectedTargetFramework = "net11.0";
    [ObservableProperty] private string _activeSection = "Overview";
    [ObservableProperty] private string _selectedPackageViewMode = "All usages";
    [ObservableProperty] private string _statusMessage = "Select a solution folder to start scanning.";
    [ObservableProperty] private string _activeSdk = "SDK not detected";
    [ObservableProperty] private string _solutionTitle = "No solution selected";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _createBackup = true;
    [ObservableProperty] private bool _hasSnapshot;
    [ObservableProperty] private string _lastOperationOutput = string.Empty;
    [ObservableProperty] private string _lastBackupPath = string.Empty;

    public bool IsOverview => ActiveSection == "Overview";
    public bool IsProjects => ActiveSection == "Projects";
    public bool IsPackages => ActiveSection == "Packages";
    public bool IsPlan => ActiveSection == "Plan";
    public bool IsSettings => ActiveSection == "Settings";
    public bool IsHelp => ActiveSection == "Help";
    public bool IsAllUsages => SelectedPackageViewMode == "All usages";
    public bool IsPackageCatalog => SelectedPackageViewMode == "Package catalog";
    public bool IsByProject => SelectedPackageViewMode == "By project";
    public bool IsUniqueIds => SelectedPackageViewMode == "Unique package IDs";
    public bool CanApply => !IsBusy && Updates.Any(x => x.CanApply);
    public bool CanScan => !IsBusy;
    public bool HasNoSnapshot => !HasSnapshot;
    public bool HasOperationOutput => !string.IsNullOrWhiteSpace(LastOperationOutput);
    public int ProjectCount => Projects.Count;
    public int UniquePackageCount => _snapshot?.UniquePackageCount ?? 0;
    public int PackageUsageCount => Packages.Count;
    public int DirectPackageCount => Packages.Count(x => !x.IsTransitive);
    public int TransitivePackageCount => Packages.Count(x => x.IsTransitive);
    public int UpdateCount => Updates.Count;
    public int ApplicableUpdateCount => Updates.Count(x => x.CanApply);
    public int CentralProjectCount => Projects.Count(x => x.ManagementMode == PackageManagementMode.Central);
    public int MixedProjectCount => Projects.Count(x => x.ManagementMode == PackageManagementMode.Mixed);
    public string SolutionPathDisplay => _snapshot?.SolutionFile ?? RootPath;

    public string EnvironmentDisplay => _snapshot?.Environment.IsAvailable == true
        ? $"SDK {_snapshot.Environment.ActiveSdk} · latest {_snapshot.Environment.LatestInstalledSdk ?? "n/a"}"
        : "SDK not found";

    public string SdkPolicyDisplay => _snapshot?.Environment.GlobalJsonSdkVersion is { Length: > 0 } pinned
        ? $"global.json pins {pinned}; rollForward: {_snapshot.Environment.GlobalJsonRollForward ?? "patch (default)"}"
        : "Latest installed SDK (standard .NET resolver policy)";

    public string RecommendationSubtitle => $"Stable package versions evaluated for {SelectedTargetFramework}";

    partial void OnActiveSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsProjects));
        OnPropertyChanged(nameof(IsPackages));
        OnPropertyChanged(nameof(IsPlan));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsHelp));
    }

    partial void OnSelectedPackageViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllUsages));
        OnPropertyChanged(nameof(IsPackageCatalog));
        OnPropertyChanged(nameof(IsByProject));
        OnPropertyChanged(nameof(IsUniqueIds));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanScan));

        ApplyPlanCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        FindUpdatesCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetFrameworkChanged(string value)
        => OnPropertyChanged(nameof(RecommendationSubtitle));

    partial void OnLastOperationOutputChanged(string value)
        => OnPropertyChanged(nameof(HasOperationOutput));

    partial void OnHasSnapshotChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoSnapshot));

        FindUpdatesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Navigate(string? section)
    {
        if (!string.IsNullOrWhiteSpace(section))
            ActiveSection = section;
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (PickFolderAsync is null)
        {
            StatusMessage = "Folder selection is unavailable.";

            return;
        }

        var selected = await PickFolderAsync();

        if (!string.IsNullOrWhiteSpace(selected))
        {
            RootPath = selected;
            await ScanAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            StatusMessage = "Select a folder or a .sln file.";

            return;
        }

        IsBusy = true;
        StatusMessage = "Preparing scan...";

        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);

            _snapshot = await scanner.ScanAsync(RootPath, progress);

            RootPath = _snapshot.RootPath;
            SolutionTitle = _snapshot.SolutionFile is null
                ? Path.GetFileName(_snapshot.RootPath)
                : Path.GetFileName(_snapshot.SolutionFile);
            ActiveSdk = _snapshot.Environment.ActiveSdk ?? "SDK not found";

            Replace(Projects, _snapshot.Projects);
            Replace(Packages, _snapshot.AllPackages.OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase));
            Replace(Warnings, _snapshot.Warnings);
            RefreshPackageViews();

            Updates.Clear();
            HasSnapshot = true;
            StatusMessage = $"Scan complete: {_snapshot.Projects.Count} projects, {_snapshot.UniquePackageCount} unique packages.";

            NotifyStats();
        }
        catch (Exception ex)
        {
            StatusMessage = "Scan failed.";
            LastOperationOutput = ex.ToString();
        }
        finally
        {
            IsBusy = false;

            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanScan));
        }
    }

    private bool CanStartScan()
        => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFindUpdates))]
    private async Task FindUpdatesAsync()
    {
        if (_snapshot is null)
            return;

        if (string.IsNullOrWhiteSpace(SelectedTargetFramework))
        {
            StatusMessage = "Enter a target TFM, for example net11.0.";

            return;
        }

        IsBusy = true;
        Updates.Clear();
        StatusMessage = $"Retrieving NuGet metadata for {SelectedTargetFramework}...";

        try
        {
            var packageGroups = _snapshot.UpdateCandidates
                .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var recommendations = new Dictionary<string, NuGetVersionRecommendation>(StringComparer.OrdinalIgnoreCase);
            var completed = 0;

            using var semaphore = new SemaphoreSlim(4, 4);

            var tasks = packageGroups.Select(async group =>
            {
                await semaphore.WaitAsync();

                try
                {
                    var recommendation = await nuget.GetRecommendationAsync(group.Key, SelectedTargetFramework);

                    lock (recommendations)
                        recommendations[group.Key] = recommendation;

                    var current = Interlocked.Increment(ref completed);

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"NuGet: {current}/{packageGroups.Count} — {group.Key}");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            foreach (var package in _snapshot.UpdateCandidates)
            {
                if (!recommendations.TryGetValue(package.PackageId, out var recommendation))
                    continue;

                package.LatestStableVersion = recommendation.LatestStable;
                package.LatestPrereleaseVersion = recommendation.LatestPrerelease;
                package.LatestCompatibleVersion = recommendation.LatestCompatible;
                package.Compatibility = recommendation.Compatibility;
                package.CompatibilityNote = recommendation.Note;
                package.Dependencies = recommendation.Dependencies;
            }

            Replace(Packages, _snapshot.AllPackages.OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase));
            RefreshPackageViews();

            var plan = planner.Build(_snapshot);

            Replace(Updates, plan);

            StatusMessage = $"Checked packages: {packageGroups.Count}. Proposed updates: {plan.Count}; applicable: {plan.Count(x => x.CanApply)}.";

            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(ApplicableUpdateCount));
            OnPropertyChanged(nameof(UpdateCount));
        }
        catch (Exception ex)
        {
            StatusMessage = "Unable to retrieve NuGet recommendations.";
            LastOperationOutput = ex.ToString();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanApply));
        }
    }

    private bool CanFindUpdates()
        => !IsBusy && HasSnapshot;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyPlanAsync()
    {
        if (_snapshot is null || !CanApply)
            return;

        var applicable = Updates.Where(x => x.CanApply).ToList();
        var backupDescription = CreateBackup
            ? "A backup will be created before files are changed."
            : "No on-disk backup will be created.";

        var prompt = $"{applicable.Count} package declarations in {applicable.Select(x => x.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count()} files will be changed. {backupDescription} Then dotnet restore and dotnet build will run. TargetFramework and global.json will remain unchanged. Continue?";
        
        if (ConfirmApplyAsync is null || !await ConfirmApplyAsync(prompt, CreateBackup))
            return;

        IsBusy = true;

        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);
            var result = await executor.ApplyAsync(_snapshot, applicable, SelectedTargetFramework, CreateBackup, progress);

            LastOperationOutput = result.Output;
            LastBackupPath = result.BackupDirectory ?? string.Empty;
            StatusMessage = result.Summary;

            if (result.RolledBack)
                StatusMessage += " Changes were rolled back.";

            if (result.FilesUpdated)
            {
                await ScanAsync();
                StatusMessage = result.Summary;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to apply updates.";
            LastOperationOutput = ex.ToString();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanApply));
        }
    }

    [RelayCommand]
    private async Task ExportPlanAsync()
    {
        if (_snapshot is null)
        {
            StatusMessage = "Scan a solution before exporting a plan.";

            return;
        }
        try
        {
            if (SaveMarkdownPathAsync is null)
            {
                StatusMessage = "The save dialog is unavailable.";

                return;
            }

            var path = await SaveMarkdownPathAsync("PUTool-Update-Plan.md");

            if (string.IsNullOrWhiteSpace(path))
            {
                StatusMessage = "Export cancelled.";

                return;
            }

            await exporter.ExportMarkdownAsync(_snapshot, Updates.ToList(), SelectedTargetFramework, path);

            StatusMessage = $"Plan saved: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = "Unable to save the plan.";
            LastOperationOutput = ex.ToString();
        }
    }

    private void RefreshPackageViews()
    {
        if (_snapshot is null)
            return;

        var all = _snapshot.AllPackages.ToList();
        var summaries = all
            .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => CreatePackageEntry(g, g.Select(x => x.ProjectName).Distinct(StringComparer.OrdinalIgnoreCase).Count()));

        Replace(PackageSummaries, summaries);

        var projectGroups = _snapshot.Projects
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(project => new PackageProjectGroup
            {
                ProjectName = project.Name,
                ProjectPath = project.FullPath,
                FrameworkDisplay = project.FrameworkDisplay,
                Packages = new ObservableCollection<PackageListEntry>(project.Packages
                    .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => CreatePackageEntry(g, 1)))
            });

        Replace(ProjectPackageGroups, projectGroups);

        var unique = all
            .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UniquePackageEntry
            {
                PackageId = g.Key,
                UsageCount = g.Count(),
                ProjectCount = g.Select(x => x.ProjectName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            });

        Replace(UniquePackageEntries, unique);
    }

    private static PackageListEntry CreatePackageEntry(IEnumerable<PackageReferenceInfo> source, int projectCount)
    {
        var items = source.ToList();

        return new()
        {
            PackageId = items[0].PackageId,
            RequestedVersion = JoinDistinct(items.Select(x => x.RequestedVersion)),
            ResolvedVersion = JoinDistinct(items.Select(x => x.ResolvedVersion)),
            TargetFramework = JoinDistinct(items.Select(x => x.TargetFramework)),
            ManagementMode = JoinDistinct(items.Select(x => x.DisplayMode)),
            LatestCompatibleVersion = JoinDistinct(items.Select(x => x.LatestCompatibleVersion)),
            LatestStableVersion = JoinDistinct(items.Select(x => x.LatestStableVersion)),
            UsageCount = items.Count,
            ProjectCount = projectCount
        };
    }

    private static string JoinDistinct(IEnumerable<string?> values)
    {
        var distinct = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count switch
        {
            0 => "Not specified",
            1 => distinct[0]!,
            _ => string.Join(", ", distinct)
        };
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();

        foreach (var item in items)
            target.Add(item);
    }

    private void NotifyStats()
    {
        OnPropertyChanged(nameof(ProjectCount));
        OnPropertyChanged(nameof(UniquePackageCount));
        OnPropertyChanged(nameof(PackageUsageCount));
        OnPropertyChanged(nameof(DirectPackageCount));
        OnPropertyChanged(nameof(TransitivePackageCount));
        OnPropertyChanged(nameof(CentralProjectCount));
        OnPropertyChanged(nameof(MixedProjectCount));
        OnPropertyChanged(nameof(SolutionPathDisplay));
        OnPropertyChanged(nameof(EnvironmentDisplay));
        OnPropertyChanged(nameof(SdkPolicyDisplay));
    }
}