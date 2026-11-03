using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

internal static partial class DataDiagram
{
    private sealed record YamlValue(string Text, TokenSpan Token, int Line)
    {
        internal List<(string Key, TokenSpan KeyToken, YamlValue Value)>? Children;
    }
    private readonly record struct YamlLine(string Text, int Start, int Line, int Indent);
    private sealed record Highlight(string[] Path, string Stereotype);
    private sealed class InvalidYaml : Exception { }

    internal static string? RenderYaml(string source)
    {
        try
        {
            if (!DataCommands(source, out source, out string title, out int titleLine, out var highlights)) return null;
            var parser = new YamlParser(source);
            var root = parser.Parse();
            var tables = new List<Table>();
            void Visit(YamlValue node, int parent, int row, int depth, string[] path)
            {
                if (depth > 64 || tables.Count >= 4096) throw new InvalidYaml();
                int index = tables.Count;
                var table = new Table(depth, parent, row) { Caption = node.Children == null ? "YAML" : node.Text };
                tables.Add(table);
                var children = node.Children ?? [("value", default, node)];
                foreach (var (key, keyToken, value) in children)
                {
                    var currentPath = path.Append(key).ToArray();
                    var highlight = highlights.LastOrDefault(h => h.Path.SequenceEqual(currentPath, StringComparer.Ordinal));
                    var field = new Field(YamlDisplay(key), YamlDisplay(value.Text), keyToken.Kind != null ? keyToken.Line : value.Line, keyToken,
                        value.Children == null ? value.Token : default)
                    {
                        Highlight = highlight != null,
                        Stereotype = highlight?.Stereotype ?? ""
                    };
                    int fieldIndex = table.Fields.Count;
                    table.Fields.Add(field);
                    if (value.Children != null) Visit(value, index, fieldIndex, depth + 1, currentPath);
                }
            }
            Visit(root, -1, -1, 0, []);
            return Render(tables, title, titleLine);
        }
        catch (InvalidYaml) { return null; }
    }

    private static string YamlDisplay(string value)
    {
        if (value == "<<") return "<U+003C><U+003C>";
        if (value.StartsWith("!<", StringComparison.Ordinal) && value.IndexOf('>') is int tagEnd && tagEnd > 1)
            return "!<U+003C>" + value[2..tagEnd] + "<U+003E>" + value[(tagEnd + 1)..];
        return value;
    }

    /// <summary>Bounded block/flow parser retaining physical UTF-16 ranges for every literal.</summary>
    private sealed class YamlParser
    {
        private readonly List<YamlLine> lines = [];
        private int cursor, nodes;

        internal YamlParser(string source)
        {
            int offset = 0, line = 0;
            bool opened = false, closed = false;
            foreach (var raw in source.AsSpan().EnumerateLines())
            {
                var trimmed = raw.Trim();
                if (!opened)
                {
                    if (!trimmed.IsEmpty)
                    {
                        if (!trimmed.TrimStart('\uFEFF').SequenceEqual("@startyaml")) throw new InvalidYaml();
                        opened = true;
                    }
                }
                else if (trimmed.SequenceEqual("@endyaml")) closed = true;
                else if (closed)
                {
                    if (!trimmed.IsEmpty) throw new InvalidYaml();
                }
                else
                {
                    if (lines.Count >= 12000 || raw.Length > 16384) throw new InvalidYaml();
                    int prefix = 0, indent = 0;
                    while (prefix < raw.Length && raw[prefix] is ' ' or '\t')
                        indent += raw[prefix++] == '\t' ? 4 : 1;
                    if (prefix < raw.Length && char.IsWhiteSpace(raw[prefix])) throw new InvalidYaml();
                    string content = raw[prefix..].ToString();
                    if (content.Contains('\t')) throw new InvalidYaml();
                    lines.Add(new(content, offset + prefix, line, indent));
                }
                offset += raw.Length;
                if (offset < source.Length && source[offset] == '\r') offset++;
                if (offset < source.Length && source[offset] == '\n') offset++;
                line++;
            }
            if (!opened || !closed) throw new InvalidYaml();
        }

        internal YamlValue Parse()
        {
            Skip();
            if (cursor >= lines.Count) throw new InvalidYaml();
            if (lines[cursor].Text.Trim() == "---") { cursor++; Skip(); }
            if (cursor >= lines.Count) throw new InvalidYaml();
            var value = Block(lines[cursor].Indent, 0);
            Skip();
            if (cursor < lines.Count && lines[cursor].Text.Trim() == "...") { cursor++; Skip(); }
            if (cursor != lines.Count) throw new InvalidYaml();
            return value;
        }

        private void Skip()
        {
            while (cursor < lines.Count && (lines[cursor].Text.Trim().Length == 0 || lines[cursor].Text.TrimStart().StartsWith('#'))) cursor++;
        }

        private YamlValue Block(int indent, int depth)
        {
            if (depth > 64 || ++nodes > 12000) throw new InvalidYaml();
            Skip();
            if (cursor >= lines.Count || lines[cursor].Indent != indent) throw new InvalidYaml();
            var line = lines[cursor];
            string text = Comment(line.Text).TrimEnd();
            if (Dash(text)) return Sequence(indent, depth);
            if (Colon(text) >= 0) return Mapping(indent, depth);
            cursor++;
            return Inline(text, line.Start, line.Line, depth);
        }

        private YamlValue Mapping(int indent, int depth, YamlLine? first = null)
        {
            var result = new YamlValue("{}", default, first?.Line ?? lines[cursor].Line) { Children = [] };
            var keys = new Dictionary<string, int>(StringComparer.Ordinal);
            while (true)
            {
                Skip();
                if (first == null && (cursor >= lines.Count || lines[cursor].Indent != indent || Dash(lines[cursor].Text))) break;
                var line = first ?? lines[cursor++];
                first = null;
                string text = Comment(line.Text);
                int colon = Colon(text);
                if (colon <= 0) throw new InvalidYaml();
                string keyText = text[..colon].TrimEnd();
                var key = Scalar(keyText, line.Start, line.Line);
                if (key.Text.Length == 0 && key.Token.Kind == "yaml-string") throw new InvalidYaml();
                int at = colon + 1;
                while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
                var value = Value(text[at..].TrimEnd(), line.Start + at, line.Line, indent, depth + 1, allowIndentlessSequence: true);
                PutMapping(result.Children!, keys, key, value);
                if (++nodes > 12000 || result.Children.Count > 4096) throw new InvalidYaml();
                Skip();
                if (cursor < lines.Count && lines[cursor].Indent > indent) throw new InvalidYaml();
            }
            return result;
        }

        private YamlValue Sequence(int indent, int depth)
        {
            var result = new YamlValue("[]", default, lines[cursor].Line) { Children = [] };
            while (true)
            {
                Skip();
                if (cursor >= lines.Count || lines[cursor].Indent != indent || !Dash(lines[cursor].Text)) break;
                var line = lines[cursor++];
                string text = Comment(line.Text);
                int start = 1;
                while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
                string content = text[start..];
                YamlValue value;
                if (Colon(content) >= 0 && content[0] is not ('{' or '['))
                    value = Mapping(indent + start, depth + 1, new(content, line.Start + start, line.Line, indent + start));
                else value = Value(content.TrimEnd(), line.Start + start, line.Line, indent, depth + 1);
                result.Children!.Add((result.Children.Count.ToString(CultureInfo.InvariantCulture), default, value));
                if (++nodes > 12000 || result.Children.Count > 4096) throw new InvalidYaml();
            }
            return result;
        }

        private YamlValue Value(string text, int start, int line, int indent, int depth, bool allowIndentlessSequence = false)
        {
            if (depth > 64) throw new InvalidYaml();
            if (text.Length > 0 && text[0] is '|' or '>' && text.Skip(1).All(c => c is '+' or '-' || char.IsAsciiDigit(c)))
                return BlockScalar(text, start, line, indent);
            Skip();
            if (text.Length == 0)
            {
                if (cursor < lines.Count)
                {
                    var next = lines[cursor];
                    string content = Comment(next.Text).TrimEnd();
                    if (allowIndentlessSequence && next.Indent == indent && Dash(content)) return Sequence(indent, depth);
                    if (next.Indent > indent)
                    {
                        if (!Dash(content) && Colon(content) < 0 && content[0] is not ('[' or '{')) return FoldedText(indent);
                        return Block(next.Indent, depth);
                    }
                }
                return new("", new(start, 0, line, "yaml-string", "yaml"), line);
            }
            return Inline(text, start, line, depth);
        }

        private YamlValue FoldedText(int parentIndent)
        {
            var first = lines[cursor];
            var value = new StringBuilder();
            int end = first.Start;
            while (cursor < lines.Count)
            {
                Skip();
                if (cursor == lines.Count) break;
                var current = lines[cursor];
                string text = Comment(current.Text).TrimEnd();
                if (current.Indent <= parentIndent || Dash(text) || Colon(text) >= 0 || text[0] is '[' or '{') break;
                if (++nodes > 12000) throw new InvalidYaml();
                var scalar = Scalar(text, current.Start, current.Line);
                if (value.Length > 0) value.Append(' ');
                value.Append(scalar.Text);
                if (value.Length > 8192) throw new InvalidYaml();
                end = current.Start + text.Length;
                cursor++;
            }
            string decoded = value.ToString();
            if (!ValidDataText(YamlDisplay(decoded))) throw new InvalidYaml();
            return new(decoded, new(first.Start, end - first.Start, first.Line, "yaml-folded-text", "yaml"), first.Line);
        }

        private YamlValue BlockScalar(string header, int start, int line, int indent)
        {
            int explicitIndent = 0;
            char chomp = ' ';
            foreach (char option in header.AsSpan(1))
            {
                if (option is '+' or '-' && chomp == ' ') chomp = option;
                else if (option is >= '1' and <= '9' && explicitIndent == 0) explicitIndent = option - '0';
                else throw new InvalidYaml();
            }
            int first = cursor, end = cursor;
            while (end < lines.Count && (lines[end].Text.Trim().Length == 0 || lines[end].Indent > indent)) end++;
            int blockIndent = lines.Skip(first).Take(end - first).Where(item => item.Text.Trim().Length != 0).Select(item => item.Indent).DefaultIfEmpty(-1).Min();
            if (blockIndent < 0) blockIndent = lines.Skip(first).Take(end - first).Select(item => item.Indent).DefaultIfEmpty(indent + 1).Max();
            if (explicitIndent != 0) blockIndent = indent + explicitIndent;
            var value = new StringBuilder();
            for (int i = first; i < end; i++)
            {
                var current = lines[i];
                if (current.Text.Trim().Length > 0 && current.Indent < blockIndent) throw new InvalidYaml();
                value.Append(' ', Math.Max(0, current.Indent - blockIndent)).Append(current.Text);
                if (header[0] == '|' || current.Text.Length == 0 || current.Indent > blockIndent)
                {
                    value.Append('\n');
                    continue;
                }
                int following = i + 1;
                while (following < end && lines[following].Text.Length == 0 && lines[following].Indent <= blockIndent) following++;
                if (following == end || lines[following].Indent > blockIndent) value.Append('\n');
                else if (following == i + 1) value.Append(' ');
            }
            cursor = end;
            string scalar = value.ToString();
            if (chomp != '+')
            {
                scalar = scalar.TrimEnd('\n');
                if (chomp != '-' && scalar.Length > 0) scalar += "\n";
            }
            if (!ValidDataText(scalar)) throw new InvalidYaml();
            int length = end > first ? lines[end - 1].Start + lines[end - 1].Text.Length - start : header.Length;
            return new(scalar, new(start, length, line, "yaml-block", "yaml"), line);
        }

        private YamlValue Inline(string text, int start, int line, int depth)
        {
            if (depth > 64 || ++nodes > 12000) throw new InvalidYaml();
            if (text.Length == 0 || text[0] is not ('[' or '{')) return Scalar(text, start, line);
            int position = 0;
            var value = Flow(text, start, line, ref position, depth);
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position != text.Length) throw new InvalidYaml();
            return value;
        }

        private YamlValue Flow(string text, int start, int line, ref int position, int depth)
        {
            if (depth > 64 || ++nodes > 12000) throw new InvalidYaml();
            char open = text[position++], close = open == '[' ? ']' : '}';
            var value = new YamlValue(open == '[' ? "[]" : "{}", default, line) { Children = [] };
            var keys = open == '{' ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
            while (true)
            {
                while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
                if (position == text.Length) throw new InvalidYaml();
                if (text[position] == close) { position++; return value; }
                string key = value.Children.Count.ToString(CultureInfo.InvariantCulture);
                TokenSpan keyToken = default;
                if (open == '{')
                {
                    int keyStart = position;
                    position = Boundary(text, position, ":");
                    if (position == text.Length || text[position] != ':') throw new InvalidYaml();
                    var parsedKey = Scalar(text[keyStart..position].TrimEnd(), start + keyStart, line);
                    key = parsedKey.Text; keyToken = parsedKey.Token;
                    position++;
                    while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
                }
                YamlValue child;
                if (position < text.Length && text[position] is '[' or '{') child = Flow(text, start, line, ref position, depth + 1);
                else
                {
                    int scalarStart = position;
                    position = Boundary(text, position, "," + close);
                    child = Scalar(text[scalarStart..position].TrimEnd(), start + scalarStart, line);
                }
                if (keys != null) PutMapping(value.Children, keys, new(key, keyToken, line), child);
                else value.Children.Add((key, keyToken, child));
                if (value.Children.Count > 4096) throw new InvalidYaml();
                while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
                if (position == text.Length) throw new InvalidYaml();
                if (text[position] == close) { position++; return value; }
                if (text[position++] != ',') throw new InvalidYaml();
            }
        }

        private static void PutMapping(List<(string Key, TokenSpan KeyToken, YamlValue Value)> children,
            Dictionary<string, int> keys, YamlValue key, YamlValue value)
        {
            if (keys.TryGetValue(key.Text, out int row)) children[row] = (key.Text, key.Token, value);
            else
            {
                keys.Add(key.Text, children.Count);
                children.Add((key.Text, key.Token, value));
            }
        }

        private static YamlValue Scalar(string text, int start, int line)
        {
            string value = text, kind = "yaml-string";
            if (text.StartsWith('"'))
            {
                try { using var json = JsonDocument.Parse(text); value = json.RootElement.GetString()!; }
                catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new InvalidYaml(); }
                kind = "yaml-double";
            }
            else if (text.StartsWith('\''))
            {
                if (text.Length < 2 || text[^1] != '\'') throw new InvalidYaml();
                var inside = text.AsSpan(1, text.Length - 2);
                for (int i = 0; i < inside.Length; i++) if (inside[i] == '\'' && (++i == inside.Length || inside[i] != '\'')) throw new InvalidYaml();
                value = inside.ToString().Replace("''", "'", StringComparison.Ordinal);
                kind = "yaml-quoted";
            }
            // PlantUML's YAML grammar displays indicators, aliases and tags as literal text.
            // No host object construction or reference expansion is performed.
            if (!ValidDataText(YamlDisplay(value))) throw new InvalidYaml();
            return new(value, new(start, text.Length, line, kind, "yaml"), line);
        }

        private static bool Dash(string text) => text == "-" || text.StartsWith("- ", StringComparison.Ordinal);
        private static int Colon(string text)
        {
            if (text.StartsWith('[') || text.StartsWith('{')) return -1;
            int index = Boundary(text, 0, ":");
            return index < text.Length ? index : -1;
        }
        private static string Comment(string text)
        {
            int at = Boundary(text, 0, "#");
            return at < text.Length ? text[..at] : text;
        }
        private static int Boundary(string text, int start, string separators)
        {
            char quote = '\0';
            bool comments = separators == "#", tokenStart = true, rootValue = false;
            int flowDepth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"') { i++; continue; }
                    if (c == quote)
                    {
                        if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') i++;
                        else quote = '\0';
                    }
                }
                else if (tokenStart && c is '\'' or '"') { quote = c; tokenStart = false; }
                else if (separators.Contains(c) && (!comments || i == 0 || char.IsWhiteSpace(text[i - 1]))) return i;
                else if (!char.IsWhiteSpace(c))
                {
                    if (!comments) tokenStart = false;
                    else if (tokenStart && c is '[' or '{') { flowDepth++; tokenStart = true; }
                    else if (flowDepth > 0 && c is ']' or '}') { flowDepth--; tokenStart = false; }
                    else if (flowDepth > 0 && c == ',') tokenStart = true;
                    else if (c == ':' && (flowDepth > 0 || !rootValue)) { rootValue = true; tokenStart = true; }
                    else if (!(tokenStart && c == '-' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))) tokenStart = false;
                }
            }
            if (quote != '\0') throw new InvalidYaml();
            return text.Length;
        }

    }

    private static bool DataCommands(string source, out string cleaned, out string title, out int titleLine, out List<Highlight> highlights)
    {
        title = ""; titleLine = -1; highlights = []; cleaned = source;
        char[]? mask = null;
        int start = 0, line = 0;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            string text = raw.TrimEnd().ToString();
            bool command = false;
            if (text.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                if (titleLine >= 0 || !SvgText.IsSupported(title = text[6..].Trim())) return false;
                titleLine = line; command = true;
            }
            else if (text.StartsWith("#highlight ", StringComparison.OrdinalIgnoreCase))
            {
                if (highlights.Count >= 1024) return false;
                string stereotype = "";
                int marker = text.IndexOf("<<", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    if (!text.EndsWith(">>", StringComparison.Ordinal)) return false;
                    stereotype = text[(marker + 2)..^2].Trim();
                    text = text[..marker];
                }
                var path = new List<string>();
                foreach (Match match in HighlightPath().Matches(text[11..]))
                {
                    try { using var json = JsonDocument.Parse(match.Value); path.Add(json.RootElement.GetString()!); }
                    catch (JsonException) { return false; }
                }
                if (path.Count == 0 || stereotype.Length > 128) return false;
                highlights.Add(new(path.ToArray(), stereotype));
                command = true;
            }
            if (command)
            {
                mask ??= source.ToCharArray();
                Array.Fill(mask, ' ', start, raw.Length);
            }
            start += raw.Length;
            if (start < source.Length && source[start] == '\r') start++;
            if (start < source.Length && source[start] == '\n') start++;
            line++;
        }
        if (mask != null) cleaned = new(mask);
        return true;
    }

    private static void ApplyHighlights(List<Table> tables, List<Highlight> highlights)
    {
        if (highlights.Count == 0) return;
        var paths = new List<string[]>();
        foreach (var table in tables)
        {
            var path = table.Parent < 0 ? [] : paths[table.Parent].Append(tables[table.Parent].Fields[table.Row].Key).ToArray();
            paths.Add(path);
            foreach (var item in table.Fields)
            {
                var target = path.Append(item.Key);
                var selected = highlights.LastOrDefault(highlight => highlight.Path.SequenceEqual(target, StringComparer.Ordinal));
                if (selected == null) continue;
                item.Highlight = true;
                item.Stereotype = selected.Stereotype;
            }
        }
    }

    [GeneratedRegex("\\\"(?:\\\\.|[^\\\"\\\\])*\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex HighlightPath();
}
