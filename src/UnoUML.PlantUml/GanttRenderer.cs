using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>PlantUML calendar-day Gantt scheduling and source-addressable vector output.</summary>
internal static partial class GanttRenderer
{
    private sealed class TaskRow(string id, string caption, int line)
    {
        public readonly string Id = id, Caption = caption;
        public readonly int Line = line;
        public int? Duration, StartAt, EndAt;
        public int DurationLine = line, StartLine = line, EndLine = line;
        public int Start, End;
        public bool Milestone;
        public TaskRow? Predecessor;
        public bool PredecessorEnd;
        public int Percent = -1;
        public string Fill = "#a0b9e7";
    }

    private sealed class Schedule
    {
        public readonly Dictionary<string, TaskRow> Names = new(StringComparer.Ordinal);
        public readonly List<TaskRow> Rows = [];
        public DateOnly? Epoch;
        public string Title = "";
        public bool Footer = true;
    }

    [GeneratedRegex(@"^\[([^\[\]]+)\](?: as \[([^\[\]]+)\])? (.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Subject();
    [GeneratedRegex(@"^(?:lasts|requires) ([1-9][0-9]{0,3}) (day|week)s?$", RegexOptions.CultureInvariant)]
    private static partial Regex Duration();
    [GeneratedRegex(@"^(starts|ends|happens)(?: on| at| the)? (\d{4}[-/]\d{2}[-/]\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex FixedDate();
    [GeneratedRegex(@"^(starts|happens) at \[([^\[\]]+)\]'s (start|end)$", RegexOptions.CultureInvariant)]
    private static partial Regex Relative();
    [GeneratedRegex(@"^is (\d{1,3})% (?:completed|complete)$", RegexOptions.CultureInvariant)]
    private static partial Regex Progress();
    [GeneratedRegex(@"^is colored in (#[A-Fa-f0-9]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex Color();

    internal static string? TryRender(string source)
    {
        if (source.Length > 524288) return null;
        var chart = new Schedule();
        bool opened = false, closed = false;
        int line = -1;
        TaskRow? previous = null;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var span = raw.Trim();
            if (span.IsEmpty || span[0] == '\'') continue;
            if (span.Length > 3000 || closed) return null;
            if (!opened)
            {
                if (!span.SequenceEqual("@startgantt")) return null;
                opened = true; continue;
            }
            if (span.SequenceEqual("@endgantt")) { closed = true; continue; }
            if (span.SequenceEqual("hide footbox")) { chart.Footer = false; continue; }
            var text = span.ToString();
            if (text.StartsWith("title ", StringComparison.Ordinal) && chart.Title.Length == 0)
            {
                chart.Title = text[6..].Trim();
                if (!Plain(chart.Title)) return null;
                continue;
            }
            if (text.StartsWith("Project ", StringComparison.Ordinal))
            {
                var m = FixedDate().Match(text[8..]);
                if (chart.Epoch != null || chart.Rows.Count > 0 || !m.Success || m.Groups[1].Value != "starts" || !Date(m.Groups[2].Value, out var d)) return null;
                chart.Epoch = DateOnly.FromDayNumber(d); continue;
            }
            bool then = text.StartsWith("then [", StringComparison.Ordinal);
            if (then) text = text[5..];
            int offset = 0;
            do
            {
                int next = text.IndexOf(" then [", offset, StringComparison.Ordinal);
                string sentence = next < 0 ? text[offset..] : text[offset..next];
                if (!Statement(chart, sentence, line, then, ref previous)) return null;
                if (next < 0) break;
                offset = next + 6; then = true;
            } while (offset < text.Length);
        }
        if (!closed || chart.Rows.Count == 0 || !ScheduleDates(chart)) return null;
        return Draw(chart);
    }

    private static bool Plain(string text) => text.Length is > 0 and <= 450 && SvgText.IsPlain(text) && !text.Any(char.IsControl);
    private static bool Date(string value, out int day)
    {
        bool ok = DateOnly.TryParseExact(value.Replace('/', '-'), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        day = date.DayNumber; return ok;
    }

    private static TaskRow? Task(Schedule chart, string name, string alias, int line)
    {
        if (!Plain(name) || alias.Length != 0 && !Plain(alias)) return null;
        if (chart.Names.TryGetValue(name, out var task))
            return alias.Length == 0 || task.Id == alias ? task : null;
        if (chart.Rows.Count >= 760 || alias.Length > 0 && chart.Names.ContainsKey(alias)) return null;
        var row = new TaskRow(alias.Length > 0 ? alias : name, name, line);
        chart.Rows.Add(row); chart.Names[name] = row;
        if (alias.Length != 0) chart.Names[alias] = row;
        return row;
    }

    private static bool Statement(Schedule chart, string text, int line, bool chained, ref TaskRow? previous)
    {
        var subject = Subject().Match(text);
        if (!subject.Success) return false;
        var task = Task(chart, subject.Groups[1].Value, subject.Groups[2].Value, line);
        if (task == null) return false;
        if (chained && (previous == null || !Depends(task, previous, true, line))) return false;
        var clause = subject.Groups[3].Value;
        if (clause.StartsWith("-> [", StringComparison.Ordinal) && clause.EndsWith(']') && !clause[4..^1].Contains(']'))
        {
            var other = Task(chart, clause[4..^1], "", line);
            if (other == null || !Depends(other, task, true, line)) return false;
            previous = other; return true;
        }
        // Connectives inside bracketed task names are part of the identifier.
        int start = 0;
        for (int i = 0; i < clause.Length; i++)
        {
            if (clause[i] == '[') { i = clause.IndexOf(']', i + 1); if (i < 0) return false; continue; }
            if (!clause.AsSpan(i).StartsWith(" and ", StringComparison.Ordinal)) continue;
            if (!Apply(chart, task, clause[start..i], line)) return false;
            start = i + 5; i = start - 1;
        }
        if (!Apply(chart, task, clause[start..], line)) return false;
        previous = task;
        return true;
    }

    private static bool Depends(TaskRow task, TaskRow from, bool end, int line)
    {
        if (task.Predecessor != null || task.StartAt != null || task.EndAt != null || task == from) return false;
        task.Predecessor = from; task.PredecessorEnd = end; task.StartLine = line;
        return true;
    }

    private static bool Apply(Schedule chart, TaskRow task, string clause, int line)
    {
        var duration = Duration().Match(clause);
        if (duration.Success)
        {
            if (task.Duration != null || task.Milestone) return false;
            int n = int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture);
            task.Duration = n * (duration.Groups[2].Value == "week" ? 7 : 1);
            task.DurationLine = line; return true;
        }
        var fixedDate = FixedDate().Match(clause);
        if (fixedDate.Success)
        {
            if (chart.Epoch == null || !Date(fixedDate.Groups[2].Value, out int day) || day < chart.Epoch.Value.DayNumber) return false;
            if (fixedDate.Groups[1].Value == "ends")
            {
                if (task.EndAt != null || task.Predecessor != null || task.Milestone) return false;
                task.EndAt = day; task.EndLine = line; return true;
            }
            if (task.StartAt != null || task.Predecessor != null || task.Milestone) return false;
            task.Milestone = fixedDate.Groups[1].Value == "happens";
            if (task.Milestone && (task.Duration != null || task.EndAt != null)) return false;
            task.StartAt = day; task.StartLine = line; return true;
        }
        var relative = Relative().Match(clause);
        if (relative.Success)
        {
            if (!chart.Names.TryGetValue(relative.Groups[2].Value, out var from)) return false;
            task.Milestone = relative.Groups[1].Value == "happens";
            if (task.Milestone && task.Duration != null) return false;
            return Depends(task, from, relative.Groups[3].Value == "end", line);
        }
        var progress = Progress().Match(clause);
        if (progress.Success)
        {
            int percent = int.Parse(progress.Groups[1].Value, CultureInfo.InvariantCulture);
            if (percent > 100 || task.Milestone) return false;
            task.Percent = percent; return true;
        }
        var color = Color().Match(clause);
        if (color.Success) { task.Fill = color.Groups[1].Value; return true; }
        return false;
    }

    private static bool ScheduleDates(Schedule chart)
    {
        int epoch = chart.Epoch?.DayNumber ?? 0;
        var ready = new Queue<TaskRow>(chart.Rows.Where(t => t.Predecessor == null));
        var dependents = chart.Rows.Where(t => t.Predecessor != null).ToLookup(t => t.Predecessor!);
        int scheduled = 0;
        while (ready.TryDequeue(out var row))
        {
            int duration = row.Duration ?? 1;
            if (row.Predecessor is { } pred)
                row.Start = row.PredecessorEnd ? pred.End + (row.Milestone ? 0 : 1) : pred.Start;
            else row.Start = row.StartAt ?? (row.EndAt is int end ? end - duration + 1 : epoch);
            row.End = row.EndAt ?? row.Start + duration - 1;
            if (row.Start < epoch || row.End < row.Start || row.End > DateOnly.MaxValue.DayNumber || row.End - epoch >= 1096 ||
                row.Duration != null && row.End - row.Start + 1 != duration) return false;
            scheduled++;
            foreach (var next in dependents[row]) ready.Enqueue(next);
        }
        return scheduled == chart.Rows.Count;
    }

    private static string? Draw(Schedule chart)
    {
        const int dayWidth = 28, rowHeight = 30, margin = 14;
        int epoch = chart.Epoch?.DayNumber ?? 0, last = chart.Rows.Max(t => t.End);
        int title = chart.Title.Length == 0 ? 0 : 33;
        int axisY = margin + title, axisHeight = chart.Epoch == null ? 33 : 54;
        int taskWidth = Math.Max(80, chart.Rows.Max(t => SvgText.Measure(t.Caption, 13)) + 24);
        int startColumn = margin + taskWidth, endColumn = startColumn + 68, daysColumn = endColumn + 68, gridX = daysColumn + 64;
        int gridY = axisY + axisHeight, gridBottom = gridY + chart.Rows.Count * rowHeight;
        int width = Math.Max(gridX + (last - epoch + 1) * dayWidth + margin, SvgText.Measure(chart.Title, 19) + margin * 2);
        int height = gridBottom + margin + (chart.Footer ? axisHeight : 0);
        if (width > 32768 || height > 32768) return null;
        var s = new StringBuilder(chart.Rows.Count * 320 + (last - epoch + 1) * 220);
        s.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"Arial, sans-serif\">");
        void Text(int x, int y, string value, int size = 13, string anchor = "start", bool bold = false) =>
            s.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" fill=\"#303247\" font-size=\"{size}\" text-anchor=\"{anchor}\"{(bold ? " font-weight=\"bold\"" : "")}>{SvgText.Escape(value)}</text>");
        void Rule(int x1, int y1, int x2, int y2) => s.Append(CultureInfo.InvariantCulture, $"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"#d5d9df\"/>");
        string DateLabel(int day) => chart.Epoch == null ? "Day " + (day + 1).ToString(CultureInfo.InvariantCulture) : DateOnly.FromDayNumber(day).ToString("MMM d", CultureInfo.InvariantCulture);
        if (title != 0) Text(width / 2, margin + 17, chart.Title, 19, "middle", true);
        Text(margin, gridY - 9, "Task", 13, bold: true); Text(startColumn, gridY - 9, "Start", 13, bold: true); Text(endColumn, gridY - 9, "End", 13, bold: true); Text(daysColumn, gridY - 9, "Duration", 13, bold: true);
        for (int day = epoch; day <= last; day++)
        {
            int x = gridX + (day - epoch) * dayWidth;
            int center = x + dayWidth / 2;
            if (chart.Epoch is not null)
            {
                var date = DateOnly.FromDayNumber(day);
                if (day == epoch || date.Day == 1)
                {
                    var month = date.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
                    int span = Math.Min(DateTime.DaysInMonth(date.Year, date.Month) - date.Day + 1, last - day + 1) * dayWidth;
                    if (SvgText.Measure(month, 13) > span - 2) month = date.ToString("MMM", CultureInfo.InvariantCulture);
                    Text(x + 2, axisY + 12, month, 12);
                    if (chart.Footer) Text(x + 2, gridBottom + axisHeight - 2, month, 12);
                }
                string weekday = date.ToString("ddd", CultureInfo.InvariantCulture)[..2];
                Text(center, gridY - 23, weekday, 11, "middle");
                if (chart.Footer) Text(center, gridBottom + 28, weekday, 11, "middle");
            }
            string number = (chart.Epoch == null ? day + 1 : DateOnly.FromDayNumber(day).Day).ToString(CultureInfo.InvariantCulture);
            Text(center, gridY - 6, number, 12, "middle");
            if (chart.Footer) Text(center, gridBottom + 13, number, 12, "middle");
            Rule(x, gridY, x, gridBottom);
        }
        Rule(width - margin, gridY, width - margin, gridBottom);
        var indices = new Dictionary<TaskRow, int>();
        for (int i = 0; i < chart.Rows.Count; i++) indices[chart.Rows[i]] = i;
        foreach (var row in chart.Rows)
        {
            if (row.Predecessor is not { } from || row.Milestone) continue;
            int startX = gridX + ((row.PredecessorEnd ? from.End + 1 : from.Start) - epoch) * dayWidth - 4;
            int startY = gridY + indices[from] * rowHeight + rowHeight / 2;
            int endX = gridX + (row.Start - epoch) * dayWidth + 3, endY = gridY + indices[row] * rowHeight + rowHeight / 2;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"dependency\" data-source-line=\"{row.StartLine}\"><title>{SvgText.Escape(from.Caption)} → {SvgText.Escape(row.Caption)}</title><path d=\"M{startX} {startY}V{endY}H{endX}\" fill=\"none\" stroke=\"#475475\"/><path d=\"M{endX - 5} {endY - 4}l5 4 -5 4Z\" fill=\"#475475\"/></g>");
        }
        for (int i = 0; i < chart.Rows.Count; i++)
        {
            var t = chart.Rows[i]; int y = gridY + i * rowHeight, cy = y + rowHeight / 2;
            int x = gridX + (t.Start - epoch) * dayWidth + 2, w = (t.End - t.Start + 1) * dayWidth - 4;
            Rule(margin, y, width - margin, y);
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"task\" data-qualified-name=\"{SvgText.Escape(t.Id)}\" data-source-line=\"{t.Line}\"><title>{SvgText.Escape(t.Caption)}</title>");
            Text(margin, cy + 4, t.Caption); Text(startColumn, cy + 4, DateLabel(t.Start), 12); Text(endColumn, cy + 4, DateLabel(t.End), 12);
            int duration = t.End - t.Start + 1; Text(daysColumn, cy + 4, duration.ToString(CultureInfo.InvariantCulture) + (duration == 1 ? " day" : " days"), 12);
            if (t.Milestone)
            {
                int cx = x + dayWidth / 2 - 2;
                s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{cx} {cy - 7}l7 7 -7 7 -7 -7Z\" stroke=\"#455273\" fill=\"{t.Fill}\"/>");
            }
            else
            {
                s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{cy - 7}\" width=\"{w}\" height=\"14\" rx=\"2\" fill=\"{t.Fill}\" stroke=\"#455273\"/>");
                if (t.Percent >= 0) s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x + 1}\" y=\"{cy - 6}\" width=\"{(w - 2) * t.Percent / 100}\" height=\"12\" rx=\"1\" fill=\"#465f92\"/>");
            }
            s.Append("</g>");
        }
        Rule(margin, gridBottom, width - margin, gridBottom);
        return s.Append("</svg>").ToString();
    }
}
