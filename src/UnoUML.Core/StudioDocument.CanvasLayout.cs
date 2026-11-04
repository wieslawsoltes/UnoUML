namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    public const string CanvasLayoutMarker = "' @unouml:canvas ";

    /// <summary>Read metadata immediately preceding the active diagram. Missing metadata has no error.</summary>
    public bool TryGetCanvasLayout(out CanvasLayoutState? layout, out string? error)
    {
        layout = null;
        if (!CanvasMetadataLocation(out int line, out _, out error) || line < 0) return false;
        var text = Index.Lines[line];
        string raw = Source[text.Start..text.End].TrimStart('\uFEFF', ' ', '\t');
        return CanvasLayoutJson.TryParse(raw[CanvasLayoutMarker.TrimEnd().Length..].TrimStart(), out layout, out error);
    }

    /// <summary>Replace or remove the active diagram's layout as one undoable source edit. Invalid metadata is never overwritten.</summary>
    public bool SetCanvasLayout(CanvasLayoutState? layout) => TrySetCanvasLayout(layout, out _);

    /// <summary>Returns false for invalid input or an unchanged source. Error is null for an unchanged source.</summary>
    public bool TrySetCanvasLayout(CanvasLayoutState? layout, out string? error)
    {
        if (!CanvasMetadataLocation(out int line, out int boundary, out error)) return false;
        if (line >= 0 && !TryGetCanvasLayout(out _, out error)) return false;
        string? json;
        try { json = layout == null ? null : CanvasLayoutJson.Serialize(layout); }
        catch (ArgumentException ex) { error = ex.Message; return false; }
        error = null;
        if (line >= 0)
        {
            var metadata = Index.Lines[line];
            int start = metadata.Start == 0 && Source.StartsWith('\uFEFF') ? 1 : metadata.Start;
            return json == null ? Patch(start, metadata.Next, "") : Patch(start, metadata.End, CanvasLayoutMarker + json);
        }
        if (json == null) return false;
        var first = Index.Lines[boundary];
        int at = first.Start == 0 && Source.StartsWith('\uFEFF') ? 1 : first.Start;
        // Prefer this diagram's actual line ending; other pages can deliberately use a different convention.
        string newline = first.Next > first.End ? Source[first.End..first.Next] : Index.NewLine;
        return Patch(at, at, CanvasLayoutMarker + json + newline);
    }

    private bool CanvasMetadataLocation(out int line, out int boundary, out string? error)
    {
        line = -1; boundary = Page.StartLine; error = null;
        var first = Index.Lines[boundary];
        string code = first.Code.TrimStart('\uFEFF', ' ', '\t');
        if (!code.StartsWith("@start", StringComparison.OrdinalIgnoreCase))
        {
            error = "Canvas layout requires an explicit @start diagram boundary.";
            return false;
        }
        if (boundary == 0 || !IsCanvasMetadataLine(boundary - 1)) return true;
        line = boundary - 1;
        if (line > 0 && IsCanvasMetadataLine(line - 1))
        {
            error = "Multiple adjacent canvas metadata comments are ambiguous.";
            return false;
        }
        return true;
    }

    private bool IsCanvasMetadataLine(int line)
    {
        var range = Index.Lines[line];
        string text = Source[range.Start..range.End].TrimStart('\uFEFF', ' ', '\t');
        const string prefix = "' @unouml:canvas";
        return text.StartsWith(prefix, StringComparison.Ordinal) && (text.Length == prefix.Length || char.IsWhiteSpace(text[prefix.Length]) || text[prefix.Length] == '{');
    }
}
