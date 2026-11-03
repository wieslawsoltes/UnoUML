using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class TimingRenderer
{
    private static void DrawTrack(StringBuilder svg, Track track, Func<decimal, double> xAt, decimal first, decimal last,
        int width, int gutter, int tailWidth)
    {
        Group(svg, "timing-track", track.Line, track.Id, track.Kind, track.Stereotype, track.Paint);
        if (track.Paint.Background != null)
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"8\" y=\"{track.Top}\" width=\"{width - 18}\" height=\"{track.Height}\" fill=\"{track.Paint.Background}\"/>");
        if (!track.Compact)
            svg.Append(CultureInfo.InvariantCulture, $"<line data-style-name=\"timingdiagram\" data-style-metrics=\"{StyleLabel.Metrics}\" x1=\"8\" x2=\"{width - 10}\" y1=\"{track.Top}\" y2=\"{track.Top}\" stroke=\"#999\"/>");
        svg.Append("<g"); StyleLabel.Scope(svg, "timingdiagram"); svg.Append('>');
        Field(svg, track.CaptionToken, () => track.Caption.Append(svg, 10, track.Top + 2));
        svg.Append("</g>");
        if (track.Kind == "analog")
        {
            if (track.TickStep == 0)
            {
                track.LowLabel.Append(svg, gutter - 9 - track.LowLabel.Width, (int)Math.Round(Level(track, "", track.Low) - track.LowLabel.Height / 2d));
                if (track.High != track.Low) track.HighLabel.Append(svg, gutter - 9 - track.HighLabel.Width, (int)Math.Round(Level(track, "", track.High) - track.HighLabel.Height / 2d));
            }
            else
            {
                foreach (var (value, label) in track.ValueTicks)
                {
                    double y = Level(track, "", value);
                    label.Append(svg, gutter - 9 - label.Width, (int)Math.Round(y - label.Height / 2d));
                    Stroke(svg, gutter - 4, y, gutter, y);
                }
            }
        }
        foreach (var (state, label) in track.StateLabels)
        {
            var origin = track.StateSources[state];
            Group(svg, "timing-state-label", origin.Line, category: track.Kind, stereotype: track.Stereotype);
            Field(svg, origin.Token, () => label.Append(svg, gutter - track.InitialWidth - 9 - label.Width,
                (int)Math.Round(Level(track, state, 0) - label.Height / 2d)), "value");
            svg.Append("</g>");
        }
        if (track.Kind == "clock") DrawClock(svg, track, first, last, xAt, tailWidth);
        svg.Append("</g>");
        if (track.Kind == "clock") return;
        double end = xAt(last) + tailWidth;
        var firstChange = track.Changes.Count == 0 ? null : track.Changes[0];
        if (track.Initial is { } initial)
        {
            DrawState(svg, track, initial, firstChange, xAt(first) - track.InitialWidth,
                firstChange == null ? end : xAt(firstChange.Time), true);
        }
        else if (track.Kind is "binary" or "analog" && (firstChange == null || firstChange.Time > first))
        {
            double value = track.Kind == "analog" ? track.FirstAnalogValue ?? 0 : 0;
            double nextValue = track.Kind == "analog" ? firstChange?.Value ?? value : value;
            Group(svg, "timing-state", track.Line, category: track.Kind, stereotype: track.Stereotype);
            Stroke(svg, xAt(first), Level(track, "", value), firstChange == null ? end : xAt(firstChange.Time), Level(track, "", nextValue));
            svg.Append("</g>");
        }
        for (int i = 0; i < track.Changes.Count; i++)
        {
            var change = track.Changes[i];
            var next = i + 1 < track.Changes.Count ? track.Changes[i + 1] : null;
            DrawState(svg, track, change, next, xAt(change.Time), next == null ? end : xAt(next.Time), false);
        }
    }

    private static void DrawState(StringBuilder svg, Track track, Change change, Change? next, double x, double endX, bool initial)
    {
        Group(svg, initial ? "timing-initial-state" : "timing-state", change.Line, category: track.Kind,
            stereotype: track.Stereotype, paint: change.Paint, field: track.Ribbon ? null : change.StateToken);
        double y = Level(track, change.State, change.Value);
        if (IsHidden(track, change)) { svg.Append("</g>"); return; }
        if (track.Ribbon)
        {
            if (change.State == "{-}") Stroke(svg, x, y, endX, y, change.Paint);
            else
            {
                double notch = track.Kind == "rectangle" ? 0 : Math.Min(4, (endX - x) / 3);
                double half = track.RibbonHeight / 2d;
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(x)} {F(y)}l{F(notch)} -{F(half)}H{F(endX - notch)}L{F(endX)} {F(y)}l-{F(notch)} {F(half)}H{F(x + notch)}z\" fill=\"{change.Paint.Background ?? "#EEEEEE"}\"");
                StrokeAttributes(svg, change.Paint);
                svg.Append("/>");
                if (change.State != "{...}")
                    Field(svg, change.StateToken, () => change.StateLabel.Append(svg, (int)Math.Ceiling(x + notch + 4),
                        (int)Math.Round(y - change.StateLabel.Height / 2d), (int)Math.Floor(endX - x - 2 * notch - 8), "center"), "value");
            }
        }
        else if (change.States.Length == 2)
        {
            double y2 = Level(track, change.States[1], BinaryValue(change.States[1]));
            DrawIndeterminate(svg, track, change, x, endX, y, y2);
            if (next != null && !IsHidden(track, next)) DrawTransition(svg, track, change, next, endX);
        }
        else
        {
            double nextY = next == null || IsHidden(track, next) ? y : Level(track, next.State, next.Value);
            Stroke(svg, x, y, endX, track.Kind == "analog" ? nextY : y, change.Paint);
            if (track.Kind != "analog" && next != null && !IsHidden(track, next)) DrawTransition(svg, track, change, next, endX);
        }
        if (change.Annotation.Length > 0)
            Field(svg, change.AnnotationToken, () => change.AnnotationLabel.Append(svg, (int)Math.Ceiling(x + 3), track.Top + 2));
        svg.Append("</g>");
    }

    private static void DrawTransition(StringBuilder svg, Track track, Change change, Change next, double x)
    {
        double first = double.PositiveInfinity, last = double.NegativeInfinity;
        foreach (string state in change.States)
        {
            double level = Level(track, state, track.Kind == "binary" ? BinaryValue(state) : 0);
            first = Math.Min(first, level);
            last = Math.Max(last, level);
        }
        foreach (string state in next.States)
        {
            double level = Level(track, state, track.Kind == "binary" ? BinaryValue(state) : 0);
            first = Math.Min(first, level);
            last = Math.Max(last, level);
        }
        if (first != last) Stroke(svg, x, first, x, last, change.Paint);
    }

    private static void DrawIndeterminate(StringBuilder svg, Track track, Change change, double x, double endX, double y1, double y2)
    {
        double top = Math.Min(y1, y2), height = Math.Abs(y2 - y1);
        string id = "timing-hatch-" + change.Line.ToString(CultureInfo.InvariantCulture);
        string color = change.Paint.Line ?? StyleDocument.Current.Style(track.Kind, "linecolor", "#333", track.Stereotype);
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(x)}\" y=\"{F(top)}\" width=\"{F(endX - x)}\" height=\"{F(height)}\" fill=\"{change.Paint.Background ?? "#EEEEEE"}\"/>");
        // A small repeating pattern keeps a long undefined interval constant-size.
        svg.Append(CultureInfo.InvariantCulture, $"<g data-rich-paint=\"true\"><defs><pattern id=\"{id}\" patternUnits=\"userSpaceOnUse\" width=\"5\" height=\"5\"><path d=\"M0 0V5\" stroke=\"{color}\" stroke-width=\"1\"/></pattern></defs><rect x=\"{F(x)}\" y=\"{F(top)}\" width=\"{F(endX - x)}\" height=\"{F(height)}\" fill=\"url(#{id})\"/></g>");
        Stroke(svg, x, y1, endX, y1, change.Paint);
        Stroke(svg, x, y2, endX, y2, change.Paint);
    }

    private static void DrawClock(StringBuilder svg, Track track, decimal first, decimal last, Func<decimal, double> xAt, int tailWidth)
    {
        decimal phase = Phase(first, track);
        bool high = phase < track.Pulse;
        svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(xAt(first))} {F(Level(track, "", high ? 1 : 0))}");
        decimal edge = first + (high ? track.Pulse - phase : track.Period - phase);
        for (int count = 0; edge <= last && count <= 4098; count++)
        {
            high = !high;
            svg.Append(CultureInfo.InvariantCulture, $"H{F(xAt(edge))}V{F(Level(track, "", high ? 1 : 0))}");
            edge += high ? track.Pulse : track.Period - track.Pulse;
        }
        svg.Append(CultureInfo.InvariantCulture, $"H{F(xAt(last) + tailWidth)}\" fill=\"none\" stroke=\"#333\" stroke-width=\"1.5\"/>");
    }
}
