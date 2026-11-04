using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// These lexical models are compiled into both the native renderer and the document editor.
// Source coordinates always refer to UTF-16 in the unmodified page, including its line endings.
namespace UnoUML.Syntax;

internal readonly record struct SourceToken(int Start, int Length)
{
    internal int End => Start + Length;
}

internal sealed record SourceRow(int Index, int Start, int End, int Next, string Code)
{
    internal string Trimmed => Code.Trim();
    internal int ContentStart => Start + Code.Length - Code.TrimStart().Length;
}

internal static class StructuredText
{
    internal const string NumericPattern = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
    internal static readonly Regex NumberPattern = new("^" + NumericPattern + "$", RegexOptions.CultureInvariant);
    internal static readonly Regex QuotedPattern = new("^\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);

    internal static List<SourceRow>? Rows(string source)
    {
        if (source.Length > 4_000_000) return null;
        var rows = new List<SourceRow>();
        bool block = false, style = false;
        int skinDepth = 0;
        for (int start = 0, index = 0; start < source.Length; index++)
        {
            int end = start;
            while (end < source.Length && source[end] is not ('\r' or '\n')) end++;
            int next = end;
            if (next < source.Length && source[next] == '\r') next++;
            if (next < source.Length && source[next] == '\n') next++;
            if (end - start > 32768) return null;
            char[] code = source[start..end].ToCharArray();
            bool quoted = false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (start == 0 && i == 0 && c == '\uFEFF') { code[i] = ' '; continue; }
                if (block)
                {
                    code[i] = ' ';
                    if (c == '\'' && i + 1 < code.Length && code[i + 1] == '/') { code[++i] = ' '; block = false; }
                }
                else if (quoted && c == '\\' && i + 1 < code.Length) i++;
                else if (c == '"') quoted = !quoted;
                else if (!quoted && c == '/' && i + 1 < code.Length && code[i + 1] == '\'') { code[i] = code[++i] = ' '; block = true; }
                else if (!quoted && c == '\'' && (i == 0 || char.IsWhiteSpace(code[i - 1])))
                { Array.Fill(code, ' ', i, code.Length - i); break; }
            }
            string text = new(code), trim = text.Trim();
            if (style)
            {
                if (trim.Equals("</style>", StringComparison.OrdinalIgnoreCase)) style = false;
                text = new string(' ', text.Length);
            }
            else if (skinDepth > 0)
            {
                skinDepth += trim.Count(c => c == '{') - trim.Count(c => c == '}');
                if (skinDepth < 0) return null;
                text = new string(' ', text.Length);
            }
            else if (trim.Equals("<style>", StringComparison.OrdinalIgnoreCase)) { style = true; text = new string(' ', text.Length); }
            else if (trim.StartsWith("skinparam ", StringComparison.OrdinalIgnoreCase))
            {
                skinDepth = trim.Count(c => c == '{') - trim.Count(c => c == '}');
                if (skinDepth < 0) return null;
                text = new string(' ', text.Length);
            }
            rows.Add(new(index, start, end, next, text));
            start = next;
        }
        return block || style || skinDepth != 0 ? null : rows;
    }

    internal static bool Number(string text, out double value) =>
        NumberPattern.IsMatch(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && Math.Abs(value) <= 1e12 || Fail(out value);
    private static bool Fail(out double value) { value = 0; return false; }
    internal static bool Integer(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    internal static bool Plain(string text, int max = 1024) => text.Length <= max && !text.Any(c => char.IsControl(c)) && !text.Contains('<') && !text.Contains('>');
    internal static bool Category(string text) => text.Length > 0 && text == text.Trim() && Plain(text) && text.IndexOfAny([',', '[', ']', '"']) < 0 && Unquoted(text);
    internal static bool PacketLabel(string text) => text.Length > 0 && text == text.Trim() && Plain(text, 256) && text.IndexOfAny(['[', ']']) < 0 && Unquoted(text);
    internal static bool Unquoted(string text) => !text.Contains("/\'", StringComparison.Ordinal) && !Regex.IsMatch(text, @"(?:^|\s)'", RegexOptions.CultureInvariant);
    internal static bool Header(string text, string family) => text.Equals("@start" + family, StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, "^@start" + family + @"\s+[\w.-]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    internal static string Format(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    internal static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    internal static string Unquote(string value)
    {
        var result = new StringBuilder();
        for (int i = 1; i < value.Length - 1; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length - 1) { c = value[++i]; if (c == 'n') { result.Append("\\n"); continue; } }
            result.Append(c);
        }
        return result.ToString();
    }
    internal static SourceToken Token(Group group, int start) => group.Success ? new(start + group.Index, group.Length) : default;
    internal static SourceToken[] ListTokens(string text, int start, bool coordinatePairs = true)
    {
        var tokens = new List<SourceToken>();
        int offset = 0, depth = 0;
        bool quoted = false;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length)
            {
                char c = text[i];
                if (quoted && c == '\\' && i + 1 < text.Length) { i++; continue; }
                if (c == '"') quoted = !quoted;
                if (!quoted && coordinatePairs) { if (c == '(') depth++; else if (c == ')') depth--; }
                if (c != ',' || quoted || depth != 0) continue;
            }
            var raw = text[offset..i];
            int leading = raw.Length - raw.TrimStart().Length;
            tokens.Add(new(start + offset + leading, raw.Trim().Length));
            offset = i + 1;
        }
        return tokens.ToArray();
    }
}
