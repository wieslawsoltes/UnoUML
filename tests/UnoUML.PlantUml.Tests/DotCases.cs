using System.Globalization;
using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class DotCases
{
    internal static void Run()
    {
        Grammar();
        Defaults();
        Clusters();
        Ranks();
        OutputDimensions();
        LabelsAndPorts();
        Attributes();
        Images();
        SourceSpans();
        Boundaries();
    }

    private static void Grammar()
    {
        foreach (string body in new[]
        {
            "digraph {}", "graph G { a -- b -- c }", "STRICT DIGRAPH Test { a -> b; a -> b [label=merged] }",
            "digraph { node [shape=box][color=red]; a,b -> c,d [label=flow][style=dashed] }",
            "digraph { /* many\n lines */ # 12 \"example.dot\"\n a -> b // comment\n b -> c }",
            "digraph { -.5 -> 1.2; \"space name\" -> 日本語; \"node\" -> \"graph\" }",
            "digraph { a[label=\"one\" + \"two\"]; b[label=\"joined\\\nline\"]; a->b }",
            "digraph { subgraph one { a; b } subgraph one { c } subgraph one { } -> d }"
        }) Render(body);
        var cross = Render("digraph { {a;b} -> {c;d} -> e }");
        Require(Edges(cross).Count() == 6, "subgraph edge products and chain");
        var empty = Render("digraph { node[color=blue]; a -> {} -> b [label=\"Edge\",color=red]; }");
        Require(!Edges(empty).Any() && Paint(Node(empty, "a"), "stroke") == "#0000ff" && Paint(Node(empty, "b"), "stroke") == "#0000ff",
            "empty endpoint products retain nodes without applying edge attributes to them");
        Require(!Node(empty, "b").Value.Contains("Edge", StringComparison.Ordinal) && (string?)Node(empty, "b").Attribute("data-dot-explicit") == "false",
            "empty edge statements retain implicit source provenance");
        var emptyTail = Render("digraph { a,b -> {} [label=Empty]; c -> d; }");
        Require(Edges(emptyTail).Count() == 1 && !Node(emptyTail, "a").Value.Contains("Empty", StringComparison.Ordinal), "empty tail sets preserve independent edges");
        var strict = Render("strict graph { a -- b [color=red]; b -- a [label=merged] }");
        Require(Edges(strict).Count() == 1 && strict.Value.Contains("merged"), "strict undirected edge identity");
        Require((string?)Edges(strict).Single().Attribute("data-dot-merged") == "true", "reversed undirected strict declarations retain merged source provenance");
        string mergedSource = "@startdot\nstrict digraph { a->b[label=first]; a->b[label=updated]; b->c; }\n@enddot";
        var mergedEdges = Edges(RenderSource(mergedSource)).ToArray();
        Require((string?)mergedEdges[0].Attribute("data-dot-merged") == "true" && Slice(mergedSource, mergedEdges[0], "data-dot-label") == "updated", "merged directed edges preserve exact label editing metadata");
        var parallel = Edges(Render("digraph { a->b; a->b; }")).ToArray();
        Require(mergedEdges[1].Attribute("data-dot-merged") == null && parallel.Length == 2 && parallel.All(edge => edge.Attribute("data-dot-merged") == null), "single and non-strict edges retain ordinary editing provenance");
        Require(Node(Render("digraph { a [label=\"literal **bold** //text//\"] }"), "a").Descendants().Any(element => element.Name.LocalName == "text" && element.Value == "literal **bold** //text//"), "DOT text remains literal");
    }

    private static void Defaults()
    {
        var svg = Render("digraph { node [shape=box style=filled fillcolor=red]; a; node [fillcolor=blue]; b; subgraph { node [fillcolor=green]; c; a [label=updated]; } d; edge [color=purple]; a->b; edge [color=orange]; b->c; }");
        Require(Paint(Node(svg, "a"), "fill") == "#ff0000", "node defaults apply only at creation");
        Require(Paint(Node(svg, "b"), "fill") == "#0000ff" && Paint(Node(svg, "d"), "fill") == "#0000ff", "nested defaults restore parent scope");
        Require(Paint(Node(svg, "c"), "fill") == "#00ff00", "X11 color palette and nested defaults");
        Require(Node(svg, "a").Value.Contains("updated"), "later explicit attributes update existing node");
        Require(Edges(svg).First().Descendants().Any(element => (string?)element.Attribute("stroke") == "#a020f0"), "edge defaults use X11 palette");
    }

    private static void Clusters()
    {
        var svg = Render("digraph { subgraph cluster_a { label=Outer; color=blue; a; subgraph cluster_b { label=Inner; b; c } } d; a->b; c->d }");
        Require(svg.Descendants().Count(element => (string?)element.Attribute("data-dot-kind") == "cluster") == 2, "nested clusters render");
        Require(Contains(Cluster(svg, "cluster_a"), Node(svg, "a")) && Contains(Cluster(svg, "cluster_a"), Cluster(svg, "cluster_b")), "cluster contains direct nodes and child cluster");
        Require(Contains(Cluster(svg, "cluster_b"), Node(svg, "b")), "inner cluster contains nodes");
        var late = Render("digraph { a; subgraph cluster_a { label=Later; a; b } a->b }");
        Require(Contains(Cluster(late, "cluster_a"), Node(late, "a")), "cluster membership after first node use");
    }

    private static void Ranks()
    {
        foreach (string direction in new[] { "TB", "BT", "LR", "RL" })
        {
            var svg = Render("digraph { rankdir=" + direction + "; a->b->c }");
            var a = Center(Node(svg, "a")); var b = Center(Node(svg, "b"));
            Require(direction switch { "TB" => a.Y < b.Y, "BT" => a.Y > b.Y, "LR" => a.X < b.X, _ => a.X > b.X }, "rankdir " + direction);
        }
        var same = Render("digraph { {rank=same; a;b} a->c; b->d }");
        Require(Center(Node(same, "a")).Y == Center(Node(same, "b")).Y, "rank same");
        var unequal = Render("digraph { {rank=same; a[fontsize=10];b[fontsize=36]} a->c }");
        Require(Math.Abs(Center(Node(unequal, "a")).Y - Center(Node(unequal, "b")).Y) < 1, "rank centers align heterogeneous node sizes");
        var source = Render("digraph { a->b; c; {rank=source; c} }");
        Require(Center(Node(source, "c")).Y < Center(Node(source, "a")).Y, "rank source excludes other minimum ranks");
        var shortSvg = Render("digraph {a->b[minlen=1]}");
        var longSvg = Render("digraph {a->b[minlen=4]}");
        Require(Center(Node(longSvg, "b")).Y - Center(Node(longSvg, "a")).Y > Center(Node(shortSvg, "b")).Y - Center(Node(shortSvg, "a")).Y + 50, "minimum edge rank length");
    }

    private static void OutputDimensions()
    {
        const string objects = "node[shape=box,width=1,height=.5,fixedsize=true]; a[label=Alpha];";
        XElement With(string attributes) => Render("digraph { " + (attributes.Length == 0 ? "" : attributes + "; ") + objects + " }");
        static double Width(XElement svg) => (double)svg.Attribute("width")!;
        static double Height(XElement svg) => (double)svg.Attribute("height")!;
        static double[] ViewBox(XElement svg) => ((string)svg.Attribute("viewBox")!).Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        static bool Close(double first, double second) => Math.Abs(first - second) < .001;
        var original = With("");
        var bare = With("pad=0");
        var padded = With("pad=\".5,1\"; bgcolor=lightblue");
        var box = ViewBox(bare); var paddedBox = ViewBox(padded);
        Require(Close(paddedBox[0], box[0] - 36) && Close(paddedBox[1], box[1] - 72) &&
            Close(paddedBox[2], box[2] + 72) && Close(paddedBox[3], box[3] + 144), "independent horizontal and vertical padding in inches");
        Require(Bounds(Node(bare, "a")) == Bounds(Node(padded, "a")), "padding does not change node layout or shape geometry");
        var background = padded.Elements().First(element => element.Name.LocalName == "rect");
        Require((double)background.Attribute("x")! == paddedBox[0] && (double)background.Attribute("y")! == paddedBox[1] &&
            (double)background.Attribute("width")! == paddedBox[2] && (double)background.Attribute("height")! == paddedBox[3], "background fills the full padded drawing");
        Require(ViewBox(With("pad=.25")).SequenceEqual(ViewBox(With("pad=\".25,.25\""))), "scalar padding applies to both axes");
        Require(ViewBox(With("pad=\"0,1\"")).SequenceEqual(new[] { box[0], box[1] - 72, box[2], box[3] + 144 }), "zero padding on one axis remains independent");
        var small = With("size=\".5,.2\"");
        double shrink = Math.Min(36 / Width(original), 14.4 / Height(original));
        Require(Close(Width(small), Width(original) * shrink) && Close(Height(small), Height(original) * shrink), "size uniformly fits the smaller bounding dimension");
        Require(ViewBox(small).SequenceEqual(ViewBox(original)) && Bounds(Node(small, "a")) == Bounds(Node(original, "a")), "size preserves viewBox coordinates and native hit geometry");
        var large = With("size=\"6,3\"");
        Require(Width(large) == Width(original) && Height(large) == Height(original), "maximum size does not enlarge a smaller graph");
        var enlarged = With("size=\"6,3!\"");
        double grow = Math.Min(432 / Width(original), 216 / Height(original));
        Require(Close(Width(enlarged), Width(original) * grow) && Close(Height(enlarged), Height(original) * grow), "exclamation size enlarges uniformly until one requested dimension is reached");
        var narrow = With("size=\"6,.25!\"");
        double narrowScale = Math.Min(432 / Width(original), 18 / Height(original));
        Require(Close(Width(narrow), Width(original) * narrowScale) && Close(Height(narrow), Height(original) * narrowScale), "exclamation size still shrinks an oversized dimension");
        Require(Width(With("size=.5")) == Width(With("size=\".5,.5\"")) && Height(With("size=.5")) == Height(With("size=\".5,.5\"")), "scalar size constrains both axes");
        foreach (string size in new[] { "0", "0,1", "1,0", "0,0!" })
        {
            var disabled = With("size=\"" + size + "\"");
            Require(Width(disabled) == Width(original) && Height(disabled) == Height(original), "zero dimension leaves size fitting disabled: " + size);
        }
        var paddedFit = With("size=1; pad=\".5,1\"");
        double fitPadding = Math.Min(72 / Width(padded), 72 / Height(padded));
        Require(Close(Width(paddedFit), Width(padded) * fitPadding) && Close(Height(paddedFit), Height(padded) * fitPadding), "size fits the drawing including its independent padding");
        var resolution = With("size=\"6,3!\"; dpi=192");
        Require(Close(Width(resolution), Width(original) * grow * 2) && Close(Height(resolution), Height(original) * grow * 2) &&
            ViewBox(resolution).SequenceEqual(ViewBox(original)), "resolution applies after uniform size fitting without changing source geometry");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            Require(With("size=\".5,.2\"").ToString(SaveOptions.DisableFormatting) == small.ToString(SaveOptions.DisableFormatting), "size and padding use culture-independent numeric parsing and formatting");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        foreach (string attributes in new[] { "size=\"!\"", "size=\"1,\"", "size=\"1,2,3\"", "size=\"-1,2\"", "size=\"NaN,2\"", "size=\"Infinity,2\"", "size=\"1e-99\"", "pad=\"1,\"", "pad=\"-1,0\"", "pad=\"1,2,3\"", "size=1; ratio=fill" })
            Require(!PlantUmlRenderer.TryRender("@startdot\ndigraph { " + attributes + "; " + objects + " }\n@enddot", out _), "bounded output-size fallback " + attributes);
    }

    private static void LabelsAndPorts()
    {
        var record = Render("digraph { node[shape=record]; a[label=\"<left> First|{<top> Upper|<bottom> Lower}|Last\"]; b[label=\"<in> Input|Output\"]; a:bottom:e -> b:in:w [headlabel=H taillabel=T label=Route] }");
        Require(record.Value.Contains("Upper") && record.Value.Contains("Lower") && record.Value.Contains("Route"), "record nested fields and edge labels");
        var html = Render("digraph { a[shape=plain label=<<TABLE BORDER=\"1\" CELLBORDER=\"1\" CELLSPACING=\"0\"><TR><TD PORT=\"header\" COLSPAN=\"2\" BGCOLOR=\"navy\"><FONT COLOR=\"white\" POINT-SIZE=\"22\"><B>Header &amp; name</B></FONT></TD></TR><TR><TD ROWSPAN=\"2\">Left</TD><TD PORT=\"right\">Right<BR ALIGN=\"LEFT\"/><I>Next</I></TD></TR><TR><TD>Bottom</TD></TR></TABLE>>]; b; a:right:e -> b:w }");
        Require(html.Descendants().Any(element => element.Name.LocalName == "text" && element.Value == "Header & name" && (string?)element.Attribute("font-size") == "22" && (string?)element.Attribute("font-weight") == "bold"), "HTML styled text and entity decoding");
        Require(html.Value.Contains("Bottom") && html.Value.Contains("Next"), "HTML row/column spans and line breaks");
        Render("digraph { a[label=<<FONT POINT-SIZE=\"18\">A<SUP>2</SUP> &beta; <U>line</U></FONT>>]; b; a:ne->b:sw }");
        var escapes = Render("digraph Name { a[label=\"\\N in \\G\\lsecond\\rrd\\n\"]; a->b[label=\"\\T to \\H (\\E)\"] }");
        Require(escapes.Value.Contains("a in Name") && escapes.Value.Contains("a to b (a->b)"), "Graphviz escString substitutions");
        Render("digraph { a[label=<John's &amp; Mary's <!-- a < b -->label>] }");
    }

    private static void Attributes()
    {
        foreach (string shape in new[] { "box", "ellipse", "circle", "point", "diamond", "triangle", "invtriangle", "hexagon", "octagon", "doublecircle", "note", "tab", "folder", "box3d", "component", "cylinder", "star", "polygon", "Mrecord", "Msquare" })
            Render("digraph { a[shape=" + shape + " label=Shape style=filled fillcolor=\"#abcdef\"]; a->b }");
        foreach (string arrow in new[] { "normal", "onormal", "diamond", "odiamond", "dot", "odot", "box", "tee", "crow", "vee", "curve", "icurve", "inv", "none", "olnormalodot", "rdiamond" })
            Render("digraph { a->b[arrowhead=" + arrow + " arrowtail=dot dir=both arrowsize=1.5 color=red penwidth=2] }");
        var gradient = Render("digraph { bgcolor=\"white:lightblue\"; a[shape=box style=filled fillcolor=\"red:blue\" gradientangle=90]; a->b[style=dashed color=\"#123456\"] }");
        Require(gradient.Descendants().Count(element => element.Name.LocalName == "linearGradient") == 2, "node and graph gradients");
        var link = Render("digraph {a[URL=\"https://example.org/a?x=1&y=2\" tooltip=\"Open A\" target=\"_blank\"];a->b[href=\"#edge\"]}");
        Require(link.Descendants().Any(element => element.Name.LocalName == "a" && (string?)element.Attribute("href") == "https://example.org/a?x=1&y=2"), "escaped node hyperlink");
        var invisible = Render("digraph { a->b[style=invis];b->c; a[style=invis] }");
        Require(!invisible.Descendants().Any(element => (string?)element.Attribute("data-dot-id") == "node:a") && Edges(invisible).Count() == 1, "invisible constraints retain placement without paint");
    }

    private static void SourceSpans()
    {
        string source = "\uFEFF@startdot\r\ndigraph { \"space name\" [label=\"First\" + \" label\"];\r\n \"space name\":e -> next:w [label=\"Edge\"]; next -> last; }\r\n@enddot\r\n";
        var svg = RenderSource(source);
        var node = Node(svg, "space name");
        Require(Slice(source, node, "data-dot-label") == "\"First\" + \" label\"", "BOM CRLF and concatenated full label token");
        Require(Slice(source, node, "data-dot-name") == "\"space name\"", "original identifier token excludes ports");
        var edge = Edges(svg).First();
        Require(Slice(source, edge, "data-dot-from") == "\"space name\":e" && Slice(source, edge, "data-dot-to") == "next:w" && Slice(source, edge, "data-dot-arrow") == "->", "literal edge endpoint and operator token spans");
        Require((string?)Node(svg, "last").Attribute("data-dot-explicit") == "false" && source[(int)Node(svg, "last").Attribute("data-dot-label-insert")!] == '}', "implicit node label insertion at graph close");
        Require(Edges(svg).Select(element => (string?)element.Attribute("data-dot-id")).Distinct().Count() == 2, "unique same-line edge identities");
        string richSource = "@startdot\ndigraph { a[shape=record label=\"<in> one\\|two|quote \\\"yes\\\"\"]; b[shape=plain label=<<TABLE><TR><TD>R&amp;D</TD><TD><B>bold</B></TD></TR></TABLE>>] }\n@enddot";
        var rich = RenderSource(richSource);
        var fields = rich.Descendants().Where(element => element.Attribute("data-edit-kind") != null).ToArray();
        Require(fields.Any(element => (string?)element.Attribute("data-edit-kind") == "dot-record-text" && Slice(richSource, element, "data-edit") == "one\\|two"), "record field raw source token");
        Require(fields.Any(element => (string?)element.Attribute("data-edit-kind") == "dot-html-text" && Slice(richSource, element, "data-edit") == "R&amp;D"), "HTML field retains original entity source span");
        var lines = Node(Render("digraph { a[label=\"first\\nsecond\"] }"), "a");
        Require((string?)lines.Attribute("data-dot-label-text") == "first\nsecond", "multiline metadata preserves XML attribute line breaks");
        var shared = Render("digraph { node[shape=record label=\"<p>Default|Value\"]; a; b; c,d[label=\"<p>Shared|Fields\"]; edge[label=<Shared <B>edge</B>> headlabel=<Head>]; a->b->c; }");
        Require(!shared.Descendants().Any(element => element.Attribute("data-edit-start") != null), "default and shared rich labels have no single-object leaf edits");
        Require((string?)Node(shared, "c").Attribute("data-dot-label-shared") == "true" && Edges(shared).All(element => (string?)element.Attribute("data-dot-label-shared") == "true"), "shared declarations and chains carry explicit edit provenance");
        var reopened = Render("strict digraph { a->b->c[label=<Shared <B>edge</B>>]; a->b[color=red]; b->c[color=blue]; }");
        Require(Edges(reopened).All(element => (string?)element.Attribute("data-dot-label-shared") == "true") &&
            !reopened.Descendants().Any(element => element.Attribute("data-edit-start") != null),
            "strict singleton redeclarations retain shared label ownership and suppress unsafe HTML leaf edits");
        var independent = Render("strict digraph { a->b->c[label=<Shared <B>edge</B>>]; a->b[label=<Own <B>edge</B>>]; b->c[color=blue]; }");
        Require(Edges(independent).All(element => element.Attribute("data-dot-label-shared") == null &&
            element.Descendants().Any(child => (string?)child.Attribute("data-edit-kind") == "dot-html-text")),
            "independently owned strict-edge labels retain safe HTML leaf editing after an explicit override");
    }

    private static void Images()
    {
        const string artwork = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\"><rect width=\"40\" height=\"20\" fill=\"red\"/></svg>";
        int calls = 0;
        var options = new PlantUmlRenderOptions { ResolveInclude = path => { calls++; return path == "icon.svg" ? artwork : null; } };
        string source = "@startdot\ndigraph { a[image=\"icon.svg\" imagescale=true width=2 height=1 label=\"\"]; b[shape=plain label=<<TABLE><TR><TD WIDTH=\"80\" HEIGHT=\"60\"><IMG SRC=\"icon.svg\" SCALE=\"BOTH\"/></TD></TR></TABLE>>]; a->b }\n@enddot";
        Require(PlantUmlRenderer.TryRender(source, out var image, options) && image.Svg.Contains("scale(", StringComparison.Ordinal), "node and HTML images use host artwork resolver");
        Require(calls == 1, "image source cache shares same-line resource requests");
    }

    private static void Boundaries()
    {
        foreach (string body in new[] { "digraph {a--b}", "graph {a->b}", "digraph {a[label=\"unterminated]}", "digraph { a[shape=epsf] }", "digraph { a[label=<<TABLE><TR><TD>bad</TR></TABLE>>] }", "digraph { a[URL=\"javascript:alert(1)\"] }" })
            Require(!PlantUmlRenderer.TryRender("@startdot\n" + body + "\n@enddot", out _), "bounded fallback " + body);
        string source = "@startdot\ndigraph { a[label=\"A & B\"]; a->b; }\n@enddot";
        var initial = RenderSource(source).ToString(SaveOptions.DisableFormatting);
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL"); Require(RenderSource(source).ToString(SaveOptions.DisableFormatting) == initial, "culture-independent deterministic DOT"); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static XElement Render(string body) => RenderSource("@startdot\n" + body + "\n@enddot");
    private static XElement RenderSource(string source)
    {
        Require(PlantUmlRenderer.TryRender(source, out var diagram) && diagram.Kind == DiagramKind.Dot, "native DOT expected " + source);
        return XElement.Parse(diagram!.Svg);
    }
    private static XElement Node(XElement svg, string id) => svg.Descendants().Single(element => (string?)element.Attribute("data-dot-id") == "node:" + id);
    private static XElement Cluster(XElement svg, string id) => svg.Descendants().Single(element => (string?)element.Attribute("data-dot-id") == "cluster:" + id);
    private static IEnumerable<XElement> Edges(XElement svg) => svg.Descendants().Where(element => (string?)element.Attribute("data-dot-kind") == "edge");
    private static string? Paint(XElement node, string attribute) => node.Descendants().Select(element => (string?)element.Attribute(attribute)).FirstOrDefault(value => value != null && value != "none");
    private static (double X, double Y, double W, double H) Bounds(XElement group)
    {
        var element = group.Descendants().First(item => item.Name.LocalName is "rect" or "ellipse");
        double Read(string key) => (double?)element.Attribute(key) ?? 0;
        return element.Name.LocalName == "ellipse" ? (Read("cx") - Read("rx"), Read("cy") - Read("ry"), Read("rx") * 2, Read("ry") * 2) : (Read("x"), Read("y"), Read("width"), Read("height"));
    }
    private static (double X, double Y) Center(XElement node) { var box = Bounds(node); return (box.X + box.W / 2, box.Y + box.H / 2); }
    private static bool Contains(XElement container, XElement child)
    {
        var outer = Bounds(container); var inner = Bounds(child);
        return inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.W <= outer.X + outer.W && inner.Y + inner.H <= outer.Y + outer.H;
    }
    private static string Slice(string source, XElement element, string prefix) => source.Substring((int)element.Attribute(prefix + "-start")!, (int)element.Attribute(prefix + "-length")!);
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException("DOT compatibility: " + label); }
}
