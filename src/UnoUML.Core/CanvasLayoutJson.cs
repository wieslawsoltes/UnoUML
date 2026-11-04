using System.Text;
using System.Text.Json;

namespace UnoUML.Core;

/// <summary>A strict, reflection-free versioned contract for source comments and browser interop.</summary>
public static class CanvasLayoutJson
{
    public const int Version = 1;

    public static string Serialize(CanvasLayoutState layout)
    {
        layout = CanvasLayoutValidation.Snapshot(layout);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteStartArray("nodes");
            foreach (var node in layout.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", node.Id);
                writer.WriteNumber("x", node.X); writer.WriteNumber("y", node.Y);
                writer.WriteNumber("width", node.Width); writer.WriteNumber("height", node.Height);
                if (node.Parent != null) writer.WriteString("parent", node.Parent);
                writer.WriteBoolean("locked", node.Locked); writer.WriteBoolean("pinned", node.Pinned);
                writer.WriteNumber("rotation", node.Rotation); writer.WriteNumber("zIndex", node.ZIndex);
                writer.WriteBoolean("collapsed", node.Collapsed);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("edges");
            foreach (var edge in layout.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", edge.Id); writer.WriteString("source", edge.Source); writer.WriteString("target", edge.Target);
                writer.WriteStartArray("waypoints");
                foreach (var point in edge.Waypoints ?? []) WritePoint(writer, point);
                writer.WriteEndArray();
                writer.WriteString("routing", edge.Routing);
                writer.WriteBoolean("manual", edge.Manual); writer.WriteBoolean("locked", edge.Locked);
                if (edge.SourcePort is { } source) { writer.WritePropertyName("sourcePort"); WritePoint(writer, source); }
                if (edge.TargetPort is { } target) { writer.WritePropertyName("targetPort"); WritePoint(writer, target); }
                if (edge.LabelOffset is { } offset) { writer.WritePropertyName("labelOffset"); WritePoint(writer, offset); }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            var settings = layout.Settings;
            writer.WriteStartObject("settings");
            writer.WriteString("algorithm", settings.Algorithm switch
            {
                CanvasLayoutAlgorithm.Layered => "layered", CanvasLayoutAlgorithm.Tree => "tree", CanvasLayoutAlgorithm.Radial => "radial",
                CanvasLayoutAlgorithm.Circle => "circle", CanvasLayoutAlgorithm.Grid => "grid", CanvasLayoutAlgorithm.ForceDirected => "forceDirected",
                _ => throw new ArgumentException("Unsupported layout algorithm.")
            });
            writer.WriteString("direction", settings.Direction switch
            {
                CanvasLayoutDirection.Down => "down", CanvasLayoutDirection.Right => "right", CanvasLayoutDirection.Up => "up",
                CanvasLayoutDirection.Left => "left", _ => throw new ArgumentException("Unsupported layout direction.")
            });
            writer.WriteNumber("nodeSpacing", settings.NodeSpacing); writer.WriteNumber("layerSpacing", settings.LayerSpacing);
            writer.WriteNumber("routingPadding", settings.RoutingPadding); writer.WriteNumber("gridColumns", settings.GridColumns);
            writer.WriteNumber("iterations", settings.Iterations); writer.WriteNumber("seed", settings.Seed);
            writer.WriteNumber("gridSize", settings.GridSize); writer.WriteBoolean("snapToGrid", settings.SnapToGrid);
            writer.WriteBoolean("snapToObjects", settings.SnapToObjects); writer.WriteBoolean("automatic", settings.Automatic);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        string json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        if (json.Length > CanvasLayoutLimits.MaxMetadataCharacters) throw new ArgumentException("Canvas metadata exceeds its size limit.");
        return json;
    }

    public static bool TryParse(string? json, out CanvasLayoutState? layout, out string? error)
    {
        layout = null; error = null;
        if (json == null || json.Length > CanvasLayoutLimits.MaxMetadataCharacters)
        {
            error = "Canvas metadata is missing or exceeds its size limit.";
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = Properties(document.RootElement, "version", "nodes", "edges", "settings");
            if (Required(root, "version").GetInt32() != Version) throw new ArgumentException("Unsupported canvas metadata version.");
            var nodes = Required(root, "nodes");
            if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > CanvasLayoutLimits.MaxNodes)
                throw new ArgumentException("Canvas nodes must be a bounded array.");
            var nodeList = nodes.EnumerateArray().Select(ReadNode).ToArray();
            var edges = new List<CanvasEdge>();
            if (root.TryGetValue("edges", out var rawEdges))
            {
                if (rawEdges.ValueKind != JsonValueKind.Array || rawEdges.GetArrayLength() > CanvasLayoutLimits.MaxEdges)
                    throw new ArgumentException("Canvas edges must be a bounded array.");
                int points = 0;
                foreach (var item in rawEdges.EnumerateArray())
                {
                    var edge = ReadEdge(item); points += edge.Waypoints?.Count ?? 0;
                    if (points > CanvasLayoutLimits.MaxTotalWaypoints) throw new ArgumentException("Canvas paths exceed their total point budget.");
                    edges.Add(edge);
                }
            }
            var settings = root.TryGetValue("settings", out var rawSettings) ? ReadSettings(rawSettings) : new CanvasLayoutSettings();
            layout = CanvasLayoutValidation.Snapshot(new(nodeList, edges) { Settings = settings });
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            error = "Invalid canvas metadata: " + ex.Message;
            return false;
        }
    }

    private static void WritePoint(Utf8JsonWriter writer, CanvasPoint point)
    {
        writer.WriteStartObject(); writer.WriteNumber("x", point.X); writer.WriteNumber("y", point.Y); writer.WriteEndObject();
    }

    private static CanvasNode ReadNode(JsonElement element)
    {
        var p = Properties(element, "id", "x", "y", "width", "height", "parent", "locked", "pinned", "rotation", "zIndex", "collapsed");
        return new(Required(p, "id").GetString()!, Required(p, "x").GetDouble(), Required(p, "y").GetDouble(),
            Required(p, "width").GetDouble(), Required(p, "height").GetDouble(),
            p.TryGetValue("parent", out var parent) && parent.ValueKind != JsonValueKind.Null ? parent.GetString() : null,
            Boolean(p, "locked"), Boolean(p, "pinned"), Number(p, "rotation", 0), Integer(p, "zIndex", 0), Boolean(p, "collapsed"));
    }

    private static CanvasEdge ReadEdge(JsonElement element)
    {
        var p = Properties(element, "id", "source", "target", "waypoints", "routing", "manual", "locked", "sourcePort", "targetPort", "labelOffset");
        var points = new List<CanvasPoint>();
        if (p.TryGetValue("waypoints", out var path) && path.ValueKind != JsonValueKind.Null)
        {
            if (path.ValueKind != JsonValueKind.Array || path.GetArrayLength() > CanvasLayoutLimits.MaxWaypointsPerEdge)
                throw new ArgumentException("Canvas edge waypoints must be a bounded array.");
            points.AddRange(path.EnumerateArray().Select(ReadPoint));
        }
        return new(Required(p, "id").GetString()!, Required(p, "source").GetString()!, Required(p, "target").GetString()!, points,
            p.TryGetValue("routing", out var routing) ? routing.GetString()! : "orthogonal", Boolean(p, "manual"), Boolean(p, "locked"),
            OptionalPoint(p, "sourcePort"), OptionalPoint(p, "targetPort"), OptionalPoint(p, "labelOffset"));
    }

    private static CanvasLayoutSettings ReadSettings(JsonElement element)
    {
        var p = Properties(element, "algorithm", "direction", "nodeSpacing", "layerSpacing", "routingPadding", "gridColumns", "iterations", "seed",
            "gridSize", "snapToGrid", "snapToObjects", "automatic");
        return new()
        {
            Algorithm = p.TryGetValue("algorithm", out var algorithm) ? algorithm.GetString() switch
            {
                "layered" => CanvasLayoutAlgorithm.Layered, "tree" => CanvasLayoutAlgorithm.Tree, "radial" => CanvasLayoutAlgorithm.Radial,
                "circle" => CanvasLayoutAlgorithm.Circle, "grid" => CanvasLayoutAlgorithm.Grid, "forceDirected" => CanvasLayoutAlgorithm.ForceDirected,
                _ => throw new ArgumentException("Unsupported canvas layout algorithm.")
            } : CanvasLayoutAlgorithm.Layered,
            Direction = p.TryGetValue("direction", out var direction) ? direction.GetString() switch
            {
                "down" => CanvasLayoutDirection.Down, "right" => CanvasLayoutDirection.Right, "up" => CanvasLayoutDirection.Up,
                "left" => CanvasLayoutDirection.Left, _ => throw new ArgumentException("Unsupported canvas layout direction.")
            } : CanvasLayoutDirection.Down,
            NodeSpacing = Number(p, "nodeSpacing", 32), LayerSpacing = Number(p, "layerSpacing", 64),
            RoutingPadding = Number(p, "routingPadding", 12), GridColumns = Integer(p, "gridColumns", 0),
            Iterations = Integer(p, "iterations", 200), Seed = Integer(p, "seed", 1), GridSize = Number(p, "gridSize", 16),
            SnapToGrid = Boolean(p, "snapToGrid", true), SnapToObjects = Boolean(p, "snapToObjects", true), Automatic = Boolean(p, "automatic")
        };
    }

    private static CanvasPoint ReadPoint(JsonElement element)
    {
        var p = Properties(element, "x", "y");
        return new(Required(p, "x").GetDouble(), Required(p, "y").GetDouble());
    }
    private static CanvasPoint? OptionalPoint(Dictionary<string, JsonElement> p, string name) =>
        p.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null ? ReadPoint(value) : null;
    private static bool Boolean(Dictionary<string, JsonElement> p, string name, bool fallback = false) => p.TryGetValue(name, out var value) ? value.GetBoolean() : fallback;
    private static double Number(Dictionary<string, JsonElement> p, string name, double fallback) => p.TryGetValue(name, out var value) ? value.GetDouble() : fallback;
    private static int Integer(Dictionary<string, JsonElement> p, string name, int fallback) => p.TryGetValue(name, out var value) ? value.GetInt32() : fallback;
    private static JsonElement Required(Dictionary<string, JsonElement> p, string name) =>
        p.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing canvas property '{name}'.");
    private static Dictionary<string, JsonElement> Properties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a canvas JSON object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException($"Unknown canvas property '{property.Name}'.");
            if (!result.TryAdd(property.Name, property.Value)) throw new ArgumentException($"Duplicate canvas property '{property.Name}'.");
        }
        return result;
    }
}
