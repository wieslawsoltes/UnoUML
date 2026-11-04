using System.Globalization;
using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class GanttAuthoringRendererCases
{
    private static int assertions;

    internal static int Run()
    {
        assertions = 0;
        RelativeDaysAndDates();
        AliasesAndExactFields();
        GeneratedSourceSafety();
        ForwardAndSharedStatements();
        DefaultAndBoundarySchedules();
        return assertions;
    }

    private static void RelativeDaysAndDates()
    {
        var dated = Render("@startgantt\nProject starts 2024-02-28\n[A] starts D+1 and ends D+3\n[B] happens D+4\n@endgantt");
        var first = Task(dated, "A"); var milestone = Task(dated, "B");
        Require(Integer(first, "start") == 1 && Integer(first, "duration") == 3 && (string?)first.Attribute("data-gantt-milestone") == "false",
            "relative days use inclusive calendar duration across a leap day");
        Require(Text(first).SequenceEqual(["A", "Feb 29", "Mar 2", "3 days"]), "relative dates and textual schedule share the same calendar model");
        Require(Integer(milestone, "start") == 4 && Integer(milestone, "duration") == 1 && (string?)milestone.Attribute("data-gantt-milestone") == "true",
            "milestones expose one-day authoring metadata");
        Require(Text(milestone).SequenceEqual(["B", "Mar 3", "Mar 3", "1 day"]), "milestone date text matches its day offset");
        var firstBar = first.Descendants().Single(e => (string?)e.Attribute("class") == "gantt-bar").Element(Ns + "rect")!;
        Require((int?)firstBar.Attribute("width") == 3 * Integer(first, "unit") - 4, "bar width is consistent with resize metadata");
        var relative = Task(Render("@startgantt\n[A] starts D+2 and lasts 2 days\n@endgantt"), "A");
        Require(Integer(relative, "start") == 2 && Text(relative).SequenceEqual(["A", "Day 3", "Day 4", "2 days"]), "undated schedules label zero-based offsets as one-based days");
        var constrained = Render("@startgantt\n[A] starts D+3 and lasts 2 days\n[B] starts at [A]'s start and lasts 1 day\n[M] happens at [A]'s end\n@endgantt");
        Require(Integer(Task(constrained, "B"), "start") == 3 && Integer(Task(constrained, "M"), "start") == 4, "start and end milestone constraints select their precise calendar point");
    }

    private static void AliasesAndExactFields()
    {
        const string caption = "Review & ship 🧩";
        foreach (string nl in new[] { "\n", "\r\n", "\r" })
            foreach (string bom in new[] { "", "\uFEFF" })
            {
                string source = bom + "@startgantt" + nl + "' heading comment" + nl +
                    $"[{caption}] as [First] lasts 2 days then [{caption}] as [Second] lasts 3 days ' keep row" + nl + "@endgantt";
                var svg = Render(source); var first = Task(svg, "First"); var second = Task(svg, "Second");
                Require(Integer(first, "start") == 0 && Integer(second, "start") == 2 && Integer(second, "duration") == 3,
                    "same-caption aliases retain separate scheduled identities");
                var firstField = Field(first); var secondField = Field(second);
                Require(Slice(source, firstField) == caption && Slice(source, secondField) == caption,
                    "label fields preserve original UTF-16 captions across BOM and line endings");
                Require((int?)firstField.Attribute("data-edit-start") == source.IndexOf(caption, StringComparison.Ordinal) &&
                    (int?)secondField.Attribute("data-edit-start") == source.LastIndexOf(caption, StringComparison.Ordinal),
                    "shared-row labels own distinct exact source spans");
                Require((int?)first.Attribute("data-source-line") == 2 && (int?)second.Attribute("data-source-line") == 2,
                    "shared-row tasks retain their physical source row");
                Require((string?)first.Attribute("data-qualified-name") == "First" && (string?)second.Attribute("data-qualified-name") == "Second",
                    "qualified canvas identities use aliases rather than repeated captions");
            }
        foreach (string name in new[] { "Director's review", "Review and approve then ship", "Review ' final" })
        {
            string source = "@startgantt\n[" + name + "] as [A] lasts 2 days\n[B] starts at [A]'s end and lasts 1 day\n@endgantt";
            var svg = Render(source);
            Require(Slice(source, Field(Task(svg, "A"))) == name && Text(Task(svg, "A"))[0] == name,
                "bracketed task names retain literal punctuation and conjunctions: " + name);
        }
    }

    private static void GeneratedSourceSafety()
    {
        const string source = "@startgantt\n[Plan /'comment-like'/ stage] as [A] lasts 2 days\n[B] starts at [A]'s end and lasts 1 day\n@endgantt";
        var svg = Render(source); var task = Task(svg, "A");
        Require(Text(task)[0] == "Plan  stage" && (string?)task.Attribute("data-source-generated") == "true",
            "preprocessing a caption marks its generated geometry explicitly");
        Require(!svg.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName.StartsWith("data-edit-", StringComparison.Ordinal)),
            "transformed source cannot expose incorrect original-source edit spans");
        Require((int?)task.Attribute("data-source-line") == 1 && Integer(Task(svg, "B"), "start") == 2,
            "preprocessing preserves physical source navigation and dependency scheduling");
    }

    private static void ForwardAndSharedStatements()
    {
        string forwardSource = "@startgantt\n[Same caption] as [B] starts at [A]'s end and lasts 3 days\n[Same caption] as [A] lasts 2 days\n@endgantt";
        var forward = Render(forwardSource); var b = Task(forward, "B"); var a = Task(forward, "A");
        Require(Integer(b, "start") == 2 && Integer(a, "start") == 0, "forward dependencies resolve aliases before scheduling");
        Require((int?)b.Attribute("data-source-line") == 1 && (int?)a.Attribute("data-source-line") == 2, "forward dependencies preserve source row order");
        Require(forward.Descendants().Any(e => (string?)e.Attribute("class") == "dependency" && (int?)e.Attribute("data-source-line") == 1), "dependency geometry identifies the actual constraint row");
        var arrowSource = "@startgantt\n[A] -> [B]\n@endgantt";
        var arrows = Render(arrowSource);
        Require(Integer(Task(arrows, "A"), "start") == 0 && Integer(Task(arrows, "B"), "start") == 1, "arrow-only tasks have deterministic default schedules");
        Require(Slice(arrowSource, Field(Task(arrows, "B"))) == "B" &&
            (int?)Field(Task(arrows, "B")).Attribute("data-edit-start") == arrowSource.IndexOf("[B]", StringComparison.Ordinal) + 1,
            "an implicit arrow target edits its target token");
        var laterSource = "@startgantt\n[A] -> [B]\n[B] lasts 4 days\n@endgantt";
        var later = Render(laterSource);
        Require(Integer(Task(later, "B"), "duration") == 4 && (int?)Task(later, "B").Attribute("data-source-line") == 1,
            "later contributions retain the task's original identity row");
        Require((int?)Field(Task(later, "B")).Attribute("data-edit-start") == laterSource.LastIndexOf("[B]", StringComparison.Ordinal) + 1,
            "task caption editing follows its exact declarative contribution");
        var chain = Render("@startgantt\n[A] lasts 2 days then [B] lasts 3 days\nthen [C] lasts 4 days\n@endgantt");
        Require(Integer(Task(chain, "B"), "start") == 2 && Integer(Task(chain, "C"), "start") == 5,
            "inline and following-row chains share inclusive scheduling");
    }

    private static void DefaultAndBoundarySchedules()
    {
        var defaultTask = Task(Render("@startgantt\n[A] is 50% completed\n@endgantt"), "A");
        Require(Integer(defaultTask, "start") == 0 && Integer(defaultTask, "duration") == 1, "property-only tasks use the documented one-day default");
        Require(defaultTask.Descendants().Count(e => e.Name.LocalName == "rect") == 2, "completion paints within the source-backed bar");
        var latest = Task(Render("@startgantt\nProject starts 9999-12-31\n[A] lasts 1 day\n@endgantt"), "A");
        Require(Text(latest).SequenceEqual(["A", "Dec 31", "Dec 31", "1 day"]), "maximum calendar date renders without overflow");
        var lastDay = Task(Render("@startgantt\n[A] starts D+1095 and lasts 1 day\n@endgantt"), "A");
        Require(Integer(lastDay, "start") == 1095, "last permitted relative day renders with finite metadata");
        foreach (string body in new[]
        {
            "[A] starts D+1096 and lasts 1 day", "Project starts 9999-12-31\n[A] lasts 2 days",
            "[A] starts at [Missing]'s end", "[A] starts at [B]'s end\n[B] starts at [A]'s end",
            "[A] lasts 2 days\n[A] starts D+0 and ends D+5", "[A] happens D+0 and is 10% completed",
            "[A] lasts 2 days\nsunday are closed", "[A] starts D+-1", "[A] lasts 0 days"
        })
            Require(!PlantUmlRenderer.TryRender("@startgantt\n" + body + "\n@endgantt", out _), "unsupported or inconsistent schedules do not expose editable native geometry: " + body);
        string source = "@startgantt\nProject starts 2024-02-28\n[A] starts D+1 and lasts 2 days\n@endgantt";
        string original = Render(source).ToString(SaveOptions.DisableFormatting);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            Require(Render(source).ToString(SaveOptions.DisableFormatting) == original, "timeline metadata and dates are invariant across process culture");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";
    private static XElement Task(XElement svg, string id) => svg.Descendants().Single(e => (string?)e.Attribute("data-gantt-id") == id);
    private static XElement Field(XElement task) => task.Descendants().SingleOrDefault(e => (string?)e.Attribute("data-edit-kind") == "gantt-label") ??
        throw new InvalidOperationException("Missing exact Gantt label field: " + task.ToString(SaveOptions.DisableFormatting));
    private static int Integer(XElement task, string name) => (int)task.Attribute("data-gantt-" + name)!;
    private static string[] Text(XElement task) => task.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToArray();
    private static string Slice(string source, XElement field) => source.Substring((int)field.Attribute("data-edit-start")!, (int)field.Attribute("data-edit-length")!);
    private static XElement Render(string source)
    {
        Require(PlantUmlRenderer.TryRender(source, out var diagram) && diagram.Kind == DiagramKind.Gantt, "native Gantt authoring expected: " + source);
        return XElement.Parse(diagram!.Svg);
    }
    private static void Require(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException("Gantt authoring renderer: " + message);
    }
}
