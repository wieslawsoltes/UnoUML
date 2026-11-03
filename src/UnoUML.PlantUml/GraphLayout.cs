namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private static bool Arrange(Graph graph)
    {
        // Project each relation into the nearest common container; nested layouts only see their own direct children.
        var slots = new Dictionary<Node, int>();
        foreach (var node in graph.Nodes.Prepend(graph.Root))
            for (int i = 0; i < node.Children.Count; i++) slots[node.Children[i]] = i;
        foreach (var edge in graph.Edges)
        {
            if (edge.From == edge.To) continue;
            Node a = edge.From, b = edge.To;
            int da = Depth(a), db = Depth(b);
            while (da > db) { a = a.Parent!; da--; }
            while (db > da) { b = b.Parent!; db--; }
            if (a == b) return false; // A connector from a container to its own descendant needs compound routing.
            while (a.Parent != b.Parent) { a = a.Parent!; b = b.Parent!; }
            string direction = edge.Direction;
            if (direction.Length == 0) direction = edge.Arrow.EndsWith("|>", StringComparison.Ordinal) ? "up" :
                edge.Arrow is "-" or "->" or "<-" or "<->" ? "right" : "down";
            int labelWidth = 0, labelHeight = 0;
            if (!edge.Hidden && edge.Caption.Length != 0)
            {
                int size = graph.Skin.Size("arrowfontsize", 13), maximum = graph.Skin.Number("arrow", "maximumwidth", 0);
                var padding = StyleSpacing.Parse(graph.Skin.Style("arrow", "padding", "0"));
                var margin = StyleSpacing.Parse(graph.Skin.Style("arrow", "margin", "0"));
                var rows = StyleText.Wrap(edge.Caption, size, maximum == 0 ? 0 : Math.Max(size, maximum - padding.Horizontal));
                labelWidth = rows.Max(row => SvgText.Measure(row, size)) + padding.Horizontal + margin.Horizontal;
                labelHeight = StyleText.Height(rows, size) + padding.Vertical + margin.Vertical;
            }
            a.Parent!.Arcs.Add(new(slots[a], slots[b], direction, edge.Hidden, labelWidth, labelHeight));
        }
        foreach (var container in graph.Nodes.Prepend(graph.Root))
        {
            // A visual placement command adds a hidden ordering relation. Keep connectors visible
            // but do not let their default rank fight an explicit constraint on the same pair.
            if (!container.Arcs.Any(arc => arc.Hidden)) continue;
            var constrained = container.Arcs.Where(arc => arc.Hidden).Select(arc => (Math.Min(arc.A, arc.B), Math.Max(arc.A, arc.B))).ToHashSet();
            container.Arcs.RemoveAll(arc => !arc.Hidden && constrained.Contains((Math.Min(arc.A, arc.B), Math.Max(arc.A, arc.B))));
        }
        graph.Heading = StyleTitle.Measure(graph.Title, graph.Skin, 19);
        MeasureNode(graph.Root, graph);
        OffsetChildren(graph.Root, 32, graph.Heading == null ? 24 : 45 + graph.Heading.Height);
        ApplyMargins(graph.Root, 0, 0);
        return graph.Root.Width < 32000 && graph.Root.Height < 32000;
    }

    private static void ApplyMargins(Node parent, int dx, int dy)
    {
        foreach (var node in parent.Children)
        {
            node.X += dx + node.Margin.Left;
            node.Y += dy + node.Margin.Top;
            node.Width -= node.Margin.Horizontal;
            node.Height -= node.Margin.Vertical;
            ApplyMargins(node, dx + node.Margin.Left, dy + node.Margin.Top);
        }
    }

    private static string StyleCategory(Node node) => node.IsType && node.Kind is not ("object" or "map") ? "class" :
        node.Kind == "namespace" ? "package" : node.Kind == "initial" || node.Kind == "state" && node.Stereotype == "start" ? "circle start" :
        node.Kind == "final" || node.Kind == "state" && node.Stereotype == "end" ? "circle end" : node.Kind == "state" && node.Stereotype == "choice" ? "diamond" : node.Kind;

    private static string SpotCategory(string kind) => "spot spot" + (kind is "abstract" or "abstract class" ? "abstractclass" : kind);

    private static int Depth(Node node)
    {
        int depth = 0;
        while (node.Parent != null) { depth++; node = node.Parent; }
        return depth;
    }

    private static void OffsetChildren(Node parent, int dx, int dy)
    {
        foreach (var node in parent.Children)
        {
            node.X += dx; node.Y += dy;
            OffsetChildren(node, node.X, node.Y);
        }
    }

    private static void MeasureNode(Node node, Graph graph)
    {
        // Library renderers measure their own card contents and reserve room for relationship labels.
        if (node.Kind == "measured-card") return;
        if (node.Kind == "measured-group") { MeasureChildren(node, graph); return; }
        if (node.IsStatePort) { node.Width = node.Height = 12; return; }
        string scope = StyleCategory(node);
        node.LabelSize = graph.Skin.Number(scope, "fontsize", 14, node.Stereotype, node.IsType ? "header" : "");
        node.MemberSize = graph.Skin.Number(scope, "fontsize", 13, node.Stereotype, "body");
        node.StereotypeSize = graph.Skin.Number(scope, "fontsize", 12, node.Stereotype, "stereotype");
        node.Padding = StyleSpacing.Parse(graph.Skin.Style(scope, "padding", "13 17", node.Stereotype));
        node.Margin = node == graph.Root ? default : StyleSpacing.Parse(graph.Skin.Style(scope, "margin", "0", node.Stereotype));
        int maximum = graph.Skin.Number(scope, "maximumwidth", 0, node.Stereotype);
        node.LabelRows = StyleText.Wrap(node.Label, node.LabelSize, maximum == 0 ? 0 : Math.Max(node.LabelSize, maximum - node.Padding.Horizontal));
        string fontStyle = graph.Skin.Style(scope, "fontstyle", "normal", node.Stereotype);
        int textWidth = node.LabelRows.Max(row => StyleText.Measure(row, node.LabelSize, fontStyle));
        bool showStereotype = node.Stereotype.Length != 0 && graph.Skin.Style(scope, "showstereotype", "true", node.Stereotype) != "false";
        int labelWidth = Math.Max(textWidth, showStereotype ? SvgText.Measure(ManagedSkin.StereotypeLabel(node.Stereotype), node.StereotypeSize) : 0);
        node.Width = Math.Max(graph.Skin.Number(scope, "minimumwidth", 68, node.Stereotype), labelWidth + node.Padding.Horizontal + (node.Kind == "archimate" ? 24 : 0));
        node.LabelHeight = StyleText.Height(node.LabelRows, node.LabelSize);
        node.HeaderHeight = node.LabelHeight + node.Padding.Vertical + (showStereotype ? node.StereotypeSize + 6 : 0);
        node.MemberHeight = node.MemberSize + 7;
        node.Height = node.HeaderHeight;
        if (node.IsType || node.Kind == "state" && node.Members.Count > 0)
        {
            if (!graph.HideCircle && node.Kind is not ("object" or "map") && node.IsType)
            {
                node.SpotSize = graph.Skin.Number(SpotCategory(node.Kind), "fontsize", 12, node.Stereotype);
                node.SpotRadius = graph.Skin.CircledCharacterRadius;
                node.Width += node.SpotRadius * 2 + 4;
                node.HeaderHeight = Math.Max(node.HeaderHeight, node.SpotRadius * 2 + Math.Min(20, node.Padding.Vertical));
                node.Height = node.HeaderHeight;
            }
            node.MemberRows = node.Members.Select(m => node.Kind == "map" ? [m.Text] :
                StyleText.Wrap(graph.Skin.VisibilityIconSize > 0 && m.Text.Length > 0 && m.Text[0] is '+' or '-' or '#' or '~' ? m.Text[1..].TrimStart() : m.Text, node.MemberSize, maximum == 0 ? 0 : Math.Max(node.MemberSize, maximum - Math.Max(28, node.Padding.Horizontal)))).ToArray();
            node.MemberHeights = node.MemberRows.Select(rows => Math.Max(StyleText.Height(rows, node.MemberSize), graph.Skin.VisibilityIconSize) + 7).ToArray();
            foreach (var rows in node.MemberRows)
                node.Width = Math.Max(node.Width, rows.Max(row => StyleText.Measure(row, node.MemberSize, graph.Skin.Style(scope, "fontstyle", "normal", node.Stereotype, "body"))) + Math.Max(28, node.Padding.Horizontal) + graph.Skin.VisibilityIconSize);
            if (node.Members.Count != 0) node.Height += 10 + node.MemberHeights.Sum();
        }
        else if (node.Kind is "actor" or "actor/") node.Height += 44;
        else if (node.Kind is "database" or "artifact" or "package" or "folder" or "node") node.Height += 12;
        else if (node.Kind is "initial" or "final") node.Width = node.Height = 20;
        else if (node.Kind is "usecase" or "usecase/" or "hexagon" or "process") node.Width += 28;
        if (node.Kind is "cloud" or "person" or "stack" or "frame") { node.Height += 14; node.Width += 12; }
        if (node.Kind is "boundary" or "control" or "circle" || node.DeploymentGlyph) node.Height += 18;
        if (node.Kind == "card") node.Height -= 14;
        if (node.Kind == "label") node.Height -= 12;
        if (node.Kind == "map" && node.Members.Count != 0)
        {
            int keyWidth = 0, valueWidth = 0;
            foreach (var (_, text) in node.Members)
            {
                var properties = text.Split("=>", StringSplitOptions.TrimEntries);
                keyWidth = Math.Max(keyWidth, SvgText.Measure(properties[0], node.MemberSize));
                valueWidth = Math.Max(valueWidth, SvgText.Measure(properties[1], node.MemberSize));
            }
            node.Width = Math.Max(node.Width, keyWidth + valueWidth + 44);
        }
        if (node.IsSpecial)
        {
            (node.Width, node.Height) = node.Stereotype switch
            {
                "choice" => (32, 28),
                "fork" or "join" => graph.Horizontal ? (7, 70) : (70, 7),
                "history" or "history*" => (30, 30),
                _ => (20, 20)
            };
        }
        MeasureChildren(node, graph);
        node.Width += node.Margin.Horizontal;
        node.Height += node.Margin.Vertical;
    }

    private static void MeasureChildren(Node node, Graph graph)
    {
        if (node.Children.Count == 0) return;
        foreach (var child in node.Children) MeasureNode(child, graph);

        bool hasPorts = node.Kind == "state" && node.Children.Any(child => child.IsStatePort);
        var idsToPlace = Enumerable.Range(0, node.Children.Count).Where(i => !hasPorts || !node.Children[i].IsStatePort);
        var layoutArcs = hasPorts ? node.Arcs.Where(a => !node.Children[a.A].IsStatePort && !node.Children[a.B].IsStatePort) : node.Arcs;
        var connected = new Disjoint(node.Children.Count);
        foreach (var arc in layoutArcs) connected.Merge(arc.A, arc.B);
        var components = idsToPlace.GroupBy(connected.Root).Select(g => g.ToArray()).ToArray();
        var arcs = layoutArcs.ToLookup(a => connected.Root(a.A));
        var boxes = new List<(int[] Ids, int Width, int Height)>();
        long area = 0;
        foreach (int[] ids in components)
        {
            var (w, h) = Layer(node.Children, ids, arcs[connected.Root(ids[0])], graph.Horizontal);
            boxes.Add((ids, w, h));
            area += (long)(w + 44) * (h + 48);
        }
        int pad = node == graph.Root ? 0 : 20, top = node == graph.Root ? 0 : node.Height;
        int wrap = Math.Max(360, (int)Math.Sqrt(area * 1.7)), x = 0, y = 0, row = 0, right = 0;
        foreach (var box in boxes)
        {
            if (x != 0 && x + box.Width > wrap) { x = 0; y += row + 48; row = 0; }
            foreach (int id in box.Ids) { node.Children[id].X += pad + x; node.Children[id].Y += top + y; }
            right = Math.Max(right, x + box.Width); x += box.Width + 44; row = Math.Max(row, box.Height);
        }
        node.Width = Math.Max(node == graph.Root ? 0 : node.Width, 2 * pad + right);
        node.Height = top + y + row + pad;
        if (hasPorts) AttachStatePorts(node, graph.Horizontal);
    }

    private static (int Width, int Height) Layer(List<Node> nodes, int[] ids, IEnumerable<Arc> arcs, bool horizontal)
    {
        if (ids.Length == 1) return (nodes[ids[0]].Width, nodes[ids[0]].Height);
        var local = new Dictionary<int, int>(ids.Length);
        for (int i = 0; i < ids.Length; i++) local[ids[i]] = i;
        int n = ids.Length;
        var peers = new Disjoint(n);
        var vertical = new List<(int From, int To)>();
        var lateral = new List<(int From, int To)>();
        int gap = 52, rankGap = 85;
        foreach (var arc in arcs)
        {
            int a = local[arc.A], b = local[arc.B];
            if (arc.Direction is "left" or "up") (a, b) = (b, a);
            if (arc.Direction is "left" or "right")
            {
                peers.Merge(a, b); lateral.Add((a, b));
                gap = Math.Max(gap, (horizontal ? arc.LabelHeight * 2 : arc.LabelWidth) + 24);
            }
            else
            {
                vertical.Add((a, b));
                rankGap = Math.Max(rankGap, (horizontal ? arc.LabelWidth : arc.LabelHeight * 2) + 24);
            }
        }
        // Horizontal constraints identify equivalent ranks. A deterministic feedback-edge break
        // retains all edges for drawing while ensuring cyclic graphs terminate.
        var levels = Ranks(n, vertical.Select(a => (peers.Root(a.From), peers.Root(a.To))));
        var layers = Enumerable.Range(0, n).GroupBy(i => levels[peers.Root(i)]).OrderBy(g => g.Key)
            .Select(g => g.ToList()).ToList();
        var rank = new int[n]; var position = new int[n];
        var neighbors = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        foreach (var (a, b) in vertical) { neighbors[a].Add(b); neighbors[b].Add(a); }
        for (int layer = 0; layer < layers.Count; layer++)
            for (int i = 0; i < layers[layer].Count; i++) { rank[layers[layer][i]] = layer; position[layers[layer][i]] = i; }

        // Barycentric sweeps reduce crossings while preserving source order for ties.
        // Explicit sibling-order constraints are re-applied after the sweeps.
        var score = new double[n];
        for (int pass = 0; pass < 4; pass++)
        {
            bool down = pass % 2 == 0;
            for (int step = 1; step < layers.Count; step++)
            {
                int level = down ? step : layers.Count - step - 1;
                var row = layers[level];
                foreach (var id in row)
                {
                    long sum = 0; int count = 0;
                    foreach (int v in neighbors[id])
                        if (down ? rank[v] < level : rank[v] > level) { sum += position[v]; count++; }
                    score[id] = count == 0 ? position[id] : (double)sum / count;
                }
                row.Sort((a, b) => score[a] == score[b] ? position[a].CompareTo(position[b]) : score[a].CompareTo(score[b]));
                for (int i = 0; i < row.Count; i++) position[row[i]] = i;
            }
        }
        if (lateral.Count != 0)
        {
            var order = Ranks(n, lateral);
            foreach (var row in layers)
                row.Sort((a, b) => order[a] == order[b] ? position[a].CompareTo(position[b]) : order[a].CompareTo(order[b]));
        }

        int Primary(Node v) => horizontal ? v.Width : v.Height;
        int Cross(Node v) => horizontal ? v.Height : v.Width;
        var breadth = layers.Select(row => row.Sum(i => Cross(nodes[ids[i]])) + (row.Count - 1) * gap).ToArray();
        int wide = breadth.Max(), main = 0;
        for (int level = 0; level < layers.Count; level++)
        {
            int cross = (wide - breadth[level]) / 2, thickness = 0;
            foreach (int index in layers[level])
            {
                var v = nodes[ids[index]];
                (v.X, v.Y) = horizontal ? (main, cross) : (cross, main);
                thickness = Math.Max(thickness, Primary(v)); cross += Cross(v) + gap;
            }
            main += thickness + (level + 1 < layers.Count ? rankGap : 0);
        }
        return horizontal ? (main, wide) : (wide, main);
    }

    private static int[] Ranks(int n, IEnumerable<(int From, int To)> arcs)
    {
        var next = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        var indegree = new int[n]; var ranks = new int[n];
        foreach (var (a, b) in arcs) if (a != b) { next[a].Add(b); indegree[b]++; }
        // The smallest outstanding indegree picks a stable cycle break without rescanning all vertices.
        var queue = new PriorityQueue<int, (int Degree, int SourceOrder)>();
        for (int i = 0; i < n; i++) queue.Enqueue(i, (indegree[i], i));
        while (queue.TryDequeue(out var node, out var priority))
        {
            if (indegree[node] != priority.Degree) continue;
            indegree[node] = -1;
            foreach (int child in next[node])
            {
                if (indegree[child] < 0) continue;
                ranks[child] = Math.Max(ranks[child], ranks[node] + 1);
                queue.Enqueue(child, (--indegree[child], child));
            }
        }
        return ranks;
    }

    private sealed class Disjoint(int count)
    {
        private readonly int[] parents = Enumerable.Range(0, count).ToArray();
        internal int Root(int i)
        {
            while (i != parents[i]) { parents[i] = parents[parents[i]]; i = parents[i]; }
            return i;
        }
        internal void Merge(int a, int b) => parents[Root(b)] = Root(a);
    }
}
