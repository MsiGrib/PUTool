namespace PUTool.Infrastructure.Models;

internal sealed record AttributeSpan
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public int ValueStart { get; init; }
    public int ValueLength { get; init; }
    public char Quote { get; init; }
}