using System.Text;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    private string DotNewLine
    {
        get
        {
            var first = Index.Lines[Page.StartLine];
            return first.Next > first.End ? Source[first.End..first.Next] : Index.NewLine;
        }
    }

    private string? dotSyntaxSource;
    private int dotSyntaxPage = -1;
    private DotSyntax? dotSyntax;
    private DotSyntax? DotStructure
    {
        get
        {
            if (!ReferenceEquals(dotSyntaxSource, Source) || dotSyntaxPage != ActivePage)
            {
                dotSyntaxSource = Source;
                dotSyntaxPage = ActivePage;
                dotSyntax = Page.Family == "DOT" ? DotSyntax.Parse(Page.Source) : null;
            }
            return dotSyntax;
        }
    }

    private static bool DotNodeMatches(DotSyntax syntax, DotSourceLocation location) =>
        location.Kind == "node" && location.Id == "node:" + location.Name && syntax.Nodes.TryGetValue(location.Name, out var node) &&
        node.Token.Start == location.NameStart && node.Token.End - node.Token.Start == location.NameLength;

    private static DotSyntax.Scope? DotCluster(DotSyntax syntax, DotSourceLocation location) =>
        location.Kind != "cluster" || location.Id != "cluster:" + location.Name ? null :
        syntax.Scopes.FirstOrDefault(scope => scope.IsCluster && scope.Name == location.Name &&
            scope.Occurrences.Any(occurrence => occurrence.Start == location.Start && occurrence.End - occurrence.Start == location.Length));

    private DotSyntax.Node[]? DotNodes(IReadOnlyList<DotObjectEdit> selection, DotSyntax syntax)
    {
        if (selection.Count == 0 || selection.Any(item => !CurrentDot(item) || !DotNodeMatches(syntax, item.Location))) return null;
        return selection.Select(item => syntax.Nodes[item.Location.Name]).Distinct().OrderBy(node => node.Token.Start).ToArray();
    }

    private static bool CanDuplicateDotNode(DotSyntax syntax, DotSyntax.Node node) =>
        !syntax.Scopes.Any(scope => scope.Members.Contains(node.Name) && scope.Occurrences.Any(occurrence => occurrence.Owner?.Edge == true));

    /// <summary>Deletes complete node identities and incident edges, including chain and subgraph references, in one undo step.</summary>
    public bool RemoveDotObjects(IReadOnlyList<DotObjectEdit> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count == 1 && selection[0].Location.Kind == "edge") return RemoveDotEdge(selection[0]);
        if (DotStructure is not { } syntax || selection.Count == 0 || selection.Any(item => !CurrentDot(item))) return false;
        DotSyntax.Scope? cluster = selection.Count == 1 ? DotCluster(syntax, selection[0].Location) : null;
        var nodes = cluster == null ? DotNodes(selection, syntax) : cluster.Members.Select(name => syntax.Nodes[name]).ToArray();
        if (nodes == null) return false;
        var names = nodes.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        var changes = new List<DotChange>();
        var removedScopes = cluster?.Occurrences.ToArray() ?? [];
        foreach (var occurrence in removedScopes)
            RemoveDotTokens(syntax, changes, occurrence.Start, occurrence.End, occurrence.Owner?.Edge == true ? "{}" : "");
        foreach (var list in syntax.Lists)
        {
            if (removedScopes.Any(scope => list.Owner.Start >= scope.Start && list.Owner.Start < scope.End)) continue;
            if (!list.Nodes.Any(endpoint => names.Contains(endpoint.Node.Name))) continue;
            int remaining = list.Nodes.Count(endpoint => !names.Contains(endpoint.Node.Name));
            if (remaining == 0 && !list.Owner.Edge)
            {
                RemoveDotTokens(syntax, changes, list.Owner.Start, list.Owner.End, "");
                continue;
            }
            bool emittedEmpty = false, hasPrevious = false, separatorKept = false;
            int remainingAfter = remaining;
            for (int i = 0; i < list.Nodes.Count; i++)
            {
                var endpoint = list.Nodes[i];
                if (names.Contains(endpoint.Node.Name))
                {
                    string replacement = remaining == 0 && !emittedEmpty ? "{}" : "";
                    RemoveDotTokens(syntax, changes, endpoint.Name.Start, endpoint.End, replacement);
                    emittedEmpty = true;
                }
                else { hasPrevious = true; separatorKept = false; remainingAfter--; }
                if (i == list.Commas.Count) continue;
                bool keep = hasPrevious && !separatorKept && remainingAfter > 0;
                if (!keep) changes.Add(new(list.Commas[i].Start, list.Commas[i].End, ""));
                else separatorKept = true;
            }
        }
        if (cluster != null) ClearDotClusterEndpoints(syntax, changes, cluster.Name, removedScopes);
        return CommitDotChanges(syntax, changes, next => next.Nodes.Count == syntax.Nodes.Count - names.Count && !next.Nodes.Keys.Any(names.Contains));
    }

    public bool RemoveDotObject(DotObjectEdit item) => RemoveDotObjects([item]);

    /// <summary>Copies nodes, their scoped memberships and internal edges without connecting the copies to existing nodes.</summary>
    public bool DuplicateDotObjects(IReadOnlyList<DotObjectEdit> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (DotStructure is not { } syntax || DotNodes(selection, syntax) is not { Length: > 0 } nodes ||
            nodes.Any(node => !CanDuplicateDotNode(syntax, node))) return false;
        var occupied = syntax.Nodes.Keys.ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes) names[node.Name] = FreshDotName(node.Name + "Copy", occupied);
        var inserts = new SortedDictionary<int, StringBuilder>();
        foreach (var node in nodes)
        {
            string name = DotName(names[node.Name]);
            AddDotInsert(inserts, node.First.Start, name + DotAttributes(syntax, node.Attributes) + ";");
            foreach (var (scope, statement) in node.Appearances)
                if (!DotSyntax.Contains(scope, node.First.Scope)) AddDotInsert(inserts, statement.Start, name + ";");
        }
        int edgeCount = 0;
        foreach (var edge in syntax.Edges)
        {
            if (!names.TryGetValue(edge.From.Node.Name, out string? from) || !names.TryGetValue(edge.To.Node.Name, out string? to)) continue;
            AddDotInsert(inserts, edge.First.After, DotEndpoint(from, edge.From) + (syntax.Directed ? " -> " : " -- ") +
                DotEndpoint(to, edge.To) + DotAttributes(syntax, edge.Attributes) + ";");
            edgeCount++;
        }
        var changes = inserts.Select(pair => new DotChange(pair.Key, pair.Key, DotNewLine + "  " + pair.Value + DotNewLine + "  ")).ToArray();
        return CommitDotChanges(syntax, changes, next => next.Nodes.Count == syntax.Nodes.Count + nodes.Length &&
            next.Edges.Count == syntax.Edges.Count + edgeCount && names.Values.All(next.Nodes.ContainsKey),
            (layout, next) => CopyDotCanvas(layout, layout, syntax, syntax, next, names, true));
    }

    public bool DuplicateDotObject(DotObjectEdit item) => DuplicateDotObjects([item]);

    public bool CanGroupDotObjects(IReadOnlyList<DotObjectEdit> selection) => DotStructure is { } syntax &&
        DotNodes(selection, syntax) is { Length: > 0 } nodes && DotGroupScope(syntax, nodes) != null;

    /// <summary>Adds cluster membership while retaining the original declaration order, defaults and attributes.</summary>
    public bool GroupDotObjects(IReadOnlyList<DotObjectEdit> selection, string label)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (label.Length == 0 || label.Length > 65536 || label.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')) ||
            DotStructure is not { } syntax || DotNodes(selection, syntax) is not { Length: > 0 } nodes ||
            DotGroupScope(syntax, nodes) is not { } scope) return false;
        string name = FreshDotName("cluster_Group", syntax.Scopes.Select(item => item.Name).ToHashSet(StringComparer.Ordinal));
        var block = new StringBuilder(DotNewLine).Append("  subgraph ").Append(name).Append(" {").Append(DotNewLine)
            .Append("    label=").Append(QuoteDot(label)).Append(';').Append(DotNewLine);
        foreach (var node in nodes) block.Append("    ").Append(syntax.Text(node.Token)).Append(';').Append(DotNewLine);
        block.Append("  }").Append(DotNewLine);
        int at = scope.Occurrences[^1].Close;
        return CommitDotChanges(syntax, [new(at, at, block.ToString())], next => next.Nodes.Count == syntax.Nodes.Count && next.Edges.Count == syntax.Edges.Count);
    }

    /// <summary>Removes a cluster boundary while retaining its subgraph scope and local defaults.</summary>
    public bool UngroupDotObject(DotObjectEdit item)
    {
        if (!CurrentDot(item) || DotStructure is not { } syntax || DotCluster(syntax, item.Location) is not { } scope) return false;
        string name = FreshDotName("group_" + scope.Name, syntax.Scopes.Select(current => current.Name).ToHashSet(StringComparer.Ordinal));
        var changes = new List<DotChange>();
        foreach (var occurrence in scope.Occurrences)
            if (occurrence.Name is { } token) RemoveDotTokens(syntax, changes, token.Start, token.End, DotName(name));
        int close = scope.Occurrences[^1].Close;
        changes.Add(new(close, close, DotNewLine + "  cluster=false;" + DotNewLine));
        ClearDotClusterEndpoints(syntax, changes, scope.Name, []);
        return CommitDotChanges(syntax, changes, next => next.Nodes.Count == syntax.Nodes.Count && next.Edges.Count == syntax.Edges.Count);
    }

    /// <summary>Returns a self-contained DOT document with the selected nodes and their internal relationships.</summary>
    public string? CopyDotSelection(IReadOnlyList<DotObjectEdit> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (DotStructure is not { } syntax || DotNodes(selection, syntax) is not { Length: > 0 } nodes) return null;
        var names = nodes.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        var text = new StringBuilder("@startdot").Append(DotNewLine).Append(syntax.Directed ? "digraph" : "graph").Append(" {").Append(DotNewLine);
        foreach (var node in nodes) text.Append("  ").Append(DotName(node.Name)).Append(DotAttributes(syntax, node.Attributes)).Append(';').Append(DotNewLine);
        foreach (var edge in syntax.Edges)
            if (names.Contains(edge.From.Node.Name) && names.Contains(edge.To.Node.Name))
                text.Append("  ").Append(DotEndpoint(edge.From.Node.Name, edge.From)).Append(syntax.Directed ? " -> " : " -- ")
                    .Append(DotEndpoint(edge.To.Node.Name, edge.To)).Append(DotAttributes(syntax, edge.Attributes, false)).Append(';').Append(DotNewLine);
        string source = text.Append('}').Append(DotNewLine).Append("@enddot").ToString();
        if (DotSyntax.Parse(source) is not { } fragment) return null;
        var mapped = names.ToDictionary(name => name, name => name, StringComparer.Ordinal);
        return WithCanvasSelection(source, CanvasDotNodes(mapped), layout => CanvasDotEdges(syntax, fragment, mapped, layout));
    }

    public bool PasteDotSelection(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReadCanvasSelection(source, out string body, out var layout) || DotStructure is not { } target || DotSyntax.Parse(body) is not { } fragment || fragment.Nodes.Count == 0 || fragment.Directed != target.Directed) return false;
        var occupied = target.Nodes.Keys.ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in fragment.Nodes.Values) names[node.Name] = FreshDotName(node.Name, occupied);
        if (!CanvasClipboardMatches(layout, CanvasDotNodes(names), CanvasDotEdges(fragment, fragment, saved: layout))) return false;
        var text = new StringBuilder(DotNewLine);
        foreach (var node in fragment.Nodes.Values)
            text.Append("  ").Append(DotName(names[node.Name])).Append(DotAttributes(fragment, node.Attributes)).Append(';').Append(DotNewLine);
        foreach (var edge in fragment.Edges)
            text.Append("  ").Append(DotEndpoint(names[edge.From.Node.Name], edge.From)).Append(target.Directed ? " -> " : " -- ")
                .Append(DotEndpoint(names[edge.To.Node.Name], edge.To)).Append(DotAttributes(fragment, edge.Attributes, false)).Append(';').Append(DotNewLine);
        // At the start of the root, pasted objects receive the same built-in defaults as their self-contained source.
        int at = target.Root.Occurrences[0].Open;
        return CommitDotChanges(target, [new(at, at, text.ToString())], next => next.Nodes.Count == target.Nodes.Count + fragment.Nodes.Count &&
            next.Edges.Count == target.Edges.Count + fragment.Edges.Count,
            (current, next) => CopyDotCanvas(current, layout, fragment, target, next, names, false));
    }

    private static DotSyntax.Scope? DotGroupScope(DotSyntax syntax, DotSyntax.Node[] nodes)
    {
        DotSyntax.Scope? parent = null;
        foreach (var node in nodes)
        {
            var scope = syntax.Root;
            foreach (var cluster in syntax.Scopes.Where(candidate => candidate.IsCluster && candidate.Members.Contains(node.Name)))
            {
                if (DotSyntax.Contains(scope, cluster)) scope = cluster;
                else if (!DotSyntax.Contains(cluster, scope)) return null;
            }
            if (parent != null && parent != scope) return null;
            parent = scope;
        }
        var selected = nodes.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        if (syntax.Scopes.Any(scope => scope.Attributes.TryGetValue("rank", out var rank) && rank.Value.Length > 0 &&
            scope.Members.Any(selected.Contains) && scope.Members.Any(name => !selected.Contains(name)))) return null;
        return parent;
    }

    private static string FreshDotName(string basis, HashSet<string> occupied)
    {
        string name = basis;
        for (int suffix = 2; !occupied.Add(name); suffix++) name = basis + suffix;
        return name;
    }

    private static string DotName(string value)
    {
        bool plain = value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_' || value[0] >= 128) &&
            value.All(c => !char.IsWhiteSpace(c) && (char.IsAsciiLetterOrDigit(c) || c == '_' || c >= 128)) &&
            value.ToLowerInvariant() is not ("graph" or "digraph" or "subgraph" or "node" or "edge" or "strict");
        return plain ? value : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string DotEndpoint(string name, DotSyntax.Endpoint endpoint) => DotName(name) +
        (endpoint.Port.Length == 0 ? "" : ":" + endpoint.Port) + (endpoint.Compass.Length == 0 ? "" : ":" + endpoint.Compass);

    private static string DotAttributes(DotSyntax syntax, Dictionary<string, DotSyntax.Token> attributes, bool includeClusters = true)
    {
        string text = string.Join(", ", attributes.Where(pair => includeClusters || pair.Key is not ("lhead" or "ltail"))
            .Select(pair => DotName(pair.Key) + "=" + syntax.Text(pair.Value)));
        return text.Length == 0 ? "" : " [" + text + "]";
    }

    private void AddDotInsert(SortedDictionary<int, StringBuilder> inserts, int at, string text)
    {
        if (!inserts.TryGetValue(at, out var builder)) inserts.Add(at, builder = new());
        if (builder.Length != 0) builder.Append(DotNewLine).Append("  ");
        builder.Append(text);
    }

    private readonly record struct DotChange(int Start, int End, string Text);

    private static void RemoveDotTokens(DotSyntax syntax, List<DotChange> changes, int start, int end, string replacement)
    {
        int low = 0, high = syntax.Tokens.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (syntax.Tokens[middle].Start < start) low = middle + 1;
            else high = middle;
        }
        for (int i = low; i < syntax.Tokens.Count && syntax.Tokens[i].End <= end; i++)
        {
            var token = syntax.Tokens[i];
            changes.Add(new(token.Start, token.End, replacement));
            replacement = "";
        }
    }

    private static void ClearDotClusterEndpoints(DotSyntax syntax, List<DotChange> changes, string cluster, DotSyntax.ScopeOccurrence[] excluded)
    {
        foreach (var (name, value) in syntax.Attributes)
            if (name is "lhead" or "ltail" && value.Value == cluster && !excluded.Any(scope => value.Start >= scope.Start && value.End <= scope.End))
                RemoveDotTokens(syntax, changes, value.Start, value.End, "\"\"");
    }

    private bool CommitDotChanges(DotSyntax syntax, IReadOnlyList<DotChange> changes, Func<DotSyntax, bool> validate,
        Func<CanvasLayoutState?, DotSyntax, CanvasLayoutState?>? canvas = null)
    {
        if (changes.Count == 0) return false;
        var ordered = changes.OrderBy(change => change.Start).ThenBy(change => change.End).ToArray();
        int at = 0;
        var output = new StringBuilder(syntax.Source.Length);
        foreach (var change in ordered)
        {
            if (change.Start < at || change.End < change.Start || change.End > syntax.Source.Length) return false;
            output.Append(syntax.Source, at, change.Start - at).Append(change.Text);
            at = change.End;
            if (output.Length > 1_048_576) return false;
        }
        output.Append(syntax.Source, at, syntax.Source.Length - at);
        string source = output.ToString();
        if (DotSyntax.Parse(source) is not { } next || !validate(next)) return false;
        int start = 0, oldEnd = syntax.Source.Length, newEnd = source.Length;
        while (start < oldEnd && start < newEnd && syntax.Source[start] == source[start]) start++;
        while (oldEnd > start && newEnd > start && syntax.Source[oldEnd - 1] == source[newEnd - 1]) { oldEnd--; newEnd--; }
        string replacement = source[start..newEnd];
        return CommitCanvasSourceEdit(document => document.PatchDot(start, oldEnd - start, replacement), canvas == null ? null : layout => canvas(layout, next));
    }
}
