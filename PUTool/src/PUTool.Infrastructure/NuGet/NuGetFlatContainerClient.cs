using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Xml.Linq;
using NuGet.Frameworks;
using NuGet.Versioning;
using PUTool.Core.Enums;
using PUTool.Core.Models;
using PUTool.Infrastructure.Models;

namespace PUTool.Infrastructure.NuGet;

/// <summary>
/// Lightweight NuGet V3 client. It uses the public flat-container API and does not
/// execute package code. Private feeds and credentials are intentionally left for
/// the next integration step; the scanner itself still respects MSBuild/NuGet.Config.
/// </summary>
public sealed class NuGetFlatContainerClient(HttpClient httpClient)
{
    private const string BaseAddress = "https://api.nuget.org/v3-flatcontainer";
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<NuGetVersion>>> _versionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<PackageInspection>> _compatibilityCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<NuGetVersionRecommendation> GetRecommendationAsync(
        string packageId,
        string targetFramework,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var versions = await GetVersionsAsync(packageId, cancellationToken);

            if (versions.Count == 0)
                return new NuGetVersionRecommendation { PackageId = packageId, Note = "NuGet returned no published versions." };

            var stable = versions
                .Where(x => !x.IsPrerelease)
                .OrderByDescending(x => x)
                .ToList();

            var latestStable = stable.FirstOrDefault();
            var latestPreview = versions
                .Where(x => x.IsPrerelease)
                .OrderByDescending(x => x)
                .FirstOrDefault();

            if (latestStable is null)
            {
                return new()
                {
                    PackageId = packageId,
                    LatestPrerelease = latestPreview?.ToNormalizedString(),
                    Compatibility = CompatibilityStatus.Unknown,
                    Note = "No stable versions are available; only prerelease versions were found."
                };
            }
                

            NuGetFramework target;

            try
            {
                target = NuGetFramework.ParseFolder(targetFramework);
            }
            catch
            {
                target = NuGetFramework.UnsupportedFramework;
            }

            if (target.IsUnsupported)
            {
                return new()
                {
                    PackageId = packageId,
                    LatestStable = latestStable.ToNormalizedString(),
                    LatestPrerelease = latestPreview?.ToNormalizedString(),
                    Compatibility = CompatibilityStatus.Unknown,
                    Note = $"Could not parse the target TFM '{targetFramework}'."
                };
            }

            var unknownCandidate = false;
            foreach (var version in stable.Take(8))
            {
                var inspection = await InspectCompatibilityAsync(packageId, version, target, cancellationToken);

                if (inspection.Status == CompatibilityStatus.Compatible)
                {
                    return new()
                    {
                        PackageId = packageId,
                        LatestStable = latestStable.ToNormalizedString(),
                        LatestPrerelease = latestPreview?.ToNormalizedString(),
                        LatestCompatible = version.ToNormalizedString(),
                        Compatibility = inspection.Status,
                        Dependencies = inspection.Dependencies.ToList(),
                        Note = version.Equals(latestStable)
                            ? "Compatible framework assets were found in the package. Restore/Build verification is still required."
                            : $"Newer versions in the checked sample had no matching framework assets. Selected {version.ToNormalizedString()}."
                    };
                }

                if (inspection.Status == CompatibilityStatus.Unknown)
                    unknownCandidate = true;
            }

            return new()
            {
                PackageId = packageId,
                LatestStable = latestStable.ToNormalizedString(),
                LatestPrerelease = latestPreview?.ToNormalizedString(),
                LatestCompatible = null,
                Compatibility = unknownCandidate ? CompatibilityStatus.Unknown : CompatibilityStatus.Incompatible,
                Note = unknownCandidate
                    ? "Compatibility cannot be determined from package metadata alone. A verification Restore is required."
                    : "No compatible framework assets were found in the latest eight stable versions checked."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new()
            {
                PackageId = packageId,
                Compatibility = CompatibilityStatus.Unknown,
                Note = $"NuGet request failed: {ex.Message}"
            };
        }
    }

    private async Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        var task = _versionCache.GetOrAdd(
            packageId,
            id => FetchVersionsAsync(id, CancellationToken.None)
        );

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _versionCache.TryRemove(packageId, out _);

            throw;
        }
    }

    private async Task<IReadOnlyList<NuGetVersion>> FetchVersionsAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseAddress}/{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json";
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("versions", out var versionArray))
            return [];

        var versions = new List<NuGetVersion>();

        foreach (var value in versionArray.EnumerateArray())
        {
            var text = value.GetString();

            if (NuGetVersion.TryParse(text, out var version) && version is not null)
                versions.Add(version);
        }

        return versions;
    }

    private async Task<PackageInspection> InspectCompatibilityAsync(
        string packageId,
        NuGetVersion version,
        NuGetFramework target,
        CancellationToken cancellationToken)
    {
        var key = $"{packageId.ToLowerInvariant()}|{version.ToNormalizedString()}|{target.GetShortFolderName()}";
        var task = _compatibilityCache.GetOrAdd(
            key,
            _ => InspectCompatibilityCoreAsync(packageId, version, target, CancellationToken.None)
        );

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _compatibilityCache.TryRemove(key, out _);

            throw;
        }
    }

    private async Task<PackageInspection> InspectCompatibilityCoreAsync(
        string packageId,
        NuGetVersion version,
        NuGetFramework target,
        CancellationToken cancellationToken)
    {
        var normalizedId = packageId.ToLowerInvariant();
        var normalizedVersion = version.ToNormalizedString().ToLowerInvariant();
        var url = $"{BaseAddress}/{Uri.EscapeDataString(normalizedId)}/{Uri.EscapeDataString(normalizedVersion)}/{Uri.EscapeDataString(normalizedId)}.{Uri.EscapeDataString(normalizedVersion)}.nupkg";
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return new PackageInspection(CompatibilityStatus.Unknown, []);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();

        await stream.CopyToAsync(memory, cancellationToken);

        memory.Position = 0;

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        var assetFrameworks = new List<NuGetFramework>();

        foreach (var entry in archive.Entries)
        {
            var segments = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length < 2 ||
                !(segments[0].Equals("lib", StringComparison.OrdinalIgnoreCase) ||
                  segments[0].Equals("ref", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (segments[1].Equals("_._", StringComparison.OrdinalIgnoreCase))
                continue;

            var parsed = NuGetFramework.ParseFolder(segments[1]);

            if (!parsed.IsUnsupported && !assetFrameworks.Any(x => x.Equals(parsed)))
                assetFrameworks.Add(parsed);
        }

        if (assetFrameworks.Count == 0)
            return new PackageInspection(CompatibilityStatus.Unknown, []);

        var nearest = new FrameworkReducer().GetNearest(target, assetFrameworks);

        if (nearest is null)
            return new PackageInspection(CompatibilityStatus.Incompatible, []);

        var dependencies = await ReadDependenciesAsync(archive, target, cancellationToken);

        return new PackageInspection(CompatibilityStatus.Compatible, dependencies);
    }

    private static async Task<IReadOnlyList<PackageDependencyInfo>> ReadDependenciesAsync(
        ZipArchive archive,
        NuGetFramework target,
        CancellationToken cancellationToken)
    {
        var nuspec = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

        if (nuspec is null)
            return [];

        await using var nuspecStream = nuspec.Open();
        var xml = await XDocument.LoadAsync(nuspecStream, LoadOptions.None, cancellationToken);
        var dependenciesNode = xml
            .Descendants()
            .FirstOrDefault(x => x.Name.LocalName.Equals("dependencies", StringComparison.OrdinalIgnoreCase));

        if (dependenciesNode is null)
            return [];

        var selected = new List<XElement>();

        selected.AddRange(dependenciesNode.Elements()
            .Where(x => x.Name.LocalName.Equals("dependency", StringComparison.OrdinalIgnoreCase)));

        var dependencyGroups = dependenciesNode.Elements()
            .Where(x => x.Name.LocalName.Equals("group", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var universalGroup = dependencyGroups.FirstOrDefault(x => string.IsNullOrWhiteSpace((string?)x.Attribute("targetFramework")));

        if (universalGroup is not null)
            selected.AddRange(universalGroup.Elements()
                .Where(x => x.Name.LocalName.Equals("dependency", StringComparison.OrdinalIgnoreCase)));

        var groups = dependencyGroups
            .Select(x => new
            {
                Element = x,
                FrameworkName = (string?)x.Attribute("targetFramework")
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.FrameworkName))
            .Select(x =>
            {
                try
                {
                    return (x.Element, Framework: NuGetFramework.Parse(x.FrameworkName!));
                }
                catch
                {
                    return (x.Element, Framework: NuGetFramework.UnsupportedFramework);
                }
            })
            .Where(x => !x.Framework.IsUnsupported)
            .ToList();

        if (groups.Count > 0)
        {
            var nearest = new FrameworkReducer().GetNearest(target, groups.Select(x => x.Framework));

            if (nearest is not null)
            {
                var group = groups.FirstOrDefault(x => x.Framework.Equals(nearest));

                if (group.Element is not null)
                    selected.AddRange(group.Element.Elements()
                        .Where(x => x.Name.LocalName.Equals("dependency", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return selected
            .Select(x => new PackageDependencyInfo
            {
                PackageId = (string?)x.Attribute("id") ?? string.Empty,
                VersionRange = (string?)x.Attribute("version") ?? "*",
                TargetFramework = target.GetShortFolderName()
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.PackageId))
            .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }
}