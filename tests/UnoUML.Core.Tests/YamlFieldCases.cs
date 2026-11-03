using UnoUML.Core;

internal static class YamlFieldCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("YAML field editing " + message); checks++; }
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        foreach (string indent in new[] { "  ", "\t", " \t" })
        {
            string source = ("\uFEFF@startyaml\nvalue:\n" + indent + "First 🪴 # first comment\n" + indent + "# middle comment\n\n" + indent + "  'second''s'  # next comment\n" + indent + "\"third \\\"quoted\\\"\" # last comment\nnext: retained\n@endyaml").ReplaceLineEndings(newline);
            var document = new StudioDocument(source);
            int start = source.IndexOf("First", StringComparison.Ordinal), end = source.IndexOf(" # last comment", StringComparison.Ordinal);
            var field = document.OpenSourceField(start, end - start, "yaml-folded-text", "value");
            Check(field != null && field.Text == "First 🪴 second's third \"quoted\"" && field.Multiline, "decodes multiline text, quotes and comments");
            Check(!document.ApplySourceField(field!, field!.Text) && document.Source == source, "unchanged multiline text is a no-op");
            Check(document.ApplySourceField(field, "Updated\nline"), "applies multiline text through trim-safe quoting");
            string expected = source.Replace("First 🪴", "\"Updated\\nline\"", StringComparison.Ordinal)
                .Replace("'second''s'", "", StringComparison.Ordinal).Replace("\"third \\\"quoted\\\"\"", "", StringComparison.Ordinal);
            Check(document.Source == expected, "retains every comment, indentation, BOM and physical newline");
            Check(!document.ApplySourceField(field, "stale"), "rejects stale multiline snapshot");
            Check(document.Undo() && document.Source == source && document.Redo() && document.Source == expected, "multiline edit is one reversible transaction");
        }
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        foreach (string header in new[] { "|", "|-", "|+", ">-", ">+", ">2-", ">-2" })
        {
            string source = ("@startyaml\nroot:\n\tbody: " + header + "\n\t\tfirst\n\t\tsecond\n\t\t\tindented\nlast: retained\n@endyaml").ReplaceLineEndings(newline);
            string spaces = source.Replace("\t", "    ", StringComparison.Ordinal);
            var tabbed = new StudioDocument(source);
            var spaced = new StudioDocument(spaces);
            int start = source.IndexOf(header, StringComparison.Ordinal), end = source.IndexOf(newline + "last:", StringComparison.Ordinal);
            int spaceStart = spaces.IndexOf(header, StringComparison.Ordinal), spaceEnd = spaces.IndexOf(newline + "last:", StringComparison.Ordinal);
            var tabField = tabbed.OpenSourceField(start, end - start, "yaml-block", "value");
            var spaceField = spaced.OpenSourceField(spaceStart, spaceEnd - spaceStart, "yaml-block", "value");
            Check(tabField != null && spaceField != null && tabField.Text == spaceField.Text, "tabbed block scalars use four-column indentation " + header);
            Check(tabbed.ApplySourceField(tabField!, "Changed\nnext") && tabbed.Source == source[..start] + "\"Changed\\nnext\"" + source[end..], "tabbed block rewrite retains the following mapping");
            Check(tabbed.Undo() && tabbed.Source == source, "tabbed block undo preserves original physical bytes");
        }
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        foreach (var sample in new[] { (Prefix: "- body: ", Body: "    "), (Prefix: "  - body: ", Body: "      "),
            (Prefix: "\t-   body: ", Body: "\t      "), (Prefix: "- ", Body: "  "), (Prefix: "  body: ", Body: "    ") })
        foreach (string header in new[] { "|2-", ">2-" })
        {
            string source = ("@startyaml\n" + sample.Prefix + header + " # retained\n" + sample.Body + "first\n" + sample.Body + "second\n@endyaml").ReplaceLineEndings(newline);
            var document = new StudioDocument(source);
            int start = source.IndexOf(header, StringComparison.Ordinal), end = source.IndexOf(newline + "@endyaml", StringComparison.Ordinal);
            var field = document.OpenSourceField(start, end - start, "yaml-block", "value");
            string expected = header[0] == '|' ? "first\nsecond" : "first second";
            Check(field?.Text == expected, "explicit block indentation includes a list mapping prefix but excludes a direct list marker");
            Check(!document.ApplySourceField(field!, expected) && document.Source == source, "unchanged sequence block content is a no-op");
            Check(document.ApplySourceField(field!, "Changed\nnext") && document.Source == source[..start] + "\"Changed\\nnext\" # retained" + source[end..],
                "sequence block edit preserves its list structure and header comment");
            Check(document.Undo() && document.Source == source, "sequence block undo retains the original source bytes");
        }
        foreach (string value in new[] { "&name repeated", "*name", "!!str 012", "!<tag:example.org,2026:item> data" })
        {
            string source = "@startyaml\nvalue: " + value + " # retained\n@endyaml";
            var document = new StudioDocument(source);
            var field = document.OpenSourceField(source.IndexOf(value, StringComparison.Ordinal), value.Length, "yaml-string", "value");
            Check(field?.Text == value && document.ApplySourceField(field!, "new: literal"), "opens indicator values as literal fields");
            Check(document.Source == source.Replace(value, "\"new: literal\"", StringComparison.Ordinal) && document.Undo(), "indicator edit preserves scalar structure and comments");
        }
        foreach (string malformed in new[] { "first\n  child: value", "first\n  - item", "first\n  [list]", "\"unterminated", "# no value\n   ", new string('x', 8193) })
        {
            string source = "@startyaml\nvalue:\n  " + malformed + "\n@endyaml";
            var document = new StudioDocument(source);
            int start = source.IndexOf("  ", StringComparison.Ordinal) + 2;
            Check(document.OpenSourceField(start, malformed.Length, "yaml-folded-text", "value") == null && document.Source == source,
                "malformed or oversized folded ranges cannot become editable values");
        }
        const string pages = "@startjson\n{}\n@endjson\n@startyaml later\ntext:\n  first\n  second\n@endyaml";
        var multiple = new StudioDocument(pages) { ActivePage = 1 };
        int local = multiple.Page.Source.IndexOf("first", StringComparison.Ordinal);
        var later = multiple.OpenSourceField(local, "first\n  second".Length, "yaml-folded-text", "value");
        Check(later?.Text == "first second" && multiple.ApplySourceField(later, "Changed"), "folded offsets resolve inside a later named page");
        Check(multiple.Source == pages.Replace("first\n  second", "\"Changed\"\n  ", StringComparison.Ordinal) && multiple.Undo() && multiple.Source == pages,
            "later-page edit and undo preserve prior pages exactly");
        var otherFamily = new StudioDocument("@startjson\n{\"value\":\"text\"}\n@endjson");
        Check(otherFamily.OpenSourceField(otherFamily.Source.IndexOf("text", StringComparison.Ordinal), 4, "yaml-folded-text", "value") == null,
            "YAML field codec does not cross diagram families");
        return checks;
    }
}
