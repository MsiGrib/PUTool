namespace PUTool.Infrastructure.Models;

public sealed record EvaluatedItem
{
    public string Identity { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string name)
        => Metadata.TryGetValue(name, out var value) ? value : null;
}