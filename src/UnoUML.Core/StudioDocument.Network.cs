using UnoUML.Syntax;

namespace UnoUML.Core;

public sealed record NetworkProperties(string? Description = null, string? Address = null, string? Color = null);
public sealed record NetworkHostProperties(string? Description = null, string? Color = null, string? Shape = null);

/// <summary>An immutable, source-aware network page. Reopen handles after any edit.</summary>
public sealed class NetworkDiagramEdit
{
    internal NetworkDiagramEdit(string source, int page, int offset, int firstLine, NetworkSource syntax)
    {
        Source = source; Page = page; Offset = offset; FirstLine = firstLine; Syntax = syntax;
        Networks = Array.AsReadOnly(syntax.Networks.Select((_, i) => new NetworkEdit(this, i)).ToArray());
        Hosts = Array.AsReadOnly(syntax.Hosts.Select((_, i) => new NetworkHostEdit(this, i)).ToArray());
        Interfaces = Array.AsReadOnly(syntax.Networks.SelectMany(n => n.Interfaces).Select(i => new NetworkInterfaceEdit(this, i)).ToArray());
    }
    internal string Source { get; }
    internal int Page { get; }
    internal int Offset { get; }
    internal int FirstLine { get; }
    internal NetworkSource Syntax { get; }
    public IReadOnlyList<NetworkEdit> Networks { get; }
    public IReadOnlyList<NetworkHostEdit> Hosts { get; }
    public IReadOnlyList<NetworkInterfaceEdit> Interfaces { get; }
}

public sealed class NetworkEdit
{
    internal NetworkEdit(NetworkDiagramEdit diagram, int index) { Diagram = diagram; Index = index; }
    public NetworkDiagramEdit Diagram { get; }
    public int Index { get; }
    internal NetworkSource.Bus Syntax => Diagram.Syntax.Networks[Index];
    public string Alias => Syntax.Alias;
    public string CanvasId => Syntax.CanvasId;
    public int Offset => Diagram.Offset + Syntax.Start;
    public int EndOffset => Diagram.Offset + Syntax.End;
    public int Line => Diagram.FirstLine + Syntax.Line;
    public NetworkProperties Properties => new(Syntax.Property("description"), Syntax.Property("address"), Syntax.Property("color"));
    public IReadOnlyList<NetworkInterfaceEdit> Interfaces => Diagram.Interfaces.Where(i => ReferenceEquals(i.Syntax.Bus, Syntax)).ToArray();
}

public sealed class NetworkHostEdit
{
    internal NetworkHostEdit(NetworkDiagramEdit diagram, int index) { Diagram = diagram; Index = index; }
    public NetworkDiagramEdit Diagram { get; }
    public int Index { get; }
    internal NetworkSource.Host Syntax => Diagram.Syntax.Hosts[Index];
    public string Alias => Syntax.Alias;
    public string CanvasId => Syntax.CanvasId;
    public NetworkHostProperties Properties => new(Syntax.Property("description"), Syntax.Property("color"), Syntax.Property("shape"));
    public IReadOnlyList<NetworkInterfaceEdit> Interfaces => Diagram.Interfaces.Where(i => ReferenceEquals(i.Syntax.Host, Syntax)).ToArray();
}

public sealed class NetworkInterfaceEdit
{
    internal NetworkInterfaceEdit(NetworkDiagramEdit diagram, NetworkSource.Interface syntax) { Diagram = diagram; Syntax = syntax; }
    public NetworkDiagramEdit Diagram { get; }
    internal NetworkSource.Interface Syntax { get; }
    public NetworkEdit Network => Diagram.Networks.First(n => ReferenceEquals(n.Syntax, Syntax.Bus));
    public NetworkHostEdit Host => Diagram.Hosts.First(h => ReferenceEquals(h.Syntax, Syntax.Host));
    public string? Address => Syntax.Address;
    public string CanvasId => Syntax.CanvasId;
    public int Offset => Diagram.Offset + Syntax.Start;
    public int EndOffset => Diagram.Offset + Syntax.End;
    public int Line => Diagram.FirstLine + Syntax.Line;
}

public sealed partial class StudioDocument
{
    public NetworkDiagramEdit? OpenNetworkDiagram() => Page.Family == "Network" && NetworkSource.Parse(Page.Source) is { } syntax
        ? new(Source, ActivePage, Index.Lines[Page.StartLine].Start, Page.StartLine, syntax) : null;
    private bool Current(NetworkDiagramEdit diagram) => diagram.Page == ActivePage && ReferenceEquals(diagram.Source, Source);

    private bool ApplyNetworkSourceField(SourceFieldEdit field, string text)
    {
        if (!CurrentStructuredField(field) || OpenNetworkDiagram() is not { } diagram) return false;
        bool Is(SourceToken token) => token.Length > 0 && diagram.Offset + token.Start == field.Offset && token.Length == field.Length;
        foreach (var network in diagram.Networks)
        {
            if (Is(network.Syntax.AliasToken)) return RenameNetwork(network, text);
            foreach (var property in network.Syntax.Properties)
                if (Is(property.ValueToken))
                    return property.Name switch
                    {
                        "address" => SetNetworkProperties(network, network.Properties with { Address = text }),
                        "description" => SetNetworkProperties(network, network.Properties with { Description = text }),
                        _ => false
                    };
        }
        foreach (var connection in diagram.Interfaces)
        {
            if (Is(connection.Syntax.AliasToken)) return RenameNetworkHost(connection.Host, text);
            foreach (var property in connection.Syntax.Attributes)
                if (Is(property.ValueToken))
                    return property.Name switch
                    {
                        "address" => SetNetworkInterfaceAddress(connection, text),
                        "description" => SetNetworkHostProperties(connection.Host, connection.Host.Properties with { Description = text }),
                        _ => false
                    };
        }
        return false;
    }

    /// <summary>Insert a named bus at the requested final index, retaining the existing wrapper convention.</summary>
    public bool InsertNetwork(NetworkDiagramEdit diagram, int index, string alias, NetworkProperties? properties = null)
    {
        properties ??= new();
        if (!Current(diagram) || index < 0 || index > diagram.Networks.Count || !NetworkSource.IsAlias(alias) ||
            diagram.Networks.Any(n => n.Alias == alias) || !ValidNetworkProperties(properties)) return false;
        string nl = StructuredNewLine(diagram.Syntax.BodyStart), indent = NetworkOuterIndent(diagram.Syntax);
        string values = string.Concat(NetworkValues(properties).Where(p => p.Value != null).Select(p => "  " + p.Key + " = " + StructuredText.Quote(p.Value!) + ";" + nl));
        string statement = "network " + alias + " {" + nl + values + "}";
        int at = index < diagram.Networks.Count ? diagram.Networks[index].Syntax.Start : diagram.Syntax.BodyEnd;
        return CommitNetwork(diagram, [NetworkInsertion(diagram.Syntax, at, statement, indent)]);
    }

    public bool SetNetworkProperties(NetworkEdit network, NetworkProperties properties)
    {
        if (!Current(network.Diagram) || !ValidNetworkProperties(properties)) return false;
        var changes = new List<Change>(); var additions = new List<string>();
        foreach (var (name, value) in NetworkValues(properties))
        {
            var existing = network.Syntax.Properties.Where(a => a.Name == name).ToArray();
            if (value == null) foreach (var item in existing) EraseNetworkAttribute(changes, item, true);
            else if (existing.Length != 0)
            {
                var last = existing[^1];
                if (last.Value != value) changes.Add(new(last.ValueToken.Start, last.ValueToken.End, StructuredText.Quote(value)));
            }
            else additions.Add(name + " = " + StructuredText.Quote(value) + ";");
        }
        if (additions.Count > 0)
        {
            int at = network.Syntax.Interfaces.FirstOrDefault()?.Start ?? network.Syntax.BodyEnd;
            changes.Add(NetworkInsertion(network.Diagram.Syntax, at, string.Join(StructuredNewLine(at), additions), NetworkInnerIndent(network.Syntax)));
        }
        return CommitNetwork(network.Diagram, changes);
    }

    /// <summary>Change a bus alias and its canvas identity, ports and routes in one source transaction.</summary>
    public bool RenameNetwork(NetworkEdit network, string alias)
    {
        if (!Current(network.Diagram) || !NetworkSource.IsAlias(alias) || network.Diagram.Networks.Any(n => n.Alias == alias)) return false;
        var token = network.Syntax.AliasToken;
        return CommitNetwork(network.Diagram, [new(token.Start, token.End, alias)], networkRename: (network.Alias, alias));
    }

    public bool RemoveNetwork(NetworkEdit network) => Current(network.Diagram) &&
        RemoveNetworkSelection(network.Diagram, [network.CanvasId], []);

    public bool MoveNetwork(NetworkEdit network, int index)
    {
        if (!Current(network.Diagram) || index < 0 || index >= network.Diagram.Networks.Count || index == network.Index) return false;
        var slots = network.Diagram.Syntax.Networks.Select(n => new SourceToken(n.Start, n.End - n.Start)).ToArray();
        return CommitNetwork(network.Diagram, MoveNetworkStatements(slots, network.Index, index));
    }

    /// <summary>Insert a new shared host with its first interface.</summary>
    public bool InsertNetworkHost(NetworkEdit network, string alias, string? address = null, NetworkHostProperties? properties = null)
    {
        properties ??= new();
        if (!Current(network.Diagram) || !NetworkSource.IsAlias(alias) || network.Diagram.Hosts.Any(h => h.Alias == alias) ||
            !NetworkSource.IsValue(address) || !ValidNetworkHostProperties(properties)) return false;
        var values = NetworkHostValues(properties); values.Insert(0, new("address", address));
        string statement = alias + NetworkAttributeList(values) + ";";
        return CommitNetwork(network.Diagram, [NetworkInsertion(network.Diagram.Syntax, network.Syntax.BodyEnd, statement, NetworkInnerIndent(network.Syntax))]);
    }

    /// <summary>Attach an existing host to another bus; its global appearance is unchanged.</summary>
    public bool AttachNetworkHost(NetworkHostEdit host, NetworkEdit network, string? address = null)
    {
        if (!ReferenceEquals(host.Diagram, network.Diagram) || !Current(host.Diagram) || !NetworkSource.IsValue(address) ||
            network.Interfaces.Any(i => i.Host.Alias == host.Alias)) return false;
        string statement = host.Alias + NetworkAttributeList([new("address", address)]) + ";";
        return CommitNetwork(host.Diagram, [NetworkInsertion(host.Diagram.Syntax, network.Syntax.BodyEnd, statement, NetworkInnerIndent(network.Syntax))]);
    }

    public bool SetNetworkHostProperties(NetworkHostEdit host, NetworkHostProperties properties)
    {
        if (!Current(host.Diagram) || !ValidNetworkHostProperties(properties)) return false;
        return CommitNetwork(host.Diagram, NetworkHostPropertyChanges(host.Syntax, properties), changedHost: host.Alias);
    }

    /// <summary>Rename every occurrence of a shared host and preserve its authored geometry.</summary>
    public bool RenameNetworkHost(NetworkHostEdit host, string alias)
    {
        if (!Current(host.Diagram) || !NetworkSource.IsAlias(alias) || host.Diagram.Hosts.Any(h => h.Alias == alias)) return false;
        return CommitNetwork(host.Diagram, host.Syntax.Interfaces.Select(i => new Change(i.AliasToken.Start, i.AliasToken.End, alias)).ToList(),
            hostRename: (host.Alias, alias));
    }

    public bool RemoveNetworkHost(NetworkHostEdit host) => Current(host.Diagram) && RemoveNetworkSelection(host.Diagram, [host.CanvasId], []);

    public bool SetNetworkInterfaceAddress(NetworkInterfaceEdit connection, string? address)
    {
        if (!Current(connection.Diagram) || !NetworkSource.IsValue(address)) return false;
        return CommitNetwork(connection.Diagram, NetworkInterfacePropertyChanges(connection.Syntax, [new("address", address)]));
    }

    public bool DetachNetworkHost(NetworkInterfaceEdit connection) => Current(connection.Diagram) &&
        RemoveNetworkSelection(connection.Diagram, [], [connection.CanvasId]);

    /// <summary>Move one attachment to another bus. Other attachments and global host properties survive.</summary>
    public bool MoveNetworkInterface(NetworkInterfaceEdit connection, NetworkEdit destination)
    {
        if (!ReferenceEquals(connection.Diagram, destination.Diagram) || !Current(connection.Diagram) ||
            ReferenceEquals(connection.Syntax.Bus, destination.Syntax) || destination.Interfaces.Any(i => i.Host.Alias == connection.Host.Alias)) return false;
        string statement = Page.Source[connection.Syntax.Start..connection.Syntax.End];
        return CommitNetwork(connection.Diagram,
        [RemoveNetworkStatement(connection.Syntax.Start, connection.Syntax.End),
         NetworkInsertion(connection.Diagram.Syntax, destination.Syntax.BodyEnd, statement, NetworkInnerIndent(destination.Syntax))]);
    }

    public bool ReorderNetworkInterface(NetworkInterfaceEdit connection, int index)
    {
        var slots = connection.Syntax.Bus.Interfaces;
        int previous = slots.IndexOf(connection.Syntax);
        if (!Current(connection.Diagram) || index < 0 || index >= slots.Count || index == previous) return false;
        return CommitNetwork(connection.Diagram, MoveNetworkStatements(slots.Select(i => new SourceToken(i.Start, i.End - i.Start)).ToArray(), previous, index));
    }

    /// <summary>Delete selected buses, hosts and/or interfaces atomically. Shared hosts on surviving buses remain.</summary>
    public bool RemoveNetworkSelection(NetworkDiagramEdit diagram, IReadOnlyCollection<string> nodeIds, IReadOnlyCollection<string> edgeIds)
    {
        if (!Current(diagram)) return false;
        var networks = diagram.Syntax.Networks.Where(n => nodeIds.Contains(n.CanvasId)).ToHashSet();
        var hosts = diagram.Syntax.Hosts.Where(h => nodeIds.Contains(h.CanvasId)).ToHashSet();
        var changes = networks.Select(n => RemoveNetworkStatement(n.Start, n.End)).ToList();
        foreach (var connection in diagram.Syntax.Networks.Where(n => !networks.Contains(n)).SelectMany(n => n.Interfaces))
            if (hosts.Contains(connection.Host) || edgeIds.Contains(connection.CanvasId)) changes.Add(RemoveNetworkStatement(connection.Start, connection.End));
        return CommitNetwork(diagram, changes);
    }

    private static bool ValidNetworkProperties(NetworkProperties value) => NetworkSource.IsValue(value.Description) &&
        NetworkSource.IsValue(value.Address) && NetworkSource.IsColor(value.Color);
    private static bool ValidNetworkHostProperties(NetworkHostProperties value) => NetworkSource.IsValue(value.Description) &&
        NetworkSource.IsColor(value.Color) && NetworkSource.IsShape(value.Shape);
    private static List<KeyValuePair<string, string?>> NetworkValues(NetworkProperties value) =>
        [new("description", value.Description), new("address", value.Address), new("color", value.Color)];
    private static List<KeyValuePair<string, string?>> NetworkHostValues(NetworkHostProperties value) =>
        [new("description", value.Description), new("color", value.Color), new("shape", value.Shape)];
    private static string NetworkAttributeList(IEnumerable<KeyValuePair<string, string?>> properties)
    {
        string values = string.Join(", ", properties.Where(p => p.Value != null).Select(p => p.Key + " = " + StructuredText.Quote(p.Value!)));
        return values.Length == 0 ? "" : " [" + values + "]";
    }

    private List<Change> NetworkHostPropertyChanges(NetworkSource.Host host, NetworkHostProperties properties)
    {
        var perInterface = new Dictionary<NetworkSource.Interface, List<KeyValuePair<string, string?>>>();
        foreach (var pair in NetworkHostValues(properties))
        {
            var occurrences = host.Interfaces.Where(i => i.Attributes.Any(a => a.Name == pair.Key)).ToArray();
            if (pair.Value == null)
            {
                foreach (var occurrence in occurrences) Add(occurrence, pair);
            }
            else Add(occurrences.LastOrDefault() ?? host.Interfaces[^1], pair);
        }
        return perInterface.SelectMany(p => NetworkInterfacePropertyChanges(p.Key, p.Value)).ToList();
        void Add(NetworkSource.Interface connection, KeyValuePair<string, string?> pair)
        {
            if (!perInterface.TryGetValue(connection, out var values)) perInterface[connection] = values = [];
            values.Add(pair);
        }
    }
    private static List<Change> NetworkInterfacePropertyChanges(NetworkSource.Interface connection, IReadOnlyList<KeyValuePair<string, string?>> values)
    {
        var changes = new List<Change>();
        var requested = values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var kept = connection.Attributes.Where(a => !requested.TryGetValue(a.Name, out var value) || value != null).ToArray();
        foreach (var attribute in connection.Attributes)
        {
            if (requested.TryGetValue(attribute.Name, out var value))
            {
                if (value == null) EraseNetworkAttribute(changes, attribute, false);
                else if (value != attribute.Value) changes.Add(new(attribute.ValueToken.Start, attribute.ValueToken.End, StructuredText.Quote(value)));
            }
            bool separatorNeeded = kept.Contains(attribute) && attribute != kept.LastOrDefault();
            if (!separatorNeeded && attribute.Separator.Length > 0) changes.Add(new(attribute.Separator.Start, attribute.Separator.End, ""));
        }
        // A surviving attribute can precede removed attributes. Its original comma
        // remains the separator to the next survivor; comments in between are untouched.
        var additions = values.Where(p => p.Value != null && !connection.Attributes.Any(a => a.Name == p.Key)).ToArray();
        if (additions.Length > 0)
        {
            string text = string.Join(", ", additions.Select(p => p.Key + " = " + StructuredText.Quote(p.Value!)));
            if (connection.OpenBracket.Length > 0)
                changes.Add(new(connection.CloseBracket.Start, connection.CloseBracket.Start, (kept.Length > 0 ? ", " : "") + text));
            else changes.Add(new(connection.AliasToken.End, connection.AliasToken.End, " [" + text + "]"));
        }
        return changes;
    }
    private static void EraseNetworkAttribute(List<Change> changes, NetworkSource.Attribute attribute, bool separator)
    {
        foreach (var token in new[] { attribute.NameToken, attribute.EqualToken, attribute.ValueToken }) changes.Add(new(token.Start, token.End, ""));
        if (separator && attribute.Separator.Length > 0) changes.Add(new(attribute.Separator.Start, attribute.Separator.End, ""));
    }

    private List<Change> MoveNetworkStatements(SourceToken[] tokens, int index, int target)
    {
        var text = tokens.Select(t => Page.Source[t.Start..t.End]).ToList();
        string moved = text[index]; text.RemoveAt(index); text.Insert(target, moved);
        return tokens.Select((t, i) => new Change(t.Start, t.End, text[i])).ToList();
    }
    private Change RemoveNetworkStatement(int start, int end)
    {
        int first = start, last = end;
        while (first > 0 && Page.Source[first - 1] is not ('\r' or '\n')) first--;
        while (last < Page.Source.Length && Page.Source[last] is not ('\r' or '\n')) last++;
        if (string.IsNullOrWhiteSpace(Page.Source[first..start]) && string.IsNullOrWhiteSpace(Page.Source[end..last]))
        {
            if (last < Page.Source.Length && Page.Source[last] == '\r') last++;
            if (last < Page.Source.Length && Page.Source[last] == '\n') last++;
            return new(first, last, "");
        }
        return new(start, end, "");
    }
    private string NetworkOuterIndent(NetworkSource diagram) => diagram.Networks.Count > 0
        ? NetworkIndentAt(diagram.Networks[0].Start, diagram.Wrapped ? "  " : "") : diagram.Wrapped ? "  " : "";
    private string NetworkInnerIndent(NetworkSource.Bus bus)
    {
        int? at = bus.Properties.FirstOrDefault()?.NameToken.Start ?? bus.Interfaces.FirstOrDefault()?.Start;
        string fallback = NetworkIndentAt(bus.Start, "") + "  ";
        return at.HasValue ? NetworkIndentAt(at.Value, fallback) : fallback;
    }
    private string NetworkIndentAt(int at, string fallback)
    {
        int start = at;
        while (start > 0 && Page.Source[start - 1] is not ('\r' or '\n')) start--;
        string value = Page.Source[start..at];
        return string.IsNullOrWhiteSpace(value) ? value : fallback;
    }
    private Change NetworkInsertion(NetworkSource diagram, int at, string statement, string indent)
    {
        int start = at;
        while (start > 0 && Page.Source[start - 1] is not ('\r' or '\n')) start--;
        string nl = StructuredNewLine(at);
        string inserted = string.Join(nl, statement.ReplaceLineEndings("\n").Split('\n').Select(line => indent + line));
        if (string.IsNullOrWhiteSpace(diagram.Source[start..at])) return new(start, start, inserted + nl);
        return new(at, at, nl + inserted + nl + indent);
    }

    private bool CommitNetwork(NetworkDiagramEdit diagram, List<Change> changes, (string Old, string New)? networkRename = null,
        (string Old, string New)? hostRename = null, string? changedHost = null)
    {
        if (!Current(diagram) || changes.Count == 0) return false;
        int cursor = 0;
        foreach (var change in changes.OrderBy(c => c.At).ThenBy(c => c.End))
        {
            if (change.At < cursor || change.End < change.At || change.End > Page.Source.Length) return false;
            cursor = change.End;
        }
        string candidate = Rewrite(Page.Source, changes, 0, Page.Source.Length);
        if (candidate == Page.Source || NetworkSource.Parse(candidate) is not { } after) return false;
        // Host description/color/shape are global and last-writer-wins in nwdiag.
        // Removing or reordering an interface must not accidentally reveal an old
        // override. Materialize only properties whose effective value would change.
        var appearance = new List<Change>();
        foreach (var host in diagram.Syntax.Hosts)
        {
            string alias = hostRename is { } rename && host.Alias == rename.Old ? rename.New : host.Alias;
            var next = after.Hosts.FirstOrDefault(h => h.Alias == alias);
            if (next == null || host.Alias == changedHost) continue;
            var values = new NetworkHostProperties(host.Property("description"), host.Property("color"), host.Property("shape"));
            if (values != new NetworkHostProperties(next.Property("description"), next.Property("color"), next.Property("shape")))
                appearance.AddRange(NetworkHostPropertyChanges(next, values));
        }
        if (appearance.Count != 0)
        {
            candidate = Rewrite(candidate, appearance, 0, candidate.Length);
            if (NetworkSource.Parse(candidate) is not { } preserved) return false;
            after = preserved;
        }
        string replacement = candidate;
        return CommitCanvasSourceEdit(document => document.Patch(diagram.Offset, diagram.Offset + diagram.Syntax.Source.Length, replacement),
            layout => RemapNetworkCanvas(layout, diagram.Syntax, after, networkRename, hostRename));
    }

    private static CanvasLayoutState? RemapNetworkCanvas(CanvasLayoutState? layout, NetworkSource before, NetworkSource after,
        (string Old, string New)? networkRename, (string Old, string New)? hostRename)
    {
        if (layout == null) return null;
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bus in before.Networks)
        {
            string alias = networkRename is { } rename && bus.Alias == rename.Old ? rename.New : bus.Alias;
            if (after.Networks.FirstOrDefault(n => n.Alias == alias) is { } match) nodes[bus.CanvasId] = match.CanvasId;
        }
        foreach (var host in before.Hosts)
        {
            string alias = hostRename is { } rename && host.Alias == rename.Old ? rename.New : host.Alias;
            if (after.Hosts.FirstOrDefault(h => h.Alias == alias) is { } match) nodes[host.CanvasId] = match.CanvasId;
        }
        var edges = new Dictionary<string, (string Id, string Source, string Target)>(StringComparer.Ordinal);
        var originalEdges = before.Networks.SelectMany(n => n.Interfaces).ToDictionary(i => i.CanvasId, StringComparer.Ordinal);
        var currentEdges = after.Networks.SelectMany(n => n.Interfaces).ToDictionary(i => (i.Bus.CanvasId, i.Host.CanvasId));
        foreach (var edge in before.Networks.SelectMany(n => n.Interfaces))
            if (nodes.TryGetValue(edge.Bus.CanvasId, out string? source) && nodes.TryGetValue(edge.Host.CanvasId, out string? target) &&
                currentEdges.TryGetValue((source, target), out var match)) edges[edge.CanvasId] = (match.CanvasId, source, target);
        bool ValidEdge(CanvasEdge edge) => originalEdges.TryGetValue(edge.Id, out var original) &&
            edge.Source == original.Bus.CanvasId && edge.Target == original.Host.CanvasId && edges.ContainsKey(edge.Id);
        if (layout.Nodes.Any(n => n.Locked && !nodes.ContainsKey(n.Id)) || layout.Edges.Any(e => e.Locked && !ValidEdge(e)))
            throw new InvalidOperationException("Unlock the affected network objects and interfaces before deleting or detaching them.");
        var movedNodes = layout.Nodes.Where(n => nodes.ContainsKey(n.Id)).Select(n => n with { Id = nodes[n.Id], Parent = null }).ToArray();
        var retained = movedNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var movedEdges = layout.Edges.Where(e => ValidEdge(e) && edges.TryGetValue(e.Id, out var next) && retained.Contains(next.Source) && retained.Contains(next.Target))
            .Select(e => e with { Id = edges[e.Id].Id, Source = edges[e.Id].Source, Target = edges[e.Id].Target }).ToArray();
        return layout with { Nodes = movedNodes, Edges = movedEdges };
    }
}
