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
        Render("start\nfork\n:First;\nfork again\n:Second;\nend fork\nstop");
        Render("start\nswitch (choice)\ncase (a)\n:Option A;\ncase (b)\nstop\nendswitch\n:Only from A;\nend");
        Render("repeat\n:Send;\nrepeat while (Retry?) is (yes) not (no)\nwhile (Continue?) is (yes)\n:Process;\nendwhile (no)\nstop");
        foreach (string unsupported in new[]{ "start\n:Missing terminator", "fork\n:X;\nend fork", "switch (q)\n:A;\nendswitch", "if (x) then\n:X;", "start\n!theme plain\n:Test;", ":**formatted**;", "start\nstop\n:Unreachable;" })
            if (PlantUmlRenderer.TryRender("@startuml\n" + unsupported + "\n@enduml", out _)) throw new InvalidOperationException("Expected activity fallback for " + unsupported);
    }

    private static XDocument Render(string body)
    {
        if (!PlantUmlRenderer.TryRender("@startuml\n"+body+"\n@enduml",out var diagram) || diagram.Kind.ToString() != "Activity")
            throw new InvalidOperationException("Activity rejected: " + body);
        return XDocument.Parse(diagram.Svg);
    }
}
