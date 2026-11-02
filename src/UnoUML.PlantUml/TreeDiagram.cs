using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>Ordered subtree packing in two iterative passes. Depth and width are independent of the call stack.</summary>
internal static class TreeDiagram
{
    private static readonly HashSet<string> NamedFills = new(StringComparer.OrdinalIgnoreCase)
    {
        "black", "white", "gray", "grey", "silver", "red", "maroon", "orange", "yellow",
        "olive", "green", "lime", "lightgreen", "teal", "aqua", "cyan", "blue", "navy",
        "lightblue", "purple", "violet", "magenta", "pink", "brown", "gold", "lightgray",
        "darkgreen", "darkblue", "darkred", "salmon", "khaki", "lavender", "transparent"
    };

    private sealed class Node(string label, int parent, int depth, int line)
    {
        internal readonly string Label = label;
        internal readonly int Parent = parent, Level = depth, Width = SvgText.Measure(label) + 24;
        internal int Line = line, Span, Used, Start, X, Y;
        internal bool West, Boxless, Directory;
        internal string? Fill;
    }

    internal static string? TryRender(string source, DiagramKind kind)
    {
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
                if (value.Length is 3 or 6 && !value.ContainsAnyExcept("0123456789abcdefABCDEF")) color = "#" + value.ToString();
                else if (value.Length is > 0 and < 22 && NamedFills.TryGetValue(value.ToString(), out var named)) color = named;
                else return null;
                s = s[(close + 1)..];
            }
            bool boxless = !s.IsEmpty && s[0] == '_';
            if (boxless) s = s[1..];
            if (s.IsEmpty || !char.IsWhiteSpace(s[0])) return null;
            string label = s.Trim().ToString();
            if (label.Length == 0 || !SvgText.IsPlain(label)) return null;
            int parent = depthIndex == 1 ? -1 : ancestors[depthIndex - 2];
            bool west = marker == '-' || marker == '*' && (depthIndex == 2 ? left : parent >= 0 && nodes[parent].West);
            if (parent >= 0 && depthIndex > 2 && west != nodes[parent].West) return null;
            if (ancestors.Count >= depthIndex) ancestors.RemoveRange(depthIndex - 1, ancestors.Count - depthIndex + 1);
            ancestors.Add(nodes.Count);
            nodes.Add(new(label, parent, depthIndex - 1, line) { West = west, Fill = color, Boxless = boxless });
        }
        if (!ended || nodes.Count == 0) return null;
        return kind == DiagramKind.Files ? DrawFiles(nodes) : DrawOutline(nodes, kind == DiagramKind.WorkBreakdown);
    }

    private static string? DrawOutline(List<Node> nodes, bool vertical)
    {
        const int height = 32, gap = 12, margin = 16;
        int[] tracks = new int[129];
        int east = 0, west = 0;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            tracks[n.Level] = Math.Max(tracks[n.Level], vertical ? height : n.Width);
            n.Span = Math.Max(vertical ? n.Width : height, n.Used > 0 ? n.Used - gap : 0);
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
            int across = n.Start + (n.Span - (vertical ? n.Width : height)) / 2;
            int along = n.Level == 0 ? 0 : tracks[n.Level - 1] + 42;
            n.X = vertical ? across : n.West ? root.Width - along - n.Width : along;
            n.Y = vertical ? along : across;
            minX = Math.Min(minX, n.X); minY = Math.Min(minY, n.Y);
            maxX = Math.Max(maxX, n.X + n.Width); maxY = Math.Max(maxY, n.Y + height);
        }
        int width = maxX - minX + 2 * margin, totalHeight = maxY - minY + 2 * margin;
        if (width > 32768 || totalHeight > 32768) return null;
        var svg = Canvas(width, totalHeight);
        svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({margin - minX} {margin - minY})\">");
        foreach (var n in nodes)
        {
            if (n.Parent < 0) continue;
            var p = nodes[n.Parent];
            int x1 = vertical ? p.X + p.Width / 2 : n.West ? p.X : p.X + p.Width;
            int x2 = vertical ? n.X + n.Width / 2 : n.West ? n.X + n.Width : n.X;
            int y1 = p.Y + (vertical ? height : height / 2), y2 = n.Y + (vertical ? 0 : height / 2);
            string route = vertical ? FormattableString.Invariant($"V{(y1 + y2) / 2} H{x2} V{y2}") :
                FormattableString.Invariant($"C{(x1+x2)/2} {y1} {(x1+x2)/2} {y2} {x2} {y2}");
            svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x1} {y1} {route}\" fill=\"none\" stroke=\"#77778e\"/>");
        }
        foreach (var n in nodes)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<g data-source-line=\"{n.Line}\">");
            if (!n.Boxless) svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{n.X}\" y=\"{n.Y}\" width=\"{n.Width}\" height=\"32\" rx=\"{(vertical ? 2 : 9)}\" fill=\"{n.Fill ?? (n.Level == 0 ? "#e6e5ff" : "#f3f3f8")}\" stroke=\"#73738b\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{n.X+12}\" y=\"{n.Y+21}\">{SvgText.Escape(n.Label)}</text></g>");
        }
        return svg.Append("</g></svg>").ToString();
    }

    private static string? DrawFiles(List<Node> nodes)
    {
        // DFS keeps a directory contiguous even if input paths are interleaved.
        var children = new List<int>[nodes.Count + 1];
        int width = 120;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i]; (children[n.Parent + 1] ??= []).Add(i);
            width = Math.Max(width, 26 + n.Level * 18 + n.Width);
        }
        int height = 24 * nodes.Count + 20;
        if (height > 32768) return null;
        var svg = Canvas(width, height);
        var stack = new Stack<int>();
        void Push(List<int>? items) { if (items != null) for (int i = items.Count - 1; i >= 0; i--) stack.Push(items[i]); }
        Push(children[0]);
        int row = 0;
        while (stack.TryPop(out int index))
        {
            var n = nodes[index];
            int x = n.Level * 18 - 5, y = 11 + row++ * 24;
            svg.Append(n.Line < 0 ? "<g>" : $"<g data-source-line=\"{n.Line}\">");
            string path = n.Directory ? "M0 4h6l2 2h8v10H0z" : "M3 1h7l4 4v12H3z";
            svg.Append(CultureInfo.InvariantCulture, $"<path transform=\"translate({x} {y})\" d=\"{path}\" stroke=\"#73738b\" fill=\"{(n.Directory ? "#efdb9e" : "#eeeefa")}\"/><text x=\"{x+23}\" y=\"{y+15}\">{SvgText.Escape(n.Label)}</text></g>");
            Push(children[index + 1]);
        }
        return svg.Append("</svg>").ToString();
    }

    internal static StringBuilder Canvas(int width, int height) =>
        new StringBuilder().Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\" font-size=\"14\" fill=\"#27273a\">");
}
