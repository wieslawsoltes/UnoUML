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
        internal int Width => Keys + Values + 24;
        internal int Height => 22 + Fields.Count * 24;
    }

    private readonly record struct TokenSpan(int Start, int Length, int Line, string Kind);
    private readonly record struct Field(string Key, string Value, int SourceLine, TokenSpan KeyToken = default, TokenSpan ValueToken = default);

    internal static string? RenderJson(string source)
    {
        int header = source.IndexOf("@startjson", StringComparison.Ordinal);
        int firstBreak = source.AsSpan(header).IndexOfAny('\r', '\n');
        if (firstBreak < 0) return null;
        int begin = header + firstBreak + 1;
        if (source[begin-1] == '\r' && begin < source.Length && source[begin] == '\n') begin++;
        int end = source.LastIndexOf("@endjson", StringComparison.Ordinal);
        if (end < begin || !source.AsSpan(end + 8).Trim().IsEmpty) return null;
        int lastLine = source.AsSpan(0,end).LastIndexOfAny('\r','\n') + 1;
        if (!source.AsSpan(lastLine, end-lastLine).Trim().IsEmpty) return null;
        var bytes = Encoding.UTF8.GetBytes(source[begin..end]);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var tables = new List<Table>();
        var stack = new Stack<int>();
        int line = 0, mark = 0, offset = begin, tokens = 0;
        foreach (var unused in source.AsSpan(0,begin).EnumerateLines()) line++;
        line--; // the header slice ends in a newline
        string? key = null;
        TokenSpan keyToken = default;
        try
        {
            while (reader.Read())
            {
                if (++tokens > 12000 || reader.ValueSpan.Length > 6 * 2048) return null;
                int pos = (int)reader.TokenStartIndex;
                var consumed = bytes.AsSpan(mark,pos-mark);
                int chars = Encoding.UTF8.GetCharCount(consumed);
                var segment = source.AsSpan(offset,chars);
                line += segment.Count('\n') + segment.Count('\r') - segment.Count("\r\n");
                offset += chars; mark = pos;
                var type = reader.TokenType;
                if (type == JsonTokenType.PropertyName)
                {
                    key = reader.GetString()!;
                    if (!ValidDataText(key)) return null;
                    keyToken = new(offset,Encoding.UTF8.GetCharCount(reader.ValueSpan)+2,line,"key");
                    continue;
                }
                if (type is JsonTokenType.EndArray or JsonTokenType.EndObject) { stack.Pop(); continue; }
                bool container = type is JsonTokenType.StartArray or JsonTokenType.StartObject;
                int parent = stack.TryPeek(out int current) ? current : -1;
                string value = type switch {
                    JsonTokenType.StartObject => "{}", JsonTokenType.StartArray => "[]",
                    JsonTokenType.String => reader.GetString()!,
                    _ => Encoding.UTF8.GetString(reader.ValueSpan)
                };
                if (!ValidDataText(value)) return null;
                int row = 0;
                if (parent >= 0 || !container)
                {
                    if (parent < 0) { if (tables.Count > 0) return null; tables.Add(new(0,-1,-1) { Caption = "JSON" }); }
                    var owner = tables[parent < 0 ? 0 : parent];
                    row = owner.Fields.Count;
                    string name = key ?? (parent < 0 ? "value" : row.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    TokenSpan token = container ? default : type == JsonTokenType.String
                        ? new(offset,Encoding.UTF8.GetCharCount(reader.ValueSpan)+2,line,"string")
                        : new(offset,reader.ValueSpan.Length,line,"scalar");
                    owner.Fields.Add(new(name, value, key != null ? keyToken.Line : line, keyToken, token));
                    owner.Keys = Math.Max(owner.Keys, SvgText.Measure(name));
                    owner.Values = Math.Max(owner.Values, SvgText.Measure(value));
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
        return Render(tables);
    }

    // Escapes can decode to XML-forbidden characters even when the original file is valid XML text.
    private static bool ValidDataText(string text) => text.Length <= 2048 && !text.Any(char.IsControl) &&
        text.AsSpan().IndexOfAny('\uFFFE','\uFFFF') < 0 && SvgText.IsPlain(text);

    private static void Cell(StringBuilder svg, string value, TokenSpan token, int x, int y)
    {
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\"");
        if (token.Length > 0)
        {
            svg.Append(CultureInfo.InvariantCulture, $" data-json-start=\"{token.Start}\" data-json-length=\"{token.Length}\" data-json-kind=\"{token.Kind}\" data-source-line=\"{token.Line}\"");
        }
        // Empty strings and keys still need a visible, keyboard/pointer-accessible edit target.
        svg.Append('>').Append(value.Length == 0 && token.Length > 0 ? "&#34;&#34;" : SvgText.Escape(value)).Append("</text>");
    }

    private static string? Render(List<Table> tables)
    {
        const int gap = 14;
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
        int left = 10, height = 20 + tables[0].Span;
        for (int i = 0; i < widths.Length; i++) { int width = widths[i]; if (width == 0) break; widths[i] = left; left += width + 48; }
        if (left > 32768 || height > 32768) return null;
        var svg = TreeDiagram.Canvas(left - 38, height);
        foreach (var t in tables)
        {
            t.X = widths[t.Depth];
            if (t.Parent >= 0)
            {
                var p = tables[t.Parent]; t.Top = p.ChildCursor; p.ChildCursor += t.Span + gap;
            }
            else t.Top = 10;
            t.Y = t.Top + (t.Span - t.Height) / 2;
            t.ChildCursor = t.Top + (t.Span - t.Children) / 2;
            if (t.Parent >= 0)
            {
                var p = tables[t.Parent];
                int x = p.X + p.Width, y = p.Y + 34 + t.Row * 24, dest = t.Y + 11;
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x} {y} C{x+26} {y} {t.X-20} {dest} {t.X} {dest}\" fill=\"none\" stroke=\"#77778e\"/>");
            }
        }
        foreach (var t in tables)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{t.X}\" y=\"{t.Y}\" width=\"{t.Width}\" height=\"{t.Height}\" rx=\"4\" fill=\"#fafaff\" stroke=\"#77778e\"/><text x=\"{t.X+8}\" y=\"{t.Y+16}\" font-weight=\"bold\">{t.Caption}</text>");
            int y = t.Y + 22;
            foreach (var f in t.Fields)
            {
                svg.Append(CultureInfo.InvariantCulture, $"<g data-source-line=\"{f.SourceLine}\"><path d=\"M{t.X} {y}h{t.Width}\" stroke=\"#d8d8e1\"/>");
                Cell(svg,f.Key,f.KeyToken,t.X+8,y+17);
                Cell(svg,f.Value,f.ValueToken,t.X+t.Keys+16,y+17);
                svg.Append("</g>");
                y += 24;
            }
        }
        return svg.Append("</svg>").ToString();
    }
}
