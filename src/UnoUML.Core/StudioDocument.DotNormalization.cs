using System.Globalization;
using System.Text;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    /// <summary>Expands the declarations of a logical edge into independent statements, preserving their scope and evaluation order.</summary>
    public bool NormalizeDotEdge(DotObjectEdit item)
    {
        if (!CurrentDot(item) || DotStructure is not { } syntax || ResolvedDotEdge(syntax, item.Location) is not { } edge ||
            !edge.Uses.Any(use => NeedsDotStatementNormalization(use.Statement))) return false;
        var changes = new List<DotChange>();
        NormalizeDotStatements(syntax, changes, [edge]);
        return CommitDotChanges(syntax, changes, next => SameDotNodes(syntax, next) && EquivalentDotEdges(syntax, next));
    }

    private static bool NeedsDotNormalization(DotSyntax.Edge edge) => edge.Uses.Count != 1 || NeedsDotStatementNormalization(edge.First);

    private static bool NeedsDotStatementNormalization(DotSyntax.Statement statement) => statement.Uses.Count != 1 ||
        statement.Lists.Count != 2 || statement.Lists.Any(list => list.Nodes.Count != 1);

    private static DotSyntax.Edge? ResolvedDotEdge(DotSyntax syntax, DotSourceLocation location)
    {
        if (location.Kind != "edge" || !location.Id.StartsWith("edge:", StringComparison.Ordinal) ||
            !int.TryParse(location.Id.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
            (uint)index >= (uint)syntax.Edges.Count) return null;
        var edge = syntax.Edges[index];
        var last = edge.Uses[^1];
        return location.Merged == (edge.Uses.Count > 1) && last.Statement.Start == location.Start && last.Statement.End - last.Statement.Start == location.Length &&
            last.Arrow.Start == location.ArrowStart && last.Arrow.End - last.Arrow.Start == location.ArrowLength &&
            edge.From.Node.Name == location.FromId && edge.To.Node.Name == location.ToId ? edge : null;
    }

    private bool ChangeSharedDotEdge(DotSyntax syntax, DotSyntax.Edge edge, bool remove = false,
        DotSyntax.Node? target = null, bool from = false, string? label = null)
    {
        if (target != null && (target == (from ? edge.From.Node : edge.To.Node) ||
            edge.Uses.Any(use => !use.Statement.Scope.Members.Contains(target.Name)))) return false;
        var changes = new List<DotChange>();
        Dictionary<DotSyntax.Edge, Dictionary<string, string>>? overrides = label == null ? null :
            new() { [edge] = new(StringComparer.Ordinal) { ["label"] = label } };
        NormalizeDotStatements(syntax, changes, [edge], remove ? edge : null, target == null ? null : edge, target, from, overrides);
        return CommitDotChanges(syntax, changes, next => SameDotNodes(syntax, next) &&
            EquivalentDotEdges(syntax, next, remove ? edge : null, target == null ? null : edge, target, from, overrides));
    }

    // A chain/product is both a declaration of its endpoint nodes/subgraphs and
    // a sequence of edge contributions. Keep the former literally in place, and
    // expand only the latter. Explicit attributes stay on each contribution so
    // strict-edge merges retain the original inherited defaults and overrides.
    private void NormalizeDotStatements(DotSyntax syntax, List<DotChange> changes, IEnumerable<DotSyntax.Edge> selected,
        DotSyntax.Edge? removed = null, DotSyntax.Edge? retargeted = null, DotSyntax.Node? target = null, bool from = false,
        Dictionary<DotSyntax.Edge, Dictionary<string, string>>? overrides = null,
        Dictionary<(string Node, string Port), string>? portChanges = null)
    {
        var statements = selected.SelectMany(edge => edge.Uses).Select(use => use.Statement).Distinct().OrderBy(statement => statement.Start);
        foreach (var statement in statements)
        {
            foreach (var arrow in statement.Arrows) changes.Add(new(arrow.Start, arrow.End, ";"));
            foreach (var comma in statement.Lists.SelectMany(list => list.Commas)) changes.Add(new(comma.Start, comma.End, ";"));
            if (statement.AttributesStart < statement.End)
                RemoveDotTokens(syntax, changes, statement.AttributesStart, statement.End, "");
            var text = new StringBuilder(";");
            var origins = new List<DotEdgeOrigin>();
            if (target != null && target.First.Start >= statement.Start && statement.Uses.Any(use => use.Edge == retargeted))
                text.Append(DotNewLine).Append("  ").Append(DotName(target.Name)).Append(DotAttributes(syntax, target.Attributes)).Append(';');
            foreach (var use in statement.Uses)
            {
                if (use.Edge == removed) continue;
                var first = use.From; var second = use.To;
                if (use.Edge == retargeted && target != null)
                {
                    bool reversed = use.From.Node != use.Edge.From.Node;
                    if (from != reversed) first = first with { Node = target };
                    else second = second with { Node = target };
                }
                first = RenamedDotEndpoint(first, portChanges); second = RenamedDotEndpoint(second, portChanges);
                text.Append(DotNewLine).Append("  ").Append(DotEndpoint(first.Node.Name, first)).Append(' ');
                origins.Add(new(text.Length, use.Edge));
                text.Append(syntax.Directed ? "->" : "--").Append(' ').Append(DotEndpoint(second.Node.Name, second));
                var attributes = statement.Attributes.ToDictionary(pair => pair.Key, pair => DotValue(syntax, pair.Value), StringComparer.Ordinal);
                if (portChanges != null)
                    foreach (string name in new[] { "headport", "tailport" })
                        if (statement.Attributes.TryGetValue(name, out var token) && DotAttributePort(token.Value) is var attributePort &&
                            portChanges.TryGetValue(((name == "headport" ? use.Edge.To : use.Edge.From).Node.Name, attributePort.Port), out string? newPort))
                            attributes[name] = QuoteDot(newPort + attributePort.Suffix);
                if (overrides != null && overrides.TryGetValue(use.Edge, out var values) && use == use.Edge.Uses[^1])
                    foreach (var pair in values) attributes[pair.Key] = pair.Value;
                if (attributes.Count > 0)
                    text.Append(" [").Append(string.Join(", ", attributes.Select(pair => DotName(pair.Key) + "=" + pair.Value))).Append(']');
                text.Append(';');
            }
            changes.Add(new(statement.End, statement.End, text.ToString(), origins));
        }
    }

    // Comments between concatenated quoted tokens already survive at their
    // original position. Do not copy those comments into every expanded edge.
    private static string DotValue(DotSyntax syntax, DotSyntax.Token value) => string.Join(" ",
        syntax.Tokens.Where(token => token.Start >= value.Start && token.End <= value.End).Select(syntax.Text));

    private static bool EquivalentDotEdges(DotSyntax before, DotSyntax after, DotSyntax.Edge? removed = null,
        DotSyntax.Edge? retargeted = null, DotSyntax.Node? target = null, bool from = false,
        Dictionary<DotSyntax.Edge, Dictionary<string, string>>? overrides = null,
        Dictionary<(string Node, string Port), string>? portChanges = null)
    {
        if (after.Edges.Count != before.Edges.Count - (removed == null ? 0 : 1)) return false;
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in before.Edges)
        {
            if (edge == removed) continue;
            var attributes = edge.Attributes;
            if (overrides != null && overrides.TryGetValue(edge, out var values))
            {
                attributes = new(attributes, StringComparer.Ordinal);
                foreach (var pair in values)
                {
                    var parsed = DotSyntax.Parse("@startdot\ndigraph { n [" + DotName(pair.Key) + "=" + pair.Value + "]; }\n@enddot");
                    if (parsed == null) return false;
                    attributes[pair.Key] = parsed.Nodes["n"].Attributes[pair.Key];
                }
            }
            string key = DotEdgeKey(edge, attributes, edge == retargeted && from ? target : null,
                edge == retargeted && !from ? target : null, portChanges);
            expected[key] = expected.GetValueOrDefault(key) + 1;
        }
        foreach (var edge in after.Edges)
        {
            string key = DotEdgeKey(edge, edge.Attributes);
            if (!expected.TryGetValue(key, out int count) || count == 0) return false;
            expected[key] = count - 1;
        }
        return expected.Values.All(count => count == 0);
    }

    private static DotSyntax.Endpoint RenamedDotEndpoint(DotSyntax.Endpoint endpoint, Dictionary<(string Node, string Port), string>? changes)
    {
        string port = endpoint.PortToken?.Value ?? endpoint.Port;
        return changes != null && changes.TryGetValue((endpoint.Node.Name, port), out string? value) ?
            endpoint with { Port = DotName(value), PortToken = new(value, 0, 0, DotSyntax.TokenKind.Quoted) } : endpoint;
    }

    private static string DotEdgeKey(DotSyntax.Edge edge, Dictionary<string, DotSyntax.Token> attributes,
        DotSyntax.Node? first = null, DotSyntax.Node? second = null, Dictionary<(string Node, string Port), string>? portChanges = null)
    {
        var source = RenamedDotEndpoint(edge.From, portChanges);
        var destination = RenamedDotEndpoint(edge.To, portChanges);
        var key = new StringBuilder();
        void Part(string text) => key.Append(text.Length).Append(':').Append(text);
        Part((first ?? edge.From.Node).Name); Part((second ?? edge.To.Node).Name);
        Part(source.PortToken?.Value ?? source.Port); Part(source.CompassToken?.Value ?? source.Compass);
        Part(destination.PortToken?.Value ?? destination.Port); Part(destination.CompassToken?.Value ?? destination.Compass);
        Part(edge.First.Scope.Name);
        foreach (var pair in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        { Part(pair.Key); Part(pair.Value.Value); Part(pair.Value.Kind == DotSyntax.TokenKind.Html ? "html" : "text"); }
        return key.ToString();
    }
}
