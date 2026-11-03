using UnoUML.Core;

internal static class BlockEditCases
{
    internal static void Run(Action<bool, string> check)
    {
        const string continued = "@startuml\r\n!include <C4/C4_Context>\r\nPerson(\\\r\n  $label=\"Customer\",\\\r\n  $alias=user, $descr=\"Visitor\") ' caller\r\nSystem(other.app, 'Sales \"portal\"')\r\nRel($label=\"Uses\", $to=other.app, $from=user, $techn=\"HTTPS\") ' edge\r\n@enduml";
        var c4 = new StudioDocument(continued);
        var caller = c4.Find("user")!;
        check(caller is { CanRename: true, Line: 2, EndLine: 4 } && caller.Label == "Customer", "continued C4 call keeps source spans and named argument binding");
        check(c4.Find(null, 3) == caller && c4.Find("other.app")?.Label == "Sales \"portal\"", "continued call lookup and single-quoted dotted alias");
        check(c4.Rename(caller, "Buyer") && c4.Source == continued.Replace("\"Customer\"", "\"Buyer\""), "continued C4 rename preserves continuation and named parameter trivia");
        check(c4.Undo() && c4.Source == continued, "undo continued C4 edit");
        var relation = c4.Objects.Single(o => o.Kind == "Relationship");
        check(relation.From == "user" && relation.To == "other.app", "reordered C4 named endpoints are indexed");
        check(c4.UpdateConnection(relation, c4.Find("other.app"), c4.Find("user"), "BiRel", "Responds") &&
            c4.Source.Contains("BiRel($label=\"Responds\", $to=user, $from=other.app, $techn=\"HTTPS\") ' edge"),
            "named C4 connection transaction patches in source order");
        check(c4.Undo() && c4.Source == continued, "named connection undo is exact");
        check(c4.Duplicate(c4.Find("user")!) && c4.Find("userCopy") is { EndLine: 7 }, "continued C4 clone remaps the named alias");
        check(c4.Undo() && c4.Source == continued, "continued clone undo is exact");

        foreach (string dialect in new[] { "math", "latex" })
        {
            string input = "@start" + dialect + "\r\ncolor blue\r\n  x^2   ' formula\r\n@end" + dialect;
            var formula = new StudioDocument(input);
            var expression = formula.Find(null, 2)!;
            check(expression.Kind == "Formula" && expression.CanRename && formula.Rename(expression, "x^3") &&
                formula.Source == input.Replace("x^2", "x^3"), "formula canvas edit preserves configuration, indentation and comments " + dialect);
            check(formula.Undo() && formula.Source == input && !formula.Rename(formula.Find(null, 2)!, "x\n@end" + dialect), "formula history and boundary validation " + dialect);
        }

        const string grid = "@startditaa\r\n+-------+\r\n| Box   |\r\n+-------+\r\n@endditaa";
        const string replacement = "+-------+    +-------+\n| Input +--->| Output|\n+-------+    +-------+";
        var drawing = new StudioDocument(grid);
        var geometry = drawing.Drawing!;
        check(geometry.MultilineLabel && !geometry.IsConnectable && geometry.EditorLanguage == "ditaa", "Ditaa exposes a whole-drawing canvas editor");
        check(drawing.Rename(geometry, replacement) && drawing.Source == "@startditaa\r\n" + replacement.ReplaceLineEndings("\r\n") + "\r\n@endditaa", "Ditaa geometry authoring is one verbatim CRLF transaction");
        check(!drawing.Rename(geometry, "stale") && drawing.Undo() && drawing.Source == grid, "Ditaa geometry stale span and undo protection");
        check(!drawing.Rename(drawing.Drawing!, "@endditaa\n@startuml\nclass Injected"), "Ditaa canvas edit cannot escape its diagram boundary");

        var emptyReference = new StudioDocument("@startuml\nparticipant A\nref over A\nend ref\n@enduml");
        check(!emptyReference.Rename(emptyReference.Find(null, 2)!, "end\t ref ' injected close"), "reference editor recognizes whitespace and comments around closing delimiters");
        check(emptyReference.Rename(emptyReference.Find(null, 2)!, "First\nSecond") && emptyReference.Source.Contains("ref over A\nFirst\nSecond\nend ref"), "empty reference body inserts its own final newline");

        var history = new StudioDocument("first");
        history.SetSource("second");
        var session = history.Fork();
        check(session.Undo() && session.Source == "first" && history.Source == "second" && history.CanUndo,
            "forked include edit sessions preserve independent undo history");
        session.SetSource("third");
        check(!session.CanRedo && history.Undo() && history.Source == "first", "cancelled include session cannot mutate its retained history");

        foreach (string header in new[] { "note right of A", "hnote over A", "rnote over A", "title", "legend right", "footer" })
        {
            string keyword = header.Split(' ')[0];
            string input = "@startuml\nparticipant A\n" + header + "\nold content\nend " + keyword + "\n@enduml";
            var caption = new StudioDocument(input);
            var body = caption.Find(null, 2)!;
            check(body.MultilineLabel && caption.Rename(body, "new\ncontent") && caption.Source == input.Replace("old content", "new\ncontent"),
                "multiline caption body edits preserve declaration and terminator " + header);
            check(caption.Undo() && caption.Source == input && !caption.Rename(caption.Find(null, 2)!, "end" + keyword),
                "caption history and closing delimiter validation " + header);
        }
    }
}
