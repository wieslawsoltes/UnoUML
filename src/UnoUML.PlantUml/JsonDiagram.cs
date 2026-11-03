using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UnoUML.PlantUml;

/// <summary>Streams JSON tokens into field tables while retaining each token's original document line.</summary>
internal static partial class DataDiagram
{
    private sealed class Table(int depth, int parent, int row)
    {
        internal readonly int Depth = depth, Parent = parent, Row = row;
        internal readonly List<Field> Fields = [];
        internal string Caption = "{}";
        internal int Keys = 32, Values = 28, Children, Span, Top, ChildCursor, X, Y;
        internal StyleLabel Header = null!;
        internal int[] RowOffsets = [];
        internal int RowsHeight;
        internal int Width => Math.Max(Keys + Values, Header.Width);
        internal int Height => Header.Height + RowsHeight;
        internal int RowCenter(int row) => Y + Header.Height + RowOffsets[row] + Fields[row].Height / 2;
    }

    private readonly record struct TokenSpan(int Start, int Length, int Line, string Kind, string Dialect = "json");
    private sealed record Field(string Key, string Value, int SourceLine, TokenSpan KeyToken = default, TokenSpan ValueToken = default)
    {
        internal StyleLabel KeyLabel = null!, ValueLabel = null!;
        internal bool Highlight;
        internal string Stereotype = "";
        internal int Height => Math.Max(KeyLabel.Height, ValueLabel.Height);
    }

    internal static string? RenderJson(string source)
    {
        if (!DataCommands(source, out source, out string title, out int titleLine, out var highlights)) return null;
        int header = source.IndexOf("@startjson", StringComparison.Ordinal);
        int firstBreak = source.AsSpan(header).IndexOfAny('\r', '\n');
        if (firstBreak < 0) return null;
        int begin = header + firstBreak + 1;
        if (source[begin - 1] == '\r' && begin < source.Length && source[begin] == '\n') begin++;
        int end = source.LastIndexOf("@endjson", StringComparison.Ordinal);
        if (end < begin || !source.AsSpan(end + 8).Trim().IsEmpty) return null;
        int lastLine = source.AsSpan(0, end).LastIndexOfAny('\r', '\n') + 1;
        if (!source.AsSpan(lastLine, end - lastLine).Trim().IsEmpty) return null;
        var bytes = Encoding.UTF8.GetBytes(source[begin..end]);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var tables = new List<Table>();
        var stack = new Stack<int>();
        int line = 0, mark = 0, offset = begin, tokens = 0;
        foreach (var unused in source.AsSpan(0, begin).EnumerateLines()) line++;
        line--; // the header slice ends in a newline
        string? key = null;
        TokenSpan keyToken = default;
        try
        {
            while (reader.Read())
            {
                if (++tokens > 12000 || reader.ValueSpan.Length > 6 * 2048) return null;
                int pos = (int)reader.TokenStartIndex;
                var consumed = bytes.AsSpan(mark, pos - mark);
                int chars = Encoding.UTF8.GetCharCount(consumed);
                var segment = source.AsSpan(offset, chars);
                line += segment.Count('\n') + segment.Count('\r') - segment.Count("\r\n");
                offset += chars; mark = pos;
                var type = reader.TokenType;
                if (type == JsonTokenType.PropertyName)
                {
                    key = reader.GetString()!;
                    if (!ValidDataText(key)) return null;
                    keyToken = new(offset, Encoding.UTF8.GetCharCount(reader.ValueSpan) + 2, line, "key");
                    continue;
                }
                if (type is JsonTokenType.EndArray or JsonTokenType.EndObject) { stack.Pop(); continue; }
                bool container = type is JsonTokenType.StartArray or JsonTokenType.StartObject;
                int parent = stack.TryPeek(out int current) ? current : -1;
                string value = type switch
                {
                    JsonTokenType.StartObject => "{}",
                    JsonTokenType.StartArray => "[]",
                    JsonTokenType.String => reader.GetString()!,
                    _ => Encoding.UTF8.GetString(reader.ValueSpan)
                };
                if (!ValidDataText(value)) return null;
                int row = 0;
                if (parent >= 0 || !container)
                {
                    if (parent < 0) { if (tables.Count > 0) return null; tables.Add(new(0, -1, -1) { Caption = "JSON" }); }
                    var owner = tables[parent < 0 ? 0 : parent];
                    row = owner.Fields.Count;
                    string name = key ?? (parent < 0 ? "value" : row.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    TokenSpan token = container ? default : type == JsonTokenType.String
                        ? new(offset, Encoding.UTF8.GetCharCount(reader.ValueSpan) + 2, line, "string")
                        : new(offset, reader.ValueSpan.Length, line, "scalar");
                    owner.Fields.Add(new(name, value, key != null ? keyToken.Line : line, keyToken, token));
                }
                if (container)
                {
                    int index = tables.Count;
                    tables.Add(new(stack.Count, parent, row) { Caption = value });
                    stack.Push(index);
                }
                key = null; keyToken = default;
            }
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        if (tables.Count == 0) return null;
        ApplyHighlights(tables, highlights);
        return Render(tables, title, titleLine);
    }

    // Escaped data is validated again after decoding, before it becomes SVG text.
    private static bool ValidDataText(string text) => text.Length <= 8192 &&
        !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) &&
        text.AsSpan().IndexOfAny('\uFFFE', '\uFFFF') < 0 && SvgText.IsSupported(text);

    private static void Cell(StringBuilder svg, TokenSpan token, string role, StyleLabel label, int x, int y, int width)
    {
        bool editable = token.Kind != null;
        bool simple = label.Rows.Length == 1 && SvgText.IsPlain(label.Rows[0]);
        int start = svg.Length;
        if (!simple) svg.Append("<g>");
        label.Append(svg, x, y, width);
        if (!simple) svg.Append("</g>");
        if (!editable) return;
        var attributes = new StringBuilder();
        if (token.Dialect == "json")
            attributes.Append(CultureInfo.InvariantCulture, $" data-json-start=\"{token.Start}\" data-json-length=\"{token.Length}\" data-json-kind=\"{token.Kind}\" data-source-line=\"{token.Line}\"");
        else
            attributes.Append(CultureInfo.InvariantCulture, $" data-edit-start=\"{token.Start}\" data-edit-length=\"{token.Length}\" data-edit-kind=\"{token.Kind}\" data-edit-role=\"{role}\" data-source-line=\"{token.Line}\"");
        int close = start;
        while (close < svg.Length && svg[close] != '>') close++;
        if (close < svg.Length) svg.Insert(close, attributes.ToString());
    }

    private static string? Render(List<Table> tables, string title = "", int titleLine = -1)
    {
        const int gap = 14;
        foreach (var table in tables)
        {
            table.Header = StyleLabel.Measure(table.Caption, "node", padding: "3 8");
            table.Keys = 32; table.Values = 28;
            table.RowOffsets = new int[table.Fields.Count];
            for (int row = 0; row < table.Fields.Count; row++)
            {
                var field = table.Fields[row];
                string category = field.Highlight ? "node highlight" : "node";
                field.KeyLabel = StyleLabel.Measure(field.Key.Length == 0 && field.KeyToken.Kind != null ? "<U+0022><U+0022>" : field.Key, category, padding: "3 8", stereotype: field.Stereotype);
                field.ValueLabel = StyleLabel.Measure(field.Value.Length == 0 && field.ValueToken.Kind != null ? "<U+0022><U+0022>" : field.Value, category, padding: "3 8", stereotype: field.Stereotype);
                table.Keys = Math.Max(table.Keys, field.KeyLabel.Width);
                table.Values = Math.Max(table.Values, field.ValueLabel.Width);
                table.RowOffsets[row] = table.RowsHeight;
                table.RowsHeight += field.Height;
            }
        }
        int[] widths = new int[65];
        for (int i = tables.Count - 1; i >= 0; i--)
        {
            var t = tables[i];
            widths[t.Depth] = Math.Max(widths[t.Depth], t.Width);
            t.Span = Math.Max(t.Height, t.Children);
            if (t.Parent >= 0)
            {
                var p = tables[t.Parent];
                p.Children += t.Span + (p.Children > 0 ? gap : 0);
            }
        }
        var heading = StyleTitle.Measure(title, StyleDocument.Current, 18);
        int titleHeight = heading == null ? 0 : heading.Height + 16;
        int left = 10, height = 20 + tables[0].Span + titleHeight;
        for (int i = 0; i < widths.Length; i++) { int width = widths[i]; if (width == 0) break; widths[i] = left; left += width + 48; }
        if (left > 32768 || height > 32768) return null;
        int canvasWidth = Math.Max(left - 38, (heading?.Width ?? 0) + 20);
        var svg = TreeDiagram.Canvas(canvasWidth, height);
        heading?.Append(svg, StyleDocument.Current, canvasWidth / 2, 8, titleLine);
        foreach (var t in tables)
        {
            t.X = widths[t.Depth];
            if (t.Parent >= 0)
            {
                var p = tables[t.Parent]; t.Top = p.ChildCursor; p.ChildCursor += t.Span + gap;
            }
            else t.Top = 10 + titleHeight;
            t.Y = t.Top + (t.Span - t.Height) / 2;
            t.ChildCursor = t.Top + (t.Span - t.Children) / 2;
            if (t.Parent >= 0)
            {
                var p = tables[t.Parent];
                int x = p.X + p.Width, y = p.RowCenter(t.Row), dest = t.Y + t.Header.Height / 2;
                svg.Append(CultureInfo.InvariantCulture, $"<path data-style-name=\"arrow\" data-style-metrics=\"fontsize\" d=\"M{x} {y} C{x + 26} {y} {t.X - 20} {dest} {t.X} {dest}\" fill=\"none\" stroke=\"#77778e\"/>");
            }
        }
        foreach (var t in tables)
        {
            svg.Append("<g class=\"data-table\"");
            StyleLabel.Scope(svg, "node");
            svg.Append(CultureInfo.InvariantCulture, $"><rect x=\"{t.X}\" y=\"{t.Y}\" width=\"{t.Width}\" height=\"{t.Height}\" rx=\"4\" fill=\"#fafaff\" stroke=\"#77778e\"/>");
            t.Header.Append(svg, t.X, t.Y, t.Width);
            int y = t.Y + t.Header.Height;
            foreach (var f in t.Fields)
            {
                svg.Append(CultureInfo.InvariantCulture, $"<g class=\"data-field\" data-source-line=\"{f.SourceLine}\"");
                StyleLabel.Scope(svg, f.Highlight ? "node highlight" : "node", f.Stereotype);
                svg.Append('>');
                if (f.Highlight)
                    svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{t.X}\" y=\"{y}\" width=\"{t.Width}\" height=\"{f.Height}\" fill=\"#fff1a8\"/>");
                svg.Append(CultureInfo.InvariantCulture, $"<path data-style-part=\"separator\" d=\"M{t.X} {y}h{t.Width}\" stroke=\"#d8d8e1\"/>");
                Cell(svg, f.KeyToken, "key", f.KeyLabel, t.X, y, t.Keys);
                Cell(svg, f.ValueToken, "value", f.ValueLabel, t.X + t.Keys, y, t.Width - t.Keys);
                svg.Append("</g>");
                y += f.Height;
            }
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }
}
