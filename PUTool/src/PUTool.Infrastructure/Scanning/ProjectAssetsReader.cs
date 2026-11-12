using System.Text.Json;
using NuGet.Frameworks;

namespace PUTool.Infrastructure.Scanning;

internal static class ProjectAssetsReader
{
    public static Dictionary<(string PackageId, string Framework), string> Read(string projectPath, string? configuredAssetsPath = null)
    {
        var result = new Dictionary<(string, string), string>();
        var assetsPath = string.IsNullOrWhiteSpace(configuredAssetsPath)
            ? Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json")
            : Path.IsPathRooted(configuredAssetsPath)
                ? configuredAssetsPath
                : Path.GetFullPath(configuredAssetsPath, Path.GetDirectoryName(projectPath)!);

        if (!File.Exists(assetsPath))
            return result;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            var root = document.RootElement;

            if (!root.TryGetProperty("targets", out var targets))
                return result;

            foreach (var target in targets.EnumerateObject())
            {
                var framework = NormalizeFramework(target.Name);

                foreach (var library in target.Value.EnumerateObject())
                {
                    var slash = library.Name.LastIndexOf('/');

                    if (slash <= 0 || slash == library.Name.Length - 1)
                        continue;

                    var id = library.Name[..slash];
                    var version = library.Name[(slash + 1)..];
                    var isPackage = true;

                    if (root.TryGetProperty("libraries", out var libraries) &&
                        libraries.TryGetProperty(library.Name, out var libraryInfo) &&
                        libraryInfo.TryGetProperty("type", out var type))
                    {
                        isPackage = string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase);
                    }

                    if (isPackage)
                        result[(id.ToLowerInvariant(), framework)] = version;
                }
            }
        }
        catch (JsonException) { /* Ignored */ }
        catch (IOException) { /* Ignored */ }

        return result;
    }

    public static string NormalizeFramework(string value)
    {
        try
        {
            var frameworkName = value.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            var framework = NuGetFramework.Parse(frameworkName);

            return framework.IsUnsupported
                ? frameworkName.Trim().ToLowerInvariant()
                : framework.GetShortFolderName().ToLowerInvariant();
        }
        catch
        {
            return value.Split('/', StringSplitOptions.RemoveEmptyEntries)[0].Trim().ToLowerInvariant();
        }
    }
}