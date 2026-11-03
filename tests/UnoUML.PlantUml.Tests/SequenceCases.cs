using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class SequenceCases
{
    public static void Run()
    {
        Pages();
        SequenceLifecycleCases.Run();
        StyleCases.Sequence();
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
        var library = Render("@startuml\nparticipant A\n!include <C4/C4_Context>\n@enduml");
        Require(Groups(library).Any(group => (string?)group.Attribute("class") == "participant participant-head" &&
            (int?)group.Attribute("data-source-line") == 1), "standard-library initialization preserves the sequence participant source");
        foreach (var invalid in new[] { "skinparam sequenceUnknownThickness 3", "autonumber 1 1 \"[00]\"", "A-[#notacolor]> B", "alt missing end" })
            Require(!PlantUmlRenderer.TryRender("@startuml\nparticipant A\n" + invalid + "\n@enduml", out _), "whole-document fallback for " + invalid);
    }

    private static void Pages()
    {
        const string source = """
            @startuml
            title Checkout
            autonumber 5
            participant User
            User -> Billing: Request
            activate Billing #66CCEE
            alt accepted
            newpage Payment\nprocessing
            Billing -> Ledger: Record
            else declined
            Billing --> User: Declined
            end
            @newpage
            Billing --> User: Complete
            deactivate Billing
            newpage: Final sheet
            @enduml
            """;
        Require(SequencePages.TryRender(source.ReplaceLineEndings("\r\n"), out var stored) && stored.Count == 4, "separate sequence pages");
        var pages = stored!;
        Require(pages.Select(p => p.Title).SequenceEqual(["Checkout", "Payment\nprocessing", null, "Final sheet"]), "independent page titles");
        Require(pages.Select(p => (p.StartLine, p.EndLine)).SequenceEqual([(0,6), (7,11), (12,14), (15,16)]), "page ranges in original source");
        var combined = Render(source);
        var ns = combined.Name.Namespace;
        var nested = combined.Elements(ns + "svg").ToArray();
        Require(nested.Length == pages.Count && (string?)nested[1].Attribute("data-page-title") == "Payment\nprocessing", "combined nested sheets");
        Require(nested.Select(s => (int)s.Attribute("data-sequence-page")!).SequenceEqual([1,2,3,4]), "sheet navigation metadata");
        var ids = combined.Descendants().Attributes("id").Select(id => id.Value).ToArray();
        Require(ids.Distinct().Count() == ids.Length, "unique SVG identifiers across sheets");
        int priorBottom = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var single = XElement.Parse(page.Svg);
            var messages = Groups(single).Where(g => (string?)g.Attribute("class") == "message").ToArray();
            var heads = Groups(single).Where(g => (string?)g.Attribute("class") == "participant participant-head").ToArray();
            Require(heads.Select(h => (string?)h.Attribute("data-qualified-name")).SequenceEqual(["User","Billing","Ledger"]), "all sheets share declaration order, including later participants");
            var refs = heads.Select(h => (string)h.Attribute("data-entity-uid")!).ToHashSet(StringComparer.Ordinal);
            foreach (var m in messages)
                Require(refs.Contains((string)m.Attribute("data-entity-1")!) && refs.Contains((string)m.Attribute("data-entity-2")!), "sheet-local endpoint identities");
            Require((int)nested[i].Attribute("y")! >= priorBottom && (int)single.Attribute("height")! == page.Height, "sheet geometry");
            priorBottom = (int)nested[i].Attribute("y")! + page.Height;
            Require(single.Elements().Select(x => x.ToString()).SequenceEqual(nested[i].Elements().Select(x => x.ToString())), "individual export matches combined view");
        }
        Require(priorBottom == (int)combined.Attribute("height")!, "combined bounds include trailing blank sheet");
        Require(Groups(nested[1]).Where(g => (string?)g.Attribute("class") == "message").Select(m => m.Value).SequenceEqual(["6Record", "7Declined"]), "continued numbering and content");
        var reply = Groups(nested[2]).Single(g => (string?)g.Attribute("class") == "message");
        Require(reply.Value == "8Complete" && (int)reply.Attribute("data-source-line")! == 13, "continuation message line mapping");
        var bars = nested.Select(n => Groups(n).Where(g => (string?)g.Attribute("class") == "activation").ToList()).ToArray();
        Require(bars.Select(b => b.Count).SequenceEqual([1,1,1,0]) && bars.Take(3).All(b => (int)b[0].Attribute("data-source-line")! == 5 && (string?)b[0].Element(ns+"rect")?.Attribute("fill") == "#66CCEE"), "continued colored activation");
        Require(!Groups(nested[3]).Any(g => (string?)g.Attribute("class") == "message"), "empty trailing page preserved");
        const string ignored = "@startuml\nA -> B: one\nignore newpage\nnewpage skipped\nA -> B: two\n@enduml";
        Require(SequencePages.TryRender(ignored, out var one) && one.Count == 1 && !XElement.Parse(one[0].Svg).Elements(ns+"svg").Any(), "ignore newpage preserves single canvas");
        const string literal = "@startuml\nA -> B: one\nnote over A\nnewpage is text here\nend note\nnewpage\nignore newpage\nnewpage ignored\nA -> B: two\n@enduml";
        Require(SequencePages.TryRender(literal, out var two) && two.Count == 2 && XElement.Parse(two[0].Svg).Value.Contains("newpage is text here", StringComparison.Ordinal), "page directives follow parser scope and ignore takes effect prospectively");
        Require(SequencePages.TryRender("@startuml\nA -> B: test\nnewpage **styled**\n@enduml", out var styledPages) && styledPages.Count == 2 &&
            XElement.Parse(styledPages[1].Svg).Descendants(ns + "tspan").Any(t => t.Value == "styled" && (string?)t.Attribute("font-weight") == "bold"), "rich page title");
        Require(!SequencePages.TryRender("@startuml\nclass A\n@enduml", out _), "conservative page API");
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
