using System.Globalization;

namespace UnoUML.PlantUml;

internal static partial class SequenceRenderer
{
    private static bool Arrange(Diagram d)
    {
        int frameMargin = 16 + d.MaxNest * 7;
        d.HeaderHeight = d.Lanes.Any(l => l.Kind is not ("participant" or "collections")) ? 57 : 34;
        d.HeadBottom = (d.Title == null ? 12 : 43) + d.HeaderHeight;
        int y = d.HeadBottom + 10, anchor = y, minimumWidth = d.Title == null ? 0 : SvgText.Measure(d.Title, 18) + 28;
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
            step.TextWidth = step.Op == Op.Note ? step.Text.Split('\n').Max(s => SvgText.Measure(s, 13)) : SvgText.Measure(step.Text, 13);
            switch (step.Op)
            {
                case Op.Send:
                    if (step.Number > 0) step.TextWidth += SvgText.Measure(step.Number.ToString(CultureInfo.InvariantCulture), 13) + 6;
                    step.FromDepth = activations[step.A].Count;
                    step.ToDepth = activations[step.B].Count;
                    int clearance = Math.Max(step.FromDepth, step.ToDepth) * 5 + 24;
                    if (step.A == step.B)
                    {
                        d.Lanes[step.A].RightSpace = Math.Max(d.Lanes[step.A].RightSpace, Math.Max(44, step.TextWidth + clearance));
                        y += 48;
                    }
                    else
                    {
                        int left = Math.Min(step.A, step.B), right = Math.Max(step.A, step.B);
                        d.Lanes[right].Incoming.Add((left, step.TextWidth + clearance));
                        y += 31;
                    }
                    anchor = y - 5;
                    break;
                case Op.Start:
                    if (activations[step.A].Count >= 64) return false;
                    activations[step.A].Push((anchor, step.Line, step.Color ?? "#FFFFFF"));
                    d.Lanes[step.A].RightSpace = Math.Max(d.Lanes[step.A].RightSpace, activations[step.A].Count * 5 + 5);
                    break;
                case Op.Finish:
                    if (!activations[step.A].TryPop(out var started)) return false;
                    d.Bars.Add(new(step.A, activations[step.A].Count, started.Y, anchor, started.Line, started.Fill));
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
                case Op.Close: y += 12; break;
                case Op.Divider:
                case Op.Delay:
                    minimumWidth = Math.Max(minimumWidth, step.TextWidth + 50);
                    y += 32; break;
                case Op.Space: y += 24; break;
            }
            step.Bottom = y;
            if (y > MaximumExtent) return false;
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
        d.Width = Math.Max(lanesWidth, minimumWidth);
        if (d.Width > lanesWidth)
            foreach (var lane in d.Lanes) lane.X += (d.Width - lanesWidth) / 2;
        d.LifelineEnd = y + 9;
        for (int i = 0; i < activations.Length; i++)
            while (activations[i].TryPop(out var bar))
                d.Bars.Add(new(i, activations[i].Count, bar.Y, d.LifelineEnd, bar.Line, bar.Fill));
        d.Height = d.LifelineEnd + (d.Footbox ? d.HeaderHeight : 0) + 10;
        return d.Width <= MaximumExtent && d.Height <= MaximumExtent;
    }
}
