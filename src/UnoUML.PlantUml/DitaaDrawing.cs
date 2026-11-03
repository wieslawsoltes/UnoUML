using System.Globalization;
using System.Numerics;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class DitaaRenderer
{
    private const int CellW = 10, CellH = 14;
    private static int Px(int x) => (x + 2) * CellW + CellW / 2;
    private static int Py(int y) => (y + 2) * CellH + CellH / 2;
    private static string XY(int x, int y) => FormattableString.Invariant($"{x} {y}");

    private static string Draw(Grid g)
    {
        int width = (g.Columns + 4) * CellW, height = (g.Rows + 4) * CellH;
        var s = new StringBuilder(g.Faces.Count * 320 + 512);
        s.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"monospace\"><rect width=\"{width}\" height=\"{height}\" fill=\"white\"/>");
        var outlines = g.Faces.Select(ShapePath).ToList();
        // Shadows are background decoration. Shared borders remain crisp and selectable.
        for (int i = 0; i < g.Faces.Count; i++) s.Append("<path d=\"").Append(outlines[i]).Append("\" transform=\"translate(3 3)\" fill=\"#d3d3d3\"/>");
        for (int i = 0; i < g.Faces.Count; i++)
        {
            var f = g.Faces[i];
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"entity\" data-source-line=\"{g.SourceLine + f.MinY}\" data-source-column=\"{f.MinX}\" data-ditaa-shape=\"{(f.Tag == "" ? "polygon" : f.Tag)}\"><title>{(f.Tag == "" ? "Shape" : f.Tag)}</title><path d=\"{outlines[i]}\" fill=\"{f.Fill}\" stroke=\"#111111\" stroke-width=\"1.5\" stroke-linejoin=\"round\"{(f.Dashed ? " stroke-dasharray=\"6 4\"" : "")}/>");
            if (f.Tag == "s")
            {
                int x = Px(f.MinX), r = Px(f.MaxX), y = Py(f.MinY) + 6;
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x} {y}C{x} {y + 8} {r} {y + 8} {r} {y}\" fill=\"none\" stroke=\"#111111\"/>");
            }
            s.Append("</g>");
        }
        DrawWires(s, g);
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Columns; x++)
            {
                int i = g.Index(x, y);
                if (g.Adj[i] == 0)
                {
                    if (g.Cells[i] == ' ') continue;
                    int end = x + 1, last = end, owner = g.Owner[i];
                    while (end < g.Columns)
                    {
                        int next = g.Index(end, y);
                        if (g.Adj[next] != 0 || g.Owner[next] != owner || (g.Cells[next] == ' ' && end > last)) break;
                        if (g.Cells[next] != ' ') last = end + 1;
                        end++;
                    }
                    string text = new(g.Cells, i, last - x);
                    string color = owner >= 0 && IsDark(g.Faces[owner].Fill) ? "#ffffff" : "#111111";
                    string alias = owner < 0 ? "" : FormattableString.Invariant($" data-qualified-name=\"ditaa:{g.SourceLine + y}:{x}\"");
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"ditaa-label\"{alias} data-source-line=\"{g.SourceLine + y}\" data-source-column=\"{x}\"><title>{SvgText.Escape(text)}</title><text x=\"{Px(x) - CellW / 2}\" y=\"{Py(y) + 5}\" font-size=\"13\" textLength=\"{text.Length * CellW}\" lengthAdjust=\"spacingAndGlyphs\" fill=\"{color}\" xml:space=\"preserve\">{SvgText.Escape(text)}</text></g>");
                    x = last - 1; continue;
                }
                if (g.Cells[i] == '*') s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{Px(x)}\" cy=\"{Py(y)}\" r=\"3.4\" fill=\"white\" stroke=\"#111111\"/>");
            }
        return s.Append("</svg>").ToString();
    }

    private static bool IsDark(string fill)
    {
        int value = int.Parse(fill.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((value >> 16) * 299 + ((value >> 8) & 255) * 587 + (value & 255) * 114) < 128000;
    }

    private static string ShapePath(Face f)
    {
        int x = Px(f.MinX), r = Px(f.MaxX), y = Py(f.MinY), b = Py(f.MaxY), w = r - x, h = b - y, cx = (x + r) / 2, cy = (y + b) / 2;
        int slant = Math.Min(12, w / 4), wave = Math.Min(8, h / 3), lip = Math.Min(6, h / 4);
        return f.Tag switch
        {
            "s" => FormattableString.Invariant($"M{x} {y + lip}C{x} {y - lip} {r} {y - lip} {r} {y + lip}V{b - lip}C{r} {b + lip} {x} {b + lip} {x} {b - lip}Z"),
            "d" => FormattableString.Invariant($"M{x} {y}H{r}V{b - wave}C{cx} {b - 3 * wave} {cx} {b + wave} {x} {b - wave}Z"),
            "io" => FormattableString.Invariant($"M{x + slant} {y}H{r}L{r - slant} {b}H{x}Z"),
            "mo" => FormattableString.Invariant($"M{x} {y}H{r}L{r - slant} {b}H{x + slant}Z"),
            "tr" => FormattableString.Invariant($"M{x + slant} {y}H{r - slant}L{r} {b}H{x}Z"),
            "c" => FormattableString.Invariant($"M{cx} {y}L{r} {cy}L{cx} {b}L{x} {cy}Z"),
            "o" => FormattableString.Invariant($"M{x} {cy}A{w / 2} {h / 2} 0 1 0 {r} {cy}A{w / 2} {h / 2} 0 1 0 {x} {cy}Z"),
            _ => PolylinePath(f.Corners, true)
        };
    }

    private static string PolylinePath(List<Vertex> corners, bool closed)
    {
        var s = new StringBuilder(corners.Count * 14);
        for (int i = 0; i < corners.Count; i++)
        {
            var v = corners[i]; int x = Px(v.X), y = Py(v.Y);
            if (!v.Round || !closed && (i == 0 || i == corners.Count - 1)) { s.Append(i == 0 ? 'M' : 'L').Append(XY(x, y)); continue; }
            var prev = corners[(i + corners.Count - 1) % corners.Count]; var next = corners[(i + 1) % corners.Count];
            int radius = Math.Min(6, Math.Min(Math.Abs(Px(prev.X) - x) + Math.Abs(Py(prev.Y) - y), Math.Abs(Px(next.X) - x) + Math.Abs(Py(next.Y) - y)) / 2);
            s.Append(i == 0 ? 'M' : 'L').Append(XY(x + Math.Sign(prev.X - v.X) * radius, y + Math.Sign(prev.Y - v.Y) * radius));
            s.Append('Q').Append(XY(x, y)).Append(' ').Append(XY(x + Math.Sign(next.X - v.X) * radius, y + Math.Sign(next.Y - v.Y) * radius));
        }
        if (closed) s.Append('Z'); return s.ToString();
    }

    private static void DrawWires(StringBuilder s, Grid g)
    {
        var used = new byte[g.Cells.Length];
        for (int from = 0; from < used.Length; from++)
        {
            if (g.Bridges[from] == 0 || (g.Bridges[from] == g.Adj[from] && BitOperations.PopCount(g.Bridges[from]) == 2)) continue;
            for (int d = 0; d < 4; d++)
            {
                if ((g.Bridges[from] & ~used[from] & (1 << d)) == 0) continue;
                var vertices = new List<Vertex> { new(g.X(from), g.Y(from), false) };
                bool dashed = g.Cells[from] is '=' or ':'; int i = from, way = d;
                while (true)
                {
                    int j = i + g.Step[way], back = (way + 2) % 4;
                    used[i] |= (byte)(1 << way); used[j] |= (byte)(1 << back);
                    dashed |= g.Cells[j] is '=' or ':';
                    int remaining = g.Bridges[j] & ~used[j];
                    if (g.Adj[j] != g.Bridges[j] || BitOperations.PopCount(g.Bridges[j]) != 2 || remaining == 0)
                    { vertices.Add(new(g.X(j), g.Y(j), false)); i = j; break; }
                    int next = BitOperations.TrailingZeroCount((uint)remaining);
                    if (way != next) vertices.Add(new(g.X(j), g.Y(j), g.Cells[j] is '/' or '\\'));
                    i = j; way = next;
                }
                string path = PolylinePath(vertices, false);
                s.Append(CultureInfo.InvariantCulture, $"<g class=\"link\" data-source-line=\"{g.SourceLine + g.Y(from)}\" data-source-column=\"{g.X(from)}\"><title>Connector</title><path d=\"{path}\" fill=\"none\" stroke=\"transparent\" stroke-width=\"9\"/><path d=\"{path}\" fill=\"none\" stroke=\"#111111\" stroke-width=\"1.6\" stroke-linejoin=\"round\"{(dashed ? " stroke-dasharray=\"6 4\"" : "")}/>");
                Arrow(s, g, from); Arrow(s, g, i); s.Append("</g>");
            }
        }
    }

    private static void Arrow(StringBuilder s, Grid g, int i)
    {
        int dir = g.Cells[i] switch { '^' => 0, '>' => 1, 'v' or 'V' => 2, '<' => 3, _ => -1 };
        if (dir < 0) return;
        int dx = dir == 1 ? 1 : dir == 3 ? -1 : 0, dy = dir == 2 ? 1 : dir == 0 ? -1 : 0;
        int x = Px(g.X(i)), y = Py(g.Y(i)), next = i + g.Step[dir];
        // An arrow immediately beside a perpendicular shape wall terminates at its edge.
        int tip = (g.Adj[next] & ~g.Bridges[next]) != 0 ? (dx != 0 ? CellW : CellH) : 4;
        int tx = x + tip * dx, ty = y + tip * dy;
        s.Append(CultureInfo.InvariantCulture, $"<path class=\"arrowhead\" d=\"M{tx} {ty}l{-10 * dx - 4 * dy} {-10 * dy + 4 * dx}l{8 * dy} {-8 * dx}Z\" fill=\"#111111\"/>");
    }
}
