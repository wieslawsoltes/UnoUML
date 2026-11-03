using System.Globalization;
using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class RichTextCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    internal static void Run()
    {
        var plain = Render("@startcreole\nPlain café 東京 🚀\n@endcreole", DiagramKind.Creole);
        Check(plain.Descendants(Ns + "text").Single().Value == "Plain café 東京 🚀", "plain Unicode label validation");
        foreach (string invalid in new[] { "\u0001", "\u007f", "\u0080", "\u009f", "\ud800", "\udc00", "\ufffe", "\uffff" })
            Check(!PlantUmlRenderer.TryRender("@startcreole\nPlain " + invalid + " label\n@endcreole", out _), "plain label retains XML and control validation");
        string limit = "A" + new string('\u0301', 65535);
        var longPlain = Render("@startcreole\n" + limit + "\n@endcreole", DiagramKind.Creole);
        Check(longPlain.Descendants(Ns + "text").Single().Value == limit, "maximum plain label length");
        Check(!PlantUmlRenderer.TryRender("@startcreole\n" + limit + "A\n@endcreole", out _), "oversized plain label declines");

        var graph = Render("""
            @startuml
            class "**Customer** and //Guest//\n<size:28><color:#c71585>Portal</color></size>\n<U+221E> &amp; <u:red>active</u>" as Client {
              +<b>Load</b>() : <font:monospaced>ID</font>
              <s>legacy</s>
            }
            class API
            Client --> API : <back:#fff0c0><color:blue>HTTPS</color></back>
            @enduml
            """, DiagramKind.Graph);
        Check(Span(graph, "Customer", "font-weight", "bold"), "Creole bold");
        Check(Span(graph, "Guest", "font-style", "italic"), "Creole italic");
        Check(Span(graph, "Portal", "font-size", "28") && Span(graph, "Portal", "fill", "#c71585"), "nested font size and color");
        Check(Span(graph, "ID", "font-family", "monospace") && Span(graph, "legacy", "text-decoration", "line-through"), "member monospace and strike");
        Check(graph.Descendants(Ns + "text").Any(t => t.Value.Contains("∞ & active", StringComparison.Ordinal)), "Unicode and entity decoding");
        Check(graph.Descendants(Ns + "line").Any(t => (string?)t.Attribute("stroke") == "red" && t.Attribute("data-rich-paint") != null), "colored underline geometry");
        Check(graph.Descendants(Ns + "rect").Any(t => (string?)t.Attribute("fill") == "#fff0c0" && t.Attribute("data-rich-paint") != null), "inline background");
        Check((string?)Entity(graph, "Client").Attribute("data-source-line") == "1", "rich labels preserve physical source identity");

        var small = Render("@startuml\nrectangle \"Portal\" as A\n@enduml", DiagramKind.Graph);
        var large = Render("@startuml\nrectangle \"<size:40>Portal</size>\" as A\n@enduml", DiagramKind.Graph);
        Check(Box(large, "A").Width > Box(small, "A").Width * 2 && Box(large, "A").Height > Box(small, "A").Height, "inline font metrics affect graph geometry");

        var wrapped = Render("""
            @startuml
            <style>
            classDiagram {
              class { MaximumWidth 145 }
            }
            </style>
            class "**alpha bravo charlie delta echo foxtrot**" as A
            @enduml
            """, DiagramKind.Graph);
        var wrappedText = Entity(wrapped, "A").Descendants(Ns + "text").Where(t => t.Value != "C").ToArray();
        Check(wrappedText.Length >= 3, "styled word wrapping creates multiple rows");
        Check(string.Join(" ", wrappedText.Select(t => t.Value)).Trim() == "alpha bravo charlie delta echo foxtrot", "word wrapping preserves text");
        Check(wrappedText.SelectMany(t => t.Descendants(Ns + "tspan")).All(t => (string?)t.Attribute("font-weight") == "bold"), "wrapping preserves style across row boundaries");

        var sequence = Render("""
            @startuml
            participant "<size:24><b>Client</b></size>" as A
            participant Server as B
            A -> B : [[https://example.org/{Documentation} **Open API**]]\nH<sub>2</sub>O and x<sup>2</sup>
            note over A,B
            <color:blue>first line
            second line</color>
            end note
            ref over A,B
            <font size=20 color=red face=Verdana>Review</font>
            end ref
            @enduml
            """, DiagramKind.Sequence);
        Check(sequence.Descendants(Ns + "a").Any(a => (string?)a.Attribute("href") == "https://example.org/" && a.Descendants(Ns + "title").Any(t => t.Value == "Documentation")), "hyperlink and tooltip");
        Check(Span(sequence, "Open API", "font-weight", "bold"), "nested link text formatting");
        Check(sequence.Descendants(Ns + "tspan").Where(t => t.Value.Contains("line", StringComparison.Ordinal)).All(t => (string?)t.Attribute("fill") == "blue"), "multiline style scope");
        Check(Span(sequence, "Review", "font-family", "Verdana") && Span(sequence, "Review", "font-size", "20"), "HTML font attributes");
        var twos = sequence.Descendants(Ns + "tspan").Where(t => t.Value == "2").ToArray();
        Check(twos.Length == 2 && double.Parse(twos[0].Attribute("y")!.Value, CultureInfo.InvariantCulture) > double.Parse(twos[1].Attribute("y")!.Value, CultureInfo.InvariantCulture), "subscript and superscript baseline positions");

        var tree = Render("""
            @startmindmap
            * **Root**
            ** <size:25>Large</size>
            ** ~~Review~~ and ""code""
            ** <U+1F680> &lt;safe&gt;
            @endmindmap
            """, DiagramKind.MindMap);
        Check(Span(tree, "Root", "font-weight", "bold") && Span(tree, "Large", "font-size", "25"), "tree labels use rich runs");
        Check(tree.Descendants(Ns + "path").Any(t => t.Attribute("data-rich-paint") != null), "wave decoration");
        Check(tree.Descendants(Ns + "tspan").Any(t => t.Value == "code" && (string?)t.Attribute("data-rich-monospaced") == "true"), "Creole code style marker");
        Check(tree.Descendants(Ns + "text").Any(t => t.Value.Contains("🚀 <safe>", StringComparison.Ordinal)), "supplementary Unicode and escaped angle brackets");

        var headings = Render("@startuml\nrectangle \"==Host\\nwide description\\l\" as A\n@enduml", DiagramKind.Graph);
        Check(Span(headings, "Host", "font-size", "16") && Span(headings, "Host", "font-weight", "bold"), "relative Creole heading font");
        var headingRows = Entity(headings, "A").Descendants(Ns + "text").Where(t => t.Value is "Host" or "wide description").ToArray();
        Check(headingRows.Length == 2 && headingRows[0].Attribute("x")!.Value == headingRows[1].Attribute("x")!.Value, "left-aligned multiline caption");
        var aligned = Render("@startuml\nrectangle \"Long caption\\nsmall\\r\" as A\n@enduml", DiagramKind.Graph);
        var alignedRows = Entity(aligned, "A").Descendants(Ns + "text").Where(t => t.Value is "Long caption" or "small").ToArray();
        Check(alignedRows.Length == 2 && double.Parse(alignedRows[0].Attribute("x")!.Value, CultureInfo.InvariantCulture) < double.Parse(alignedRows[1].Attribute("x")!.Value, CultureInfo.InvariantCulture), "right-aligned multiline caption");

        var literalMath = Render("@startlatex\n\\text{**bold**}\n@endlatex", DiagramKind.LaTeX);
        var bareMath = Render("@startlatex\n\\text{bold}\n@endlatex", DiagramKind.LaTeX);
        Check((double)literalMath.Attribute("width")! > (double)bareMath.Attribute("width")! && literalMath.Descendants(Ns + "text").Any(t => t.Value == "**bold**"), "mathematical literal text keeps its complete width");

        foreach (string label in new[] { "<img:https://example.org/icon.png>", "<b><unknown>bad</unknown></b>", "[[javascript:alert(1) bad]]", "<$missing>", "<U+D800>", "<size:0>zero</size>", "bad\u0001" })
            Check(!PlantUmlRenderer.TryRender("@startuml\nrectangle \"" + label + "\" as A\n@enduml", out _), "unsupported rich syntax declines safely");

        var sprites = Render("""
            @startuml
            sprite icon [2x2/16] {
            F0
            0F
            }
            rectangle "<$icon*2> Badge" as A
            rectangle "<#red$icon,scale=3.4> Color" as B
            A --> B
            @enduml
            """, DiagramKind.Graph);
        Check(sprites.Descendants(Ns + "g").Count(g => (string?)g.Attribute("data-sprite") == "icon") == 2, "inline pixel sprites");
        Check(sprites.Descendants(Ns + "g").Any(g => (string?)g.Attribute("style") == "color:red"), "sprite tint");

        var artwork = Render("@startuml\nrectangle \"<size:24><&person> <:1f600:></size> <#red:grinning:>\" as A\n@enduml", DiagramKind.Graph);
        var icon = artwork.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-sprite") == "$openiconic/person");
        var emoji = artwork.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-sprite") == "$emoji/1f600");
        Check(icon.Attribute("transform")!.Value.Contains("scale(2)", StringComparison.Ordinal) && emoji.Attribute("transform")!.Value.Contains("scale(1)", StringComparison.Ordinal), "icon and emoji intrinsic font scaling");
        var tinted = artwork.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-sprite") == "$emoji/grinning");
        var palette = tinted.Descendants().Attributes("fill").Select(a => a.Value.ToUpperInvariant()).ToHashSet();
        Check(new[] { "#FEA8A8", "#FE0000", "#FECDCD" }.All(palette.Contains), "emoji tint preserves upstream luminance shading");

        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=";
        var image = Render("@startuml\nrectangle \"<img:data:image/png;base64," + png + "{scale=12}> Icon\" as A\n@enduml", DiagramKind.Graph);
        Check(image.Descendants(Ns + "image").Any(e => e.Attributes().Any(a => a.Name.LocalName == "href" && a.Value.StartsWith("data:image/png;base64,", StringComparison.Ordinal))), "inline raster data image");
    }

    private static XElement Render(string source, DiagramKind kind)
    {
        if (!PlantUmlRenderer.TryRender(source, out var result) || result.Kind != kind) throw new InvalidOperationException("Rich label renderer declined " + kind + "\n" + source);
        return XElement.Parse(result.Svg);
    }

    private static bool Span(XElement svg, string text, string attribute, string value) =>
        svg.Descendants(Ns + "tspan").Any(span => span.Value == text && (string?)span.Attribute(attribute) == value);

    private static XElement Entity(XElement svg, string id) => svg.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-qualified-name") == id);

    private static (int Width, int Height) Box(XElement svg, string id)
    {
        var box = Entity(svg, id).Elements(Ns + "rect").First();
        return ((int)box.Attribute("width")!, (int)box.Attribute("height")!);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Rich label regression failed for " + message);
    }
}
