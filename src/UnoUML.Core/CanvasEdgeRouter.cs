namespace UnoUML.Core;

/// <summary>Rectilinear visibility-grid routing with an admissible A* heuristic and a shared work budget.</summary>
internal static class CanvasEdgeRouter
{
    private const double Epsilon = 0.000001;
    private readonly record struct Obstacle(string Id, CanvasRect Bounds);
    private readonly record struct Port(CanvasPoint Dock, CanvasPoint Exit, int Axis, int Sign)
    {
        internal double Length => Distance(Dock, Exit);
    }
    // A geometric obstruction is distinct from cancellation or bounded-work
    // exhaustion. Only the former may use an explicit-gesture fallback.
    private sealed class ObstructedRouteException(string message) : InvalidOperationException(message);

    internal static CanvasLayoutState Route(CanvasLayoutState state, Func<CanvasEdge, bool> include, CancellationToken cancellationToken,
        bool allowOverlapFallback = false)
    {
        var nodes = state.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var budget = new Budget(cancellationToken);
        var parallel = state.Edges.GroupBy(edge => (edge.Source, edge.Target))
            .SelectMany(group => group.OrderBy(edge => edge.Id, StringComparer.Ordinal).Select((edge, index) => (edge.Id, Offset: (index - (group.Count() - 1) / 2.0) * 8)))
            .ToDictionary(pair => pair.Id, pair => pair.Offset, StringComparer.Ordinal);
        bool Ancestor(string ancestor, string child)
        {
            for (var parent = nodes[child].Parent; parent != null; parent = nodes[parent].Parent) if (parent == ancestor) return true;
            return false;
        }
        bool Hidden(CanvasNode node)
        {
            for (var parent = node.Parent; parent != null; parent = nodes[parent].Parent) if (nodes[parent].Collapsed) return true;
            return false;
        }
        var visible = state.Nodes.Where(node => !Hidden(node)).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var routes = new Dictionary<string, CanvasEdge>(StringComparer.Ordinal);
        foreach (var edge in state.Edges.Where(include).OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = nodes[edge.Source]; var target = nodes[edge.Target];
            if (Hidden(source) || Hidden(target)) continue;
            if (edge.Routing == "straight" && source.Id != target.Id)
            {
                var a = Dock(source, Center(target), edge.SourcePort); var b = Dock(target, Center(source), edge.TargetPort);
                routes.Add(edge.Id, edge with { Waypoints = new[] { a, b } });
                continue;
            }
            var obstacles = visible.Where(node => !Ancestor(node.Id, source.Id) && !Ancestor(node.Id, target.Id) &&
                    !Ancestor(source.Id, node.Id) && !Ancestor(target.Id, node.Id))
                .Select(node => new Obstacle(node.Id, CanvasRect.Of(node).Inflate(state.Settings.RoutingPadding))).ToArray();
            CanvasPoint[] path;
            try
            {
                var starts = Ports(source, edge.SourcePort, state.Settings.RoutingPadding, parallel[edge.Id])
                    .Where(port => ClearStub(port, source.Id, obstacles, budget)).ToArray();
                var ends = Ports(target, edge.TargetPort, state.Settings.RoutingPadding, parallel[edge.Id])
                    .Where(port => ClearStub(port, target.Id, obstacles, budget)).ToArray();
                if (starts.Length == 0 || ends.Length == 0)
                    throw new ObstructedRouteException($"Edge '{edge.Id}' has no unobstructed port. Separate overlapping nodes or change its port.");
                if (source.Id == target.Id && starts.Length == 1 && ends.Length == 1 && Same(starts[0].Dock, ends[0].Dock))
                    path = SamePortLoop(starts[0], obstacles, state.Settings, budget);
                else path = Orthogonal(starts, ends, obstacles, source.Id == target.Id, state.Settings, budget);
            }
            catch (ObstructedRouteException) when (allowOverlapFallback)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Do not change Routing or Manual: moving the endpoints apart must
                // retry the intended router, not turn a temporary overlap into an edit.
                path = [Dock(source, Center(target), edge.SourcePort), Dock(target, Center(source), edge.TargetPort)];
            }
            routes.Add(edge.Id, edge with { Waypoints = path });
        }
        var result = state with { Edges = state.Edges.Select(edge => routes.GetValueOrDefault(edge.Id, edge)).ToArray() };
        return CanvasLayoutValidation.Snapshot(result, cancellationToken);
    }

    internal static CanvasEdge Reattach(CanvasEdge edge, CanvasNode oldSource, CanvasNode oldTarget, CanvasNode source, CanvasNode target, bool forcePorts = false)
    {
        if (edge.Waypoints is not { Count: >= 2 } path) return edge;
        double sx = source.X - oldSource.X, sy = source.Y - oldSource.Y;
        double tx = target.X - oldTarget.X, ty = target.Y - oldTarget.Y;
        bool unchangedSize = source.Width == oldSource.Width && source.Height == oldSource.Height && source.Rotation == oldSource.Rotation &&
            target.Width == oldTarget.Width && target.Height == oldTarget.Height && target.Rotation == oldTarget.Rotation;
        if (!forcePorts && unchangedSize && Math.Abs(sx - tx) < Epsilon && Math.Abs(sy - ty) < Epsilon)
            return edge with { Waypoints = path.Select(point => new CanvasPoint(point.X + sx, point.Y + sy)).ToArray() };
        var sourcePoint = Dock(source, path.Count > 2 ? path[1] : Center(target), edge.SourcePort);
        var targetPoint = Dock(target, path.Count > 2 ? path[^2] : Center(source), edge.TargetPort);
        if (edge.Routing == "straight") return edge with { Waypoints = new[] { sourcePoint, targetPoint } };
        var result = new List<CanvasPoint> { sourcePoint };
        bool orthogonal = edge.Routing is "orthogonal" or "curved";
        for (int i = 1; i < path.Count - 1; i++)
        {
            if (orthogonal && i == 1 && !AxisAligned(sourcePoint, path[i]))
                result.Add(Math.Abs(path[1].X - path[0].X) >= Math.Abs(path[1].Y - path[0].Y) ? new(path[i].X, sourcePoint.Y) : new(sourcePoint.X, path[i].Y));
            result.Add(path[i]);
        }
        if (orthogonal && !AxisAligned(result[^1], targetPoint))
            result.Add(Math.Abs(path[^1].X - path[^2].X) >= Math.Abs(path[^1].Y - path[^2].Y) ? new(result[^1].X, targetPoint.Y) : new(targetPoint.X, result[^1].Y));
        result.Add(targetPoint);
        return edge with { Waypoints = Simplify(result) };
    }

    internal static CanvasPoint Dock(CanvasNode node, CanvasPoint toward, CanvasPoint? normalized)
    {
        double radians = node.Rotation % 360 * Math.PI / 180, c = Math.Cos(radians), s = Math.Sin(radians);
        double x, y;
        if (normalized is { } port) { x = (port.X - 0.5) * node.Width; y = (port.Y - 0.5) * node.Height; }
        else
        {
            double dx = toward.X - node.X - node.Width / 2, dy = toward.Y - node.Y - node.Height / 2;
            double localX = c * dx + s * dy, localY = -s * dx + c * dy;
            if (Math.Abs(localX) + Math.Abs(localY) < Epsilon) localX = 1;
            double factor = Math.Min(Math.Abs(localX) < Epsilon ? double.PositiveInfinity : node.Width / 2 / Math.Abs(localX),
                Math.Abs(localY) < Epsilon ? double.PositiveInfinity : node.Height / 2 / Math.Abs(localY));
            x = localX * factor; y = localY * factor;
        }
        return new(node.X + node.Width / 2 + c * x - s * y, node.Y + node.Height / 2 + s * x + c * y);
    }

    private static Port[] Ports(CanvasNode node, CanvasPoint? explicitPort, double padding, double offset)
    {
        double horizontal = Math.Clamp(offset / node.Width, -0.4, 0.4), vertical = Math.Clamp(offset / node.Height, -0.4, 0.4);
        CanvasPoint[] points = explicitPort is { } port ? [port] : [new(1, 0.5 + vertical), new(0.5 + horizontal, 1), new(0, 0.5 + vertical), new(0.5 + horizontal, 0)];
        var bounds = CanvasRect.Of(node).Inflate(padding);
        double radians = node.Rotation % 360 * Math.PI / 180, cosine = Math.Cos(radians), sine = Math.Sin(radians);
        return points.Select(point =>
        {
            var dock = Dock(node, Center(node), point);
            double nx = point.X - 0.5, ny = point.Y - 0.5;
            if (Math.Abs(nx) + Math.Abs(ny) < Epsilon) nx = 1;
            double x = cosine * nx - sine * ny, y = sine * nx + cosine * ny;
            bool horizontalAxis = Math.Abs(x) >= Math.Abs(y);
            int sign = (horizontalAxis ? x : y) >= 0 ? 1 : -1;
            var exit = horizontalAxis ? new CanvasPoint(sign > 0 ? bounds.Right : bounds.X, dock.Y) : new(dock.X, sign > 0 ? bounds.Bottom : bounds.Y);
            return new Port(dock, exit, horizontalAxis ? 0 : 1, sign);
        }).ToArray();
    }

    private static bool ClearStub(Port port, string owner, Obstacle[] obstacles, Budget budget)
    {
        foreach (var obstacle in obstacles)
        {
            budget.Spend();
            if (obstacle.Id != owner && Intersects(port.Dock, port.Exit, obstacle.Bounds)) return false;
        }
        return true;
    }

    private static CanvasPoint[] Orthogonal(Port[] starts, Port[] ends, Obstacle[] obstacles, bool selfLoop, CanvasLayoutSettings settings, Budget budget)
    {
        CanvasPoint[]? best = null; double bestCost = double.PositiveInfinity;
        double bendCost = Math.Max(8, settings.RoutingPadding);
        foreach (var source in starts)
        foreach (var target in ends)
        {
            if (selfLoop && Same(source.Dock, target.Dock)) continue;
            foreach (var middle in new[]
            {
                new[] { source.Exit, new CanvasPoint(target.Exit.X, source.Exit.Y), target.Exit },
                new[] { source.Exit, new CanvasPoint(source.Exit.X, target.Exit.Y), target.Exit }
            })
            {
                var candidate = Simplify(new[] { source.Dock }.Concat(middle).Append(target.Dock));
                double cost = Cost(candidate, bendCost);
                if (cost >= bestCost || !ClearPath(middle, obstacles, budget)) continue;
                best = candidate; bestCost = cost;
            }
        }
        if (best != null) return best;

        var xs = obstacles.SelectMany(obstacle => new[] { obstacle.Bounds.X, obstacle.Bounds.Right })
            .Concat(starts.Select(port => port.Exit.X)).Concat(ends.Select(port => port.Exit.X)).Distinct().Order().ToArray();
        var ys = obstacles.SelectMany(obstacle => new[] { obstacle.Bounds.Y, obstacle.Bounds.Bottom })
            .Concat(starts.Select(port => port.Exit.Y)).Concat(ends.Select(port => port.Exit.Y)).Distinct().Order().ToArray();
        var xIndex = xs.Select((value, index) => (value, index)).ToDictionary(pair => pair.value, pair => pair.index);
        var yIndex = ys.Select((value, index) => (value, index)).ToDictionary(pair => pair.value, pair => pair.index);
        var grid = new VisibilityGrid(xs, ys, obstacles, budget);
        int PointIndex(CanvasPoint point) => yIndex[point.Y] * xs.Length + xIndex[point.X];
        CanvasPoint Point(int index) => new(xs[index % xs.Length], ys[index / xs.Length]);
        var targets = ends.Select((port, index) => (Point: PointIndex(port.Exit), index)).GroupBy(pair => pair.Point)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.index).ToArray());
        double Heuristic(CanvasPoint point) => ends.Min(port => Distance(point, port.Exit) + port.Length);
        var queue = new PriorityQueue<(int Key, double Distance), (double Score, int Bends, double Remaining, int Key)>();
        var distances = new Dictionary<int, double>(); var parents = new Dictionary<int, int>(); var origins = new Dictionary<int, int>();
        var bends = new Dictionary<int, int>();
        for (int i = 0; i < starts.Length; i++)
        {
            int key = PointIndex(starts[i].Exit) * 2 + starts[i].Axis;
            double distance = starts[i].Length;
            if (distances.TryGetValue(key, out double previous) && previous <= distance) continue;
            distances[key] = distance; origins[key] = i; bends[key] = 0; parents[key] = -1;
            double remaining = Heuristic(starts[i].Exit);
            queue.Enqueue((key, distance), (distance + remaining, 0, remaining, key));
        }
        int bestKey = -1, bestTarget = -1, expanded = 0;
        while (queue.TryDequeue(out var current, out var priority))
        {
            budget.CheckCancellation();
            if (current.Distance != distances[current.Key]) continue;
            if (priority.Score >= bestCost) break;
            if (++expanded > CanvasLayoutLimits.MaxRoutingSearchStates)
                throw new InvalidOperationException("Orthogonal routing exceeded its search budget. Use a smaller selection or move obstructing nodes.");
            int pointIndex = current.Key / 2, incomingAxis = current.Key % 2;
            var point = Point(pointIndex);
            if (targets.TryGetValue(pointIndex, out var destinationIndices))
            {
                foreach (int targetIndex in destinationIndices)
                {
                    if (selfLoop && Same(starts[origins[current.Key]].Dock, ends[targetIndex].Dock)) continue;
                    double cost = current.Distance + ends[targetIndex].Length + (incomingAxis == ends[targetIndex].Axis ? 0 : bendCost);
                    if (cost < bestCost) { bestCost = cost; bestKey = current.Key; bestTarget = targetIndex; }
                }
            }
            int x = pointIndex % xs.Length, y = pointIndex / xs.Length;
            Span<(int X, int Y, int Axis)> neighbors = [(x - 1, y, 0), (x + 1, y, 0), (x, y - 1, 1), (x, y + 1, 1)];
            foreach (var neighbor in neighbors)
            {
                if (neighbor.X < 0 || neighbor.X >= xs.Length || neighbor.Y < 0 || neighbor.Y >= ys.Length ||
                    !grid.Clear(x, y, neighbor.X, neighbor.Y)) continue;
                int key = (neighbor.Y * xs.Length + neighbor.X) * 2 + neighbor.Axis;
                var next = new CanvasPoint(xs[neighbor.X], ys[neighbor.Y]);
                int turn = incomingAxis == neighbor.Axis ? 0 : 1;
                double distance = current.Distance + Distance(point, next) + turn * bendCost;
                if (distances.TryGetValue(key, out double previous) && distance >= previous - Epsilon) continue;
                distances[key] = distance; parents[key] = current.Key; origins[key] = origins[current.Key];
                bends[key] = bends[current.Key] + turn;
                double remaining = Heuristic(next);
                queue.Enqueue((key, distance), (distance + remaining, bends[key], remaining, key));
            }
        }
        if (bestKey < 0) throw new ObstructedRouteException("No obstacle-free orthogonal route exists for the current ports and clearance.");
        var points = new List<CanvasPoint>();
        for (int key = bestKey; key >= 0; key = parents[key]) points.Add(Point(key / 2));
        points.Reverse(); points.Insert(0, starts[origins[bestKey]].Dock); points.Add(ends[bestTarget].Dock);
        return Simplify(points);
    }

    private static CanvasPoint[] SamePortLoop(Port port, Obstacle[] obstacles, CanvasLayoutSettings settings, Budget budget)
    {
        double size = Math.Max(24, settings.RoutingPadding * 2);
        for (int attempt = 0; attempt < 8; attempt++, size *= 2)
        foreach (int side in new[] { -1, 1 })
        {
            var a = port.Exit;
            var b = new CanvasPoint(a.X + (port.Axis == 0 ? port.Sign * size : 0), a.Y + (port.Axis == 1 ? port.Sign * size : 0));
            var c = new CanvasPoint(b.X + (port.Axis == 1 ? side * size : 0), b.Y + (port.Axis == 0 ? side * size : 0));
            var d = new CanvasPoint(a.X + (port.Axis == 1 ? side * size : 0), a.Y + (port.Axis == 0 ? side * size : 0));
            CanvasPoint[] middle = [a, b, c, d, a];
            if (ClearPath(middle, obstacles, budget)) return Simplify(new[] { port.Dock }.Concat(middle).Append(port.Dock));
        }
        throw new ObstructedRouteException("No clear self-loop fits the chosen port within the allowed routing extent.");
    }

    private static bool ClearPath(IReadOnlyList<CanvasPoint> points, Obstacle[] obstacles, Budget budget)
    {
        for (int i = 1; i < points.Count; i++)
        foreach (var obstacle in obstacles)
        {
            budget.Spend();
            if (Intersects(points[i - 1], points[i], obstacle.Bounds)) return false;
        }
        return true;
    }
    private static bool Intersects(CanvasPoint a, CanvasPoint b, CanvasRect rect)
    {
        if (Same(a, b)) return a.X > rect.X + Epsilon && a.X < rect.Right - Epsilon && a.Y > rect.Y + Epsilon && a.Y < rect.Bottom - Epsilon;
        if (Math.Abs(a.Y - b.Y) < Epsilon)
            return a.Y > rect.Y + Epsilon && a.Y < rect.Bottom - Epsilon && Math.Max(a.X, b.X) > rect.X + Epsilon && Math.Min(a.X, b.X) < rect.Right - Epsilon;
        if (Math.Abs(a.X - b.X) < Epsilon)
            return a.X > rect.X + Epsilon && a.X < rect.Right - Epsilon && Math.Max(a.Y, b.Y) > rect.Y + Epsilon && Math.Min(a.Y, b.Y) < rect.Bottom - Epsilon;
        throw new InvalidOperationException("The orthogonal router received a diagonal segment.");
    }

    private sealed class VisibilityGrid(double[] xs, double[] ys, Obstacle[] obstacles, Budget budget)
    {
        private readonly Dictionary<(int Axis, int Index), (double Start, double End)[]> intervals = [];
        internal bool Clear(int x1, int y1, int x2, int y2)
        {
            int axis = y1 == y2 ? 0 : 1, index = axis == 0 ? y1 : x1;
            double coordinate = axis == 0 ? ys[index] : xs[index];
            if (!intervals.TryGetValue((axis, index), out var blocked))
            {
                var list = new List<(double Start, double End)>();
                foreach (var obstacle in obstacles)
                {
                    budget.Spend(); var rect = obstacle.Bounds;
                    if (axis == 0 ? coordinate > rect.Y + Epsilon && coordinate < rect.Bottom - Epsilon : coordinate > rect.X + Epsilon && coordinate < rect.Right - Epsilon)
                        list.Add(axis == 0 ? (rect.X, rect.Right) : (rect.Y, rect.Bottom));
                }
                list.Sort((a, b) => { int order = a.Start.CompareTo(b.Start); return order == 0 ? a.End.CompareTo(b.End) : order; });
                var merged = new List<(double Start, double End)>();
                foreach (var range in list)
                    if (merged.Count > 0 && range.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(range.End, merged[^1].End));
                    else merged.Add(range);
                blocked = merged.ToArray(); intervals[(axis, index)] = blocked;
            }
            budget.Spend();
            double start = axis == 0 ? Math.Min(xs[x1], xs[x2]) : Math.Min(ys[y1], ys[y2]);
            double end = axis == 0 ? Math.Max(xs[x1], xs[x2]) : Math.Max(ys[y1], ys[y2]);
            int low = 0, high = blocked.Length;
            while (low < high) { int mid = (low + high) / 2; if (blocked[mid].End <= start + Epsilon) low = mid + 1; else high = mid; }
            return low == blocked.Length || blocked[low].Start >= end - Epsilon;
        }
    }

    private sealed class Budget(CancellationToken cancellationToken)
    {
        private long work;
        internal void Spend()
        {
            if ((++work & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (work > CanvasLayoutLimits.MaxRoutingWork)
                throw new InvalidOperationException("Routing exceeded its work budget. Route a smaller selection.");
        }
        internal void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();
    }

    private static CanvasPoint[] Simplify(IEnumerable<CanvasPoint> input)
    {
        var result = new List<CanvasPoint>();
        foreach (var point in input)
        {
            if (result.Count > 0 && Same(result[^1], point)) continue;
            while (result.Count > 1)
            {
                var a = result[^2]; var b = result[^1];
                double cross = (b.X - a.X) * (point.Y - b.Y) - (b.Y - a.Y) * (point.X - b.X);
                double dot = (b.X - a.X) * (point.X - b.X) + (b.Y - a.Y) * (point.Y - b.Y);
                if (Math.Abs(cross) >= Epsilon || dot < -Epsilon) break;
                result.RemoveAt(result.Count - 1);
            }
            result.Add(point);
        }
        if (result.Count == 1) result.Add(result[0]);
        if (result.Count > CanvasLayoutLimits.MaxWaypointsPerEdge) throw new InvalidOperationException("The route exceeds its waypoint budget.");
        return result.ToArray();
    }
    private static double Cost(IReadOnlyList<CanvasPoint> points, double bendCost)
    {
        double result = Math.Max(0, points.Count - 2) * bendCost;
        for (int i = 1; i < points.Count; i++) result += Distance(points[i - 1], points[i]);
        return result;
    }
    private static CanvasPoint Center(CanvasNode node) => new(node.X + node.Width / 2, node.Y + node.Height / 2);
    private static double Distance(CanvasPoint a, CanvasPoint b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    private static bool Same(CanvasPoint a, CanvasPoint b) => Math.Abs(a.X - b.X) < Epsilon && Math.Abs(a.Y - b.Y) < Epsilon;
    private static bool AxisAligned(CanvasPoint a, CanvasPoint b) => Math.Abs(a.X - b.X) < Epsilon || Math.Abs(a.Y - b.Y) < Epsilon;
}
