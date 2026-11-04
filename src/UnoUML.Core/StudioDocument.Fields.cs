using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace UnoUML.Core;

/// <summary>A literal token reported by the renderer for the current immutable source snapshot.</summary>
public sealed class SourceFieldEdit
{
    internal SourceFieldEdit(string document, int page, int offset, int length, string kind, string role, string text)
        => (Document, Page, Offset, Length, Kind, Role, Text) = (document, page, offset, length, kind, role, text);
    internal string Document { get; }
    internal int Page { get; }
    public int Offset { get; }
    public int Length { get; }
    public string Kind { get; }
    public string Role { get; }
    public string Text { get; }
    public bool Multiline => Kind is "yaml-block" or "yaml-folded-text" || Text.IndexOfAny(['\r', '\n']) >= 0;
}

public sealed partial class StudioDocument
{
    public SourceFieldEdit? OpenSourceField(int start, int length, string kind, string role = "label")
    {
        if (start < 0 || length < 0 || length == 0 && kind is not ("yaml-string" or "salt-text") || start > Page.Source.Length - length || role is not ("label" or "key" or "value" or "member")) return null;
        int pageStart = Index.Lines[Page.StartLine].Start;
        var first = Index.Lines[Page.StartLine];
        var last = Index.Lines[Page.EndLine];
        int body = first.Code.TrimStart('\ufeff', ' ', '\t').StartsWith("@start", StringComparison.OrdinalIgnoreCase) ? first.Next - pageStart : 0;
        int end = last.Code.TrimStart().StartsWith("@end", StringComparison.OrdinalIgnoreCase) ? last.Start - pageStart : Page.Source.Length;
        if (start < body || start + length > end) return null;
        if (kind.StartsWith("yaml-", StringComparison.Ordinal) && Page.Family != "YAML" || kind.StartsWith("salt-", StringComparison.Ordinal) && Page.Family != "Wireframe" ||
            kind.StartsWith("dot-", StringComparison.Ordinal) && Page.Family != "DOT" || kind == "chart-category" && Page.Family != "Chart" ||
            kind == "packet-label" && Page.Family != "Packet" || kind == "files-name" && Page.Family != "Files" ||
            kind == "gantt-label" && Page.Family != "Gantt" || kind == "activity-label" && Page.Family is not ("Activity" or "UML") ||
            kind is "timing-state" or "timing-binary" && Page.Family is not ("Timing" or "UML")) return null;
        string token = Page.Source.Substring(start, length);
        int lineStart = start;
        while (lineStart > 0 && Page.Source[lineStart - 1] is not ('\r' or '\n')) lineStart--;
        if (length == 0 && kind == "yaml-string")
        {
            var before = Page.Source.AsSpan(lineStart, start - lineStart).Trim();
            if (!before.EndsWith(":") && !before.SequenceEqual("-")) return null;
        }
        int indent = YamlBlockParentIndent(Page.Source[lineStart..start]);
        string? text = kind == "yaml-block" ? DecodeYamlBlock(token, indent) : DecodeField(token, kind);
        return text == null ? null : new(Source, ActivePage, pageStart + start, length, kind, role, text);
    }

    public bool ApplySourceField(SourceFieldEdit edit, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!ReferenceEquals(edit.Document, Source) || edit.Page != ActivePage || text == edit.Text || text.Length > 65536 ||
            text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) return false;
        if (edit.Kind == "salt-text") return ApplySaltTextField(edit, text);
        if (edit.Kind == "files-name") return ApplyFilesNameField(edit, text);
        if (edit.Kind == "gantt-label") return ApplyGanttLabelField(edit, text);
        if (Page.Family == "Network") return ApplyNetworkSourceField(edit, text);
        if (Page.Family is "Chart" or "Packet") return ApplyStructuredSourceField(edit, text);
        string? replacement = edit.Kind switch
        {
            "activity-label" => EncodeActivityLabel(edit, text),
            "salt-state" => EncodeSaltState(edit, text),
            "yaml-folded-text" => EncodeYamlFoldedText(edit, text),
            _ => EncodeField(text, edit.Kind)
        };
        if (replacement == null || DecodeField(replacement, edit.Kind == "yaml-block" ? "yaml-double" : edit.Kind) == null) return false;
        if (edit.Kind == "yaml-block")
        {
            var original = Source.AsSpan(edit.Offset, edit.Length);
            int end = original.IndexOfAny('\r', '\n');
            var header = end < 0 ? original : original[..end];
            int comment = header.IndexOf('#');
            if (comment >= 0)
            {
                int padding = comment;
                while (padding > 0 && header[padding - 1] is ' ' or '\t') padding--;
                replacement += (padding == comment ? " " : "") + header[padding..].ToString();
            }
        }
        if (edit.Kind == "yaml-string" && edit.Length == 0)
        {
            if (edit.Offset > 0 && Source[edit.Offset - 1] is ':' or '-') replacement = " " + replacement;
            if (edit.Offset < Source.Length && Source[edit.Offset] == '#') replacement += " ";
        }
        return Patch(edit.Offset, edit.Offset + edit.Length, replacement);
    }

    private static string? DecodeField(string token, string kind)
    {
        switch (kind)
        {
            case "yaml-folded-text": return DecodeYamlFoldedText(token);
            case "yaml-double":
            case "json-string":
                try { using var json = JsonDocument.Parse(token); return json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString() : null; }
                catch (JsonException) { return null; }
            case "yaml-quoted":
                if (token.Length < 2 || token[0] != '\'' || token[^1] != '\'') return null;
                for (int i = 1; i < token.Length - 1; i++)
                    if (token[i] == '\'' && (++i == token.Length - 1 || token[i] != '\'')) return null;
                return token[1..^1].Replace("''", "'", StringComparison.Ordinal);
            case "quoted":
                if (token.Length < 2 || token[0] != '"' || token[^1] != '"') return null;
                var decoded = new StringBuilder();
                for (int i = 1; i < token.Length - 1; i++)
                {
                    char c = token[i];
                    if (c is '"' or '\r' or '\n') return null;
                    if (c == '\\' && i + 1 < token.Length - 1)
                    {
                        char next = token[i + 1];
                        if (next is '\\' or '"' or 'n' or 'r' or 't')
                        {
                            decoded.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
                            i++;
                            continue;
                        }
                    }
                    decoded.Append(c);
                }
                return decoded.ToString();
            case "number":
                return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? token : null;
            case "integer":
                return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? token : null;
            case "date":
                return DateOnly.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? token : null;
            case "boolean":
                return token is "true" or "false" ? token : null;
            case "json-scalar":
                try { using var json = JsonDocument.Parse(token); return json.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) ? token : null; }
                catch (JsonException) { return null; }
            case "files-name":
            case "gantt-label":
            case "yaml-string":
                return !token.Contains('\n') && !token.Contains('\r') ? token : null;
            case "dot-html-text": return WebUtility.HtmlDecode(token);
            case "dot-record-text":
                var record = new StringBuilder();
                for (int i = 0; i < token.Length; i++)
                {
                    char c = token[i];
                    if (c == '\\' && i + 1 < token.Length)
                    {
                        char next = token[++i];
                        record.Append(next is 'n' or 'l' or 'r' ? '\n' : next);
                    }
                    else record.Append(c);
                }
                return record.ToString();
            case "activity-label":
                return string.Join("\n", token.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim())).Replace("\\n", "\n", StringComparison.Ordinal);
            case "timing-state":
                return DecodeField(token, token.StartsWith('"') ? "quoted" : "text");
            case "timing-binary":
                string? binary = DecodeField(token, token.StartsWith('"') ? "quoted" : "text");
                return binary?.ToLowerInvariant() is "0" or "1" or "low" or "high" or "down" ? binary : null;
            case "salt-state":
                return token switch { "[X]" or "[x]" or "(X)" or "(x)" => "true", "[]" or "[ ]" or "()" or "( )" => "false", _ => null };
            case "salt-text":
                return token.IndexOfAny(['\r', '\n']) < 0 ? DecodeSaltText(token).Replace("\\n", "\n", StringComparison.Ordinal) : null;
            case "chart-category":
            case "packet-label":
            case "text":
                return token.IndexOfAny(['\r', '\n']) < 0 ? token.Replace("\\n", "\n", StringComparison.Ordinal) : null;
            default: return null;
        }
    }

    private static string? EncodeField(string text, string kind)
    {
        switch (kind)
        {
            case "json-string":
            case "yaml-block":
            case "yaml-double": return "\"" + JsonEncodedText.Encode(text) + "\"";
            case "yaml-quoted": return text.IndexOfAny(['\r', '\n']) < 0 ? "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'" : null;
            case "yaml-string":
                if (text.Length == 0 || text != text.Trim() || "!&*%|>{}[]@`#\"'".Contains(text[0]) || text.Contains(": ", StringComparison.Ordinal) ||
                    text.Contains(" #", StringComparison.Ordinal) || text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("? ", StringComparison.Ordinal) ||
                    text.EndsWith(':') || text.IndexOfAny(['\r', '\n', ',', '[', ']', '{', '}']) >= 0) return "\"" + JsonEncodedText.Encode(text) + "\"";
                return text;
            case "quoted": return "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).ReplaceLineEndings("\\n") + "\"";
            case "dot-html-text": return WebUtility.HtmlEncode(text).ReplaceLineEndings("&#10;");
            case "dot-record-text":
                var record = new StringBuilder();
                foreach (char c in text.ReplaceLineEndings("\n"))
                {
                    if (c is '\\' or '"' or '{' or '}' or '|' or '<' or '>') record.Append('\\');
                    if (c == '\n') record.Append("\\n"); else record.Append(c);
                }
                return record.ToString();
            case "salt-text":
                if (text.IndexOfAny(['[', ']', '^', '"']) >= 0 || text is "*" or "." || text.TrimStart().StartsWith('@') || text.TrimStart().StartsWith('!')) return null;
                return text.Replace("|", "\\|", StringComparison.Ordinal).Replace("{", "\\{", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal).ReplaceLineEndings("\\n");
            case "chart-category":
                return text.Length is > 0 and <= 1024 && text == text.Trim() && text.IndexOfAny([',', '[', ']', '"', '\r', '\n']) < 0 ? text : null;
            case "packet-label":
                return text.Length is > 0 and <= 256 && text == text.Trim() && text.IndexOfAny(['[', '\r', '\n']) < 0 ? text : null;
            case "timing-state":
                if (string.IsNullOrWhiteSpace(text)) return null;
                return text.All(c => char.IsLetter(c) || char.IsNumber(c) || c is '_' or '.' or '+' or '-') ? text : EncodeField(text, "quoted");
            case "timing-binary":
                return text.ToLowerInvariant() is "0" or "1" or "low" or "high" or "down" ? text : null;
            case "text":
                if (text.TrimStart().StartsWith('@') || text.TrimStart().StartsWith('!')) return null;
                return text.ReplaceLineEndings("\\n");
            case "number":
            case "integer":
            case "date":
            case "boolean":
            case "json-scalar": return text.Trim();
            default: return null;
        }
    }

    private string? EncodeActivityLabel(SourceFieldEdit edit, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains(';')) return null;
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Any(line => line.TrimStart().StartsWith('@') || line.TrimStart().StartsWith('!'))) return null;
        string[] original = Source.Substring(edit.Offset, edit.Length).ReplaceLineEndings("\n").Split('\n');
        if (original.Length == 1) return string.Join("\\n", lines);
        string fallback = original[1][..(original[1].Length - original[1].TrimStart(' ', '\t').Length)];
        for (int i = 1; i < lines.Length; i++)
        {
            string indent = i < original.Length ? original[i][..(original[i].Length - original[i].TrimStart(' ', '\t').Length)] : fallback;
            lines[i] = indent + lines[i];
        }
        return string.Join(Index.NewLine, lines);
    }

    private static string? DecodeYamlBlock(string token, int parentIndent)
    {
        string[] rows = token.ReplaceLineEndings("\n").Split('\n');
        string header = rows[0].Split('#')[0].TrimEnd();
        if (header.Length == 0 || header[0] is not ('|' or '>')) return null;
        int indent = 0;
        char chomp = ' ';
        foreach (char option in header.AsSpan(1))
        {
            if (option is '+' or '-' && chomp == ' ') chomp = option;
            else if (option is >= '1' and <= '9' && indent == 0) indent = parentIndent + option - '0';
            else return null;
        }
        if (rows.Length == 1) return "";
        if (indent == 0)
        {
            indent = rows.Skip(1).Where(row => row.Trim().Length > 0).Select(YamlIndent).DefaultIfEmpty(-1).Min();
            if (indent < 0) indent = rows.Skip(1).Select(YamlIndent).DefaultIfEmpty(parentIndent + 1).Max();
        }
        var content = rows.Skip(1).Select(row => StripYamlIndent(row, indent)).ToArray();
        if (content.Any(row => row == null)) return null;
        var value = new StringBuilder();
        for (int i = 0; i < content.Length; i++)
        {
            string row = content[i]!;
            value.Append(row);
            if (header[0] == '|' || row.Length == 0 || row.StartsWith(' ')) value.Append('\n');
            else
            {
                int next = i + 1;
                while (next < content.Length && content[next]!.Length == 0) next++;
                if (next == content.Length || content[next]!.StartsWith(' ')) value.Append('\n');
                else if (next == i + 1) value.Append(' ');
            }
        }
        string decoded = value.ToString();
        return chomp switch { '+' => decoded, '-' => decoded.TrimEnd('\n'), _ => decoded.TrimEnd('\n') is { Length: > 0 } clipped ? clipped + "\n" : "" };
    }
}
