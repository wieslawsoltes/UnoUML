using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class SequenceLifecycleCases
{
    public static void Run()
    {
        var references = Render("""
            @startuml
            participant Alice
            actor Bob
            ref over Alice, Bob : init
            Alice -> Bob : hello
            ref#LightBlue over Bob
              This can be on
              several lines
            end ref
            ref over "Other, service", Alice, Bob : multiple
            @enduml
            """);
        var frames = Groups(references, "reference");
        Require(frames.Length == 3 && frames.Select(g => g.Value).SequenceEqual(["refinit", "refThis can be onseveral lines", "refmultiple"]), "single-line, multiline and multi-participant references");
        Require(frames.Select(g => (int?)g.Attribute("data-source-line")).SequenceEqual([3, 5, 9]), "reference source lines");
        Require(frames[1].Elements().Any(e => (string?)e.Attribute("fill") == "lightblue"), "reference header color");

        var explicitActivation = Render("""
            @startuml
            Bob -> Alice : hello
            activate Alice #ABCDEF
            Alice -> Alice : some action
            return bye
            return again
            @enduml
            """);
        var replies = Groups(explicitActivation, "message");
        Require(replies.Select(m => m.Value).SequenceEqual(["hello", "some action", "bye", "again"]), "return labels");
        Require(Endpoints(replies[2]) == ("part2", "part1") && Endpoints(replies[3]) == ("part1", "part2"), "return uses activation then reverses last message");
        Require(replies[2].Descendants().Any(e => e.Attribute("stroke-dasharray") != null), "return arrow is dashed");
        Require(Groups(explicitActivation, "activation").Length == 1, "explicit activation returns once");

        var automatic = Render("""
            @startuml
            autoactivate on
            autonumber
            A -> B : call
            B -> C : work
            return result
            B --> A : finished
            A -> A #005500 : self
            return ack
            autoactivate off
            A -> C : untracked
            @enduml
            """);
        Require(Groups(automatic, "message").Select(m => m.Value).SequenceEqual(["1call", "2work", "3result", "4finished", "5self", "6ack", "7untracked"]), "automatic replies retain autonumber");
        Require(Groups(automatic, "activation").Length == 3 && Groups(automatic, "activation").Any(g => g.Elements().Any(e => (string?)e.Attribute("fill") == "#005500")), "automatic activation, self call, dotted reply and switch off");

        var shorthand = Render("""
            @startuml
            A -> B ++ #00FF00 : enter
            B -> C --++ #FF0000 : transfer
            C -> Worker ** : create
            ref over C, Worker : run
            C -> Worker !! : stop
            return result
            @enduml
            """);
        Require(Groups(shorthand, "activation").Length == 2 && Groups(shorthand, "destruction").Length == 1, "mixed activation and lifecycle shortcuts");
        var worker = shorthand.Descendants().Single(g => (string?)g.Attribute("data-qualified-name") == "Worker" && (string?)g.Attribute("class") == "participant participant-head");
        var creation = Groups(shorthand, "message").Single(g => g.Value == "create");
        var rect = worker.Elements().First(e => e.Name.LocalName == "rect");
        var arrow = creation.Elements().First(e => e.Name.LocalName == "line");
        Require((int)rect.Attribute("y")! < (int)arrow.Attribute("y1")! && (int)rect.Attribute("y")! + (int)rect.Attribute("height")! > (int)arrow.Attribute("y1")!, "creation arrow aligns with deferred participant head");
        Require((int)arrow.Attribute("x2")! == (int)rect.Attribute("x")!, "creation arrow ends at participant border");

        const string pages = """
            @startuml
            title Login
            autonumber
            participant Client
            participant Gateway
            Client -> Gateway ++ : request
            create participant "Worker service" as Worker
            Gateway -> Worker ++ : spawn
            ref over Gateway, Worker : validate
            newpage Phase two
            Worker -> Worker ++ : local
            return self done
            return response
            destroy Worker
            return confirmation
            newpage Finished
            Client -> Gateway : next
            @enduml
            """;
        Require(SequencePages.TryRender(pages, out var sheets) && sheets.Count == 3, "lifecycle across sequence pages");
        var first = XElement.Parse(sheets![0].Svg);
        var second = XElement.Parse(sheets[1].Svg);
        var third = XElement.Parse(sheets[2].Svg);
        Require(Groups(first, "participant participant-head").Select(h => h.Value).SequenceEqual(["Client", "Gateway", "Worker service"]) && Groups(second, "participant participant-head").Length == 2 && Groups(third, "participant participant-head").Length == 2, "created head appears only on creation page");
        Require(new[] { first, second, third }.All(s => Groups(s, "participant participant-tail").Length == 3), "created participant tails persist on all pages");
        Require(Groups(second, "message").Select(m => m.Value).SequenceEqual(["3local", "4self done", "5response", "6confirmation"]), "nested return stack survives newpage");
        Require(Endpoints(Groups(second, "message")[2]) == ("p2-part3", "p2-part2") && Endpoints(Groups(second, "message")[3]) == ("p2-part2", "p2-part1"), "returns preserve page-local endpoint identities");
        Require(Groups(second, "destruction").Single().Attribute("data-source-line")?.Value == "13", "destruction source line survives pagination");
        Require(Groups(third, "participant-lifeline").Single(g => (string?)g.Attribute("data-qualified-name") == "Worker").Elements().Count() == 0, "destroyed lifeline does not reappear on later pages");
        Require(Groups(second, "activation").Count(g => (int?)g.Attribute("data-source-line") == 7) == 1, "activation is clipped and keeps its initiating line");

        var createdActor = Render("@startuml\nparticipant Client\ncreate actor Service\nClient -> Service: spawn\nactivate Service\nService --> Client: done\ndestroy Service\n@enduml");
        Require(Groups(createdActor, "participant participant-head").Single(g => g.Value == "Service").Descendants().Any(e => e.Name.LocalName == "circle"), "typed participant creation");
        var standalone = Render("@startuml\nA -> B: ready\nA -> A: thinking\ndestroy B\n@enduml");
        Require(Groups(standalone, "destruction").Length == 1, "standalone destruction after unrelated message");
        var coloredReturn = Render("@startuml\nA -[#blue]>> B ++: call\nreturn #red reply\n@enduml");
        var returnMessage = Groups(coloredReturn, "message")[1];
        Require(returnMessage.Elements().Any(e => (string?)e.Attribute("stroke") == "red") && returnMessage.Elements().Last(e => e.Name.LocalName == "path").Attribute("fill")?.Value == "none", "return overrides color and retains asynchronous arrow head");

        const string generatedPages = """
            @startuml checkout.svg
            !$caption = "Payment"
            !include actors.iuml
            A -> B: Request
            newpage $caption
            !procedure $reply()
            B --> A: Response
            !endprocedure
            $reply()
            @enduml
            """;
        string? Include(string path) => path == "actors.iuml" ? "participant A\nparticipant B" : null;
        Require(SequencePages.TryRender("\uFEFF" + generatedPages.ReplaceLineEndings("\r\n"), Include, out var generated) && generated.Count == 2, "page API shares BOM, newline, filename, variable and local-include preprocessing");
        Require(generated![1].Title == "Payment" && generated[1].StartLine == 4 && generated[1].EndLine == 9, "generated page title and source range");
        var generatedSvg = XElement.Parse(generated[1].Svg);
        var generatedReply = Groups(generatedSvg, "message").Single();
        Require((int?)generatedReply.Attribute("data-source-line") == 8 && (string?)generatedReply.Attribute("data-source-generated") == "true", "generated reply maps to its procedure invocation");
        Require(Groups(generatedSvg, "participant participant-head").All(g => (int?)g.Attribute("data-source-line") == 2 && (string?)g.Attribute("data-source-file") == "actors.iuml"), "included participants keep file origins on every page");
        Require(!SequencePages.TryRender(generatedPages, out _) && !SequencePages.TryRender("@startjson\n{}\n@endjson", out _), "page API declines missing includes and other dialects");

        foreach (string invalid in new[]
        {
            "return orphan", "ref over A\nmissing closing marker", "create B\nA -> C: wrong target", "create B",
            "A -> B **: create\nA -> B **: duplicate", "A -> B: call\ndestroy B\nB -> A: after destruction",
            "A -> B: call\n...\nactivate B", "A -> B ++ #unknowncolor: call", "ref#unknowncolor over A: body",
            "A -> B: call\nref over A [[https://example.com]]: link"
        })
            Require(!PlantUmlRenderer.TryRender("@startuml\n" + invalid + "\n@enduml", out _), "conservative lifecycle fallback for " + invalid);
    }

    private static XElement Render(string source)
    {
        if (!PlantUmlRenderer.TryRender(source, out var diagram) || diagram.Kind != DiagramKind.Sequence)
            throw new Exception("Sequence lifecycle source was rejected: " + source);
        return XElement.Parse(diagram.Svg);
    }
    private static XElement[] Groups(XElement svg, string kind) => svg.Descendants().Where(g => (string?)g.Attribute("class") == kind).ToArray();
    private static (string?, string?) Endpoints(XElement message) => ((string?)message.Attribute("data-entity-1"), (string?)message.Attribute("data-entity-2"));
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new Exception("Sequence lifecycle test failed: " + description);
    }
}
