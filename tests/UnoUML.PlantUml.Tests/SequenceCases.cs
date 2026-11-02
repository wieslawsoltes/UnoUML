using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class SequenceCases
{
    public static void Run()
    {
        var sequence = Render("""
            @startuml
            title Bank transfer
            hide footbox
            skinparam shadowing false
            skinparam sequence {
             ArrowColor #435ECE
            }
            autonumber 7 2
            actor "Jo & Co" as Jo
            participant Service as "Payments"
            database Ledger #DAF5DD
            Jo -> Service: Transfer
            activate Service
            Service ->> Ledger: Commit
            Ledger --> Service: Receipt
            Service -> Service: Verify
            Service --> Jo: Done
            deactivate Service
            @enduml
            """);
        var groups = Groups(sequence).ToList();
        var heads = groups.Where(x => (string?)x.Attribute("class") == "participant participant-head").ToArray();
        Require(heads.Length == 3 && (string?)heads[0].Attribute("data-qualified-name") == "Jo", "declaration order and alias");
        Require(heads[0].Value == "Jo & Co" && heads[1].Value == "Payments", "quoted labels and XML escaping");
        Require((int?)heads[0].Attribute("data-source-line") == 8 && (int?)heads[1].Attribute("data-source-line") == 9, "source coordinates");
        Require(!groups.Any(g => ((string?)g.Attribute("class"))?.Contains("participant-tail") == true), "hide footbox");
        var messages = groups.Where(g => (string?)g.Attribute("class") == "message").ToArray();
        Require(messages.Length == 5 && (string?)messages[0].Attribute("data-entity-2") == "part2", "message hit metadata");
        Require(messages[0].Value == "7Transfer" && messages[^1].Value == "15Done", "numbered messages");
        Require(messages[2].Descendants().Any(x => x.Attribute("stroke-dasharray") != null), "return arrow dash");
        Require(messages[3].Elements().Any(x => x.Name.LocalName == "path"), "self message");
        Require(groups.Any(g => (string?)g.Attribute("class") == "activation" && (int?)g.Attribute("data-source-line") == 12), "activation source mapping");
        var reversed = Render("\n@startuml\r\nFirst <-- Second: Back\r\n@enduml");
        var reply = Groups(reversed).Single(x => (string?)x.Attribute("class") == "message");
        Require((string?)reply.Attribute("data-entity-1") == "part2" && (int?)reply.Attribute("data-source-line") == 2, "reverse and CRLF source map");

        var grouped = Render("""
            @startuml
            participant A
            participant B
            alt passed
              A -> B ++: Enter
              opt verify
                B -> B: Check
              end
              B --> A --: Return
            else failed
              note over A, B #F0F0FF
              Retry later
              Wait for signal
              end note
            end
            == Next phase ==
            @enduml
            """);
        Require(Groups(grouped).Count(x => (string?)x.Attribute("class") == "fragment") == 2, "nested fragments");
        Require(Groups(grouped).Single(x => (string?)x.Attribute("class") == "note").Value == "Retry laterWait for signal", "multiline note");

        var roles = Render("@startuml\n" + string.Join('\n', new[] { "participant", "actor", "boundary", "control", "entity", "database", "collections", "queue" }.Select((role, i) => role + " P" + i)) + "\n@enduml");
        Require(Groups(roles).Count(x => (string?)x.Attribute("class") == "participant participant-tail") == 8, "all lifeline heads and tails");
        foreach (var invalid in new[] { "!include <C4/C4_Context>", "skinparam sequenceArrowThickness 3", "autonumber 1 1 \"[00]\"", "A -> B : **bold**", "A-[#green]> B", "A -> B : [[https://example.com link]]", "newpage next", "alt missing end" })
            Require(!PlantUmlRenderer.TryRender("@startuml\nparticipant A\n" + invalid + "\n@enduml", out _), "whole-document fallback for " + invalid);
    }

    private static XElement Render(string text)
    {
        if (!PlantUmlRenderer.TryRender(text, out var diagram) || diagram.Kind != DiagramKind.Sequence)
            throw new Exception("Managed sequence renderer did not accept the test document: " + text);
        var svg = XElement.Parse(diagram.Svg);
        Require((int?)svg.Attribute("width") > 0 && (int?)svg.Attribute("height") > 0 && svg.Attribute("viewBox") != null, "SVG extent");
        return svg;
    }
    private static IEnumerable<XElement> Groups(XElement svg) => svg.Descendants().Where(x => x.Name.LocalName == "g");
    private static void Require(bool result, string context)
    {
        if (!result) throw new Exception("Sequence test failed: " + context);
    }
}
