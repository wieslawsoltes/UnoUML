using UnoUML.Core;
using UnoUML.PlantUml;
using System.Xml.Linq;

internal static class SaltEditCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("Salt editing " + message); checks++; }
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        foreach (string marker in new[] { "[]", "[ ]", "[X]", "[x]", "()", "( )", "(X)", "(x)" })
        {
            string source = ("\uFEFF@startsalt\n{+\nLabel | { " + marker + " Same | [X] Same }\n}\n@endsalt").ReplaceLineEndings(newline);
            var doc = new StudioDocument(source);
            int offset = source.IndexOf(marker, StringComparison.Ordinal);
            var field = doc.OpenSourceField(offset, marker.Length, "salt-state", "value")!;
            string next = field.Text == "true" ? "false" : "true";
            Check(!doc.ApplySourceField(field, "invalid") && doc.Source == source, "invalid states are atomic");
            Check(doc.ApplySourceField(field, next), "toggle a nested state");
            string replacement = marker[0] + (next == "true" ? "X" : " ") + marker[^1];
            Check(doc.Source == source[..offset] + replacement + source[(offset + marker.Length)..], "only selected marker changes");
            Check(!doc.ApplySourceField(field, field.Text) && doc.Undo() && doc.Source == source && doc.Redo(), "stale guard and atomic undo redo");
        }
        const string choicesSource = "@startsalt\r\n{\r\n^Plan^^ Basic ^^ Pro \\| Plus ^^ Enterprise^ | ^Other^^First^\r\n}\r\n@endsalt";
        var choices = new StudioDocument(choicesSource);
        var heading = choices.OpenSourceField(choicesSource.IndexOf("Plan", StringComparison.Ordinal), 4, "salt-text")!;
        Check(choices.SaltChoices(heading).SequenceEqual(new[] { "Basic", "Pro | Plus", "Enterprise" }), "read declared options including escaped separators");
        Check(!choices.SelectSaltChoice(heading, -1) && !choices.SelectSaltChoice(heading, 3) && choices.Source == choicesSource, "invalid choice preserves source");
        Check(choices.SelectSaltChoice(heading, 1) && choices.Source == choicesSource.Replace("^Plan^", "^Pro \\| Plus^", StringComparison.Ordinal), "select heading without touching any option");
        Check(choices.SaltChoices(heading).Count == 0 && !choices.SelectSaltChoice(heading, 0), "reject stale dropdown snapshot");
        Check(choices.Undo() && choices.Source == choicesSource, "dropdown undo restores exact source");
        var escaped = new StudioDocument("@startsalt\n{ [A \\{B\\} \\| C] }\n@endsalt");
        int at = escaped.Source.IndexOf("A ", StringComparison.Ordinal);
        var label = escaped.OpenSourceField(at, "A \\{B\\} \\| C".Length, "salt-text")!;
        Check(label.Text == "A {B} | C" && escaped.ApplySourceField(label, "Next {D} | E") && escaped.Source.Contains("[Next \\{D\\} \\| E]", StringComparison.Ordinal), "edit escaped structural characters");
        Check(escaped.Undo(), "escaped label undo");
        var multiline = escaped.OpenSourceField(at, "A \\{B\\} \\| C".Length, "salt-text")!;
        Check(escaped.ApplySourceField(multiline, "First\nSecond") && escaped.Source.Contains("[First\\nSecond]", StringComparison.Ordinal), "encode multiline Salt captions inside one cell");
        var empty = new StudioDocument("@startsalt\n{ [] }\n@endsalt");
        var emptyLabel = empty.OpenSourceField(empty.Source.IndexOf(']') + 1, 0, "salt-text")!;
        Check(empty.ApplySourceField(emptyLabel, "New") && empty.Source.Contains("[] New", StringComparison.Ordinal), "empty checkbox caption retains marker separation");
        var other = new StudioDocument("@startuml\nclass A\n@enduml");
        Check(other.OpenSourceField(10, 2, "salt-state") == null, "reject Salt state outside its dialect");
        var button = StudioCatalog.Tools.Single(tool => tool.Group == "Wireframe" && tool.Name == "Button");
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string compact = ("@startsalt\n{+ { [First] | \\{Text\\} } | [Second] }\n@endsalt").ReplaceLineEndings(newline);
            var compactDoc = new StudioDocument(compact);
            Check(compactDoc.Add(button) && compactDoc.Source.Contains("[Second] " + newline + "  [Button]" + newline + "}", StringComparison.Ordinal), "tool insertion finds the inline outer closing brace");
            Check(compactDoc.Undo() && compactDoc.Source == compact, "inline insertion undoes exactly");
        }
        var optionDoc = new StudioDocument(choicesSource);
        var option = optionDoc.OpenSourceField(choicesSource.IndexOf("Basic", StringComparison.Ordinal), 5, "salt-text")!;
        Check(optionDoc.SaltChoices(option).Count == 0 && !optionDoc.SelectSaltChoice(option, 0), "popup options are not dropdown headings");
        Check(optionDoc.ApplySourceField(option, "*") && Render(optionDoc.Source).Descendants().Any(e => (string?)e.Attribute("class") == "salt-widget salt-option" && e.Value == "*"),
            "a literal wildcard remains an editable popup option");
        var plain = new StudioDocument("@startsalt\n{\nCaption\n}\n@endsalt");
        var plainField = Caption(plain, "Caption");
        foreach (string syntax in new[] { "(X) Checked", "() Radio", "[Button]", "^Choice^", "--", ".", "*", "", "' Comment", "title Changed", "!include secret", "Text /' comment", "<style>" })
            Check(!plain.ApplySourceField(plainField, syntax) && plain.Source.Contains("\nCaption\n", StringComparison.Ordinal), "label edits cannot become syntax: " + syntax);
        Check(plain.ApplySourceField(plainField, "(Project)") && Render(plain.Source).Descendants().Any(e => (string?)e.Attribute("class") == "salt-widget salt-label" && e.Value == "(Project)"),
            "ordinary parenthesized text stays a label");
        var buttonDoc = new StudioDocument("@startsalt\n{ [Apply] }\n@endsalt");
        var buttonLabel = Caption(buttonDoc, "Apply");
        foreach (string marker in new[] { "X", "x", " ", "" })
            Check(!buttonDoc.ApplySourceField(buttonLabel, marker) && buttonDoc.Source.Contains("[Apply]", StringComparison.Ordinal), "button label must not turn into a checkbox marker");
        Check(buttonDoc.ApplySourceField(buttonLabel, "(X) caption") && Render(buttonDoc.Source).Descendants().Any(e => (string?)e.Attribute("class") == "salt-widget salt-button" && e.Value == "(X) caption"),
            "button wrapper makes parenthesized text literal");
        var checkboxDoc = new StudioDocument("@startsalt\n{ [X] Enabled }\n@endsalt");
        Check(checkboxDoc.ApplySourceField(Caption(checkboxDoc, "Enabled"), "(X) with [brackets]") &&
            Render(checkboxDoc.Source).Descendants().Any(e => (string?)e.Attribute("class") == "salt-widget salt-check" && e.Value == "(X) with [brackets]"),
            "stateful control captions admit literal punctuation without changing their state");
        foreach (string title in new[] { "", ".", "*", "(Project)" })
        {
            var titleDoc = new StudioDocument("@startsalt\n{^\"Settings\"\n[OK]\n}\n@endsalt");
            Check(titleDoc.ApplySourceField(Caption(titleDoc, "Settings"), title) &&
                Render(titleDoc.Source).Descendants().Any(e => (string?)e.Attribute("class") == "salt-group-title" && e.Value == title),
                "quoted title accepts literal content: " + title);
            Check(titleDoc.Undo() && titleDoc.Source.Contains("\"Settings\"", StringComparison.Ordinal), "title editing undo");
        }
        var treeDoc = new StudioDocument("@startsalt\n{T\n+Root\n++Leaf\n}\n@endsalt");
        Check(treeDoc.ApplySourceField(Caption(treeDoc, "Leaf"), "+Prefix") && treeDoc.Source.Contains("++ +Prefix", StringComparison.Ordinal),
            "tree caption starting with plus preserves hierarchy depth");
        Check(Render(treeDoc.Source).Descendants().Any(e => (string?)e.Attribute("class") == "salt-widget salt-tree" && e.Value == "+Prefix"), "tree caption plus remains visible");
        var escapeEnd = new StudioDocument("@startsalt\n{Caption}\n@endsalt");
        Check(!escapeEnd.ApplySourceField(Caption(escapeEnd, "Caption"), "Path\\") && escapeEnd.Source.Contains("{Caption}", StringComparison.Ordinal), "incomplete Creole escape is rejected atomically");
        Check(escapeEnd.ApplySourceField(Caption(escapeEnd, "Caption"), "Path\\\\") && escapeEnd.Source.Contains("{Path\\\\ }", StringComparison.Ordinal) &&
            Render(escapeEnd.Source).Descendants().Any(e => e.Name.LocalName == "text" && e.Value == "Path\\"), "trailing escape cannot consume the form's closing brace");
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string menuSource = ("\uFEFF@startsalt\n{+\n{* File | Edit\nFile | New | Open }\n{* File | Help\nFile | Save }\n}\n@endsalt").ReplaceLineEndings(newline);
            var menuDoc = new StudioDocument(menuSource);
            var menuField = menuDoc.OpenSourceField(menuSource.IndexOf("File", StringComparison.Ordinal), 4, "salt-text")!;
            Check(!menuDoc.ApplySourceField(menuField, "Edit") && menuDoc.Source == menuSource, "menu rename cannot create ambiguous popup targets");
            Check(!menuDoc.ApplySourceField(menuField, "*") && !menuDoc.ApplySourceField(menuField, "N=") && menuDoc.Source == menuSource, "menu heading cannot become a form directive");
            Check(menuDoc.ApplySourceField(menuField, "Project"), "rename a menu heading and its popup target");
            string expected = menuSource.Replace("{* File | Edit" + newline + "File | New", "{* Project | Edit" + newline + "Project | New", StringComparison.Ordinal);
            Check(menuDoc.Source == expected && Render(menuDoc.Source).Descendants().Count(e => (string?)e.Attribute("class") == "salt-popup") == 2,
                "menu reference rewrite preserves independent nested scopes and exact line endings");
            Check(menuDoc.Undo() && menuDoc.Source == menuSource && menuDoc.Redo() && menuDoc.Source == expected, "menu rename is one atomic history transaction");
            var menuOption = Caption(menuDoc, "New");
            Check(!menuDoc.ApplySourceField(menuOption, "-") && menuDoc.Source == expected, "menu option label cannot silently become a separator");
        }
        return checks;
    }

    private static SourceFieldEdit Caption(StudioDocument doc, string text)
    {
        var field = Render(doc.Source).Descendants().Single(element => (string?)element.Attribute("data-edit-kind") == "salt-text" && element.Value == text);
        return doc.OpenSourceField((int)field.Attribute("data-edit-start")!, (int)field.Attribute("data-edit-length")!, "salt-text")
            ?? throw new Exception("Salt caption range did not open: " + text);
    }

    private static XElement Render(string source)
    {
        if (!PlantUmlRenderer.TryRender(source, out var diagram) || diagram.Kind != DiagramKind.Salt) throw new Exception("Edited Salt no longer renders natively: " + source);
        return XElement.Parse(diagram.Svg);
    }
}
