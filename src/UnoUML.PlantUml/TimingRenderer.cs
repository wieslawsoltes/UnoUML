using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>Time-proportional tracks with sparse transitions and source-addressable wave segments.</summary>
internal static partial class TimingRenderer
{
    private static string? Render(TimingDocument document)
    {
        var tracks = document.Tracks;
        var links = document.Links;
        var legend = document.Legend;
        var heading = StyleTitle.Measure(document.Title, StyleDocument.Current, 18);
        if (legend != null) legend.Labels = legend.Rows.Select(row => StyleLabel.Measure(row, "legend", 13)).ToArray();
        foreach (var link in links)
        {
            link.Caption = StyleLabel.Measure(link.Label, "arrow", 12);
            var from = tracks[link.From];
            var to = tracks[link.To];
            (from.Line <= to.Line ? from : to).Messages.Add(link);
        }
        decimal first = document.Ticks.First().Key, last = document.Ticks.Last().Key;
        if (last == first) last += tracks.Values.Where(track => track.Kind == "clock").Select(track => track.Period * 2).DefaultIfEmpty(1).Max();
        var ticks = AxisTicks(document, first, last);
        if (ticks == null) return null;
        last = Math.Max(last, ticks.Last().Key);
        var axisLabels = new Dictionary<decimal, StyleLabel>();
        var names = document.ManualAxis ? document.Anchors.GroupBy(pair => pair.Value.Value)
            .ToDictionary(group => group.Key, group => group.First().Key) : null;
        foreach (var (time, _) in ticks)
        {
            string? text = names?.GetValueOrDefault(time) ?? FormatTime(time, document.AxisFormat);
            if (text == null) return null;
            axisLabels.Add(time, StyleLabel.Measure(text, "timeline", 12));
        }
        bool fixedScale = document.ScalePixels != 0;
        double scale = fixedScale ? document.ScalePixels / (double)document.ScaleTicks :
            Math.Max(300, Math.Min(16000, ticks.Count * 68)) / (double)(last - first);
        int headerBottom = (heading == null ? 12 : heading.Height + 20) + (legend is { Above: true } ? legend.Height + 10 : 0);
        int top = headerBottom, gutter = 48, tailWidth = 16;
        foreach (var highlight in document.Highlights)
        {
            highlight.Caption = StyleLabel.Measure(highlight.Label, "highlight", 12);
            highlight.Top = top;
            if (highlight.Label.Length > 0) top += highlight.Caption.Height + 6;
            if (!fixedScale) scale = Math.Max(scale, (highlight.Caption.Width + 8) / (double)(highlight.End - highlight.Start));
        }
        int height = top;
        foreach (var track in tracks.Values)
        {
            track.StartTime = first;
            if (!MeasureTrack(track) || !FitStateLabels(track, last, fixedScale, ref scale, ref tailWidth)) return null;
            if (track.Kind == "clock" && last - first > track.Period * 2048) return null;
            gutter = Math.Max(gutter, GutterWidth(track));
            height = PlaceTrack(track, height);
        }
        if (!fixedScale)
            foreach (var link in links)
                if (link.Start != link.End) scale = Math.Max(scale, (link.Caption.Width + 12) / (double)Math.Abs(link.End - link.Start));
        double plotWidth = (double)(last - first) * scale;
        if (!double.IsFinite(plotWidth) || plotWidth > 16000) return null;
        if (document.ShowAxis)
        {
            gutter = Math.Max(gutter, axisLabels.Max(pair =>
                (int)Math.Ceiling(pair.Value.Width / 2d - (double)(pair.Key - first) * scale)) + 8);
            tailWidth = Math.Max(tailWidth, axisLabels.Values.Max(label => label.Width) / 2 + 8);
        }
        double X(decimal time) => gutter + (double)(time - first) * scale;
        int axisHeight = axisLabels.Values.Max(label => label.Height);
        Dictionary<decimal, int>? axisRows = document.ManualAxis ? PlaceAxisLabels(axisLabels, X, out axisHeight) : null;
        int width = Math.Max((int)Math.Ceiling(gutter + plotWidth + tailWidth + 12), (heading?.Width ?? 0) + 28);
        if (legend != null) width = Math.Max(width, legend.Width + 16);
        foreach (var track in tracks.Values)
        {
            foreach (var note in track.Notes) width = Math.Max(width, (int)Math.Ceiling(X(note.Time)) + note.Width + 12);
            foreach (var constraint in track.Constraints)
                width = Math.Max(width, (int)Math.Ceiling(ConstraintLeft(constraint, X)) + constraint.Caption.Width + 12);
        }
        foreach (var highlight in document.Highlights)
            width = Math.Max(width, (int)Math.Ceiling(X(highlight.Start)) + highlight.Caption.Width + 12);
        foreach (var link in links)
        {
            double x1 = X(link.Start), x2 = X(link.End);
            if (link.From == link.To)
                link.CaptionLeft = link.Start == link.End ? x1 + 8 : Math.Max(8, (x1 + x2 - link.Caption.Width) / 2);
            else
            {
                var (y1, y2) = MessageLevels(tracks[link.From], tracks[link.To], link);
                double topFraction = Math.Clamp((link.CaptionTop - y1) / (y2 - y1), 0, 1);
                double bottomFraction = Math.Clamp((link.CaptionTop + link.Caption.Height - y1) / (y2 - y1), 0, 1);
                link.CaptionLeft = Math.Max(x1 + (x2 - x1) * topFraction, x1 + (x2 - x1) * bottomFraction) + 8;
            }
            width = Math.Max(width, (int)Math.Ceiling(link.CaptionLeft) + link.Caption.Width + 16);
        }
        int axisY = height + 2;
        height += document.ShowAxis ? axisHeight + 18 : 12;
        if (legend is { Above: false }) height += legend.Height + 10;
        if (width > 32768 || height > 32768) return null;
        var svg = new StringBuilder(512 + ticks.Count * 70 + tracks.Count * 200);
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\">");
        heading?.Append(svg, StyleDocument.Current, width / 2, 8, document.TitleLine);
        if (legend != null) DrawLegend(svg, legend, width, height, headerBottom);
        DrawHighlights(svg, document.Highlights, X, top, axisY, false);
        svg.Append("<g"); StyleLabel.Scope(svg, "timegrid"); svg.Append('>');
        foreach (var (time, _) in ticks)
        {
            double x = X(time);
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(x)}\" x2=\"{F(x)}\" y1=\"{top}\" y2=\"{axisY}\" stroke=\"#B8B8B8\" stroke-dasharray=\"2 3\"/>");
        }
        svg.Append("</g>");
        foreach (var track in tracks.Values)
        {
            DrawTrack(svg, track, X, first, last, width, gutter, tailWidth);
            DrawConstraints(svg, track, X);
            DrawNotes(svg, track, X);
        }
        foreach (var link in links) DrawLink(svg, link, tracks[link.From], tracks[link.To], X);
        DrawHighlights(svg, document.Highlights, X, top, axisY, true);
        if (document.ShowAxis)
        {
            svg.Append("<g"); StyleLabel.Scope(svg, "timeline"); svg.Append('>');
            Stroke(svg, gutter, axisY, X(last) + tailWidth, axisY);
            double lastRight = double.NegativeInfinity;
            foreach (var (time, row) in ticks)
            {
                var label = axisLabels[time];
                double x = X(time), halfWidth = label.Width / 2d;
                if (row >= 0) Group(svg, "time", row, category: "timeline");
                Stroke(svg, x, axisY, x, axisY + 4);
                if (axisRows != null || x - halfWidth > lastRight)
                {
                    label.Append(svg, (int)Math.Round(x - halfWidth), axisY + 6 + (axisRows?.GetValueOrDefault(time) ?? 0), alignment: "center");
                    lastRight = x + halfWidth + 4;
                }
                if (row >= 0) svg.Append("</g>");
            }
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }

    private static void Field(StringBuilder svg, TextToken? token, Action draw, string role = "label")
    {
        if (token is { Length: > 0 } field)
            svg.Append(CultureInfo.InvariantCulture, $"<g data-edit-start=\"{field.Start}\" data-edit-length=\"{field.Length}\" data-edit-kind=\"{field.Kind}\" data-edit-role=\"{role}\">");
        draw();
        if (token is { Length: > 0 }) svg.Append("</g>");
    }

    private static (double From, double To) MessageLevels(Track from, Track to, Link link)
    {
        double y1 = SignalY(from, link.Start), y2 = SignalY(to, link.End);
        int direction = from == to ? 1 : Math.Sign(to.Top - from.Top);
        if (from.Ribbon) y1 += direction * from.RibbonHeight / 2d;
        if (to.Ribbon) y2 += (from == to ? 1 : -direction) * to.RibbonHeight / 2d;
        return (y1, y2);
    }

    private static double SignalY(Track track, decimal time)
    {
        if (track.Kind == "clock") return Level(track, "", Phase(time, track) < track.Pulse ? 1 : 0);
        var events = track.Changes;
        int low = 0, high = events.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (events[mid].Time <= time) low = mid + 1; else high = mid;
        }
        var last = low == 0 ? track.Initial : events[low - 1];
        if (low == 0 && track.Kind == "analog")
        {
            double initial = track.Initial?.Value ?? track.FirstAnalogValue ?? 0;
            if (events.Count > 0 && events[0].Time > track.StartTime)
                initial += (events[0].Value - initial) * (double)((time - track.StartTime) / (events[0].Time - track.StartTime));
            return Level(track, "", initial);
        }
        if (last == null) return track.SignalTop + track.SignalHeight;
        if (IsHidden(track, last)) return track.SignalTop + track.SignalHeight / 2d;
        if (last.States.Length == 2)
            return (Level(track, last.States[0], BinaryValue(last.States[0])) + Level(track, last.States[1], BinaryValue(last.States[1]))) / 2;
        double value = last.Value;
        if (track.Kind == "analog" && low > 0 && low < events.Count)
        {
            var next = events[low];
            value += (next.Value - value) * (double)((time - last.Time) / (next.Time - last.Time));
        }
        return Level(track, last.State, value);
    }

    private static bool IsHidden(Track track, Change? change) => track.Kind != "binary" && change?.State == "{hidden}";

    private static double Level(Track track, string state, double value) => track.Kind switch
    {
        "robust" => track.SignalTop + track.SignalHeight - track.StateOffsets.GetValueOrDefault(state, track.SignalHeight / 2d),
        "concise" or "rectangle" => track.SignalTop + track.RibbonHeight / 2d,
        "analog" => track.SignalTop + (track.High > track.Low ? track.SignalHeight * (1 - (value - track.Low) / (track.High - track.Low)) : track.SignalHeight / 2d),
        _ => track.SignalTop + track.SignalHeight * (1 - value)
    };

    private static decimal Phase(decimal time, Track track) => ((time - track.Offset) % track.Period + track.Period) % track.Period;
    private static string F(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
    private static void Group(StringBuilder svg, string kind, int line, string? alias = null, string category = "timeline", string stereotype = "", Paint paint = default, TextToken? field = null)
    {
        svg.Append(CultureInfo.InvariantCulture, $"<g class=\"{kind}\" data-source-line=\"{line}\"");
        if (alias != null) svg.Append(CultureInfo.InvariantCulture, $" data-qualified-name=\"{SvgText.Escape(alias)}\"");
        StyleLabel.Scope(svg, category, stereotype);
        if (paint.Background != null) svg.Append(" data-style-fill=\"explicit\"");
        if (paint.Line != null) svg.Append(" data-style-linecolor=\"explicit\"");
        if (paint.Dash != null) svg.Append(" data-style-linestyle=\"explicit\"");
        if (paint.Thickness != null) svg.Append(" data-style-linethickness=\"explicit\"");
        if (field is { Length: > 0 } token)
            svg.Append(CultureInfo.InvariantCulture, $" data-edit-start=\"{token.Start}\" data-edit-length=\"{token.Length}\" data-edit-kind=\"{token.Kind}\" data-edit-role=\"value\"");
        svg.Append('>');
    }
    private static void Stroke(StringBuilder svg, double x, double y, double endX, double endY, Paint paint = default)
    {
        svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(x)}\" y1=\"{F(y)}\" x2=\"{F(endX)}\" y2=\"{F(endY)}\"");
        StrokeAttributes(svg, paint);
        svg.Append("/>");
    }
    private static void StrokeAttributes(StringBuilder svg, Paint paint)
    {
        svg.Append(" stroke=\"").Append(paint.Line ?? "#333").Append("\" stroke-width=\"")
            .Append(paint.Thickness?.ToString(CultureInfo.InvariantCulture) ?? "1.5").Append('"');
        if (paint.Dash != null) svg.Append(" stroke-dasharray=\"").Append(paint.Dash).Append('"');
    }
}
