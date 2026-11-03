using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class ActivityCases
{
    public static void Run()
    {
        const string split = "title Review\nstart\n:Prepare review;\nif (Approved?) then (yes)\n  :Publish;\nelse (no)\n  :Revise;\nendif\nstop";
        var doc = Render(split);
        string[] texts = doc.Descendants().Where(x => x.Name.LocalName == "text").Select(x => x.Value).ToArray();
        foreach (string text in new[] { "Review", "Prepare review", "Approved?", "yes", "no", "Publish", "Revise" })
            if (!texts.Contains(text)) throw new InvalidOperationException("Missing activity label " + text);
        var shapes = doc.Descendants().Where(e => (string?)e.Attribute("class") == "activity-Action").ToArray();
        if (shapes.Length != 3 || (int?)shapes[1].Attribute("data-source-line") != 5) throw new InvalidOperationException("Activity source mapping");
        if (doc.ToString() != Render(split).ToString()) throw new InvalidOperationException("Activity layout must be deterministic");
        var debugging = Render("!option debug\nstart\n:Inspect;\nif (Ready?) then (yes)\n:Run;\nendif\nstop");
        var debugBoxes = debugging.Descendants().Where(element => (string?)element.Attribute("class") == "activity-debug").ToArray();
        if (debugBoxes.Length != 2 || debugBoxes.Any(element => (string?)element.Attribute("stroke") != "#0000FF" || (int?)element.Attribute("stroke-width") != 2))
            throw new InvalidOperationException("Activity debug outlines must follow the action bounds");
        if (Render("!option debug false\nstart\n:Inspect;\nstop").Descendants().Any(element => (string?)element.Attribute("class") == "activity-debug"))
            throw new InvalidOperationException("Activity debug option must be render-local and disableable");
        Render("start\nfork\n:First;\nfork again\n:Second;\nend fork\nstop");
        Render("start\nswitch (choice)\ncase (a)\n:Option A;\ncase (b)\nstop\nendswitch\n:Only from A;\nend");
        Render("repeat\n:Send;\nrepeat while (Retry?) is (yes) not (no)\nwhile (Continue?) is (yes)\n:Process;\nendwhile (no)\nstop");
        var lanes = Render("|Author|\nstart\n:Write;\n|Reviewer|\n:Approve;\n|Author|\n:Publish;\nstop");
        var columns = Groups(lanes,"Swimlane").ToArray();
        if (columns.Length != 2 || (int?)columns[1].Attribute("data-source-line") != 4) throw new Exception("Lane identity/source lost");
        var actions = Groups(lanes,"Action").Select(g => (int)g.Elements().Single(e=>e.Name.LocalName=="text").Attribute("x")!).ToArray();
        if (actions.Length!=3 || actions[0] != actions[2] || actions[1] <= actions[0]) throw new Exception("Swimlane transitions do not follow column ownership");
        if (Groups(Render("|Solo|\nstart\n:Write;\nstop"),"Swimlane").Any()) throw new Exception("Lone swimlane header must be elided");
        var partition = Render("start\npartition \"Build phase\" {\n:Compile;\n}\nstop");
        var frame = Groups(partition,"Partition").Single();
        var bounds = frame.Elements().Single(e=>e.Name.LocalName=="rect");
        var child = Groups(partition,"Action").Single().Elements().Single(e=>e.Name.LocalName=="rect");
        if ((int)bounds.Attribute("x")! >= (int)child.Attribute("x")! || (int)bounds.Attribute("y")! >= (int)child.Attribute("y")! ||
            !frame.Value.Contains("Build phase") || (int?)frame.Attribute("data-source-line") != 2) throw new Exception("Partition must enclose the action and map to its declaration");
        var notes = Render("start\n:Short;\nstop\nlegend right\n  Line one\n  Line two\nendlegend");
        var legend = Groups(notes,"Legend").Single();
        if (legend.Descendants().Count(e=>e.Name.LocalName=="text") != 2 || (int?)legend.Attribute("data-source-line")!=4) throw new Exception("Plain legend lines and source identity");
        foreach (string unsupported in new[]{ "start\n:Missing terminator", "fork\n:X;\nend fork", "switch (q)\n:A;\nendswitch", "if (x) then\n:X;", "start\n!theme plain\n:Test;", ":**formatted**;", "start\nstop\n:Unreachable;", "|A|\nif (OK?) then\n|B|\n:X;\nendif", "partition \"Unclosed\" {\n:X;", "start\nlegend right\n|= styled |\nendlegend" })
            if (PlantUmlRenderer.TryRender("@startuml\n" + unsupported + "\n@enduml", out _)) throw new InvalidOperationException("Expected activity fallback for " + unsupported);
    }

    private static IEnumerable<XElement> Groups(XDocument document,string kind) =>
        document.Descendants().Where(e=>(string?)e.Attribute("class")=="activity-"+kind);

    private static XDocument Render(string body)
    {
        if (!PlantUmlRenderer.TryRender("@startuml\n"+body+"\n@enduml",out var diagram) || diagram.Kind.ToString() != "Activity")
            throw new InvalidOperationException("Activity rejected: " + body);
        return XDocument.Parse(diagram.Svg);
    }
}
