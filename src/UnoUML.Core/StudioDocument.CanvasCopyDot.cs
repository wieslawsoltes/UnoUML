using System.Globalization;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    private static Dictionary<string, string> CanvasDotNodes(IReadOnlyDictionary<string, string> names) =>
        names.ToDictionary(pair => "node:" + pair.Key, pair => "node:" + pair.Value, StringComparer.Ordinal);

    private static Dictionary<string, string> CanvasDotEdges(DotSyntax before, DotSyntax after, IReadOnlyDictionary<string, string>? names = null,
        CanvasLayoutState? saved = null)
    {
        static (string, string, string, string, string, string) Key(DotSyntax.Edge edge, string from, string to) =>
            (from, to, edge.From.Port, edge.From.Compass, edge.To.Port, edge.To.Compass);
        var available = after.Edges.Select((edge, index) => (edge, index))
            .GroupBy(pair => Key(pair.edge, pair.edge.From.Node.Name, pair.edge.To.Node.Name))
            .ToDictionary(group => group.Key, group => new Queue<int>(group.Select(pair => pair.index)));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var geometry = saved?.Edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        for (int index = 0; index < before.Edges.Count; index++)
        {
            var edge = before.Edges[index];
            string from = edge.From.Node.Name, to = edge.To.Node.Name;
            if (names != null)
            {
                if (!names.TryGetValue(from, out string? first) || !names.TryGetValue(to, out string? second)) continue;
                from = first; to = second;
            }
            if (available.TryGetValue(Key(edge, from, to), out var indices) && indices.TryDequeue(out int next))
            {
                // Consume every parallel occurrence even when only a later edge
                // has saved geometry, so partial snapshots retain their ordinal.
                string id = "edge:" + index.ToString(CultureInfo.InvariantCulture);
                if (geometry == null || geometry.TryGetValue(id, out var route) &&
                    route.Source == "node:" + edge.From.Node.Name && route.Target == "node:" + edge.To.Node.Name)
                    result[id] = "edge:" + next.ToString(CultureInfo.InvariantCulture);
            }
        }
        return result;
    }

    private static CanvasLayoutState? ReindexDotCanvas(CanvasLayoutState? layout, DotSyntax before, DotSyntax after)
    {
        if (layout == null) return null;
        // Validate against the original semantic edge before assigning its new
        // ordinal. Stale records can otherwise collide with shifted survivors.
        var indices = CanvasDotEdges(before, after, saved: layout);
        return layout with
        {
            Edges = layout.Edges.Where(edge => !edge.Id.StartsWith("edge:", StringComparison.Ordinal) || indices.ContainsKey(edge.Id))
                .Select(edge => indices.TryGetValue(edge.Id, out string? id) ? edge with { Id = id } : edge).ToArray()
        };
    }

    private static CanvasLayoutState? CopyDotCanvas(CanvasLayoutState? target, CanvasLayoutState? source, DotSyntax sourceSyntax,
        DotSyntax oldTarget, DotSyntax newTarget, IReadOnlyDictionary<string, string> names, bool retainOutsideParents)
    {
        var current = ReindexDotCanvas(target, oldTarget, newTarget);
        if (source == null) return current;
        var nodes = CanvasDotNodes(names);
        var edges = CanvasDotEdges(sourceSyntax, newTarget, names, source);
        var copy = CanvasSelectionGeometry(source, nodes, edges, retainOutsideParents);
        return copy.Nodes.Count == 0 ? current : AppendCanvasCopy(current, copy);
    }
}
