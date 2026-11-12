using System.Text.Json;
using PUTool.Infrastructure.Models;
using PUTool.Infrastructure.Processes;

namespace PUTool.Infrastructure.Scanning;

public sealed class MsBuildProjectEvaluator(ProcessRunner processRunner)
{
    private const string PropertyList =
        "TargetFramework,TargetFrameworks,ManagePackageVersionsCentrally," +
        "CentralPackageTransitivePinningEnabled,CentralPackageVersionOverrideEnabled," +
        "MSBuildProjectExtensionsPath,ProjectAssetsFile";

    public async Task<ProjectEvaluation> EvaluateAsync(
        string projectPath,
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var args = new List<string> { "msbuild", fullPath, "-nologo" };

        if (!string.IsNullOrWhiteSpace(targetFramework))
            args.Add($"-property:TargetFramework={targetFramework}");

        args.Add($"-getProperty:{PropertyList}");
        args.Add("-getItem:PackageReference,PackageVersion");

        var result = await processRunner.RunAsync("dotnet", args, Path.GetDirectoryName(fullPath), cancellationToken);

        if (!result.Succeeded)
            throw new InvalidOperationException($"MSBuild could not evaluate project '{Path.GetFileName(fullPath)}'.{Environment.NewLine}{result.CombinedOutput}");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var properties = ReadStringDictionary(root, "Properties");
        var items = root.TryGetProperty("Items", out var itemsElement)
            ? itemsElement
            : default;

        return new()
        {
            Properties = properties,
            PackageReferences = ReadItems(items, "PackageReference"),
            PackageVersions = ReadItems(items, "PackageVersion")
        };
    }

    private static Dictionary<string, string> ReadStringDictionary(JsonElement root, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var item in values.EnumerateObject())
        {
            result[item.Name] = item.Value.ValueKind == JsonValueKind.String
                ? item.Value.GetString() ?? string.Empty
                : item.Value.ToString();
        }

        return result;
    }

    private static List<EvaluatedItem> ReadItems(JsonElement items, string itemName)
    {
        var result = new List<EvaluatedItem>();

        if (items.ValueKind != JsonValueKind.Object ||
            !items.TryGetProperty(itemName, out var collection) ||
            collection.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in collection.EnumerateArray())
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in item.EnumerateObject())
            {
                if (property.NameEquals("Identity"))
                    continue;

                metadata[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }

            var identity = item.TryGetProperty("Identity", out var id)
                ? id.GetString() ?? string.Empty
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(identity))
            {
                result.Add(new EvaluatedItem
                {
                    Identity = identity,
                    Metadata = metadata
                });
            }
        }

        return result;
    }
}