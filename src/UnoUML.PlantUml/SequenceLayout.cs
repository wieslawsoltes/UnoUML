using System.Globalization;

namespace UnoUML.PlantUml;

internal static partial class SequenceRenderer
{
    private static int HeadingHeight(StyleTitle? title) => title == null ? 12 : 25 + title.Height;

    private static int TitleWidth(StyleTitle? title) => title == null ? 0 : title.Width + 28;

    private static int LaneHeight(Diagram d, Lane lane) => lane.HeaderHeight;

    private static string StepCategory(Step step) => step.Op switch
    {
        Op.Send => "arrow", Op.Note => "note", Op.Reference => "reference",
        Op.Open or Op.Branch or Op.Close => "group", Op.Divider => "separator", Op.Delay => "delay", _ => "element"
    };

    private static bool Arrange(Diagram d)
    {
        int frameMargin = 16 + d.MaxNest * 7;
        int messageFont = d.Skin.Size("arrowfontsize",13);
        foreach (var lane in d.Lanes)
        {
            lane.FontSize = d.Skin.Number(lane.Kind, "fontsize", 14, lane.Stereotype);
            lane.StereotypeSize = d.Skin.Number(lane.Kind, "fontsize", 12, lane.Stereotype, "stereotype");
            lane.ShowStereotype = lane.Stereotype.Length != 0 && d.Skin.Style(lane.Kind, "showstereotype", "true", lane.Stereotype) != "false";
            lane.Padding = StyleSpacing.Parse(d.Skin.Style(lane.Kind, "padding", "6 10", lane.Stereotype));
            lane.Margin = StyleSpacing.Parse(d.Skin.Style(lane.Kind, "margin", "0", lane.Stereotype));
            int maximum = d.Skin.Number(lane.Kind, "maximumwidth", 0, lane.Stereotype);
            lane.Rows = StyleText.Wrap(lane.Name, lane.FontSize, maximum == 0 ? 0 : Math.Max(lane.FontSize, maximum - lane.Padding.Horizontal));
            lane.TextHeight = StyleText.Height(lane.Rows, lane.FontSize);
            int content = lane.Rows.Max(row => StyleText.Measure(row, lane.FontSize, d.Skin.Style(lane.Kind, "fontstyle", "normal", lane.Stereotype)));
            if (lane.ShowStereotype) content = Math.Max(content, SvgText.Measure(ManagedSkin.StereotypeLabel(lane.Stereotype), lane.StereotypeSize));
            lane.Width = Math.Max(d.Skin.Number(lane.Kind, "minimumwidth", 38, lane.Stereotype), content + lane.Padding.Horizontal);
            lane.HeaderHeight = (lane.Kind is "participant" or "collections" ? 27 : 57) + Math.Max(0, lane.TextHeight - 14) +
                Math.Max(0, lane.Padding.Vertical - 12) + (lane.ShowStereotype ? lane.StereotypeSize + 4 : 0);
        }
        d.HeaderHeight = d.Lanes.Max(l => l.HeaderHeight + l.Margin.Vertical + (l.Kind is "participant" or "collections" ? 7 : 0));
        d.Heading = StyleTitle.Measure(d.Title, d.Skin, 18);
        d.HeadBottom = HeadingHeight(d.Heading) + d.HeaderHeight;
        int y = d.HeadBottom + 10, anchor = y, minimumWidth = TitleWidth(d.Heading);
        if (d.Legend != null) d.LegendWidth = Math.Max(50, d.Legend.Max(row => SvgText.Measure(row, d.Skin.Size("legendfontsize", 13))) + 20);
        var activations = new Stack<(int Y, int Line, string Fill)>[d.Lanes.Count];
        for (int i = 0; i < d.Lanes.Count; i++)
        {
            var lane = d.Lanes[i];
            lane.LeftSpace = (lane.Width + 1) / 2 + lane.Margin.Left;
            lane.RightSpace = (lane.Width + 1) / 2 + lane.Margin.Right;
            activations[i] = new();
        }

        foreach (var step in d.Steps)
        {
            string category = StepCategory(step);
            step.Margin = StyleSpacing.Parse(d.Skin.Style(category, "margin", "0"));
            y += step.Margin.Top;
            step.Top = y;
            step.Padding = StyleSpacing.Parse(d.Skin.Style(category, "padding", step.Op == Op.Note ? "4 16 9 6" : step.Op == Op.Reference ? "0 12 6 12" : "0"));
            step.FontSize = d.Skin.Number(category, "fontsize", 13);
            int maximum = d.Skin.Number(category, "maximumwidth", 0);
            step.Rows = StyleText.Wrap(step.Text, step.FontSize, maximum == 0 ? 0 : Math.Max(step.FontSize, maximum - step.Padding.Horizontal));
            step.TextHeight = StyleText.Height(step.Rows, step.FontSize);
            string headerCategory = step.Op == Op.Open ? "groupheader" : "referenceheader";
            step.HeaderSize = d.Skin.Number(headerCategory, "fontsize", 13);
            var headerPadding = StyleSpacing.Parse(d.Skin.Style(headerCategory, "padding", "0"));
            var headerInk = StyleText.InkPadding(step.HeaderSize);
            step.HeaderPadding = new(headerPadding.Top + headerInk.Top, headerPadding.Right, headerPadding.Bottom + headerInk.Bottom, headerPadding.Left);
            step.HeaderWidth = StyleText.Measure(step.Op == Op.Open ? step.Variant : "ref", step.HeaderSize, d.Skin.Style(headerCategory, "fontstyle", "bold")) + 19 + step.HeaderPadding.Horizontal;
            step.TextWidth = step.Rows.Max(row => StyleText.Measure(row, step.FontSize, d.Skin.Style(category, "fontstyle", step.Op is Op.Open or Op.Branch ? "bold" : "normal")));
            switch (step.Op)
            {
                case Op.Send:
                    if (step.Number > 0) step.TextWidth += SvgText.Measure(step.Number.ToString(CultureInfo.InvariantCulture), messageFont) + 6;
                    step.FromDepth = activations[step.A].Count;
                    step.ToDepth = activations[step.B].Count;
                    int clearance = Math.Max(step.FromDepth, step.ToDepth) * 5 + 24 + step.Margin.Horizontal + step.Padding.Horizontal;
                    if (step.A == step.B)
                    {
                        d.Lanes[step.A].RightSpace = Math.Max(d.Lanes[step.A].RightSpace, Math.Max(44, step.TextWidth + clearance));
                        y += 35 + Math.Max(13, step.TextHeight) + step.Padding.Vertical;
                    }
                    else
                    {
                        int left = Math.Min(step.A, step.B), right = Math.Max(step.A, step.B);
                        d.Lanes[right].Incoming.Add((left, step.TextWidth + clearance + (step.Creates ? d.Lanes[step.B].Width / 2 : 0)));
                        y += 18 + Math.Max(13, step.TextHeight) + step.Padding.Vertical;
                    }
                    anchor = y - 5;
                    if (step.Creates)
                    {
                        var created = d.Lanes[step.B];
                        int height = LaneHeight(d, created);
                        anchor = Math.Max(anchor, step.Top + height / 2 + 8);
                        created.HeadTop = anchor - height / 2;
                        created.LifeStart = created.HeadTop + height;
                        y = anchor + height / 2 + 8;
                    }
                    step.Anchor = anchor;
                    break;
                case Op.Start:
                    if (activations[step.A].Count >= 64) return false;
                    activations[step.A].Push((anchor, step.Line, step.Color ?? d.Style("lifelinebackgroundcolor","#FFFFFF")));
                    d.Lanes[step.A].RightSpace = Math.Max(d.Lanes[step.A].RightSpace, activations[step.A].Count * 5 + 5);
                    break;
                case Op.Finish:
                    if (!activations[step.A].TryPop(out var started)) return false;
                    d.Bars.Add(new(step.A, activations[step.A].Count, started.Y, anchor, started.Line, started.Fill));
                    break;
                case Op.Destroy:
                    step.Anchor = step.Standalone ? y + 10 : anchor;
                    d.Lanes[step.A].LifeEnd = step.Anchor;
                    while (activations[step.A].TryPop(out var active))
                        d.Bars.Add(new(step.A, activations[step.A].Count, active.Y, step.Anchor, active.Line, active.Fill));
                    y = Math.Max(y, step.Anchor + 14);
                    break;
                case Op.Reference:
                    int referenceWidth = Math.Max(60, Math.Max(step.HeaderWidth, step.TextWidth + step.Padding.Horizontal) + step.Margin.Horizontal);
                    d.Lanes[step.A].LeftSpace = Math.Max(d.Lanes[step.A].LeftSpace, referenceWidth / 2);
                    d.Lanes[step.B].RightSpace = Math.Max(d.Lanes[step.B].RightSpace, (referenceWidth + 1) / 2);
                    y += 21 + step.HeaderSize + step.TextHeight + step.Padding.Vertical + step.HeaderPadding.Vertical;
                    break;
                case Op.Note:
                    var lane = d.Lanes[step.A];
                    int width = step.TextWidth + step.Padding.Horizontal + step.Margin.Horizontal + 2;
                    if (step.Variant == "left of") lane.LeftSpace = Math.Max(lane.LeftSpace, width + 12);
                    else if (step.Variant == "right of") lane.RightSpace = Math.Max(lane.RightSpace, width + 12);
                    else
                    {
                        var leftLane = d.Lanes[Math.Min(step.A, step.B)];
                        var rightLane = d.Lanes[Math.Max(step.A, step.B)];
                        leftLane.LeftSpace = Math.Max(leftLane.LeftSpace, (width + 1) / 2);
                        rightLane.RightSpace = Math.Max(rightLane.RightSpace, (width + 1) / 2);
                    }
                    y += 13 + step.TextHeight + step.Padding.Vertical; break;
                case Op.Open:
                    minimumWidth = Math.Max(minimumWidth, step.TextWidth + step.HeaderWidth + 2 * frameMargin + 20 + step.Padding.Horizontal + step.Margin.Horizontal);
                    y += Math.Max(18 + Math.Max(13, step.TextHeight) + step.Padding.Vertical, step.HeaderSize + 18 + step.HeaderPadding.Vertical);
                    break;
                case Op.Branch:
                    minimumWidth = Math.Max(minimumWidth, step.TextWidth + SvgText.Measure(step.Variant, step.FontSize) + 2 * frameMargin + 60 + step.Padding.Horizontal + step.Margin.Horizontal);
                    y += 18 + Math.Max(13, step.TextHeight) + step.Padding.Vertical; break;
                case Op.Close: y += 12; anchor = y; break;
                case Op.Divider:
                case Op.Delay:
                    minimumWidth = Math.Max(minimumWidth, step.TextWidth + 50);
                    y += 19 + Math.Max(13, step.TextHeight) + step.Padding.Vertical;
                    if (step.Op == Op.Divider) anchor = y - 16;
                    break;
                case Op.Space: y += 24; break;
                case Op.Page:
                    anchor = y;
                    minimumWidth = Math.Max(minimumWidth, TitleWidth(StyleTitle.Measure(step.Text, d.Skin, 18)));
                    break;
            }
            y += step.Margin.Bottom;
            step.Bottom = y;
            if (d.Breaks.Count == 0 && y > MaximumExtent) return false;
        }

        // All horizontal constraints point from lower to higher participant order.
        // One longest-path sweep gives the minimum feasible lane centers in O(lanes + messages).
        for (int i = 0; i < d.Lanes.Count; i++)
        {
            var lane = d.Lanes[i];
            lane.X = i == 0 ? frameMargin + lane.LeftSpace : d.Lanes[i - 1].X + d.Lanes[i - 1].RightSpace + 20 + lane.LeftSpace;
            foreach (var edge in lane.Incoming) lane.X = Math.Max(lane.X, d.Lanes[edge.Left].X + edge.Width);
            if (lane.X > MaximumExtent) return false;
        }
        int lanesWidth = d.Lanes[^1].X + d.Lanes[^1].RightSpace + frameMargin;
        d.Width = Math.Max(Math.Max(lanesWidth, minimumWidth), d.LegendWidth + 16);
        if (d.Width > lanesWidth)
            foreach (var lane in d.Lanes) lane.X += (d.Width - lanesWidth) / 2;
        d.LifelineEnd = y + 9;
        for (int i = 0; i < activations.Length; i++)
            while (activations[i].TryPop(out var bar))
                d.Bars.Add(new(i, activations[i].Count, bar.Y, d.LifelineEnd, bar.Line, bar.Fill));
        d.Height = d.LifelineEnd + (d.Footbox ? d.HeaderHeight : 0) + 10;
        if (d.Legend != null) d.Height += 26 + StyleText.Height(d.Legend, d.Skin.Size("legendfontsize", 13));
        foreach (var step in d.Steps)
            if (step.Op == Op.Open) d.Frames.Add(new(step.Nest, step.Top, d.Steps[step.End].Bottom, true, true));
        return d.Width <= MaximumExtent && (d.Breaks.Count != 0 || d.Height <= MaximumExtent);
    }
}
