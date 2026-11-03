using System.Globalization;

namespace UnoUML.PlantUml;

internal static partial class TimingRenderer
{
    private static bool MeasureTrack(Track track)
    {
        track.Changes.Sort((left, right) => left.Time.CompareTo(right.Time));
        for (int i = 1; i < track.Changes.Count; i++)
            if (track.Changes[i - 1].Time == track.Changes[i].Time) return false;
        track.Caption = StyleLabel.Measure(track.Label, "timingdiagram", 14);
        track.HeaderHeight = track.Caption.Height + 4;
        IEnumerable<Change> changes = track.Initial == null ? track.Changes : track.Changes.Prepend(track.Initial);
        foreach (var change in changes)
        {
            change.StateLabel = StyleLabel.Measure(change.State, track.Kind, 12, stereotype: track.Stereotype);
            change.AnnotationLabel = StyleLabel.Measure(change.Annotation, track.Kind, 12, stereotype: track.Stereotype);
            if (change.Annotation.Length > 0) track.HeaderHeight = Math.Max(track.HeaderHeight, change.AnnotationLabel.Height + 4);
        }
        if (track.Kind == "robust")
        {
            // Explicit state order is top to bottom. Discovered states retain the
            // established bottom-to-top order of the native waveform painter.
            foreach (var definition in track.Definitions.Values.Reverse())
            {
                if (definition.Label == "{hidden}") continue;
                track.StateLabels.TryAdd(definition.Label, StyleLabel.Measure(definition.Label, track.Kind, 12, stereotype: track.Stereotype));
                track.StateSources[definition.Label] = definition;
            }
            foreach (var state in track.StateSources.Values)
                track.StateLabels.TryAdd(state.Label, StyleLabel.Measure(state.Label, track.Kind, 12, stereotype: track.Stereotype));
        }
        double stateOffset = 0;
        foreach (var (state, label) in track.StateLabels)
        {
            int band = Math.Max(18, label.Height + 6);
            track.StateOffsets[state] = stateOffset + band / 2d;
            stateOffset += band;
        }
        if (track.Kind == "analog")
        {
            if (!track.HasBounds)
            {
                track.Low = Math.Min(0, changes.Select(change => change.Value).DefaultIfEmpty(0).Min());
                track.High = Math.Max(0, changes.Select(change => change.Value).DefaultIfEmpty(0).Max());
                if (track.High == 0) track.High = 10;
            }
            else if (changes.Any(change => change.Value < track.Low || change.Value > track.High)) return false;
        }
        track.LowLabel = NumberLabel(track, track.Low);
        track.HighLabel = NumberLabel(track, track.High);
        if (track.Kind == "analog" && track.TickStep > 0)
        {
            double first = Math.Ceiling(track.Low / track.TickStep) * track.TickStep;
            double count = Math.Floor((track.High - first) / track.TickStep);
            if (count > 256) return false;
            for (int i = 0; i <= count; i++)
            {
                double value = first + i * (double)track.TickStep;
                track.ValueTicks.Add((value, NumberLabel(track, value)));
            }
        }
        track.RibbonHeight = Math.Max(22, changes.Select(change => change.StateLabel.Height + 8).DefaultIfEmpty(22).Max());
        track.SignalHeight = track.Kind switch
        {
            "robust" => Math.Max(36, (int)Math.Ceiling(stateOffset)),
            "concise" or "rectangle" => track.RibbonHeight,
            "analog" => Math.Max(45, Math.Max(track.LowLabel.Height, track.HighLabel.Height) * 2 + 10),
            _ => 20
        };
        if (track.RequestedHeight > track.SignalHeight)
        {
            double factor = (double)track.RequestedHeight / track.SignalHeight;
            foreach (var state in track.StateOffsets.Keys.ToArray()) track.StateOffsets[state] *= factor;
            track.SignalHeight = track.RequestedHeight;
            if (track.Ribbon) track.RibbonHeight = track.RequestedHeight;
        }
        if (track.Initial != null)
            track.InitialWidth = track.Kind == "robust" ? 40 : track.Ribbon ? track.Initial.StateLabel.Width + 20 : 0;
        foreach (var constraint in track.Constraints) constraint.Caption = StyleLabel.Measure(constraint.Label, "constraintarrow", 12);
        foreach (var note in track.Notes)
            note.Labels = note.Rows.Select(row => StyleLabel.Measure(row.Label, "note", 12, stereotype: note.Stereotype)).ToArray();
        return true;
    }

    private static StyleLabel NumberLabel(Track track, double value) =>
        StyleLabel.Measure(value.ToString(track.NumberFormat, CultureInfo.InvariantCulture), track.Kind, 12, stereotype: track.Stereotype);

    private static int PlaceTrack(Track track, int top)
    {
        track.Top = top;
        int next = top + track.HeaderHeight + (track.Compact ? 3 : 8);
        foreach (var constraint in track.Constraints)
        {
            constraint.Top = next;
            next += constraint.Caption.Height + 18;
        }
        foreach (var note in track.Notes.Where(note => note.Above))
        {
            note.Top = next;
            next += note.Height + 6;
        }
        int signalPadding = track.Kind == "analog" ? (Math.Max(track.LowLabel.Height, track.HighLabel.Height) + 1) / 2 : 0;
        track.SignalTop = next + signalPadding;
        next = track.SignalTop + track.SignalHeight + signalPadding + (track.Compact ? 6 : 12);
        foreach (var note in track.Notes.Where(note => !note.Above))
        {
            note.Top = next;
            next += note.Height + 6;
        }
        foreach (var message in track.Messages)
        {
            message.CaptionTop = next;
            next += message.Caption.Height + 12;
        }
        track.Height = next - top;
        return next;
    }

    private static int GutterWidth(Track track)
    {
        int stateWidth = track.StateLabels.Values.Select(label => label.Width).DefaultIfEmpty(0).Max();
        if (track.Kind == "analog")
            stateWidth = Math.Max(stateWidth, Math.Max(track.LowLabel.Width, Math.Max(track.HighLabel.Width,
                track.ValueTicks.Select(tick => tick.Label.Width).DefaultIfEmpty(0).Max())));
        return Math.Max(track.Caption.Width + 24, stateWidth + track.InitialWidth + 28);
    }

    private static bool FitStateLabels(Track track, decimal last, bool fixedScale, ref double scale, ref int tailWidth)
    {
        for (int i = 0; i < track.Changes.Count; i++)
        {
            var change = track.Changes[i];
            decimal end = i + 1 < track.Changes.Count ? track.Changes[i + 1].Time : last;
            int required = track.Ribbon && change.State is not ("{hidden}" or "{-}" or "{...}") ? change.StateLabel.Width + 16 : 0;
            if (change.Annotation.Length > 0) required = Math.Max(required, change.AnnotationLabel.Width + 8);
            if (end > change.Time && required > 0)
            {
                double needed = required / (double)(end - change.Time);
                if (fixedScale && i + 1 < track.Changes.Count && needed > scale) return false;
                if (!fixedScale) scale = Math.Max(scale, needed);
            }
            if (i + 1 == track.Changes.Count) tailWidth = Math.Max(tailWidth, required);
        }
        return true;
    }

    private static Dictionary<decimal, int> PlaceAxisLabels(Dictionary<decimal, StyleLabel> labels,
        Func<decimal, double> xAt, out int height)
    {
        var active = new PriorityQueue<int, double>();
        var rows = new Dictionary<decimal, int>();
        var heights = new List<int>();
        // Interval partitioning retains every manual label while using the minimum
        // number of non-overlapping rows. Work is O(n log n), including sorting.
        foreach (var (time, label) in labels.OrderBy(pair => xAt(pair.Key) - pair.Value.Width / 2d))
        {
            double left = xAt(time) - label.Width / 2d, right = left + label.Width + 4;
            int row;
            if (active.TryPeek(out int available, out double previousRight) && previousRight < left)
            {
                row = available;
                active.Dequeue();
                heights[row] = Math.Max(heights[row], label.Height + 4);
            }
            else
            {
                row = heights.Count;
                heights.Add(label.Height + 4);
            }
            rows.Add(time, row);
            active.Enqueue(row, right);
        }
        var offsets = new int[heights.Count];
        height = 0;
        for (int row = 0; row < heights.Count; row++)
        {
            offsets[row] = height;
            height += heights[row];
        }
        foreach (decimal time in rows.Keys.ToArray()) rows[time] = offsets[rows[time]];
        return rows;
    }
}
