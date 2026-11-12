using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PUTool.Core.Models;
using PUTool.Infrastructure.Models;

namespace PUTool.Infrastructure.Execution;

/// <summary>
/// Replaces only the characters that represent a package version in an MSBuild XML
/// file. Unlike XDocument.Save(), this intentionally preserves the rest of the file
/// byte-for-byte: XML declaration presence, indentation, spaces, quote style,
/// attribute order, comments, line endings and encoding/BOM.
/// </summary>
internal static class XmlTextVersionPatcher
{
    private static readonly Regex AttributeRegex = new(
        "(?<name>[^\\s=/>]+)\\s*=\\s*(?<quote>['\\\"])(?<value>.*?)\\k<quote>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static byte[] Patch(
        byte[] originalBytes,
        IReadOnlyCollection<PackageUpdate> updates,
        out int appliedCount)
    {
        var (encoding, preambleLength) = DetectEncoding(originalBytes);
        var text = encoding.GetString(originalBytes, preambleLength, originalBytes.Length - preambleLength);

        _ = System.Xml.Linq.XDocument.Parse(text, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var roots = ParseElements(text);
        var replacements = new List<TextReplacement>(updates.Count);

        foreach (var update in updates)
        {
            var matches = roots
                .Where(element => element.Name.Equals(update.ElementName, StringComparison.OrdinalIgnoreCase))
                .Where(element => string.Equals(
                    GetAttribute(element, "Include") ?? GetAttribute(element, "Update"),
                    update.PackageId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count != 1)
                throw new InvalidOperationException($"For {update.PackageId} expected exactly one element {update.ElementName}, found: {matches.Count}.");

            var element = matches[0];

            if (HasCondition(element))
                throw new InvalidOperationException($"Element {update.ElementName} for {update.PackageId} contains a Condition. Automatic replacement is blocked.");

            var versionAttribute = FindVersionAttribute(element, update.CurrentVersion);

            if (versionAttribute is not null)
            {
                var oldValue = text.Substring(versionAttribute.ValueStart, versionAttribute.ValueLength);
                var leading = oldValue.Length - oldValue.TrimStart().Length;
                var trailing = oldValue.Length - oldValue.TrimEnd().Length;
                var replacement = oldValue[..leading] + EscapeAttribute(update.TargetVersion, versionAttribute.Quote) +
                                  (trailing == 0 ? string.Empty : oldValue[^trailing..]);
                replacements.Add(new(versionAttribute.ValueStart, versionAttribute.ValueLength, replacement));

                continue;
            }

            var versionChild = element.Children.FirstOrDefault(x => x.Name.Equals("Version", StringComparison.OrdinalIgnoreCase));

            if (versionChild is null || versionChild.EndTagStart < 0)
                throw new InvalidOperationException($"Could not find a literal Version value for {update.PackageId}.");

            var valueStart = versionChild.StartTagEnd;
            var valueLength = versionChild.EndTagStart - valueStart;
            var childText = text.Substring(valueStart, valueLength);

            if (childText.Contains('<') || !string.Equals(WebUtility.HtmlDecode(childText).Trim(), update.CurrentVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The Version content for {update.PackageId} changed or contains nested markup.");

            var childLeading = childText.Length - childText.TrimStart().Length;
            var childTrailing = childText.Length - childText.TrimEnd().Length;
            var childReplacement = childText[..childLeading] + EscapeText(update.TargetVersion) + (childTrailing == 0 ? string.Empty : childText[^childTrailing..]);

            replacements.Add(new(valueStart, valueLength, childReplacement));
        }

        var ordered = replacements.OrderByDescending(x => x.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i - 1].Start < ordered[i].Start + ordered[i].Length)
                throw new InvalidOperationException("Overlapping XML edits were detected.");
        }

        var builder = new StringBuilder(text);
        foreach (var replacement in ordered)
        {
            builder.Remove(replacement.Start, replacement.Length);
            builder.Insert(replacement.Start, replacement.Value);
        }

        var body = encoding.GetBytes(builder.ToString());
        var result = new byte[preambleLength + body.Length];

        if (preambleLength > 0)
            Buffer.BlockCopy(originalBytes, 0, result, 0, preambleLength);

        Buffer.BlockCopy(body, 0, result, preambleLength, body.Length);
        appliedCount = replacements.Count;

        return result;
    }

    private static List<ElementSpan> ParseElements(string text)
    {
        var roots = new List<ElementSpan>();
        var stack = new List<ElementSpan>();
        var position = 0;

        while (position < text.Length)
        {
            var start = text.IndexOf('<', position);

            if (start < 0)
                break;

            if (StartsWith(text, start, "<!--"))
            {
                position = SkipThrough(text, start + 4, "-->");

                continue;
            }
            if (StartsWith(text, start, "<![CDATA["))
            {
                position = SkipThrough(text, start + 9, "]]>");

                continue;
            }
            if (StartsWith(text, start, "<?"))
            {
                position = SkipThrough(text, start + 2, "?>");

                continue;
            }
            if (StartsWith(text, start, "<!"))
            {
                position = FindTagEnd(text, start) + 1;

                continue;
            }

            var end = FindTagEnd(text, start);

            if (end < 0)
                throw new InvalidDataException("XML contains an unclosed tag.");

            var isEndTag = start + 1 < text.Length && text[start + 1] == '/';
            var nameStart = start + (isEndTag ? 2 : 1);

            while (nameStart < end && char.IsWhiteSpace(text[nameStart]))
                nameStart++;

            var nameEnd = nameStart;

            while (nameEnd < end && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('/' or '>'))
                nameEnd++;

            if (nameEnd == nameStart)
            {
                position = end + 1;

                continue;
            }

            var name = text[nameStart..nameEnd];

            if (isEndTag)
            {
                for (var stackIndex = stack.Count - 1; stackIndex >= 0; stackIndex--)
                {
                    if (!stack[stackIndex].Name.Equals(name, StringComparison.Ordinal))
                        continue;

                    stack[stackIndex].EndTagStart = start;
                    stack[stackIndex].EndTagEnd = end + 1;
                    stack.RemoveRange(stackIndex, stack.Count - stackIndex);

                    break;
                }
            }
            else
            {
                var tag = new ElementSpan
                {
                    Name = name,
                    StartTagStart = start,
                    StartTagEnd = end + 1,
                    Parent = stack.Count == 0 ? null : stack[^1]
                };

                ParseAttributes(text, nameEnd, end, tag);

                if (tag.Parent is null)
                    roots.Add(tag);
                else
                    tag.Parent.Children.Add(tag);

                var beforeClose = end - 1;

                while (beforeClose > start && char.IsWhiteSpace(text[beforeClose]))
                    beforeClose--;

                tag.IsSelfClosing = text[beforeClose] == '/';

                if (!tag.IsSelfClosing)
                {
                    stack.Add(tag);
                }
                else
                {
                    tag.EndTagStart = end;
                    tag.EndTagEnd = end + 1;
                }
            }

            position = end + 1;
        }

        return Flatten(roots).ToList();
    }

    private static IEnumerable<ElementSpan> Flatten(IEnumerable<ElementSpan> elements)
    {
        foreach (var element in elements)
        {
            yield return element;

            foreach (var child in Flatten(element.Children))
                yield return child;
        }
    }

    private static void ParseAttributes(string text, int nameEnd, int tagEnd, ElementSpan element)
    {
        var segmentStart = nameEnd;
        var segment = text[segmentStart..tagEnd];

        foreach (Match match in AttributeRegex.Matches(segment))
        {
            var quoteGroup = match.Groups["quote"];
            var valueGroup = match.Groups["value"];

            element.Attributes.Add(new()
            {
                Name = match.Groups["name"].Value,
                Value = WebUtility.HtmlDecode(valueGroup.Value),
                ValueStart = segmentStart + valueGroup.Index,
                ValueLength = valueGroup.Length,
                Quote = quoteGroup.Value[0]
            });
        }
    }

    private static AttributeSpan? FindVersionAttribute(ElementSpan element, string currentVersion)
    {
        foreach (var name in new[] { "VersionOverride", "Version" })
        {
            var attribute = element.Attributes.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (attribute is not null && string.Equals(attribute.Value.Trim(), currentVersion, StringComparison.OrdinalIgnoreCase))
                return attribute;
        }

        return null;
    }

    private static string? GetAttribute(ElementSpan element, string name)
        => element.Attributes.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool HasCondition(ElementSpan element)
    {
        for (ElementSpan? current = element; current is not null; current = current.Parent)
        {
            if (current.Attributes.Any(x => x.Name.Equals("Condition", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static int FindTagEnd(string text, int start)
    {
        char quote = '\0';

        for (var i = start + 1; i < text.Length; i++)
        {
            var ch = text[i];

            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';

                continue;
            }

            if (ch is '\'' or '"')
                quote = ch;
            else if (ch == '>')
                return i;
        }

        return -1;
    }

    private static int SkipThrough(string text, int start, string terminator)
    {
        var end = text.IndexOf(terminator, start, StringComparison.Ordinal);

        return end < 0 ? text.Length : end + terminator.Length;
    }

    private static bool StartsWith(string text, int start, string value)
        => start + value.Length <= text.Length && text.AsSpan(start, value.Length).SequenceEqual(value.AsSpan());

    private static string EscapeAttribute(string value, char quote)
    {
        var escaped = value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        return quote == '"'
            ? escaped.Replace("\"", "&quot;", StringComparison.Ordinal)
            : escaped.Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static string EscapeText(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(true, false, true), 4);

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (new UTF32Encoding(false, false, true), 4);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false, true), 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, false, true), 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, false, true), 2);

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == (byte)'<')
            return (new UTF32Encoding(true, false, true), 0);

        if (bytes.Length >= 4 && bytes[0] == (byte)'<' && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (new UTF32Encoding(false, false, true), 0);

        if (bytes.Length >= 2 && bytes[0] == 0x00 && bytes[1] == (byte)'<')
            return (new UnicodeEncoding(true, false, true), 0);

        if (bytes.Length >= 2 && bytes[0] == (byte)'<' && bytes[1] == 0x00)
            return (new UnicodeEncoding(false, false, true), 0);

        var prefixLength = Math.Min(bytes.Length, 256);
        var asciiPrefix = Encoding.ASCII.GetString(bytes, 0, prefixLength);

        var declaration = Regex.Match(
            asciiPrefix,
            "<\\?xml\\b[^>]*?encoding\\s*=\\s*['\"](?<name>[^'\"]+)['\"]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline
        );

        if (declaration.Success)
        {
            var encodingName = declaration.Groups["name"].Value;

            if (encodingName.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                return (new UTF8Encoding(false, true), 0);

            try
            {
                return (Encoding.GetEncoding(
                    encodingName,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback), 0
                );
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"XML encoding '{encodingName}' is not supported by the safe editor. The file was not changed.", ex);
            }
        }

        return (new UTF8Encoding(false, true), 0);
    }
}