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

    public DiagramObject? FindAtOffset(int offset, out int pageIndex)
    {
        pageIndex = -1;
        if (offset < 0 || offset > Source.Length) return null;
        var lines = Index.Lines;
        int low = 0, high = lines.Count - 1;
        while (low < high)
        {
            int mid = low + ((high - low + 1) / 2);
            if (lines[mid].Start <= offset) low = mid;
            else high = mid - 1;
        }
        int line = low;
        for (int i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            if (line < page.StartLine || line > page.EndLine) continue;
            pageIndex = i;
            DiagramObject? best = null;
            foreach (var item in Index.Objects(i))
            {
                if (item.Line > line) break;
                if (item.EndLine >= line) best = item;
            }
            return best;
        }
        return null;
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

    public bool Include(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Split('/').Any(s => s is "" or "." or "..") ||
            path.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '.' or '_' or '-'))) return false;
        string directive = "!include <" + path + ">";
        for (int i = Page.StartLine; i <= Page.EndLine; i++)
            if (Index.Lines[i].Code.Trim().Equals(directive, StringComparison.OrdinalIgnoreCase)) return false;
        var first = Index.Lines[Page.StartLine];
        if (!first.Code.TrimStart(' ', '\t', '\uFEFF').StartsWith("@start", StringComparison.OrdinalIgnoreCase)) return InsertAt(Page.StartLine, directive);
        if (first.Next == first.End) return Patch(first.End, first.End, Index.NewLine + directive);
        return Patch(first.Next, first.Next, directive + Index.NewLine);
    }

    public bool Add(ToolItem tool)
    {
        if (!StudioCatalog.Accepts(tool, Page)) return false;
        if (Page.Family == "JSON" && tool.Group == "Data") return AppendJsonValue();
        string expanded = tool.Snippet;
        if (expanded.Contains("{id}", StringComparison.Ordinal))
        {
            string id = NewAlias(string.Concat(tool.Name.Where(char.IsLetterOrDigit)));
            expanded = expanded.Replace("{id}", id, StringComparison.Ordinal);
        }
        if (tool.Group is "Topology" or "Wireframe" or "Network")
        {
            // These languages require children inside the outer graph/table container.
            for (int i = Page.EndLine; i >= Page.StartLine; i--)
                if (Index.Lines[i].Code.Trim() == "}") return InsertAt(i, "  " + expanded);
            return false;
        }
        if (tool.Group == "Activities")
        {
            if (tool.Name == "Swimlane")
            {
                for (int i = Page.StartLine; i <= Page.EndLine; i++)
                    if (Index.Lines[i].Code.Trim() == "start") return InsertAt(i, expanded);
            }
            for (int i = Page.EndLine; i >= Page.StartLine; i--)
                if (Index.Lines[i].Code.Trim() is "stop" or "end" or "kill" or "detach") return InsertAt(i, expanded);
        }
        if (tool.Group is "Lifelines" or "Timing")
        {
            var first = Objects.FirstOrDefault(o => o.Kind == "Relationship" || (tool.Group == "Timing" && Index.Lines[o.Line].Code.TrimStart().StartsWith('@')));
            if (first != null) return InsertAt(first.Line, expanded);
        }
        return Insert(expanded);
    }

    private bool AppendJsonValue()
    {
        int start = Index.Lines[Page.StartLine].Next;
        int end = Index.Lines[Page.EndLine].Start;
        if (end <= start) return false;
        string json = Source[start..end];
        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            var root = parsed.RootElement;
            bool map = root.ValueKind == System.Text.Json.JsonValueKind.Object;
            if (!map && root.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
            bool empty = map ? !root.EnumerateObject().Any() : root.GetArrayLength() == 0;
            int close = json.LastIndexOf(map ? '}' : ']');
            int insert = close;
            while (insert > 0 && char.IsWhiteSpace(json[insert - 1])) insert--;
            string key = "property";
            if (map)
                for (int n = 2; root.TryGetProperty(key, out _); n++) key = "property" + n;
            string value = map ? "\"" + key + "\": \"value\"" : "\"value\"";
            return Patch(start + insert, start + insert, (empty ? "" : ",") + Index.NewLine + "  " + value);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private bool InsertAt(int line, string snippet)
    {
        int at = Index.Lines[line].Start;
        return Patch(at, at, LocalNewLines(snippet) + Index.NewLine);
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
        if (!ValidArrow(arrow)) return false;
        return Insert($"{from.Alias} {arrow} {to.Alias}" + (label.Length == 0 ? "" : " : " + label));
    }

    private static bool ValidArrow(string arrow) =>
        arrow is "-->" or "->" or "<-" or "<--" or "--" or ".." or "..>" or "<|--" or "--|>" or "*--" or "o--" or "<|.." or "..|>" or "->>" or "-->>" or "<->" or "<-->";

    public bool SetArrow(DiagramObject item, string arrow) => UpdateConnection(item, null, null, arrow);

    public bool Retarget(DiagramObject item, DiagramObject from, DiagramObject to) => UpdateConnection(item, from, to, item.Arrow);

    /// <summary>Change endpoints, appearance and caption of one relationship as a single undo operation.</summary>
    public bool UpdateConnection(DiagramObject item, DiagramObject? from, DiagramObject? to, string arrow, string? label = null)
    {
        if (!Current(item) || item.Kind != "Relationship" || (arrow != item.Arrow && !ValidArrow(arrow))
            || (from != null && (!Current(from) || !from.IsConnectable)) || (to != null && (!Current(to) || !to.IsConnectable))
            || (label != null && !OneLine(label))) return false;
        var raw = new System.Text.StringBuilder(item.Statement);
        // Work right-to-left, keeping caller-supplied escape text, whitespace, cardinalities and comments verbatim.
        if (label != null && label != item.Label)
        {
            bool missingColon = item.LabelLength == 0 && !Index.Lines[item.Line].Code.TrimEnd().EndsWith(':');
            raw.Remove(item.LabelAt, item.LabelLength).Insert(item.LabelAt, (missingColon ? " : " : "") + label);
        }
        if (to != null) raw.Remove(item.ToAt, item.To.Length).Insert(item.ToAt, to.Alias);
        raw.Remove(item.ArrowAt, item.Arrow.Length).Insert(item.ArrowAt, arrow);
        if (from != null) raw.Remove(item.FromAt, item.From.Length).Insert(item.FromAt, from.Alias);
        return Patch(item.Offset, item.End, raw.ToString());
    }

    public bool SetFill(DiagramObject item, string colorHex)
    {
        if (!Current(item) || !item.CanSetFill || colorHex.Length != 7 || colorHex[0] != '#' || colorHex.AsSpan(1).IndexOfAnyExcept("0123456789ABCDEFabcdef") >= 0) return false;
        int at = item.Offset + item.ColorAt;
        return Patch(at, at + item.ColorLength, item.ColorLength > 0 ? colorHex : " " + colorHex);
    }

    public bool InsertMember(DiagramObject item, string member)
    {
        if (!Current(item) || !item.CanInsertMember || !OneLine(member) || string.IsNullOrWhiteSpace(member) || member.Trim() is "{" or "}") return false;
        string decl = Index.Lines[item.Line].Code;
        string indent = decl[..(decl.Length - decl.TrimStart().Length)];
        if (item.EndLine > item.Line)
            return InsertAt(item.EndLine, indent + "  " + member.Trim());
        // Insert the opening brace before any trailing comment; preserve the comment on the declaration line.
        int at = item.Offset + decl.TrimEnd().Length;
        string originalTail = Source[at..item.End];
        return Patch(at, item.End, " {" + originalTail + Index.NewLine + indent + "  " + member.Trim() + Index.NewLine + indent + "}");
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
