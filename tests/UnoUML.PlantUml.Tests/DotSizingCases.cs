using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class DotSizingCases
{
    private const string Zero = "BORDER='0' CELLBORDER='0' CELLSPACING='0' CELLPADDING='0'";
    private static int assertions;

    internal static int Run()
    {
        assertions = 0;
        FixedTableBounds();
        MinimumAndAllocatedSpace();
        FixedCellsAndSpans();
        NestedTableAlignment();
        EmptyCellsAndBorders();
        TextOverflowAndSourceMaps();
        Ports();
        Images();
        InvisibleCells();
        NodeSizeModes();
        BoundariesAndDeterminism();
        return assertions;
    }

    private static void FixedTableBounds()
    {
        foreach (string size in new[] { "WIDTH='80' HEIGHT='40'", "width='80' height='40'" })
        {
            var fixedTable = RenderLabel($"<TABLE {Zero} FIXEDSIZE='TRUE' {size} BGCOLOR='#010101'><TR><TD WIDTH='140' HEIGHT='60' BGCOLOR='#020202'></TD></TR></TABLE>");
            var frame = Fill(fixedTable, "#010101"); var cell = Fill(fixedTable, "#020202");
            Require(frame.Width == 80 && frame.Height == 40, "fixed table keeps both declared dimensions");
            Require(cell.Width == 140 && cell.Height == 60 && cell.X == frame.X && cell.Y == frame.Y,
                "an undersized fixed table exposes its natural cell grid from the top left");
            Require(View(fixedTable).Contains(cell) && View(fixedTable).Contains(frame), "export bounds contain overflowing cells and the fixed frame");
            Require(!fixedTable.Descendants().Any(e => e.Name.LocalName == "clipPath" || e.Attribute("clip-path") != null), "table overflow is not clipped");
        }
        var natural = RenderLabel($"<TABLE {Zero} WIDTH='80' HEIGHT='40' BGCOLOR='#010101'><TR><TD WIDTH='140' HEIGHT='60' BGCOLOR='#020202'></TD></TR></TABLE>");
        Require(Fill(natural, "#010101").Width == 140 && Fill(natural, "#010101").Height == 60, "nonfixed minima expand to contain the grid");
        foreach (string dimensions in new[] { "WIDTH='80'", "HEIGHT='40'", "WIDTH='0' HEIGHT='40'", "WIDTH='80' HEIGHT='0'", "" })
        {
            string contents = $"{Zero} {dimensions} BGCOLOR='#010101'><TR><TD WIDTH='140' HEIGHT='60' BGCOLOR='#020202'></TD></TR></TABLE>";
            var incomplete = RenderLabel("<TABLE FIXEDSIZE='TRUE' " + contents);
            var minimum = RenderLabel("<TABLE " + contents);
            Require(Fill(incomplete, "#010101").Size == Fill(minimum, "#010101").Size, "an incomplete fixed table request retains natural sizing: " + dimensions);
        }
    }

    private static void MinimumAndAllocatedSpace()
    {
        foreach (string fixedSize in new[] { "", "FIXEDSIZE='TRUE'" })
        {
            var root = RenderLabel($"<TABLE {Zero} {fixedSize} WIDTH='200' HEIGHT='100' BGCOLOR='#010101'><TR><TD WIDTH='40' HEIGHT='20' BGCOLOR='#020202'></TD></TR></TABLE>");
            Require(Fill(root, "#010101").Size == (200, 100), "table minimum reserves the requested frame");
            Require(Fill(root, "#020202").Size == (40, 20), "table minima do not distribute trailing space into cells");
        }
        var nested = RenderLabel($"<TABLE {Zero}><TR><TD WIDTH='200' HEIGHT='100' BGCOLOR='#010101'><TABLE {Zero} WIDTH='80' HEIGHT='40' BGCOLOR='#020202'><TR><TD WIDTH='40' HEIGHT='20' BGCOLOR='#030303'></TD></TR></TABLE></TD></TR></TABLE>");
        Require(Fill(nested, "#020202").Size == (200, 100), "nonfixed nested table fills the allocated cell interior");
        Require(Fill(nested, "#030303").Size == (160, 80), "only allocation beyond the table minimum expands its grid");
        var smallParent = RenderLabel($"<TABLE {Zero}><TR><TD FIXEDSIZE='TRUE' WIDTH='20' HEIGHT='10' BGCOLOR='#010101'><TABLE {Zero} WIDTH='80' HEIGHT='40' BGCOLOR='#020202'><TR><TD WIDTH='40' HEIGHT='20' BGCOLOR='#030303'></TD></TR></TABLE></TD></TR></TABLE>");
        Require(Fill(smallParent, "#020202").Size == (20, 10) && Fill(smallParent, "#030303").Size == (40, 20),
            "an undersized parent constrains the nested frame without crushing its cell grid");
        Require(View(smallParent).Contains(Fill(smallParent, "#030303")), "nested grid overflow remains exportable");
    }

    private static void FixedCellsAndSpans()
    {
        var single = RenderLabel($"<TABLE {Zero}><TR><TD FIXEDSIZE='TRUE' WIDTH='20' HEIGHT='10' BGCOLOR='#010101'>A long label</TD></TR></TABLE>");
        Require(Fill(single, "#010101").Size == (20, 10), "fixed cell contributes its declared dimensions, not its overflowing text");
        var shared = RenderLabel($"<TABLE {Zero}><TR><TD FIXEDSIZE='TRUE' WIDTH='20' HEIGHT='10' BGCOLOR='#010101'>A long label</TD><TD WIDTH='50' HEIGHT='100' BGCOLOR='#020202'></TD></TR><TR><TD WIDTH='150' HEIGHT='20' BGCOLOR='#030303'></TD><TD></TD></TR></TABLE>");
        Require(Fill(shared, "#010101").Size == (150, 100), "shared rows and columns can enlarge a fixed cell's slot");
        Require(Fill(shared, "#020202").Size == (50, 100) && Fill(shared, "#030303").Size == (150, 20), "enlarged fixed cells preserve the rest of their grid");
        var spans = RenderLabel("<TABLE BORDER='0' CELLBORDER='0' CELLSPACING='4' CELLPADDING='0' FIXEDSIZE='TRUE' WIDTH='40' HEIGHT='20' BGCOLOR='#010101'>" +
            "<TR><TD ROWSPAN='2' FIXEDSIZE='TRUE' WIDTH='30' HEIGHT='80' BGCOLOR='#020202'></TD><TD WIDTH='50' HEIGHT='20' BGCOLOR='#030303'></TD></TR>" +
            "<TR><TD WIDTH='70' HEIGHT='40' BGCOLOR='#040404'></TD></TR></TABLE>");
        var frame = Fill(spans, "#010101"); var tall = Fill(spans, "#020202"); var upper = Fill(spans, "#030303"); var lower = Fill(spans, "#040404");
        Require(frame.Size == (40, 20) && tall.Size == (30, 80), "rowspan and fixed frame retain independent dimensions");
        Require(upper.Width == 70 && lower.Width == 70 && upper.Height + lower.Height + 4 == tall.Height,
            "spanning minimum distributes only its deficit across rows");
        Require(upper.X == tall.Right + 4 && lower.Y == upper.Bottom + 4, "spans preserve spacing and grid origins while overflowing");
        Require(View(spans).Contains(lower), "spanning overflow contributes to the final viewport");
        foreach (string incomplete in new[] { "WIDTH='20'", "HEIGHT='10'", "WIDTH='0' HEIGHT='10'" })
        {
            string cell = $"{Zero}><TR><TD {incomplete} BGCOLOR='#010101' {{0}}>A long label</TD></TR></TABLE>";
            var fixedMissing = RenderLabel("<TABLE " + cell.Replace("{0}", "FIXEDSIZE='TRUE'", StringComparison.Ordinal));
            var natural = RenderLabel("<TABLE " + cell.Replace("{0}", "", StringComparison.Ordinal));
            Require(Fill(fixedMissing, "#010101").Size == Fill(natural, "#010101").Size, "incomplete fixed cells retain natural sizing: " + incomplete);
        }
    }

    private static void NestedTableAlignment()
    {
        foreach (var (align, dx) in new[] { ("LEFT", 0), ("CENTER", 60), ("RIGHT", 120) })
            foreach (var (valign, dy) in new[] { ("TOP", 0), ("MIDDLE", 30), ("BOTTOM", 60) })
            {
                var svg = RenderLabel($"<TABLE {Zero}><TR><TD WIDTH='200' HEIGHT='100' ALIGN='RIGHT' VALIGN='BOTTOM' BGCOLOR='#010101'><TABLE {Zero} ALIGN='{align}' VALIGN='{valign}' FIXEDSIZE='TRUE' WIDTH='80' HEIGHT='40' BGCOLOR='#020202'><TR><TD WIDTH='20' HEIGHT='10' BGCOLOR='#030303'></TD></TR></TABLE></TD></TR></TABLE>");
                var outer = Fill(svg, "#010101"); var inner = Fill(svg, "#020202"); var cell = Fill(svg, "#030303");
                Require(inner.Size == (80, 40), "nested fixed table retains dimensions with " + align + "/" + valign);
                Require(inner.X - outer.X == dx && inner.Y - outer.Y == dy, "table owns its alignment independently of parent TD: " + align + "/" + valign);
                Require(cell.X == inner.X && cell.Y == inner.Y && cell.Size == (20, 10), "alignment translates the nested grid without stretching it");
            }
        var inset = RenderLabel("<TABLE BORDER='0' CELLSPACING='0'><TR><TD WIDTH='200' HEIGHT='100' CELLPADDING='3' BORDER='2' BGCOLOR='#010101'>" +
            $"<TABLE {Zero} FIXEDSIZE='TRUE' ALIGN='RIGHT' VALIGN='BOTTOM' WIDTH='80' HEIGHT='40' BGCOLOR='#020202'><TR><TD WIDTH='20' HEIGHT='10'></TD></TR></TABLE></TD></TR></TABLE>");
        var parent = Fill(inset, "#010101"); var child = Fill(inset, "#020202");
        Require(parent.Right - child.Right == 5 && parent.Bottom - child.Bottom == 5, "nested table alignment includes cell border and padding");
    }

    private static void EmptyCellsAndBorders()
    {
        var empty = RenderLabel("<TABLE BORDER='0' CELLBORDER='5' CELLSPACING='0' CELLPADDING='2' BGCOLOR='#010101'><TR><TD BGCOLOR='#020202'></TD></TR></TABLE>");
        Require(Fill(empty, "#010101").Size == (14, 14) && Fill(empty, "#020202").Size == (14, 14), "empty cells measure border and padding without a phantom text row");
        Require(!empty.Descendants().Any(e => e.Name.LocalName == "text"), "empty cells emit no text glyphs");
        var rectangle = Fill(empty, "#020202");
        var border = Node(empty, "a").Descendants().Single(e => e.Name.LocalName == "path" && (string?)e.Attribute("stroke-width") == "5");
        var values = PathNumbers(border);
        Require(values[0] == rectangle.X + 2.5 && values[1] == rectangle.Y + 2.5 && values[2] == 9,
            "thick cell borders are inset into their allocated rectangle");
        var blankLine = RenderLabel($"<TABLE {Zero}><TR><TD BGCOLOR='#010101'><BR/></TD></TR></TABLE>");
        Require(Fill(blankLine, "#010101").Height > 0, "an explicit blank BR retains a text line");
        var spaced = RenderLabel($"<TABLE {Zero}><TR><TD BGCOLOR='#010101'> </TD></TR></TABLE>");
        Require(Fill(spaced, "#010101").Width > 0 && Fill(spaced, "#010101").Height > 0, "ordinary spaces remain real HTML label content");
        var nestedEmpty = RenderLabel($"<TABLE {Zero}><TR><TD><TABLE {Zero}><TR><TD></TD></TR></TABLE></TD></TR></TABLE>");
        Require(double.IsFinite(View(nestedEmpty).Width) && View(nestedEmpty).Width > 0, "zero-size nested content still produces a finite SVG viewport");
    }

    private static void TextOverflowAndSourceMaps()
    {
        const string label = "<TABLE BORDER='0' CELLSPACING='0' CELLBORDER='0' CELLPADDING='0'><TR><TD FIXEDSIZE='TRUE' WIDTH='20' HEIGHT='10' BGCOLOR='#010101'>First long line<BR/>R&amp;D</TD></TR></TABLE>";
        string source = "@startdot\r\ndigraph { a[shape=plain,label=<" + label + ">]; }\r\n@enddot";
        var svg = RenderSource(source); var cell = Fill(svg, "#010101");
        var first = svg.Descendants().Single(e => e.Name.LocalName == "text" && e.Value == "First long line");
        var second = svg.Descendants().Single(e => e.Name.LocalName == "text" && e.Value == "R&D");
        Require(Number(first, "x") < cell.X && Number(first, "y") > cell.Y, "oversized text remains centered horizontally and starts in the content area");
        Require(Number(second, "y") > cell.Bottom && View(svg).Bottom > Number(second, "y"), "multiline overflow is retained below the fixed cell and inside the SVG");
        Require(svg.Descendants().Any(e => (string?)e.Attribute("data-edit-kind") == "dot-html-text" &&
            source.Substring((int)e.Attribute("data-edit-start")!, (int)e.Attribute("data-edit-length")!) == "R&amp;D"), "fixed labels retain exact entity and CRLF source edits");
        foreach (string align in new[] { "LEFT", "CENTER", "RIGHT", "TEXT" })
        {
            var aligned = RenderLabel(label.Replace("BGCOLOR='#010101'", $"BGCOLOR='#010101' ALIGN='{align}' VALIGN='BOTTOM'", StringComparison.Ordinal));
            var text = aligned.Descendants().First(e => e.Name.LocalName == "text");
            Require(Number(text, "y") > Fill(aligned, "#010101").Y, "overflow is not moved above the fixed content area: " + align);
        }
    }

    private static void Ports()
    {
        string html = $"<TABLE {Zero} FIXEDSIZE='TRUE' WIDTH='80' HEIGHT='40' PORT='table' BGCOLOR='#010101'><TR><TD WIDTH='140' HEIGHT='60' PORT='cell' BGCOLOR='#020202'></TD></TR></TABLE>";
        var svg = RenderDot($"digraph {{ splines=polyline; a[shape=plain,label=<{html}>]; b[shape=box,width=.5,height=.5,fixedsize=true,label='']; a:table:e->b:w[dir=none]; a:cell:e->b:s[dir=none]; }}".Replace("label=''", "label=\"\"", StringComparison.Ordinal));
        var frame = Fill(svg, "#010101"); var cell = Fill(svg, "#020202");
        var tablePath = EdgePath(svg, "edge:0"); var cellPath = EdgePath(svg, "edge:1");
        Require(tablePath[0] == frame.Right && tablePath[1] == frame.Y + frame.Height / 2, "table ports use the fixed frame");
        Require(cellPath[0] == cell.Right && cellPath[1] == cell.Y + cell.Height / 2, "cell ports use the overflowing cell rectangle");
        var nested = RenderDot($"digraph {{ a[shape=plain,label=<<TABLE {Zero}><TR><TD WIDTH='200' HEIGHT='100'><TABLE {Zero} FIXEDSIZE='TRUE' WIDTH='80' HEIGHT='40' ALIGN='RIGHT' VALIGN='BOTTOM' PORT='inner' BGCOLOR='#010101'><TR><TD></TD></TR></TABLE></TD></TR></TABLE>>]; b; a:inner:e->b[dir=none]; }}");
        var inner = Fill(nested, "#010101"); var route = EdgePath(nested, "edge:0");
        Require(route[0] == inner.Right && route[1] == inner.Y + inner.Height / 2, "nested table ports follow their final aligned rectangle");
        var edgeLabel = RenderDot($"digraph {{a->b[label=<{html}>]}}");
        Require(Fill(edgeLabel, "#010101").Size == (80, 40) && Fill(edgeLabel, "#020202").Size == (140, 60), "fixed table dimensions also apply to edge labels");
        Require(edgeLabel.Descendants().Any(e => (string?)e.Attribute("data-canvas-label-role") == "center" && e.Descendants().Any(child => (string?)child.Attribute("fill") == "#010101")),
            "the complete fixed table remains in its canvas edge-caption group");
    }

    private static void Images()
    {
        const string artwork = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\"><rect width=\"40\" height=\"20\" fill=\"red\"/></svg>";
        var options = new PlantUmlRenderOptions { ResolveInclude = path => path == "icon.svg" ? artwork : null };
        foreach (var (scale, sx, sy) in new[] { ("FALSE", .4, .4), ("TRUE", .4, .4), ("BOTH", .4, .4), ("WIDTH", .4, .4), ("HEIGHT", .4, .4) })
        {
            string source = $"@startdot\ndigraph{{a[shape=plain,label=<<TABLE {Zero}><TR><TD FIXEDSIZE='TRUE' WIDTH='20' HEIGHT='12' CELLPADDING='1' BORDER='1' BGCOLOR='#010101'><IMG SRC='icon.svg' SCALE='{scale}'/></TD></TR></TABLE>>]}}\n@enddot";
            var svg = RenderSource(source, options); var cell = Fill(svg, "#010101");
            var image = Node(svg, "a").Descendants().Single(e => ((string?)e.Attribute("transform"))?.Contains(" scale(", StringComparison.Ordinal) == true);
            double[] transform = Numbers((string)image.Attribute("transform")!);
            Require(transform[2] == sx && transform[3] == sy, "fixed-cell image fits its border/padding-adjusted interior: " + scale);
            Require(transform[0] == cell.X + 2 && transform[1] == cell.Y + 2, "fixed-cell image starts inside its border and padding: " + scale);
        }
    }

    private static void InvisibleCells()
    {
        var svg = RenderLabel($"<TABLE {Zero}><TR><TD WIDTH='100' HEIGHT='40' STYLE='INVIS' BGCOLOR='#010101'>Hidden</TD><TD WIDTH='20' BGCOLOR='#020202'>Shown</TD></TR></TABLE>");
        Require(!svg.Descendants().Any(e => (string?)e.Attribute("fill") == "#010101" || e.Name.LocalName == "text" && e.Value == "Hidden"), "invisible cells suppress background, border and content");
        Require(Fill(svg, "#020202").Height == 40 && svg.Value.Contains("Shown", StringComparison.Ordinal), "invisible cells retain their row/column allocation");
        var nested = RenderLabel($"<TABLE {Zero}><TR><TD WIDTH='100' HEIGHT='40'><TABLE {Zero} STYLE='rounded, invis' BGCOLOR='#010101'><TR><TD>Hidden</TD></TR></TABLE></TD></TR></TABLE>");
        Require(!nested.Descendants().Any(e => e.Name.LocalName == "text"), "invisible nested tables suppress their descendants");
    }

    private static void NodeSizeModes()
    {
        string Diagram(string mode) => $"digraph {{ node[shape=box,width=1,height=.5,fixedsize={mode},label=\"A deliberately wide label that overflows\"]; {{ rank=same; a; b; }} }}";
        var fixedNode = RenderDot(Diagram("true")); var fixedShape = RenderDot(Diagram("shape"));
        Box Shape(XElement svg, string id) => Rect(Node(svg, id).Descendants().First(e => e.Name.LocalName == "rect"));
        var trueA = Shape(fixedNode, "a"); var trueB = Shape(fixedNode, "b"); var shapeA = Shape(fixedShape, "a"); var shapeB = Shape(fixedShape, "b");
        Require(trueA.Size == (72, 36) && shapeA.Size == (72, 36), "both fixed node modes preserve the declared shape dimensions");
        Require(shapeB.X - shapeA.X > trueB.X - trueA.X + 100, "fixedsize=shape reserves label width while fixedsize=true packs by shape width");
        Require(View(fixedNode).X < trueA.X && View(fixedNode).Right > trueB.Right, "fixed node text overflow is included in exported bounds");
    }

    private static void BoundariesAndDeterminism()
    {
        foreach (string attributes in new[] { "WIDTH='-1'", "HEIGHT='NaN'", "WIDTH='Infinity'", "WIDTH='999999'", "ALIGN='TEXT'", "VALIGN='LEFT'", "STYLE='unsupported'" })
            Require(!PlantUmlRenderer.TryRender($"@startdot\ndigraph {{a[label=<<TABLE {attributes}><TR><TD>x</TD></TR></TABLE>>]}}\n@enddot", out _), "invalid table geometry uses reference fallback: " + attributes);
        string source = $"@startdot\ndigraph {{a[shape=plain,label=<<TABLE {Zero} FIXEDSIZE='TRUE' WIDTH='80' HEIGHT='40'><TR><TD FIXEDSIZE='TRUE' WIDTH='20' HEIGHT='10'>Long label</TD></TR></TABLE>>]}}\n@enddot";
        string original = RenderSource(source).ToString(SaveOptions.DisableFormatting);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            Require(RenderSource(source).ToString(SaveOptions.DisableFormatting) == original, "fixed table output remains deterministic across cultures");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private readonly record struct Box(double X, double Y, double Width, double Height)
    {
        internal double Right => X + Width;
        internal double Bottom => Y + Height;
        internal (double Width, double Height) Size => (Width, Height);
        internal bool Contains(Box other) => X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;
    }
    private static Box Fill(XElement svg, string color) => Rect(svg.Descendants().Single(e => e.Name.LocalName == "rect" && (string?)e.Attribute("fill") == color));
    private static Box Rect(XElement element) => new(Number(element, "x"), Number(element, "y"), Number(element, "width"), Number(element, "height"));
    private static Box View(XElement svg) { var values = Numbers((string)svg.Attribute("viewBox")!); return new(values[0], values[1], values[2], values[3]); }
    private static double Number(XElement element, string name) => double.Parse((string)element.Attribute(name)!, CultureInfo.InvariantCulture);
    private static double[] Numbers(string value) => Regex.Matches(value, @"[-+]?(?:\d*\.)?\d+(?:[eE][-+]?\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
    private static double[] PathNumbers(XElement path) => Numbers((string)path.Attribute("d")!);
    private static double[] EdgePath(XElement svg, string id)
    {
        var path = svg.Descendants().Single(e => (string?)e.Attribute("data-dot-id") == id).Elements().First(e => e.Name.LocalName is "path" or "polyline");
        return Numbers((string)(path.Attribute("d") ?? path.Attribute("points"))!);
    }
    private static XElement Node(XElement svg, string id) => svg.Descendants().Single(e => (string?)e.Attribute("data-dot-id") == "node:" + id);
    private static XElement RenderLabel(string html) => RenderDot("digraph { a[shape=plain,label=<" + html + ">]; }");
    private static XElement RenderDot(string body) => RenderSource("@startdot\n" + body + "\n@enddot");
    private static XElement RenderSource(string source, PlantUmlRenderOptions? options = null)
    {
        Require(PlantUmlRenderer.TryRender(source, out var diagram, options ?? new()) && diagram.Kind == DiagramKind.Dot, "native DOT sizing expected: " + source);
        return XElement.Parse(diagram!.Svg);
    }
    private static void Require(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException("DOT sizing: " + label);
    }
}
