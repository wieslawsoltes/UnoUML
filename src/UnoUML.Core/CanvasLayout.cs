namespace UnoUML.Core;

/// <summary>A point in diagram coordinates; port coordinates are normalized to the node's unrotated bounds.</summary>
public readonly record struct CanvasPoint(double X, double Y);

/// <summary>Absolute, unrotated bounds. Rotation is clockwise in degrees around the center.</summary>
public sealed record CanvasNode(string Id, double X, double Y, double Width, double Height,
    string? Parent = null, bool Locked = false, bool Pinned = false, double Rotation = 0,
    int ZIndex = 0, bool Collapsed = false);

/// <summary>Waypoints describe the complete polyline, including its docked first and last points.</summary>
public sealed record CanvasEdge(string Id, string Source, string Target,
    IReadOnlyList<CanvasPoint>? Waypoints = null, string Routing = "orthogonal", bool Manual = false,
    bool Locked = false, CanvasPoint? SourcePort = null, CanvasPoint? TargetPort = null,
    CanvasPoint? LabelOffset = null);

public enum CanvasLayoutAlgorithm { Layered, Tree, Radial, Circle, Grid, ForceDirected }
public enum CanvasLayoutDirection { Down, Right, Up, Left }
public enum CanvasAlignment { Left, Center, Right, Top, Middle, Bottom }
public enum CanvasAxis { Horizontal, Vertical }
public enum CanvasSize { Width, Height, Both }

/// <summary>Serializable layout and editor preferences. Settings never override locked or pinned nodes.</summary>
public sealed record CanvasLayoutSettings
{
    public CanvasLayoutAlgorithm Algorithm { get; init; } = CanvasLayoutAlgorithm.Layered;
    public CanvasLayoutDirection Direction { get; init; } = CanvasLayoutDirection.Down;
    public double NodeSpacing { get; init; } = 32;
    public double LayerSpacing { get; init; } = 64;
    public double RoutingPadding { get; init; } = 12;
    public int GridColumns { get; init; }
    public int Iterations { get; init; } = 200;
    public int Seed { get; init; } = 1;
    public double GridSize { get; init; } = 16;
    public bool SnapToGrid { get; init; } = true;
    public bool SnapToObjects { get; init; } = true;
    public bool Automatic { get; init; }
}

/// <summary>A snapshot, independent of any renderer. Engines and serialization never mutate the supplied collections.</summary>
public sealed record CanvasLayoutState(IReadOnlyList<CanvasNode> Nodes, IReadOnlyList<CanvasEdge> Edges)
{
    public CanvasLayoutSettings Settings { get; init; } = new();
}

/// <summary>Bounds on untrusted source metadata and interactive work. Exceeding a bound fails atomically.</summary>
public static class CanvasLayoutLimits
{
    public const int MaxNodes = 2048;
    public const int MaxEdges = 8192;
    public const int MaxWaypointsPerEdge = 256;
    public const int MaxTotalWaypoints = 65536;
    public const int MaxMetadataCharacters = 4 * 1024 * 1024;
    public const int MaxIdLength = 512;
    public const double MaxCoordinate = 10_000_000;
    public const double MaxDimension = 1_000_000;
    public const int MaxForceNodes = 512;
    public const long MaxForceInteractions = 32_000_000;
    public const int MaxRoutingSearchStates = 60_000;
    public const long MaxRoutingWork = 12_000_000;
}

internal readonly record struct CanvasRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public CanvasRect Inflate(double padding) => new(X - padding, Y - padding, Width + padding * 2, Height + padding * 2);
    public bool Contains(CanvasRect other, double tolerance = 0.001) => other.X >= X - tolerance &&
        other.Y >= Y - tolerance && other.Right <= Right + tolerance && other.Bottom <= Bottom + tolerance;
    public bool Overlaps(CanvasRect other, double gap = 0) => X < other.Right + gap - 0.001 &&
        Right + gap > other.X + 0.001 && Y < other.Bottom + gap - 0.001 && Bottom + gap > other.Y + 0.001;
    public static CanvasRect Of(CanvasNode node)
    {
        double radians = node.Rotation % 360 * Math.PI / 180;
        double width = Math.Abs(Math.Cos(radians)) * node.Width + Math.Abs(Math.Sin(radians)) * node.Height;
        double height = Math.Abs(Math.Sin(radians)) * node.Width + Math.Abs(Math.Cos(radians)) * node.Height;
        return new(node.X + (node.Width - width) / 2, node.Y + (node.Height - height) / 2, width, height);
    }
}

internal static class CanvasLayoutValidation
{
    public static CanvasLayoutState Snapshot(CanvasLayoutState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Nodes);
        ArgumentNullException.ThrowIfNull(state.Edges);
        ArgumentNullException.ThrowIfNull(state.Settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (state.Nodes.Count > CanvasLayoutLimits.MaxNodes || state.Edges.Count > CanvasLayoutLimits.MaxEdges)
            throw new ArgumentException($"Canvas layout supports at most {CanvasLayoutLimits.MaxNodes} nodes and {CanvasLayoutLimits.MaxEdges} edges.");
        ValidateSettings(state.Settings);
        var nodes = new Dictionary<string, CanvasNode>(StringComparer.Ordinal);
        foreach (var node in state.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is null || !Id(node.Id) || !nodes.TryAdd(node.Id, node))
                throw new ArgumentException("Canvas node IDs must be nonempty, unique, bounded strings without control characters.");
            if (!Coordinate(node.X) || !Coordinate(node.Y) || !Dimension(node.Width) || !Dimension(node.Height) ||
                !Coordinate(node.Rotation) || !Coordinate(node.X + node.Width) || !Coordinate(node.Y + node.Height))
                throw new ArgumentException($"Node '{node.Id}' has invalid or out-of-range geometry.");
        }
        foreach (var node in nodes.Values)
        {
            if (node.Parent != null && (!nodes.ContainsKey(node.Parent) || node.Parent == node.Id))
                throw new ArgumentException($"Node '{node.Id}' has a missing or self-referencing parent.");
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            var chain = new HashSet<string>(StringComparer.Ordinal);
            for (CanvasNode? current = node; current != null && !visited.Contains(current.Id); current = current.Parent == null ? null : nodes[current.Parent])
                if (!chain.Add(current.Id)) throw new ArgumentException("Canvas parent relationships must be acyclic.");
            visited.UnionWith(chain);
        }
        int totalWaypoints = 0;
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<CanvasEdge>(state.Edges.Count);
        foreach (var edge in state.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edge is null || !Id(edge.Id) || !edgeIds.Add(edge.Id))
                throw new ArgumentException("Canvas edge IDs must be nonempty, unique, bounded strings without control characters.");
            if (edge.Source == null || edge.Target == null || !nodes.ContainsKey(edge.Source) || !nodes.ContainsKey(edge.Target))
                throw new ArgumentException($"Edge '{edge.Id}' references a missing node.");
            if (edge.Routing is not ("orthogonal" or "straight" or "polyline" or "curved"))
                throw new ArgumentException($"Edge '{edge.Id}' has an unsupported routing style.");
            if (!Port(edge.SourcePort) || !Port(edge.TargetPort) || edge.LabelOffset is { } label && !Point(label))
                throw new ArgumentException($"Edge '{edge.Id}' has invalid port or label coordinates.");
            int count = edge.Waypoints?.Count ?? 0;
            totalWaypoints += count;
            if (count == 1 || count > CanvasLayoutLimits.MaxWaypointsPerEdge || totalWaypoints > CanvasLayoutLimits.MaxTotalWaypoints)
                throw new ArgumentException("Edge paths must be empty or contain between two and 256 points, within the total point budget.");
            if (edge.Waypoints?.Any(p => !Point(p)) == true)
                throw new ArgumentException($"Edge '{edge.Id}' has invalid waypoint coordinates.");
            edges.Add(edge with { Waypoints = edge.Waypoints?.ToArray() ?? [] });
        }
        return new(state.Nodes.ToArray(), edges.ToArray()) { Settings = state.Settings };
    }

    private static bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CanvasLayoutLimits.MaxIdLength && !value.Any(char.IsControl);
    internal static bool Coordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= CanvasLayoutLimits.MaxCoordinate;
    internal static bool Point(CanvasPoint point) => Coordinate(point.X) && Coordinate(point.Y);
    private static bool Dimension(double value) => double.IsFinite(value) && value >= 1 && value <= CanvasLayoutLimits.MaxDimension;
    private static bool Port(CanvasPoint? point) => point == null || Point(point.Value) && point.Value.X is >= 0 and <= 1 && point.Value.Y is >= 0 and <= 1;
    internal static void ValidateSettings(CanvasLayoutSettings settings)
    {
        if (!Enum.IsDefined(settings.Algorithm) || !Enum.IsDefined(settings.Direction) ||
            !Spacing(settings.NodeSpacing) || !Spacing(settings.LayerSpacing) || !Spacing(settings.RoutingPadding) ||
            !double.IsFinite(settings.GridSize) || settings.GridSize is < 1 or > 10000 ||
            settings.GridColumns is < 0 or > CanvasLayoutLimits.MaxNodes || settings.Iterations is < 1 or > 1000)
            throw new ArgumentException("Canvas layout settings are outside their supported range.");
    }
    private static bool Spacing(double value) => double.IsFinite(value) && value is >= 0 and <= 10000;
}
