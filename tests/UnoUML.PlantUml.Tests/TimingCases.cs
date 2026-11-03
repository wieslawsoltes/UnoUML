using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class TimingCases
{
    public static void Run()
    {
        TimingStyleCases.Run();
        var sample = Render("""
            @startuml
            robust "Bus" as Bus
            @0
            Bus is Idle
            @10
            Bus is Active
            @30
            Bus is Idle
            @enduml
            """);
        var segments = Groups(sample, "timing-state").ToArray();
        Require(segments.Length == 3 && (int?)segments[0].Attribute("data-source-line") == 3, "transition hit mapping");
        var paths = segments.Select(s => s.Elements().First(e => e.Name.LocalName == "line")).ToArray();
        double Y(XElement path) => (double)path.Attribute("y1")!;
        double Length(XElement path) => (double)path.Attribute("x2")! - (double)path.Attribute("x1")!;
        Require(Y(paths[0]) == Y(paths[2]) && Y(paths[0]) > Y(paths[1]), "named state levels");
        Require(Math.Abs(2 * Length(paths[0]) - Length(paths[1])) < .001, "linear time projection");

        var mixed = Render("""
            @startuml
            clock "CLK" as CLK with period 4 pulse 1 offset 1
            binary "Enabled" as EN
            concise "Bus" as B
            analog "Analog" as A
            @0
            EN is low
            A is 0
            B is Link
            @+2
            EN is high
            B is Ready
            B -> EN@+1 : IRQ
            @5
            A is 10
            @enduml
            """);
        Require(Groups(mixed,"timing-track").Count() == 4 && Groups(mixed,"time").Count() == 4, "timing tracks and source times");
        Require(Groups(mixed, "timing-track").First().Elements().Any(e => e.Name.LocalName == "path" && e.Attribute("d")!.Value.Contains('V')), "clock duty cycle");
        Require(Groups(mixed, "timing-state").Any(g => g.Value == "Ready"), "concise state labels");
        Require(Groups(mixed, "timing-message").Single().Value == "IRQ", "cross-track message");
        var analog = Groups(mixed,"timing-state").Single(g => (int?)g.Attribute("data-source-line") == 7).Elements().First();
        Require((double?)analog.Attribute("y1") > (double?)analog.Attribute("y2"), "analog interpolation");

        string Traces(string alignment, string label = "Idle and active") =>
            $"@startuml\nrobust \"Bus\" as Bus\n@0\nBus is Idle\nlegend {alignment}\n{label}\nTransitions\nendlegend\n@enduml";
        var above = Render(Traces("top left"));
        var below = Render(Traces("right"));
        var upper = Groups(above,"legend").Single();
        var lower = Groups(below,"legend").Single();
        var ns = upper.Name.Namespace;
        Require((int?)upper.Attribute("data-source-line") == 4 && upper.Value == "Idle and activeTransitions", "legend text and source map");
        var upperBox = upper.Element(ns+"rect")!; var lowerBox = lower.Element(ns+"rect")!;
        var upperTrack = Groups(above, "timing-track").Single().Element(ns+"line")!;
        var lowerTrack = Groups(below, "timing-track").Single().Element(ns+"line")!;
        Require((int)upperBox.Attribute("y")! + (int)upperBox.Attribute("height")! < (int)upperTrack.Attribute("y1")! &&
            (int)lowerBox.Attribute("y")! > (int)lowerTrack.Attribute("y1")! && (int)lowerBox.Attribute("x")! > (int)upperBox.Attribute("x")!, "legend location relative to waveform");
        foreach (var label in new[] { "|Header|Value|", "!include file.puml" })
            Require(!PlantUmlRenderer.TryRender(Traces("right", label), out _), "rich legend delegates to reference");

        var background = Render("@startuml\nrobust \"Bus\" as Bus\nskinparam backgroundcolor red\n@enduml");
        Require((string?)background.Element(ns + "rect")?.Attribute("fill") == "red", "native document background paint");
        foreach (string cmd in new[] { "@:anchor", "Bus is {-}", "Bus is {Low, High}", "Unknown is State", "clock c with period 0" })
            Require(!PlantUmlRenderer.TryRender("@startuml\nrobust \"Bus\" as Bus\n" + cmd + "\n@enduml", out _), "compatibility fallback for " + cmd);
        Require(!PlantUmlRenderer.TryRender("@startuml\nclock clk with period 0.0001\n@10000\n@enduml", out _), "bounded clock transitions");
    }
    private static IEnumerable<XElement> Groups(XElement svg, string name) => svg.Descendants().Where(e => (string?)e.Attribute("class") == name);
    private static XElement Render(string input)
    {
        if (!PlantUmlRenderer.TryRender(input, out var d) || d.Kind.ToString() != "Timing") throw new Exception("No managed timing render: " + input);
        return XElement.Parse(d.Svg);
    }
    private static void Require(bool assertion, string context)
    {
        if (!assertion) throw new Exception("Timing test failed: " + context);
    }
}
