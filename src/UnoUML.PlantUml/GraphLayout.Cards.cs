using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private sealed record SizedCard(string Id, string Label, int Line, int Width, int Height, string? Parent = null, bool Group = false);
    private sealed record SizedLink(string From, string To, string Label, int Line, string Arrow, string Direction, int LabelWidth, int LabelHeight);

    // Semantic libraries supply the appearance and text metrics. They share the graph's
    // ordering, component packing and obstacle routing, without generating intermediate UML.
    private static Graph? ArrangeCards(IEnumerable<SizedCard> cards, List<SizedLink> links, string title, bool horizontal)
    {
        var graph = new Graph { Title = title, Horizontal = horizontal };
        var byId = new Dictionary<string, Node>(StringComparer.Ordinal);
        int padX = Math.Max(0, links.Select(r => r.LabelWidth).DefaultIfEmpty(0).Max() - 46);
        int padY = Math.Max(0, links.Select(r => r.LabelHeight).DefaultIfEmpty(0).Max() - 46);
        foreach (var card in cards)
        {
            // Scope headers are measured by the semantic renderer. Child extents, ranks
            // and relation projection are handled by the same compound layout as UML.
            var parent = card.Parent == null ? graph.Root : byId.GetValueOrDefault(card.Parent);
            if (parent == null) return null;
            var n = new Node(card.Group ? "measured-group" : "measured-card", card.Id, card.Label, card.Line, parent)
            { Width = card.Width + (card.Group ? 0 : padX), Height = card.Height + (card.Group ? 0 : padY) };
            byId.Add(card.Id, n);
            graph.Nodes.Add(n);
            parent.Children.Add(n);
        }
        foreach (var r in links)
            graph.Edges.Add(new(byId[r.From], byId[r.To], r.Arrow, r.Label, "", "", r.Line, r.Direction, false));
        if (!Arrange(graph)) return null;
        foreach (var n in graph.Nodes.Where(n => n.Kind == "measured-card")) { n.X += padX / 2; n.Y += padY / 2; n.Width -= padX; n.Height -= padY; }
        return graph;
    }

    private static void SemanticLabel(StringBuilder s, int x, int y, string value, int size, string color, bool bold = false, bool italic = false)
    {
        SvgText.Append(s, x, y, value, size, bold: bold, italic: italic, color: color);
    }
}
