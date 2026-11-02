using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class ChartCases
{
    public static void Run()
    {
        var svg = Render("""
            @startchart
            h-axis [W1, W2, W3, W4]
            v-axis 0 --> 100
            bar "Completed" [32, 51, 60, 75] #7464CF
            legend right
            @endchart
            """);
        var bars = Elements(svg, "g").Single(e => (string?)e.Attribute("class") == "series bar");
        Require((int?)bars.Attribute("data-source-line") == 3, "series source mapping");
        var rectangles = Elements(bars, "rect").ToArray();
        Require(rectangles.Length == 4 && (string?)rectangles[0].Attribute("fill") == "#7464CF", "categorical bars");
        // These plot coordinates were compared against @plantuml/core 1.2026.9.
        Require((int?)rectangles[0].Attribute("x") == 80 && (int?)rectangles[0].Attribute("y") == 254 &&
                (int?)rectangles[0].Attribute("width") == 60 && (int?)rectangles[0].Attribute("height") == 96, "browser engine coordinates");
        Require(Elements(svg, "text").Any(e => e.Value == "Completed"), "legend");
        var mixed = Render("""
            @startchart
            title Delta
            h-axis "Quarter" [A, B]
            v-axis "Change" -20 --> 20 spacing 10
            bar "First" [-15, 12]
            bar "Second" [2, -3]
            line "Trend" [0, 16] #990011
            scatter "Measured" [2, -3] #335599
            area "Capacity" [7, 8] #AA77BB
            legend bottom
            @endchart
            """);
        var first = Elements(mixed, "g").First(x => (string?)x.Attribute("class") == "series bar").Elements().First();
        var second = Elements(mixed, "g").Where(x => (string?)x.Attribute("class") == "series bar").Skip(1).First().Elements().First();
        Require((double?)first.Attribute("width") == 80 && (double?)first.Attribute("height") == 112.5, "negative grouped bar");
        Require((double?)first.Attribute("x") + (double?)first.Attribute("width") == (double?)second.Attribute("x"), "group adjacency");
        Require(Elements(mixed, "circle").Count() == 2 && Elements(mixed, "polyline").Count() == 1 && Elements(mixed, "polygon").Count() == 1, "mixed series");
        foreach (string directive in new[] { "v2-axis 0 --> 20", "orientation horizontal", "bar \"x\" [NaN,2]", "line \"x\" [2,3,4]", "stack bar \"x\" [1,2]", "legend nowhere" })
            Require(!PlantUmlRenderer.TryRender("@startchart\nh-axis [a,b]\nv-axis 0 --> 10\nbar [2,4]\n" + directive + "\n@endchart", out _), "compatibility path: " + directive);
        Require(!PlantUmlRenderer.TryRender("@startchart\nh-axis [a]\nv-axis 0 --> 1 spacing 0.000001\nbar [0]\n@endchart", out _), "bounded tick count");
    }

    private static XElement Render(string text)
    {
        if (!PlantUmlRenderer.TryRender(text, out var result) || result.Kind.ToString() != "Chart")
            throw new Exception("Managed chart did not accept: " + text);
        return XElement.Parse(result.Svg);
    }
    private static IEnumerable<XElement> Elements(XElement e, string local) => e.Descendants().Where(child => child.Name.LocalName == local);
    private static void Require(bool result, string reason)
    {
        if (!result) throw new Exception("Chart test failed: " + reason);
    }
}
