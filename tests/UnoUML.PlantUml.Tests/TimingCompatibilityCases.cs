using System.Globalization;
using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class TimingCompatibilityCases
{
    internal static void Run()
    {
        ParticipantTime();
        CalendarTime();
        Decorations();
        StatesAndAnalog();
        WaveformEndpoints();
        SourceFields();
        Boundaries();
        Console.WriteLine("Native timing time-expression, state, annotation and layout cases passed.");
    }

    private static void ParticipantTime()
    {
        var source = """
            @startuml
            robust "Browser" as WB
            concise "User" as WU
            WB is Initializing
            WU is Absent
            @WB
            0 is idle
            +200 is Processing
            +100 is Waiting
            WB@0 <-> @50 : {50 ms lag}
            @WU
            0 is Waiting
            +500 is ok
            @200 <-> @+150 : {150 ms}
            @enduml
            """;
        var svg = Render(source);
        Require(Groups(svg, "timing-initial-state").Count() == 2, "separate initial states before participant time");
        Require(Groups(svg, "timing-state").Count() == 5, "participant-oriented transitions");
        Require(Groups(svg, "timing-constraint").Count() == 2, "explicit and selected participant constraints");
        Require(svg.Value.Contains("Initializing") && svg.Value.Contains("Absent"), "initial labels retained at the left of time zero");
        var initial = Groups(svg, "timing-initial-state").Last();
        var waiting = Groups(svg, "timing-state").Single(group => group.Value == "Waiting");
        Require(FirstX(initial) < FirstX(waiting), "initial ribbon has its own measured width");

        string decimals = """
            @startuml
            manual time-axis
            scale 1 as 100 pixels
            binary "Enable" as EN
            @-.5 as :start
            EN is low
            @+.1
            EN is high
            @+.2 as :ready
            EN is low
            @:ready+.5
            EN is high
            EN@:start -> EN@:ready : exact anchors
            @enduml
            """;
        var exact = Render(decimals);
        Require(exact.Value.Contains("start") && exact.Value.Contains("ready"), "manual axis uses named anchors");
        var segments = Groups(exact, "timing-state").ToArray();
        Require(Near(FirstX(segments[1]) - FirstX(segments[0]), 10) &&
            Near(FirstX(segments[2]) - FirstX(segments[1]), 20), "exact fractional relative time and explicit scale");
        Reject("@startuml\nbinary EN\n@.1\nEN is low\n@+.2\nEN is high\n@.3\nEN is low\n@enduml", "decimal duplicate time is detected");

        var clock = Render("""
            @startuml
            scale 1 as 50 pixels
            clock "Clock" as clk with period 2 pulse .5 offset 1
            binary "Enable" as EN
            @clk*0
            EN is low
            @clk*1
            EN is high
            @clk*2
            EN is down
            @enduml
            """);
        var wave = Groups(clock, "timing-state").ToArray();
        Require(Near(FirstX(wave[1]) - FirstX(wave[0]), 100), "clock references use period independently of pulse and offset");
        Require(Near(FirstY(wave[0]), FirstY(wave[2])), "binary down state maps to low");
        var relative = Render("""
            @startuml
            binary B
            @100
            B is high
            B@10 <-> @+20 : relative to first endpoint
            B -> B@+20 : relative to current time
            @+30
            B is low
            @enduml
            """);
        Require(Groups(relative, "time").Any(group => group.Value == "130"), "decoration endpoints do not change current time");
        var reversed = Render("@startuml\nbinary B\n@0\nB is low\n@10\nB is high\nB@10 <-> @0 : reverse\n@enduml");
        Require(Groups(reversed, "timing-constraint").Single().Elements().Count(element => element.Name.LocalName == "path") == 2, "reversed constraint has both arrow heads");
    }

    private static void CalendarTime()
    {
        var dates = Render("""
            @startuml
            scale 1d as 60 pixels
            use date format "yyyy-MM-dd"
            concise "Season" as S
            @2024/02/28
            S is W
            @2024/02/29
            S is S
            @2024/03/01
            S is F
            @enduml
            """);
        Require(dates.Value.Contains("2024-02-28") && dates.Value.Contains("2024-03-01"), "date labels use the existing Java-compatible date formatter");
        var segments = Groups(dates, "timing-state").ToArray();
        Require(Near(FirstX(segments[1]) - FirstX(segments[0]), 60) &&
            Near(FirstX(segments[2]) - FirstX(segments[1]), 60), "leap day projects to a one-day interval");
        Require((int)dates.Attribute("width")! < 600, "calendar axis omits unrelated Unix zero");
        var hours = Render("@startuml\nscale 1m as 100 pixels\nbinary B\n@1:15:00\nB is low\n@1:16:30\nB is high\n@1:17:30\nB is low\n@enduml");
        Require(hours.Value.Contains("1:15:00"), "hour axis format");
        var hourSegments = Groups(hours, "timing-state").ToArray();
        Require(Near(FirstX(hourSegments[1]) - FirstX(hourSegments[0]), 150), "minute scale units");
        var years = Render("@startuml\nscale 1y as 80 pixels\nbinary B\n@2000/01/01\nB is low\n@2001/01/01\nB is high\n@enduml");
        var yearSegments = Groups(years, "timing-state").ToArray();
        Require(Near(FirstX(yearSegments[1]) - FirstX(yearSegments[0]), 80 * 366 / 365.25), "year scale is 365.25 days");
        var formatted = Render("@startuml\nmanual time-axis\nusedateformat\"yy 'year' MM\"\nconcise S\n@2025/01/01 as :launch\nS is Begin\n@2025/03/01\nS is Continue\n@enduml");
        Require(formatted.Value.Contains("launch") && formatted.Value.Contains("25 year 03"), "manual named ticks and date format literals");
        var hidden = Render("@startuml\nhide time axis\nbinary B\n@1:00:00\nB is low\n@1:01:00\nB is high\n@enduml");
        Require(!Groups(hidden, "time").Any(), "space-separated hidden axis command");
        var ordinaryDates = Render("@startuml\nbinary B\n@2025/01/02\nB is low\n@2025/01/03\nB is high\n@enduml");
        Require(ordinaryDates.Value.Contains("01/02") && ordinaryDates.Value.Contains("01/03"), "default date format");
        var negativeHours = Render("@startuml\nmanual time-axis\nbinary B\n@0:00:00\nB is low\n@+-60\nB is high\n@+-1\nB is low\n@enduml");
        Require(Groups(negativeHours, "time").Select(group => group.Value).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "0:-1:-1", "0:-1:00", "0:00:00" }.Order(StringComparer.Ordinal)),
            "negative clock components follow the pinned JVM sign-inclusive padding");
    }

    private static void Decorations()
    {
        string source = """
            @startuml
            <style>
            timingDiagram {
              constraintArrow { FontSize 18; LineColor blue; LineStyle 2-1 }
              note { FontSize 20; Padding 4; MaximumWidth 180 }
              highlight { FontSize 18; BackgroundColor #eef6ff }
            }
            </style>
            concise "Bus" as B
            binary "Enable" as EN
            @0 as :begin
            B is Waiting
            EN is low
            @20 as :end
            B is Ready #LightCyan;line:Aqua
            EN is high
            B@:begin <-> @:end : **Propagation** delay
            B@:begin <-[#red,dashed,bold]> @:end : Explicit stroke
            highlight :begin to :end #Gold;line:DimGrey : Transfer window
            note top of B : **Top note**\nsecond line
            note bottom of B
              Lower **note**
              second row
            end note
            note bottom of EN : Binary note
            B@0 -[#red,dotted]> EN@20 : Endpoint message
            @enduml
            """;
        var svg = Render(source);
        Require(Groups(svg, "timing-note").Count() == 3 && Groups(svg, "timing-highlight").Count() == 1, "measured notes and highlighted interval");
        var constraints = Groups(svg, "timing-constraint").ToArray();
        Require(constraints[0].Elements().Any(element => (string?)element.Attribute("stroke") == "blue"), "constraint-arrow style scope");
        Require(constraints[0].Elements().Where(element => element.Name.LocalName == "path").All(element => (string?)element.Attribute("fill") == "blue"), "constraint heads follow style color");
        Require(constraints[1].Elements().Any(element => (string?)element.Attribute("stroke") == "red" &&
            (string?)element.Attribute("stroke-dasharray") == "6 4" && (string?)element.Attribute("stroke-width") == "3"), "inline arrow style wins over global style");
        var ready = Groups(svg, "timing-state").Single(group => group.Value == "Ready");
        Require(ready.Elements().Any(element => (string?)element.Attribute("fill") == "lightcyan"), "state background color");
        Require(ready.Elements().Any(element => ((string?)element.Attribute("stroke"))?.Equals("Aqua", StringComparison.OrdinalIgnoreCase) == true), "state inline stroke color");
        var note = Groups(svg, "timing-note").First();
        var texts = note.Descendants().Where(element => element.Name.LocalName == "text").ToArray();
        Require(texts.Length >= 2 && texts.All(element => (string?)element.Attribute("font-size") == "20"), "notes use measured rich text");
        double noteTop = note.Elements().Where(element => element.Name.LocalName == "path").Select(FirstPathY).Min();
        Require(noteTop < FirstY(ready), "top note is above its state ribbon");
        Require(svg.Descendants().Any(element => (string?)element.Attribute("data-edit-kind") == "text" &&
            Slice(source, element) == "Lower **note**"), "multiline note row source mapping");
        var background = Groups(svg, "timing-highlight-background").Single();
        Require(background.Elements().Single().Attribute("height") is { } span && (double)span > 200, "highlight covers all participant rows");
        var message = Groups(svg, "timing-message").Single();
        Require(message.Elements().Any(element => (string?)element.Attribute("stroke") == "red" &&
            (string?)element.Attribute("stroke-dasharray") == "2 3"), "explicit source/end message times and style");
    }

    private static void StatesAndAnalog()
    {
        var robust = Render("""
            @startuml
            robust "Flow" as F
            F has "35 gpm" as high
            F has "15 gpm" as low
            F has "0 gpm" as none
            @0
            F is high
            @10
            F is {low,high} #SlateGrey
            @20
            F is none
            @30
            F is {hidden}
            @40
            F is low
            @enduml
            """);
        var labels = Groups(robust, "timing-state-label").ToDictionary(group => group.Value, group => group);
        Require(TextY(labels["35 gpm"]) < TextY(labels["15 gpm"]) && TextY(labels["15 gpm"]) < TextY(labels["0 gpm"]), "declared robust state order is top to bottom");
        Require(robust.Descendants().Any(element => element.Name.LocalName == "pattern"), "set-valued robust state uses bounded SVG hatching");
        var states = Groups(robust, "timing-state").ToArray();
        Require(!states[3].HasElements && states[2].Elements().Count(element => element.Name.LocalName == "line") == 1, "hidden interval removes waveform and incident vertical transition");
        Require(states.All(group => group.Attribute("data-edit-start") == null), "aliased state expressions do not expose misleading literal edits");
        var concise = Render("@startuml\nconcise C\n@0\nC is {-}\n@10\nC is Active\n@20\nC is {...}\n@30\nC is {hidden}\n@40\nC is Done\n@enduml");
        var ribbons = Groups(concise, "timing-state").ToArray();
        Require(ribbons[0].Elements().Single().Name.LocalName == "line" && ribbons[2].Value.Length == 0 &&
            ribbons[2].Elements().Any(element => element.Name.LocalName == "path") && !ribbons[3].HasElements, "flat, blank and hidden concise states");
        var binary = Render("@startuml\nbinary B\n@0\nB is low\n@10\nB is {low,high}\n@20\nB is high\n@enduml");
        Require(binary.Descendants().Any(element => element.Name.LocalName == "pattern"), "indeterminate binary interval");
        var rectangle = Render("@startuml\nrectangle R\n@0\nR is Starting\n@20\nR is Running\n@enduml");
        Require(Groups(rectangle, "timing-state").Count() == 2 && Groups(rectangle, "timing-state").All(group =>
            (string?)group.Attribute("data-style-name") == "rectangle"), "rectangle timing participant");
        var analog = Render("""
            @startuml
            analog "VCC" between -4.5 and 6.5 as V
            V ticks num on multiple 3
            V is 200 pixels height
            @0
            V is -4.5
            @10
            V is 6.5
            @enduml
            """);
        var values = Groups(analog, "timing-state").ToArray();
        Require(Near(FirstY(values[0]) - FirstY(values[1]), 200), "explicit analog range and measured height");
        Require(analog.Value.Contains("-3.0") && analog.Value.Contains("0.0") && analog.Value.Contains("6.0"), "analog labels at requested multiples");
        var baseline = Render("@startuml\nanalog \"A\" as A\n@0\nA is 350\n@100\nA is 450\n@enduml");
        Require(baseline.Value.Contains("0.0") && baseline.Value.Contains("450.0"), "automatic analog scale includes zero");
        var full = Render("@startuml\nrobust R\nconcise C\n@0\nR is Low\nC is Idle\n@10\nR is High\nC is Run\n@enduml");
        var compact = Render("@startuml\nmode compact\nrobust R\ncompact concise C\n@0\nR is Low\nC is Idle\n@10\nR is High\nC is Run\n@enduml");
        Require((int)compact.Attribute("height")! < (int)full.Attribute("height")!, "compact global and participant layouts");
    }

    private static void WaveformEndpoints()
    {
        // PlayerBinary converts every state other than high/1 to low, including
        // {hidden}. Both the waveform and a message endpoint must use that level.
        var binary = Render("""
            @startuml
            binary B
            binary C
            @0
            B is high
            C is high
            @10
            B is {hidden}
            C is low
            @20
            B is high
            B@15 -> C@15 : low-state message
            @enduml
            """);
        var binaryStates = Groups(binary, "timing-state").Take(3).ToArray();
        var transition = binaryStates[0].Elements().Where(element => element.Name.LocalName == "line").ToArray();
        Require(transition.Length == 2 && Near((double)transition[1].Attribute("y2")!, FirstY(binaryStates[1])),
            "binary hidden-as-low retains its incoming vertical transition");
        Require(Near(FirstY(Groups(binary, "timing-message").Single()), FirstY(binaryStates[1])),
            "binary hidden-as-low message attaches to the visible low segment");

        var analog = Render("""
            @startuml
            analog "A" as A
            binary B
            @0
            B is low
            @10
            A is 100
            @20
            A is 200
            A@5 -> B@5 : before first sample
            A@15 -> B@15 : interpolated sample
            @enduml
            """);
        var analogStates = Groups(analog, "timing-state").Where(group => (string?)group.Attribute("data-style-name") == "analog").ToArray();
        var analogMessages = Groups(analog, "timing-message").ToArray();
        Require(Near(FirstY(analogMessages[0]), FirstY(analogStates[0])),
            "analog message before the first sample attaches to its held initial value");
        Require(Near(FirstY(analogMessages[1]), (FirstY(analogStates[1]) + FirstY(analogStates[2])) / 2),
            "analog message between samples attaches to the interpolated waveform");

        var unsorted = Render("@startuml\nanalog \"A\" as A\nbinary B\n@0\nB is low\n@20\nA is 200\n@10\nA is 100\nA@5 -> B@5 : first declared value\n@enduml");
        var unsortedStates = Groups(unsorted, "timing-state").Where(group => (string?)group.Attribute("data-style-name") == "analog").ToArray();
        Require(Near(FirstY(unsortedStates[0]), FirstY(unsortedStates[2])) &&
            Near((double)unsortedStates[0].Elements().First().Attribute("y2")!, FirstY(unsortedStates[1])),
            "out-of-order analog samples retain the first source-declared initial value");
        Require(Near(FirstY(Groups(unsorted, "timing-message").Single()), (FirstY(unsortedStates[0]) + FirstY(unsortedStates[1])) / 2),
            "analog message follows interpolation before the earliest sorted sample");

        var initialized = Render("@startuml\nanalog \"A\" as A\nbinary B\nA is 50\n@30\nB is low\n@40\nA is 100\n@50\nA is 200\nA@35 -> B@35 : initial interpolation\n@enduml");
        var initial = Groups(initialized, "timing-initial-state").Single();
        var firstSample = Groups(initialized, "timing-state").First(group => (string?)group.Attribute("data-style-name") == "analog");
        Require(Near((double)initial.Elements().First().Attribute("y2")!, FirstY(firstSample)),
            "explicit analog initial value joins its first sample without a discontinuity");
        Require(Near(FirstY(Groups(initialized, "timing-message").Single()), (FirstY(initial) + FirstY(firstSample)) / 2),
            "analog initial interpolation uses the visible nonzero time origin");

        var emptyAnalog = Render("@startuml\nanalog \"A\" between -10 and 10 as A\nbinary B\n@0\nB is low\n@10\nB is high\nA@5 -> B@5 : no samples\n@enduml");
        Require(Near(FirstY(Groups(emptyAnalog, "timing-message").Single()),
            FirstY(Groups(emptyAnalog, "timing-state").First(group => (string?)group.Attribute("data-style-name") == "analog"))),
            "an analog track without samples anchors messages at its rendered zero value");
    }

    private static void SourceFields()
    {
        const string body = "@startuml\nconcise \"Caption\" as C\n@0 as :start\nC is \"Waiting\"\n@20\nC is Ready : Annotation\nC@:start <-> @20 : Constraint caption\nhighlight 0 to 20 : Highlight caption\nnote top of C : Note caption\nnote bottom of C\n  First row\n  Second row\nendnote\n@enduml";
        foreach (string ending in new[] { "\n", "\r\n", "\r" })
        {
            string source = "\uFEFF" + body.Replace("\n", ending, StringComparison.Ordinal) + ending;
            var svg = Render(source);
            var fields = svg.Descendants().Where(element => element.Attribute("data-edit-start") != null).ToArray();
            foreach (var field in fields)
            {
                int start = (int)field.Attribute("data-edit-start")!, length = (int)field.Attribute("data-edit-length")!;
                Require(start >= 0 && length > 0 && start + length <= source.Length, "field range lies in original source");
            }
            foreach (string token in new[] { "\"Caption\"", "\"Waiting\"", "Ready", "Annotation", "Constraint caption", "Highlight caption", "Note caption", "First row", "Second row" })
                Require(fields.Any(field => Slice(source, field) == token), "exact BOM/line-ending token " + token);
        }
        var mapping = Render("@startuml\nbinary B\n@0\nB is low\n@1\nB is high\n@enduml");
        Require(Groups(mapping, "timing-state").All(group => (string?)group.Attribute("data-edit-kind") == "timing-binary"), "binary wave segments expose typed state editing");
        const string numbers = "@startuml\nanalog \"A\" as A\n@0\nA is \"1e2\"\n@1\nA is 2e2\n@enduml";
        var numeric = Render(numbers);
        Require(Groups(numeric, "timing-state").All(group => (string?)group.Attribute("data-edit-kind") == "number"), "quoted and bare analog states use numeric editing");
        Require(Groups(numeric, "timing-state").Select(group => Slice(numbers, group)).SequenceEqual(new[] { "1e2", "2e2" }), "quoted numeric fields target only the inner scalar");
    }

    private static void Boundaries()
    {
        foreach (string command in new[]
        {
            "@:unknown", "@missing*2", "scale 0 as 50 pixels", "scale 1 as 0 pixels",
            "scale 1fortnight as 50 pixels", "use date format \"quarter q\"",
            "@2025/02/29", "@1500/01/01", "B is {low,high,other}", "B ticks every 0",
            "highlight 10 to 5 : reversed", "highlight 0 to 5 #NotAColor : invalid color",
            "B@0 <-[invisible]> @10 : unsupported stroke", "note top of Unknown : note"
        })
            Reject("@startuml\nbinary B\n@0\nB is low\n" + command + "\n@enduml", "invalid command " + command);
        Reject("@startuml\nconcise C\n@0\nC is Wait\nnote top of C\nunfinished\n@enduml", "unterminated note");
        Reject("@startuml\nclock c with period .00001\n@0\n@100\n@enduml", "bounded clock output");
        Reject("@startuml\nclock c with period .0000000000000000000000000001\n@0\n@100\n@enduml", "smallest decimal clock stays bounded");
        Reject("@startuml\nscale 1 as 10 pixels\nbinary B\n@0\nB is low\n@100000\nB is high\n@enduml", "bounded explicit scale");
        Reject("@startuml\nanalog \"A\" between 4 and 3 as A\n@enduml", "invalid analog bounds");
        Reject("@startuml\nanalog \"A\" between 0 and 1 as A\n@0\nA is 2\n@enduml", "unsupported out-of-range analog samples");
        Reject("@startuml\nrobust R\n@0\nR is A\nnote top of R : unavailable upstream\n@enduml", "notes require a supported participant");
        var plain = Render("@startuml\nbinary B\n@-10\nB is high\n@-5\nB is low\n@enduml");
        Require(plain.Value.Contains("-10") && plain.Value.Contains("-5"), "negative-only range");
    }

    private static IEnumerable<XElement> Groups(XElement svg, string name) =>
        svg.Descendants().Where(element => (string?)element.Attribute("class") == name);
    private static string Slice(string source, XElement field) =>
        field.Attribute("data-edit-start") == null ? "" : source.Substring((int)field.Attribute("data-edit-start")!, (int)field.Attribute("data-edit-length")!);
    private static double FirstX(XElement group)
    {
        var shape = group.Elements().First(element => element.Name.LocalName is "line" or "path");
        if (shape.Attribute("x1") is { } value) return (double)value;
        return double.Parse(((string)shape.Attribute("d")!)[1..].Split(' ')[0], CultureInfo.InvariantCulture);
    }
    private static double FirstY(XElement group)
    {
        var shape = group.Elements().First(element => element.Name.LocalName is "line" or "path");
        return shape.Attribute("y1") is { } value ? (double)value : FirstPathY(shape);
    }
    private static double FirstPathY(XElement path)
    {
        string value = ((string)path.Attribute("d")!).Split(' ')[1];
        int end = 0;
        while (end < value.Length && value[end] is >= '0' and <= '9' or '.' or '-') end++;
        return double.Parse(value[..end], CultureInfo.InvariantCulture);
    }
    private static double TextY(XElement group) => (double)group.Descendants().First(element => element.Name.LocalName == "text").Attribute("y")!;
    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .002;
    private static XElement Render(string source)
    {
        Require(PlantUmlRenderer.TryRender(source, out var diagram) && diagram.Kind == DiagramKind.Timing, "expected native timing " + source);
        var svg = XElement.Parse(diagram!.Svg);
        Require(!diagram.Svg.Contains("NaN", StringComparison.Ordinal) && !diagram.Svg.Contains("Infinity", StringComparison.Ordinal), "finite timing geometry");
        return svg;
    }
    private static void Reject(string source, string label) => Require(!PlantUmlRenderer.TryRender(source, out _), label);
    private static void Require(bool valid, string label) { if (!valid) throw new InvalidOperationException("Timing compatibility: " + label); }
}
