using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DefectScout.Core.Services;

internal sealed record TicketContext(string Text, long SourceBytes, bool WasCompacted);

internal static class TicketContextExtractor
{
    private const int DefaultMaxChars = 40000;
    private const int MaxFieldChars = 6000;
    private const int FallbackReadChars = 160000;

    private static readonly Regex s_spaceRx =
        new(@"\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> s_xmlFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "title",
        "summary",
        "description",
        "environment",
        "steps",
        "step",
        "expected",
        "actual",
        "reproduce",
        "reproduction",
        "comment",
        "comments",
        "component",
        "components",
        "module",
        "priority",
        "status",
        "resolution",
        "customfield",
        "customfieldname",
        "customfieldvalue",
        "value",
        "field",
    };

    private static readonly string[] s_fallbackKeywords =
    [
        "summary",
        "description",
        "steps",
        "repro",
        "expected",
        "actual",
        "environment",
        "module",
        "component",
        "error",
        "exception",
        "observed",
        "should",
        "instead",
    ];

    public static TicketContext Extract(string filePath, int maxChars = DefaultMaxChars)
    {
        var sourceBytes = new FileInfo(filePath).Length;
        var text = LooksLikeXml(filePath)
            ? TryExtractXml(filePath, maxChars)
            : null;

        text ??= ExtractTextFallback(filePath, maxChars);

        var compacted = sourceBytes > Encoding.UTF8.GetByteCount(text) || text.Length >= maxChars;
        return new TicketContext(text, sourceBytes, compacted);
    }

    private static bool LooksLikeXml(string filePath)
    {
        if (string.Equals(Path.GetExtension(filePath), ".xml", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            Span<char> buffer = stackalloc char[128];
            var read = reader.Read(buffer);
            return buffer[..read].ToString().TrimStart().StartsWith("<", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryExtractXml(string filePath, int maxChars)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersFromEntities = 1024,
        };

        var sb = new StringBuilder(Math.Min(maxChars, 8192));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var reader = XmlReader.Create(filePath, settings);
            while (reader.Read() && sb.Length < maxChars)
            {
                if (reader.NodeType != XmlNodeType.Element ||
                    !s_xmlFieldNames.Contains(reader.LocalName))
                {
                    continue;
                }

                var fieldName = BuildFieldName(reader);
                var value = TryReadElementText(reader);
                value = Clean(value);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                value = Limit(value, MaxFieldChars);
                var dedupeKey = Limit(value, 240);
                if (!seen.Add($"{fieldName}:{dedupeKey}"))
                    continue;

                AppendField(sb, fieldName, value, maxChars);
            }
        }
        catch
        {
            return null;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string BuildFieldName(XmlReader reader)
    {
        var label =
            reader.GetAttribute("name") ??
            reader.GetAttribute("key") ??
            reader.GetAttribute("id");

        return string.IsNullOrWhiteSpace(label)
            ? reader.LocalName
            : $"{reader.LocalName}:{label}";
    }

    private static string TryReadElementText(XmlReader reader)
    {
        try
        {
            return reader.ReadElementContentAsString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractTextFallback(string filePath, int maxChars)
    {
        var buffer = new char[FallbackReadChars];
        int read;
        using (var stream = File.OpenRead(filePath))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            read = reader.ReadBlock(buffer, 0, buffer.Length);
        }

        var source = new string(buffer, 0, read);
        var sb = new StringBuilder(Math.Min(maxChars, source.Length));
        var lineNumber = 0;

        foreach (var rawLine in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = Clean(rawLine);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (lineNumber <= 120 || s_fallbackKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                AppendField(sb, $"line {lineNumber}", Limit(line, MaxFieldChars), maxChars);

            if (sb.Length >= maxChars)
                break;
        }

        return sb.Length == 0 ? Limit(Clean(source), maxChars) : sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string fieldName, string value, int maxChars)
    {
        if (sb.Length >= maxChars)
            return;

        var remaining = maxChars - sb.Length;
        var entry = $"{fieldName}: {value}{Environment.NewLine}";
        sb.Append(Limit(entry, remaining));
    }

    private static string Clean(string value) =>
        s_spaceRx.Replace(value ?? string.Empty, " ").Trim();

    private static string Limit(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..Math.Max(0, maxChars)] + "\n...<truncated>";
}
