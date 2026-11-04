using System.Globalization;
using System.Text;
using UnoUML.Syntax;

namespace UnoUML.PlantUml;

/// <summary>PlantUML calendar-day Gantt scheduling and source-addressable vector output.</summary>
internal static partial class GanttRenderer
{
    internal static string? TryRender(string source)
    {
        if (GanttSource.Parse(source) is not { Tasks.Count: > 0 } chart ||
            !SvgText.IsPlain(chart.Title) || chart.Tasks.Any(t => !SvgText.IsPlain(t.Caption))) return null;
        return Draw(chart);
    }

    private static string? Draw(GanttSource chart)
    {
        const int dayWidth = 28, rowHeight = 30, margin = 14;
        int epoch = chart.Epoch?.DayNumber ?? 0, last = chart.Tasks.Max(t => t.End);
        int title = chart.Title.Length == 0 ? 0 : 33;
        int axisY = margin + title, axisHeight = chart.Epoch == null ? 33 : 54;
        int taskWidth = Math.Max(80, chart.Tasks.Max(t => SvgText.Measure(t.Caption, 13)) + 24);
        int startColumn = margin + taskWidth, endColumn = startColumn + 68, daysColumn = endColumn + 68, gridX = daysColumn + 64;
        int gridY = axisY + axisHeight, gridBottom = gridY + chart.Tasks.Count * rowHeight;
        int width = Math.Max(gridX + (last - epoch + 1) * dayWidth + margin, SvgText.Measure(chart.Title, 19) + margin * 2);
        int height = gridBottom + margin + (chart.Footer ? axisHeight : 0);
        if (width > 32768 || height > 32768) return null;
        var s = new StringBuilder(chart.Tasks.Count * 320 + (last - epoch + 1) * 220);
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
        var indices = new Dictionary<GanttSourceTask, int>();
        for (int i = 0; i < chart.Tasks.Count; i++) indices[chart.Tasks[i]] = i;
        foreach (var row in chart.Tasks)
        {
            if (row.Predecessor is not { } from || row.Milestone) continue;
            int startX = gridX + ((row.PredecessorEnd ? from.End + 1 : from.Start) - epoch) * dayWidth - 4;
            int startY = gridY + indices[from] * rowHeight + rowHeight / 2;
            int endX = gridX + (row.Start - epoch) * dayWidth + 3, endY = gridY + indices[row] * rowHeight + rowHeight / 2;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"dependency\" data-source-line=\"{row.StartLine}\"><title>{SvgText.Escape(from.Caption)} → {SvgText.Escape(row.Caption)}</title><path d=\"M{startX} {startY}V{endY}H{endX}\" fill=\"none\" stroke=\"#475475\"/><path d=\"M{endX - 5} {endY - 4}l5 4 -5 4Z\" fill=\"#475475\"/></g>");
        }
        for (int i = 0; i < chart.Tasks.Count; i++)
        {
            var t = chart.Tasks[i]; int y = gridY + i * rowHeight, cy = y + rowHeight / 2;
            int x = gridX + (t.Start - epoch) * dayWidth + 2, w = (t.End - t.Start + 1) * dayWidth - 4;
            Rule(margin, y, width - margin, y);
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"task\" data-qualified-name=\"{SvgText.Escape(t.Id)}\" data-source-line=\"{t.Line}\" data-gantt-id=\"{SvgText.Escape(t.Id)}\" data-gantt-start=\"{t.Start - epoch}\" data-gantt-duration=\"{t.End - t.Start + 1}\" data-gantt-milestone=\"{(t.Milestone ? "true" : "false")}\" data-gantt-unit=\"{dayWidth}\"><title>{SvgText.Escape(t.Caption)}</title>");
            var nameToken = t.Statements.FirstOrDefault()?.NameToken ?? chart.Statements.First(st => st.ArrowTarget == t).ArrowTargetToken;
            s.Append(CultureInfo.InvariantCulture, $"<g data-edit-kind=\"gantt-label\" data-edit-start=\"{nameToken.Start}\" data-edit-length=\"{nameToken.Length}\" data-edit-role=\"label\">");
            Text(margin, cy + 4, t.Caption); s.Append("</g>");
            Text(startColumn, cy + 4, DateLabel(t.Start), 12); Text(endColumn, cy + 4, DateLabel(t.End), 12);
            int duration = t.End - t.Start + 1; Text(daysColumn, cy + 4, duration.ToString(CultureInfo.InvariantCulture) + (duration == 1 ? " day" : " days"), 12);
            s.Append("<g class=\"gantt-bar\">");
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
            s.Append("</g></g>");
        }
        Rule(margin, gridBottom, width - margin, gridBottom);
        return s.Append("</svg>").ToString();
    }
}
