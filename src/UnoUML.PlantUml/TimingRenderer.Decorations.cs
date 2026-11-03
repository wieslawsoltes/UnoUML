using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class TimingRenderer
{
    private static void DrawLegend(StringBuilder svg, Legend legend, int width, int height, int headerBottom)
    {
        int x = legend.Align switch { "left" => 8, "right" => width - legend.Width - 8, _ => (width - legend.Width) / 2 };
        int y = legend.Above ? headerBottom - legend.Height - 10 : height - legend.Height - 8;
        Group(svg, "legend", legend.Line, category: "legend");
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{legend.Width}\" height=\"{legend.Height}\" fill=\"#DDD\" stroke=\"#888\"/>");
        y += 5;
        for (int i = 0; i < legend.Labels.Length; i++)
        {
            var label = legend.Labels[i];
            Field(svg, legend.Tokens[i], () => label.Append(svg, x + 8, y));
            y += label.Height + 4;
        }
        svg.Append("</g>");
    }

    private static void DrawHighlights(StringBuilder svg, List<Highlight> highlights, Func<decimal, double> xAt, int top, int bottom, bool foreground)
    {
        foreach (var highlight in highlights)
        {
            Group(svg, foreground ? "timing-highlight" : "timing-highlight-background", highlight.Line, category: "highlight", paint: highlight.Paint);
            double x1 = xAt(highlight.Start), x2 = xAt(highlight.End);
            if (foreground)
            {
                Stroke(svg, x1, top, x1, bottom, highlight.Paint);
                Stroke(svg, x2, top, x2, bottom, highlight.Paint);
                if (highlight.Label.Length > 0)
                    Field(svg, highlight.Token, () => highlight.Caption.Append(svg, (int)Math.Ceiling(x1 + 3), highlight.Top));
            }
            else
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(x1)}\" y=\"{top}\" width=\"{F(x2 - x1)}\" height=\"{bottom - top}\" fill=\"{highlight.Paint.Background ?? "#FFF4B8"}\"/>");
            svg.Append("</g>");
        }
    }

    private static double ConstraintLeft(Constraint constraint, Func<decimal, double> xAt) =>
        Math.Max(8, (xAt(constraint.Start) + xAt(constraint.End) - constraint.Caption.Width) / 2);

    private static void DrawConstraints(StringBuilder svg, Track track, Func<decimal, double> xAt)
    {
        foreach (var constraint in track.Constraints)
        {
            Group(svg, "timing-constraint", constraint.Line, category: "constraintarrow", paint: constraint.Paint);
            double x1 = xAt(constraint.Start), x2 = xAt(constraint.End);
            double y = constraint.Top + constraint.Caption.Height + 5;
            Stroke(svg, x1, y, x2, y, constraint.Paint);
            var guide = constraint.Paint with { Dash = "2 3" };
            Stroke(svg, x1, y + 4, x1, track.SignalTop, guide);
            Stroke(svg, x2, y + 4, x2, track.SignalTop, guide);
            double direction = x2 > x1 ? 0 : Math.PI;
            ArrowHead(svg, x1, y, direction + Math.PI, constraint.Paint, "constraintarrow");
            ArrowHead(svg, x2, y, direction, constraint.Paint, "constraintarrow");
            Field(svg, constraint.Token, () => constraint.Caption.Append(svg, (int)Math.Round(ConstraintLeft(constraint, xAt)), constraint.Top));
            svg.Append("</g>");
        }
    }

    private static void DrawNotes(StringBuilder svg, Track track, Func<decimal, double> xAt)
    {
        foreach (var note in track.Notes)
        {
            Group(svg, "timing-note", note.Line, category: "note", stereotype: note.Stereotype);
            double x = xAt(note.Time), y = note.Top;
            int width = note.Width, height = note.Height;
            double pointY = note.Above ? track.SignalTop : track.SignalTop + track.SignalHeight;
            Stroke(svg, x, note.Above ? y + height : y, x, pointY, new(Dash: "2 3"));
            svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(x)} {F(y)}h{width - 8}l8 8v{height - 8}h-{width}z\" fill=\"#FFF6C6\" stroke=\"#555\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(x + width - 8)} {F(y)}v8h8\" fill=\"none\" stroke=\"#555\"/>");
            int rowTop = note.Top + 5;
            for (int i = 0; i < note.Labels.Length; i++)
            {
                var label = note.Labels[i];
                Field(svg, note.Rows[i].Token, () => label.Append(svg, (int)Math.Ceiling(x + 7), rowTop));
                rowTop += label.Height + 4;
            }
            svg.Append("</g>");
        }
    }

    private static void DrawLink(StringBuilder svg, Link link, Track from, Track to, Func<decimal, double> xAt)
    {
        double x1 = xAt(link.Start), x2 = xAt(link.End);
        var (y1, y2) = MessageLevels(from, to, link);
        double angle = Math.Atan2(y2 - y1, x2 - x1);
        Group(svg, "timing-message", link.Line, category: "arrow", paint: link.Paint);
        if (link.From == link.To)
        {
            double messageY = link.CaptionTop + link.Caption.Height + 4;
            svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(x1)} {F(y1)}V{F(messageY)}");
            if (x1 == x2)
            {
                svg.Append(CultureInfo.InvariantCulture, $"H{F(link.CaptionLeft + link.Caption.Width + 8)}V{F(y2)}H{F(x2)}");
                angle = Math.PI;
            }
            else
            {
                svg.Append(CultureInfo.InvariantCulture, $"H{F(x2)}V{F(y2)}");
                angle = -Math.PI / 2;
            }
            svg.Append("\" fill=\"none\"");
            StrokeAttributes(svg, link.Paint);
            svg.Append("/>");
        }
        else Stroke(svg, x1, y1, x2, y2, link.Paint);
        ArrowHead(svg, x2, y2, angle, link.Paint, "arrow");
        Field(svg, link.Token, () => link.Caption.Append(svg, (int)Math.Round(link.CaptionLeft), link.CaptionTop));
        svg.Append("</g>");
    }

    private static void ArrowHead(StringBuilder svg, double x, double y, double angle, Paint paint, string category)
    {
        string color = paint.Line ?? StyleDocument.Current.Style(category, "linecolor", "#333");
        svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(x)} {F(y)}l{F(-8 * Math.Cos(angle - .45))} {F(-8 * Math.Sin(angle - .45))}L{F(x - 8 * Math.Cos(angle + .45))} {F(y - 8 * Math.Sin(angle + .45))}z\" fill=\"{color}\"/>");
    }
}
