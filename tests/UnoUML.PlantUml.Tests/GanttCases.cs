using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class GanttCases
{
    public static void Run()
    {
        // Pinned TeaVM 1.2026.9 emits these same inclusive start/end/duration tuples.
        const string release = "Project starts 2026-01-05\n[Design] lasts 3 days\n[Build] starts at [Design]'s end\n[Build] lasts 5 days";
        var plan = Render(release);
        Task(plan, "Design", 2, "Design", "Jan 5", "Jan 7", "3 days");
        Task(plan, "Build", 3, "Build", "Jan 8", "Jan 12", "5 days");
        if (!plan.Descendants().Any(x => (string?)x.Attribute("class") == "dependency" && (string?)x.Attribute("data-source-line") == "3"))
            throw new InvalidOperationException("Missing source-backed dependency.");
        Task(Render(release + "\n[Review] happens 2026-01-06"), "Review", 5, "Review", "Jan 6", "Jan 6", "1 day");
        var relative = Render("[Draft] requires 3 days\n[Sign] happens at [Draft]'s end\n[Ship] starts at [Draft]'s end and lasts 1 week");
        Task(relative, "Sign", 2, "Sign", "Day 3", "Day 3", "1 day");
        Task(relative, "Ship", 3, "Ship", "Day 4", "Day 10", "7 days");
        const string leap = "Project starts 2024-02-28\n[First] as [A] lasts 2 days then [Next] lasts 2 days\n[A] is 50% completed";
        Task(Render(leap), "A", 2, "First", "Feb 28", "Feb 29", "2 days");
        Task(Render(leap), "Next", 2, "Next", "Mar 1", "Mar 2", "2 days");
        var linked = Render("[A] lasts 2 days\n[A] -> [B]\n[B] lasts 3 days");
        Task(linked, "B", 2, "B", "Day 3", "Day 5", "3 days");
        var finish = Render("Project starts 2026-01-05\n[A] ends 2026-01-09\n[A] lasts 2 days");
        Task(finish, "A", 2, "A", "Jan 8", "Jan 9", "2 days");
        foreach (var bad in new[]{ "[A] starts at [Missing]'s end", "sunday are closed\n[A] lasts 3 days", "Project starts 2026-02-30\n[A] lasts 3 days",
            "printscale weekly\n[A] lasts 3 days", "[A] lasts 2 days\n[B] starts at [A]'s end\n[A] starts at [B]'s end",
            "Project starts 2026-01-05\n[A] starts 2026-01-01", "[A] lasts 9999 weeks", "[A] links to [[https://example.org]]" })
            if (PlantUmlRenderer.TryRender("@startgantt\n" + bad + "\n@endgantt", out _))
                throw new InvalidOperationException("Unsupported schedule was accepted: " + bad);
    }

    private static XElement Render(string body)
    {
        if (!PlantUmlRenderer.TryRender("@startgantt\n" + body + "\n@endgantt", out var diagram))
            throw new InvalidOperationException("Missing native Gantt output: " + body);
        return XElement.Parse(diagram.Svg);
    }
    private static void Task(XElement svg, string id, int line, params string[] values)
    {
        var task = svg.Descendants().Single(e => (string?)e.Attribute("data-qualified-name") == id);
        if ((int?)task.Attribute("data-source-line") != line || !task.Elements().Where(e => e.Name.LocalName == "text").Select(e => e.Value).SequenceEqual(values))
            throw new InvalidOperationException("Gantt reference mismatch: " + id);
    }
}
