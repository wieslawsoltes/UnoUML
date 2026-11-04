namespace UnoUML.Core;

public static partial class CanvasLayoutEngine
{
    // Original implementations of established graph-drawing techniques: acyclic
    // layering, barycenter sweeps, spanning forests, concentric rings and bounded
    // spring relaxation. No third-party layout engine or proprietary code is used.
    private static CanvasPoint[] Arrange((double Width, double Height)[] sizes, (int From, int To)[] edges,
        CanvasLayoutSettings settings, CancellationToken cancellationToken) => settings.Algorithm switch
    {
        CanvasLayoutAlgorithm.Layered => Layered(sizes, edges, settings, cancellationToken),
        CanvasLayoutAlgorithm.Tree => Tree(sizes, edges, settings, false, cancellationToken),
        CanvasLayoutAlgorithm.Radial => Tree(sizes, edges, settings, true, cancellationToken),
        CanvasLayoutAlgorithm.Circle => Circle(sizes, settings),
        CanvasLayoutAlgorithm.Grid => Grid(sizes, settings),
        CanvasLayoutAlgorithm.ForceDirected => Force(sizes, edges, settings, cancellationToken),
        _ => throw new ArgumentException("Unsupported canvas layout algorithm.")
    };

    private static CanvasPoint[] Grid((double Width, double Height)[] sizes, CanvasLayoutSettings settings)
    {
        int columns = settings.GridColumns == 0 ? (int)Math.Ceiling(Math.Sqrt(sizes.Length)) : Math.Min(settings.GridColumns, sizes.Length);
        int rows = (sizes.Length + columns - 1) / columns;
        var widths = new double[columns]; var heights = new double[rows];
        for (int i = 0; i < sizes.Length; i++) { widths[i % columns] = Math.Max(widths[i % columns], sizes[i].Width); heights[i / columns] = Math.Max(heights[i / columns], sizes[i].Height); }
        var x = Prefix(widths, settings.NodeSpacing); var y = Prefix(heights, settings.NodeSpacing);
        return Enumerable.Range(0, sizes.Length).Select(i => new CanvasPoint(x[i % columns], y[i / columns])).ToArray();
    }

    private static CanvasPoint[] Circle((double Width, double Height)[] sizes, CanvasLayoutSettings settings)
    {
        if (sizes.Length == 1) return [new(0, 0)];
        double radius = 0, angle = Math.PI * 2 / sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
            radius = Math.Max(radius, (Diameter(sizes[i]) / 2 + Diameter(sizes[(i + 1) % sizes.Length]) / 2 + settings.NodeSpacing) / (2 * Math.Sin(angle / 2)));
        return Enumerable.Range(0, sizes.Length).Select(i => new CanvasPoint(radius * Math.Cos(angle * i - Math.PI / 2) - sizes[i].Width / 2,
            radius * Math.Sin(angle * i - Math.PI / 2) - sizes[i].Height / 2)).ToArray();
    }

    private static CanvasPoint[] Layered((double Width, double Height)[] sizes, (int From, int To)[] edges,
        CanvasLayoutSettings settings, CancellationToken cancellationToken)
    {
        int count = sizes.Length;
        var outgoing = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var incoming = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach (var edge in edges) { outgoing[edge.From].Add(edge.To); incoming[edge.To].Add(edge.From); }
        var inDegree = incoming.Select(list => list.Count).ToArray();
        var outDegree = outgoing.Select(list => list.Count).ToArray();
        var removed = new bool[count]; var left = new List<int>(); var right = new List<int>();
        // Greedy source/sink removal preserves DAG directions; cyclic cores use
        // the maximum out-minus-in score with stable ordinal-ID tie breaking.
        for (int iteration = 0; iteration < count; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int node = -1; bool sink = false;
            for (int i = 0; i < count; i++) if (!removed[i] && outDegree[i] == 0) { node = i; sink = true; break; }
            if (node < 0) for (int i = 0; i < count; i++) if (!removed[i] && inDegree[i] == 0) { node = i; break; }
            if (node < 0)
                for (int i = 0; i < count; i++) if (!removed[i] && (node < 0 || outDegree[i] - inDegree[i] > outDegree[node] - inDegree[node])) node = i;
            removed[node] = true;
            (sink ? right : left).Add(node);
            foreach (int target in outgoing[node]) if (!removed[target]) inDegree[target]--;
            foreach (int source in incoming[node]) if (!removed[source]) outDegree[source]--;
        }
        right.Reverse(); left.AddRange(right);
        var order = new int[count];
        for (int i = 0; i < count; i++) order[left[i]] = i;
        var arcs = edges.Select(edge => order[edge.From] < order[edge.To] ? edge : (From: edge.To, To: edge.From)).Distinct().ToArray();
        var forward = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var backward = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach (var edge in arcs) { forward[edge.From].Add(edge.To); backward[edge.To].Add(edge.From); }
        var rank = new int[count];
        foreach (int node in left) foreach (int next in forward[node]) rank[next] = Math.Max(rank[next], rank[node] + 1);
        var layers = Enumerable.Range(0, rank.Max() + 1).Select(layer => Enumerable.Range(0, count).Where(node => rank[node] == layer).ToList()).ToArray();
        var positions = new int[count];
        void UpdatePositions() { foreach (var layer in layers) for (int i = 0; i < layer.Count; i++) positions[layer[i]] = i; }
        UpdatePositions();
        long bestCrossings = CrossingCount(arcs, rank, positions, layers);
        var best = layers.Select(layer => layer.ToArray()).ToArray();
        for (int sweep = 0; sweep < 12; sweep++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool down = sweep % 2 == 0;
            for (int step = 0; step < layers.Length; step++)
            {
                int layerIndex = down ? step : layers.Length - 1 - step;
                var layer = layers[layerIndex];
                var oldOrder = layer.Select((node, index) => (node, index)).ToDictionary(pair => pair.node, pair => pair.index);
                double Barycenter(int node)
                {
                    var neighbors = down ? backward[node] : forward[node];
                    return neighbors.Count == 0 ? (double)oldOrder[node] / Math.Max(1, layer.Count - 1) :
                        neighbors.Average(neighbor => (double)positions[neighbor] / Math.Max(1, layers[rank[neighbor]].Count - 1));
                }
                var keys = layer.ToDictionary(node => node, Barycenter);
                layer.Sort((a, b) => { int compare = keys[a].CompareTo(keys[b]); return compare != 0 ? compare : oldOrder[a].CompareTo(oldOrder[b]); });
                for (int i = 0; i < layer.Count; i++) positions[layer[i]] = i;
            }
            long crossings = CrossingCount(arcs, rank, positions, layers);
            if (crossings < bestCrossings) { bestCrossings = crossings; best = layers.Select(layer => layer.ToArray()).ToArray(); }
        }
        var widths = best.Select(layer => layer.Sum(node => sizes[node].Width) + Math.Max(0, layer.Length - 1) * settings.NodeSpacing).ToArray();
        var heights = best.Select(layer => layer.Max(node => sizes[node].Height)).ToArray();
        var y = Prefix(heights, settings.LayerSpacing); double maxWidth = widths.Max();
        var result = new CanvasPoint[count];
        for (int layer = 0; layer < best.Length; layer++)
        {
            double x = (maxWidth - widths[layer]) / 2;
            foreach (int node in best[layer]) { result[node] = new(x, y[layer]); x += sizes[node].Width + settings.NodeSpacing; }
        }
        return result;
    }

    private static long CrossingCount((int From, int To)[] arcs, int[] rank, int[] position, List<int>[] layers)
    {
        long total = 0;
        foreach (var group in arcs.GroupBy(edge => (rank[edge.From], rank[edge.To])))
        {
            var edges = group.OrderBy(edge => position[edge.From]).ThenBy(edge => position[edge.To]).ToArray();
            var tree = new int[layers[group.Key.Item2].Count + 1]; int inserted = 0;
            for (int start = 0; start < edges.Length;)
            {
                int end = start + 1;
                while (end < edges.Length && edges[end].From == edges[start].From) end++;
                for (int i = start; i < end; i++)
                {
                    int prefix = 0;
                    for (int at = position[edges[i].To] + 1; at > 0; at -= at & -at) prefix += tree[at];
                    total += inserted - prefix;
                }
                for (int i = start; i < end; i++)
                {
                    for (int at = position[edges[i].To] + 1; at < tree.Length; at += at & -at) tree[at]++;
                    inserted++;
                }
                start = end;
            }
        }
        return total;
    }

    private static CanvasPoint[] Tree((double Width, double Height)[] sizes, (int From, int To)[] edges,
        CanvasLayoutSettings settings, bool radial, CancellationToken cancellationToken)
    {
        int count = sizes.Length;
        var outgoing = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var indegree = new int[count];
        foreach (var edge in edges) { outgoing[edge.From].Add(edge.To); indegree[edge.To]++; }
        foreach (var neighbors in outgoing) neighbors.Sort();
        var children = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var seen = new bool[count]; var depth = new int[count]; var roots = new List<int>(); var traversal = new List<int>();
        foreach (int candidate in Enumerable.Range(0, count).OrderBy(node => indegree[node] == 0 ? 0 : 1).ThenBy(node => node))
        {
            if (seen[candidate]) continue;
            roots.Add(candidate); seen[candidate] = true;
            var queue = new Queue<int>(); queue.Enqueue(candidate);
            while (queue.TryDequeue(out int node))
            {
                cancellationToken.ThrowIfCancellationRequested(); traversal.Add(node);
                foreach (int child in outgoing[node])
                {
                    if (seen[child]) continue;
                    seen[child] = true; depth[child] = depth[node] + 1; children[node].Add(child); queue.Enqueue(child);
                }
            }
        }
        var span = new double[count];
        for (int i = traversal.Count - 1; i >= 0; i--)
        {
            int node = traversal[i];
            span[node] = radial ? Math.Max(1, children[node].Sum(child => span[child])) :
                Math.Max(sizes[node].Width, children[node].Sum(child => span[child]) + Math.Max(0, children[node].Count - 1) * settings.NodeSpacing);
        }
        var result = new CanvasPoint[count];
        if (!radial)
        {
            var heights = Enumerable.Range(0, depth.Max() + 1).Select(level => Enumerable.Range(0, count).Where(node => depth[node] == level).Max(node => sizes[node].Height)).ToArray();
            var levels = Prefix(heights, settings.LayerSpacing);
            var start = new double[count]; double cursor = 0;
            foreach (int root in roots) { start[root] = cursor; cursor += span[root] + settings.NodeSpacing * 2; }
            foreach (int node in traversal)
            {
                result[node] = new(start[node] + (span[node] - sizes[node].Width) / 2, levels[depth[node]]);
                double total = children[node].Sum(child => span[child]) + Math.Max(0, children[node].Count - 1) * settings.NodeSpacing;
                cursor = start[node] + (span[node] - total) / 2;
                foreach (int child in children[node]) { start[child] = cursor; cursor += span[child] + settings.NodeSpacing; }
            }
            return result;
        }
        var lowAngle = new double[count]; var arc = new double[count];
        double rootWeight = roots.Sum(root => span[root]), angleCursor = -Math.PI / 2;
        foreach (int root in roots) { lowAngle[root] = angleCursor; arc[root] = 2 * Math.PI * span[root] / rootWeight; angleCursor += arc[root]; }
        foreach (int node in traversal)
        {
            double total = children[node].Sum(child => span[child]); angleCursor = lowAngle[node];
            foreach (int child in children[node]) { lowAngle[child] = angleCursor; arc[child] = arc[node] * span[child] / total; angleCursor += arc[child]; }
        }
        var angles = Enumerable.Range(0, count).Select(node => lowAngle[node] + arc[node] / 2).ToArray();
        double previousRadius = 0, previousDiameter = roots.Count == 1 ? Diameter(sizes[roots[0]]) : 0;
        int offset = roots.Count == 1 ? 0 : 1;
        for (int ring = 0; ring <= depth.Max(); ring++)
        {
            var nodes = Enumerable.Range(0, count).Where(node => depth[node] == ring).OrderBy(node => angles[node]).ToArray();
            double diameter = nodes.Max(node => Diameter(sizes[node]));
            double radius = ring + offset == 0 ? 0 : previousRadius + previousDiameter / 2 + diameter / 2 + settings.LayerSpacing;
            if (nodes.Length > 1)
                for (int i = 0; i < nodes.Length; i++)
                {
                    int a = nodes[i], b = nodes[(i + 1) % nodes.Length];
                    double delta = (angles[b] - angles[a] + 2 * Math.PI) % (2 * Math.PI);
                    radius = Math.Max(radius, (Diameter(sizes[a]) / 2 + Diameter(sizes[b]) / 2 + settings.NodeSpacing) / (2 * Math.Max(0.000001, Math.Sin(delta / 2))));
                }
            foreach (int node in nodes) result[node] = new(radius * Math.Cos(angles[node]) - sizes[node].Width / 2, radius * Math.Sin(angles[node]) - sizes[node].Height / 2);
            previousRadius = radius; previousDiameter = diameter;
        }
        return result;
    }

    private static CanvasPoint[] Force((double Width, double Height)[] sizes, (int From, int To)[] edges,
        CanvasLayoutSettings settings, CancellationToken cancellationToken)
    {
        int count = sizes.Length;
        if (count > CanvasLayoutLimits.MaxForceNodes || (long)count * (count - 1) / 2 * settings.Iterations > CanvasLayoutLimits.MaxForceInteractions)
            throw new InvalidOperationException($"Force-directed layout exceeds its work budget. Use at most {CanvasLayoutLimits.MaxForceNodes} nodes or fewer iterations.");
        if (count == 1) return [new(0, 0)];
        double ideal = Math.Max(24, sizes.Average(Diameter) + settings.NodeSpacing);
        double radius = Math.Sqrt(count) * ideal / 2;
        var x = new double[count]; var y = new double[count]; var dx = new double[count]; var dy = new double[count];
        uint random = unchecked((uint)settings.Seed) ^ 0x9E3779B9u;
        double Noise() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return random / (double)uint.MaxValue - 0.5; }
        for (int i = 0; i < count; i++)
        {
            double angle = Math.PI * 2 * i / count;
            x[i] = radius * Math.Cos(angle) + Noise() * ideal / 10; y[i] = radius * Math.Sin(angle) + Noise() * ideal / 10;
        }
        for (int iteration = 0; iteration < settings.Iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested(); Array.Clear(dx); Array.Clear(dy);
            for (int a = 0; a < count; a++)
            {
                if (a % 16 == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int b = a + 1; b < count; b++)
                {
                    double vx = x[a] - x[b], vy = y[a] - y[b], distance = Math.Max(0.01, Math.Sqrt(vx * vx + vy * vy));
                    if (Math.Abs(vx) + Math.Abs(vy) < 0.001) { vx = 0.01; vy = 0.01; distance = Math.Sqrt(0.0002); }
                    double separation = Diameter(sizes[a]) / 2 + Diameter(sizes[b]) / 2 + settings.NodeSpacing;
                    double force = ideal * ideal / distance + Math.Max(0, separation - distance) * 2;
                    double fx = vx / distance * force, fy = vy / distance * force;
                    dx[a] += fx; dy[a] += fy; dx[b] -= fx; dy[b] -= fy;
                }
            }
            foreach (var edge in edges)
            {
                double vx = x[edge.To] - x[edge.From], vy = y[edge.To] - y[edge.From];
                double distance = Math.Max(0.01, Math.Sqrt(vx * vx + vy * vy));
                double force = distance * distance / ideal;
                double fx = vx / distance * force, fy = vy / distance * force;
                dx[edge.From] += fx; dy[edge.From] += fy; dx[edge.To] -= fx; dy[edge.To] -= fy;
            }
            double temperature = ideal * 0.5 * Math.Pow(1 - (double)iteration / settings.Iterations, 2) + 0.01;
            for (int node = 0; node < count; node++)
            {
                dx[node] -= x[node] * 0.02; dy[node] -= y[node] * 0.02;
                double length = Math.Max(0.001, Math.Sqrt(dx[node] * dx[node] + dy[node] * dy[node]));
                double step = Math.Min(length, temperature) / length;
                x[node] += dx[node] * step; y[node] += dy[node] * step;
            }
        }
        return Enumerable.Range(0, count).Select(node => new CanvasPoint(x[node] - sizes[node].Width / 2, y[node] - sizes[node].Height / 2)).ToArray();
    }

    private static double[] Prefix(double[] sizes, double spacing)
    {
        var result = new double[sizes.Length];
        for (int i = 1; i < sizes.Length; i++) result[i] = result[i - 1] + sizes[i - 1] + spacing;
        return result;
    }
    private static double Diameter((double Width, double Height) size) => Math.Sqrt(size.Width * size.Width + size.Height * size.Height);
}
