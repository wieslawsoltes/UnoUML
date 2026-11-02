using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private sealed record SizedCard(string Id, string Label, int Line, int Width, int Height);
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
            var n = new Node("measured-card", card.Id, card.Label, card.Line, graph.Root) { Width = card.Width + padX, Height = card.Height + padY };
            byId.Add(card.Id, n);
            graph.Nodes.Add(n);
            graph.Root.Children.Add(n);
        }
        foreach (var r in links)
            graph.Edges.Add(new(byId[r.From], byId[r.To], r.Arrow, r.Label, "", "", r.Line, r.Direction, false));
        if (!Arrange(graph)) return null;
        foreach (var n in graph.Nodes) { n.X += padX / 2; n.Y += padY / 2; n.Width -= padX; n.Height -= padY; }
        return graph;
    }

    private static void SemanticLabel(StringBuilder s, int x, int y, string value, int size, string color, bool bold = false, bool italic = false)
    {
        if (value.Length == 0) return;
        s.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" text-anchor=\"middle\" font-size=\"{size}\" fill=\"{color}\"{(bold ? " font-weight=\"bold\"" : "")}{(italic ? " font-style=\"italic\"" : "")}>{SvgText.Escape(value)}</text>");
    }
}
