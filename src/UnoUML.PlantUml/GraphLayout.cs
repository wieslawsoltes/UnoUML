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
            a.Parent!.Arcs.Add(new(slots[a], slots[b], direction, edge.Hidden));
        }
        foreach (var container in graph.Nodes.Prepend(graph.Root))
        {
            // A visual placement command adds a hidden ordering relation. Keep connectors visible
            // but do not let their default rank fight an explicit constraint on the same pair.
            if (!container.Arcs.Any(arc => arc.Hidden)) continue;
            var constrained = container.Arcs.Where(arc => arc.Hidden).Select(arc => (Math.Min(arc.A, arc.B), Math.Max(arc.A, arc.B))).ToHashSet();
            container.Arcs.RemoveAll(arc => !arc.Hidden && constrained.Contains((Math.Min(arc.A, arc.B), Math.Max(arc.A, arc.B))));
        }
        MeasureNode(graph.Root, graph);
        OffsetChildren(graph.Root, 32, graph.Title.Length == 0 ? 24 : 64);
        return graph.Root.Width < 32000 && graph.Root.Height < 32000;
    }

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
        if (node.IsStatePort) { node.Width = node.Height = 12; return; }
        int textWidth = SvgText.Measure(node.Label);
        int labelWidth = Math.Max(textWidth, node.Stereotype.Length == 0 ? 0 : SvgText.Measure("«" + node.Stereotype + "»", 12));
        node.Width = Math.Max(68, labelWidth + 34);
        node.Height = node.Stereotype.Length == 0 ? 40 : 58;
        if (node.IsType || node.Kind == "state" && node.Members.Count > 0)
        {
            if (!graph.HideCircle && node.Kind is not ("object" or "map") && node.IsType) node.Width += 24;
            foreach (var member in node.Members) node.Width = Math.Max(node.Width, SvgText.Measure(member.Text, 13) + 28);
            if (node.Members.Count != 0) node.Height += 10 + 20 * node.Members.Count;
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
                keyWidth = Math.Max(keyWidth, SvgText.Measure(properties[0], 13));
                valueWidth = Math.Max(valueWidth, SvgText.Measure(properties[1], 13));
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
        foreach (var arc in arcs)
        {
            int a = local[arc.A], b = local[arc.B];
            if (arc.Direction is "left" or "up") (a, b) = (b, a);
            if (arc.Direction is "left" or "right") { peers.Merge(a, b); lateral.Add((a, b)); }
            else vertical.Add((a, b));
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
        const int gap = 52;
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
            main += thickness + (level + 1 < layers.Count ? 85 : 0);
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
