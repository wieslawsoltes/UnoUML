using System.Globalization;
using System.Text;
using UnoUML.Syntax;

namespace UnoUML.PlantUml;

internal static partial class ChartRenderer
{
    private static Token T(SourceToken token) => new(token.Start, token.Length);
    private static string? RenderStructured(ChartSource chart)
    {
        var h = chart.Horizontal; var v = chart.Vertical;
        if (!SvgText.IsPlain(chart.Title) || !SvgText.IsPlain(h.Label) || !SvgText.IsPlain(v.Label) ||
            chart.Series.Any(s => !SvgText.IsPlain(s.Label)) || chart.CategoryAxis.Categories.Any(c => !SvgText.IsPlain(c)) ||
            chart.Annotations.Any(a => !SvgText.IsPlain(a.Label)) ||
            new[] { h, v, chart.Secondary }.OfType<ChartSourceAxis>().Any(axis => axis.Ticks.Any(t => !SvgText.IsPlain(t.Label)))) return null;
        var entries = chart.Series.Where(s => s.Label.Length > 0).ToArray();
        int legendWidth = entries.Select(e => SvgText.Measure(e.Label) + 36).DefaultIfEmpty(0).Max();
        int margin = Math.Max(60, v.Categorical ? v.Categories.Select(c => SvgText.Measure(c) + 15).DefaultIfEmpty(60).Max() : Math.Max(SvgText.Measure(Tick(v.Min)), SvgText.Measure(Tick(v.Max))) + 16 + (v.Label.Length == 0 ? 0 : 22));
        int left = margin + (chart.Legend == "left" ? legendWidth : 0);
        int top = 50 + (chart.Title.Length == 0 ? 0 : 30) + (chart.Legend == "top" ? 28 : 0);
        int plotWidth = Math.Max(400, h.Categorical ? h.Categories.Length * Math.Max(24, h.Categories.Where((_, i) => i % h.CategorySpacing == 0).Select(SvgText.Measure).DefaultIfEmpty(0).Max() + 12) : 400);
        int plotHeight = Math.Max(300, v.Categorical ? v.Categories.Length * 26 : 300);
        int right = left + plotWidth, bottom = top + plotHeight;
        int width = right + (chart.Secondary == null ? 24 : 78) + (chart.Legend == "right" ? legendWidth : 0);
        int height = bottom + 60 + (h.Label.Length == 0 ? 0 : 18) + (chart.Legend == "bottom" ? 32 : 0);
        if (chart.Legend is "top" or "bottom") width = Math.Max(width, left + entries.Sum(e => SvgText.Measure(e.Label) + 36) + 20);
        if (chart.Legend is "left" or "right") height = Math.Max(height, top + entries.Length * 22 + 10);
        width = Math.Max(width, SvgText.Measure(chart.Title, 18) + 32);
        if (width > 32768 || height > 32768) return null;
        double NumericX(double value) => left + (value - h.Min) / (h.Max - h.Min) * plotWidth;
        double NumericY(double value, ChartSourceAxis axis) => bottom - (value - axis.Min) / (axis.Max - axis.Min) * plotHeight;
        double CategoryX(int index) => left + (index + .5) * plotWidth / h.Categories.Length;
        double CategoryY(int index) => bottom - (index + .5) * plotHeight / v.Categories.Length;
        int count = chart.Series.Max(s => s.Points.Length);
        double IndexedX(int index) => count == 1 ? left + plotWidth / 2.0 : left + (double)index * plotWidth / (count - 1);
        (double X, double Y) Position(ChartSourceSeries series, ChartSourcePoint point, int index) => chart.HorizontalOrientation
            ? (NumericX(point.Y), CategoryY(index))
            : (chart.Coordinates ? NumericX(point.X) : h.Categorical ? CategoryX(index) : IndexedX(index), NumericY(point.Y, series.Secondary ? chart.Secondary! : v));
        var svg = new StringBuilder(2048 + chart.Series.Sum(s => s.Points.Length) * 150);
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-size=\"14\" font-family=\"sans-serif\">");
        if (chart.Title.Length > 0) { Open(svg, "title", chart.TitleLine); Text(svg, width / 2, 26, chart.Title, size: 18, token: T(chart.TitleToken)); svg.Append("</g>"); }

        void Axis(ChartSourceAxis axis, bool horizontal, bool secondary = false)
        {
            string name = horizontal ? "horizontal-axis" : secondary ? "secondary-axis" : "vertical-axis";
            Open(svg, name, axis.Row?.Index ?? 0);
            double axisX = secondary ? right : left;
            double axisY = horizontal && !v.Categorical ? NumericY(Math.Clamp(0, v.Min, v.Max), v) : bottom;
            if (horizontal) Line(svg, left, axisY, right, axisY); else Line(svg, axisX, top, axisX, bottom);
            if (axis.Categorical)
            {
                for (int i = 0; i < axis.Categories.Length; i += axis.CategorySpacing)
                {
                    double p = horizontal ? CategoryX(i) : CategoryY(i);
                    if (axis.Grid)
                    {
                        svg.Append("<g data-style-name=\"grid\">");
                        if (horizontal) Line(svg, p, top, p, bottom, "#DDDDDD"); else Line(svg, left, p, right, p, "#DDDDDD");
                        svg.Append("</g>");
                    }
                    if (horizontal) Line(svg, p, axisY, p, axisY + 5); else Line(svg, axisX - 5, p, axisX, p);
                    var token = i < axis.CategoryTokens.Length ? T(axis.CategoryTokens[i]) : default;
                    Text(svg, horizontal ? p : axisX - 10, horizontal ? axisY + 23 : p + 5, axis.Categories[i], horizontal ? "middle" : "end", token: token, kind: "chart-category");
                }
            }
            else
            {
                IEnumerable<ChartSourceTick> ticks = axis.Ticks.Length > 0 ? axis.Ticks : Enumerable.Range(0, (int)Math.Floor((axis.Max - axis.Min) / axis.Step + 1e-9) + 1)
                    .Select(i => new ChartSourceTick(axis.Min + axis.Step * i, Tick(axis.Min + axis.Step * i), i == 0 ? axis.MinToken : Math.Abs(axis.Min + axis.Step * i - axis.Max) < 1e-9 ? axis.MaxToken : default, default));
                foreach (var tick in ticks)
                {
                    double p = horizontal ? NumericX(tick.Value) : NumericY(tick.Value, axis);
                    if (axis.Grid)
                    {
                        svg.Append("<g data-style-name=\"grid\">");
                        if (horizontal) Line(svg, p, top, p, bottom, "#DDDDDD"); else Line(svg, left, p, right, p, "#DDDDDD");
                        svg.Append("</g>");
                    }
                    if (horizontal) Line(svg, p, axisY, p, axisY + 5); else Line(svg, axisX, p, axisX + (secondary ? 5 : -5), p);
                    Text(svg, horizontal ? p : axisX + (secondary ? 10 : -10), horizontal ? axisY + 23 : p + 5, tick.Label,
                        horizontal ? "middle" : secondary ? "start" : "end", token: T(tick.LabelToken.Length > 0 ? tick.LabelToken : tick.ValueToken), kind: tick.LabelToken.Length > 0 ? "quoted" : "number", role: tick.LabelToken.Length > 0 ? "label" : "value");
                }
            }
            if (axis.Label.Length > 0)
            {
                if (horizontal) Text(svg, axis.LabelPosition == "label-right" ? right : left + plotWidth / 2, bottom + 48, axis.Label, axis.LabelPosition == "label-right" ? "end" : "middle", token: T(axis.LabelToken), kind: "quoted");
                else if (axis.LabelPosition == "label-top") Text(svg, axisX, top - 14, axis.Label, secondary ? "start" : "end", token: T(axis.LabelToken), kind: "quoted");
                else
                {
                    int x = secondary ? right + 62 : left - margin + 17, y = top + plotHeight / 2;
                    svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"rotate(-90 {x} {y})\">"); Text(svg, x, y, axis.Label, token: T(axis.LabelToken), kind: "quoted"); svg.Append("</g>");
                }
            }
            svg.Append("</g>");
        }
        Axis(v, false); Axis(h, true); if (chart.Secondary is { } secondaryAxis) Axis(secondaryAxis, false, true);
        int barCount = chart.Series.Count(s => s.Kind == "bar"), barIndex = 0;
        int stackGroups = chart.Series.Where(s => s.Kind == "bar").Select(s => s.Secondary).Distinct().Count();
        var stacks = new Dictionary<(bool Secondary, int Point, bool Positive), double>();
        foreach (var series in chart.Series)
        {
            int seriesIndex = chart.Series.IndexOf(series);
            string color = SeriesColor(series);
            svg.Append(CultureInfo.InvariantCulture, $"<g class=\"series {series.Kind}\" data-source-line=\"{series.Row.Index}\" data-chart-series-index=\"{seriesIndex}\"");
            if (series.Stereotype.Length > 0) svg.Append(" data-style-stereotype=\"").Append(SvgText.Escape(series.Stereotype)).Append('"');
            svg.Append('>');
            var axis = series.Secondary ? chart.Secondary! : chart.ValueAxis;
            double baseline = chart.HorizontalOrientation ? NumericX(Math.Clamp(0, axis.Min, axis.Max)) : NumericY(Math.Clamp(0, axis.Min, axis.Max), axis);
            if (series.Kind == "bar")
            {
                int groups = chart.Stacked ? stackGroups : barCount;
                int group = chart.Stacked ? series.Secondary && chart.Series.Any(s => s.Kind == "bar" && !s.Secondary) ? 1 : 0 : barIndex;
                double categorySize = chart.HorizontalOrientation ? (double)plotHeight / v.Categories.Length : h.Categorical ? (double)plotWidth / h.Categories.Length : (double)plotWidth / Math.Max(1, count);
                double groupWidth = categorySize * (groups == 1 ? .6 : .8), barWidth = groupWidth / groups;
                for (int i = 0; i < series.Points.Length; i++)
                {
                    var point = series.Points[i]; var pos = Position(series, point, i);
                    double begin = baseline, end = chart.HorizontalOrientation ? pos.X : pos.Y;
                    if (chart.Stacked)
                    {
                        var key = (series.Secondary, i, point.Y >= 0); stacks.TryGetValue(key, out double previous); stacks[key] = previous + point.Y;
                        begin = chart.HorizontalOrientation ? NumericX(Math.Clamp(previous, axis.Min, axis.Max)) : NumericY(Math.Clamp(previous, axis.Min, axis.Max), axis);
                        end = chart.HorizontalOrientation ? NumericX(previous + point.Y) : NumericY(previous + point.Y, axis);
                    }
                    double center = (chart.HorizontalOrientation ? pos.Y : pos.X) - groupWidth / 2 + (group + .5) * barWidth;
                    if (chart.HorizontalOrientation) Rect(svg, Math.Min(begin, end), center - barWidth / 2, Math.Abs(end - begin), barWidth, color, "#181818", i, T(point.YToken));
                    else Rect(svg, center - barWidth / 2, Math.Min(begin, end), barWidth, Math.Abs(end - begin), color, "#181818", i, T(point.YToken));
                    if (begin == end) Point(svg, chart.HorizontalOrientation ? end : center, chart.HorizontalOrientation ? center : end, 6, "transparent", i, T(point.YToken));
                    if (series.Labels) Text(svg, chart.HorizontalOrientation ? end + 8 : center, chart.HorizontalOrientation ? center + 5 : end - 7, Tick(point.Y), chart.HorizontalOrientation ? "start" : "middle", token: T(point.YToken), kind: "number", role: "value");
                }
                barIndex++;
            }
            else
            {
                if (series.Kind == "line") DrawLineSeries(svg, series, (point, index) => Position(series, point, index), color);
                else if (series.Kind == "area")
                {
                    bool area = series.Kind == "area"; var first = Position(series, series.Points[0], 0);
                    svg.Append(area ? "<polygon" : "<polyline").Append(" fill=\"").Append(area ? color : "none").Append("\" stroke=\"").Append(color).Append("\" stroke-width=\"2\" points=\"");
                    if (area) svg.Append(chart.HorizontalOrientation ? F(baseline) + "," + F(first.Y) + " " : F(first.X) + "," + F(baseline) + " ");
                    for (int i = 0; i < series.Points.Length; i++) { var p = Position(series, series.Points[i], i); svg.Append(F(p.X)).Append(',').Append(F(p.Y)).Append(' '); }
                    if (area) { var last = Position(series, series.Points[^1], series.Points.Length - 1); svg.Append(chart.HorizontalOrientation ? F(baseline) + "," + F(last.Y) : F(last.X) + "," + F(baseline)); }
                    svg.Append("\"/>");
                }
                for (int i = 0; i < series.Points.Length; i++)
                {
                    var point = series.Points[i]; if (point.IsMissing) continue; var pos = Position(series, point, i);
                    svg.Append(CultureInfo.InvariantCulture, $"<g data-point-index=\"{i}\" data-point-x=\"{StructuredText.Format(point.X)}\" data-point-y=\"{StructuredText.Format(point.Y)}\"");
                    if (point.XToken.Length > 0) svg.Append(CultureInfo.InvariantCulture, $" data-x-edit-start=\"{point.XToken.Start}\" data-x-edit-length=\"{point.XToken.Length}\"");
                    svg.Append('>');
                    Point(svg, pos.X, pos.Y, series.Kind == "scatter" ? 4 : 6, series.Kind == "scatter" ? color : "transparent", i, T(point.YToken), series.Kind == "scatter");
                    if (series.Labels) Text(svg, pos.X, pos.Y - 8, Tick(point.Y), token: T(point.YToken), kind: "number", role: "value");
                    svg.Append("</g>");
                }
            }
            svg.Append("</g>");
        }
        foreach (var annotation in chart.Annotations)
        {
            string key = annotation.X.StartsWith('"') ? StructuredText.Unquote(annotation.X) : annotation.X;
            int index = Array.IndexOf(chart.CategoryAxis.Categories, key);
            double x = chart.HorizontalOrientation ? NumericX(annotation.Y) : h.Categorical ? CategoryX(index) : NumericX(double.Parse(key, CultureInfo.InvariantCulture));
            double y = chart.HorizontalOrientation ? CategoryY(index) : NumericY(annotation.Y, v);
            Open(svg, "annotation", annotation.Row.Index);
            if (annotation.Arrow)
            {
                Line(svg, x, y - 34, x, y - 7);
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M {F(x - 4)} {F(y - 12)} L {F(x)} {F(y - 6)} L {F(x + 4)} {F(y - 12)}\" fill=\"none\" stroke=\"#181818\"/>");
            }
            Text(svg, x, y - (annotation.Arrow ? 40 : 14), annotation.Label, token: T(annotation.LabelToken), kind: "quoted"); svg.Append("</g>");
        }
        if (chart.Legend.Length > 0)
        {
            bool column = chart.Legend is "left" or "right";
            int x = chart.Legend switch { "left" => 8, "right" => right + (chart.Secondary == null ? 10 : 74), _ => left };
            int y = chart.Legend switch { "top" => top - 28, "bottom" => height - 34, _ => top };
            foreach (var series in entries)
            {
                string color = SeriesColor(series); Open(svg, "legend", series.Row.Index);
                if (series.Kind == "line") Line(svg, x, y + 6, x + 12, y + 6, color, 2);
                else if (series.Kind == "scatter") svg.Append(CultureInfo.InvariantCulture, $"<circle data-chart-marker=\"true\" data-style-name=\"scatter\" cx=\"{x + 6}\" cy=\"{y + 6}\" r=\"4.2\" fill=\"{color}\"/>");
                else Rect(svg, x, y, 12, 12, color, color);
                Text(svg, x + 17, y + 13, series.Label, "start", token: T(series.LabelToken), kind: "quoted"); svg.Append("</g>");
                if (column) y += 22; else x += SvgText.Measure(series.Label) + 36;
            }
        }
        return svg.Append("</svg>").ToString();
    }
    private static void DrawLineSeries(StringBuilder svg, ChartSourceSeries series, Func<ChartSourcePoint, int, (double X, double Y)> project, string color)
    {
        var visible = series.Points.Select((point, i) => (Point: point, Pixel: project(point, i))).Where(p => !p.Point.IsMissing).Select(p => p.Pixel).ToArray();
        if (series.Interpolate && NaturalChartPath(visible) is { } curve)
        {
            svg.Append("<path data-chart-interpolation=\"natural\" d=\"").Append(curve).Append("\" fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"2\"/>");
            return;
        }
        var segment = new List<(double X, double Y)>();
        void Flush()
        {
            if (segment.Count >= 2)
            {
                svg.Append("<polyline fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"2\" points=\"");
                foreach (var p in segment) svg.Append(F(p.X)).Append(',').Append(F(p.Y)).Append(' ');
                svg.Append("\"/>");
            }
            segment.Clear();
        }
        for (int i = 0; i < series.Points.Length; i++)
        {
            if (series.Points[i].IsMissing) { if (!series.Connect) Flush(); continue; }
            segment.Add(project(series.Points[i], i));
        }
        Flush();
    }

    // Natural cubic interpolation, represented as one exact cubic Bezier per knot interval.
    // The tridiagonal solve fixes the endpoint second derivatives at zero. As in PlantUML,
    // nonincreasing X coordinates fall back to ordinary segments rather than reordering data.
    private static string? NaturalChartPath((double X, double Y)[] points)
    {
        if (points.Length < 2) return null;
        int n = points.Length;
        var widths = new double[n - 1];
        for (int i = 0; i < widths.Length; i++)
        { widths[i] = points[i + 1].X - points[i].X; if (!(widths[i] > 0) || !double.IsFinite(widths[i])) return null; }
        var diagonal = new double[n]; var rhs = new double[n]; var curvature = new double[n];
        diagonal[0] = diagonal[^1] = 1;
        for (int i = 1; i < n - 1; i++)
        {
            diagonal[i] = 2 * (widths[i - 1] + widths[i]);
            rhs[i] = 6 * ((points[i + 1].Y - points[i].Y) / widths[i] - (points[i].Y - points[i - 1].Y) / widths[i - 1]);
            if (i > 1) { double factor = widths[i - 1] / diagonal[i - 1]; diagonal[i] -= factor * widths[i - 1]; rhs[i] -= factor * rhs[i - 1]; }
        }
        for (int i = n - 2; i > 0; i--) curvature[i] = (rhs[i] - widths[i] * curvature[i + 1]) / diagonal[i];
        var path = new StringBuilder().Append("M ").Append(F(points[0].X)).Append(' ').Append(F(points[0].Y));
        for (int i = 0; i < n - 1; i++)
        {
            double width = widths[i], rise = points[i + 1].Y - points[i].Y;
            double firstY = points[i].Y + rise / 3 - width * width * (2 * curvature[i] + curvature[i + 1]) / 18;
            double lastY = points[i + 1].Y - rise / 3 - width * width * (curvature[i] + 2 * curvature[i + 1]) / 18;
            if (!double.IsFinite(firstY) || !double.IsFinite(lastY)) return null;
            path.Append(" C ").Append(F(points[i].X + width / 3)).Append(' ').Append(F(firstY)).Append(' ')
                .Append(F(points[i + 1].X - width / 3)).Append(' ').Append(F(lastY)).Append(' ')
                .Append(F(points[i + 1].X)).Append(' ').Append(F(points[i + 1].Y));
        }
        return path.ToString();
    }
    private static string SeriesColor(ChartSourceSeries series) => series.Color is { } color && ManagedSkin.Normalize("linecolor", color, out string normalized)
        ? normalized : series.Kind is "line" or "scatter" ? "#181818" : "#F1F1F1";
}
