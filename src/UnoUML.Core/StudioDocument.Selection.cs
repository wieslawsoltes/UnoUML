using System.Text;

namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    private readonly record struct Change(int At, int End, string Text);

    private List<DiagramObject>? SelectionRoots(IReadOnlyList<DiagramObject> selection)
    {
        if (selection.Count == 0) return null;
        var current = Objects.ToHashSet();
        var roots = new List<DiagramObject>();
        foreach (var item in selection.Distinct().OrderBy(o => o.Line))
        {
            if (!current.Contains(item) || item.Unclosed) return null;
            if (roots.Count == 0 || roots[^1].EndLine < item.Line) roots.Add(item);
        }
        return roots;
    }

    private static bool Inside(DiagramObject item, List<DiagramObject> roots)
    {
        // Binary search the ordered, disjoint roots. Large selections stay O(N log K).
        int low = 0, high = roots.Count - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            var root = roots[mid];
            if (item.Line < root.Line) high = mid - 1;
            else if (item.Line > root.EndLine) low = mid + 1;
            else return true;
        }
        return false;
    }

    private HashSet<string> SelectedAliases(List<DiagramObject> roots)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Objects)
        {
            if (!item.IsConnectable || !Inside(item, roots)) continue;
            names.Add(item.Alias);
            if (item.ParentLine >= 0) names.Add(Qualified(item));
        }
        return names;
    }

    private string Qualified(DiagramObject item, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var path = new Stack<string>();
        path.Push(aliases?.GetValueOrDefault(item.Alias, item.Alias) ?? item.Alias);
        while (item.ParentLine >= 0 && Objects.FirstOrDefault(o => o.Line == item.ParentLine) is { } parent)
        {
            if (parent.Alias.Length != 0) path.Push(aliases?.GetValueOrDefault(parent.Alias, parent.Alias) ?? parent.Alias);
            item = parent;
        }
        return string.Join(".", path);
    }

    private List<DiagramObject> WithConnections(List<DiagramObject> roots, bool incident)
    {
        var aliases = SelectedAliases(roots);
        var items = new List<DiagramObject>(roots);
        foreach (var item in Objects)
        {
            if (Inside(item, roots) || item.Kind is not ("Relationship" or "Layout")) continue;
            bool from = aliases.Contains(item.From), to = aliases.Contains(item.To);
            if (incident ? from || to : from && to) items.Add(item);
        }
        items.Sort((a, b) => a.Line.CompareTo(b.Line));
        return items;
    }

    /// <summary>Preserves unselected source, including comments, even between non-contiguous selected objects.</summary>
    public bool RemoveMany(IReadOnlyList<DiagramObject> selection)
    {
        if (Page.Family == "Ditaa" && selection.Any(o => o.Kind == LegacySyntax.AsciiLabel))
        {
            if (selection.Any(o => o.Kind != LegacySyntax.AsciiLabel || !Objects.Contains(o))) return false;
            return ApplyChanges(selection.Distinct().Select(o => new Change(o.Offset, o.End, new string(' ', o.Label.Length))).ToList());
        }
        var roots = SelectionRoots(selection);
        if (roots == null) return false;
        var items = WithConnections(roots, incident: true);
        if (SequenceProtectedSelection(items)) return false;
        var changes = items
            .Select(item => new Change(item.Offset, Index.Lines[item.EndLine].Next, "")).ToList();
        return ApplyChanges(changes);
    }

    public string? CopySelection(IReadOnlyList<DiagramObject> selection)
    {
        if (Page.Family == "Ditaa" && selection.Any(o => o.Kind == LegacySyntax.AsciiLabel))
        {
            if (selection.Any(o => o.Kind != LegacySyntax.AsciiLabel || !Objects.Contains(o))) return null;
            return string.Join(Index.NewLine, selection.Distinct().OrderBy(o => o.Offset).Select(o => o.Label));
        }
        var roots = SelectionRoots(selection);
        if (roots == null) return null;
        var items = WithConnections(roots, incident: false);
        if (SequenceProtectedSelection(items)) return null;
        string fragment = Fragment(items);
        if (!AliasCanvasFamily || !RemapFragment(fragment, out string portable, out var remap, items, renameCollisions: false)) return fragment;
        return WithCanvasSelection(portable, CanvasFragmentNodes(items, remap, false));
    }

    public bool CutSelection(IReadOnlyList<DiagramObject> selection, out string fragment)
    {
        fragment = CopySelection(selection) ?? "";
        return fragment.Length > 0 && RemoveMany(selection);
    }

    private string Fragment(List<DiagramObject> items)
    {
        var output = new StringBuilder();
        int lastLine = -1;
        foreach (var item in items)
        {
            if (lastLine >= 0)
            {
                bool trivia = true;
                for (int i = lastLine + 1; i < item.Line && trivia; i++) trivia = string.IsNullOrWhiteSpace(Index.Lines[i].Code);
                output.Append(trivia ? Source[Index.Lines[lastLine].End..item.Offset] : Index.NewLine);
            }
            output.Append(Source, item.Offset, item.End - item.Offset);
            lastLine = item.EndLine;
        }
        return output.ToString();
    }

    public bool DuplicateMany(IReadOnlyList<DiagramObject> selection)
    {
        if (Page.Family == "Ditaa" && selection.Any(o => o.Kind == LegacySyntax.AsciiLabel)) return false;
        var roots = SelectionRoots(selection);
        if (roots == null || roots.Any(o => o.ParentLine != roots[0].ParentLine || o.Ambiguous)) return false;
        if (Page.Family == "Chen" && roots.Any(o => o.Kind == LegacySyntax.ChenAttribute)) return false;
        var items = WithConnections(roots, incident: false);
        if (SequenceProtectedSelection(items)) return false;
        if (items.Any(o => o.ParentLine != roots[0].ParentLine)) return false;
        if (!RemapFragment(Fragment(items), out string copy, out var remap, items)) return false;
        var last = items[^1];
        string insertion = Index.NewLine + copy;
        return CommitAliasCanvasCopy(document => document.Patch(last.End, last.End, insertion), CanvasFragmentNodes(items, remap, true));
    }

    public bool PasteSelection(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (!ReadCanvasSelection(fragment, out string body, out var layout) || !RemapFragment(body, out string copy, out var remap) ||
            !CanvasClipboardMatches(layout, remap.Nodes, layout == null ? null : CanvasAliasEdges(layout, remap.Nodes)) || layout != null && !AliasCanvasFamily) return false;
        return layout == null ? Insert(copy) : CommitAliasCanvasCopy(document => document.Insert(copy), remap.Nodes, layout, false);
    }

    private bool RemapFragment(string fragment, out string copy, out CanvasFragmentMap remap,
        List<DiagramObject>? originalItems = null, bool renameCollisions = true)
    {
        copy = "";
        remap = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        string body = LocalNewLines(fragment.TrimEnd('\r', '\n'));
        string dialect = Page.Family == "Chen" ? "chen" : "uml";
        string prefix = Page.Family == "C4" ? "@startuml\n!include <C4/C4_Context>\n" : "@start" + dialect + "\n";
        var parsed = new StudioDocument(prefix + body + "\n@end" + dialect);
        // An embedded diagram boundary belongs to a complete document, not a selection.
        if (parsed.Pages.Count != 1 || parsed.Page.EndLine != parsed.Index.Lines.Count - 1) return false;
        if (parsed.Objects.Any(SequenceSyntax.CreationBound)) return false;
        if (Page.Family == "Chen" && parsed.Objects.Any(o => o.Kind == "Source")) return false;
        if (Page.Family == "C4" && parsed.Objects.Any(o => o.Kind == "Source" || o.Kind == "Library" && o.Offset >= prefix.Length)) return false;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var reserved = parsed.Objects.Where(o => o.Alias.Length != 0).Select(o => o.Alias).ToHashSet(StringComparer.Ordinal);
        var spans = new List<Change>();
        foreach (var item in parsed.Objects)
        {
            if (item.Ambiguous || item.Unclosed) return false;
            if (!renameCollisions || !item.IsConnectable || !ContainsIdentifier(Page.Source, item.Alias)) continue;
            if (item.Alias.StartsWith('"')) return false;
            string stem = item.Alias + "Copy", alias = stem;
            for (int n = 2; ContainsIdentifier(Page.Source, alias) || !reserved.Add(alias); n++) alias = stem + n;
            replacements.Add(item.Alias, alias);
            int at = item.Offset - prefix.Length + item.AliasAt;
            spans.Add(new(at, at + item.AliasLength, alias));
        }
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in parsed.Objects.Where(o => o.IsConnectable))
        {
            string path = parsed.Qualified(declaration);
            // Alias tokens may themselves contain dots; remap the parsed
            // ancestry instead of splitting its rendered qualified string.
            paths[path] = parsed.Qualified(declaration, replacements);
            if (declaration.Alias.StartsWith('"')) continue;
            string identity = FlatCanvasAliases ? declaration.Alias : path;
            remap.Aliases[declaration.Alias] = identity;
            remap.Nodes[identity] = FlatCanvasAliases ? replacements.GetValueOrDefault(declaration.Alias, declaration.Alias) : paths[path];
        }
        // A selection lifted out of its original parent has shorter qualified
        // paths. Rebind only endpoints proven to name copied declarations;
        // external qualified references retain their original spelling.
        if (originalItems != null)
            foreach (var (original, fragmentId) in CanvasFragmentNodes(originalItems, remap, false))
                paths[original] = remap.Nodes[fragmentId];
        foreach (var item in parsed.Objects)
        {
            if (item.Kind is not ("Relationship" or "Layout")) continue;
            RemapEndpoint(item.From, item.FromAt);
            RemapEndpoint(item.To, item.ToAt);
            void RemapEndpoint(string endpoint, int column)
            {
                string mapped = replacements.GetValueOrDefault(endpoint, paths.GetValueOrDefault(endpoint, endpoint));
                if (mapped == endpoint) return;
                int at = item.Offset - prefix.Length + column;
                spans.Add(new(at, at + endpoint.Length, mapped));
            }
        }
        // Never guess which tokens inside arbitrary preprocessor calls or directives are references.
        foreach (var item in parsed.Objects)
            if (replacements.Count > 0 && item.Kind is "Source" or "Library" or SequenceSyntax.ReferenceKind && item.ParentLine < 0 &&
                replacements.Keys.Any(alias => ContainsIdentifier(item.Statement, alias))) return false;
        copy = Rewrite(body, spans, 0, body.Length);
        return true;
    }

    /// <summary>Group sibling declarations. Unique short names keep resolving to the same entity.
    /// Rewrite qualified edges outside the package when the qualified path changes.</summary>
    public bool Group(IReadOnlyList<DiagramObject> selection, string name)
    {
        if (Page.Family is not ("Class" or "Object" or "Component" or "Deployment" or "Use case") ||
            !OneLine(name) || string.IsNullOrWhiteSpace(name)) return false;
        var roots = SelectionRoots(selection);
        if (roots == null || roots.Any(o => o.ParentLine != roots[0].ParentLine)) return false;
        var nodes = roots.Where(o => o.IsConnectable).ToList();
        if (nodes.Count == 0) return false;
        var selectedNames = SelectedAliases(nodes);
        if (roots.Any(o => !o.IsConnectable && (o.Kind is not ("Relationship" or "Layout") || !selectedNames.Contains(o.From) || !selectedNames.Contains(o.To)))) return false;
        roots = nodes;
        string alias = NewAlias(string.Concat(name.Where(char.IsLetterOrDigit)));
        var items = WithConnections(roots, incident: false).Where(o => o.ParentLine == roots[0].ParentLine).ToList();
        var qualifiedRoots = roots.Select(root => (Path: Qualified(root), root.Alias)).ToList();
        var moved = new List<Change>();
        var spans = new List<Change>();
        foreach (var edge in Objects)
        {
            if (edge.Kind is not ("Relationship" or "Layout")) continue;
            var output = Inside(edge, items) ? moved : spans;
            Qualify(edge.From, edge.FromAt);
            Qualify(edge.To, edge.ToAt);
            void Qualify(string endpoint, int column)
            {
                if (!endpoint.Contains('.')) return;
                foreach (var root in qualifiedRoots)
                {
                    if (endpoint != root.Path && !endpoint.StartsWith(root.Path + ".", StringComparison.Ordinal)) continue;
                    int scope = root.Path.Length - root.Alias.Length;
                    int at = edge.Offset + column;
                    output.Add(new(at, at + endpoint.Length, endpoint.Insert(scope, alias + ".")));
                    return;
                }
            }
        }
        var blocks = new List<(int Start, int EndLine)>();
        foreach (var item in items)
        {
            if (blocks.Count > 0)
            {
                var previous = blocks[^1];
                bool contiguous = true;
                for (int line = previous.EndLine + 1; line < item.Line && contiguous; line++)
                    contiguous = string.IsNullOrWhiteSpace(Index.Lines[line].Code);
                if (contiguous) { blocks[^1] = (previous.Start, item.EndLine); continue; }
            }
            blocks.Add((item.Offset, item.EndLine));
        }
        string escaped = name.Replace("\"", "\\\"");
        var group = new StringBuilder().Append("package \"").Append(escaped).Append("\" as ").Append(alias).Append(" {").Append(Index.NewLine);
        foreach (var block in blocks)
        {
            var last = Index.Lines[block.EndLine];
            group.Append(Rewrite(Source, moved.Where(c => c.At >= block.Start && c.End <= last.End).ToList(), block.Start, last.End));
            group.Append(Index.NewLine);
            spans.Add(new(block.Start, last.Next, ""));
        }
        group.Append('}').Append(Index.NewLine);
        spans.Add(new(items[0].Offset, items[0].Offset, group.ToString()));
        return ApplyChanges(spans);
    }

    private bool ApplyChanges(List<Change> changes)
    {
        int first = changes.Min(c => c.At), last = changes.Max(c => c.End);
        return Patch(first, last, Rewrite(Source, changes, first, last));
    }

    private static string Rewrite(string source, List<Change> changes, int start, int end)
    {
        var output = new StringBuilder();
        int cursor = start;
        foreach (var change in changes.OrderBy(c => c.At).ThenBy(c => c.End))
        {
            output.Append(source, cursor, change.At - cursor).Append(change.Text);
            cursor = change.End;
        }
        output.Append(source, cursor, end - cursor);
        return output.ToString();
    }
}
