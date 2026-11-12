using System.Text.Json;
using PUTool.Core.Models;
using PUTool.Infrastructure.Processes;

namespace PUTool.Infrastructure.Scanning;

public sealed class DotNetEnvironmentService(ProcessRunner runner)
{
    public async Task<DotNetEnvironmentInfo> ReadAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            var sdkTask = runner.RunAsync("dotnet", ["--list-sdks"], workingDirectory, cancellationToken);
            var runtimeTask = runner.RunAsync("dotnet", ["--list-runtimes"], workingDirectory, cancellationToken);
            var activeTask = runner.RunAsync("dotnet", ["--version"], workingDirectory, cancellationToken);

            await Task.WhenAll(sdkTask, runtimeTask, activeTask);

            var sdks = (await sdkTask).StandardOutput
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var sdkVersions = sdks.Select(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => (Text: x!, Parsed: Version.TryParse(x, out var parsed) ? parsed : null))
                .Where(x => x.Parsed is not null)
                .OrderByDescending(x => x.Parsed)
                .ToList();

            var (globalJsonPath, pinnedVersion, rollForward) = ReadGlobalJson(workingDirectory);

            var runtimes = (await runtimeTask).StandardOutput
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var active = await activeTask;

            return new()
            {
                IsAvailable = active.Succeeded,
                ActiveSdk = active.Succeeded ? active.StandardOutput.Trim() : null,
                LatestInstalledSdk = sdkVersions.FirstOrDefault().Text,
                GlobalJsonPath = globalJsonPath,
                GlobalJsonSdkVersion = pinnedVersion,
                GlobalJsonRollForward = rollForward,
                InstalledSdks = sdks,
                InstalledRuntimes = runtimes,
                Error = active.Succeeded ? null : active.CombinedOutput
            };
        }
        catch (Exception ex)
        {
            return new()
            {
                IsAvailable = false,
                Error = ex.Message
            };
        }
    }
    private static (string? Path, string? Version, string? RollForward) ReadGlobalJson(string workingDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(workingDirectory));

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "global.json");

            if (File.Exists(candidate))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(candidate));

                    if (document.RootElement.TryGetProperty("sdk", out var sdk))
                    {
                        var version = sdk.TryGetProperty("version", out var v) ? v.GetString() : null;
                        var rollForward = sdk.TryGetProperty("rollForward", out var r) ? r.GetString() : null;

                        return (candidate, version, rollForward);
                    }

                    return (candidate, null, null);
                }
                catch
                {
                    return (candidate, null, null);
                }
            }

            current = current.Parent;
        }

        return (null, null, null);
    }
}