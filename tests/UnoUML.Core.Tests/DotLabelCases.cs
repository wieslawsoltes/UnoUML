using System.Xml.Linq;
using UnoUML.Core;

internal static class DotLabelCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("DOT structured labels: " + message); checks++; }
        static StudioDocument Doc(string body) => new("@startdot\ndigraph { " + body + " }\n@enddot");
        static DotObjectEdit Node(StudioDocument doc, string name = "a") => DotStructureCases.Open(doc, "node", name);
        static DotLabelEdit Label(StudioDocument doc, string name = "a") => doc.OpenDotLabel(Node(doc, name)) ?? throw new Exception("Structured DOT label did not open\n" + doc.Source);
        static string Display(StudioDocument doc, string name = "a") => (string)DotStructureCases.Render(doc).Descendants().Single(x =>
            (string?)x.Attribute("data-dot-kind") == "node" && (string?)x.Attribute("data-qualified-name") == name).Attribute("data-dot-label-text")!;
        void Undo(StudioDocument doc, string before, string message)
        {
            string after = doc.Source; doc.Undo(); Check(doc.Source == before, message + " exact undo");
            doc.Redo(); Check(doc.Source == after, message + " exact redo");
        }
        {
            var doc = Doc("a[shape=record,label=\"<p> One|{Two|Three}\"]; b; a:p->b;");
            var label = Label(doc);
            Check(label.RecordFields.Count == 4 && label.RecordFields[1].IsGroup, "nested record field projection");
            Check(label.RecordFields.All(field => field.Start >= 0 && field.Length > 0 && field.Start + field.Length < doc.Page.Source.Length), "record source spans");
            string before = doc.Source;
            Check(doc.InsertDotRecordField(label, "1", 1, "Inserted | {field} <x> \\ slash", "new"), "insert escaped nested record field");
            Check(Label(doc).RecordFields.Single(field => field.Port == "new").Text == "Inserted | {field} <x> \\ slash", "record escapes round trip");
            Check(!doc.InsertDotRecordField(label, "", 0, "stale"), "record stale selection rejected");
            Undo(doc, before, "record insert");
            before = doc.Source;
            Check(doc.MoveDotRecordField(Label(doc), "1.1", 0), "move nested record field");
            Check(Label(doc).RecordFields.Single(field => field.Path == "1.0").Port == "new", "field port follows reorder");
            Undo(doc, before, "record reorder");
            before = doc.Source;
            Check(doc.GroupDotRecordFields(Label(doc), "1", 0, 2), "group adjacent record fields");
            Check(Label(doc).RecordFields.Single(field => field.Path == "1.0").IsGroup, "group projection");
            Check(doc.UngroupDotRecordField(Label(doc), "1.0"), "ungroup record fields");
            Check(doc.Source == before, "group then ungroup preserves raw label text");
            Check(doc.SetDotRecordFieldText(Label(doc), "1.0", "Line 1\nLine 2 & \"quoted\""), "record text authoring");
            Check(Label(doc).RecordFields.Single(field => field.Path == "1.0").Text == "Line 1\nLine 2 & \"quoted\"", "record text quotes and newlines round trip");
            before = doc.Source;
            Check(!doc.RemoveDotRecordField(Label(doc), "0") && doc.Source == before, "referenced record port blocks deletion");
            Check(doc.RemoveDotRecordField(Label(doc), "1.0"), "unreferenced record field deletion");
            Check(!doc.SetDotRecordFieldPort(Label(doc), "1.0", "p"), "duplicate record port rejected");
        }
        {
            var doc = Doc("node[shape=record,label=\"<p> Default|Second\"]; a; b; a:p->b:p;");
            string before = doc.Source, other = Display(doc, "b");
            Check(doc.InsertDotRecordField(Label(doc), "", 2, "Only A"), "default record label gets local override");
            Check(Display(doc, "b") == other && Display(doc).Contains("Only A"), "default label other owner unchanged");
            Undo(doc, before, "default record override");
        }
        {
            var doc = Doc("a,b[shape=record,label=\"<p> Shared|Second\"]; a:p->b:p;");
            string before = doc.Source;
            Check(doc.SetDotRecordFieldPort(Label(doc), "0", "quoted port"), "shared record port renamed locally");
            Check(Label(doc).RecordFields[0].Port == "quoted port" && Label(doc, "b").RecordFields[0].Port == "p", "other shared node retains port");
            Check(doc.Source.Contains("a:\"quoted port\"->b:p"), "source endpoint port renamed with identifier quoting");
            Undo(doc, before, "shared record port");
        }
        {
            var doc = new StudioDocument("@startdot\nstrict digraph { a[shape=record,label=\"<p> Port|Body\"]; b; c; edge[tailport=\"p:e\"]; a:p -> b -> c; a -> b [color=red]; c->a[headport=p]; }\n@enddot");
            string before = doc.Source;
            Check(doc.SetDotRecordFieldPort(Label(doc), "0", "next"), "port rename across strict chain and inherited edge attributes");
            var svg = DotStructureCases.Render(doc);
            Check(svg.Descendants().Count(x => (string?)x.Attribute("data-dot-kind") == "edge") == 3, "port rename preserves logical edges");
            Check(doc.Source.Contains("tailport=\"next:e\"") && doc.Source.Contains("headport=\"next\""), "port attribute overrides follow renamed port");
            Check(doc.Source.Contains("edge[tailport=\"p:e\"]"), "edge defaults remain literal and scoped");
            Undo(doc, before, "strict port rename");
        }
        {
            var doc = Doc("a[shape=record,label=\"Fi\"+/* quoted concatenation */\"rst|Second\"]; a->b;");
            var label = Label(doc); string before = doc.Source;
            Check(label.RecordFields[0].Start == doc.Page.Source.IndexOf("Fi", StringComparison.Ordinal), "concatenated record source start");
            Check(doc.InsertDotRecordField(label, "", 1, "Middle"), "concatenated record restructures");
            Check(doc.Source.Split("/* quoted concatenation */").Length == 2, "concatenation comment retained exactly once");
            Undo(doc, before, "concatenated record");
        }
        {
            var doc = Doc("a[label=Simple]; b; a->b;");
            string before = doc.Source;
            Check(doc.CreateDotRecordLabel(Node(doc), "Root | literal"), "create record label");
            Check(Label(doc).RecordFields.Single().Text == "Root | literal", "record initializer escapes structure");
            Undo(doc, before, "record conversion");
            Check(doc.CreateDotTableLabel(Node(doc), 2, 3), "create table label");
            Check(Label(doc).Tables.Single() is { Rows: 2, Columns: 3, Rectangular: true }, "table initializer dimensions");
            Check(!doc.CreateDotTableLabel(Node(doc), 0, 2), "table initializer validates dimensions");
        }
        {
            var doc = Table("<TR><TD PORT='p' BGCOLOR='pink'>A &amp; B</TD><TD><B>Two</B></TD></TR><!-- keep table -->\n<TR><TD>Three</TD><TD>Four</TD></TR>", "a:p:e->b;");
            var label = Label(doc); string before = doc.Source;
            Check(label.Tables[0].Cells.Count == 4 && label.Tables[0].Cells[0].Text == "A & B", "HTML cells decoded in projection");
            Check(label.Tables[0].Cells.All(cell => doc.Page.Source.AsSpan(cell.Start, cell.Length).StartsWith("<TD")), "HTML cell spans address exact tags");
            Check(doc.SetDotTableCellText(label, 0, 0, 1, "<Escaped> & value\nNext"), "HTML cell text authoring");
            Check(Display(doc).Contains("<Escaped> & value") && doc.Source.Contains("&lt;Escaped&gt; &amp; value<BR/>Next"), "HTML authoring encodes metacharacters");
            Check(!doc.InsertDotTableRow(label, 0, 1), "HTML stale selection rejected");
            Undo(doc, before, "HTML text");
            before = doc.Source;
            Check(doc.SetDotTableCellPort(Label(doc), 0, 0, 0, "in&out"), "HTML port rename");
            Check(doc.Source.Contains("PORT='in&amp;out'") && doc.Source.Contains("a:\"in&out\":e->b"), "HTML port retains quotes and compass");
            Check(doc.Source.Contains("BGCOLOR='pink'") && doc.Source.Contains("<!-- keep table -->"), "HTML unrelated attributes and comment retained");
            Check(!doc.SetDotTableCellPort(Label(doc), 0, 0, 0, ""), "referenced HTML port blocks clear");
            Check(!doc.RemoveDotTableRow(Label(doc), 0, 0), "referenced HTML port blocks row removal");
            Undo(doc, before, "HTML port rename");
        }
        {
            var doc = Table("<TR><TD>A</TD><TD>B</TD></TR><TR><TD>C</TD><TD>D</TD></TR>");
            string before = doc.Source;
            Check(doc.InsertDotTableRow(Label(doc), 0, 1), "insert table row");
            Check(Label(doc).Tables[0] is { Rows: 3, Columns: 2, Rectangular: true }, "row insertion rectangle");
            Check(doc.RemoveDotTableRow(Label(doc), 0, 1) && doc.Source == before, "row insertion/removal exact inverse");
            Check(doc.InsertDotTableColumn(Label(doc), 0, 1), "insert table column");
            Check(Label(doc).Tables[0] is { Rows: 2, Columns: 3, Rectangular: true }, "column insertion rectangle");
            Check(doc.RemoveDotTableColumn(Label(doc), 0, 1) && doc.Source == before, "column insertion/removal exact inverse");
            Check(doc.InsertDotTableCell(Label(doc), 0, 0, 1, "Extra", "e"), "insert individual cell");
            Check(Label(doc).Tables[0] is { Columns: 3, Rectangular: false }, "ragged table authoring supported");
            Check(!doc.InsertDotTableColumn(Label(doc), 0, 1), "grid reshape rejects ambiguous ragged grid");
            Check(doc.RemoveDotTableCell(Label(doc), 0, 0, 1) && doc.Source == before, "individual cell removal");
            Check(doc.MergeDotTableCells(Label(doc), 0, 0, 0, 1, 2), "merge adjacent table cells");
            Check(Label(doc).Tables[0].Cells[0].ColumnSpan == 2 && Display(doc).Contains("A"), "merged cell keeps content");
            Check(doc.SplitDotTableCell(Label(doc), 0, 0, 0), "split spanning cell");
            Check(Label(doc).Tables[0].Cells.Count == 4 && Label(doc).Tables[0].Rectangular, "split restores rectangular unit grid");
        }
        {
            var doc = Table("<TR><TD PORT='p' ROWSPAN='2'>Keep</TD><TD>A</TD></TR><TR><TD>B</TD></TR>", "a:p->b;");
            string before = doc.Source;
            Check(doc.InsertDotTableRow(Label(doc), 0, 1), "insert row across rowspan");
            Check(Label(doc).Tables[0].Cells[0].RowSpan == 3 && Label(doc).Tables[0].Rectangular, "crossing rowspan expands");
            Check(doc.RemoveDotTableRow(Label(doc), 0, 1) && doc.Source == before, "crossing rowspan shrink preserves lexical quotes");
            Check(doc.RemoveDotTableRow(Label(doc), 0, 0), "remove row moves live spanning cell to surviving row");
            Check(Label(doc).Tables[0] is { Rows: 1, Columns: 2, Rectangular: true } && Label(doc).Tables[0].Cells[0].Port == "p", "moved rowspan retains live port");
            Undo(doc, before, "remove originating span row");
        }
        {
            var doc = Table("<TR><TD COLSPAN='2' PORT='p'>Wide</TD><TD>C</TD></TR><TR><TD>D</TD><TD>E</TD><TD>F</TD></TR>", "a:p->b;");
            string before = doc.Source;
            Check(doc.InsertDotTableColumn(Label(doc), 0, 1), "insert inside colspan");
            Check(Label(doc).Tables[0] is { Columns: 4, Rectangular: true } && Label(doc).Tables[0].Cells[0].ColumnSpan == 3, "crossing colspan expands");
            Check(doc.RemoveDotTableColumn(Label(doc), 0, 1) && doc.Source == before, "colspan contraction preserves literal text");
            Check(doc.RemoveDotTableColumn(Label(doc), 0, 0), "remove one column of live spanned cell");
            Check(Label(doc).Tables[0].Cells[0].Port == "p", "column contraction keeps live port");
            Check(!doc.RemoveDotTableColumn(Label(doc), 0, 0), "last column of live port refuses deletion");
        }
        {
            var doc = Table("<TR><TD><TABLE><TR><TD PORT='inner'>Nested</TD><TD>Other</TD></TR></TABLE></TD><TD>Outer</TD></TR>", "a:inner->b;");
            string before = doc.Source;
            Check(Label(doc).Tables.Count == 2, "nested table projection");
            Check(doc.InsertDotTableColumn(Label(doc), 1, 1), "reshape nested table independently");
            Check(Label(doc).Tables[0].Columns == 2 && Label(doc).Tables[1].Columns == 3, "nested reshape leaves outer grid");
            Check(!doc.SetDotTableCellText(Label(doc), 0, 0, 0, "Replace"), "nested live port prevents replacing containing cell");
            Undo(doc, before, "nested table reshape");
        }
        {
            var doc = Table("<TR><TD PORT='p'>A</TD><TD PORT='q'>B</TD><TD>C</TD></TR>", "a:q->b;");
            string before = doc.Source;
            Check(!doc.MergeDotTableCells(Label(doc), 0, 0, 0, 1, 2) && doc.Source == before, "merge refuses removing referenced port");
            Check(!doc.SetDotTableCellPort(Label(doc), 0, 0, 0, "q"), "duplicate table port rejected");
            Check(doc.SetDotTableCellText(Label(doc), 0, 0, 2, "Allowed"), "failed structural transaction leaves snapshot usable");
        }
        {
            var doc = new StudioDocument("@startdot\nstrict digraph { a[shape=record,label=\"<p> Old|<q> Current\"]; b; a:p->b[tailport=\"p:e\"]; a:q->b[tailport=q]; }\n@enddot");
            string before = doc.Source;
            Check(!doc.RemoveDotRecordField(Label(doc), "0") && !doc.CanUndo && doc.Source == before,
                "overridden strict-edge endpoint and attribute references still protect their port");
            Check(doc.SetDotRecordFieldPort(Label(doc), "0", "renamed"), "rename updates earlier strict-edge source references");
            Check(!doc.Source.Contains("a:p", StringComparison.Ordinal) && !doc.Source.Contains("p:e", StringComparison.Ordinal) &&
                doc.Source.Contains("tailport=\"renamed:e\"", StringComparison.Ordinal), "earlier attribute and endpoint references follow port rename");
            Check(Label(doc).RecordFields[1].Port == "q", "current strict-edge port remains unchanged");
            Undo(doc, before, "overridden strict references");
        }
        {
            var doc = Table("<TR><TD><IMG SRC='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII='/></TD><TD>Text</TD></TR>");
            string before = doc.Source;
            Check(!doc.MergeDotTableCells(Label(doc), 0, 0, 0, 1, 2) && !doc.CanUndo && doc.Source == before,
                "image cells cannot be merged into an invalid mixed HTML content production");
            Check(doc.InsertDotTableColumn(Label(doc), 0, 1), "other structure operations retain image markup");
        }
        {
            var doc = new StudioDocument("@startdot\nstrict digraph { a[shape=record,label=\"<p> Node port|Text\"]; a:p->b->c[label=<<TABLE><TR><TD PORT='p'>Shared</TD><TD>Cell</TD></TR></TABLE>>]; a->b[color=red]; }\n@enddot");
            DotLabelEdit EdgeLabel(string name) => doc.OpenDotLabel(DotStructureCases.Open(doc, "edge", name)) ?? throw new Exception("Missing edge table");
            string before = doc.Source;
            var edgeLabel = EdgeLabel("a->b");
            Check(doc.InsertDotTableColumn(edgeLabel, 0, 1), "reshape a shared strict-edge table label");
            Check(EdgeLabel("a->b").Tables[0].Columns == 3 && EdgeLabel("b->c").Tables[0].Columns == 2,
                "edge table restructuring isolates the selected logical edge");
            Check(!doc.InsertDotTableColumn(edgeLabel, 0, 0), "edge table snapshots guard stale source");
            Check(doc.SetDotTableCellPort(EdgeLabel("a->b"), 0, 0, 0, "edgePort") && Label(doc).RecordFields[0].Port == "p" &&
                doc.Source.Contains("a:p", StringComparison.Ordinal), "edge label ports do not rewrite node endpoint ports");
            Check(doc.Undo() && doc.Undo() && doc.Source == before, "edge table transactions undo independently");
            var plain = Doc("a->b->c[label=Shared];");
            Check(plain.CreateDotTableLabel(DotStructureCases.Open(plain, "edge", "a->b")), "create a table label on one shared edge");
            Check(plain.OpenDotLabel(DotStructureCases.Open(plain, "edge", "b->c")) == null, "creating edge table preserves other plain labels");
        }
        {
            var doc = Doc("a[shape=record,label=\"\"];");
            var field = Label(doc).RecordFields.Single();
            Check(field.Length == 0 && field.Start == doc.Page.Source.IndexOf("\"\"", StringComparison.Ordinal) + 1,
                "empty record field span points inside its quoted token");
            Check(doc.SetDotRecordFieldText(Label(doc), field.Path, "First"), "empty record field can be authored");
        }
        checks += CanvasMetadata();
        Console.WriteLine($"{checks} DOT structured-label checks passed.");
        return checks;
    }

    private static StudioDocument Table(string rows, string edges = "") => new("@startdot\ndigraph { a[shape=plain,label=<<TABLE BORDER='0' CELLBORDER='1'>" + rows + "</TABLE>>]; b; " + edges + " }\n@enddot");

    private static int CanvasMetadata()
    {
        var doc = new StudioDocument("@startdot\ndigraph { a; subgraph cluster_outer { subgraph ranks { subgraph cluster_inner { a;b; } } c; } d; a->d; }\n@enddot");
        var elements = DotStructureCases.Render(doc).Descendants().Where(x => x.Attribute("data-dot-kind") != null).ToArray();
        void Check(bool condition, string message) { if (!condition) throw new Exception("DOT canvas metadata: " + message); }
        Check(elements.Where(x => (string?)x.Attribute("data-dot-kind") != "graph").All(x => (string?)x.Attribute("data-canvas-id") == (string?)x.Attribute("data-dot-id")), "stable canvas identity");
        string? Parent(string id) => (string?)elements.Single(x => (string?)x.Attribute("data-dot-id") == id).Attribute("data-canvas-parent");
        Check(Parent("node:a") == "cluster:cluster_inner" && Parent("node:b") == "cluster:cluster_inner", "late membership uses deepest cluster");
        Check(Parent("node:c") == "cluster:cluster_outer" && Parent("node:d") == null, "direct node parent");
        Check(Parent("cluster:cluster_inner") == "cluster:cluster_outer" && Parent("cluster:cluster_outer") == null, "cluster parent skips noncluster scopes");
        return 4;
    }
}
