using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class StructuredRendererCases
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool condition, string reason) { if (!condition) throw new Exception("Structured native renderer: " + reason); count++; }
        XElement Render(string source)
        {
            Check(PlantUmlRenderer.TryRender(source, out var rendered), "accepted " + source);
            return XElement.Parse(rendered!.Svg);
        }
        static IEnumerable<XElement> Elements(XElement root, string name) => root.Descendants().Where(e => e.Name.LocalName == name);
        string pairs = "\uFEFF@startchart\r\nh-axis \"Distance\" -10 --> 10 spacing 5 grid label-right\r\nv-axis \"Rate\" 0 --> 100 ticks [0:\"None\",100:\"Full\"] grid label-top\r\nv2-axis \"Secondary\" 0 --> 10\r\nscatter <<measured>> \"Data\" [(-5.00, 20), (5, 80)] #blue labels\r\nline \"Trend\" [(0, 5), (10, 10)] #red v2\r\nannotation \"Peak\" at (5,80) <<arrow>>\r\nlegend right\r\n@endchart";
        var plot = Render(pairs);
        Check(Elements(plot, "g").Count(g => (string?)g.Attribute("class") == "secondary-axis") == 1, "secondary axis rendered");
        Check(Elements(plot, "g").Any(g => (string?)g.Attribute("data-style-name") == "grid") && Elements(plot, "text").Any(t => t.Value == "Full"), "grid and custom ticks");
        var scatter = Elements(plot, "g").Single(g => (string?)g.Attribute("class") == "series scatter");
        var markers = Elements(scatter, "circle").ToArray();
        Check(markers.Length == 2 && (double)markers[0].Attribute("cx")! < (double)markers[1].Attribute("cx")! && (double)markers[0].Attribute("cy")! > (double)markers[1].Attribute("cy")!, "explicit coordinate projection");
        Check((string?)markers[0].Attribute("fill") == "blue", "named series color");
        foreach (var element in plot.Descendants().Where(e => e.Attribute("data-edit-start") != null))
        {
            int start = (int)element.Attribute("data-edit-start")!, length = (int)element.Attribute("data-edit-length")!;
            string token = pairs.Substring(start, length);
            Check(token.Length > 0 && !token.Contains('\r') && !token.Contains('\n'), "source spans retain BOM/CRLF offsets");
            if ((string?)element.Attribute("data-edit-kind") == "quoted") Check(token.StartsWith('"') && token.EndsWith('"'), "quoted caption span");
        }
        foreach (var point in Elements(plot, "g").Where(g => g.Attribute("data-x-edit-start") != null))
            Check(double.TryParse(pairs.Substring((int)point.Attribute("data-x-edit-start")!, (int)point.Attribute("data-x-edit-length")!), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _), "coordinate X token metadata");
        Check(Elements(plot, "g").Any(g => (string?)g.Attribute("class") == "annotation") && Elements(plot, "path").Any(), "annotation and arrow");
        var horizontal = Render("@startchart\norientation horizontal\nv-axis \"Quarter\" [A,B]\nh-axis \"Total\" -10 --> 30\nbar \"One\" [10,-5] labels\nbar \"Two\" [20,5]\nlegend bottom\n@endchart");
        var bars = Elements(horizontal, "g").Where(g => (string?)g.Attribute("class") == "series bar").ToArray();
        var first = bars[0].Elements().First(e => e.Name.LocalName == "rect");
        Check((double)first.Attribute("width")! == 100 && (double)first.Attribute("height")! == 60, "horizontal grouped bar dimensions");
        var stacked = Render("@startchart\nstackMode stacked\nh-axis [A,B]\nv-axis -10 --> 10\nbar [2,-3]\nbar [4,-2]\n@endchart");
        var stacks = Elements(stacked, "g").Where(g => (string?)g.Attribute("class") == "series bar").Select(g => g.Elements().Where(e => e.Name.LocalName == "rect").ToArray()).ToArray();
        Check((double)stacks[0][0].Attribute("x")! == (double)stacks[1][0].Attribute("x")! && (double)stacks[1][0].Attribute("y")! + (double)stacks[1][0].Attribute("height")! == (double)stacks[0][0].Attribute("y")!, "positive stacked baseline");
        Check((double)stacks[0][1].Attribute("y")! + (double)stacks[0][1].Attribute("height")! == (double)stacks[1][1].Attribute("y")!, "negative stacked baseline");
        Render("@startchart\nline [1,2,3]\n@endchart");
        Render("@startchart\nh-axis 0 --> 100\nv-axis 0 --> 10\nline [1,2,3]\n@endchart");
        string packet = "@startpacketdiag\n{\ncolwidth = 16;\nnode_height = 48;\nscale_interval = 4;\nscale_direction = rtl;\nsame_height = false;\n* Header [len=8, height=2, rotate=270]\n* Flags [len=4]\n16-31: Payload\n}\n@endpacketdiag";
        var diagram = Render(packet);
        var fields = Elements(diagram, "g").Where(g => (string?)g.Attribute("class") == "packet-field").ToArray();
        Check(fields.Length == 3 && (int?)fields[1].Attribute("data-packet-first") == 8 && (int?)fields[1].Attribute("data-packet-last") == 11, "automatic packet fields metadata");
        Check(Elements(fields[0], "g").Any(g => ((string?)g.Attribute("transform"))?.StartsWith("rotate(270", StringComparison.Ordinal) == true), "packet label rotation");
        Check((double)Elements(fields[0], "rect").First().Attribute("height")! > (double)Elements(fields[2], "rect").First().Attribute("height")!, "packet variable row height");
        Check((double)Elements(fields[0], "rect").First().Attribute("x")! > (double)Elements(fields[1], "rect").First().Attribute("x")!, "packet scale direction");
        foreach (var field in fields) Check(packet.Substring((int)field.Attribute("data-edit-start")!, (int)field.Attribute("data-edit-length")!) == field.Elements().First(e => e.Name.LocalName == "title").Value, "packet label source mapping excludes attributes");
        foreach (string bad in new[] {
            "@startpacketdiag\n0-7: A\n7-8: B\n@endpacketdiag", "@startpacketdiag\n* A [len=0]\n@endpacketdiag",
            "@startchart\nh-axis [A,B]\nv-axis 0 --> 10\nbar [(0,2),(1,3)]\n@endchart",
            "@startchart\nh-axis 0 --> 10\nv-axis 0 --> 10\nline [(0,2)]\nscatter [3]\n@endchart",
            "@startchart\nstackMode stacked\nh-axis [A]\nv-axis 0 --> 10\nbar [6]\nbar [5]\n@endchart",
            "@startchart\nh-axis [A]\nv-axis 0 --> 10\nbar [2]\nannotation \"Invalid\" at (Missing,3)\n@endchart" })
            Check(!PlantUmlRenderer.TryRender(bad, out _), "invalid structure delegates without partial SVG");
        return count;
    }
}
