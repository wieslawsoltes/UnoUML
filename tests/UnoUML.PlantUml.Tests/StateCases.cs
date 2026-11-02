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
        const string unsupported = "@startuml\nstate Work {\n state Entry <<entryPoint>>\n}\n@enduml";
        if (PlantUmlRenderer.TryRender(unsupported, out _)) throw new InvalidOperationException("Unsupported composite port was accepted.");
    }
}
