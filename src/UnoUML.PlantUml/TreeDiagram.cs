using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>Ordered subtree packing in two iterative passes. Depth and width are independent of the call stack.</summary>
internal static class TreeDiagram
{
    private sealed class Node(string label, int parent, int depth, int line)
    {
        internal readonly string Label = label;
        internal readonly int Parent = parent, Level = depth;
        internal int Width = SvgText.Measure(label) + 24, Height = 32, FontSize = 14;
        internal string Stereotype = "", Category = "node", FontColor = "#27273a", FontName = "sans-serif", FontStyle = "normal";
        internal string[] Rows = [label];
        internal StyleSpacing Padding = new(9, 12, 9, 12), Margin;
        internal Dictionary<string, StyleSheet.Value> Paint = [], ArrowPaint = [];
        internal int OuterWidth => Width + Margin.Horizontal;
        internal int OuterHeight => Height + Margin.Vertical;
        internal int Line = line, Span, Used, Start, X, Y;
        internal bool West, Boxless, Directory;
        internal string? Fill;
        internal string CanvasId = "";
    }

    internal static string? TryRender(string source, DiagramKind kind)
    {
        if (!StyleSheet.TryExtract(source, out source, out var styles)) return null;
        var skin = new ManagedSkin { Sheet = styles, Diagram = kind switch { DiagramKind.MindMap => "mindmapdiagram", DiagramKind.WorkBreakdown => "wbsdiagram", _ => "filesdiagram" } };
        string? skinBlock = null;
        string dialect = kind switch { DiagramKind.MindMap => "mindmap", DiagramKind.WorkBreakdown => "wbs", _ => "files" };
        var nodes = new List<Node>();
        var ancestors = new List<int>();
        var paths = new Dictionary<(int Parent, string Part), int>();
        bool started = false, ended = false, left = false;
        int line = -1;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var s = raw.Trim();
            if (s.IsEmpty || s.StartsWith("'")) continue;
            if (!started) { if (!s.SequenceEqual("@start" + dialect)) return null; started = true; continue; }
            if (ended) return null;
            if (skinBlock != null || s.StartsWith("skinparam ", StringComparison.OrdinalIgnoreCase))
            {
                if (!skin.Read(s.ToString(), ref skinBlock, false, line)) return null;
                continue;
            }
            if (s.SequenceEqual("@end" + dialect)) { ended = true; continue; }
            if (nodes.Count >= 4096 || s.Length > 2048) return null;
            if (kind == DiagramKind.Files)
            {
                if (s[0] != '/' || s.StartsWith("//") || s.Contains("//", StringComparison.Ordinal)) return null;
                bool directory = s[^1] == '/';
                int current = -1, depth = 0;
                var path = s.Trim('/');
                if (path.IsEmpty) return null;
                foreach (var part in path.Split('/'))
                {
                    string name = path[part].ToString();
                    if (++depth > 128 || !SvgText.IsPlain(name) || name is "." or "..") return null;
                    bool last = part.End.Value == path.Length;
                    if (!paths.TryGetValue((current, name), out int index))
                    {
                        if (nodes.Count >= 4096) return null;
                        index = nodes.Count;
                        paths.Add((current, name), index);
                        nodes.Add(new(name, current, depth, last ? line : -1) { Directory = !last || directory });
                    }
                    else if (nodes[index].Directory != (!last || directory) || last && nodes[index].Line >= 0) return null;
                    else if (last) nodes[index].Line = line;
                    current = index;
                }
                continue;
            }
            if (kind == DiagramKind.MindMap && (s.SequenceEqual("left side") || s.SequenceEqual("right side")))
            {
                if (nodes.Count == 0) return null;
                left = s[0] == 'l'; ancestors.RemoveRange(1, ancestors.Count - 1);
                continue;
            }
            char marker = s[0];
            if (marker != '*' && (kind != DiagramKind.MindMap || marker is not ('+' or '-'))) return null;
            int depthIndex = 0;
            while (depthIndex < s.Length && s[depthIndex] == marker) depthIndex++;
            if (depthIndex > 128 || depthIndex > ancestors.Count + 1 || depthIndex == 1 && nodes.Count != 0) return null;
            s = s[depthIndex..];
            string? color = null;
            if (s.StartsWith("[#"))
            {
                int close = s.IndexOf(']');
                if (close < 0) return null;
                var value = s[2..close];
                color = ManagedSkin.Color("#" + value.ToString());
                if (color == null) return null;
                s = s[(close + 1)..];
            }
            bool boxless = !s.IsEmpty && s[0] == '_';
            if (boxless) s = s[1..];
            if (s.IsEmpty || !char.IsWhiteSpace(s[0])) return null;
            string label = s.Trim().ToString(), stereotype = "";
            int tag = label.IndexOf("<<", StringComparison.Ordinal);
            if (tag >= 0)
            {
                if (!label.EndsWith(">>", StringComparison.Ordinal)) return null;
                stereotype = label[(tag + 2)..^2].Trim('<', '>');
                label = label[..tag].TrimEnd();
                if (stereotype.Length == 0 || stereotype.Length > 160) return null;
            }
            if (label.Length == 0 || !SvgText.IsSupported(label)) return null;
            int parent = depthIndex == 1 ? -1 : ancestors[depthIndex - 2];
            bool west = marker == '-' || marker == '*' && (depthIndex == 2 ? left : parent >= 0 && nodes[parent].West);
            if (parent >= 0 && depthIndex > 2 && west != nodes[parent].West) return null;
            if (ancestors.Count >= depthIndex) ancestors.RemoveRange(depthIndex - 1, ancestors.Count - depthIndex + 1);
            ancestors.Add(nodes.Count);
            nodes.Add(new(label, parent, depthIndex - 1, line) { West = west, Fill = color, Boxless = boxless, Stereotype = stereotype });
        }
        if (!ended || skinBlock != null || nodes.Count == 0) return null;
        if (kind != DiagramKind.Files)
        {
            var occurrences = new Dictionary<(int Parent, string Label), int>();
            foreach (var node in nodes)
            {
                var key = (node.Parent, node.Label);
                int occurrence = occurrences.GetValueOrDefault(key);
                occurrences[key] = occurrence + 1;
                node.CanvasId = CanvasIdentity.Create(dialect, node.Parent < 0 ? "" : nodes[node.Parent].CanvasId,
                    node.Label, occurrence.ToString(CultureInfo.InvariantCulture));
            }
        }
        else if (nodes.Count <= 2048)
        {
            // Match the canvas node limit without restricting the native listing renderer.
            // A path is semantic identity; explicit/implicit directory declarations are equivalent.
            foreach (var node in nodes)
                node.CanvasId = CanvasIdentity.Create("files", node.Parent < 0 ? "" : nodes[node.Parent].CanvasId,
                    node.Label, node.Directory ? "directory" : "file");
        }
        MeasureStyles(nodes, skin, kind == DiagramKind.Files);
        if (kind == DiagramKind.WorkBreakdown)
            foreach (var siblings in nodes.Where(n => n.Parent >= 0).GroupBy(n => n.Parent))
                if (nodes[siblings.Key].Paint.GetValueOrDefault("width").Text == "auto")
                {
                    int width = siblings.Max(n => n.Width);
                    foreach (var child in siblings) child.Width = width;
                }
        return kind == DiagramKind.Files ? DrawFiles(nodes, skin) : DrawOutline(nodes, kind == DiagramKind.WorkBreakdown, skin);
    }

    private static void MeasureStyles(List<Node> nodes, ManagedSkin skin, bool files)
    {
        var parents = nodes.Where(n => n.Parent >= 0).Select(n => n.Parent).ToHashSet();
        for (int index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            string Category(Node n, int level, int id) => files ? (n.Directory ? "folder" : "file") :
                "node" + (level == 0 ? " rootnode" : !parents.Contains(id) ? " leafnode" : "") + (n.Boxless ? " boxless" : "");
            node.Category = Category(node, node.Level, index);
            node.Paint = new(skin.Query(node.Category, node.Stereotype, depth: node.Level), StringComparer.Ordinal);
            node.ArrowPaint = new(skin.Query("arrow", node.Stereotype, depth: node.Level), StringComparer.Ordinal);
            int ancestor = index, rank = 0;
            while (ancestor >= 0)
            {
                var parent = nodes[ancestor];
                foreach (var (property, value) in skin.Query(Category(parent, node.Level, ancestor), parent.Stereotype, depth: node.Level, ancestorsOnly: true))
                {
                    var inherited = value with { Ancestor = rank };
                    if (!node.Paint.TryGetValue(property, out var old) || inherited.Wins(old)) node.Paint[property] = inherited;
                }
                ancestor = parent.Parent; rank--;
            }
            string Value(string property, string fallback) => node.Paint.TryGetValue(property, out var value) ? value.Text : skin.Style(node.Category, property, fallback, node.Stereotype, depth: node.Level);
            int Number(string property, int fallback) => (int)Math.Ceiling(double.Parse(Value(property, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture));
            node.FontSize = Number("fontsize", 14);
            node.FontColor = Value("fontcolor", skin.Get("defaultfontcolor", "#27273a"));
            node.FontName = Value("fontname", skin.Get("defaultfontname", "sans-serif"));
            node.FontStyle = Value("fontstyle", "normal");
            node.Padding = StyleSpacing.Parse(Value("padding", files ? "5 12" : "9 12"));
            node.Margin = StyleSpacing.Parse(Value("margin", "0"));
            int maximum = Number("maximumwidth", 0);
            node.Rows = StyleText.Wrap(node.Label, node.FontSize, maximum <= 0 ? 0 : Math.Max(node.FontSize, maximum - node.Padding.Horizontal));
            node.Width = Math.Max(Number("minimumwidth", 0), node.Rows.Max(row => StyleText.Measure(row, node.FontSize, node.FontStyle)) + node.Padding.Horizontal);
            node.Height = StyleText.Height(node.Rows, node.FontSize) + node.Padding.Vertical;
        }
    }

    private static string Finish(StringBuilder svg, List<Node> nodes, ManagedSkin skin)
    {
        var paint = new Dictionary<string, Dictionary<string, StyleSheet.Value>>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
        {
            string id = i.ToString(CultureInfo.InvariantCulture);
            paint[id] = nodes[i].Paint;
            paint["edge-" + id] = nodes[i].ArrowPaint;
        }
        return StyleDrawing.Apply(svg.ToString(), skin, paint);
    }

    private static string? DrawOutline(List<Node> nodes, bool vertical, ManagedSkin skin)
    {
        const int gap = 12, margin = 16;
        int[] tracks = new int[129];
        int east = 0, west = 0;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            tracks[n.Level] = Math.Max(tracks[n.Level], vertical ? n.OuterHeight : n.OuterWidth);
            n.Span = Math.Max(vertical ? n.OuterWidth : n.OuterHeight, n.Used > 0 ? n.Used - gap : 0);
            if (n.Parent > 0) nodes[n.Parent].Used += n.Span + gap;
            else if (n.Parent == 0)
            {
                if (n.West) west += n.Span + gap; else east += n.Span + gap;
            }
        }
        var root = nodes[0];
        root.Span = Math.Max(root.Span, Math.Max(east, west) - gap);
        int westCursor = (root.Span - Math.Max(0, west - gap)) / 2;
        int eastCursor = (root.Span - Math.Max(0, east - gap)) / 2;
        for (int d = 1; d < tracks.Length; d++) tracks[d] += tracks[d - 1] + 42;
        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var n in nodes)
        {
            if (n.Parent == 0)
            {
                ref int cursor = ref (n.West ? ref westCursor : ref eastCursor);
                n.Start = cursor; cursor += n.Span + gap;
            }
            else if (n.Parent > 0)
            {
                var parent = nodes[n.Parent];
                n.Start = parent.Used; parent.Used += n.Span + gap;
            }
            n.Used = n.Start + (n.Span - Math.Max(0, n.Used - gap)) / 2;
            int across = n.Start + (n.Span - (vertical ? n.OuterWidth : n.OuterHeight)) / 2;
            int along = n.Level == 0 ? 0 : tracks[n.Level - 1] + 42;
            n.X = (vertical ? across : n.West ? root.OuterWidth - along - n.OuterWidth : along) + n.Margin.Left;
            n.Y = (vertical ? along : across) + n.Margin.Top;
            minX = Math.Min(minX, n.X - n.Margin.Left); minY = Math.Min(minY, n.Y - n.Margin.Top);
            maxX = Math.Max(maxX, n.X + n.Width + n.Margin.Right); maxY = Math.Max(maxY, n.Y + n.Height + n.Margin.Bottom);
        }
        int width = maxX - minX + 2 * margin, totalHeight = maxY - minY + 2 * margin;
        if (width > 32768 || totalHeight > 32768) return null;
        var svg = Canvas(width, totalHeight);
        Background(svg, width, totalHeight, skin);
        svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({margin - minX} {margin - minY})\">");
        for (int index = 0; index < nodes.Count; index++)
        {
            var n = nodes[index];
            if (n.Parent < 0) continue;
            var p = nodes[n.Parent];
            int x1 = vertical ? p.X + p.Width / 2 : n.West ? p.X : p.X + p.Width;
            int x2 = vertical ? n.X + n.Width / 2 : n.West ? n.X + n.Width : n.X;
            int y1 = p.Y + (vertical ? p.Height : p.Height / 2), y2 = n.Y + (vertical ? 0 : n.Height / 2);
            string route = vertical ? FormattableString.Invariant($"V{(y1 + y2) / 2} H{x2} V{y2}") :
                FormattableString.Invariant($"C{(x1+x2)/2} {y1} {(x1+x2)/2} {y2} {x2} {y2}");
            svg.Append(CultureInfo.InvariantCulture, $"<g class=\"link\" data-canvas-id=\"edge:{n.CanvasId}\" data-entity-1=\"{p.CanvasId}\" data-entity-2=\"{n.CanvasId}\" data-style-name=\"arrow\" data-style-id=\"edge-{index}\"><path d=\"M{x1} {y1} {route}\" fill=\"none\" stroke=\"#77778e\"/></g>");
        }
        for (int index = 0; index < nodes.Count; index++)
        {
            var n = nodes[index];
            svg.Append(CultureInfo.InvariantCulture, $"<g data-canvas-id=\"{n.CanvasId}\" data-source-line=\"{n.Line}\" data-style-name=\"{n.Category}\" data-style-id=\"{index}\"{(n.Fill == null ? "" : " data-style-fill=\"explicit\"")}>");
            if (!n.Boxless) svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{n.X}\" y=\"{n.Y}\" width=\"{n.Width}\" height=\"{n.Height}\" rx=\"{(vertical ? 2 : 9)}\" fill=\"{n.Fill ?? (n.Level == 0 ? "#e6e5ff" : "#f3f3f8")}\" stroke=\"#73738b\"/>");
            string alignment = n.Paint.GetValueOrDefault("horizontalalignment").Text ?? "left";
            int x = alignment == "right" ? n.X + n.Width - n.Padding.Right : alignment == "center" ? n.X + n.Width / 2 : n.X + n.Padding.Left;
            string anchor = alignment == "right" ? "end" : alignment == "center" ? "middle" : "start";
            int textTop = n.Y + n.Padding.Top + n.FontSize - 2 - SvgText.Ascent("M", n.FontSize);
            foreach (string row in n.Rows)
            {
                string rowAnchor = SvgText.Alignment(row, anchor);
                int rowX = rowAnchor == "start" ? n.X + n.Padding.Left : rowAnchor == "end" ? n.X + n.Width - n.Padding.Right : n.X + n.Width / 2;
                SvgText.Append(svg, rowX, textTop + SvgText.Ascent(row, n.FontSize), row, n.FontSize, rowAnchor, color: n.FontColor);
                textTop += SvgText.Height(row, n.FontSize) + 4;
            }
            svg.Append("</g>");
        }
        return Finish(svg.Append("</g></svg>"), nodes, skin);
    }

    private static void Background(StringBuilder svg, int width, int height, ManagedSkin skin)
    {
        string fill = skin.Get("backgroundcolor", "none");
        if (fill != "none") svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{width}\" height=\"{height}\" fill=\"{fill}\"/>");
    }

    private static string? DrawFiles(List<Node> nodes, ManagedSkin skin)
    {
        // DFS keeps a directory contiguous even if input paths are interleaved.
        var children = new List<int>[nodes.Count + 1];
        int width = 120;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i]; (children[n.Parent + 1] ??= []).Add(i);
            width = Math.Max(width, 26 + n.Level * 18 + n.OuterWidth);
        }
        int height = nodes.Sum(n => n.OuterHeight) + 20;
        if (height > 32768) return null;
        var svg = Canvas(width, height);
        Background(svg, width, height, skin);
        var stack = new Stack<int>();
        void Push(List<int>? items) { if (items != null) for (int i = items.Count - 1; i >= 0; i--) stack.Push(items[i]); }
        Push(children[0]);
        int row = 0;
        while (stack.TryPop(out int index))
        {
            var n = nodes[index];
            int x = n.Level * 18 - 17 + n.Padding.Left + n.Margin.Left, y = 6 + n.Padding.Top + row + n.Margin.Top;
            row += n.OuterHeight;
            svg.Append(CultureInfo.InvariantCulture, $"<g data-style-name=\"{n.Category}\" data-style-id=\"{index}\"{(n.CanvasId.Length == 0 ? "" : " data-canvas-id=\"" + n.CanvasId + "\"")}{(n.Line < 0 ? "" : " data-source-line=\"" + n.Line.ToString(CultureInfo.InvariantCulture) + "\"")}>");
            string path = n.Directory ? "M0 4h6l2 2h8v10H0z" : "M3 1h7l4 4v12H3z";
            svg.Append(CultureInfo.InvariantCulture, $"<path transform=\"translate({x} {y})\" d=\"{path}\" stroke=\"#73738b\" fill=\"{(n.Directory ? "#efdb9e" : "#eeeefa")}\"/>");
            int textTop = y + n.FontSize + 1 - SvgText.Ascent("M", n.FontSize);
            foreach (string text in n.Rows)
            {
                SvgText.Append(svg, x + 23, textTop + SvgText.Ascent(text, n.FontSize), text, n.FontSize, "start", color: n.FontColor);
                textTop += SvgText.Height(text, n.FontSize) + 4;
            }
            svg.Append("</g>");
            Push(children[index + 1]);
        }
        return Finish(svg.Append("</svg>"), nodes, skin);
    }

    internal static StringBuilder Canvas(int width, int height) =>
        new StringBuilder().Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\" font-size=\"14\" fill=\"#27273a\">");
}
