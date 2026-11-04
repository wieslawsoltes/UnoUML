using System.Xml.Linq;
using UnoUML.Core;

internal static class DotTableRuleCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("DOT table rules: " + message); checks++; }
        static StudioDocument Doc(string rows, string attributes = "", string edges = "") =>
            new("@startdot\ndigraph { a[shape=plain,label=<<TABLE BORDER='0' CELLBORDER='0' " + attributes + ">" + rows + "</TABLE>>]; b; " + edges + " }\n@enddot");
        static DotLabelEdit Label(StudioDocument doc) => doc.OpenDotLabel(DotStructureCases.Open(doc, "node", "a")) ?? throw new Exception("Rule table did not open\n" + doc.Source);
        static int Rules(StudioDocument doc, string kind) => DotStructureCases.Render(doc).Descendants().Count(element => (string?)element.Attribute("data-dot-table-rule") == kind);
        void Edit(StudioDocument doc, Func<DotLabelEdit, bool> action, string expected, string message)
        {
            string before = doc.Source;
            var label = Label(doc);
            Check(action(label), message + " succeeds");
            Check(doc.Source == expected, message + " preserves exact source\nExpected: " + expected + "\nActual: " + doc.Source);
            Check(Label(doc).Tables[0].Cells.Count > 0, message + " remains natively editable");
            Check(!doc.InsertDotTableRow(label, 0, 0), message + " rejects stale spans");
            Check(doc.Undo() && doc.Source == before && !doc.CanUndo, message + " is one exact undo");
            Check(doc.Redo() && doc.Source == expected, message + " is one exact redo");
            Check(doc.Undo() && doc.Source == before, message + " restores fixture");
        }

        const string a = "<TD>A</TD>", b = "<TD>B</TD>", c = "<TD>C</TD>";
        const string vr1 = "<!-- v1 --><vR /><!-- between -->", vr2 = "<VR><!-- inside v2 --></VR><!-- v2 -->";
        const string hr1 = "<!-- h1 -->\r\n<hR /><!-- between rows -->", hr2 = "<HR><!-- inside h2 --></HR><!-- h2 -->";
        string row0 = "<TR>" + a + vr1 + b + vr2 + c + "</TR>";
        string row1 = "<tr><td>D</td><vr/><td>E</td><vr/><td>F</td></tr>";
        string row2 = "<TR><TD>G</TD><TD>H</TD><TD>I</TD></TR>";
        {
            var doc = Doc(row0 + hr1 + row1, "rows='*' columns=\"*\"");
            var table = Label(doc).Tables.Single();
            Check(table is { Rows: 2, Columns: 3, Rectangular: true } && table.Cells.Count == 6, "rules are not projected as cells or rows");
            Check(table.Cells.All(cell => doc.Page.Source.AsSpan(cell.Start, cell.Length).StartsWith("<TD", StringComparison.OrdinalIgnoreCase)), "rules do not shift literal cell spans");
            Check(Rules(doc, "horizontal") == 3 && Rules(doc, "vertical") == 4, "explicit and uniform rules do not double-paint");
            Edit(doc, label => doc.SetDotTableCellText(label, 0, 0, 1, "R&D"), doc.Source.Replace(b, "<TD>R&amp;D</TD>", StringComparison.Ordinal), "cell text beside rules");
            Edit(doc, label => doc.InsertDotTableCell(label, 0, 0, 1, "New", "p"),
                doc.Source.Replace(b, "<TD PORT=\"p\">New</TD>" + b, StringComparison.Ordinal), "cell ordinal skips VR tags");
            Check(!doc.InsertDotTableCell(Label(doc), 0, 0, 4) && !doc.CanUndo, "cell ordinal excludes separator count");
        }
        for (int index = 0; index <= 2; index++)
        {
            var doc = Doc(row0 + hr1 + row1);
            const string added = "<TR><TD>Cell</TD><TD>Cell</TD><TD>Cell</TD></TR>";
            string expected = index == 0 ? doc.Source.Replace(row0, added + row0, StringComparison.Ordinal) :
                index == 1 ? doc.Source.Replace(row1, added + row1, StringComparison.Ordinal) : doc.Source.Replace(row1, row1 + added, StringComparison.Ordinal);
            Edit(doc, label => doc.InsertDotTableRow(label, 0, index), expected, "insert row " + index);
            Check(doc.InsertDotTableRow(Label(doc), 0, index) && doc.RemoveDotTableRow(Label(doc), 0, index) && doc.Source == Doc(row0 + hr1 + row1).Source,
                "row insertion/removal retains separator ownership " + index);
        }
        for (int index = 0; index < 3; index++)
        {
            var doc = Doc(row0 + hr1 + row1 + hr2 + row2);
            string row = new[] { row0, row1, row2 }[index];
            string expected = doc.Source.Replace(row, "", StringComparison.Ordinal);
            expected = index == 0 ? expected.Replace("<hR />", "", StringComparison.Ordinal) :
                expected.Replace("<HR><!-- inside h2 --></HR>", "<!-- inside h2 -->", StringComparison.Ordinal);
            Edit(doc, label => doc.RemoveDotTableRow(label, 0, index), expected, "remove ruled row " + index);
        }
        for (int index = 0; index < 3; index++)
        {
            var doc = Doc(row0);
            string expected = doc.Source.Replace(new[] { a, b, c }[index], "", StringComparison.Ordinal);
            expected = index == 0 ? expected.Replace("<vR />", "", StringComparison.Ordinal) :
                expected.Replace("<VR><!-- inside v2 --></VR>", "<!-- inside v2 -->", StringComparison.Ordinal);
            Edit(doc, label => doc.RemoveDotTableCell(label, 0, 0, index), expected, "remove ruled cell " + index);
        }
        for (int index = 0; index <= 3; index++)
        {
            var doc = Doc(row0 + hr1 + row1, "ROWS='*' COLUMNS='*'");
            string expected = doc.Source;
            if (index < 3)
            {
                expected = expected.Replace(new[] { a, b, c }[index], "<TD>Cell</TD>" + new[] { a, b, c }[index], StringComparison.Ordinal);
                string lower = new[] { "<td>D</td>", "<td>E</td>", "<td>F</td>" }[index];
                expected = expected.Replace(lower, "<TD>Cell</TD>" + lower, StringComparison.Ordinal);
            }
            else expected = expected.Replace("</TR>", "<TD>Cell</TD></TR>", StringComparison.Ordinal).Replace("</tr>", "<TD>Cell</TD></tr>", StringComparison.Ordinal);
            Edit(doc, label => doc.InsertDotTableColumn(label, 0, index), expected, "insert ruled column " + index);
            string before = doc.Source;
            Check(doc.InsertDotTableColumn(Label(doc), 0, index) && doc.RemoveDotTableColumn(Label(doc), 0, index) && doc.Source == before,
                "column insertion/removal preserves uniform and explicit rules " + index);
        }
        for (int index = 0; index < 3; index++)
        {
            var doc = Doc(row0 + hr1 + row2);
            string expected = doc.Source.Replace(new[] { a, b, c }[index], "", StringComparison.Ordinal)
                .Replace(new[] { "<TD>G</TD>", "<TD>H</TD>", "<TD>I</TD>" }[index], "", StringComparison.Ordinal);
            expected = index == 0 ? expected.Replace("<vR />", "", StringComparison.Ordinal) :
                expected.Replace("<VR><!-- inside v2 --></VR>", "<!-- inside v2 -->", StringComparison.Ordinal);
            Edit(doc, label => doc.RemoveDotTableColumn(label, 0, index), expected, "remove ruled column " + index);
        }
        {
            var doc = Doc(row0, "ROWS='*' COLUMNS='*'");
            string expected = doc.Source.Replace(a, "<TD COLSPAN=\"2\">A<BR/>B</TD>", StringComparison.Ordinal)
                .Replace(b, "", StringComparison.Ordinal).Replace("<vR />", "", StringComparison.Ordinal);
            Edit(doc, label => doc.MergeDotTableCells(label, 0, 0, 0, 1, 2), expected, "merge removes internal rule and retains outer rule");
        }
        {
            const string first = "<TD PORT='p' COLSPAN='2'>Wide</TD>";
            var doc = Doc("<TR>" + first + vr1 + c + "</TR>", edges: "a:p:e->b;");
            string expected = doc.Source.Replace(first, "<TD PORT='p' >Wide</TD><TD>Cell</TD>", StringComparison.Ordinal);
            Edit(doc, label => doc.SplitDotTableCell(label, 0, 0, 0), expected, "split retains external rule and live port");
        }
        {
            string rows = "<TR>" + a + "<VR/>" + b + "<!-- outer 0 --><VR/><TD>X</TD></TR><HR/>" +
                "<TR>" + c + "<VR/><TD>D</TD><!-- outer 1 --><VR/><TD>Y</TD></TR>";
            var doc = Doc(rows);
            string expectedRows = "<TR><TD ROWSPAN=\"2\" COLSPAN=\"2\">A<BR/>B<BR/>C<BR/>D</TD><!-- outer 0 --><VR/><TD>X</TD></TR><HR/>" +
                "<TR><!-- outer 1 --><TD>Y</TD></TR>";
            Edit(doc, label => doc.MergeDotTableCells(label, 0, 0, 0, 2, 2), doc.Source.Replace(rows, expectedRows, StringComparison.Ordinal), "merge across ruled rows");
            Check(doc.MergeDotTableCells(Label(doc), 0, 0, 0, 2, 2) && Label(doc).Tables[0].Rectangular && Rules(doc, "horizontal") == 1 && Rules(doc, "vertical") == 1,
                "merged span keeps rules outside merged rectangle");
        }
        {
            const string span = "<TD PORT='p' ROWSPAN='2'>Keep</TD>";
            string first = "<TR>" + span + vr1 + a + "</TR>", next = "<TR>" + b + "</TR>";
            var doc = Doc(first + hr1 + next, edges: "a:p:e->b;");
            string moved = "<TD PORT='p' >Keep</TD>" + vr1;
            string expected = doc.Source.Replace(first, "", StringComparison.Ordinal).Replace("<hR />", "", StringComparison.Ordinal)
                .Replace(next, "<TR>" + moved + b + "</TR>", StringComparison.Ordinal);
            Edit(doc, label => doc.RemoveDotTableRow(label, 0, 0), expected, "rowspan relocation retains vertical rule and trivia");
        }
        {
            var doc = Doc("<TR><TD>Left</TD><VR/><!-- protect --><TD PORT='p'>Live</TD></TR>", edges: "a:p->b;");
            string before = doc.Source;
            var label = Label(doc);
            Check(!doc.RemoveDotTableColumn(label, 0, 1) && doc.Source == before && !doc.CanUndo, "port guard prevents orphan-rule cleanup from mutating history");
            Check(doc.SetDotTableCellPort(label, 0, 0, 1, "next") && doc.Source.Contains("PORT='next'") && doc.Source.Contains("a:next->b") && doc.Source.Contains("<VR/><!-- protect -->"),
                "failed ruled edit leaves snapshot usable for atomic port rename");
        }
        {
            var doc = Doc("<TR><TD><TABLE ROWS='*' COLUMNS='*'><TR>" + a + "<VR/>" + b + "</TR><HR/><TR>" + b + c + "</TR></TABLE></TD><VR/><TD>Outer</TD></TR>");
            var label = Label(doc);
            Check(label.Tables.Count == 2 && doc.InsertDotTableColumn(label, 1, 1), "nested ruled table remains independently editable");
            Check(Label(doc).Tables[0].Columns == 2 && Label(doc).Tables[1].Columns == 3, "nested ruled edits preserve outer dimensions");
        }
        return checks;
    }
}
