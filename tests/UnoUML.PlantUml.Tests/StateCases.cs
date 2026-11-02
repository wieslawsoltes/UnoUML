using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class StateCases
{
    public static void Run()
    {
        const string source = """
            @startuml
            [*] --> Idle
            state Working {
              [*] --> Waiting
              state Gate <<choice>>
              Waiting --> Gate : received
              Gate --> [*] : finish
            }
            Idle : entry / reset
            Idle --> Working : begin
            Working --> [*]
            @enduml
            """;
        if (!PlantUmlRenderer.TryRender(source, out var diagram))
            throw new InvalidOperationException("Compound state document not recognized.");
        var xml = XElement.Parse(diagram.Svg);
        var groups = xml.Descendants().Where(e => e.Name.LocalName == "g").ToArray();
        foreach (var id in new[] { "Idle", "Working", "Working.Gate", "Working.Waiting" })
            if (!groups.Any(e => (string?)e.Attribute("data-qualified-name") == id))
                throw new InvalidOperationException("Missing state " + id);
        if (groups.Count(e => (string?)e.Attribute("class") == "link") != 6 ||
            !xml.Descendants().Any(e => e.Name.LocalName == "text" && e.Value == "entry / reset"))
            throw new InvalidOperationException("State transitions or description missing.");
        foreach (var special in new[] { "choice", "fork", "join", "history", "history*", "start", "end" })
            if (!PlantUmlRenderer.TryRender($"@startuml\nstate A <<{special}>>\nA --> B\n@enduml", out _))
                throw new InvalidOperationException("Missing pseudostate " + special);
        foreach (var kind in new[] { "entryPoint", "exitPoint", "inputPin", "outputPin" })
        {
            foreach (bool horizontal in new[] { false, true })
            {
                string rank = horizontal ? "left to right direction\n" : "";
                string input = $"@startuml\n{rank}state Outer {{\nstate Port <<{kind}>>\nstate Inner\nPort --> Inner\n}}\nstate Other\nOther --> Port\n@enduml";
                if (!PlantUmlRenderer.TryRender(input, out var rendered)) throw new InvalidOperationException("State port was rejected: " + kind);
                var root = XElement.Parse(rendered.Svg);
                var owner = root.Descendants().Single(e => (string?)e.Attribute("data-qualified-name")=="Outer").Elements().First(e => e.Name.LocalName=="rect");
                var port = root.Descendants().Single(e => (string?)e.Attribute("data-qualified-name")=="Outer.Port");
                var point = port.Elements().Single(e => e.Name.LocalName == (kind.EndsWith("Pin",StringComparison.Ordinal) ? "rect" : "circle"));
                int cx = (int?)point.Attribute("cx") ?? (int)point.Attribute("x")!+6, cy = (int?)point.Attribute("cy") ?? (int)point.Attribute("y")!+6;
                bool incoming = kind is "entryPoint" or "inputPin";
                int ox=(int)owner.Attribute("x")!, oy=(int)owner.Attribute("y")!;
                bool attached = horizontal ? cx==ox+(incoming?0:(int)owner.Attribute("width")!) : cy==oy+(incoming?0:(int)owner.Attribute("height")!);
                if (!attached || (int?)port.Attribute("data-source-line")!=(horizontal?3:2) || !port.Elements().Any(e=>e.Name.LocalName=="text" && e.Value=="Port"))
                    throw new InvalidOperationException("Disconnected or uneditable port: "+kind);
                if (port.Elements().Any(e=>e.Name.LocalName=="path")!=(kind=="exitPoint")) throw new InvalidOperationException("Exit point cross mismatch");
            }
            if (PlantUmlRenderer.TryRender($"@startuml\nstate Draft\nstate Port <<{kind}>>\n@enduml",out _))
                throw new InvalidOperationException("Invalid top-level port accepted: "+kind);
        }
    }
}
