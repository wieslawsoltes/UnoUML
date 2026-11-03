namespace UnoUML.Core;

internal sealed partial class DiagramIndex
{
    private DiagramObject? CaptionObject(int line, int end, int parent, string code)
    {
        string keyword = code.TrimStart().Split(' ', '\t')[0].ToLowerInvariant();
        if (keyword is not ("note" or "hnote" or "rnote" or "title" or "header" or "footer" or "legend" or "caption")) return null;
        if (end <= line) return null;
        int start = Lines[line].Next, stop = end == line + 1 ? start : Lines[end - 1].End;
        return new(_source, Lines[line].Start, Lines[end].End, line, end, parent,
            keyword.EndsWith("note", StringComparison.Ordinal) ? "Note" : "Caption", _source[start..stop])
        {
            LabelAt = start - Lines[line].Start,
            LabelLength = stop - start,
            MultilineLabel = true,
            ClosingKeyword = "end" + keyword
        };
    }

    private List<DiagramObject> FormulaObjects(DiagramPage page)
    {
        var result = new List<DiagramObject>();
        for (int line = page.StartLine + 1; line < page.EndLine; line++)
        {
            var row = Lines[line];
            string code = row.Code.Trim();
            if (code.Length == 0) continue;
            string keyword = code.Split(' ', '\t')[0].ToLowerInvariant();
            bool settings = keyword is "scale" or "dpi" or "color" or "backgroundcolor" || code.StartsWith('!');
            result.Add(new(_source, row.Start, row.End, line, line, -1, settings ? "Source" : "Formula", code)
            {
                LabelAt = settings ? -1 : row.Code.Length - row.Code.TrimStart().Length,
                LabelLength = settings ? 0 : code.Length,
                EditorLanguage = page.Family == "LaTeX" ? "tex" : "asciimath"
            });
        }
        return result;
    }
}

public sealed partial class StudioDocument
{
    public DiagramObject? Drawing => Page.Family == "Ditaa" ? Objects.FirstOrDefault(item => item.Kind == "ASCII drawing") : null;

    private bool RenameBlock(DiagramObject item, string content)
    {
        if (!Current(item) || !item.CanRename || content == item.EditableLabel || content.Length > 1_048_576) return false;
        if (item.Kind == "Formula" && (!OneLine(content) || string.IsNullOrWhiteSpace(content))) return false;
        bool comment = false;
        foreach (var line in content.AsSpan().EnumerateLines())
        {
            var text = DiagramIndex.MaskComments(line.ToString(), ref comment).AsSpan().Trim();
            if (text.StartsWith("@start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("@end", StringComparison.OrdinalIgnoreCase)) return false;
            if (!text.StartsWith("end", StringComparison.OrdinalIgnoreCase)) continue;
            string closing = string.Concat(text.ToString().Where(c => !char.IsWhiteSpace(c)));
            if (item.Kind == SequenceSyntax.ReferenceKind && (closing.Equals("end", StringComparison.OrdinalIgnoreCase) ||
                closing.Equals("endref", StringComparison.OrdinalIgnoreCase))) return false;
            if (item.ClosingKeyword.Length > 0 && closing.Equals(item.ClosingKeyword, StringComparison.OrdinalIgnoreCase)) return false;
        }
        string replacement = LocalNewLines(content);
        if (item.MultilineLabel && item.Kind != "ASCII drawing" && item.EndLine == item.Line + 1) replacement += Index.NewLine;
        return Patch(item.Offset + item.LabelAt, item.Offset + item.LabelAt + item.LabelLength, replacement);
    }
}
