using System.Text;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    private bool SharedDotLabel(DotSourceLocation location)
    {
        if (location.LabelStart < 0 || DotStructure is not { } syntax) return false;
        var attributes = location.Kind == "node" ? syntax.Nodes.Values.Select(node => node.Attributes) :
            location.Kind == "edge" ? syntax.Edges.Select(edge => edge.Attributes) : [];
        int owners = 0;
        foreach (var values in attributes)
            if (values.TryGetValue("label", out var label) && label.Start == location.LabelStart && ++owners > 1) return true;
        return false;
    }

    private bool ReplaceDotLabel(DotSourceLocation location, string value)
    {
        if (DotStructure is not { } syntax || !syntax.Attributes.Any(attribute => attribute.Name == "label" &&
            attribute.Value.Start == location.LabelStart && attribute.Value.End - attribute.Value.Start == location.LabelLength)) return false;
        var changes = new List<DotChange>();
        // Quoted-string concatenation may contain comments. Replace the tokens,
        // retaining every intervening comment and its original line ending.
        RemoveDotTokens(syntax, changes, location.LabelStart, location.LabelStart + location.LabelLength, value);
        return CommitDotChanges(syntax, changes, next => next.Nodes.Count == syntax.Nodes.Count && next.Edges.Count == syntax.Edges.Count);
    }

    private static DotSyntax.Edge? LiteralDotEdge(DotSyntax syntax, DotSourceLocation location)
    {
        if (location.Kind != "edge" || location.Merged) return null;
        return syntax.Edges.FirstOrDefault(edge => edge.First.Start == location.Start && edge.First.End - edge.First.Start == location.Length &&
            edge.First.Lists.Count == 2 && edge.First.Lists.All(list => list.Nodes.Count == 1) &&
            edge.From.Node.Name == location.FromId && edge.To.Node.Name == location.ToId &&
            edge.From.Name.Start == location.FromStart && edge.From.End - edge.From.Name.Start == location.FromLength &&
            edge.To.Name.Start == location.ToStart && edge.To.End - edge.To.Name.Start == location.ToLength);
    }

    private bool RetargetDotEdge(DotSyntax syntax, DotSyntax.Edge edge, DotSyntax.Node target, bool from)
    {
        var endpoint = from ? edge.From : edge.To;
        if (endpoint.Node == target || !edge.First.Scope.Members.Contains(target.Name)) return false;
        var changes = new List<DotChange>();
        // Replace only the identifier tokens. A colon in a comment or an HTML
        // identifier is not a port delimiter, and the real port spelling survives.
        RemoveDotTokens(syntax, changes, endpoint.Name.Start, endpoint.Name.End, DotName(target.Name));
        var declarations = DotEndpointDeclarations(edge);
        if (target.Token.Start >= edge.First.Start && target != edge.From.Node && target != edge.To.Node)
        {
            if (declarations.Length > 0) declarations.Append(' ');
            declarations.Append(DotName(target.Name)).Append(DotAttributes(syntax, target.Attributes)).Append(';');
        }
        if (declarations.Length > 0)
            changes.Add(new(edge.First.Start, edge.First.Start, declarations.Append(DotNewLine).Append("  ").ToString()));
        int changedEdge = syntax.Edges.IndexOf(edge);
        // Reject strict-edge coalescing and early declarations that would acquire
        // inherited defaults absent from the original target. Neither is an edit
        // of just the selected connection.
        return CommitDotChanges(syntax, changes, next => SameDotNodes(syntax, next) &&
            SameDotEdges(syntax, next, changedEdge, false, target.Name, from));
    }

    private bool RemoveDotConnection(DotSyntax syntax, DotSyntax.Edge edge)
    {
        var changes = new List<DotChange>();
        // An edge can be the first declaration of either endpoint. Retain those
        // node declarations in the same scope so deleting a connector cannot
        // delete its nodes or make them acquire later node defaults.
        RemoveDotTokens(syntax, changes, edge.First.Start, edge.First.End, DotEndpointDeclarations(edge).ToString());
        int removedEdge = syntax.Edges.IndexOf(edge);
        return CommitDotChanges(syntax, changes, next => SameDotNodes(syntax, next) &&
            SameDotEdges(syntax, next, removedEdge, true));
    }

    private static StringBuilder DotEndpointDeclarations(DotSyntax.Edge edge)
    {
        var text = new StringBuilder();
        void Retain(DotSyntax.Node node)
        {
            if (!node.Appearances.TryGetValue(edge.First.Scope, out var first) || first != edge.First) return;
            if (text.Length > 0) text.Append(' ');
            text.Append(DotName(node.Name)).Append(';');
        }
        Retain(edge.From.Node);
        if (edge.To.Node != edge.From.Node) Retain(edge.To.Node);
        return text;
    }

    private static bool SameDotNodes(DotSyntax before, DotSyntax after) => before.Nodes.Count == after.Nodes.Count &&
        before.Nodes.All(pair => after.Nodes.TryGetValue(pair.Key, out var node) && SameDotAttributes(pair.Value.Attributes, node.Attributes)) &&
        before.Scopes.Count == after.Scopes.Count && before.Scopes.Zip(after.Scopes).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.Members.SetEquals(pair.Second.Members));

    private static bool SameDotAttributes(Dictionary<string, DotSyntax.Token> before, Dictionary<string, DotSyntax.Token> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value.Value == pair.Value.Value &&
            (value.Kind == DotSyntax.TokenKind.Html) == (pair.Value.Kind == DotSyntax.TokenKind.Html));

    private static bool SameDotEdges(DotSyntax before, DotSyntax after, int changedEdge, bool removed, string? target = null, bool from = false)
    {
        if (after.Edges.Count != before.Edges.Count - (removed ? 1 : 0)) return false;
        for (int index = 0, next = 0; index < before.Edges.Count; index++)
        {
            if (index == changedEdge && removed) continue;
            var previous = before.Edges[index];
            var current = after.Edges[next++];
            if (current.From.Node.Name != (index == changedEdge && from ? target : previous.From.Node.Name) ||
                current.To.Node.Name != (index == changedEdge && !from ? target : previous.To.Node.Name) ||
                current.From.Port != previous.From.Port || current.From.Compass != previous.From.Compass ||
                current.To.Port != previous.To.Port || current.To.Compass != previous.To.Compass ||
                !SameDotAttributes(previous.Attributes, current.Attributes)) return false;
        }
        return true;
    }
}
