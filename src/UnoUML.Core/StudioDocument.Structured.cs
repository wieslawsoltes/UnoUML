using System.Globalization;
using UnoUML.Syntax;

namespace UnoUML.Core;

/// <summary>An immutable packet page snapshot. Reopen it after any source mutation.</summary>
public sealed class PacketDiagramEdit
{
    internal PacketDiagramEdit(string source, int page, int offset, PacketSource syntax)
    {
        Source = source; Page = page; Offset = offset; Syntax = syntax;
        Fields = Array.AsReadOnly(syntax.Fields.Select((_, i) => new PacketFieldEdit(this, i)).ToArray());
    }
    internal string Source { get; }
    internal int Page { get; }
    internal int Offset { get; }
    internal PacketSource Syntax { get; }
    public int Columns => Syntax.Columns;
    public IReadOnlyList<PacketFieldEdit> Fields { get; }
}

public sealed class PacketFieldEdit
{
    internal PacketFieldEdit(PacketDiagramEdit diagram, int index) { Diagram = diagram; Index = index; }
    public PacketDiagramEdit Diagram { get; }
    public int Index { get; }
    internal PacketSourceField Syntax => Diagram.Syntax.Fields[Index];
    public int FirstBit => Syntax.First;
    public int LastBit => Syntax.Last;
    public int BitCount => Syntax.Width;
    public string Label => Syntax.Label;
    public int Offset => Diagram.Offset + Syntax.LabelToken.Start;
}

/// <summary>A chart point in source coordinates. Indexed series use only Y.</summary>
public readonly record struct ChartPoint(double X, double Y, bool IsMissing = false);

/// <summary>An immutable chart page snapshot shared by all of its structural edit handles.</summary>
public sealed class ChartDiagramEdit
{
    internal ChartDiagramEdit(string source, int page, int offset, ChartSource syntax)
    {
        Source = source; Page = page; Offset = offset; Syntax = syntax;
        Series = Array.AsReadOnly(syntax.Series.Select((_, i) => new ChartSeriesEdit(this, i)).ToArray());
        Categories = Array.AsReadOnly(syntax.CategoryAxis.Categories.ToArray());
        HorizontalAxis = new(this, ChartAxisKind.Horizontal, syntax.Horizontal);
        VerticalAxis = new(this, ChartAxisKind.Vertical, syntax.Vertical);
        SecondaryAxis = syntax.Secondary == null ? null : new(this, ChartAxisKind.Secondary, syntax.Secondary);
        Annotations = Array.AsReadOnly(syntax.Annotations.Select((_, i) => new ChartAnnotationEdit(this, i)).ToArray());
    }
    internal string Source { get; }
    internal int Page { get; }
    internal int Offset { get; }
    internal ChartSource Syntax { get; }
    public IReadOnlyList<ChartSeriesEdit> Series { get; }
    public IReadOnlyList<string> Categories { get; }
    public ChartAxisEdit HorizontalAxis { get; }
    public ChartAxisEdit VerticalAxis { get; }
    public ChartAxisEdit? SecondaryAxis { get; }
    public IReadOnlyList<ChartAnnotationEdit> Annotations { get; }
    public ChartSettings Settings => new(Syntax.Title, Enum.TryParse<ChartLegend>(Syntax.Legend, true, out var legend) ? legend : ChartLegend.None,
        Syntax.Settings.ContainsKey("orientation") ? IsHorizontal ? ChartOrientation.Horizontal : ChartOrientation.Vertical : null,
        Syntax.Settings.ContainsKey("stackmode") ? IsStacked ? ChartStackMode.Stacked : ChartStackMode.Grouped : null);
    public bool HasCategoryLabels => Syntax.ExplicitCategories;
    public bool UsesCoordinates => Syntax.Coordinates;
    public bool IsHorizontal => Syntax.HorizontalOrientation;
    public bool IsStacked => Syntax.Stacked;
    public bool HasSecondaryAxis => Syntax.Secondary != null;
    public double MinimumValue => Syntax.ValueAxis.Min;
    public double MaximumValue => Syntax.ValueAxis.Max;
    public double MinimumX => Syntax.Horizontal.Min;
    public double MaximumX => Syntax.Horizontal.Max;
}

public sealed class ChartSeriesEdit
{
    internal ChartSeriesEdit(ChartDiagramEdit diagram, int index)
    {
        Diagram = diagram; Index = index;
        Points = Array.AsReadOnly(Syntax.Points.Select((_, i) => new ChartPointEdit(this, i)).ToArray());
    }
    public ChartDiagramEdit Diagram { get; }
    public int Index { get; }
    internal ChartSourceSeries Syntax => Diagram.Syntax.Series[Index];
    public string Kind => Syntax.Kind;
    public string Label => Syntax.Label;
    public bool UsesCoordinates => Syntax.Coordinates;
    public bool UsesSecondaryAxis => Syntax.Secondary;
    public bool Interpolate => Syntax.Interpolate;
    public bool ConnectMissingValues => Syntax.Connect;
    public IReadOnlyList<ChartPointEdit> Points { get; }
}

public sealed class ChartPointEdit
{
    internal ChartPointEdit(ChartSeriesEdit series, int index) { Series = series; Index = index; }
    public ChartSeriesEdit Series { get; }
    public int Index { get; }
    internal ChartSourcePoint Syntax => Series.Syntax.Points[Index];
    public double X => Syntax.X;
    public double Y => Syntax.Y;
    public bool IsMissing => Syntax.IsMissing;
    public int Offset => Series.Diagram.Offset + Syntax.YToken.Start;
    public int XOffset => Syntax.XToken.Length == 0 ? -1 : Series.Diagram.Offset + Syntax.XToken.Start;
}

public sealed partial class StudioDocument
{
    private bool ApplyStructuredSourceField(SourceFieldEdit field, string text)
    {
        if (OpenPacketField(field) is { } packet)
        {
            if (!StructuredText.PacketLabel(text)) return false;
            var token = packet.Syntax.LabelToken;
            return CommitPacket(packet.Diagram, [new(token.Start, token.End, text)]);
        }
        if (OpenChartDiagram() is not { } chart) return false;
        int index = ChartCategoryIndex(chart, field);
        if (index >= 0) return RenameChartCategory(chart, index, text);
        int at = field.Offset - chart.Offset;
        if (!ChartTokens(chart.Syntax).Any(t => t.Token.Start == at && t.Token.Length == field.Length && t.Kind == field.Kind)) return false;
        string? value = EncodeField(text, field.Kind);
        if (value == null || DecodeField(value, field.Kind) == null) return false;
        string candidate = Page.Source[..at] + value + Page.Source[(at + field.Length)..];
        var parsed = ChartSource.Parse(candidate);
        if (parsed == null || !ChartTokens(parsed).Any(t => t.Token.Start == at && t.Token.Length == value.Length && t.Kind == field.Kind)) return false;
        return CommitChart(chart, [new(at, at + field.Length, value)]);
    }
    private static IEnumerable<(SourceToken Token, string Kind)> ChartTokens(ChartSource chart)
    {
        if (chart.TitleToken.Length > 0) yield return (chart.TitleToken, "text");
        foreach (var axis in new[] { chart.Horizontal, chart.Vertical, chart.Secondary }.OfType<ChartSourceAxis>())
        {
            if (axis.LabelToken.Length > 0) yield return (axis.LabelToken, "quoted");
            if (axis.MinToken.Length > 0) yield return (axis.MinToken, "number");
            if (axis.MaxToken.Length > 0) yield return (axis.MaxToken, "number");
            foreach (var tick in axis.Ticks) { yield return (tick.ValueToken, "number"); yield return (tick.LabelToken, "quoted"); }
        }
        foreach (var series in chart.Series)
        {
            if (series.LabelToken.Length > 0) yield return (series.LabelToken, "quoted");
            foreach (var point in series.Points)
            { yield return (point.YToken, "number"); if (point.XToken.Length > 0) yield return (point.XToken, "number"); }
        }
        foreach (var annotation in chart.Annotations)
        { yield return (annotation.LabelToken, "quoted"); yield return (annotation.YToken, "number"); }
    }

    public PacketDiagramEdit? OpenPacketDiagram() => Page.Family == "Packet" && PacketSource.Parse(Page.Source) is { } packet
        ? new(Source, ActivePage, Index.Lines[Page.StartLine].Start, packet) : null;
    public ChartDiagramEdit? OpenChartDiagram() => Page.Family == "Chart" && ChartSource.Parse(Page.Source) is { } chart
        ? new(Source, ActivePage, Index.Lines[Page.StartLine].Start, chart) : null;

    public PacketFieldEdit? OpenPacketField(SourceFieldEdit field) => CurrentStructuredField(field) && field.Kind == "packet-label"
        ? OpenPacketDiagram()?.Fields.FirstOrDefault(f => f.Offset == field.Offset && f.Syntax.LabelToken.Length == field.Length) : null;
    public ChartSeriesEdit? OpenChartSeries(SourceFieldEdit field)
    {
        if (!CurrentStructuredField(field) || OpenChartDiagram() is not { } chart) return null;
        int at = field.Offset - chart.Offset;
        return chart.Series.FirstOrDefault(s => s.Syntax.Row.Start <= at && at + field.Length <= s.Syntax.Row.End);
    }
    public ChartPointEdit? OpenChartPoint(SourceFieldEdit field) => OpenChartSeries(field)?.Points.FirstOrDefault(p =>
        p.Offset == field.Offset && p.Syntax.YToken.Length == field.Length || p.XOffset == field.Offset && p.Syntax.XToken.Length == field.Length);
    public int ChartCategoryIndex(ChartDiagramEdit chart, SourceFieldEdit field)
    {
        if (!Current(chart) || !CurrentStructuredField(field) || field.Kind != "chart-category") return -1;
        return Array.FindIndex(chart.Syntax.CategoryAxis.CategoryTokens, t => chart.Offset + t.Start == field.Offset && t.Length == field.Length);
    }
    private bool CurrentStructuredField(SourceFieldEdit field) => field.Page == ActivePage && ReferenceEquals(field.Document, Source);
    private bool Current(PacketDiagramEdit diagram) => diagram.Page == ActivePage && ReferenceEquals(diagram.Source, Source);
    private bool Current(ChartDiagramEdit diagram) => diagram.Page == ActivePage && ReferenceEquals(diagram.Source, Source);

    /// <summary>Inserts a field at the requested final index and shifts later addresses by its width, preserving gaps.</summary>
    public bool InsertPacketField(PacketDiagramEdit diagram, int index, int bitCount, string label)
    {
        if (!Current(diagram) || index < 0 || index > diagram.Fields.Count || bitCount is < 1 or > 46080 || !StructuredText.PacketLabel(label)) return false;
        var fields = diagram.Syntax.Fields;
        int first = index < fields.Count ? fields[index].First : fields.Count > 0 ? fields[^1].Last + 1 : 0;
        if ((long)first + bitCount > diagram.Columns * 360) return false;
        var changes = new List<Change>();
        for (int i = index; i < fields.Count; i++)
            PacketRangeChange(changes, fields[i], fields[i].First + bitCount, fields[i].Last + bitCount, i == index ? first + bitCount - 1 : fields[i - 1].Last + bitCount);
        int at = index < fields.Count ? fields[index].Row.Start : diagram.Syntax.InsertAt;
        string indent = fields.Count == 0 ? "  " : Indent(fields[Math.Min(index, fields.Count - 1)].Row);
        changes.Add(new(at, at, indent + PacketRange(first, first + bitCount - 1) + ": " + label + StructuredNewLine(at)));
        return CommitPacket(diagram, changes);
    }

    /// <summary>Removes just the selected declaration; optionally closes its occupied bit range in following fields.</summary>
    public bool RemovePacketField(PacketFieldEdit field, bool closeGap = false)
    {
        var diagram = field.Diagram;
        if (!Current(diagram)) return false;
        var selected = field.Syntax;
        var changes = new List<Change> { new(selected.Row.Start, selected.Row.Next, "") };
        for (int i = field.Index + 1; i < diagram.Fields.Count; i++)
        {
            var next = diagram.Syntax.Fields[i];
            int shift = closeGap ? selected.Width : 0;
            int previous = i == field.Index + 1 ? field.Index == 0 ? -1 : diagram.Syntax.Fields[field.Index - 1].Last : diagram.Syntax.Fields[i - 1].Last - shift;
            PacketRangeChange(changes, next, next.First - shift, next.Last - shift, previous);
        }
        return CommitPacket(diagram, changes);
    }

    /// <summary>Changes a range without moving another field. Overlaps and address-order changes are rejected atomically.</summary>
    public bool SetPacketFieldRange(PacketFieldEdit field, int firstBit, int lastBit)
    {
        if (!Current(field.Diagram) || firstBit < 0 || lastBit < firstBit || lastBit >= field.Diagram.Columns * 360) return false;
        var fields = field.Diagram.Syntax.Fields;
        var changes = new List<Change>();
        PacketRangeChange(changes, field.Syntax, firstBit, lastBit, field.Index == 0 ? -1 : fields[field.Index - 1].Last);
        // Automatic successors keep their previous addresses, so an explicit resize cannot silently move them.
        for (int i = field.Index + 1; i < fields.Count; i++)
            PacketRangeChange(changes, fields[i], fields[i].First, fields[i].Last, i == field.Index + 1 ? lastBit : fields[i - 1].Last);
        return CommitPacket(field.Diagram, changes);
    }

    /// <summary>Resizes one field, optionally shifting all later fields by the same delta.</summary>
    public bool ResizePacketField(PacketFieldEdit field, int bitCount, bool shiftFollowing = true)
    {
        if (!Current(field.Diagram) || bitCount is < 1 or > 46080 || bitCount == field.BitCount) return false;
        if (!shiftFollowing) return SetPacketFieldRange(field, field.FirstBit, field.FirstBit + bitCount - 1);
        int delta = bitCount - field.BitCount;
        var fields = field.Diagram.Syntax.Fields;
        var changes = new List<Change>();
        for (int i = field.Index; i < fields.Count; i++)
        {
            int first = fields[i].First + (i == field.Index ? 0 : delta), last = fields[i].Last + delta;
            int previous = i == 0 ? -1 : fields[i - 1].Last + (i > field.Index ? delta : 0);
            PacketRangeChange(changes, fields[i], first, last, previous);
        }
        return CommitPacket(field.Diagram, changes);
    }

    /// <summary>Splits a field into two adjacent declarations, retaining its attributes on both parts.</summary>
    public bool SplitPacketField(PacketFieldEdit field, int firstBitCount, string secondLabel)
    {
        if (!Current(field.Diagram) || firstBitCount <= 0 || firstBitCount >= field.BitCount || !StructuredText.PacketLabel(secondLabel)) return false;
        var syntax = field.Syntax;
        var changes = new List<Change>();
        PacketRangeChange(changes, syntax, syntax.First, syntax.First + firstBitCount - 1, field.Index == 0 ? -1 : field.Diagram.Syntax.Fields[field.Index - 1].Last);
        // Copy only the declaration text; an inline comment belongs to the original first part.
        int codeEnd = syntax.Row.Start + syntax.Row.Code.TrimEnd().Length;
        string second = Rewrite(Page.Source, [new(syntax.Range.Start, syntax.Range.End, syntax.LengthToken.Length > 0 ? FormattableString.Invariant($"{syntax.First + firstBitCount}-{syntax.Last}") : PacketRange(syntax.First + firstBitCount, syntax.Last)),
            new(syntax.LabelToken.Start, syntax.LabelToken.End, secondLabel)], syntax.Row.Start, codeEnd);
        changes.Add(new(syntax.Row.Next, syntax.Row.Next, second + StructuredNewLine(syntax.Row.Start)));
        return CommitPacket(field.Diagram, changes);
    }

    /// <summary>Moves a declaration to a final index. Widths and inter-field gaps are retained; addresses are recomputed.</summary>
    public bool MovePacketField(PacketFieldEdit field, int index)
    {
        var diagram = field.Diagram;
        if (!Current(diagram) || index < 0 || index >= diagram.Fields.Count || index == field.Index) return false;
        var original = diagram.Syntax.Fields;
        var ordered = original.ToList(); ordered.RemoveAt(field.Index); ordered.Insert(index, field.Syntax);
        var changes = new List<Change>();
        int first = original[0].First;
        for (int i = 0; i < original.Count; i++)
        {
            var moved = ordered[i];
            int last = first + moved.Width - 1;
            var rangeChanges = new List<Change>();
            PacketRangeChange(rangeChanges, moved, first, last, i == 0 ? -1 : first - (original[i].First - original[i - 1].Last - 1) - 1);
            string text = Rewrite(Page.Source, rangeChanges, moved.Row.Start, moved.Row.End);
            changes.Add(new(original[i].Row.Start, original[i].Row.End, text));
            if (i + 1 < original.Count) first = last + 1 + original[i + 1].First - original[i].Last - 1;
        }
        return CommitPacket(diagram, changes);
    }

    private static string PacketRange(int first, int last) => first == last ? first.ToString(CultureInfo.InvariantCulture) : FormattableString.Invariant($"{first}-{last}");
    private void PacketRangeChange(List<Change> changes, PacketSourceField field, int first, int last, int previous)
    {
        if (field.Automatic && first == previous + 1)
        {
            if (last - first + 1 == field.Width) return;
            if (field.LengthToken.Length > 0) { changes.Add(new(field.LengthToken.Start, field.LengthToken.End, (last - first + 1).ToString(CultureInfo.InvariantCulture))); return; }
        }
        else if (!field.Automatic && field.First == first && field.Last == last) return;
        // Explicit ranges override len; use a two-ended range even for a single bit with a retained len attribute.
        string replacement = field.LengthToken.Length > 0 ? FormattableString.Invariant($"{first}-{last}") : PacketRange(first, last);
        changes.Add(new(field.Range.Start, field.Range.End, replacement));
    }

    public bool InsertChartSeries(ChartDiagramEdit chart, int index, string kind, string label, IReadOnlyList<double> values, string? color = null, bool secondary = false, bool labels = false, bool interpolate = false, bool connect = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        return InsertChartSeriesCore(chart, index, kind, label, values.Select((y, x) => new ChartPoint(x, y)).ToArray(), false, color, secondary, labels, interpolate, connect);
    }
    public bool InsertChartCoordinateSeries(ChartDiagramEdit chart, int index, string kind, string label, IReadOnlyList<ChartPoint> points, string? color = null, bool secondary = false, bool labels = false, bool interpolate = false, bool connect = false)
        => InsertChartSeriesCore(chart, index, kind, label, points, true, color, secondary, labels, interpolate, connect);
    private bool InsertChartSeriesCore(ChartDiagramEdit chart, int index, string kind, string label, IReadOnlyList<ChartPoint> points, bool coordinates, string? color, bool secondary, bool labels, bool interpolate, bool connect)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!Current(chart) || index < 0 || index > chart.Series.Count || kind is not ("bar" or "line" or "area" or "scatter") || !StructuredText.Plain(label) || points.Count is < 1 or > 512 || points.Any(p => p.IsMissing ? kind != "line" || coordinates : !Finite(p.Y) || coordinates && !Finite(p.X))) return false;
        int at = index == chart.Series.Count ? chart.Syntax.InsertAt : chart.Syntax.Series[index].Row.Start;
        string indent = chart.Series.Count == 0 ? "" : Indent(chart.Syntax.Series[Math.Min(index, chart.Series.Count - 1)].Row);
        string data = string.Join(", ", points.Select(p => PointText(p, coordinates)));
        string text = indent + kind + (label.Length == 0 ? "" : " " + StructuredText.Quote(label)) + " [" + data + "]" +
            (color == null ? "" : " " + color) + (secondary ? " v2" : "") + (labels ? " labels" : "") + (interpolate ? " interpolate" : "") + (connect ? " connect" : "") + StructuredNewLine(at);
        return CommitChart(chart, [new(at, at, text)]);
    }
    /// <summary>Inserts an indexed line series with optional missing values.</summary>
    public bool InsertChartIndexedSeries(ChartDiagramEdit chart, int index, string kind, string label, IReadOnlyList<double?> values,
        string? color = null, bool secondary = false, bool labels = false, bool interpolate = false, bool connect = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        return InsertChartSeriesCore(chart, index, kind, label, values.Select((y, x) => new ChartPoint(x, y ?? 0, !y.HasValue)).ToArray(), false, color, secondary, labels, interpolate, connect);
    }
    /// <summary>Updates line interpolation and missing-value connection without rewriting its data or other flags.</summary>
    public bool SetChartLineOptions(ChartSeriesEdit series, bool interpolate, bool connect)
    {
        if (!Current(series.Diagram) || series.Kind != "line") return false;
        var changes = new List<Change>();
        int end = series.Syntax.Row.Start + series.Syntax.Row.Code.TrimEnd().Length;
        var flags = series.Syntax.Flags;
        if (series.Interpolate != interpolate)
        {
            if (interpolate) { int at = flags.TryGetValue("connect", out var before) ? before.Start : end; changes.Add(new(at, at, at == end ? " interpolate" : "interpolate ")); }
            else { var token = flags["interpolate"]; changes.Add(new(token.Start, token.End, "")); }
        }
        if (series.ConnectMissingValues != connect)
        {
            if (connect) changes.Add(new(end, end, " connect"));
            else { var token = flags["connect"]; changes.Add(new(token.Start, token.End, "")); }
        }
        return CommitChart(series.Diagram, changes);
    }
    public bool RemoveChartSeries(ChartSeriesEdit series) => Current(series.Diagram) && CommitChart(series.Diagram, [new(series.Syntax.Row.Start, series.Syntax.Row.Next, "")]);
    public bool MoveChartSeries(ChartSeriesEdit series, int index)
    {
        var chart = series.Diagram;
        if (!Current(chart) || index < 0 || index >= chart.Series.Count || index == series.Index) return false;
        var old = chart.Syntax.Series;
        var moved = old.ToList(); moved.RemoveAt(series.Index); moved.Insert(index, series.Syntax);
        return CommitChart(chart, old.Select((s, i) => new Change(s.Row.Start, s.Row.End, Page.Source[moved[i].Row.Start..moved[i].Row.End])).ToList());
    }

    /// <summary>Inserts one categorical column into the axis and every series in a single undo transaction.</summary>
    public bool InsertChartCategory(ChartDiagramEdit chart, int index, string label, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Current(chart) || !chart.HasCategoryLabels || index < 0 || index > chart.Categories.Count || !StructuredText.Category(label) || values.Count != chart.Series.Count || values.Any(v => !Finite(v))) return false;
        var changes = new List<Change> { InsertListItem(chart.Syntax.CategoryAxis.CategoryTokens, index, label) };
        for (int s = 0; s < chart.Series.Count; s++) changes.Add(InsertListItem(chart.Syntax.Series[s].Points.Select(p => p.Token).ToArray(), index, StructuredText.Format(values[s])));
        return CommitChart(chart, changes);
    }
    /// <summary>Removes one category and each corresponding value. Annotations anchored to it prevent removal.</summary>
    public bool RemoveChartCategory(ChartDiagramEdit chart, int index)
    {
        if (!Current(chart) || !chart.HasCategoryLabels || chart.Categories.Count <= 1 || index < 0 || index >= chart.Categories.Count) return false;
        string category = chart.Categories[index];
        if (chart.Syntax.Annotations.Any(a => (a.X.StartsWith('"') ? StructuredText.Unquote(a.X) : a.X) == category) && chart.Categories.Count(c => c == category) == 1) return false;
        var changes = new List<Change> { RemoveListItem(chart.Syntax.CategoryAxis.CategoryTokens, index) };
        foreach (var series in chart.Syntax.Series) changes.Add(RemoveListItem(series.Points.Select(p => p.Token).ToArray(), index));
        return CommitChart(chart, changes);
    }
    public bool MoveChartCategory(ChartDiagramEdit chart, int index, int targetIndex)
    {
        if (!Current(chart) || !chart.HasCategoryLabels || index < 0 || targetIndex < 0 || index >= chart.Categories.Count || targetIndex >= chart.Categories.Count || index == targetIndex) return false;
        var changes = MoveListItem(chart.Syntax.CategoryAxis.CategoryTokens, index, targetIndex);
        foreach (var series in chart.Syntax.Series) changes.AddRange(MoveListItem(series.Points.Select(p => p.Token).ToArray(), index, targetIndex));
        return CommitChart(chart, changes);
    }
    /// <summary>Renames a category and its matching annotation anchors without changing any numeric data.</summary>
    public bool RenameChartCategory(ChartDiagramEdit chart, int index, string label)
    {
        if (!Current(chart) || !chart.HasCategoryLabels || index < 0 || index >= chart.Categories.Count || !StructuredText.Category(label) || label == chart.Categories[index]) return false;
        var token = chart.Syntax.CategoryAxis.CategoryTokens[index];
        var changes = new List<Change> { new(token.Start, token.End, label) };
        if (chart.Categories.Count(c => c == chart.Categories[index]) == 1)
            foreach (var annotation in chart.Syntax.Annotations.Where(a => (a.X.StartsWith('"') ? StructuredText.Unquote(a.X) : a.X) == chart.Categories[index]))
                changes.Add(new(annotation.XToken.Start, annotation.XToken.End, label));
        return CommitChart(chart, changes);
    }

    /// <summary>Inserts into a coordinate or automatically indexed series. Explicit categories require a column operation.</summary>
    public bool InsertChartPoint(ChartSeriesEdit series, int index, ChartPoint point)
    {
        if (!Current(series.Diagram) || series.Diagram.HasCategoryLabels || index < 0 || index > series.Points.Count || (point.IsMissing ? series.Kind != "line" || series.UsesCoordinates : !Finite(point.Y) || series.UsesCoordinates && !Finite(point.X))) return false;
        return CommitChart(series.Diagram, [InsertListItem(series.Syntax.Points.Select(p => p.Token).ToArray(), index, PointText(point, series.UsesCoordinates))]);
    }
    public bool RemoveChartPoint(ChartPointEdit point)
    {
        var series = point.Series;
        return Current(series.Diagram) && !series.Diagram.HasCategoryLabels && series.Points.Count > 1 &&
            CommitChart(series.Diagram, [RemoveListItem(series.Syntax.Points.Select(p => p.Token).ToArray(), point.Index)]);
    }
    public bool MoveChartPoint(ChartPointEdit point, int index)
    {
        var series = point.Series;
        return Current(series.Diagram) && !series.Diagram.HasCategoryLabels && index >= 0 && index < series.Points.Count && index != point.Index &&
            CommitChart(series.Diagram, MoveListItem(series.Syntax.Points.Select(p => p.Token).ToArray(), point.Index, index));
    }
    public bool SetChartPoint(ChartPointEdit point, ChartPoint value)
    {
        var series = point.Series;
        if (!Current(series.Diagram) || (value.IsMissing ? series.Kind != "line" || series.UsesCoordinates : !Finite(value.Y) || series.UsesCoordinates && !Finite(value.X))) return false;
        var changes = new List<Change>();
        if (point.IsMissing != value.IsMissing || !value.IsMissing && point.Y != value.Y) changes.Add(new(point.Syntax.YToken.Start, point.Syntax.YToken.End, value.IsMissing ? " " : StructuredText.Format(value.Y)));
        if (series.UsesCoordinates && point.X != value.X) changes.Add(new(point.Syntax.XToken.Start, point.Syntax.XToken.End, StructuredText.Format(value.X)));
        return CommitChart(series.Diagram, changes);
    }

    private static bool Finite(double number) => double.IsFinite(number) && Math.Abs(number) <= 1e12;
    private static string PointText(ChartPoint point, bool coordinates) => point.IsMissing ? " " : coordinates ? "(" + StructuredText.Format(point.X) + ", " + StructuredText.Format(point.Y) + ")" : StructuredText.Format(point.Y);
    private string Indent(SourceRow row) => Page.Source[row.Start..row.ContentStart];
    private string StructuredNewLine(int at)
    {
        int index = at;
        while (index < Page.Source.Length && Page.Source[index] is not ('\r' or '\n')) index++;
        return index == Page.Source.Length ? Index.NewLine : Page.Source[index] == '\r' ? index + 1 < Page.Source.Length && Page.Source[index + 1] == '\n' ? "\r\n" : "\r" : "\n";
    }
    private string ListSeparator(SourceToken[] tokens)
    {
        string separator = tokens.Length < 2 ? ", " : Page.Source[tokens[0].End..tokens[1].Start];
        // A separator may contain a block comment. Reuse its whitespace convention, never duplicate its prose.
        return separator.Contains("/'", StringComparison.Ordinal) ? ", " : separator;
    }
    private Change InsertListItem(SourceToken[] tokens, int index, string value)
    {
        string separator = ListSeparator(tokens);
        return index == tokens.Length ? new(tokens[^1].End, tokens[^1].End, separator + value) : new(tokens[index].Start, tokens[index].Start, value + separator);
    }
    private Change RemoveListItem(SourceToken[] tokens, int index)
    {
        int start = index + 1 < tokens.Length ? tokens[index].Start : tokens[index - 1].End;
        int end = index + 1 < tokens.Length ? tokens[index + 1].Start : tokens[index].End;
        string trivia = index + 1 < tokens.Length ? Page.Source[tokens[index].End..end] : Page.Source[start..tokens[index].Start];
        if (!trivia.Contains("/'", StringComparison.Ordinal)) return new(start, end, "");
        bool block = false;
        for (int i = 0; i < trivia.Length; i++)
        {
            if (!block && i + 1 < trivia.Length && trivia[i] == '/' && trivia[i + 1] == '\'') { block = true; i++; }
            else if (block && i + 1 < trivia.Length && trivia[i] == '\'' && trivia[i + 1] == '/') { block = false; i++; }
            else if (!block && trivia[i] == ',') return new(start, end, trivia.Remove(i, 1));
        }
        return new(start, end, trivia);
    }
    private List<Change> MoveListItem(SourceToken[] tokens, int index, int target)
    {
        var items = tokens.Select(t => t.Length == 0 ? " " : Page.Source.Substring(t.Start, t.Length)).ToList();
        string moved = items[index]; items.RemoveAt(index); items.Insert(target, moved);
        return tokens.Select((t, i) => new Change(t.Start, t.End, items[i])).ToList();
    }
    private bool CommitPacket(PacketDiagramEdit diagram, List<Change> changes) => Current(diagram) && CommitStructured(diagram.Offset, changes, candidate => PacketSource.Parse(candidate) != null);
    private bool CommitChart(ChartDiagramEdit diagram, List<Change> changes) => Current(diagram) && CommitStructured(diagram.Offset, changes, candidate => ChartSource.Parse(candidate) != null);
    private bool CommitStructured(int pageOffset, List<Change> changes, Func<string, bool> validate)
    {
        if (changes.Count == 0) return false;
        var ordered = changes.OrderBy(c => c.At).ThenBy(c => c.End).ToArray();
        int cursor = 0;
        foreach (var change in ordered)
        { if (change.At < cursor || change.At < 0 || change.End < change.At || change.End > Page.Source.Length) return false; cursor = change.End; }
        string candidate = Rewrite(Page.Source, changes, 0, Page.Source.Length);
        if (candidate == Page.Source || !validate(candidate)) return false;
        return ApplyChanges(changes.Select(c => new Change(pageOffset + c.At, pageOffset + c.End, c.Text)).ToList());
    }
}
