using System.Globalization;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    // DOT edge IDs are renderer ordinals, not persistent identities. Associate
    // surviving source arrows with their original logical edges before assigning
    // new ordinals. Endpoints alone cannot distinguish parallel occurrences.
    private static CanvasLayoutState? RemapDotCanvasEdits(CanvasLayoutState? layout, DotSyntax before, DotSyntax after,
        IReadOnlyList<DotChange> changes, IReadOnlyDictionary<int, DotSyntax.Edge> generated)
    {
        if (layout == null || !layout.Edges.Any(edge => edge.Id.StartsWith("edge:", StringComparison.Ordinal))) return layout;
        var uses = after.Edges.SelectMany(edge => edge.Uses).GroupBy(use => use.Arrow.Start)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var matches = new Dictionary<DotSyntax.Edge, HashSet<DotSyntax.Edge>>();
        void Match(DotSyntax.Edge original, DotSyntax.Edge current)
        {
            if (original.From.Node.Name != current.From.Node.Name || original.To.Node.Name != current.To.Node.Name) return;
            if (!matches.TryGetValue(original, out var candidates)) matches[original] = candidates = [];
            candidates.Add(current);
        }

        // Normalization replaces a chain/product's arrows and can move its edges
        // past nested subgraph edges. Each emitted singleton records its owner;
        // neither attributes nor relative occurrence order are used as identity.
        foreach (var (position, original) in generated)
            if (uses.TryGetValue(position, out var current) && current.Length == 1) Match(original, current[0].Edge);

        int changeIndex = 0, delta = 0;
        foreach (var group in before.Edges.SelectMany(edge => edge.Uses).GroupBy(use => use.Arrow.Start).OrderBy(group => group.Key))
        {
            int start = group.Key, end = group.First().Arrow.End;
            while (changeIndex < changes.Count && changes[changeIndex].End <= start)
            {
                var change = changes[changeIndex++];
                delta += change.Text.Length - (change.End - change.Start);
            }
            if (changeIndex < changes.Count && changes[changeIndex].Start < end || !uses.TryGetValue(start + delta, out var current)) continue;
            // A surviving arrow can still be an endpoint product. Removing a
            // node removes every occurrence involving that node; remaining
            // identical pairs retain their order within this exact source arrow.
            var available = current.GroupBy(use => (use.From.Node.Name, use.To.Node.Name))
                .ToDictionary(pair => pair.Key, pair => new Queue<DotSyntax.EdgeUse>(pair));
            foreach (var original in group)
                if (available.TryGetValue((original.From.Node.Name, original.To.Node.Name), out var candidates) && candidates.TryDequeue(out var use))
                    Match(original.Edge, use.Edge);
        }

        var indices = after.Edges.Select((edge, index) => (edge, index)).ToDictionary(pair => pair.edge, pair => pair.index);
        // Ambiguous or stale provenance is discarded instead of attaching one
        // stored route to two edges, or two stored routes to one edge.
        var map = matches.Where(pair => pair.Value.Count == 1).Select(pair => (Original: pair.Key, Current: pair.Value.Single()))
            .GroupBy(pair => pair.Current).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Single().Original, group => indices[group.Key]);
        var edges = new List<CanvasEdge>();
        foreach (var route in layout.Edges)
        {
            if (!route.Id.StartsWith("edge:", StringComparison.Ordinal)) { edges.Add(route); continue; }
            if (!int.TryParse(route.Id.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                (uint)index >= (uint)before.Edges.Count || route.Id != "edge:" + index.ToString(CultureInfo.InvariantCulture)) continue;
            var original = before.Edges[index];
            if (route.Source != "node:" + original.From.Node.Name || route.Target != "node:" + original.To.Node.Name ||
                !map.TryGetValue(original, out int next)) continue;
            edges.Add(route with { Id = "edge:" + next.ToString(CultureInfo.InvariantCulture) });
        }
        return layout with { Edges = edges };
    }
}
