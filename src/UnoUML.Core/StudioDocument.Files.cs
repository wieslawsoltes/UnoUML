using System.Text;
using UnoUML.Syntax;

namespace UnoUML.Core;

/// <summary>An immutable literal files-tree snapshot. Reopen it after a document change.</summary>
public sealed class FilesDiagramEdit
{
    internal FilesDiagramEdit(string source, int page, int offset, FilesSource syntax)
    {
        Source = source; Page = page; Offset = offset; Syntax = syntax;
        Entries = Array.AsReadOnly(syntax.Nodes.Select((_, index) => new FilesEntryEdit(this, index)).ToArray());
        Declarations = Array.AsReadOnly(syntax.Declarations.Select(declaration => Entries[declaration.Node]).ToArray());
    }
    internal string Source { get; }
    internal int Page { get; }
    internal int Offset { get; }
    internal FilesSource Syntax { get; }
    /// <summary>Files and explicit or inferred directories, in first-appearance order.</summary>
    public IReadOnlyList<FilesEntryEdit> Entries { get; }
    /// <summary>Only explicitly written paths, in source order.</summary>
    public IReadOnlyList<FilesEntryEdit> Declarations { get; }
}

/// <summary>A semantic path with an exact name token and native canvas identity.</summary>
public sealed class FilesEntryEdit
{
    internal FilesEntryEdit(FilesDiagramEdit diagram, int index) { Diagram = diagram; Index = index; }
    public FilesDiagramEdit Diagram { get; }
    public int Index { get; }
    internal FilesSourceNode Syntax => Diagram.Syntax.Nodes[Index];
    public string Name => Syntax.Name;
    public string Path => Syntax.Path;
    public bool IsDirectory => Syntax.Directory;
    public bool IsExplicit => Syntax.Declaration >= 0;
    public string ParentPath => Syntax.Parent < 0 ? "/" : Diagram.Syntax.Nodes[Syntax.Parent].Path;
    public int Depth => Syntax.Depth;
    public string CanvasId => Syntax.Identity;
    public int Offset => Diagram.Offset + Syntax.NameToken.Start;
    public int Length => Syntax.NameToken.Length;
}

public sealed partial class StudioDocument
{
    public FilesDiagramEdit? OpenFilesDiagram()
    {
        if (Page.Family != "Files" || FilesSource.Parse(Page.Source) is not { } syntax) return null;
        return new(Source, ActivePage, Index.Lines[Page.StartLine].Start, syntax);
    }

    public FilesEntryEdit? OpenFilesEntry(SourceFieldEdit field)
    {
        if (field.Kind != "files-name" || !ReferenceEquals(field.Document, Source) || field.Page != ActivePage || OpenFilesDiagram() is not { } diagram) return null;
        return diagram.Entries.FirstOrDefault(entry => entry.Offset == field.Offset && entry.Length == field.Length);
    }

    /// <summary>Find an explicit path declaration selected through the source outline.</summary>
    public FilesEntryEdit? OpenFilesEntry(DiagramObject item)
    {
        if (!Current(item) || OpenFilesDiagram() is not { } diagram) return null;
        int localLine = item.Line - Page.StartLine;
        var declaration = diagram.Syntax.Declarations.FirstOrDefault(path => path.Row.Index == localLine);
        return declaration == null ? null : diagram.Entries[declaration.Node];
    }

    public FilesEntryEdit? FindFilesEntry(string path) => OpenFilesDiagram() is { } diagram && diagram.Syntax.ByPath.TryGetValue(path, out int index) ? diagram.Entries[index] : null;

    /// <summary>Insert an absolute path. A trailing slash declares a directory; index is a declaration slot.</summary>
    public bool InsertFilesEntry(FilesDiagramEdit diagram, string path, int index = -1)
    {
        if (!CurrentFiles(diagram) || !FilesSource.TryPath(path, out _, out bool directory)) return false;
        var syntax = diagram.Syntax;
        if (syntax.ByPath.TryGetValue(path, out int existing) && (!directory || syntax.Nodes[existing].Declaration >= 0)) return false;
        if (index == -1) index = syntax.Declarations.Count;
        if (index < 0 || index > syntax.Declarations.Count) return false;
        int at = index == syntax.Declarations.Count ? syntax.End.Start : syntax.Declarations[index].Row.Start;
        string indent = syntax.Declarations.Count == 0 ? "" : FilesIndent(syntax.Declarations[Math.Min(index, syntax.Declarations.Count - 1)]);
        return CommitFiles(diagram, [new(at, at, indent + path + FilesNewLine(diagram))]);
    }

    /// <summary>Rename a file or directory component; directory descendants change atomically.</summary>
    public bool RenameFilesEntry(FilesEntryEdit entry, string name)
    {
        if (!FilesSource.LiteralName(name)) return false;
        return MoveFilesEntry(entry, entry.ParentPath + name + (entry.IsDirectory ? "/" : ""));
    }

    /// <summary>Move a complete subtree to a new literal path without changing its visual geometry.</summary>
    public bool MoveFilesEntry(FilesEntryEdit entry, string destination)
    {
        if (!FilesDestination(entry, destination)) return false;
        var changes = new List<Change>();
        foreach (var path in entry.Diagram.Syntax.Declarations.Where(path => FilesWithin(path.Path, entry)))
            changes.Add(new(path.Token.Start, path.Token.End, FilesRemapPath(path.Path, entry, destination)));
        var map = entry.Diagram.Entries.Where(node => FilesWithin(node.Path, entry))
            .ToDictionary(node => node.Path, node => FilesRemapPath(node.Path, entry, destination), StringComparer.Ordinal);
        return CommitFiles(entry.Diagram, changes, map);
    }

    /// <summary>Duplicate a literal file or subtree, copying authored geometry two grid cells away.</summary>
    public bool DuplicateFilesEntry(FilesEntryEdit entry, string destination)
    {
        if (!FilesDestination(entry, destination)) return false;
        var source = entry.Diagram.Syntax.Declarations.Where(path => FilesWithin(path.Path, entry)).ToArray();
        if (source.Length == 0) return false;
        int at = source[^1].Row.Next;
        string newline = FilesNewLine(entry.Diagram);
        var text = new StringBuilder();
        foreach (var path in source)
        {
            // Independent comments remain in their original slots. The copied
            // declaration carries its same-line comment but no surrounding rows.
            text.Append(Page.Source, path.Row.Start, path.Token.Start - path.Row.Start)
                .Append(FilesRemapPath(path.Path, entry, destination))
                .Append(Page.Source, path.Token.End, path.Row.End - path.Token.End).Append(newline);
        }
        var copies = entry.Diagram.Entries.Where(node => FilesWithin(node.Path, entry))
            .ToDictionary(node => node.Path, node => FilesRemapPath(node.Path, entry, destination), StringComparer.Ordinal);
        return CommitFiles(entry.Diagram, [new(at, at, text.ToString())], copies: copies);
    }

    /// <summary>Remove a file or explicit empty directory. Nonempty directories require recursive=true.</summary>
    public bool RemoveFilesEntry(FilesEntryEdit entry, bool recursive = false)
    {
        if (!CurrentFiles(entry.Diagram) || !recursive && entry.IsDirectory && entry.Diagram.Entries.Any(node => node.Path != entry.Path && FilesWithin(node.Path, entry))) return false;
        var changes = new List<Change>();
        foreach (var path in entry.Diagram.Syntax.Declarations.Where(path => FilesWithin(path.Path, entry)))
        {
            var row = path.Row;
            bool trivia = !string.IsNullOrWhiteSpace(Page.Source[row.Start..path.Token.Start]) || !string.IsNullOrWhiteSpace(Page.Source[path.Token.End..row.End]);
            // Retain inline/block comments as standalone trivia when their path
            // disappears. A bare declaration removes its entire physical row.
            changes.Add(trivia ? new(path.Token.Start, path.Token.End, "") : new(row.Start, row.Next, ""));
        }
        return CommitFiles(entry.Diagram, changes);
    }

    /// <summary>Change sibling order without changing their paths or their authored geometry.</summary>
    public bool ReorderFilesEntry(FilesEntryEdit entry, int index)
    {
        if (!CurrentFiles(entry.Diagram)) return false;
        var syntax = entry.Diagram.Syntax;
        var siblings = entry.Diagram.Entries.Where(node => node.Syntax.Parent == entry.Syntax.Parent).ToList();
        int old = siblings.FindIndex(node => node.Index == entry.Index);
        if (index < 0 || index >= siblings.Count || index == old) return false;
        siblings.RemoveAt(old); siblings.Insert(index, entry);
        var rows = syntax.Declarations.Where(path => siblings.Any(node => FilesWithin(path.Path, node))).ToArray();
        var ordered = siblings.SelectMany(node => syntax.Declarations.Where(path => FilesWithin(path.Path, node))).ToArray();
        // Source slots and their comments remain fixed. Only complete path
        // tokens are permuted, so multiline trivia cannot be split or duplicated.
        return CommitFiles(entry.Diagram, rows.Select((path, i) => new Change(path.Token.Start, path.Token.End, ordered[i].Path)).ToList());
    }

    /// <summary>Copy one subtree as a standalone absolute-path fragment with optional authored geometry.</summary>
    public string? CopyFilesEntry(FilesEntryEdit entry)
    {
        if (!CurrentFiles(entry.Diagram)) return null;
        var text = new StringBuilder();
        foreach (var path in entry.Diagram.Syntax.Declarations.Where(path => FilesWithin(path.Path, entry)))
            text.Append(FilesIndent(path)).Append('/').Append(path.Path.AsSpan(entry.ParentPath.Length))
                .Append(Page.Source, path.Token.End, path.Row.End - path.Token.End).Append(FilesNewLine(entry.Diagram));
        string body = text.ToString();
        if (FilesSource.Parse("@startfiles\n" + body + "@endfiles") is not { } fragment || fragment.Declarations.Count == 0) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in entry.Diagram.Entries.Where(node => FilesWithin(node.Path, entry)))
            if (fragment.ByPath.TryGetValue("/" + node.Path[entry.ParentPath.Length..], out int index))
                map.Add(node.CanvasId, fragment.Nodes[index].Identity);
        return WithCanvasSelection(body, map, _ => new Dictionary<string, string>());
    }

    /// <summary>
    /// Paste a literal paths fragment into a directory. Colliding root names get
    /// deterministic copy suffixes; authored relative geometry follows the paths.
    /// </summary>
    public bool PasteFilesEntries(FilesDiagramEdit diagram, string text, string directory = "/")
    {
        if (!CurrentFiles(diagram) || text.Length > CanvasLayoutLimits.MaxMetadataCharacters + 4_000_000 ||
            directory != "/" && (!diagram.Syntax.ByPath.TryGetValue(directory, out int parent) || !diagram.Syntax.Nodes[parent].Directory) ||
            !ReadCanvasSelection(text, out string body, out var clipboard)) return false;
        string newline = FilesNewLine(diagram);
        body = body.ReplaceLineEndings(newline).TrimEnd('\r', '\n') + newline;
        string header = "@startfiles" + newline;
        if (FilesSource.Parse(header + body + "@endfiles") is not { } fragment || fragment.Declarations.Count == 0) return false;
        var ids = fragment.Nodes.ToDictionary(node => node.Identity, node => node.Identity, StringComparer.Ordinal);
        if (!CanvasClipboardMatches(clipboard, ids, new Dictionary<string, string>())) return false;
        var occupied = diagram.Entries.Select(node => node.Path.TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in fragment.Nodes.Where(node => node.Parent < 0))
        {
            string candidate = directory + root.Name;
            if (occupied.Contains(candidate))
            {
                int suffix = 1;
                do { candidate = directory + FilesCopyName(root.Name, root.Directory, suffix++); }
                while (occupied.Contains(candidate) && suffix <= FilesSource.MaxNodes + 1);
                if (occupied.Contains(candidate)) return false;
            }
            occupied.Add(candidate);
            roots.Add(root.Path, candidate + (root.Directory ? "/" : ""));
        }
        string Target(FilesSourceNode node)
        {
            var root = node;
            while (root.Parent >= 0) root = fragment.Nodes[root.Parent];
            return roots[root.Path] + node.Path[root.Path.Length..];
        }
        var replacements = fragment.Declarations.Select(path => new Change(path.Token.Start - header.Length,
            path.Token.End - header.Length, Target(fragment.Nodes[path.Node]))).ToList();
        body = Rewrite(body, replacements, 0, body.Length);
        var mapping = fragment.Nodes.ToDictionary(node => node.Identity, Target, StringComparer.Ordinal);
        return CommitFiles(diagram, [new(diagram.Syntax.End.Start, diagram.Syntax.End.Start, body)], clipboard: clipboard, clipboardPaths: mapping);
    }

    /// <summary>A collision-free sibling path suitable for a Duplicate dialog.</summary>
    public string? SuggestFilesCopyPath(FilesEntryEdit entry)
    {
        if (!CurrentFiles(entry.Diagram)) return null;
        var occupied = entry.Diagram.Entries.Select(node => node.Path.TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
        for (int copy = 1; copy <= FilesSource.MaxNodes + 1; copy++)
        {
            string path = entry.ParentPath + FilesCopyName(entry.Name, entry.IsDirectory, copy);
            if (!occupied.Contains(path)) return path + (entry.IsDirectory ? "/" : "");
        }
        return null;
    }

    private static string FilesCopyName(string name, bool directory, int copy)
    {
        int extension = directory ? -1 : name.LastIndexOf('.');
        if (extension <= 0) extension = name.Length;
        return name[..extension] + " copy" + (copy == 1 ? "" : " " + copy.ToString(System.Globalization.CultureInfo.InvariantCulture)) + name[extension..];
    }

    private bool ApplyFilesNameField(SourceFieldEdit field, string text) => OpenFilesEntry(field) is { } entry && RenameFilesEntry(entry, text);

    private bool CurrentFiles(FilesDiagramEdit diagram) => ReferenceEquals(diagram.Source, Source) && diagram.Page == ActivePage && Page.Family == "Files";
    private static bool FilesWithin(string path, FilesEntryEdit entry) => path == entry.Path || entry.IsDirectory && path.StartsWith(entry.Path, StringComparison.Ordinal);
    private static string FilesRemapPath(string path, FilesEntryEdit entry, string destination) => destination + path[entry.Path.Length..];
    private bool FilesDestination(FilesEntryEdit entry, string destination) => CurrentFiles(entry.Diagram) &&
        FilesSource.TryPath(destination, out _, out bool directory) && directory == entry.IsDirectory && destination != entry.Path &&
        !entry.Diagram.Syntax.ByPath.ContainsKey(destination) && !(entry.IsDirectory && destination.StartsWith(entry.Path, StringComparison.Ordinal));
    private string FilesIndent(FilesSourceDeclaration path)
    {
        int end = path.Row.Start;
        while (end < path.Token.Start && Page.Source[end] is ' ' or '\t') end++;
        return Page.Source[path.Row.Start..end];
    }
    private string FilesNewLine(FilesDiagramEdit diagram)
    {
        var header = diagram.Syntax.Header;
        return header.Next > header.End ? Page.Source[header.End..header.Next] : Index.NewLine;
    }

    private bool CommitFiles(FilesDiagramEdit diagram, List<Change> changes,
        IReadOnlyDictionary<string, string>? paths = null, IReadOnlyDictionary<string, string>? copies = null,
        CanvasLayoutState? clipboard = null, IReadOnlyDictionary<string, string>? clipboardPaths = null)
    {
        if (!CurrentFiles(diagram) || changes.Count == 0) return false;
        int end = 0;
        foreach (var change in changes.OrderBy(change => change.At).ThenBy(change => change.End))
        {
            if (change.At < end || change.At < 0 || change.End < change.At || change.End > Page.Source.Length) return false;
            end = change.End;
        }
        string candidate = Rewrite(Page.Source, changes, 0, Page.Source.Length);
        if (candidate == Page.Source || FilesSource.Parse(candidate) is not { } after) return false;
        var global = changes.Select(change => new Change(diagram.Offset + change.At, diagram.Offset + change.End, change.Text)).ToList();
        return CommitCanvasSourceEdit(preview => preview.ApplyChanges(global), layout =>
        {
            var next = RemapFilesCanvas(layout, diagram.Syntax, after, paths, copies);
            if (clipboard == null || clipboardPaths == null) return next;
            var map = clipboardPaths.Where(pair => after.ByPath.ContainsKey(pair.Value)).ToDictionary(pair => pair.Key,
                pair => after.Nodes[after.ByPath[pair.Value]].Identity, StringComparer.Ordinal);
            return AppendCanvasCopy(next, CanvasSelectionGeometry(clipboard, map, new Dictionary<string, string>(), false));
        });
    }

    private static CanvasLayoutState? RemapFilesCanvas(CanvasLayoutState? layout, FilesSource before, FilesSource after,
        IReadOnlyDictionary<string, string>? paths, IReadOnlyDictionary<string, string>? copies)
    {
        if (layout == null) return null;
        var original = before.Nodes.ToDictionary(node => node.Identity, StringComparer.Ordinal);
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in layout.Nodes)
        {
            if (!original.TryGetValue(node.Id, out var semantic))
            {
                if (!node.Id.StartsWith("files:", StringComparison.Ordinal)) mapped.Add(node.Id, node.Id);
                continue;
            }
            string path = paths != null && paths.TryGetValue(semantic.Path, out var renamed) ? renamed : semantic.Path;
            if (after.ByPath.TryGetValue(path, out int index) && after.Nodes[index].Directory == semantic.Directory)
                mapped.Add(node.Id, after.Nodes[index].Identity);
            else if (node.Locked) throw new InvalidOperationException("Unlock a file or folder before removing its row.");
        }
        if (layout.Edges.Any(edge => edge.Locked && (!mapped.ContainsKey(edge.Source) || !mapped.ContainsKey(edge.Target))))
            throw new InvalidOperationException("Unlock incident connections before removing their rows.");
        var nodes = layout.Nodes.Where(node => mapped.ContainsKey(node.Id)).Select(node => node with
        {
            Id = mapped[node.Id], Parent = node.Parent is { } parent && mapped.TryGetValue(parent, out var mappedParent) ? mappedParent : null
        }).ToArray();
        var edges = layout.Edges.Where(edge => mapped.ContainsKey(edge.Source) && mapped.ContainsKey(edge.Target)).Select(edge => edge with
        {
            Source = mapped[edge.Source], Target = mapped[edge.Target]
        }).ToArray();
        var result = layout with { Nodes = nodes, Edges = edges };
        if (copies == null) return result;
        var duplicate = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var semantic in before.Nodes)
            if (copies.TryGetValue(semantic.Path, out string? path) && after.ByPath.TryGetValue(path, out int index))
                duplicate.Add(semantic.Identity, after.Nodes[index].Identity);
        return AppendCanvasCopy(result, CanvasSelectionGeometry(layout, duplicate, new Dictionary<string, string>(), false));
    }
}
