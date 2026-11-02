namespace UnoUML.Core;

/// <summary>Owns the verbatim PlantUML text. The index is a projection, never a serializer.</summary>
public sealed class StudioDocument
{
    private sealed record Edit(int At, string Before, string After, int Page);
    private const int HistoryLimit = 200;
    private const int CharacterBudget = 4 * 1024 * 1024;
    private readonly List<Edit> _history = [];
    private int _cursor, _storedChars, _activePage;
    private long _lastTypingTick;
    private DiagramIndex? _index;
    private DiagramIndex Index => _index ??= new(Source);
    public string Source { get; private set; }
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _history.Count;
    public IReadOnlyList<DiagramPage> Pages => Index.Pages;
    public int ActivePage
    {
        get => Math.Clamp(_activePage, 0, Pages.Count - 1);
        set { _activePage = Math.Clamp(value, 0, Pages.Count - 1); _lastTypingTick = 0; }
    }
    public DiagramPage Page => Pages[ActivePage];
    public IReadOnlyList<DiagramObject> Objects => Index.Objects(ActivePage);
    public StudioDocument(string source) => Source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Record only the changed interval. Index rebuilding is lazy, so source edits can be debounced by the host.</summary>
    public void SetSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Source == source) return;
        int start = 0, oldEnd = Source.Length, newEnd = source.Length;
        while (start < oldEnd && start < newEnd && Source[start] == source[start]) start++;
        while (oldEnd > start && newEnd > start && Source[oldEnd - 1] == source[newEnd - 1]) { oldEnd--; newEnd--; }
        Record(new(start, Source[start..oldEnd], source[start..newEnd], _activePage), source, true);
    }

    private bool Patch(int start, int end, string insert)
    {
        if (Source.AsSpan(start, end - start).SequenceEqual(insert)) return false;
        var edit = new Edit(start, Source[start..end], insert, _activePage);
        Record(edit, string.Concat(Source.AsSpan(0, start), insert, Source.AsSpan(end)), false);
        return true;
    }

    private void Record(Edit edit, string next, bool typing)
    {
        for (int i = _history.Count - 1; i >= _cursor; i--) Drop(i);
        long now = Environment.TickCount64;
        bool key = typing && edit.Before.Length == 0 && edit.After.Length == 1 && edit.After[0] != '\n';
        if (key && _lastTypingTick != 0 && now - _lastTypingTick < 700 && _cursor > 0 &&
            _history[_cursor - 1] is { Before.Length: 0 } prev && prev.Page == edit.Page && prev.At + prev.After.Length == edit.At)
        {
            _history[_cursor - 1] = prev with { After = prev.After + edit.After };
            _storedChars++;
        }
        else
        {
            _history.Add(edit);
            _cursor++;
            _storedChars += edit.Before.Length + edit.After.Length;
        }
        while (_history.Count > 1 && (_history.Count > HistoryLimit || _storedChars > CharacterBudget)) { Drop(0); _cursor--; }
        _lastTypingTick = key ? now : 0;
        Source = next;
        _index = null;
    }

    private void Drop(int index)
    {
        var edit = _history[index];
        _storedChars -= edit.Before.Length + edit.After.Length;
        _history.RemoveAt(index);
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var edit = _history[--_cursor];
        Restore(edit, edit.After, edit.Before);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        var edit = _history[_cursor++];
        Restore(edit, edit.Before, edit.After);
        return true;
    }

    private void Restore(Edit edit, string old, string next)
    {
        Source = string.Concat(Source.AsSpan(0, edit.At), next, Source.AsSpan(edit.At + old.Length));
        _index = null;
        _activePage = edit.Page;
        _lastTypingTick = 0;
    }

    public DiagramObject? Find(string? alias, int? sourceLine = null)
    {
        if (sourceLine.HasValue)
        {
            // Prefer the innermost source object, e.g. a member inside a class.
            DiagramObject? best = null;
            foreach (var obj in Objects)
            {
                if (obj.Line == sourceLine) return obj;
                if (obj.Line < sourceLine && obj.EndLine >= sourceLine && (best == null || best.Line < obj.Line)) best = obj;
            }
            if (best != null) return best;
        }
        return string.IsNullOrEmpty(alias) ? null : Objects.FirstOrDefault(o => !o.Ambiguous && o.Alias == alias);
    }

    private bool Current(DiagramObject item) => Objects.Contains(item) && !item.Unclosed;
    private string LocalNewLines(string text) => text.ReplaceLineEndings(Index.NewLine);

    public bool Insert(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet)) return false;
        string text = LocalNewLines(snippet.TrimEnd('\r', '\n'));
        var end = Index.Lines[Page.EndLine];
        if (end.Code.TrimStart().StartsWith("@end", StringComparison.OrdinalIgnoreCase))
            return Patch(end.Start, end.Start, text + Index.NewLine);
        return Patch(end.End, end.End, (end.Start == end.End ? "" : Index.NewLine) + text);
    }

    public bool Add(ToolItem tool)
    {
        if (!StudioCatalog.Accepts(tool, Page)) return false;
        string expanded = tool.Snippet;
        if (expanded.Contains("{id}", StringComparison.Ordinal))
        {
            string id = NewAlias(string.Concat(tool.Name.Where(char.IsLetterOrDigit)));
            expanded = expanded.Replace("{id}", id, StringComparison.Ordinal);
        }
        return Insert(expanded);
    }

    private string NewAlias(string stem)
    {
        if (stem.Length == 0 || !char.IsLetter(stem[0])) stem = "Item" + stem;
        // Include implicit declaration sites, preprocessor symbols and endpoints in the collision test.
        string source = Page.Source;
        string id = stem;
        for (int n = 2; ContainsIdentifier(source, id); n++) id = stem + n;
        return id;
    }

    private static bool ContainsIdentifier(string source, string word)
    {
        static bool Id(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '$';
        for (int at = 0; at < source.Length;)
        {
            int p = source.IndexOf(word, at, StringComparison.Ordinal);
            if (p < 0) return false;
            at = p + word.Length;
            if ((p == 0 || !Id(source[p - 1])) && (at == source.Length || !Id(source[at]))) return true;
        }
        return false;
    }

    public bool Connect(DiagramObject from, DiagramObject to, string arrow = "-->", string label = "")
    {
        if (!Current(from) || !Current(to) || !from.IsConnectable || !to.IsConnectable || !OneLine(label)) return false;
        if (arrow is not ("-->" or "->" or "<-" or "<--" or "--" or ".." or "..>" or "<|--" or "--|>" or "*--" or "o--" or "<|.." or "..|>" or "->>" or "-->>" or "<->" or "<-->")) return false;
        return Insert($"{from.Alias} {arrow} {to.Alias}" + (label.Length == 0 ? "" : " : " + label));
    }

    public bool Rename(DiagramObject item, string label)
    {
        if (!Current(item) || !item.CanRename || !OneLine(label) || label.Length == 0 || label == item.Label) return false;
        string text = label;
        if (item.Alias.Length != 0)
        {
            text = "\"" + label.Replace("\"", "\\\"") + "\"";
            if (item.PlainName) text += " as " + item.Alias;
        }
        else if (item.Kind == "Relationship" && item.LabelLength == 0 && !Index.Lines[item.Line].Code.TrimEnd().EndsWith(':'))
            text = " : " + label;
        return Patch(item.Offset + item.LabelAt, item.Offset + item.LabelAt + item.LabelLength, text);
    }

    private static bool OneLine(string label) => label.IndexOfAny(['\r', '\n']) < 0;

    public bool Replace(DiagramObject item, string statement) =>
        Current(item) && Patch(item.Offset, item.End, LocalNewLines(statement));

    public bool Remove(DiagramObject item)
    {
        if (!Current(item)) return false;
        // Explicit relationships to unique declarations are removed atomically with their declarations.
        var aliases = Objects.Where(x => x.Line >= item.Line && x.Line <= item.EndLine && x.IsConnectable)
                             .Select(x => x.Alias).ToHashSet(StringComparer.Ordinal);
        var ranges = Objects.Where(x => !(x.Line >= item.Line && x.Line <= item.EndLine) &&
                                          x.Kind == "Relationship" && (aliases.Contains(x.From) || aliases.Contains(x.To)))
                            .Append(item).OrderBy(x => x.Line).ToArray();
        int start = ranges[0].Offset, cursor = start;
        var result = new System.Text.StringBuilder();
        foreach (var range in ranges)
        {
            result.Append(Source, cursor, range.Offset - cursor);
            cursor = Index.Lines[range.EndLine].Next;
        }
        return Patch(start, cursor, result.ToString());
    }

    public bool Duplicate(DiagramObject item)
    {
        if (!Current(item) || item.Ambiguous) return false;
        // A nested alias can be referenced by arbitrary preprocessing. Require a manual edit for a subtree copy.
        if (Objects.Any(o => o.Line > item.Line && o.Line <= item.EndLine && o.Alias.Length != 0)) return false;
        string copy = item.Statement;
        if (item.IsConnectable)
        {
            string alias = NewAlias(item.Alias + "Copy");
            copy = string.Concat(copy.AsSpan(0, item.AliasAt), alias, copy.AsSpan(item.AliasAt + item.AliasLength));
        }
        return Patch(item.End, item.End, Index.NewLine + copy);
    }

    public bool Move(DiagramObject item, int direction)
    {
        if (!Current(item) || direction == 0) return false;
        var siblings = Objects.Where(o => o.ParentLine == item.ParentLine && !o.Unclosed).ToList();
        int i = siblings.IndexOf(item) + Math.Sign(direction);
        if (i < 0 || i >= siblings.Count) return false;
        return direction < 0 ? MoveBefore(item, siblings[i]) : MoveBefore(siblings[i], item);
    }

    public bool MoveBefore(DiagramObject from, DiagramObject to)
    {
        if (!Current(from) || !Current(to) || from == to || from.ParentLine != to.ParentLine) return false;
        int after = Index.Lines[from.EndLine].Next;
        if (after == to.Offset) return false;
        if (from.Offset > to.Offset)
        {
            string block = Source[from.Offset..after];
            if (after == from.End) block += Index.NewLine;
            return Patch(to.Offset, after, block + Source[to.Offset..from.Offset]);
        }
        return Patch(from.Offset, to.Offset, Source[after..to.Offset] + Source[from.Offset..after]);
    }
}
