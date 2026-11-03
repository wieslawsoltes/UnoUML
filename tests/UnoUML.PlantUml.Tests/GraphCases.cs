using System.Globalization;
using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class GraphCases
{
    public static void Run()
    {
        StyleCases.Graph();
        var examples = new[]
        {
            "title Order domain\nclass Order {\n +id : UUID\n +total() : Money\n}\nclass LineItem\nOrder \"1\" *-- \"many\" LineItem : contains",
            "actor \"Customer\" as Customer\nusecase \"Place order\" as Checkout\nCustomer --> Checkout",
            "component \"Client\" as Client\ncomponent \"API\" as API\ndatabase \"Storage\" as Store\nClient --> API : HTTPS\nAPI --> Store : query",
            "node Edge\nnode \"Application cluster\" as Cluster {\n artifact Worker\n}\nEdge --> Worker",
            "object \"order : Order\" as Order {\n id = 42\n}\nobject \"item : Line\" as Item\nOrder *-- Item",
            "package \"Domain\" as Group {\nclass Order\nclass Line\nOrder --> Line\n}\nclass External\nGroup.Order --> External",
            "class A\nclass B\nclass C\nA --> B\nB --> C\nC --> A\nA --> A\nA --> A : again"
        };
        foreach (var example in examples) _ = Render(example);
        var sample = Render(examples[0]);
        XNamespace svg = "http://www.w3.org/2000/svg";
        Check(sample.Descendants(svg + "g").Any(x => (string?)x.Attribute("data-qualified-name") == "Order" && (string?)x.Attribute("data-source-line") == "2"), "class source binding");
        Check(sample.Descendants(svg + "g").Any(x => (string?)x.Attribute("class") == "member" && (string?)x.Attribute("data-source-line") == "3"), "member source binding");
        Check(sample.Descendants(svg + "g").Any(x => (string?)x.Attribute("class") == "link" && (string?)x.Attribute("data-source-line") == "7"), "link source binding");
        foreach (string marker in new[] { "1", "many", "contains", "+id : UUID" }) Check(sample.Descendants(svg + "text").Any(x => x.Value == marker), "lost label " + marker);
        foreach (bool lr in new[] { false, true })
            foreach (string dir in new[] { "left", "right", "up", "down" })
            {
                var result = Render((lr ? "left to right direction\n" : "") + $"class A\nclass B\n' @unouml:layout\nA -[hidden]{dir}-> B");
                var a = Box(result, "A"); var b = Box(result, "B");
                bool good = (lr, dir) switch
                {
                    (false, "left") or (true, "up") => b.X + b.W < a.X,
                    (false, "right") or (true, "down") => a.X + a.W < b.X,
                    (false, "up") or (true, "left") => b.Y + b.H < a.Y,
                    _ => a.Y + a.H < b.Y
                };
                Check(good, "hidden direction " + dir);
                Check(!result.Descendants(svg + "g").Any(x => (string?)x.Attribute("class") == "link"), "invisible edge");
            }
        var constrained = Render("class A\nclass B\nA --> B : still visible\n' @unouml:layout\nA -[hidden]up-> B");
        Check(Box(constrained, "B").Y < Box(constrained, "A").Y, "explicit rank overrides default edge ranking");
        Check(constrained.Descendants(svg + "g").Count(x => (string?)x.Attribute("class") == "link") == 1, "preserved visible edge");
        var safe = Render("class \"A & B\" as A #abc\nclass B\nA --> B : read & write");
        Check(safe.Descendants(svg + "text").Any(x => x.Value == "A & B"), "escaped plain text");
        foreach (var body in new[] { "class A\n!theme plain", "class A\nA --> Unknown", "class A\nA --> A : **bold**", "class A {\n+missing", "class A\nactor B\n@startuml", "class A #g00000", "class A\nA -[red]-> A" })
            Check(!PlantUmlRenderer.TryRender("@startuml\n" + body + "\n@enduml", out _), "expected fallback: " + body);
        // Inspect primitive families used by PlantUML's deployment notation. An interface in a
        // component diagram is a lollipop; the same declaration in a class diagram is a classifier.
        foreach (var (kind, primitive) in new (string, string)[] {
            ("action","path"), ("actor/","circle"), ("agent","rect"), ("artifact","rect"), ("boundary","circle"),
            ("card","rect"), ("circle","circle"), ("cloud","path"), ("collections","rect"), ("control","circle"),
            ("database","path"), ("entity","circle"), ("file","path"), ("folder","path"), ("frame","path"),
            ("hexagon","path"), ("interface","circle"), ("label","text"), ("node","path"), ("package","path"),
            ("person","circle"), ("process","path"), ("queue","path"), ("rectangle","rect"), ("stack","path"),
            ("storage","rect"), ("usecase/","ellipse") })
        {
            var symbol = Render($"component Base\n{kind} \"Glyph\" as N\nBase --> N").Descendants(svg + "g")
                .Single(x => (string?)x.Attribute("data-qualified-name") == "N");
            Check(symbol.Elements(svg + primitive).Any(), "deployment shape " + kind);
            Check(symbol.Elements(svg + "text").Any(x => x.Value == "Glyph"), "deployment caption " + kind);
        }
        foreach (var (kind, letter) in new[] { ("struct", "S"), ("exception", "X"), ("protocol", "P"), ("metaclass", "M"), ("stereotype", "S") })
        {
            var type = Render($"class Root\n{kind} \"Model\" as N {{\n}}");
            Check(type.Descendants(svg + "text").Any(t => t.Value == letter), "classifier type badge " + kind);
        }
        var map = Render("map Values {\n key => Some value\n}\nlegend right\nExample key\nendlegend");
        Check(map.Descendants(svg + "text").Any(t => t.Value == "Some value") && map.Descendants(svg + "text").Any(t => t.Value == "Example key"), "map cells and legend");
        var first = Render(examples[^1]).ToString(SaveOptions.DisableFormatting);
        Check(first == Render(examples[^1]).ToString(SaveOptions.DisableFormatting), "deterministic cycles");
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL"); Check(first == Render(examples[^1]).ToString(SaveOptions.DisableFormatting), "culture-independent geometry"); }
        finally { CultureInfo.CurrentCulture = old; }
    }

    private static (int X, int Y, int W, int H) Box(XElement image, string alias)
    {
        var group = image.Descendants().Single(x => (string?)x.Attribute("data-qualified-name") == alias);
        var rect = group.Elements().First(x => x.Name.LocalName == "rect");
        return ((int)rect.Attribute("x")!, (int)rect.Attribute("y")!, (int)rect.Attribute("width")!, (int)rect.Attribute("height")!);
    }
    private static XElement Render(string body)
    {
        if (!PlantUmlRenderer.TryRender("@startuml\n" + body + "\n@enduml", out var result) || result.Kind != DiagramKind.Graph)
            throw new InvalidOperationException("Managed graph failed to recognize:\n" + body);
        return XElement.Parse(result.Svg);
    }
    private static void Check(bool result, string name)
    {
        if (!result) throw new InvalidOperationException("Graph check failed: " + name);
    }
}
