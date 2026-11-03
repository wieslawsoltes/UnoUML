using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class TimingStyleCases
{
    internal static void Run()
    {
        const string source = """
            @startuml
            title <size:24>**Measured timing**</size>
            <style>
            timingDiagram {
              FontSize 16
              concise { FontSize 22; Padding 4; MaximumWidth 150; BackgroundColor #eef5ff }
              robust { FontSize 18; Padding 2; MaximumWidth 130 }
              arrow { FontSize 18; MaximumWidth 160; FontColor navy }
              timeline { FontSize 20 }
              .important { LineColor red; LineThickness 3 }
              legend { FontSize 16; Padding 3 }
            }
            </style>
            robust "**API**\\nGateway" as A
            concise "Client" as B <<important>>
            binary "Enabled" as EN <<important>>
            @0
            A is "<color:blue>Idle</color>"
            B is "**Waiting**\\nfor response"
            EN is low
            @10
            A is "<size:24>Working</size>" : <i>Parsing</i> request
            B is "[[https://example.org Details]]"
            B -> EN@+2 : **Ready**\\nfor delivery
            @20
            EN is high
            legend top right
            **State** summary
            <color:red>Styled signals</color>
            endlegend
            @enduml
            """;
        var svg = Render(source.Replace("\\\\n", "\\n", StringComparison.Ordinal));
        Require(svg.Descendants().Any(element => element.Name.LocalName == "tspan" && (string?)element.Attribute("font-weight") == "bold" && element.Value == "API"), "rich track captions");
        Require(svg.Descendants().Any(element => element.Name.LocalName == "a" && (string?)element.Attribute("href") == "https://example.org"), "concise inline hyperlink");
        Require(svg.Descendants().Any(element => element.Name.LocalName == "tspan" && (string?)element.Attribute("font-size") == "24" && element.Value == "Working"), "large state text");
        Require(svg.Descendants().Any(element => (string?)element.Attribute("data-style-stereotype") == "important" && element.Descendants().Any(shape => (string?)shape.Attribute("stroke") == "red")), "stereotype waveform paint");
        var concise = svg.Descendants().First(element => (string?)element.Attribute("class") == "timing-state" && (string?)element.Attribute("data-style-name") == "concise");
        Require(concise.Elements().Any(element => element.Name.LocalName == "path" && (string?)element.Attribute("fill") == "#eef5ff"), "measured concise style background");
        Require((int)svg.Attribute("height")! > 300, "multiline style labels reserve vertical space");
        Require(svg.Descendants().Any(element => (string?)element.Attribute("data-style-name") == "timeline" && element.Descendants().Any(text => text.Name.LocalName == "text" && (string?)text.Attribute("font-size") == "20")), "measured axis typography");

        string tokens = "\uFEFF@startuml\r\nconcise \"Caption\" as C\r\n@0\r\nC is \"<b>One</b>\" : annotation\r\n@10\r\nC is Two\r\nC -> C@+1 : \"literal quotes\" tail\r\n@enduml\r\n";
        var fields = Render(tokens).Descendants().Where(element => element.Attribute("data-edit-start") != null).ToArray();
        string Slice(XElement field) => tokens.Substring((int)field.Attribute("data-edit-start")!, (int)field.Attribute("data-edit-length")!);
        Require(fields.Any(field => (string?)field.Attribute("data-edit-kind") == "quoted" && Slice(field) == "\"Caption\""), "quoted track source token");
        Require(fields.Any(field => (string?)field.Attribute("data-edit-kind") == "quoted" && Slice(field) == "\"<b>One</b>\"" && (string?)field.Attribute("data-edit-role") == "value"), "quoted rich state source token");
        Require(fields.Any(field => (string?)field.Attribute("data-edit-kind") == "text" && Slice(field) == "\"literal quotes\" tail"), "message tail remains literal text token");
        Render("@startuml\nclock \"<b>Clock</b>\" as C with period 4 <<important>>\n@10\nlegend right\n**Bold**\n<color:red>Red</color>\nendlegend\n@enduml");
        Render("@startuml\rconcise \"Multiline\\ntrack\" as C\r@0\rC is \"One\\nTwo\"\r@enduml");
        Render("@startuml\nrobust R\nlegend\n\nendlegend\n@enduml");

        var largeMessage = Render("@startuml\n<style>\ntimingDiagram { arrow { FontSize 32; MaximumWidth 150 } }\n</style>\nconcise C\n@0\nC is Waiting\n@10\nC is Received\nC -> C@+3 : **Very large**\\nthree line\\ncaption\nlegend\nSummary\nendlegend\n@enduml");
        var message = largeMessage.Descendants().Single(element => (string?)element.Attribute("class") == "timing-message");
        var stateText = largeMessage.Descendants().Where(element => (string?)element.Attribute("class") == "timing-state").SelectMany(element => element.Descendants().Where(child => child.Name.LocalName == "text")).ToArray();
        var messageText = message.Descendants().Where(element => element.Name.LocalName == "text").ToArray();
        Require(messageText.Min(text => (double)text.Attribute("y")!) > stateText.Max(text => (double)text.Attribute("y")!) + 20, "large self-message text occupies a measured band below state text");
        Require(message.Elements().Any(element => element.Name.LocalName == "path" && ((string?)element.Attribute("d"))?.Contains('V') == true), "self-message route uses its reserved band");
        var legend = largeMessage.Descendants().Single(element => (string?)element.Attribute("class") == "legend");
        Require((double)legend.Elements().First(element => element.Name.LocalName == "rect").Attribute("y")! > messageText.Max(text => (double)text.Attribute("y")!) + 10, "large message remains above legend");

        var analog = Render("@startuml\n<style>\ntimingDiagram { FontSize 22; analog { FontSize 32 } }\n</style>\nanalog \"Analog value\" as A\n@0\nA is -150\n@1\nA is 100\n@enduml");
        var analogText = analog.Descendants().Where(element => element.Name.LocalName == "text").ToArray();
        Require((double)analogText.Single(text => text.Value == "100.0").Attribute("y")! > (double)analogText.Single(text => text.Value == "Analog value").Attribute("y")! + 30, "analog upper value has measured header clearance");
    }

    private static XElement Render(string source)
    {
        Require(PlantUmlRenderer.TryRender(source, out var diagram) && diagram.Kind == DiagramKind.Timing, "expected measured native timing " + source);
        return XElement.Parse(diagram!.Svg);
    }
    private static void Require(bool valid, string label) { if (!valid) throw new InvalidOperationException("Timing styles: " + label); }
}
