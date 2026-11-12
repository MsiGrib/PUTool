namespace PUTool.Core.Models;

public sealed record DotNetEnvironmentInfo
{
    public string? ActiveSdk { get; init; }
    public string? LatestInstalledSdk { get; init; }
    public string? GlobalJsonPath { get; init; }
    public string? GlobalJsonSdkVersion { get; init; }
    public string? GlobalJsonRollForward { get; init; }
    public List<string> InstalledSdks { get; init; } = [];
    public List<string> InstalledRuntimes { get; init; } = [];
    public bool IsAvailable { get; init; }
    public string? Error { get; init; }
}