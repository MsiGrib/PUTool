namespace PUTool.Core.Models;

public sealed record ApplyResult
{
    public bool FilesUpdated { get; init; }
    public bool RestoreSucceeded { get; init; }
    public bool BuildSucceeded { get; init; }
    public bool RolledBack { get; init; }
    public string? BackupDirectory { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}