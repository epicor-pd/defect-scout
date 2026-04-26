using System.Text.RegularExpressions;

namespace DefectScout.Core.Services;

internal static class JsonResponseParser
{
    public static string ExtractFirstObject(string raw)
    {
        var cleaned = Regex.Replace(raw.Trim(), @"^```[a-z]*\n?|```$", "", RegexOptions.Multiline).Trim();
        var start = cleaned.IndexOf('{');
        if (start < 0)
            return cleaned;

        var inString = false;
        var escaped = false;
        var depth = 0;

        for (var i = start; i < cleaned.Length; i++)
        {
            var c = cleaned[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c != '}')
                continue;

            depth--;
            if (depth == 0)
                return cleaned[start..(i + 1)];
        }

        return cleaned[start..];
    }
}
