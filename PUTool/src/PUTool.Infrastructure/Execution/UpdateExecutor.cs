using System.Text;
using PUTool.Core.Models;
using PUTool.Infrastructure.Models;
using PUTool.Infrastructure.Processes;

namespace PUTool.Infrastructure.Execution;

public sealed class UpdateExecutor(ProcessRunner runner)
{
    public async Task<ApplyResult> ApplyAsync(
        SolutionSnapshot snapshot,
        IReadOnlyCollection<PackageUpdate> updates,
        string targetFramework,
        bool createBackup = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var applicable = updates
            .Where(x => x.CanApply)
            .ToList();

        if (applicable.Count == 0)
            return new ApplyResult { Summary = "No changes are eligible for automatic application." };

        var files = applicable
            .Select(x => x.SourceFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var backupDirectory = createBackup
            ? Path.Combine(snapshot.RootPath, ".nuget-upgrade-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"))
            : null;

        if (backupDirectory is not null)
            Directory.CreateDirectory(backupDirectory);

        var originalContents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();

        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(file))
                    throw new FileNotFoundException("The file to update was not found.", file);

                var original = await File.ReadAllBytesAsync(file, cancellationToken);

                originalContents[file] = original;

                if (backupDirectory is not null)
                {
                    var relative = Path.GetRelativePath(snapshot.RootPath, file);
                    var parentPrefix = ".." + Path.DirectorySeparatorChar;
                    var alternateParentPrefix = ".." + Path.AltDirectorySeparatorChar;

                    if (relative == ".." || relative.StartsWith(parentPrefix, StringComparison.Ordinal) || relative.StartsWith(alternateParentPrefix, StringComparison.Ordinal))
                        relative = Path.Combine("_external", Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(file));

                    var backup = Path.Combine(backupDirectory, relative);

                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);

                    await File.WriteAllBytesAsync(backup, original, cancellationToken);
                }
            }

            var applied = 0;

            foreach (var fileGroup in applicable.GroupBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Patching {Path.GetRelativePath(snapshot.RootPath, fileGroup.Key)}");

                var patchedBytes = XmlTextVersionPatcher.Patch(originalContents[fileGroup.Key], fileGroup.ToList(), out var fileApplied);

                await File.WriteAllBytesAsync(fileGroup.Key, patchedBytes, cancellationToken);

                applied += fileApplied;
            }

            output.AppendLine($"Package declarations updated: {applied}.");
            output.AppendLine($"TargetFramework/TargetFrameworks were not modified. The selected TFM {targetFramework} was used only to select package versions.");
            output.AppendLine("All other XML file bytes were preserved without reformatting.");
            output.AppendLine(createBackup ? $"Backups: {backupDirectory}" : "Backups were disabled by the user.");
        }
        catch (Exception ex)
        {
            var rollbackFailures = RestoreOriginalContents(originalContents);
            var rollbackOutput = rollbackFailures.Count == 0
                ? "Original file bytes were restored successfully."
                : "Some files could not be restored: " + string.Join("; ", rollbackFailures);

            return new()
            {
                RolledBack = rollbackFailures.Count == 0,
                BackupDirectory = backupDirectory,
                Output = ex + Environment.NewLine + rollbackOutput,
                Summary = rollbackFailures.Count == 0
                    ? "Failed to apply changes. Original files were restored byte-for-byte from the pre-operation snapshot."
                    : "Failed to apply changes and fully restore the original files. Check the backup and operation output."
            };
        }

        var targets = snapshot.SolutionFile is not null
            ? [ snapshot.SolutionFile ]
            : snapshot.Projects.Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (targets.Length == 0)
        {
            return new()
            {
                FilesUpdated = true,
                BackupDirectory = backupDirectory,
                Output = output.ToString(),
                Summary = "Files were updated, but no projects were found for verification."
            };
        }

        foreach (var target in targets)
        {
            progress?.Report($"Running dotnet restore: {Path.GetFileName(target)}...");

            ProcessResult restore;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                restore = await runner.RunAsync("dotnet", ["restore", target], snapshot.RootPath, cancellationToken);
            }
            catch (Exception ex)
            {
                output.AppendLine($"Restore could not be started: {ex}");

                return new()
                {
                    FilesUpdated = true,
                    RestoreSucceeded = false,
                    RolledBack = false,
                    BackupDirectory = backupDirectory,
                    Output = output.ToString(),
                    Summary = "Changes were saved, but Restore could not be started. Check the installed SDK and operation output; the backup was retained."
                };
            }

            output.AppendLine("$ dotnet restore " + target);
            output.AppendLine(restore.CombinedOutput);

            if (!restore.Succeeded)
            {
                return new()
                {
                    FilesUpdated = true,
                    RestoreSucceeded = false,
                    RolledBack = false,
                    BackupDirectory = backupDirectory,
                    Output = output.ToString(),
                    Summary = "Changes were saved, but NuGet Restore failed. Resolve the reported conflicts or manually restore the files from the backup."
                };
            }
        }

        foreach (var target in targets)
        {
            progress?.Report($"Running dotnet build: {Path.GetFileName(target)}...");

            ProcessResult build;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                build = await runner.RunAsync("dotnet", ["build", target, "--no-restore"], snapshot.RootPath, cancellationToken);
            }
            catch (Exception ex)
            {
                output.AppendLine($"Build could not be completed for {target}: {ex}");

                return new()
                {
                    FilesUpdated = true,
                    RestoreSucceeded = true,
                    BuildSucceeded = false,
                    BackupDirectory = backupDirectory,
                    Output = output.ToString(),
                    Summary = "Restore succeeded, but Build could not complete. Changes were saved; the backup is available for a manual rollback."
                };
            }

            output.AppendLine("$ dotnet build " + target + " --no-restore");
            output.AppendLine(build.CombinedOutput);

            if (!build.Succeeded)
            {
                return new()
                {
                    FilesUpdated = true,
                    RestoreSucceeded = true,
                    BuildSucceeded = false,
                    BackupDirectory = backupDirectory,
                    Output = output.ToString(),
                    Summary = "Restore succeeded, but Build reported errors. Changes were saved; the backup is available for a manual rollback."
                };
            }
        }

        return new()
        {
            FilesUpdated = true,
            RestoreSucceeded = true,
            BuildSucceeded = true,
            BackupDirectory = backupDirectory,
            Output = output.ToString(),
            Summary = "Updates applied. Restore and Build succeeded for all verification targets."
        };
    }

    private static List<string> RestoreOriginalContents(Dictionary<string, byte[]> originals)
    {
        var failures = new List<string>();

        foreach (var (path, contents) in originals)
        {
            try
            {
                File.WriteAllBytes(path, contents);
            }
            catch (Exception ex)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        return failures;
    }
}