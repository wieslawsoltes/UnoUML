using System.Globalization;
using System.Text;
using static UnoUML.PlantUml.SvgText;

namespace UnoUML.PlantUml;

internal static partial class SequenceRenderer
{
    private static string WriteSvg(Diagram d, string pageAttributes = "")
    {
        var s = new StringBuilder(512 + d.Lanes.Count * 420 + d.Steps.Count * 240);
        s.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\"{pageAttributes} width=\"{d.Width}\" height=\"{d.Height}\" viewBox=\"0 0 {d.Width} {d.Height}\" font-family=\"{d.Style("defaultfontname","sans-serif")}\" font-size=\"14\">");
        var background = d.Style("backgroundcolor", "none");
        if (background != "none") Rect(s, 0, 0, d.Width, d.Height, background, "none");
        string defaultFont = d.Style("defaultfontcolor","#000");
        if (d.Heading is { } heading)
            heading.Append(s, d.Skin, d.Width / 2, 9 + heading.FontSize - SvgText.Ascent("M", heading.FontSize), d.TitleLine);
        string lifeStroke = d.Style("lifelinebordercolor", "#181818");
        for (int i = 0; i < d.Lanes.Count; i++)
        {
            var l = d.Lanes[i];
            LaneGroup(s, l, i, "lifeline", d.IdPrefix);
            int start = l.LifeStart ?? d.HeadBottom, end = l.LifeEnd ?? d.LifelineEnd;
            if (start <= end) Line(s, l.X, start, l.X, end, lifeStroke, true);
            s.Append("</g>");
        }
        // Paint frames before their descendants; source-linked header groups remain individually hittable.
        foreach (var frame in d.Frames)
        {
            int left = 5 + frame.Nest * 7, right = d.Width - left;
            s.Append("<g data-style-name=\"group\">");
            if (frame.DrawTop && frame.DrawBottom)
            {
                Rect(s, left, frame.Start, right - left, frame.End - frame.Start, d.Skin.Style("group", "backgroundcolor", "none"), "#424242");
                s.Append("</g>"); continue;
            }
            Line(s, left, frame.Start, left, frame.End, "#424242");
            Line(s, right, frame.Start, right, frame.End, "#424242");
            if (frame.DrawTop) Line(s, left, frame.Start, right, frame.Start, "#424242");
            if (frame.DrawBottom) Line(s, left, frame.End, right, frame.End, "#424242");
            s.Append("</g>");
        }
        foreach (var bar in d.Bars.OrderBy(b => b.Depth))
        {
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"activation\" data-style-name=\"lifeline activationbox\" data-style-fill=\"explicit\" data-source-line=\"{bar.Line}\">");
            Rect(s, d.Lanes[bar.Lane].X - 5 + bar.Depth * 5, bar.Start, 10, Math.Max(2, bar.End - bar.Start), bar.Fill, d.Style("lifelinebordercolor", "#454545"));
            s.Append("</g>");
        }
        for (int i = 0; i < d.Lanes.Count; i++)
        {
            if (d.Lanes[i].ShowHead) Head(s, d, i, false);
            if (d.Footbox) Head(s, d, i, true);
        }
        int count = 0;
        foreach (var step in d.Steps)
        {
            switch (step.Op)
            {
                case Op.Send: Message(s, d, step, ++count); break;
                case Op.Note: DrawNote(s, d, step); break;
                case Op.Reference: DrawReference(s, d, step); break;
                case Op.Destroy:
                {
                    int x = d.Lanes[step.A].X, y = step.Anchor;
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"destruction\" data-style-name=\"destroy\" data-source-line=\"{step.Line}\" data-entity-uid=\"{d.IdPrefix}part{step.A + 1}\"><path d=\"M{x-8} {y-8}l16 16m-16 0l16-16\" stroke=\"#A80036\" stroke-width=\"2\" fill=\"none\"/></g>");
                    break;
                }
                case Op.Open:
                {
                    int x = 5 + step.Nest * 7 + step.Margin.Left, y = step.Top, labelWidth = step.HeaderWidth;
                    int headerHeight = step.HeaderSize + 7 + step.HeaderPadding.Vertical;
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"fragment\" data-style-name=\"groupheader\" data-source-line=\"{step.Line}\"><path d=\"M{x} {y}h{labelWidth}v{headerHeight - 7}l-7 7H{x}z\" fill=\"#EEE\" stroke=\"#424242\"/>");
                    Text(s, x + 6 + step.HeaderPadding.Left, y + step.HeaderPadding.Top + SvgText.Ascent("M", step.HeaderSize) + 2, step.Variant, step.HeaderSize, bold: true, color: defaultFont);
                    s.Append("<g data-style-name=\"group\">");
                    int rowTop = y + 2 + step.Padding.Top + step.FontSize - SvgText.Ascent("M", step.FontSize);
                    foreach (string row in step.Rows)
                    {
                        Text(s, x + labelWidth + 8 + step.Padding.Left, rowTop + SvgText.Ascent(row, step.FontSize), row, step.FontSize, bold: true, color: defaultFont);
                        rowTop += SvgText.Height(row, step.FontSize) + 4;
                    }
                    s.Append("</g></g>"); break;
                }
                case Op.Branch:
                {
                    int x = 5 + step.Nest * 7;
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"branch\" data-style-name=\"group\" data-source-line=\"{step.Line}\">");
                    Line(s, x, step.Top, d.Width - x, step.Top, "#424242", true);
                    int rowTop = step.Top + 5 + step.FontSize - SvgText.Ascent("M", step.FontSize);
                    foreach (string row in step.Rows)
                    {
                        Text(s, x + 8 + step.Padding.Left, rowTop + SvgText.Ascent(row, step.FontSize), "[" + row + "]", step.FontSize, bold: true, color: defaultFont);
                        rowTop += SvgText.Height(row, step.FontSize) + 4;
                    }
                    s.Append("</g>"); break;
                }
                case Op.Divider:
                case Op.Delay:
                {
                    s.Append(CultureInfo.InvariantCulture, $"<g data-style-name=\"{StepCategory(step)}\" data-source-line=\"{step.Line}\">");
                    if (step.Op == Op.Divider)
                    {
                        Line(s, 5, step.Top + step.TextHeight / 2 + 9, d.Width - 5, step.Top + step.TextHeight / 2 + 9, "#363636");
                        Rect(s, (d.Width - step.TextWidth) / 2 - 9, step.Top + 3, step.TextWidth + 18, step.TextHeight + 11, "#EEE", "#565656");
                    }
                    int rowTop = step.Top + 7 + step.FontSize - SvgText.Ascent("M", step.FontSize);
                    foreach (string row in step.Rows)
                    {
                        Text(s, d.Width / 2, rowTop + SvgText.Ascent(row, step.FontSize), row, step.FontSize, middle: true, bold: step.Op == Op.Divider, color: defaultFont);
                        rowTop += SvgText.Height(row, step.FontSize) + 4;
                    }
                    s.Append("</g>"); break;
                }
            }
        }
        if (d.Legend != null)
        {
            int legendSize = d.Skin.Size("legendfontsize", 13);
            int w = d.LegendWidth, h = StyleText.Height(d.Legend, legendSize) + 14;
            int x = d.LegendAlignment switch { "left" => 8, "right" => d.Width - 8 - w, _ => (d.Width - w) / 2 };
            int y = d.Height - 8 - h;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"legend\" data-style-name=\"legend\" data-source-line=\"{d.LegendLine}\">");
            Rect(s, x, y, w, h, "#DDDDDD", "#888888");
            foreach (var row in d.Legend) { Text(s, x + 8, y + legendSize + 4 - SvgText.Ascent("M", legendSize) + SvgText.Ascent(row, legendSize), row, legendSize, color: defaultFont); y += SvgText.Height(row, legendSize) + 4; }
            s.Append("</g>");
        }
        return StyleDrawing.Apply(s.Append("</svg>").ToString(), d.Skin);
    }

    private static void LaneGroup(StringBuilder s, Lane lane, int index, string suffix, string prefix)
    {
        string kind = suffix == "lifeline" ? "participant-lifeline" : "participant participant-" + suffix;
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"{kind}\" data-style-name=\"{(suffix == "lifeline" ? "lifeline" : lane.Kind)}\" data-style-stereotype=\"{Escape(lane.Stereotype)}\"{(lane.Fill == null ? "" : " data-style-fill=\"explicit\"")} data-source-line=\"{lane.Line}\" data-qualified-name=\"{Escape(lane.Id)}\" data-entity-uid=\"{prefix}part{index + 1}\" id=\"{prefix}part{index + 1}-{suffix}\">");
    }

    private static void Head(StringBuilder s, Diagram d, int index, bool foot)
    {
        var l = d.Lanes[index];
        int x = l.X, w = l.Width, top = foot ? d.LifelineEnd : d.HeadBottom - d.HeaderHeight;
        bool box = l.Kind is "participant" or "collections";
        string key = l.Kind;
        int size = l.FontSize;
        int extra = l.HeaderHeight - (box ? 27 : 57);
        if (!foot) top = l.HeadTop ?? (box ? d.HeadBottom - 31 - extra - l.Margin.Bottom : top + l.Margin.Top);
        else top += l.Margin.Top;
        var fill = l.Fill ?? d.Skin.For(key, "backgroundcolor", l.Stereotype, "#E2E2F0");
        var stroke = d.Skin.For(key, "bordercolor", l.Stereotype, "#181818");
        LaneGroup(s, l, index, foot ? "tail" : "head", d.IdPrefix);
        if (box)
        {
            if (l.Kind == "collections") Rect(s, x - w / 2 + 4, top, w - 4, 27+extra, fill, stroke, d.Skin.For(key, "borderthickness", l.Stereotype, "1"));
            Rect(s, x - w / 2, top + (l.Kind == "collections" ? 4 : 0), w, 27+extra, fill, stroke, d.Skin.For(key, "borderthickness", l.Stereotype, "1"));
            LaneText(top + 19 + extra);
        }
        else
        {
            int iconY = top + (foot ? 19 + extra : 2);
            s.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({x} {iconY})\" fill=\"{fill}\" stroke=\"{stroke}\">");
            s.Append(l.Kind switch
            {
                "actor" when d.Skin.ActorStyle != "stickman" => "<circle cx=\"0\" cy=\"6\" r=\"6\"/><path d=\"M-6 14Q-12 14-12 21V27H-6V38H-1V27H1V38H6V27H12V21Q12 14 6 14Z\"" + (d.Skin.ActorStyle == "hollow" ? " fill=\"none\"" : "") + "/>",
                "actor" => "<circle cx=\"0\" cy=\"6\" r=\"6\"/><path d=\"M0 13v11m-10-7h20M0 24l-9 11m9-11 9 11\" fill=\"none\"/>",
                "database" => "<path d=\"M-15 7c0-7 30-7 30 0v20c0 7-30 7-30 0z\"/><ellipse cx=\"0\" cy=\"7\" rx=\"15\" ry=\"5\"/>",
                "boundary" => "<circle cx=\"4\" cy=\"18\" r=\"11\"/><path d=\"M-8 18h-8m0-11v22\" fill=\"none\"/>",
                "control" => "<circle cx=\"0\" cy=\"19\" r=\"11\"/><path d=\"m-2 8 6-5m-6 5 7 2\" fill=\"none\"/>",
                "entity" => "<circle cx=\"0\" cy=\"16\" r=\"11\"/><path d=\"M-13 29h26\"/>",
                "queue" => "<path d=\"M-11 6h20c9 0 9 23 0 23h-20c-8 0-8-23 0-23z\"/><ellipse cx=\"9\" cy=\"17.5\" rx=\"6\" ry=\"11.5\"/>",
                _ => ""
            });
            s.Append("</g>");
            LaneText(top + (foot ? 14 : 52) + extra);
        }
        if (l.ShowStereotype)
            Text(s, x, top + (box ? l.StereotypeSize + l.Padding.Top : (foot ? size + 4 : 39)), ManagedSkin.StereotypeLabel(l.Stereotype), l.StereotypeSize, middle: true,
                color: d.Skin.For(key, "stereotypefontcolor", l.Stereotype, d.Style("defaultfontcolor", "#000")), part: "stereotype");
        s.Append("</g>");
        void LaneText(int bottomBaseline)
        {
            int rowTop = bottomBaseline - l.TextHeight + size - SvgText.Ascent("M", size);
            foreach (string row in l.Rows)
            {
                string anchor = SvgText.Alignment(row, "middle");
                int labelX = anchor == "start" ? x - w / 2 + l.Padding.Left : anchor == "end" ? x + w / 2 - l.Padding.Right : x;
                SvgText.Append(s, labelX, rowTop + SvgText.Ascent(row, size), row, size, anchor, color: d.Skin.For(key, "fontcolor", l.Stereotype, d.Style("defaultfontcolor", "#000")));
                rowTop += SvgText.Height(row, size) + 4;
            }
        }
    }

    private static void Message(StringBuilder s, Diagram d, Step step, int ordinal)
    {
        bool self = step.A == step.B;
        int from = d.Lanes[step.A].X + Math.Max(0, step.FromDepth - 1) * 5;
        int to = d.Lanes[step.B].X + Math.Max(0, step.ToDepth - 1) * 5;
        int dir = from <= to ? 1 : -1;
        if (step.FromDepth > 0) from += 5 * dir;
        if (step.ToDepth > 0) to += (self ? 5 : -5 * dir);
        if (step.Creates) to = d.Lanes[step.B].X - dir * d.Lanes[step.B].Width / 2;
        string stroke = step.Color ?? d.Style("arrowcolor", "#181818");
        string weight = d.Style("arrowthickness","1"), fontColor = d.Style("arrowfontcolor",d.Style("defaultfontcolor","#000"));
        int size = step.FontSize;
        bool dashed = step.Variant.Contains("--"), open = step.Variant.Contains(">>") || step.Variant.Contains("<<");
        int y = step.Anchor;
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"message\" data-style-name=\"arrow\"{(step.Color == null ? "" : " data-style-linecolor=\"explicit\"")} id=\"{d.IdPrefix}msg{ordinal}\" data-entity-1=\"{d.IdPrefix}part{step.A + 1}\" data-entity-2=\"{d.IdPrefix}part{step.B + 1}\" data-source-line=\"{step.Line}\">");
        if (self)
        {
            int loopWidth = Math.Max(35, step.TextWidth + 12);
            s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{from} {y - 15}h{loopWidth}v15H{to}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{weight}\"");
            if (dashed) s.Append(" stroke-dasharray=\"3 3\"");
            s.Append("/>");
            dir = -1;
        }
        else Line(s, from, y, to, y, stroke, dashed, weight);
        s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{to - dir * 9} {y - 4}L{to} {y}l{-dir * 9} 4");
        s.Append(open ? "\"" : "z\"");
        s.Append(CultureInfo.InvariantCulture, $" stroke=\"{stroke}\" fill=\"{(open ? "none" : stroke)}\"/>");
        string alignment = d.Skin.Style("arrow", "horizontalalignment", "left");
        if (alignment == "direction") alignment = dir > 0 ? "left" : "right";
        if (alignment == "reversedirection") alignment = dir > 0 ? "right" : "left";
        int labelX = self ? from + 7 : alignment == "right" ? Math.Max(from, to) - step.TextWidth - 7 :
            alignment == "center" ? (from + to - step.TextWidth) / 2 : Math.Min(from, to) + 7;
        labelX += step.Margin.Left + step.Padding.Left;
        int labelY = y - (self ? 20 : 6) - step.TextHeight + size - step.Padding.Bottom;
        if (step.Number > 0)
        {
            var n = step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Text(s, labelX, labelY, n, size, bold: true, color: fontColor);
            labelX += Measure(n, size) + 6;
        }
        int rowTop = labelY - SvgText.Ascent("M", size);
        foreach (string row in step.Rows)
        {
            string anchor = SvgText.Alignment(row, "start");
            int rowX = anchor == "end" ? labelX + step.TextWidth : anchor == "middle" ? labelX + step.TextWidth / 2 : labelX;
            SvgText.Append(s, rowX, rowTop + SvgText.Ascent(row, size), row, size, anchor, color: fontColor);
            rowTop += SvgText.Height(row, size) + 4;
        }
        s.Append("</g>");
    }

    private static void DrawNote(StringBuilder s, Diagram d, Step step)
    {
        int w = step.TextWidth + step.Padding.Horizontal, h = step.Bottom - step.Top - 9 - step.Margin.Bottom;
        int a = d.Lanes[step.A].X, b = d.Lanes[step.B].X, y = step.Top + 4;
        int x = step.Variant switch { "left of" => a - 10 - w, "right of" => a + 10, _ => (a + b - w) / 2 };
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"note\" data-style-name=\"note\"{(step.Color == null ? "" : " data-style-fill=\"explicit\"")} data-source-line=\"{step.Line}\"><path d=\"M{x} {y}h{w - 8}l8 8v{h - 8}h{-w}zM{x + w - 8} {y}v8h8\" fill=\"{step.Color ?? d.Style("notebackgroundcolor","#FEFFDD")}\" stroke=\"{d.Style("notebordercolor","#454545")}\"/>");
        int rowTop = y + step.Padding.Top + step.FontSize - SvgText.Ascent("M", step.FontSize);
        foreach (var row in step.Rows)
        {
            string alignment = d.Skin.Style("note", "horizontalalignment", "left");
            string anchor = SvgText.Alignment(row, alignment == "right" ? "end" : alignment == "center" ? "middle" : "start");
            int rowX = anchor == "end" ? x + w - step.Padding.Right : anchor == "middle" ? x + w / 2 : x + step.Padding.Left;
            SvgText.Append(s, rowX, rowTop + SvgText.Ascent(row, step.FontSize), row, step.FontSize, anchor, color:d.Style("notefontcolor",d.Style("defaultfontcolor","#000")));
            rowTop += SvgText.Height(row, step.FontSize) + 4;
        }
        s.Append("</g>");
    }

    private static void DrawReference(StringBuilder s, Diagram d, Step step)
    {
        int half = Math.Max(30, (Math.Max(step.HeaderWidth, step.TextWidth + step.Padding.Horizontal) + 1) / 2);
        int x = d.Lanes[step.A].X - half, right = d.Lanes[step.B].X + half;
        int y = step.Top + 3, height = step.Bottom - step.Top - 9 - step.Margin.Bottom;
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"reference\" data-style-name=\"reference\" data-source-line=\"{step.Line}\" data-entity-1=\"{d.IdPrefix}part{step.A + 1}\" data-entity-2=\"{d.IdPrefix}part{step.B + 1}\">");
        Rect(s, x, y, right - x, height, "#FFFFFF", "#424242");
        s.Append(CultureInfo.InvariantCulture, $"<g data-style-name=\"referenceheader\"{(step.Color == null ? "" : " data-style-fill=\"explicit\" fill=\"" + step.Color + "\"")}><path d=\"M{x} {y}h{step.HeaderWidth}v{step.HeaderSize + 1 + step.HeaderPadding.Vertical}l-7 7H{x}z\" fill=\"{step.Color ?? "#EEEEEE"}\" stroke=\"#424242\"/>");
        Text(s, x + 6 + step.HeaderPadding.Left, y + step.HeaderPadding.Top + SvgText.Ascent("M", step.HeaderSize) + 2, "ref", step.HeaderSize, bold: true, color: d.Style("defaultfontcolor", "#000"));
        s.Append("</g>");
        y += step.HeaderSize + 9 + step.HeaderPadding.Vertical + step.Padding.Top;
        foreach (string row in step.Rows)
        {
            string alignment = d.Skin.Style("reference", "horizontalalignment", "center");
            string anchor = SvgText.Alignment(row, alignment == "right" ? "end" : alignment == "left" ? "start" : "middle");
            int rowX = anchor == "end" ? right - step.Padding.Right : anchor == "start" ? x + step.Padding.Left : (x + right) / 2;
            SvgText.Append(s, rowX, y + step.FontSize + 4 - SvgText.Ascent("M", step.FontSize) + SvgText.Ascent(row, step.FontSize), row, step.FontSize, anchor, color: d.Style("defaultfontcolor", "#000"));
            y += SvgText.Height(row, step.FontSize) + 4;
        }
        s.Append("</g>");
    }

    private static void Rect(StringBuilder s, int x, int y, int w, int h, string fill, string stroke, string weight = "1") =>
        s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{weight}\"/>");

    private static void Line(StringBuilder s, int x1, int y1, int x2, int y2, string stroke, bool dashed = false, string weight = "1")
    {
        s.Append(CultureInfo.InvariantCulture, $"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"{stroke}\" stroke-width=\"{weight}\"");
        if (dashed) s.Append(" stroke-dasharray=\"3 3\"");
        s.Append("/>");
    }

    private static void Text(StringBuilder s, int x, int y, string text, int size = 14, bool middle = false, bool bold = false, string color = "#000", string part = "") =>
        SvgText.Append(s, x, y, text, size, middle ? "middle" : "start", bold, color: color, part: part);
}
