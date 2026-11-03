using System.Text;
using System.Text.Json;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    private static string? DecodeYamlFoldedText(string token) => ReadYamlFoldedText(token, null);

    private string? EncodeYamlFoldedText(SourceFieldEdit edit, string text)
    {
        string token = Source.Substring(edit.Offset, edit.Length);
        var fields = new List<(int Start, int Length)>();
        if (ReadYamlFoldedText(token, fields) == null) return null;
        var replacement = new StringBuilder(token.Length + text.Length);
        int copied = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            replacement.Append(token.AsSpan(copied, field.Start - copied));
            if (i == 0) replacement.Append('"').Append(JsonEncodedText.Encode(text)).Append('"');
            copied = field.Start + field.Length;
        }
        return replacement.Append(token.AsSpan(copied)).ToString();
    }

    // Keep every original comment, line ending and indentation when replacing a folded scalar.
    // Only its content tokens change. Remaining lines become blank or comment-only YAML lines.
    private static string? ReadYamlFoldedText(string token, List<(int Start, int Length)>? fields)
    {
        var value = new StringBuilder();
        int offset = 0, rows = 0, scalars = 0;
        foreach (var row in token.AsSpan().EnumerateLines())
        {
            if (++rows > 12000 || row.Length > 16384 || !YamlScalarRange(row, out int start, out int length)) return null;
            if (length > 0)
            {
                string scalar = row.Slice(start, length).ToString();
                string kind = scalar[0] switch { '"' => "yaml-double", '\'' => "yaml-quoted", _ => "yaml-string" };
                string? decoded = DecodeField(scalar, kind);
                if (decoded == null) return null;
                if (value.Length > 0) value.Append(' ');
                value.Append(decoded);
                if (value.Length > 8192) return null;
                fields?.Add((offset + start, length));
                scalars++;
            }
            offset += row.Length;
            if (offset < token.Length && token[offset] == '\r') offset++;
            if (offset < token.Length && token[offset] == '\n') offset++;
        }
        return scalars == 0 ? null : value.ToString();
    }

    private static bool YamlScalarRange(ReadOnlySpan<char> row, out int start, out int length)
    {
        start = 0;
        while (start < row.Length && row[start] is ' ' or '\t') start++;
        length = 0;
        if (start == row.Length || row[start] == '#') return true;
        if (row[start] is '[' or '{' || row[start] == '-' && (start + 1 == row.Length || char.IsWhiteSpace(row[start + 1]))) return false;
        char quote = '\0';
        int end = row.Length;
        for (int i = start; i < row.Length; i++)
        {
            char c = row[i];
            if (quote != '\0')
            {
                if (quote == '"' && c == '\\') { i++; continue; }
                if (c != quote) continue;
                if (quote == '\'' && i + 1 < row.Length && row[i + 1] == '\'') i++;
                else quote = '\0';
            }
            else if (i == start && c is '\'' or '"') quote = c;
            else if (c == '#' && i > start && char.IsWhiteSpace(row[i - 1])) { end = i; break; }
            else if (c == ':') return false;
        }
        if (quote != '\0') return false;
        while (end > start && char.IsWhiteSpace(row[end - 1])) end--;
        length = end - start;
        return true;
    }

    // A block value on "- key: |2" is relative to the mapping key, whose indentation
    // includes the sequence marker. A direct "- |2" value remains relative to the list.
    private static int YamlBlockParentIndent(string prefix)
    {
        int indent = YamlIndent(prefix), at = 0;
        while (at < prefix.Length && prefix[at] is ' ' or '\t') at++;
        if (at + 1 < prefix.Length && prefix[at] == '-' && prefix[at + 1] == ' ')
        {
            int key = at + 1;
            while (key < prefix.Length && char.IsWhiteSpace(prefix[key])) key++;
            if (prefix.AsSpan(key).Contains(':')) indent += key - at;
        }
        return indent;
    }

    private static int YamlIndent(string row)
    {
        int indent = 0;
        foreach (char c in row)
        {
            if (c is not (' ' or '\t')) break;
            indent += c == '\t' ? 4 : 1;
        }
        return indent;
    }

    private static string? StripYamlIndent(string row, int indent)
    {
        int column = 0, at = 0;
        while (at < row.Length && row[at] is ' ' or '\t') column += row[at++] == '\t' ? 4 : 1;
        if (column < indent) return row.Trim().Length == 0 ? "" : null;
        return new string(' ', column - indent) + row[at..];
    }
}
