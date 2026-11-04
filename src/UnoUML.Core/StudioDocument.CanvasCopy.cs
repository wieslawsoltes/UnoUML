using System.Globalization;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    /// <summary>Optional geometry carried with a source selection, never an active diagram layout comment.</summary>
    public const string CanvasSelectionMarker = "' @unouml:canvas-selection ";

    // Only these families publish the alias/qualified-name identities below.
    // Content-derived activity/tree IDs and opaque renderer IDs keep their
    // ordinary source-only copy behavior until an explicit identity map exists.
    private bool AliasCanvasFamily => Page.Family is "Class" or "Object" or "Component" or "Deployment" or "Use case" or "State" or "C4" or "ArchiMate";
    private bool FlatCanvasAliases => Page.Family is "C4" or "ArchiMate";
    private string CanvasObjectId(DiagramObject item) => FlatCanvasAliases ? item.Alias : Qualified(item);
    private string CanvasParentPrefix(int line) => !FlatCanvasAliases && Objects.FirstOrDefault(item => item.Line == line) is { Alias.Length: > 0 } parent ? Qualified(parent) + "." : "";

    private sealed record CanvasFragmentMap(Dictionary<string, string> Nodes, Dictionary<string, string> Aliases);

    private bool CanvasCopyLayout(out CanvasLayoutState? layout)
    {
        // Implicit source-only pages cannot carry active canvas metadata, but
        // still retain all their ordinary source copy/duplicate operations.
        layout = null;
        if (!Index.Lines[Page.StartLine].Code.TrimStart('\uFEFF', ' ', '\t').StartsWith("@start", StringComparison.OrdinalIgnoreCase)) return true;
        TryGetCanvasLayout(out layout, out string? error);
        return error == null;
    }

    private Dictionary<string, string> CanvasFragmentNodes(List<DiagramObject> items, CanvasFragmentMap remap, bool duplicate)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!AliasCanvasFamily) return result;
        string prefix = duplicate ? CanvasParentPrefix(items[0].ParentLine) : "";
        foreach (var item in Objects.Where(item => item.IsConnectable && !item.Alias.StartsWith('"') && Inside(item, items)))
            if (remap.Aliases.TryGetValue(item.Alias, out string? fragmentId) && remap.Nodes.TryGetValue(fragmentId, out string? copiedId))
                result[CanvasObjectId(item)] = duplicate ? prefix + copiedId : fragmentId;
        return result;
    }

    private static Dictionary<string, string> CanvasAliasEdges(CanvasLayoutState source, IReadOnlyDictionary<string, string> nodes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edge in source.Edges)
        {
            if (!nodes.TryGetValue(edge.Source, out string? from) || !nodes.TryGetValue(edge.Target, out string? to)) continue;
            string prefix = edge.Source + "->" + edge.Target + ":";
            // This is the native alias-graph contract. Opaque IDs and synthetic
            // content identities are deliberately not inferred from spelling.
            if (!edge.Id.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(edge.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                index < 0 || edge.Id[prefix.Length..] != index.ToString(CultureInfo.InvariantCulture)) continue;
            result[edge.Id] = from + "->" + to + ":" + index.ToString(CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static CanvasLayoutState CanvasSelectionGeometry(CanvasLayoutState source, IReadOnlyDictionary<string, string> nodeMap,
        IReadOnlyDictionary<string, string> edgeMap, bool retainOutsideParents)
    {
        var present = source.Nodes.Where(node => nodeMap.ContainsKey(node.Id)).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = source.Nodes.Where(node => present.Contains(node.Id)).Select(node => node with
        {
            Id = nodeMap[node.Id], Parent = node.Parent is { } parent && present.Contains(parent) ? nodeMap[parent] : retainOutsideParents ? node.Parent : null
        }).ToArray();
        var edges = source.Edges.Where(edge => edgeMap.ContainsKey(edge.Id) && present.Contains(edge.Source) && present.Contains(edge.Target))
            .Select(edge => edge with { Id = edgeMap[edge.Id], Source = nodeMap[edge.Source], Target = nodeMap[edge.Target], Waypoints = edge.Waypoints?.ToArray() }).ToArray();
        return new(nodes, edges) { Settings = source.Settings };
    }

    private static CanvasLayoutState AppendCanvasCopy(CanvasLayoutState? target, CanvasLayoutState copied)
    {
        target ??= new([], []);
        if (copied.Nodes.Count == 0) return target;
        var replacing = copied.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var existing = target.Nodes.Where(node => !replacing.Contains(node.Id)).ToArray();
        var replacingEdges = copied.Edges.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
        var existingEdges = target.Edges.Where(edge => !replacingEdges.Contains(edge.Id)).ToArray();
        if (existing.Length + copied.Nodes.Count > CanvasLayoutLimits.MaxNodes || existingEdges.Length + copied.Edges.Count > CanvasLayoutLimits.MaxEdges)
            throw new ArgumentException("The copied canvas exceeds its object limit.");

        // Two grid cells is predictable and preserves relative geometry. Repeated
        // pastes advance along that diagonal instead of exactly covering a prior
        // copy. This bounded pass does not run an automatic layout or move originals.
        double step = target.Settings.GridSize * 2;
        var occupied = new HashSet<int>();
        foreach (var node in copied.Nodes)
        foreach (var other in existing)
        {
            double x = (other.X - node.X) / step, y = (other.Y - node.Y) / step;
            if (x >= 1 && x <= CanvasLayoutLimits.MaxNodes * CanvasLayoutLimits.MaxNodes &&
                Math.Abs(x - y) < 0.000001 && Math.Abs(x - Math.Round(x)) < 0.000001) occupied.Add((int)Math.Round(x));
        }
        int multiple = 1;
        while (occupied.Contains(multiple)) multiple++;
        double offset = step * multiple;
        var nodes = copied.Nodes.Select(node => node with { X = node.X + offset, Y = node.Y + offset }).ToArray();
        var edges = copied.Edges.Select(edge => edge with
        {
            Waypoints = edge.Waypoints?.Select(point => new CanvasPoint(point.X + offset, point.Y + offset)).ToArray()
        }).ToArray();
        return CanvasLayoutValidation.Snapshot(target with { Nodes = [.. existing, .. nodes], Edges = [.. existingEdges, .. edges] });
    }

    private bool CommitCanvasSourceEdit(Func<StudioDocument, bool> edit, Func<CanvasLayoutState?, CanvasLayoutState?>? transform)
    {
        if (transform == null) return edit(this);
        if (!CanvasCopyLayout(out var layout)) return false;
        CanvasLayoutState? next;
        string? nextJson, currentJson;
        try
        {
            next = transform(layout);
            if (next != null) next = CanvasLayoutValidation.Snapshot(next);
            nextJson = next == null ? null : CanvasLayoutJson.Serialize(next);
            currentJson = layout == null ? null : CanvasLayoutJson.Serialize(layout);
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException) { return false; }
        var preview = Fork();
        if (!edit(preview)) return false;
        if (next != null && nextJson != currentJson &&
            !preview.TrySetCanvasLayout(next, preview.HistoryState, out _)) return false;
        int start = 0, end = Source.Length, nextEnd = preview.Source.Length;
        while (start < end && start < nextEnd && Source[start] == preview.Source[start]) start++;
        while (end > start && nextEnd > start && Source[end - 1] == preview.Source[nextEnd - 1]) { end--; nextEnd--; }
        return Patch(start, end, preview.Source[start..nextEnd]);
    }

    private bool CommitAliasCanvasCopy(Func<StudioDocument, bool> edit, IReadOnlyDictionary<string, string> nodeMap,
        CanvasLayoutState? clipboard = null, bool retainOutsideParents = true, bool copyEdges = true) =>
        CommitCanvasSourceEdit(edit, nodeMap.Count == 0 ? null : current =>
        {
            var source = clipboard ?? current;
            if (source == null) return current;
            var copy = CanvasSelectionGeometry(source, nodeMap, copyEdges ? CanvasAliasEdges(source, nodeMap) : new Dictionary<string, string>(), retainOutsideParents);
            return copy.Nodes.Count == 0 ? current : AppendCanvasCopy(current, copy);
        });

    private string? WithCanvasSelection(string source, IReadOnlyDictionary<string, string> nodeMap,
        Func<CanvasLayoutState, IReadOnlyDictionary<string, string>>? edges = null)
    {
        if (nodeMap.Count == 0) return source;
        if (!CanvasCopyLayout(out var layout)) return null;
        if (layout == null) return source;
        var copy = CanvasSelectionGeometry(layout, nodeMap, edges?.Invoke(layout) ?? CanvasAliasEdges(layout, nodeMap), false);
        if (copy.Nodes.Count == 0) return source;
        try { return CanvasSelectionMarker + CanvasLayoutJson.Serialize(copy) + Index.NewLine + source; }
        catch (ArgumentException) { return null; }
    }

    private static bool ReadCanvasSelection(string source, out string body, out CanvasLayoutState? layout)
    {
        body = source; layout = null;
        if (!source.StartsWith(CanvasSelectionMarker, StringComparison.Ordinal)) return true;
        int lineEnd = source.IndexOfAny(['\r', '\n']);
        if (lineEnd < 0 || !CanvasLayoutJson.TryParse(source[CanvasSelectionMarker.Length..lineEnd], out layout, out _)) return false;
        int next = lineEnd + 1;
        if (source[lineEnd] == '\r' && next < source.Length && source[next] == '\n') next++;
        body = source[next..];
        return !string.IsNullOrWhiteSpace(body) && !body.TrimStart().StartsWith(CanvasSelectionMarker.TrimEnd(), StringComparison.Ordinal);
    }

    private static bool CanvasClipboardMatches(CanvasLayoutState? layout, IReadOnlyDictionary<string, string> nodes,
        IReadOnlyDictionary<string, string>? edges = null) => layout == null || layout.Nodes.All(node => nodes.ContainsKey(node.Id)) &&
        (edges == null || layout.Edges.All(edge => edges.ContainsKey(edge.Id)));
}
