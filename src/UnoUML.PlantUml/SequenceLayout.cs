using System.Globalization;

namespace UnoUML.PlantUml;

internal static partial class SequenceRenderer
{
    private static int HeadingHeight(string? title) =>
        string.IsNullOrEmpty(title) ? 12 : 43 + 22 * title.AsSpan().Count('\n');

    private static int TitleWidth(string? title) =>
        string.IsNullOrEmpty(title) ? 0 : (title.Contains('\n')
            ? title.Split('\n').Max(row => SvgText.Measure(row, 18)) : SvgText.Measure(title, 18)) + 28;

    private static int LaneHeight(Diagram d, Lane lane) =>
        (lane.Kind is "participant" or "collections" ? 27 : 57) + Math.Max(0, d.Skin.Size(lane.Kind == "actor" ? "actorfontsize" : "participantfontsize", 14) - 14);

    private static bool Arrange(Diagram d)
    {
        int frameMargin = 16 + d.MaxNest * 7;
        int messageFont = d.Skin.Size("arrowfontsize",13);
        int participantFont = d.Skin.Size("participantfontsize",14), actorFont = d.Skin.Size("actorfontsize",14);
        foreach (var lane in d.Lanes)
        {
            int size = lane.Kind == "actor" ? actorFont : participantFont;
            if (size != 14) lane.Width = Math.Max(38, SvgText.Measure(lane.Name,size) + 20);
        }
        d.HeaderHeight = d.Lanes.Max(l => l.Kind is "participant" or "collections" ? 34 + Math.Max(0, participantFont-14)
            : 57 + Math.Max(0,(l.Kind == "actor" ? actorFont : participantFont)-14));
        d.HeadBottom = HeadingHeight(d.Title) + d.HeaderHeight;
        int y = d.HeadBottom + 10, anchor = y, minimumWidth = TitleWidth(d.Title);
        var activations = new Stack<(int Y, int Line, string Fill)>[d.Lanes.Count];
        for (int i = 0; i < d.Lanes.Count; i++)
        {
            var lane = d.Lanes[i];
            lane.LeftSpace = lane.RightSpace = (lane.Width + 1) / 2;
            activations[i] = new();
        }

        foreach (var step in d.Steps)
        {
            step.Top = y;
            step.TextWidth = step.Op is Op.Note or Op.Reference ? step.Text.Split('\n').Max(s => SvgText.Measure(s, 13)) : SvgText.Measure(step.Text, step.Op == Op.Send ? messageFont : 13);
            switch (step.Op)
            {
                case Op.Send:
                    if (step.Number > 0) step.TextWidth += SvgText.Measure(step.Number.ToString(CultureInfo.InvariantCulture), messageFont) + 6;
                    step.FromDepth = activations[step.A].Count;
                    step.ToDepth = activations[step.B].Count;
                    int clearance = Math.Max(step.FromDepth, step.ToDepth) * 5 + 24;
                    if (step.A == step.B)
                    {
                        d.Lanes[step.A].RightSpace = Math.Max(d.Lanes[step.A].RightSpace, Math.Max(44, step.TextWidth + clearance));
                        y += 48 + Math.Max(0,messageFont-13);
                    }
                    else
                    {
                        int left = Math.Min(step.A, step.B), right = Math.Max(step.A, step.B);
                        d.Lanes[right].Incoming.Add((left, step.TextWidth + clearance + (step.Creates ? d.Lanes[step.B].Width / 2 : 0)));
                        y += 31 + Math.Max(0,messageFont-13);
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
                    int referenceWidth = Math.Max(60, step.TextWidth + 24);
                    d.Lanes[step.A].LeftSpace = Math.Max(d.Lanes[step.A].LeftSpace, referenceWidth / 2);
                    d.Lanes[step.B].RightSpace = Math.Max(d.Lanes[step.B].RightSpace, (referenceWidth + 1) / 2);
                    y += 36 + 17 * (step.Text.AsSpan().Count('\n') + 1);
                    break;
                case Op.Note:
                    var lane = d.Lanes[step.A];
                    int width = step.TextWidth + 24;
                    if (step.Variant == "left of") lane.LeftSpace = Math.Max(lane.LeftSpace, width + 12);
                    else if (step.Variant == "right of") lane.RightSpace = Math.Max(lane.RightSpace, width + 12);
                    else
                    {
                        var leftLane = d.Lanes[Math.Min(step.A, step.B)];
                        var rightLane = d.Lanes[Math.Max(step.A, step.B)];
                        leftLane.LeftSpace = Math.Max(leftLane.LeftSpace, (width + 1) / 2);
                        rightLane.RightSpace = Math.Max(rightLane.RightSpace, (width + 1) / 2);
                    }
                    y += 22 + 17 * (step.Text.AsSpan().Count('\n') + 1); break;
                case Op.Open:
                case Op.Branch:
                    minimumWidth = Math.Max(minimumWidth, step.TextWidth + SvgText.Measure(step.Variant) + 2 * frameMargin + 60);
                    y += 31; break;
                case Op.Close: y += 12; anchor = y; break;
                case Op.Divider:
                case Op.Delay:
                    minimumWidth = Math.Max(minimumWidth, step.TextWidth + 50);
                    y += 32;
                    if (step.Op == Op.Divider) anchor = y - 16;
                    break;
                case Op.Space: y += 24; break;
                case Op.Page:
                    anchor = y;
                    minimumWidth = Math.Max(minimumWidth, TitleWidth(step.Text));
                    break;
            }
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
        if (d.Legend != null) d.Height += 22 + 17 * d.Legend.Length;
        foreach (var step in d.Steps)
            if (step.Op == Op.Open) d.Frames.Add(new(step.Nest, step.Top, d.Steps[step.End].Bottom, true, true));
        return d.Width <= MaximumExtent && (d.Breaks.Count != 0 || d.Height <= MaximumExtent);
    }
}
