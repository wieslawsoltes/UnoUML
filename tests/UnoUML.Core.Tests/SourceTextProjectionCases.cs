using UnoUML.Core;

internal static class SourceTextProjectionCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("Source text projection: " + message); checks++; }

        // Model the deferred native TextChanged after a programmatic Refresh.
        // Neither CR coercion nor the semantic textarea's LF spelling is an edit.
        foreach (var template in StudioCatalog.Templates)
        {
            var document = new StudioDocument(template.Source);
            var projection = new SourceTextProjection(document.Source);
            document.SetSource(projection.ApplyEditorText(projection.EditorText));
            document.SetSource(projection.ApplyEditorText(projection.EditorText.Replace('\r', '\n')));
            Check(document.Source == template.Source && !document.CanUndo && document.HistoryState == 0,
                template.Name + " startup normalization leaves source and history untouched");
        }

        const string mixed = "\uFEFFa\r\nb\nc\rd😀e\r\nf";
        var map = new SourceTextProjection(mixed);
        Check(map.Source == mixed && map.EditorText == "\uFEFFa\rb\rc\rd😀e\rf", "projects only CR/LF while preserving BOM and surrogate pairs");
        int[] sourceToEditor = [0, 1, 2, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 12, 13];
        int[] editorToSource = [0, 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 15];
        for (int offset = 0; offset < sourceToEditor.Length; offset++)
            Check(map.ToEditorOffset(offset) == sourceToEditor[offset], "source boundary " + offset);
        for (int offset = 0; offset < editorToSource.Length; offset++)
        {
            Check(map.ToSourceOffset(offset) == editorToSource[offset], "editor boundary " + offset);
            Check(map.ToEditorOffset(map.ToSourceOffset(offset)) == offset, "every editor boundary round trips " + offset);
        }
        Check(map.ToEditorRange(2, 2) == (2, 1) && map.ToSourceRange(2, 1) == (2, 2), "CRLF selection maps its endpoints independently");
        Check(map.ToEditorRange(3, 1) == (3, 0) && map.ToSourceOffset(3) == 4, "an interior CRLF boundary resolves after the complete source newline");
        Check(map.ToEditorRange(9, 2) == (8, 2) && map.ToSourceRange(8, 2) == (9, 2), "UTF-16 surrogate selection retains two code units");
        Check(map.ToEditorRange(0, mixed.Length) == (0, map.EditorText.Length) &&
            map.ToSourceRange(0, map.EditorText.Length) == (0, mixed.Length), "whole document range includes the BOM");
        Check(map.ToSourceOffset(-1) == 0 && map.ToEditorOffset(int.MaxValue) == map.EditorText.Length &&
            map.ToSourceRange(1, int.MaxValue) == (1, mixed.Length - 1), "invalid and overflowing selection bounds clamp safely");

        const string separators = "\uFEFFa\u0085b\u2028c\u2029d\ve\ff\r\ng";
        var unicode = new SourceTextProjection(separators);
        Check(unicode.EditorText == "\uFEFFa\u0085b\u2028c\u2029d\ve\ff\rg" &&
            ReferenceEquals(unicode.ApplyEditorText(unicode.EditorText), separators), "native projection does not rewrite Unicode separators or BOM");
        Check(ReferenceEquals(map.ApplyEditorText("\uFEFFa\nb\r\nc\nd😀e\nf"), mixed), "normalization-only updates preserve the original mixed source instance");

        const string source = "\uFEFF@startuml\r\nclass A\nclass B\rclass C\r\n@enduml";
        var original = new SourceTextProjection(source);
        string changed = original.ApplyEditorText(original.EditorText.Replace("class B", "class Beta", StringComparison.Ordinal));
        Check(changed == source.Replace("class B", "class Beta", StringComparison.Ordinal), "single-label edit retains all surrounding mixed newline bytes and BOM");
        var documentWithHistory = new StudioDocument(source);
        documentWithHistory.SetSource(changed);
        long editedState = documentWithHistory.HistoryState;
        var editedProjection = new SourceTextProjection(documentWithHistory.Source);
        documentWithHistory.SetSource(editedProjection.ApplyEditorText(editedProjection.EditorText));
        Check(documentWithHistory.HistoryState == editedState && documentWithHistory.CanUndo, "queued refresh does not append a second history entry");
        Check(documentWithHistory.Undo() && documentWithHistory.Source == source && !documentWithHistory.CanUndo,
            "one undo restores the exact original mixed source");
        var undoProjection = new SourceTextProjection(documentWithHistory.Source);
        documentWithHistory.SetSource(undoProjection.ApplyEditorText(undoProjection.EditorText));
        Check(documentWithHistory.CanRedo && documentWithHistory.Redo() && documentWithHistory.Source == changed,
            "a deferred undo refresh preserves redo history and exact edited bytes");

        var sourceIndex = new StudioDocument(source);
        int editorAt = original.EditorText.IndexOf("class B", StringComparison.Ordinal);
        var item = sourceIndex.FindAtOffset(original.ToSourceOffset(editorAt), out int page);
        Check(item?.Alias == "B" && page == 0, "source selection resolves the correct indexed object after CRLF collapse");
        var range = original.ToEditorRange(item!.Offset, item.End - item.Offset);
        Check(original.EditorText.Substring(range.Start, range.Length) == new SourceTextProjection(item.Statement).EditorText &&
            original.ToSourceRange(range.Start, range.Length) == (item.Offset, item.End - item.Offset), "canvas-to-source object selection returns the exact original span");

        foreach (var (before, find, insert, expected) in new[]
        {
            ("alpha\r\nbeta\ngamma\rdelta", "al", "al\nnew", "al\r\nnewpha\r\nbeta\ngamma\rdelta"),
            ("alpha\r\nbeta\ngamma\rdelta", "be", "be\rnew", "alpha\r\nbe\nnewta\ngamma\rdelta"),
            ("alpha\r\nbeta\ngamma\rdelta", "gam", "gam\r\nnew", "alpha\r\nbeta\ngam\rnewma\rdelta"),
            ("alpha\r\nbeta\ngamma\rdelta", "del", "del\nnew", "alpha\r\nbeta\ngamma\rdel\rnewta"),
            ("alpha", "al", "al\rnew", "al\nnewpha"),
            ("\uFEFFalpha\r\n", "alpha", "new\ralpha", "\uFEFFnew\r\nalpha\r\n")
        })
        {
            var projection = new SourceTextProjection(before);
            string next = projection.EditorText.Replace(find, insert, StringComparison.Ordinal);
            Check(projection.ApplyEditorText(next) == expected, "inserted lines use the edited line's newline convention: " + find);
        }
        Check(new SourceTextProjection("").ApplyEditorText("first\rsecond") == "first\nsecond", "new documents use LF");
        Check(new SourceTextProjection("first\r\n").ApplyEditorText("first\rnext\r") == "first\r\nnext\r\n", "appending at EOF retains the preceding CRLF convention");

        var delete = new SourceTextProjection("\uFEFFone\r\ntwo\nthree\rfour\r\n");
        Check(delete.ApplyEditorText("\uFEFFonetwo\rthree\rfour\r") == "\uFEFFonetwo\nthree\rfour\r\n", "deleting a projected CR removes both original CRLF code units");
        Check(delete.ApplyEditorText("\uFEFFone\rthree\rfour\r") == "\uFEFFone\r\nthree\rfour\r\n", "deleting one line retains the untouched prefix and suffix delimiters");
        Check(delete.ApplyEditorText("") == "", "explicit select-all deletion can remove the BOM and the complete source");
        Check(new SourceTextProjection("\uFEFF").ApplyEditorText("\uFEFFclass A") == "\uFEFFclass A", "typing into a BOM-only document preserves the BOM");
        return checks;
    }
}
