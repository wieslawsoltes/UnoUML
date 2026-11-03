using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>Ports PlantUML's categorical plot coordinates, zero baseline, grouped bars and series projection.</summary>
internal static partial class ChartRenderer
{
    private const string Number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)";
    private sealed record Series(string Kind, string Label, string? Color, double[] Values, int Line);
    private sealed record Domain(double Min, double Max, double Step, string Label, int Line);
    private sealed class Chart
    {
        internal string[]? Categories;
        internal Domain? Vertical;
        internal string HorizontalLabel = "", Title = "", Legend = "";
        internal int TitleLine, HorizontalLine, CategorySpacing = 1;
        internal readonly List<Series> Series = [];
    }

    [GeneratedRegex("""^h-axis\s+(?:"([^"]*)"\s+)?\[([^\[\]]+)\](?:\s+spacing\s+([1-9][0-9]*))?$""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HorizontalAxis();

    [GeneratedRegex("^v-axis\\s+(?:\"([^\"]*)\"\\s+)?(" + Number + @")\s*-->\s*(" + Number + @")(?:\s+spacing\s+(" + Number + @"))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VerticalAxis();

    [GeneratedRegex("""^(bar|line|area|scatter)\s+(?:"([^"]+)"\s+)?\[([^\[\]]+)\](?:\s+(\#[a-fA-F0-9]{3}|\#[a-fA-F0-9]{6}))?$""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SeriesDeclaration();

    internal static string? TryRender(string source)
    {
        var chart = new Chart();
        bool opened = false, closed = false;
        int index = -1, count = 0;
        foreach (var segment in source.AsSpan().EnumerateLines())
        {
            index++;
            var line = segment.Trim();
            if (line.IsEmpty || line[0] == '\'') continue;
            if (!opened) { if (!line.SequenceEqual("@startchart")) return null; opened = true; continue; }
            if (closed || line.Length > 32768) return null;
            if (line.SequenceEqual("@endchart")) { closed = true; continue; }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                if (chart.Title.Length != 0 || !Plain(chart.Title = line[6..].Trim().ToString())) return null;
                chart.TitleLine = index; continue;
            }
            if (line.StartsWith("legend ", StringComparison.OrdinalIgnoreCase))
            {
                if (chart.Legend.Length != 0) return null;
                chart.Legend = line[7..].Trim().ToString().ToLowerInvariant();
                if (chart.Legend is not ("left" or "right" or "top" or "bottom")) return null;
                continue;
            }
            if (line.StartsWith("h-axis ", StringComparison.OrdinalIgnoreCase))
            {
                var axis = HorizontalAxis().Match(line.ToString());
                if (chart.Categories != null || !axis.Success || !Plain(axis.Groups[1].Value)) return null;
                chart.HorizontalLabel = axis.Groups[1].Value;
                var categories = axis.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries);
                if (categories.Length > 512 || categories.Any(s => s.Length == 0 || s.Contains('"') || !Plain(s))) return null;
                chart.Categories = categories;
                chart.HorizontalLine = index;
                if (axis.Groups[3].Success && !int.TryParse(axis.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out chart.CategorySpacing)) return null;
                continue;
            }
            if (line.StartsWith("v-axis ", StringComparison.OrdinalIgnoreCase))
            {
                var axis = VerticalAxis().Match(line.ToString());
                if (chart.Vertical != null || !axis.Success || !Plain(axis.Groups[1].Value) || !Value(axis.Groups[2].Value, out double min) || !Value(axis.Groups[3].Value, out double max) || max <= min) return null;
                double step = (max - min) / 5;
                if (axis.Groups[4].Success && !Value(axis.Groups[4].Value, out step)) return null;
                if (step <= 0 || (max - min) / step > 128) return null;
                chart.Vertical = new(min, max, step, axis.Groups[1].Value, index);
                continue;
            }
            var series = SeriesDeclaration().Match(line.ToString());
            if (!series.Success || chart.Series.Count >= 64 || !Plain(series.Groups[2].Value)) return null;
            var values = series.Groups[3].Value.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length > 512 || (count += values.Length) > 8192) return null;
            var data = new double[values.Length];
            for (int i = 0; i < values.Length; i++) if (!Value(values[i], out data[i])) return null;
            chart.Series.Add(new(series.Groups[1].Value.ToLowerInvariant(), series.Groups[2].Value,
                series.Groups[4].Success ? series.Groups[4].Value : null, data, index));
        }
        if (!closed || chart.Categories == null || chart.Vertical is not {} vertical || chart.Series.Count == 0) return null;
        // Explicit coordinate pairs, secondary axes, stacked series and noncategorical charts use the upstream engine.
        if (chart.Series.Any(series => series.Values.Length != chart.Categories.Length || series.Values.Any(v => v < vertical.Min || v > vertical.Max))) return null;
        return Render(chart, vertical, chart.Categories);
    }

    private static bool Plain(string label) => label.Length <= 1024 && SvgText.IsPlain(label);
    private static bool Value(string text, out double number) =>
        double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && Math.Abs(number) <= 1e12;
    private static string F(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Tick(double n) => n.ToString("G11", CultureInfo.InvariantCulture);

    private static string? Render(Chart chart, Domain v, string[] categories)
    {
        var s = new StringBuilder(1024 + chart.Series.Count * categories.Length * 70);
        var entries = chart.Series.Where(e => e.Label.Length > 0).ToArray();
        int legendWidth = entries.Length == 0 ? 0 : entries.Max(e => SvgText.Measure(e.Label)) + 36;
        int rowLegendWidth = entries.Sum(e => SvgText.Measure(e.Label) + 36);
        int marginLeft = Math.Max(60, Math.Max(SvgText.Measure(Tick(v.Min)), SvgText.Measure(Tick(v.Max))) + 16 + (v.Label.Length == 0 ? 0 : 22));
        int left = marginLeft + (chart.Legend == "left" ? legendWidth : 0);
        int top = 50 + (chart.Title.Length == 0 ? 0 : 30) + (chart.Legend == "top" ? 28 : 0);
        int stride = Math.Max(24, categories.Where((_, i) => i % chart.CategorySpacing == 0).Max(label => SvgText.Measure(label)) + 12);
        int plot = Math.Max(400, categories.Length * stride);
        int right = left + plot, bottom = top + 300;
        int width = right + 20 + (chart.Legend == "right" ? legendWidth : 0);
        if (chart.Legend is "top" or "bottom") width = Math.Max(width, left + rowLegendWidth + 20);
        width = Math.Max(width, SvgText.Measure(chart.Title, 18) + 32);
        int height = bottom + 60 + (chart.Legend == "bottom" ? 32 : 0) + (chart.HorizontalLabel.Length == 0 ? 0 : 18);
        if (chart.Legend is "left" or "right") height = Math.Max(height, top + entries.Length * 22 + 10);
        if (width > 32768 || height > 32768) return null;
        double categoryWidth = (double)plot / categories.Length;
        double X(int i) => left + (i + .5) * categoryWidth;
        double Y(double value) => bottom - (value - v.Min) / (v.Max - v.Min) * 300;
        double zero = Y(Math.Clamp(0, v.Min, v.Max));

        s.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-size=\"14\" font-family=\"sans-serif\">");
        if (chart.Title.Length > 0)
        {
            Open(s, "title", chart.TitleLine);
            Text(s, width / 2, 26, chart.Title, size: 18); s.Append("</g>");
        }
        Open(s, "vertical-axis", v.Line);
        Line(s, left, top, left, bottom);
        int ticks = (int)Math.Floor((v.Max - v.Min) / v.Step + 1e-9);
        for (int i = 0; i <= ticks; i++)
        {
            double value = v.Min + v.Step * i, y = Y(value);
            Line(s, left, y, left - 5, y);
            Text(s, left - 10, y + 5, Tick(value), anchor: "end");
        }
        if (v.Label.Length > 0)
        {
            int x = left - marginLeft + 17, y = top + 150;
            s.Append(CultureInfo.InvariantCulture, $"<g transform=\"rotate(-90 {x} {y})\">");
            Text(s, x, y, v.Label); s.Append("</g>");
        }
        s.Append("</g>");
        Open(s, "horizontal-axis", chart.HorizontalLine);
        Line(s, left, zero, right, zero);
        for (int i = 0; i < categories.Length; i++)
        {
            if (i % chart.CategorySpacing != 0) continue;
            double x = X(i); Line(s, x, zero, x, zero + 5); Text(s, x, zero + 23, categories[i]);
        }
        if (chart.HorizontalLabel.Length > 0) Text(s, left + plot / 2, bottom + 48, chart.HorizontalLabel);
        s.Append("</g>");

        int barCount = chart.Series.Count(e => e.Kind == "bar"), barIndex = 0;
        double groupWidth = categoryWidth * (barCount == 1 ? .6 : .8);
        foreach (var series in chart.Series)
        {
            string color = series.Color ?? (series.Kind is "line" or "scatter" ? "#181818" : "#F1F1F1");
            Open(s, "series " + series.Kind, series.Line);
            if (series.Kind == "bar")
            {
                for (int i = 0; i < categories.Length; i++)
                {
                    double y = Y(series.Values[i]);
                    Rect(s, X(i) - groupWidth / 2 + barIndex * groupWidth / barCount, Math.Min(zero, y), groupWidth / barCount,
                        Math.Abs(zero - y), color, "#181818", i);
                }
                barIndex++;
            }
            else if (series.Kind == "scatter")
            {
                for (int i = 0; i < categories.Length; i++)
                    s.Append(CultureInfo.InvariantCulture, $"<circle data-point-index=\"{i}\" data-chart-marker=\"true\" data-style-name=\"scatter\" cx=\"{F(X(i))}\" cy=\"{F(Y(series.Values[i]))}\" r=\"4\" fill=\"{color}\"/>");
            }
            else
            {
                bool area = series.Kind == "area";
                if (area) s.Append(CultureInfo.InvariantCulture, $"<polygon fill=\"{color}\" stroke=\"{color}\" points=\"{F(X(0))},{F(zero)} ");
                else s.Append(CultureInfo.InvariantCulture, $"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" points=\"");
                for (int i = 0; i < categories.Length; i++) s.Append(CultureInfo.InvariantCulture, $"{F(X(i))},{F(Y(series.Values[i]))} ");
                if (area) s.Append(CultureInfo.InvariantCulture, $"{F(X(categories.Length - 1))},{F(zero)}");
                s.Append("\"/>");
            }
            s.Append("</g>");
        }
        if (chart.Legend.Length > 0)
        {
            bool column = chart.Legend is "left" or "right";
            int x = chart.Legend switch { "left" => 8, "right" => right + 10, _ => left };
            int y = chart.Legend switch { "top" => top - 28, "bottom" => height - 34, _ => top };
            foreach (var series in entries)
            {
                string c = series.Color ?? (series.Kind is "line" or "scatter" ? "#181818" : "#F1F1F1");
                Open(s, "legend", series.Line);
                if (series.Kind == "line") Line(s, x, y + 6, x + 12, y + 6, c, 2);
                else if (series.Kind == "scatter") s.Append(CultureInfo.InvariantCulture, $"<circle data-chart-marker=\"true\" data-style-name=\"scatter\" cx=\"{x + 6}\" cy=\"{y + 6}\" r=\"4.2\" fill=\"{c}\"/>");
                else Rect(s, x, y, 12, 12, c, c);
                Text(s, x + 17, y + 13, series.Label, "start"); s.Append("</g>");
                if (column) y += 22; else x += SvgText.Measure(series.Label) + 36;
            }
        }
        return s.Append("</svg>").ToString();
    }

    private static void Open(StringBuilder s, string kind, int line) => s.Append(CultureInfo.InvariantCulture, $"<g class=\"{kind}\" data-source-line=\"{line}\">");
    private static void Text(StringBuilder s, double x, double y, string text, string anchor = "middle", int size = 14) =>
        s.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\" font-size=\"{size}\">{SvgText.Escape(text)}</text>");
    private static void Line(StringBuilder s, double x1, double y1, double x2, double y2, string color = "#181818", double weight = .5) =>
        s.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{color}\" stroke-width=\"{F(weight)}\"/>");
    private static void Rect(StringBuilder s, double x, double y, double w, double h, string color, string stroke, int index = -1)
    {
        s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"{color}\" stroke=\"{stroke}\" stroke-width=\"0.5\"");
        if (index >= 0) s.Append(CultureInfo.InvariantCulture, $" data-point-index=\"{index}\"");
        s.Append("/>");
    }
}
