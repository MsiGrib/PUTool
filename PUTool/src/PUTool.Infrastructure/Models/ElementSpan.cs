namespace PUTool.Infrastructure.Models;

internal sealed record ElementSpan
{
    public string Name { get; init; } = string.Empty;
    public int StartTagStart { get; init; }
    public int StartTagEnd { get; init; }
    public int EndTagStart { get; set; } = -1;
    public int EndTagEnd { get; set; } = -1;
    public bool IsSelfClosing { get; set; }
    public ElementSpan? Parent { get; init; }
    public List<ElementSpan> Children { get; } = [];
    public List<AttributeSpan> Attributes { get; } = [];
}