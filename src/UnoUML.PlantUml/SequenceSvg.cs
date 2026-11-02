using System.Globalization;
using System.Text;
using static UnoUML.PlantUml.SvgText;

namespace UnoUML.PlantUml;

internal static partial class SequenceRenderer
{
    private static string WriteSvg(Diagram d)
    {
        var s = new StringBuilder(512 + d.Lanes.Count * 420 + d.Steps.Count * 240);
        s.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{d.Width}\" height=\"{d.Height}\" viewBox=\"0 0 {d.Width} {d.Height}\" font-family=\"sans-serif\" font-size=\"14\">");
        var background = d.Style("backgroundcolor", "none");
        if (background != "none") Rect(s, 0, 0, d.Width, d.Height, background, "none");
        if (d.Title != null)
        {
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"title\" data-source-line=\"{d.TitleLine}\">");
            Text(s, d.Width / 2, 27, d.Title, size: 18, middle: true, bold: true); s.Append("</g>");
        }
        string lifeStroke = d.Style("lifelinebordercolor", "#181818");
        for (int i = 0; i < d.Lanes.Count; i++)
        {
            var l = d.Lanes[i];
            LaneGroup(s, l, i, "lifeline");
            Line(s, l.X, d.HeadBottom, l.X, d.LifelineEnd, lifeStroke, true);
            s.Append("</g>");
        }
        // Paint frames before their descendants; source-linked header groups remain individually hittable.
        foreach (var step in d.Steps)
        {
            if (step.Op != Op.Open) continue;
            int left = 5 + step.Nest * 7;
            int width = d.Width - left * 2;
            Rect(s, left, step.Top, width, d.Steps[step.End].Bottom - step.Top, "none", "#424242");
        }
        foreach (var bar in d.Bars.OrderBy(b => b.Depth))
        {
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"activation\" data-source-line=\"{bar.Line}\">");
            Rect(s, d.Lanes[bar.Lane].X - 5 + bar.Depth * 5, bar.Start, 10, Math.Max(2, bar.End - bar.Start), bar.Fill, "#454545");
            s.Append("</g>");
        }
        for (int i = 0; i < d.Lanes.Count; i++)
        {
            Head(s, d, i, false);
            if (d.Footbox) Head(s, d, i, true);
        }
        int count = 0;
        foreach (var step in d.Steps)
        {
            switch (step.Op)
            {
                case Op.Send: Message(s, d, step, ++count); break;
                case Op.Note: DrawNote(s, d, step); break;
                case Op.Open:
                {
                    int x = 5 + step.Nest * 7, y = step.Top, labelWidth = Measure(step.Variant) + 19;
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"fragment\" data-source-line=\"{step.Line}\"><path d=\"M{x} {y}h{labelWidth}v13l-7 7H{x}z\" fill=\"#EEE\" stroke=\"#424242\"/>");
                    Text(s, x + 6, y + 15, step.Variant, 13, bold: true);
                    if (step.Text.Length > 0) Text(s, x + labelWidth + 8, y + 15, step.Text, 13);
                    s.Append("</g>"); break;
                }
                case Op.Branch:
                {
                    int x = 5 + step.Nest * 7;
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"branch\" data-source-line=\"{step.Line}\">");
                    Line(s, x, step.Top, d.Width - x, step.Top, "#424242", true);
                    Text(s, x + 8, step.Top + 18, "[" + step.Text + "]", 13); s.Append("</g>"); break;
                }
                case Op.Divider:
                case Op.Delay:
                    s.Append(CultureInfo.InvariantCulture, $"<g data-source-line=\"{step.Line}\">");
                    if (step.Op == Op.Divider)
                    {
                        Line(s, 5, step.Top + 15, d.Width - 5, step.Top + 15, "#363636");
                        Rect(s, (d.Width - step.TextWidth) / 2 - 9, step.Top + 3, step.TextWidth + 18, 24, "#EEE", "#565656");
                    }
                    Text(s, d.Width / 2, step.Top + 20, step.Text, 13, middle: true, bold: step.Op == Op.Divider);
                    s.Append("</g>"); break;
            }
        }
        if (d.Legend != null)
        {
            int w = d.LegendWidth, h = d.Legend.Length * 17 + 10;
            int x = d.LegendAlignment switch { "left" => 8, "right" => d.Width - 8 - w, _ => (d.Width - w) / 2 };
            int y = d.Height - 8 - h;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"legend\" data-source-line=\"{d.LegendLine}\">");
            Rect(s, x, y, w, h, "#DDDDDD", "#888888");
            foreach (var row in d.Legend) { y += 17; Text(s, x + 8, y, row, 13); }
            s.Append("</g>");
        }
        return s.Append("</svg>").ToString();
    }

    private static void LaneGroup(StringBuilder s, Lane lane, int index, string suffix)
    {
        string kind = suffix == "lifeline" ? "participant-lifeline" : "participant participant-" + suffix;
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"{kind}\" data-source-line=\"{lane.Line}\" data-qualified-name=\"{Escape(lane.Id)}\" data-entity-uid=\"part{index + 1}\" id=\"part{index + 1}-{suffix}\">");
    }

    private static void Head(StringBuilder s, Diagram d, int index, bool foot)
    {
        var l = d.Lanes[index];
        int x = l.X, w = l.Width, top = foot ? d.LifelineEnd : d.HeadBottom - d.HeaderHeight;
        bool box = l.Kind is "participant" or "collections";
        if (!foot && box) top = d.HeadBottom - 31;
        string key = l.Kind == "actor" ? "actor" : "participant";
        var fill = l.Fill ?? d.Style(key + "backgroundcolor", "#E2E2F0");
        var stroke = d.Style(key + "bordercolor", "#181818");
        LaneGroup(s, l, index, foot ? "tail" : "head");
        if (box)
        {
            if (l.Kind == "collections") Rect(s, x - w / 2 + 4, top, w - 4, 27, fill, stroke);
            Rect(s, x - w / 2, top + (l.Kind == "collections" ? 4 : 0), w, 27, fill, stroke);
            Text(s, x, top + 19, l.Name, middle: true, color: d.Style(key + "fontcolor", "#000"));
        }
        else
        {
            int iconY = top + (foot ? 19 : 2);
            s.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({x} {iconY})\" fill=\"{fill}\" stroke=\"{stroke}\">");
            s.Append(l.Kind switch
            {
                "actor" => "<circle cx=\"0\" cy=\"6\" r=\"6\"/><path d=\"M0 13v11m-10-7h20M0 24l-9 11m9-11 9 11\" fill=\"none\"/>",
                "database" => "<path d=\"M-15 7c0-7 30-7 30 0v20c0 7-30 7-30 0z\"/><ellipse cx=\"0\" cy=\"7\" rx=\"15\" ry=\"5\"/>",
                "boundary" => "<circle cx=\"4\" cy=\"18\" r=\"11\"/><path d=\"M-8 18h-8m0-11v22\" fill=\"none\"/>",
                "control" => "<circle cx=\"0\" cy=\"19\" r=\"11\"/><path d=\"m-2 8 6-5m-6 5 7 2\" fill=\"none\"/>",
                "entity" => "<circle cx=\"0\" cy=\"16\" r=\"11\"/><path d=\"M-13 29h26\"/>",
                "queue" => "<path d=\"M-11 6h20c9 0 9 23 0 23h-20c-8 0-8-23 0-23z\"/><ellipse cx=\"9\" cy=\"17.5\" rx=\"6\" ry=\"11.5\"/>",
                _ => ""
            });
            s.Append("</g>");
            Text(s, x, top + (foot ? 14 : 52), l.Name, middle: true, color: d.Style(key + "fontcolor", "#000"));
        }
        s.Append("</g>");
    }

    private static void Message(StringBuilder s, Diagram d, Step step, int ordinal)
    {
        bool self = step.A == step.B;
        int from = d.Lanes[step.A].X + Math.Max(0, step.FromDepth - 1) * 5;
        int to = d.Lanes[step.B].X + Math.Max(0, step.ToDepth - 1) * 5;
        int dir = from <= to ? 1 : -1;
        if (step.FromDepth > 0) from += 5 * dir;
        if (step.ToDepth > 0) to += (self ? 5 : -5 * dir);
        string stroke = d.Style("arrowcolor", "#181818");
        bool dashed = step.Variant.Contains("--"), open = step.Variant.Contains(">>") || step.Variant.Contains("<<");
        int y = step.Bottom - 5;
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"message\" id=\"msg{ordinal}\" data-entity-1=\"part{step.A + 1}\" data-entity-2=\"part{step.B + 1}\" data-source-line=\"{step.Line}\">");
        if (self)
        {
            int loopWidth = Math.Max(35, step.TextWidth + 12);
            s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{from} {y - 15}h{loopWidth}v15H{to}\" fill=\"none\" stroke=\"{stroke}\"");
            if (dashed) s.Append(" stroke-dasharray=\"3 3\"");
            s.Append("/>");
            dir = -1;
        }
        else Line(s, from, y, to, y, stroke, dashed);
        s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{to - dir * 9} {y - 4}L{to} {y}l{-dir * 9} 4");
        s.Append(open ? "\"" : "z\"");
        s.Append(CultureInfo.InvariantCulture, $" stroke=\"{stroke}\" fill=\"{(open ? "none" : stroke)}\"/>");
        int labelX = (self ? from : Math.Min(from, to)) + 7, labelY = y - (self ? 20 : 6);
        if (step.Number > 0)
        {
            var n = step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Text(s, labelX, labelY, n, 13, bold: true);
            labelX += Measure(n, 13) + 6;
        }
        if (step.Text.Length > 0) Text(s, labelX, labelY, step.Text, 13);
        s.Append("</g>");
    }

    private static void DrawNote(StringBuilder s, Diagram d, Step step)
    {
        int w = step.TextWidth + 22, h = step.Bottom - step.Top - 9;
        int a = d.Lanes[step.A].X, b = d.Lanes[step.B].X, y = step.Top + 4;
        int x = step.Variant switch { "left of" => a - 10 - w, "right of" => a + 10, _ => (a + b - w) / 2 };
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"note\" data-source-line=\"{step.Line}\"><path d=\"M{x} {y}h{w - 8}l8 8v{h - 8}h{-w}zM{x + w - 8} {y}v8h8\" fill=\"{step.Color ?? "#FEFFDD"}\" stroke=\"#454545\"/>");
        foreach (var row in step.Text.Split('\n')) { y += 17; Text(s, x + 6, y, row, 13); }
        s.Append("</g>");
    }

    private static void Rect(StringBuilder s, int x, int y, int w, int h, string fill, string stroke) =>
        s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");

    private static void Line(StringBuilder s, int x1, int y1, int x2, int y2, string stroke, bool dashed = false)
    {
        s.Append(CultureInfo.InvariantCulture, $"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"{stroke}\"");
        if (dashed) s.Append(" stroke-dasharray=\"3 3\"");
        s.Append("/>");
    }

    private static void Text(StringBuilder s, int x, int y, string text, int size = 14, bool middle = false, bool bold = false, string color = "#000")
    {
        s.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" font-size=\"{size}\" fill=\"{color}\" xml:space=\"preserve\"");
        if (middle) s.Append(" text-anchor=\"middle\"");
        if (bold) s.Append(" font-weight=\"bold\"");
        s.Append('>').Append(Escape(text)).Append("</text>");
    }
}
