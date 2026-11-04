using System.Text.RegularExpressions;

namespace UnoUML.Syntax;

internal sealed record ChartSourcePoint(double X, double Y, SourceToken Token, SourceToken XToken, SourceToken YToken, bool IsMissing = false);
internal sealed record ChartSourceSeries(string Kind, string Label, string? Color, string Stereotype, bool Secondary,
    bool Labels, bool Coordinates, SourceRow Row, SourceToken LabelToken, SourceToken DataToken, ChartSourcePoint[] Points,
    bool Interpolate, bool Connect, IReadOnlyDictionary<string, SourceToken> Flags);
internal sealed record ChartSourceTick(double Value, string Label, SourceToken ValueToken, SourceToken LabelToken);
internal sealed record ChartSourceAnnotation(string Label, string X, double Y, bool Arrow, SourceRow Row, SourceToken LabelToken, SourceToken XToken, SourceToken YToken, SourceToken ArrowToken);
internal sealed record ChartSourceSetting(SourceRow Row, SourceToken Value);
internal sealed record ChartSourceOption(SourceToken Token, SourceToken Value);
internal sealed class ChartSourceAxis
{
    internal bool Categorical, Grid;
    internal string Label = "", LabelPosition = "";
    internal double Min, Max = 100, Step = 20;
    internal int CategorySpacing = 1;
    internal SourceRow? Row;
    internal SourceToken LabelToken, MinToken, MaxToken, ListToken, KindToken;
    internal readonly Dictionary<string, ChartSourceOption> Options = new(StringComparer.OrdinalIgnoreCase);
    internal string[] Categories = [];
    internal SourceToken[] CategoryTokens = [];
    internal ChartSourceTick[] Ticks = [];
}

internal sealed class ChartSource
{
    internal ChartSourceAxis Horizontal = new(), Vertical = new();
    internal ChartSourceAxis? Secondary;
    internal string Title = "", Legend = "";
    internal bool HorizontalOrientation, Stacked, Coordinates;
    internal SourceToken TitleToken;
    internal int TitleLine, InsertAt, BodyStart;
    internal readonly Dictionary<string, ChartSourceSetting> Settings = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<ChartSourceSeries> Series = [];
    internal readonly List<ChartSourceAnnotation> Annotations = [];
    internal ChartSourceAxis CategoryAxis => HorizontalOrientation ? Vertical : Horizontal;
    internal ChartSourceAxis ValueAxis => HorizontalOrientation ? Horizontal : Vertical;
    internal bool ExplicitCategories => CategoryAxis.Categorical && CategoryAxis.ListToken.Length > 0;
    private static readonly Regex AxisPattern = new("^(?<axis>h-axis|v-axis|v2-axis)\\s+(?:(?<label>\"(?:[^\"\\\\]|\\\\.)*\")\\s+)?(?:(?:\\[(?<categories>[^\\[\\]]*)\\])|(?<min>" + StructuredText.NumericPattern + @")\s*-->\s*(?<max>" + StructuredText.NumericPattern + @"))(?<options>.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SeriesPattern = new("^(?<kind>bar|line|area|scatter)\\s+(?:<<(?<stereotype>[\\w.-]+)>>\\s+)?(?:(?<label>\"(?:[^\"\\\\]|\\\\.)*\")\\s+)?\\[(?<data>[^\\[\\]]+)\\](?<options>.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PairPattern = new(@"^\(\s*(?<x>" + StructuredText.NumericPattern + @")\s*,\s*(?<y>" + StructuredText.NumericPattern + @")\s*\)$", RegexOptions.CultureInvariant);
    private static readonly Regex AnnotationPattern = new("^annotation\\s+(?<label>\"(?:[^\"\\\\]|\\\\.)*\")\\s+at\\s*\\(\\s*(?<x>[^,]+?)\\s*,\\s*(?<y>" + StructuredText.NumericPattern + @")\s*\)(?:\s*<<(?<arrow>arrow)>>)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ColorPattern = new(@"^#(?:[a-fA-F0-9]{3}|[a-fA-F0-9]{6}|[a-zA-Z]+)$", RegexOptions.CultureInvariant);

    internal static ChartSource? Parse(string source)
    {
        var rows = StructuredText.Rows(source);
        if (rows == null) return null;
        var chart = new ChartSource();
        var axes = new Dictionary<string, ChartSourceAxis>(StringComparer.OrdinalIgnoreCase);
        var settings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int stage = 0, count = 0;
        foreach (var row in rows)
        {
            string line = row.Trimmed;
            if (line.Length == 0) continue;
            if (stage == 0) { if (!StructuredText.Header(line, "chart")) return null; chart.BodyStart = row.Next; stage = 1; continue; }
            if (stage == 2) return null;
            if (line.Equals("@endchart", StringComparison.OrdinalIgnoreCase)) { chart.InsertAt = row.Start; stage = 2; continue; }
            int space = line.IndexOf(' ');
            string key = (space < 0 ? line : line[..space]).ToLowerInvariant(), option = space < 0 ? "" : line[(space + 1)..].Trim();
            if (key is "title" or "legend" or "orientation" or "stackmode")
            {
                if (!settings.Add(key)) return null;
                chart.Settings.Add(key, new(row, new(row.ContentStart + line.Length - option.Length, option.Length)));
                switch (key)
                {
                    case "title":
                        if (!StructuredText.Plain(option) || option.Length == 0) return null;
                        chart.Title = option; chart.TitleToken = new(row.ContentStart + line.IndexOf(option, space, StringComparison.Ordinal), option.Length); chart.TitleLine = row.Index; break;
                    case "legend": if (option is not ("left" or "right" or "top" or "bottom")) return null; chart.Legend = option; break;
                    case "orientation": if (option is not ("horizontal" or "vertical")) return null; chart.HorizontalOrientation = option == "horizontal"; break;
                    case "stackmode": if (option is not ("grouped" or "stacked")) return null; chart.Stacked = option == "stacked"; break;
                }
                continue;
            }
            var axisMatch = AxisPattern.Match(line);
            if (axisMatch.Success)
            {
                var axis = ParseAxis(axisMatch, row);
                if (axis == null || axis.Categorical && axis.CategoryTokens.Where((token, i) => source.Substring(token.Start, token.Length) != axis.Categories[i]).Any() || !axes.TryAdd(axisMatch.Groups["axis"].Value, axis)) return null;
                continue;
            }
            var annotation = AnnotationPattern.Match(line);
            if (annotation.Success)
            {
                if (chart.Annotations.Count >= 256 || !StructuredText.Number(annotation.Groups["y"].Value, out double y)) return null;
                string label = StructuredText.Unquote(annotation.Groups["label"].Value);
                if (!StructuredText.Plain(label)) return null;
                chart.Annotations.Add(new(label, annotation.Groups["x"].Value, y, annotation.Groups["arrow"].Success, row,
                    StructuredText.Token(annotation.Groups["label"], row.ContentStart), StructuredText.Token(annotation.Groups["x"], row.ContentStart), StructuredText.Token(annotation.Groups["y"], row.ContentStart), annotation.Groups["arrow"].Success ? new(row.ContentStart + annotation.Groups["arrow"].Index - 2, annotation.Groups["arrow"].Length + 4) : default));
                continue;
            }
            var series = SeriesPattern.Match(line);
            if (!series.Success || chart.Series.Count >= 64) return null;
            string kind = series.Groups["kind"].Value.ToLowerInvariant();
            string labelText = series.Groups["label"].Success ? StructuredText.Unquote(series.Groups["label"].Value) : "";
            if (!StructuredText.Plain(labelText)) return null;
            bool secondary = false, labels = false, interpolate = false, connect = false;
            string? color = null;
            var flags = new Dictionary<string, SourceToken>(StringComparer.OrdinalIgnoreCase);
            foreach (Match optionToken in Regex.Matches(series.Groups["options"].Value, @"\S+", RegexOptions.CultureInvariant))
            {
                string flag = optionToken.Value;
                if (!flags.TryAdd(flag, new(row.ContentStart + series.Groups["options"].Index + optionToken.Index, optionToken.Length))) return null;
                if (flag is "v2" or "y2") { if (secondary) return null; secondary = true; }
                else if (flag == "labels") labels = true;
                else if (flag == "interpolate" && kind == "line") interpolate = true;
                else if (flag == "connect" && kind == "line") connect = true;
                else if (color == null && ColorPattern.IsMatch(flag)) color = flag;
                else return null;
            }
            var data = series.Groups["data"];
            if (string.IsNullOrWhiteSpace(data.Value)) return null;
            var tokens = StructuredText.ListTokens(data.Value, row.ContentStart + data.Index);
            if (tokens.Length > 512 || (count += tokens.Length) > 8192) return null;
            bool coordinates = data.Value.TrimStart().StartsWith('(');
            if (coordinates && kind is not ("line" or "scatter")) return null;
            var points = new ChartSourcePoint[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                string value = source.Substring(token.Start, token.Length);
                if (coordinates)
                {
                    var pair = PairPattern.Match(value);
                    if (!pair.Success || !StructuredText.Number(pair.Groups["x"].Value, out double x) || !StructuredText.Number(pair.Groups["y"].Value, out double y)) return null;
                    points[i] = new(x, y, token, StructuredText.Token(pair.Groups["x"], token.Start), StructuredText.Token(pair.Groups["y"], token.Start));
                }
                else
                {
                    bool missing = value.Length == 0 && kind == "line";
                    double y = 0;
                    if (!missing && !StructuredText.Number(value, out y)) return null;
                    // Java retains a final missing slot when it contains whitespace, but drops a bare trailing comma.
                    if (missing && i == tokens.Length - 1 && data.Value.EndsWith(',')) return null;
                    points[i] = new(i, y, token, default, token, missing);
                }
            }
            chart.Series.Add(new(kind, labelText, color, series.Groups["stereotype"].Value, secondary, labels, coordinates, row,
                StructuredText.Token(series.Groups["label"], row.ContentStart), new(row.ContentStart + data.Index, data.Length), points, interpolate, connect, flags));
        }
        if (stage != 2 || chart.Series.Select(s => s.Coordinates).Distinct().Count() > 1) return null;
        chart.Coordinates = chart.Series.FirstOrDefault()?.Coordinates ?? false;
        axes.TryGetValue("h-axis", out var h); axes.TryGetValue("v-axis", out var v); axes.TryGetValue("v2-axis", out chart.Secondary);
        if (chart.Secondary is { Categorical: true } || chart.HorizontalOrientation && chart.Secondary != null) return null;
        int length = chart.Series.Select(s => s.Points.Length).DefaultIfEmpty(1).Max();
        var categorical = new ChartSourceAxis { Categorical = true, Categories = Enumerable.Range(1, length).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray() };
        if (chart.HorizontalOrientation)
        {
            if (chart.Coordinates || v is { Categorical: false } || h is { Categorical: true }) return null;
            chart.Vertical = v ?? categorical; chart.Horizontal = h ?? AutoAxis(chart, false);
        }
        else
        {
            if (v is { Categorical: true } || chart.Coordinates && h is { Categorical: true }) return null;
            chart.Horizontal = h ?? (chart.Coordinates ? AutoAxis(chart, true) : categorical);
            chart.Vertical = v ?? AutoAxis(chart, false);
        }
        if (new[] { chart.Horizontal, chart.Vertical, chart.Secondary }.OfType<ChartSourceAxis>().Any(axis => !axis.Categorical && (axis.Step <= 0 || !double.IsFinite(axis.Step) || (axis.Max - axis.Min) / axis.Step > 128))) return null;
        if (chart.Secondary == null && chart.Series.Any(s => s.Secondary)) return null;
        if (chart.ExplicitCategories && chart.Series.Any(s => s.Points.Length != chart.CategoryAxis.Categories.Length)) return null;
        if (chart.Series.Any(s => s.Points.Any(p => !p.IsMissing && (!Within(p.Y, s.Secondary ? chart.Secondary! : chart.ValueAxis) || chart.Coordinates && !Within(p.X, chart.Horizontal))))) return null;
        if (chart.Stacked)
        {
            if (!chart.CategoryAxis.Categorical) return null;
            foreach (bool secondary in new[] { false, true })
            for (int i = 0; i < length; i++)
            {
                double plus = 0, minus = 0;
                foreach (var series in chart.Series.Where(s => s.Kind == "bar" && s.Secondary == secondary && s.Points.Length > i))
                { double y = series.Points[i].Y; if (y >= 0) plus += y; else minus += y; }
                var domain = secondary ? chart.Secondary : chart.ValueAxis;
                if (domain != null && (plus != 0 && !Within(plus, domain) || minus != 0 && !Within(minus, domain))) return null;
            }
        }
        foreach (var a in chart.Annotations)
        {
            string x = a.X.StartsWith('"') && a.X.EndsWith('"') ? StructuredText.Unquote(a.X) : a.X;
            if (!Within(a.Y, chart.ValueAxis) || (chart.CategoryAxis.Categorical ? !chart.CategoryAxis.Categories.Contains(x) : !StructuredText.Number(x, out double value) || !Within(value, chart.Horizontal))) return null;
        }
        return chart;
    }

    private static bool Within(double value, ChartSourceAxis axis) => value >= axis.Min && value <= axis.Max;
    private static ChartSourceAxis AutoAxis(ChartSource chart, bool x)
    {
        double[] values = chart.Series.Where(s => x || !s.Secondary).SelectMany(s => s.Points.Where(p => !p.IsMissing).Select(p => x ? p.X : p.Y)).ToArray();
        double min = values.Length == 0 ? 0 : Math.Min(0, values.Min()), max = values.Length == 0 ? 100 : Math.Max(0, values.Max());
        if (chart.Stacked && !x)
        {
            int count = chart.Series.Select(s => s.Points.Length).DefaultIfEmpty(0).Max();
            for (int i = 0; i < count; i++)
            {
                var items = chart.Series.Where(s => s.Kind == "bar" && !s.Secondary && s.Points.Length > i).Select(s => s.Points[i].Y).ToArray();
                min = Math.Min(min, items.Where(v => v < 0).Sum()); max = Math.Max(max, items.Where(v => v > 0).Sum());
            }
        }
        if (min == max) { min -= 1; max += 1; }
        return new() { Min = min, Max = max, Step = (max - min) / 5 };
    }

    private static ChartSourceAxis? ParseAxis(Match match, SourceRow row)
    {
        var axis = new ChartSourceAxis { Row = row, LabelToken = StructuredText.Token(match.Groups["label"], row.ContentStart), KindToken = StructuredText.Token(match.Groups["axis"], row.ContentStart) };
        if (axis.LabelToken.Length > 0) axis.Label = StructuredText.Unquote(match.Groups["label"].Value);
        if (!StructuredText.Plain(axis.Label)) return null;
        axis.Categorical = match.Groups["categories"].Success;
        if (axis.Categorical)
        {
            var list = match.Groups["categories"];
            axis.ListToken = new(row.ContentStart + list.Index, list.Length);
            axis.Categories = list.Value.Split(',', StringSplitOptions.TrimEntries);
            axis.CategoryTokens = StructuredText.ListTokens(list.Value, axis.ListToken.Start, coordinatePairs: false);
            if (axis.Categories.Length > 512 || axis.Categories.Any(s => !StructuredText.Category(s))) return null;
        }
        else
        {
            if (!StructuredText.Number(match.Groups["min"].Value, out axis.Min) || !StructuredText.Number(match.Groups["max"].Value, out axis.Max) || axis.Max <= axis.Min) return null;
            axis.Step = (axis.Max - axis.Min) / 5;
            axis.MinToken = StructuredText.Token(match.Groups["min"], row.ContentStart); axis.MaxToken = StructuredText.Token(match.Groups["max"], row.ContentStart);
        }
        var options = match.Groups["options"];
        string rest = options.Value; int used = 0;
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (used < rest.Length)
        {
            while (used < rest.Length && char.IsWhiteSpace(rest[used])) used++;
            if (used == rest.Length) break;
            int flagStart = used;
            var flag = Regex.Match(rest[used..], @"^(grid|spacing|ticks|label-top|label-right)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!flag.Success || !flags.Add(flag.Value)) return null;
            string name = flag.Value.ToLowerInvariant(); used += flag.Length;
            while (used < rest.Length && char.IsWhiteSpace(rest[used])) used++;
            if (name == "grid") { axis.Grid = true; axis.Options.Add(name, new(new(row.ContentStart + options.Index + flagStart, flag.Length), default)); continue; }
            if (name is "label-top" or "label-right") { if (axis.LabelPosition.Length > 0) return null; axis.LabelPosition = name; axis.Options.Add(name, new(new(row.ContentStart + options.Index + flagStart, flag.Length), default)); continue; }
            if (name == "spacing")
            {
                var number = Regex.Match(rest[used..], "^" + StructuredText.NumericPattern, RegexOptions.CultureInvariant);
                if (!number.Success || !StructuredText.Number(number.Value, out double step) || step <= 0) return null;
                if (axis.Categorical) { if (step != Math.Floor(step) || step > 512) return null; axis.CategorySpacing = (int)step; }
                else { if ((axis.Max - axis.Min) / step > 128) return null; axis.Step = step; }
                var valueToken = new SourceToken(row.ContentStart + options.Index + used, number.Length);
                used += number.Length; axis.Options.Add(name, new(new(row.ContentStart + options.Index + flagStart, used - flagStart), valueToken)); continue;
            }
            if (axis.Categorical || used >= rest.Length || rest[used] != '[') return null;
            int end = rest.IndexOf(']', used + 1);
            if (end < 0) return null;
            int start = row.ContentStart + options.Index + used + 1;
            string ticks = rest[(used + 1)..end];
            var tokens = StructuredText.ListTokens(ticks, start);
            if (tokens.Length > 128) return null;
            var parsed = new List<ChartSourceTick>();
            foreach (var token in tokens)
            {
                string item = ticks.Substring(token.Start - start, token.Length);
                var tick = Regex.Match(item, "^(?<value>" + StructuredText.NumericPattern + ")\\s*:\\s*(?<label>\"(?:[^\"\\\\]|\\\\.)*\")$", RegexOptions.CultureInvariant);
                if (!tick.Success || !StructuredText.Number(tick.Groups["value"].Value, out double number) || !Within(number, axis)) return null;
                string label = StructuredText.Unquote(tick.Groups["label"].Value);
                if (!StructuredText.Plain(label)) return null;
                parsed.Add(new(number, label, StructuredText.Token(tick.Groups["value"], token.Start), StructuredText.Token(tick.Groups["label"], token.Start)));
            }
            if (parsed.Select(t => t.Value).Distinct().Count() != parsed.Count) return null;
            axis.Ticks = parsed.ToArray();
            axis.Options.Add(name, new(new(row.ContentStart + options.Index + flagStart, end + 1 - flagStart), new(row.ContentStart + options.Index + used, end + 1 - used)));
            used = end + 1;
        }
        return axis;
    }
}
