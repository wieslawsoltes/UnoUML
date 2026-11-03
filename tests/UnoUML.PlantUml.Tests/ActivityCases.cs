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
        var columns = Groups(lanes, "Swimlane").ToArray();
        if (columns.Length != 2 || (int?)columns[1].Attribute("data-source-line") != 4) throw new Exception("Lane identity/source lost");
        var actions = Groups(lanes, "Action").Select(g => (int)g.Elements().Single(e => e.Name.LocalName == "text").Attribute("x")!).ToArray();
        if (actions.Length != 3 || actions[0] != actions[2] || actions[1] <= actions[0]) throw new Exception("Swimlane transitions do not follow column ownership");
        if (Groups(Render("|Solo|\nstart\n:Write;\nstop"), "Swimlane").Any()) throw new Exception("Lone swimlane header must be elided");
        var partition = Render("start\npartition \"Build phase\" {\n:Compile;\n}\nstop");
        var frame = Groups(partition, "Partition").Single();
        var bounds = frame.Elements().Single(e => e.Name.LocalName == "rect");
        var child = Groups(partition, "Action").Single().Elements().Single(e => e.Name.LocalName == "rect");
        if ((int)bounds.Attribute("x")! >= (int)child.Attribute("x")! || (int)bounds.Attribute("y")! >= (int)child.Attribute("y")! ||
            !frame.Value.Contains("Build phase") || (int?)frame.Attribute("data-source-line") != 2) throw new Exception("Partition must enclose the action and map to its declaration");
        var notes = Render("start\n:Short;\nstop\nlegend right\n  Line one\n  Line two\nendlegend");
        var legend = Groups(notes, "Legend").Single();
        if (legend.Descendants().Count(e => e.Name.LocalName == "text") != 2 || (int?)legend.Attribute("data-source-line") != 4) throw new Exception("Plain legend lines and source identity");
        var styled = Render("""
            <style>
            activityDiagram {
              activity { FontSize 24; Padding 12; MaximumWidth 180; BackgroundColor #123456; FontColor automatic }
              diamond { FontSize 20; MaximumWidth 170; Padding 6 }
              arrow { FontSize 16 }
              composite { FontSize 22 }
              swimlane { FontSize 23 }
              legend { FontSize 19; MaximumWidth 200; Padding 10 }
            }
            title { FontSize 24; Padding 6 }
            </style>
            title **Measured activity**
            start
            partition "<b>Review phase</b>" {
            :**Prepare** a detailed\nreview <math>sum_(i=0)^n x_i</math>;
            if (<size:28>Ready?</size>) then (**yes**)
            :<color:green>Publish</color>;
            else (//no//)
            :Revise;
            endif
            }
            stop
            legend
            **Rich** legend text that wraps over several lines
            endlegend
            """);
        var action = Groups(styled, "Action").First();
        var actionBox = action.Elements().First(e => e.Name.LocalName == "rect");
        if ((int)actionBox.Attribute("height")! <= 70 || !action.Descendants().Any(e => e.Attribute("data-sprite")?.Value.StartsWith("$math/", StringComparison.Ordinal) == true) ||
            action.Descendants().Where(e => e.Name.LocalName == "text").Any(e => e.Attribute("fill")?.Value == "automatic")) throw new Exception("Activity rich text and measured style geometry");
        Render("<style>\nactivityDiagram { diamond { FontSize 25; Padding 7 } arrow { FontSize 20 } }\n</style>\nrepeat\n:**Send**;\nrepeat while (<b>Retry?</b>) is (yes) not (no)\nwhile (Continue?) is (yes)\n:Process;\nendwhile (no)\nstop");
        Render("<style>\nswimlane { FontSize 28; Padding 8 }\n</style>\n|**Author**|\nstart\n:Write;\n|<size:32>Reviewer</size>|\n:Approve;\nstop");
        var multiline = Render("start\n#lightblue:First line\n**Second line**; <<procedure>> <<important>>\nstop");
        var procedure = Groups(multiline, "Action").Single();
        if ((int?)procedure.Attribute("data-source-line") != 2 || !procedure.Value.Contains("Second line") ||
            !procedure.Descendants().Any(e => (string?)e.Attribute("fill") == "lightblue") ||
            !procedure.Descendants().Any(e => e.Attribute("data-activity-shape")?.Value == "procedure")) throw new Exception("Multiline colored activity and procedure stereotype");
        const string physical = "\ufeff@startuml\r\nstart\r\n  #lightblue: First line\r\n    **Second line**  ; <<procedure>> <<important>>\r\nstop\r\n@enduml";
        if (!PlantUmlRenderer.TryRender(physical, out var physicalResult)) throw new Exception("Physical action span fixture");
        var actionField = Groups(XDocument.Parse(physicalResult.Svg), "Action").Single();
        int fieldStart = (int)actionField.Attribute("data-edit-start")!, fieldLength = (int)actionField.Attribute("data-edit-length")!;
        if ((string?)actionField.Attribute("data-edit-kind") != "activity-label" || physical.Substring(fieldStart, fieldLength) != "First line\r\n    **Second line**" ||
            fieldStart != physical.IndexOf("First line", StringComparison.Ordinal)) throw new Exception("Activity exact BOM and CRLF token span must exclude color, spacing and stereotype suffix");
        foreach (string shape in new[] { "input", "output", "load", "save", "continuous", "task", "object", "objectSignal", "trigger", "sendSignal", "acceptEvent" })
            Render("start\n:Styled action; <<" + shape + ">>\nstop");
        foreach (string unsupported in new[] { "start\n:Missing terminator", "fork\n:X;\nend fork", "switch (q)\n:A;\nendswitch", "if (x) then\n:X;", "start\n!theme plain\n:Test;", "start\nstop\n:Unreachable;", "|A|\nif (OK?) then\n|B|\n:X;\nendif", "partition \"Unclosed\" {\n:X;", "start\nlegend right\n|= styled |\nendlegend" })
            if (PlantUmlRenderer.TryRender("@startuml\n" + unsupported + "\n@enduml", out _)) throw new InvalidOperationException("Expected activity fallback for " + unsupported);
    }

    private static IEnumerable<XElement> Groups(XDocument document, string kind) =>
        document.Descendants().Where(e => (string?)e.Attribute("class") == "activity-" + kind);

    private static XDocument Render(string body)
    {
        if (!PlantUmlRenderer.TryRender("@startuml\n" + body + "\n@enduml", out var diagram) || diagram.Kind.ToString() != "Activity")
            throw new InvalidOperationException("Activity rejected: " + body);
        return XDocument.Parse(diagram.Svg);
    }
}
