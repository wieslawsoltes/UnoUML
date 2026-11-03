using UnoUML.Core;

internal static class SourceFieldCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("Source field editing " + message); checks++; }
        const string source = "@startyaml\r\nname: 'café 東京'  # retained\r\nvalue: 42\r\nempty: \r\nbody: >-\r\n  first line\r\n  second line\r\n\r\n  third\r\nnext: true\r\n@endyaml";
        var doc = new StudioDocument(source);
        int name = source.IndexOf("'café", StringComparison.Ordinal);
        var field = doc.OpenSourceField(name, "'café 東京'".Length, "yaml-quoted", "value")!;
        Check(field.Text == "café 東京" && doc.ApplySourceField(field, "Sam's café"), "decodes and edits quoted YAML");
        Check(doc.Source == source.Replace("'café 東京'", "'Sam''s café'", StringComparison.Ordinal), "preserves comments and CRLF");
        Check(!doc.ApplySourceField(field, "stale") && doc.Undo() && doc.Source == source, "rejects stale token and undoes atomically");
        int empty = source.IndexOf("empty: ", StringComparison.Ordinal) + 7;
        var emptyField = doc.OpenSourceField(empty, 0, "yaml-string", "value")!;
        Check(emptyField.Text == "" && doc.ApplySourceField(emptyField, "hello: world"), "edits an empty YAML value");
        Check(doc.Source == source.Replace("empty: ", "empty: \"hello: world\"", StringComparison.Ordinal) && doc.Undo(), "quotes structural YAML text");
        int block = source.IndexOf(">-", StringComparison.Ordinal), stop = source.IndexOf("\r\nnext:", StringComparison.Ordinal);
        var blockField = doc.OpenSourceField(block, stop - block, "yaml-block", "value")!;
        Check(blockField.Text == "first line second line\nthird" && blockField.Multiline, "decodes folded YAML block");
        Check(doc.ApplySourceField(blockField, "updated\nsecond") && doc.Source == source[..block] + "\"updated\\nsecond\"" + source[stop..], "encodes multiline block edits without changing following fields");
        Check(doc.Undo() && doc.Source == source, "block undo restores original indentation and folding");
        var literal = new StudioDocument("@startyaml\nbody: |+\n  one\n  two\n\nnext: end\n@endyaml");
        int literalAt = literal.Source.IndexOf("|+", StringComparison.Ordinal), literalEnd = literal.Source.IndexOf("\nnext:", StringComparison.Ordinal);
        Check(literal.OpenSourceField(literalAt, literalEnd - literalAt, "yaml-block", "value")!.Text == "one\ntwo\n\n", "preserves keep chomping");
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        foreach (var (suffix, content, expected) in new[]
        {
            ("-", "  first\n\n  second", "first\nsecond"),
            ("-", "  first\n\n    indented\n  last", "first\n\n  indented\nlast"),
            ("+", "  first\n\n", "first\n\n\n"),
            ("", "  first\n\n", "first\n"),
            ("", "  \n  ", ""),
            ("+", "  \n    ", "\n\n")
        })
        {
            string tokenText = ">" + suffix + "\n" + content;
            string input = ("@startyaml\nvalue: " + tokenText + "\nnext: end\n@endyaml").ReplaceLineEndings(newline);
            var folded = new StudioDocument(input);
            int at = input.IndexOf('>'), finish = input.IndexOf(newline + "next:", StringComparison.Ordinal);
            string decoded = folded.OpenSourceField(at, finish - at, "yaml-block", "value")!.Text;
            Check(decoded == expected, $"folded block {suffix} [{content.Replace("\n", "|")}] produced [{decoded.Replace("\n", "|")}] instead of [{expected.Replace("\n", "|")}]");
        }
        var commented = new StudioDocument("@startyaml\nvalue: >-  # retained comment\n  first\n  second\nnext: end\n@endyaml");
        string commentedBefore = commented.Source;
        int commentAt = commentedBefore.IndexOf('>'), commentEnd = commentedBefore.IndexOf("\nnext:", StringComparison.Ordinal);
        var commentedField = commented.OpenSourceField(commentAt, commentEnd - commentAt, "yaml-block", "value")!;
        Check(commented.ApplySourceField(commentedField, "Updated\nvalue") && commented.Source.Contains("value: \"Updated\\nvalue\"  # retained comment\nnext: end", StringComparison.Ordinal), "retains block header comments when quoting an edited scalar");
        Check(commented.Undo() && commented.Source == commentedBefore, "block comment rewrite undoes atomically");
        int number = source.IndexOf("42", StringComparison.Ordinal);
        var numeric = doc.OpenSourceField(number, 2, "number", "value")!;
        Check(!doc.ApplySourceField(numeric, "NaN") && !doc.ApplySourceField(numeric, "12x") && doc.Source == source, "rejects invalid numeric values atomically");
        Check(doc.ApplySourceField(numeric, "81") && doc.Undo(), "edits numeric field");
        Check(doc.OpenSourceField(0, 10, "text") == null && doc.OpenSourceField(-1, 2, "text") == null && doc.OpenSourceField(name, 3, "unknown") == null, "rejects boundaries and unknown token kinds");
        var dot = new StudioDocument("@startdot\ndigraph { a [label=<<TABLE><TR><TD>A &amp; B</TD></TR></TABLE>>]; }\n@enddot");
        int htmlAt = dot.Source.IndexOf("A &amp; B", StringComparison.Ordinal);
        var html = dot.OpenSourceField(htmlAt, "A &amp; B".Length, "dot-html-text")!;
        string dotBefore = dot.Source;
        Check(html.Text == "A & B" && dot.ApplySourceField(html, "<new> & text") && dot.Source == dotBefore.Replace("A &amp; B", "&lt;new&gt; &amp; text", StringComparison.Ordinal), "edits DOT HTML cell entities in place");
        Check(dot.Undo() && dot.Source == dotBefore, "DOT cell undo");
        var record = new StudioDocument("@startdot\ndigraph { a [shape=record,label=\"{<p> left \\| value|right}\"]; }\n@enddot");
        int recordAt = record.Source.IndexOf("left", StringComparison.Ordinal);
        var recordField = record.OpenSourceField(recordAt, "left \\| value".Length, "dot-record-text")!;
        Check(recordField.Text == "left | value" && record.ApplySourceField(recordField, "new {field}"), "edits DOT record escapes");
        Check(record.Source.Contains("<p> new \\{field\\}|right", StringComparison.Ordinal), "preserves record ports and field structure");
        foreach (var (line, offset, expected) in new[]
        {
            ("empty:", 6, "empty: Value"),
            ("empty:  # keep", 8, "empty:  Value # keep"),
            ("-", 1, "- Value"),
            ("-  # keep", 3, "-  Value # keep")
        })
        {
            var emptyDoc = new StudioDocument("@startyaml\n" + line + "\n@endyaml");
            var token = emptyDoc.OpenSourceField("@startyaml\n".Length + offset, 0, "yaml-string", "value");
            Check(token != null && emptyDoc.ApplySourceField(token, "Value") && emptyDoc.Source == "@startyaml\n" + expected + "\n@endyaml", "empty values preserve whitespace and comments");
        }
        var flow = new StudioDocument("@startyaml\n{item: old}\n@endyaml");
        var flowValue = flow.OpenSourceField(flow.Source.IndexOf("old", StringComparison.Ordinal), 3, "yaml-string", "value")!;
        Check(flow.ApplySourceField(flowValue, "a,b") && flow.Source.Contains("item: \"a,b\"", StringComparison.Ordinal), "quotes flow collection delimiters");
        var chart = new StudioDocument("@startchart\nh-axis [Same, Same]\nv-axis 0 --> 100\nbar [20, 20]\n@endchart");
        int categoryAt = chart.Source.LastIndexOf("Same", StringComparison.Ordinal);
        var category = chart.OpenSourceField(categoryAt, 4, "chart-category")!;
        Check(!chart.ApplySourceField(category, "a,b") && chart.ApplySourceField(category, "Later") && chart.Source.Contains("[Same, Later]", StringComparison.Ordinal), "chart caption editing preserves list structure");
        var packet = new StudioDocument("@startpacketdiag\n{\n0-7: Same\n8-15: Same\n}\n@endpacketdiag");
        int packetAt = packet.Source.LastIndexOf("Same", StringComparison.Ordinal);
        var packetField = packet.OpenSourceField(packetAt, 4, "packet-label")!;
        Check(packet.ApplySourceField(packetField, "Flags") && packet.Source.Contains("0-7: Same\n8-15: Flags", StringComparison.Ordinal), "packet label editing preserves bit ranges");
        var timing = new StudioDocument("@startuml\nrobust Bus\n@0\nBus is Idle : retained annotation\n@enduml");
        string timingBefore = timing.Source;
        var state = timing.OpenSourceField(timing.Source.IndexOf("Idle", StringComparison.Ordinal), 4, "timing-state", "value")!;
        Check(timing.ApplySourceField(state, "Working now: ready") && timing.Source == timingBefore.Replace("Idle", "\"Working now: ready\"", StringComparison.Ordinal), "quotes timing states containing spaces or annotation delimiters");
        Check(timing.Undo() && timing.Source == timingBefore, "timing state quote rewrite undoes atomically");
        const string activitySource = "\ufeff@startuml\r\nstart\r\n  #lightblue: First line\r\n    **Second line**  ; <<procedure>> <<important>>\r\nstop\r\n@enduml";
        var activity = new StudioDocument(activitySource);
        int actionAt = activitySource.IndexOf("First line", StringComparison.Ordinal), actionEnd = activitySource.IndexOf("  ;", StringComparison.Ordinal);
        var action = activity.OpenSourceField(actionAt, actionEnd - actionAt, "activity-label")!;
        Check(action.Text == "First line\n**Second line**" && action.Multiline, "decodes physical activity lines without source indentation");
        Check(!activity.ApplySourceField(action, "Escaped;\n:New action") && !activity.ApplySourceField(action, "Text\n@enduml") && activity.Source == activitySource, "rejects activity statement boundaries atomically");
        Check(activity.ApplySourceField(action, "Changed\n**Next**\nThird") && activity.Source == activitySource[..actionAt] + "Changed\r\n    **Next**\r\n    Third" + activitySource[actionEnd..], "preserves action color, shape tags, indentation, BOM and CRLF");
        Check(activity.Undo() && activity.Source == activitySource, "undo restores the complete physical action in one transaction");
        var escapedActivity = new StudioDocument("@startuml\nstart\n:First\\nSecond;\nstop\n@enduml");
        var escapedAction = escapedActivity.OpenSourceField(escapedActivity.Source.IndexOf("First", StringComparison.Ordinal), "First\\nSecond".Length, "activity-label")!;
        Check(escapedAction.Text == "First\nSecond" && escapedActivity.ApplySourceField(escapedAction, "One\nTwo") && escapedActivity.Source.Contains(":One\\nTwo;", StringComparison.Ordinal), "retains escaped-newline action representation");
        return checks + DotEditCases.Run();
    }
}
