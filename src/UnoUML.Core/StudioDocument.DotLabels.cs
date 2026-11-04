using System.Globalization;
using System.Net;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    public DotLabelEdit? OpenDotLabel(DotObjectEdit item)
    {
        if (!CurrentDot(item) || DotStructure is not { } syntax) return null;
        Dictionary<string, DotSyntax.Token> attributes;
        if (item.Location.Kind == "node" && DotNodeMatches(syntax, item.Location)) attributes = syntax.Nodes[item.Location.Name].Attributes;
        else if (ResolvedDotEdge(syntax, item.Location) is { } edge) attributes = edge.Attributes;
        else return null;
        var token = attributes.GetValueOrDefault("label", new(item.Location.Name, -1, -1, DotSyntax.TokenKind.Quoted));
        string kind = token.Kind == DotSyntax.TokenKind.Html ? "html" : item.Location.Kind == "node" &&
            attributes.TryGetValue("shape", out var shape) && (shape.Value.Equals("record", StringComparison.OrdinalIgnoreCase) ||
                shape.Value.Equals("Mrecord", StringComparison.OrdinalIgnoreCase)) ? "record" : "";
        return DotLabelSyntax.Parse(syntax, token, kind) is { } parsed ? new(item, parsed) : null;
    }

    public bool CreateDotRecordLabel(DotObjectEdit item, string text = "Field") => ValidDotLabelText(text) &&
        CreateDotStructuredLabel(item, "record", DotLabelSyntax.RecordText(text));

    public bool CreateDotTableLabel(DotObjectEdit item, int rows = 2, int columns = 2)
    {
        if (rows is < 1 or > 32 || columns is < 1 or > 32 || rows * columns > 1024) return false;
        string row = "<TR>" + string.Concat(Enumerable.Repeat("<TD>Cell</TD>", columns)) + "</TR>";
        return CreateDotStructuredLabel(item, "html", "<TABLE BORDER=\"0\" CELLBORDER=\"1\" CELLSPACING=\"0\">" +
            string.Concat(Enumerable.Repeat(row, rows)) + "</TABLE>");
    }

    private bool CreateDotStructuredLabel(DotObjectEdit item, string kind, string value)
    {
        if (!CurrentDot(item) || DotStructure is not { } syntax || DotLabelSyntax.ParseValue(value, kind) == null) return false;
        if (item.Location.Kind == "edge")
            return kind == "html" && ResolvedDotEdge(syntax, item.Location) is { } edge && ChangeSharedDotEdge(syntax, edge, label: "<" + value + ">");
        if (!DotNodeMatches(syntax, item.Location)) return false;
        var old = OpenDotLabel(item);
        if (old != null && old.Syntax.Ports.Any(port => DotPortReferenced(syntax, item.Location.Name, port))) return false;
        return CommitDotNodeAttributes(syntax, syntax.Nodes[item.Location.Name], new(StringComparer.Ordinal)
        { ["shape"] = kind == "record" ? "record" : "plain", ["label"] = kind == "html" ? "<" + value + ">" : DotLabelSyntax.QuoteRecord(value) });
    }

    public bool InsertDotRecordField(DotLabelEdit label, string parentPath, int index, string text = "Field", string port = "")
    {
        if (!CurrentDotLabel(label, "record") || !ValidDotLabelText(text) || !DotLabelSyntax.ValidPort(port) ||
            label.Syntax.Siblings(parentPath) is not { } fields || index < 0 || index > fields.Count) return false;
        string field = (port.Length == 0 ? "" : "<" + port + "> ") + DotLabelSyntax.RecordText(text);
        int at = index < fields.Count ? fields[index].Start : fields[^1].End;
        return EditDotLabel(label, label.Syntax.Value.Insert(at, index < fields.Count ? field + "|" : "|" + field));
    }

    public bool RemoveDotRecordField(DotLabelEdit label, string path)
    {
        if (!CurrentDotLabel(label, "record") || label.Syntax.Record.FirstOrDefault(field => field.Path == path) is not { } field ||
            label.Syntax.Siblings(field.Parent) is not { Count: > 1 } fields) return false;
        int start = field.Index == fields.Count - 1 ? fields[field.Index - 1].End : field.Start;
        int end = field.Index == fields.Count - 1 ? field.End : fields[field.Index + 1].Start;
        return EditDotLabel(label, label.Syntax.Value.Remove(start, end - start));
    }

    public bool MoveDotRecordField(DotLabelEdit label, string path, int index)
    {
        if (!CurrentDotLabel(label, "record") || label.Syntax.Record.FirstOrDefault(field => field.Path == path) is not { } field ||
            label.Syntax.Siblings(field.Parent) is not { } fields || index < 0 || index >= fields.Count || index == field.Index) return false;
        int start = fields[0].Start, end = fields[^1].End;
        var order = fields.ToList(); order.Remove(field); order.Insert(index, field);
        string replacement = string.Join("|", order.Select(part => label.Syntax.Value[part.Start..part.End]));
        return EditDotLabel(label, label.Syntax.Value[..start] + replacement + label.Syntax.Value[end..]);
    }

    public bool GroupDotRecordFields(DotLabelEdit label, string parentPath, int index, int count)
    {
        if (!CurrentDotLabel(label, "record") || label.Syntax.Siblings(parentPath) is not { } fields ||
            index < 0 || count < 1 || index > fields.Count - count) return false;
        int start = fields[index].Start, end = fields[index + count - 1].End;
        return EditDotLabel(label, label.Syntax.Value[..start] + "{" + label.Syntax.Value[start..end] + "}" + label.Syntax.Value[end..]);
    }

    public bool UngroupDotRecordField(DotLabelEdit label, string path)
    {
        if (!CurrentDotLabel(label, "record") || label.Syntax.Record.FirstOrDefault(field => field.Path == path && field.IsGroup) is not { } field) return false;
        return EditDotLabel(label, label.Syntax.Value[..field.Start] + label.Syntax.Value[field.ContentStart..field.ContentEnd] + label.Syntax.Value[field.End..]);
    }

    public bool SetDotRecordFieldText(DotLabelEdit label, string path, string text)
    {
        if (!CurrentDotLabel(label, "record") || !ValidDotLabelText(text) ||
            label.Syntax.Record.FirstOrDefault(field => field.Path == path && !field.IsGroup) is not { } field) return false;
        return EditDotLabel(label, label.Syntax.Value[..field.TextStart] + (field.Port.Length > 0 ? " " : "") + DotLabelSyntax.RecordText(text) + label.Syntax.Value[field.End..]);
    }

    public bool SetDotRecordFieldPort(DotLabelEdit label, string path, string port)
    {
        if (!CurrentDotLabel(label, "record") || !DotLabelSyntax.ValidPort(port) ||
            label.Syntax.Record.FirstOrDefault(field => field.Path == path && !field.IsGroup) is not { } field || field.Port == port) return false;
        int start = field.PortStart >= 0 ? field.PortStart : field.Start, end = field.PortEnd >= 0 ? field.PortEnd : field.Start;
        string value = label.Syntax.Value[..start] + (port.Length == 0 ? "" : "<" + port + ">") + label.Syntax.Value[end..];
        return EditDotLabel(label, value, field.Port, port);
    }

    private bool CurrentDotLabel(DotLabelEdit label, string kind) => CurrentDot(label.Item) && label.Kind == kind;
    private static bool ValidDotLabelText(string text) => text.Length <= 65536 && !text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r'));

    private bool EditDotLabel(DotLabelEdit label, string value, string oldPort = "", string newPort = "")
    {
        if (!CurrentDot(label.Item) || value == label.Syntax.Value || DotStructure is not { } syntax ||
            label.Syntax.WithValue(value) is not { } nextLabel) return false;
        string literal = label.Kind == "html" ? "<" + value + ">" : DotLabelSyntax.QuoteRecord(value);
        if (label.Item.Location.Kind == "edge")
            return label.Kind == "html" && ResolvedDotEdge(syntax, label.Item.Location) is { } edge && ChangeSharedDotEdge(syntax, edge, label: literal);
        string nodeName = label.Item.Location.Name;
        bool rename = oldPort.Length > 0 && newPort.Length > 0 && oldPort != newPort;
        foreach (string port in label.Syntax.Ports.Except(nextLabel.Ports))
            if ((!rename || port != oldPort) && DotPortReferenced(syntax, nodeName, port)) return false;
        var changes = new List<DotChange>();
        var node = syntax.Nodes[nodeName];
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["label"] = literal };
        AddDotNodeAttributes(syntax, node, changes, values);
        Dictionary<DotSyntax.Edge, Dictionary<string, string>>? overrides = null;
        Dictionary<(string Node, string Port), string>? portChanges = null;
        if (rename)
        {
            portChanges = new() { [(nodeName, oldPort)] = newPort };
            foreach (var endpoint in syntax.Lists.SelectMany(list => list.Nodes).Where(endpoint => endpoint.Node == node && endpoint.PortToken?.Value == oldPort)
                .DistinctBy(endpoint => endpoint.PortToken!.Value.Start))
            {
                var token = endpoint.PortToken!.Value;
                RemoveDotTokens(syntax, changes, token.Start, token.End, DotName(newPort));
            }
            overrides = DotPortAttributeOverrides(syntax, nodeName, oldPort, newPort);
            var references = syntax.Edges.Where(edge => overrides.ContainsKey(edge) || edge.Uses.Any(use =>
                use.Edge.From.Node.Name == nodeName && PortAttribute(use.Statement.Attributes, "tailport", oldPort) ||
                use.Edge.To.Node.Name == nodeName && PortAttribute(use.Statement.Attributes, "headport", oldPort))).ToArray();
            if (references.Length > 0) NormalizeDotStatements(syntax, changes, references, overrides: overrides, portChanges: portChanges);
        }
        return CommitDotChanges(syntax, changes, next => SameDotNodesWithAttributes(syntax, next, node, values) &&
            EquivalentDotEdges(syntax, next, overrides: overrides, portChanges: portChanges));
    }

    private bool CommitDotNodeAttributes(DotSyntax syntax, DotSyntax.Node node, Dictionary<string, string> values)
    {
        var changes = new List<DotChange>(); AddDotNodeAttributes(syntax, node, changes, values);
        return CommitDotChanges(syntax, changes, next => SameDotNodesWithAttributes(syntax, next, node, values) && EquivalentDotEdges(syntax, next));
    }

    private void AddDotNodeAttributes(DotSyntax syntax, DotSyntax.Node node, List<DotChange> changes, Dictionary<string, string> values)
    {
        // A privately owned label can be changed in place. Shared/default labels
        // get a node-local override, leaving every declaration and default intact.
        if (values.Count == 1 && values.TryGetValue("label", out var label) && node.Attributes.TryGetValue("label", out var token) &&
            syntax.Lists.Any(list => !list.Owner.Edge && list.Nodes.Count == 1 && list.Nodes[0].Node == node &&
                list.Owner.Attributes.TryGetValue("label", out var owned) && owned.Start == token.Start))
        { RemoveDotTokens(syntax, changes, token.Start, token.End, label); return; }
        int at = syntax.Root.Occurrences[0].Close;
        changes.Add(new(at, at, ";" + DotNewLine + "  " + DotName(node.Name) + " [" +
            string.Join(", ", values.Select(pair => DotName(pair.Key) + "=" + pair.Value)) + "];" + DotNewLine));
    }

    private static bool SameDotNodesWithAttributes(DotSyntax before, DotSyntax after, DotSyntax.Node edited, Dictionary<string, string> values)
    {
        if (before.Nodes.Count != after.Nodes.Count || before.Scopes.Count != after.Scopes.Count ||
            !before.Scopes.Zip(after.Scopes).All(pair => pair.First.Name == pair.Second.Name && pair.First.Members.SetEquals(pair.Second.Members))) return false;
        var expected = new Dictionary<string, DotSyntax.Token>(edited.Attributes, StringComparer.Ordinal);
        var parsed = DotSyntax.Parse("@startdot\ndigraph { n [" + string.Join(",", values.Select(pair => DotName(pair.Key) + "=" + pair.Value)) + "]; }\n@enddot");
        if (parsed == null) return false;
        foreach (var pair in parsed.Nodes["n"].Attributes) expected[pair.Key] = pair.Value;
        return before.Nodes.Values.All(node => after.Nodes.TryGetValue(node.Name, out var current) &&
            SameDotAttributes(node == edited ? expected : node.Attributes, current.Attributes));
    }

    private static bool DotPortReferenced(DotSyntax syntax, string node, string port) =>
        syntax.Lists.SelectMany(list => list.Nodes).Any(endpoint => endpoint.Node.Name == node && endpoint.PortToken?.Value == port) ||
        syntax.Edges.Any(edge =>
            edge.From.Node.Name == node && PortAttribute(edge.Attributes, "tailport", port) ||
            edge.To.Node.Name == node && PortAttribute(edge.Attributes, "headport", port) ||
            edge.Uses.Any(use => use.Edge.From.Node.Name == node && PortAttribute(use.Statement.Attributes, "tailport", port) ||
                use.Edge.To.Node.Name == node && PortAttribute(use.Statement.Attributes, "headport", port)));

    private static bool PortAttribute(IReadOnlyDictionary<string, DotSyntax.Token> attributes, string name, string port) =>
        attributes.TryGetValue(name, out var value) && DotAttributePort(value.Value).Port == port;

    private static (string Port, string Suffix) DotAttributePort(string value)
    {
        int colon = value.LastIndexOf(':');
        return colon >= 0 && value[(colon + 1)..] is "n" or "ne" or "e" or "se" or "s" or "sw" or "w" or "nw" or "c" or "_" ?
            (value[..colon], value[colon..]) : (value, "");
    }

    private static Dictionary<DotSyntax.Edge, Dictionary<string, string>> DotPortAttributeOverrides(DotSyntax syntax, string node, string oldPort, string newPort)
    {
        var result = new Dictionary<DotSyntax.Edge, Dictionary<string, string>>();
        foreach (var edge in syntax.Edges)
            foreach (string name in new[] { "headport", "tailport" })
                if ((name == "headport" ? edge.To.Node.Name : edge.From.Node.Name) == node && edge.Attributes.TryGetValue(name, out var token) &&
                    DotAttributePort(token.Value) is var value && value.Port == oldPort)
                {
                    if (!result.TryGetValue(edge, out var values)) result.Add(edge, values = new(StringComparer.Ordinal));
                    values[name] = QuoteDot(newPort + value.Suffix);
                }
        return result;
    }
}
