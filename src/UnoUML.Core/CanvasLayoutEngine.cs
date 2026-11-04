namespace UnoUML.Core;

/// <summary>
/// Deterministic, renderer-independent geometry operations. Null layout scope means all nodes;
/// an empty scope means none. Locked, pinned, and unselected nodes are never moved implicitly.
/// A wholly movable compound subtree is a rigid unit; select its children separately to lay out its interior.
/// </summary>
public static partial class CanvasLayoutEngine
{
    public static CanvasLayoutState Layout(CanvasLayoutState state, CanvasLayoutSettings? settings = null,
        IReadOnlyCollection<string>? selection = null, CancellationToken cancellationToken = default)
    {
        state = CanvasLayoutValidation.Snapshot(state, cancellationToken);
        settings ??= state.Settings;
        CanvasLayoutValidation.ValidateSettings(settings);
        state = state with { Settings = settings };
        var context = new EditContext(state, selection, cancellationToken);
        if (context.Units.Count == 0) return state;
        var placements = new Dictionary<string, CanvasRect>(StringComparer.Ordinal);
        foreach (var scope in context.Units.GroupBy(n => n.Node.Parent ?? "", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var units = scope.OrderBy(n => n.Node.Id, StringComparer.Ordinal).ToArray();
            var indices = units.Select((unit, index) => (unit, index)).ToDictionary(p => p.unit.Node.Id, p => p.index, StringComparer.Ordinal);
            var unitForNode = units.SelectMany(unit => unit.Members.Select(member => (member.Id, unit.Node.Id)))
                .ToDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.Ordinal);
            var links = state.Edges.Where(e => unitForNode.ContainsKey(e.Source) && unitForNode.ContainsKey(e.Target))
                .Select(e => (From: indices[unitForNode[e.Source]], To: indices[unitForNode[e.Target]]))
                .Where(e => e.From != e.To).Distinct().OrderBy(e => e.From).ThenBy(e => e.To).ToArray();
            bool horizontal = settings.Direction is CanvasLayoutDirection.Left or CanvasLayoutDirection.Right;
            var sizes = units.Select(unit => horizontal ? (Width: unit.Bounds.Height, Height: unit.Bounds.Width) : (unit.Bounds.Width, unit.Bounds.Height)).ToArray();
            var points = Arrange(sizes, links, settings, cancellationToken);
            double originX = units.Min(u => u.Bounds.X), originY = units.Min(u => u.Bounds.Y);
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            var transformed = new CanvasPoint[units.Length];
            for (int i = 0; i < units.Length; i++)
            {
                var point = points[i];
                transformed[i] = settings.Direction switch
                {
                    CanvasLayoutDirection.Down => point,
                    CanvasLayoutDirection.Right => new(point.Y, point.X),
                    CanvasLayoutDirection.Up => new(point.X, -point.Y - units[i].Bounds.Height),
                    CanvasLayoutDirection.Left => new(-point.Y - units[i].Bounds.Width, point.X),
                    _ => throw new ArgumentException("Unsupported layout direction.")
                };
                minX = Math.Min(minX, transformed[i].X); minY = Math.Min(minY, transformed[i].Y);
            }
            for (int i = 0; i < units.Length; i++)
            {
                double x = originX + transformed[i].X - minX, y = originY + transformed[i].Y - minY;
                if (settings.SnapToGrid) { x = Snap(x, settings.GridSize); y = Snap(y, settings.GridSize); }
                placements.Add(units[i].Node.Id, units[i].Bounds with { X = x, Y = y });
            }
        }
        // Only movable units are removed from the obstacle set. Previously positioned
        // units become obstacles again, so the final packing cannot overlap a fixed node.
        var occupied = state.Nodes.Where(n => !context.Moving.Contains(n.Id)).OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => (n.Id, Rect: CanvasRect.Of(n))).ToList();
        long placementWork = 0;
        foreach (var unit in context.Units.OrderBy(u => placements[u.Node.Id].Y).ThenBy(u => placements[u.Node.Id].X).ThenBy(u => u.Node.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var obstacles = occupied.Where(item => !context.IsAncestor(item.Id, unit.Node.Id)).Select(item => item.Rect).ToArray();
            CanvasRect? container = unit.Node.Parent is { } parent ? CanvasRect.Of(context.Nodes[parent]) : null;
            var placed = FindFree(placements[unit.Node.Id], obstacles, container, settings, ref placementWork, cancellationToken);
            context.Move(unit, placed.X - unit.Bounds.X, placed.Y - unit.Bounds.Y);
            occupied.Add((unit.Node.Id, placed));
        }
        return Finish(context, cancellationToken);
    }

    /// <summary>Align selected movable units to their collective bounds, or to an explicit reference node.</summary>
    public static CanvasLayoutState Align(CanvasLayoutState state, CanvasAlignment alignment, IReadOnlyCollection<string> selection,
        string? referenceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
        state = CanvasLayoutValidation.Snapshot(state, cancellationToken);
        var context = new EditContext(state, selection, cancellationToken);
        if (context.Units.Count == 0) return state;
        var reference = referenceId != null ? CanvasRect.Of(context.RequireNode(referenceId)) : Bounds(context.Selected.Select(id => CanvasRect.Of(context.Nodes[id])));
        foreach (var unit in context.Units)
        {
            double dx = alignment switch
            {
                CanvasAlignment.Left => reference.X - unit.Bounds.X, CanvasAlignment.Center => reference.CenterX - unit.Bounds.CenterX,
                CanvasAlignment.Right => reference.Right - unit.Bounds.Right, _ => 0
            };
            double dy = alignment switch
            {
                CanvasAlignment.Top => reference.Y - unit.Bounds.Y, CanvasAlignment.Middle => reference.CenterY - unit.Bounds.CenterY,
                CanvasAlignment.Bottom => reference.Bottom - unit.Bounds.Bottom, _ => 0
            };
            context.Move(unit, dx, dy);
        }
        return Finish(context, cancellationToken);
    }

    /// <summary>Equalize edge-to-edge gaps. Fixed selected nodes and the two extremes are anchors.</summary>
    public static CanvasLayoutState Distribute(CanvasLayoutState state, CanvasAxis axis, IReadOnlyCollection<string> selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(axis)) throw new ArgumentOutOfRangeException(nameof(axis));
        state = CanvasLayoutValidation.Snapshot(state, cancellationToken);
        var context = new EditContext(state, selection, cancellationToken);
        var movable = context.Units.ToDictionary(u => u.Node.Id, StringComparer.Ordinal);
        var entries = context.Units.Select(u => (Id: u.Node.Id, u.Bounds))
            .Concat(context.Selected.Where(id => !context.Moving.Contains(id) && !context.Units.Any(u => context.IsAncestor(id, u.Node.Id)))
                .Select(id => (Id: id, Bounds: CanvasRect.Of(context.Nodes[id]))))
            .OrderBy(item => axis == CanvasAxis.Horizontal ? item.Bounds.X : item.Bounds.Y).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (entries.Length < 3) return state;
        var anchors = Enumerable.Range(0, entries.Length).Where(i => i == 0 || i == entries.Length - 1 || !movable.ContainsKey(entries[i].Id)).ToArray();
        for (int segment = 1; segment < anchors.Length; segment++)
        {
            int first = anchors[segment - 1], last = anchors[segment];
            if (last - first < 2) continue;
            double Start(CanvasRect rect) => axis == CanvasAxis.Horizontal ? rect.X : rect.Y;
            double Size(CanvasRect rect) => axis == CanvasAxis.Horizontal ? rect.Width : rect.Height;
            double available = Start(entries[last].Bounds) - Start(entries[first].Bounds);
            double occupied = Enumerable.Range(first, last - first).Sum(i => Size(entries[i].Bounds));
            double gap = (available - occupied) / (last - first);
            if (gap < -0.001) throw new InvalidOperationException("The fixed distribution anchors leave insufficient room for nonoverlapping nodes.");
            double cursor = Start(entries[first].Bounds) + Size(entries[first].Bounds) + gap;
            for (int i = first + 1; i < last; i++)
            {
                var unit = movable[entries[i].Id];
                context.Move(unit, axis == CanvasAxis.Horizontal ? cursor - unit.Bounds.X : 0, axis == CanvasAxis.Vertical ? cursor - unit.Bounds.Y : 0);
                cursor += Size(entries[i].Bounds) + gap;
            }
        }
        return Finish(context, cancellationToken);
    }

    /// <summary>Match unrotated dimensions to an explicit node, or the first selected node in ordinal ID order.</summary>
    public static CanvasLayoutState MatchSize(CanvasLayoutState state, CanvasSize size, IReadOnlyCollection<string> selection,
        string? referenceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(size)) throw new ArgumentOutOfRangeException(nameof(size));
        state = CanvasLayoutValidation.Snapshot(state, cancellationToken);
        var context = new EditContext(state, selection, cancellationToken);
        if (context.Units.Count == 0) return state;
        var reference = context.RequireNode(referenceId ?? context.Selected.Order(StringComparer.Ordinal).First());
        foreach (var unit in context.Units)
        {
            var node = unit.Node with { Width = size == CanvasSize.Height ? unit.Node.Width : reference.Width, Height = size == CanvasSize.Width ? unit.Node.Height : reference.Height };
            if (unit.Members.Any(child => child.Id != node.Id && !CanvasRect.Of(node).Contains(CanvasRect.Of(child))))
                throw new InvalidOperationException($"Resizing '{node.Id}' would exclude a child. Resize or select the child separately.");
            context.Updated[node.Id] = node;
        }
        return Finish(context, cancellationToken);
    }

    /// <summary>Reroute automatic, unlocked edges incident to this node scope. Manual paths remain unchanged.</summary>
    public static CanvasLayoutState RouteEdges(CanvasLayoutState state, IReadOnlyCollection<string>? selection = null,
        CancellationToken cancellationToken = default)
    {
        state = CanvasLayoutValidation.Snapshot(state, cancellationToken);
        var selected = ResolveSelection(state, selection);
        return CanvasEdgeRouter.Route(state, edge => !edge.Locked && !edge.Manual && (selected.Contains(edge.Source) || selected.Contains(edge.Target)), cancellationToken);
    }

    /// <summary>Explicitly discard bends and manual-route flags on selected unlocked edges; retain routing style, ports and labels.</summary>
    public static CanvasLayoutState ResetRoutes(CanvasLayoutState state, IReadOnlyCollection<string>? edgeIds = null,
        CancellationToken cancellationToken = default)
    {
        state = CanvasLayoutValidation.Snapshot(state, cancellationToken);
        var all = state.Edges.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var selected = edgeIds == null ? all : edgeIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Any(id => id == null || !all.Contains(id))) throw new ArgumentException("The route selection references a missing edge.", nameof(edgeIds));
        state = state with { Edges = state.Edges.Select(edge => selected.Contains(edge.Id) && !edge.Locked ? edge with { Manual = false, Waypoints = [] } : edge).ToArray() };
        return CanvasEdgeRouter.Route(state, edge => selected.Contains(edge.Id) && !edge.Locked, cancellationToken);
    }

    /// <summary>
    /// Commit an explicit geometry gesture over an unchanged graph. Pinned nodes may be moved deliberately;
    /// nodes or edges locked in the prior snapshot cannot have their geometry edited. Manual bends survive,
    /// and a common translation of both endpoints translates their complete manual path.
    /// </summary>
    public static CanvasLayoutState UpdateGeometry(CanvasLayoutState before, CanvasLayoutState after, CancellationToken cancellationToken = default)
    {
        before = CanvasLayoutValidation.Snapshot(before, cancellationToken);
        after = CanvasLayoutValidation.Snapshot(after, cancellationToken);
        var oldNodes = before.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var nodes = after.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var oldEdges = before.Edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        if (nodes.Count != oldNodes.Count || nodes.Keys.Any(id => !oldNodes.ContainsKey(id)) ||
            oldEdges.Count != after.Edges.Count || after.Edges.Any(edge => !oldEdges.TryGetValue(edge.Id, out var old) || old.Source != edge.Source || old.Target != edge.Target))
            throw new ArgumentException("A geometry gesture must preserve node IDs, edge IDs, and edge endpoints.");
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            var old = oldNodes[node.Id];
            if (node.Parent != old.Parent) throw new ArgumentException("A geometry gesture cannot change the grouping hierarchy.");
            if (node.X != old.X || node.Y != old.Y || node.Width != old.Width || node.Height != old.Height || node.Rotation != old.Rotation)
            {
                if (old.Locked) throw new ArgumentException($"Node '{node.Id}' is locked.");
                changed.Add(node.Id);
            }
        }
        bool Ancestor(string ancestor, string child)
        {
            for (var parent = oldNodes[child].Parent; parent != null; parent = oldNodes[parent].Parent)
                if (parent == ancestor) return true;
            return false;
        }
        foreach (var locked in before.Nodes.Where(node => node.Locked))
            if (changed.Any(id => Ancestor(locked.Id, id) || Ancestor(id, locked.Id)))
                throw new ArgumentException($"The gesture would transform the locked subtree containing '{locked.Id}'.");
        var reroute = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<CanvasEdge>(after.Edges.Count);
        foreach (var edge in after.Edges)
        {
            var old = oldEdges[edge.Id];
            bool portChange = old.SourcePort != edge.SourcePort || old.TargetPort != edge.TargetPort;
            bool routeChange = edge.Routing != old.Routing || edge.Manual != old.Manual || portChange ||
                !(edge.Waypoints ?? []).SequenceEqual(old.Waypoints ?? []) || edge.LabelOffset != old.LabelOffset;
            if (old.Locked && routeChange) throw new ArgumentException($"Edge '{edge.Id}' is locked.");
            bool moved = changed.Contains(edge.Source) || changed.Contains(edge.Target);
            if (!edge.Locked && !edge.Manual && (moved || portChange || edge.Routing != old.Routing || old.Manual)) reroute.Add(edge.Id);
            edges.Add(!edge.Locked && edge.Manual && (moved || portChange)
                ? CanvasEdgeRouter.Reattach(edge, oldNodes[edge.Source], oldNodes[edge.Target], nodes[edge.Source], nodes[edge.Target], portChange)
                : edge);
        }
        after = after with { Edges = edges.ToArray() };
        return CanvasEdgeRouter.Route(after, edge => reroute.Contains(edge.Id), cancellationToken);
    }

    private static CanvasLayoutState Finish(EditContext context, CancellationToken cancellationToken)
    {
        var state = context.State with { Nodes = context.State.Nodes.Select(node => context.Updated.GetValueOrDefault(node.Id, node)).ToArray() };
        state = CanvasLayoutValidation.Snapshot(state, cancellationToken);
        return UpdateGeometry(context.State, state, cancellationToken);
    }

    private static HashSet<string> ResolveSelection(CanvasLayoutState state, IReadOnlyCollection<string>? selection)
    {
        var all = state.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (selection == null) return all;
        var selected = selection.ToHashSet(StringComparer.Ordinal);
        if (selected.Any(id => id == null || !all.Contains(id))) throw new ArgumentException("The canvas selection references a missing node.", nameof(selection));
        return selected;
    }

    private sealed record Unit(CanvasNode Node, CanvasNode[] Members, CanvasRect Bounds);
    private sealed class EditContext
    {
        internal readonly CanvasLayoutState State;
        internal readonly Dictionary<string, CanvasNode> Nodes;
        internal readonly Dictionary<string, CanvasNode> Updated = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Selected, Moving = new(StringComparer.Ordinal);
        internal readonly List<Unit> Units = [];
        private readonly CancellationToken cancellation;

        internal EditContext(CanvasLayoutState state, IReadOnlyCollection<string>? selection, CancellationToken cancellationToken)
        {
            State = state; cancellation = cancellationToken;
            Nodes = state.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            Selected = ResolveSelection(state, selection);
            var children = state.Nodes.Where(node => node.Parent != null).GroupBy(node => node.Parent!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(n => n.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            bool Movable(CanvasNode node)
            {
                if (!Selected.Contains(node.Id) || node.Locked || node.Pinned) return false;
                for (string? parent = node.Parent; parent != null; parent = Nodes[parent].Parent)
                    if (Nodes[parent].Locked || Nodes[parent].Collapsed && (!Selected.Contains(parent) || Nodes[parent].Pinned)) return false;
                return true;
            }
            var free = state.Nodes.Where(Movable).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var node in state.Nodes.OrderBy(node => Depth(node)).ThenBy(node => node.Id, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!free.Contains(node.Id) || Moving.Contains(node.Id)) continue;
                var members = new List<CanvasNode>();
                var pending = new Stack<CanvasNode>(); pending.Push(node);
                while (pending.TryPop(out var item))
                {
                    members.Add(item);
                    if (children.TryGetValue(item.Id, out var descendants)) foreach (var child in descendants) pending.Push(child);
                }
                if (members.Any(member => !free.Contains(member.Id))) continue;
                Moving.UnionWith(members.Select(member => member.Id));
                Units.Add(new(node, members.ToArray(), Bounds(members.Select(CanvasRect.Of))));
            }
        }
        private int Depth(CanvasNode node)
        {
            int depth = 0;
            for (var parent = node.Parent; parent != null; parent = Nodes[parent].Parent) depth++;
            return depth;
        }
        internal bool IsAncestor(string ancestor, string child)
        {
            for (var parent = Nodes[child].Parent; parent != null; parent = Nodes[parent].Parent) if (parent == ancestor) return true;
            return false;
        }
        internal CanvasNode RequireNode(string id) => Nodes.TryGetValue(id, out var node) ? node : throw new ArgumentException("The reference node is missing.", nameof(id));
        internal void Move(Unit unit, double dx, double dy)
        {
            cancellation.ThrowIfCancellationRequested();
            if (unit.Node.Parent is { } parent && !CanvasRect.Of(Nodes[parent]).Contains(unit.Bounds with { X = unit.Bounds.X + dx, Y = unit.Bounds.Y + dy }))
                throw new InvalidOperationException($"The fixed container '{parent}' cannot contain this arrangement. Enlarge it or select its entire subtree.");
            foreach (var member in unit.Members) Updated[member.Id] = member with { X = member.X + dx, Y = member.Y + dy };
        }
    }

    private static CanvasRect Bounds(IEnumerable<CanvasRect> rectangles)
    {
        double x = double.PositiveInfinity, y = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var rect in rectangles) { x = Math.Min(x, rect.X); y = Math.Min(y, rect.Y); right = Math.Max(right, rect.Right); bottom = Math.Max(bottom, rect.Bottom); }
        return double.IsFinite(x) ? new(x, y, right - x, bottom - y) : new(0, 0, 0, 0);
    }

    private static double Snap(double value, double grid) => Math.Round(value / grid, MidpointRounding.AwayFromZero) * grid;

    private static CanvasRect FindFree(CanvasRect desired, CanvasRect[] obstacles, CanvasRect? container, CanvasLayoutSettings settings,
        ref long work, CancellationToken cancellationToken)
    {
        var queue = new PriorityQueue<CanvasRect, (double Distance, double X, double Y)>();
        var seen = new HashSet<(double, double)>();
        void Enqueue(CanvasRect candidate)
        {
            if (!CanvasLayoutValidation.Coordinate(candidate.X) || !CanvasLayoutValidation.Coordinate(candidate.Y) ||
                container is { } bounds && !bounds.Contains(candidate) || !seen.Add((candidate.X, candidate.Y))) return;
            queue.Enqueue(candidate, (Math.Abs(candidate.X - desired.X) + Math.Abs(candidate.Y - desired.Y), candidate.X, candidate.Y));
        }
        if (container is { } parent)
        {
            if (desired.Width > parent.Width + 0.001 || desired.Height > parent.Height + 0.001)
                throw new InvalidOperationException("The selected arrangement does not fit in its fixed container.");
            Enqueue(desired with { X = Math.Clamp(desired.X, parent.X, parent.Right - desired.Width), Y = Math.Clamp(desired.Y, parent.Y, parent.Bottom - desired.Height) });
        }
        else Enqueue(desired);
        double gap = settings.NodeSpacing;
        double RoundUp(double value) => settings.SnapToGrid ? Math.Ceiling(value / settings.GridSize) * settings.GridSize : value;
        double RoundDown(double value) => settings.SnapToGrid ? Math.Floor(value / settings.GridSize) * settings.GridSize : value;
        while (queue.TryDequeue(out var candidate, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CanvasRect? collision = null;
            foreach (var obstacle in obstacles)
            {
                if (++work > 12_000_000) throw new InvalidOperationException("Canvas placement exceeded its work budget. Use a smaller selection.");
                if (candidate.Overlaps(obstacle)) { collision = obstacle; break; }
            }
            if (collision is not { } block) return candidate;
            if (seen.Count > 20_000) break;
            Enqueue(candidate with { X = RoundUp(block.Right + gap) });
            Enqueue(candidate with { X = RoundDown(block.X - candidate.Width - gap) });
            Enqueue(candidate with { Y = RoundUp(block.Bottom + gap) });
            Enqueue(candidate with { Y = RoundDown(block.Y - candidate.Height - gap) });
        }
        throw new InvalidOperationException("The selected nodes cannot be placed without overlapping fixed geometry within the layout budget.");
    }
}
