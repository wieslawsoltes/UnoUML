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

    private readonly record struct Field(string Key, string Value, int SourceLine);

    internal static string? RenderJson(string source)
    {
        int first = source.IndexOf("@startjson", StringComparison.Ordinal);
        int begin = source.IndexOf('\n', first);
        int end = source.LastIndexOf("@endjson", StringComparison.Ordinal);
        if (begin < 0 || end <= begin || !source.AsSpan(end + 8).Trim().IsEmpty) return null;
        int lastLine = source.LastIndexOf('\n', end) + 1;
        if (!source.AsSpan(lastLine, end - lastLine).Trim().IsEmpty) return null;
        var bytes = Encoding.UTF8.GetBytes(source[(begin+1)..end]);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var tables = new List<Table>();
        var stack = new Stack<int>();
        int line = source.AsSpan(0, begin + 1).Count('\n'), mark = 0, tokens = 0, keyLine = -1;
        string? key = null;
        try
        {
            while (reader.Read())
            {
                if (++tokens > 12000) return null;
                int pos = checked((int)reader.TokenStartIndex);
                line += bytes.AsSpan(mark, pos - mark).Count((byte)'\n'); mark = pos;
                var type = reader.TokenType;
                if (type == JsonTokenType.PropertyName) { key = Printable(reader.GetString()!); keyLine = line; continue; }
                if (type is JsonTokenType.EndArray or JsonTokenType.EndObject) { stack.Pop(); continue; }
                bool container = type is JsonTokenType.StartArray or JsonTokenType.StartObject;
                int parent = stack.TryPeek(out int current) ? current : -1;
                string value = type switch {
                    JsonTokenType.StartObject => "{}", JsonTokenType.StartArray => "[]",
                    JsonTokenType.String => Printable(reader.GetString()!),
                    _ => Encoding.UTF8.GetString(reader.ValueSpan)
                };
                if (value.Length > 2048 || key?.Length > 2048 || !SvgText.IsPlain(value) || key != null && !SvgText.IsPlain(key)) return null;
                int row = 0;
                if (parent >= 0 || !container)
                {
                    if (parent < 0) { if (tables.Count > 0) return null; tables.Add(new(0,-1,-1) { Caption = "JSON" }); }
                    var owner = tables[parent < 0 ? 0 : parent];
                    row = owner.Fields.Count;
                    string name = key ?? (parent < 0 ? "value" : row.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    owner.Fields.Add(new(name, value, key != null ? keyLine : line));
                    owner.Keys = Math.Max(owner.Keys, SvgText.Measure(name));
                    owner.Values = Math.Max(owner.Values, SvgText.Measure(value));
                }
                if (container)
                {
                    int index = tables.Count;
                    tables.Add(new(stack.Count, parent, row) { Caption = value });
                    stack.Push(index);
                }
                key = null;
            }
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; } // Invalid Unicode escape sequences.
        if (tables.Count == 0) return null;
        return Render(tables);
    }

    private static string Printable(string text)
    {
        if (!text.Any(char.IsControl)) return text;
        return string.Concat(text.Select(c => c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
            _ when char.IsControl(c) => "\\u" + ((int)c).ToString("X4"), _ => c.ToString() }));
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
                svg.Append($"<path d=\"M{x} {y} C{x+26} {y} {t.X-20} {dest} {t.X} {dest}\" fill=\"none\" stroke=\"#77778e\"/>");
            }
        }
        foreach (var t in tables)
        {
            svg.Append($"<rect x=\"{t.X}\" y=\"{t.Y}\" width=\"{t.Width}\" height=\"{t.Height}\" rx=\"4\" fill=\"#fafaff\" stroke=\"#77778e\"/><text x=\"{t.X+8}\" y=\"{t.Y+16}\" font-weight=\"bold\">{t.Caption}</text>");
            int y = t.Y + 22;
            foreach (var f in t.Fields)
            {
                svg.Append($"<g data-source-line=\"{f.SourceLine}\"><path d=\"M{t.X} {y}h{t.Width}\" stroke=\"#d8d8e1\"/><text x=\"{t.X+8}\" y=\"{y+17}\">{SvgText.Escape(f.Key)}</text><text x=\"{t.X+t.Keys+16}\" y=\"{y+17}\">{SvgText.Escape(f.Value)}</text></g>");
                y += 24;
            }
        }
        return svg.Append("</svg>").ToString();
    }
}
