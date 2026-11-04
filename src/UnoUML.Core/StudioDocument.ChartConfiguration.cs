using System.Text;
using UnoUML.Syntax;

namespace UnoUML.Core;

public enum ChartAxisKind { Horizontal, Vertical, Secondary }
public enum ChartAxisLabelPosition { Default, Top, Right }
public enum ChartLegend { None, Left, Right, Top, Bottom }
public enum ChartOrientation { Vertical, Horizontal }
public enum ChartStackMode { Grouped, Stacked }
public enum ChartSetting { Title, Legend, Orientation, StackMode }
public readonly record struct ChartTick(double Value, string Label);
public sealed record ChartSettings(string Title = "", ChartLegend Legend = ChartLegend.None,
    ChartOrientation? Orientation = null, ChartStackMode? StackMode = null);

/// <summary>Categories is null for a numeric axis; a null spacing uses the renderer's automatic tick interval.</summary>
public sealed record ChartAxisDefinition
{
    public string Label { get; init; } = "";
    public IReadOnlyList<string>? Categories { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 100;
    public double? Spacing { get; init; }
    public bool Grid { get; init; }
    public ChartAxisLabelPosition LabelPosition { get; init; }
    public IReadOnlyList<ChartTick> Ticks { get; init; } = [];
}

public sealed class ChartAxisEdit
{
    internal ChartAxisEdit(ChartDiagramEdit diagram, ChartAxisKind kind, ChartSourceAxis syntax)
    {
        Diagram = diagram; Kind = kind; Syntax = syntax;
        Definition = new()
        {
            Label = syntax.Label, Categories = syntax.Categorical ? Array.AsReadOnly(syntax.Categories.ToArray()) : null,
            Minimum = syntax.Min, Maximum = syntax.Max, Grid = syntax.Grid,
            Spacing = syntax.Options.ContainsKey("spacing") ? syntax.Categorical ? syntax.CategorySpacing : syntax.Step : null,
            LabelPosition = syntax.LabelPosition == "label-top" ? ChartAxisLabelPosition.Top : syntax.LabelPosition == "label-right" ? ChartAxisLabelPosition.Right : ChartAxisLabelPosition.Default,
            Ticks = Array.AsReadOnly(syntax.Ticks.Select(t => new ChartTick(t.Value, t.Label)).ToArray())
        };
    }
    public ChartDiagramEdit Diagram { get; }
    public ChartAxisKind Kind { get; }
    public bool IsExplicit => Syntax.Row != null;
    public ChartAxisDefinition Definition { get; }
    internal ChartSourceAxis Syntax { get; }
}

public sealed class ChartAnnotationEdit
{
    internal ChartAnnotationEdit(ChartDiagramEdit diagram, int index) { Diagram = diagram; Index = index; }
    public ChartDiagramEdit Diagram { get; }
    public int Index { get; }
    internal ChartSourceAnnotation Syntax => Diagram.Syntax.Annotations[Index];
    public string Label => Syntax.Label;
    public string X => Syntax.X.StartsWith('"') && Syntax.X.EndsWith('"') ? StructuredText.Unquote(Syntax.X) : Syntax.X;
    public double Y => Syntax.Y;
    public bool Arrow => Syntax.Arrow;
}

public sealed partial class StudioDocument
{
    public ChartAnnotationEdit? OpenChartAnnotation(SourceFieldEdit field)
    {
        if (!CurrentStructuredField(field) || OpenChartDiagram() is not { } chart) return null;
        int at = field.Offset - chart.Offset;
        return chart.Annotations.FirstOrDefault(a => a.Syntax.Row.Start <= at && at + field.Length <= a.Syntax.Row.End);
    }

    /// <summary>Creates or edits an axis, retaining unaffected literals, option spelling, comments and annotation references.</summary>
    public bool SetChartAxis(ChartDiagramEdit chart, ChartAxisKind kind, ChartAxisDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Current(chart) || !ValidChartAxisDefinition(kind, definition)) return false;
        var axis = Axis(chart, kind);
        string name = AxisName(kind);
        if (axis?.Row == null)
        {
            int at = chart.Syntax.BodyStart;
            return CommitChart(chart, [new(at, at, name + " " + AxisDeclaration(definition) + StructuredNewLine(at))]);
        }
        var changes = new List<Change>();
        if (definition.Label != axis.Label)
        {
            if (axis.LabelToken.Length > 0) ReplaceChartToken(changes, axis.LabelToken, definition.Label.Length == 0 ? "" : StructuredText.Quote(definition.Label));
            else if (definition.Label.Length > 0) changes.Add(new(axis.KindToken.End, axis.KindToken.End, " " + StructuredText.Quote(definition.Label)));
        }
        bool categorical = definition.Categories != null;
        if (categorical != axis.Categorical)
        {
            var span = axis.Categorical ? new SourceToken(axis.ListToken.Start - 1, axis.ListToken.Length + 2) : new SourceToken(axis.MinToken.Start, axis.MaxToken.End - axis.MinToken.Start);
            ReplaceChartToken(changes, span, AxisData(definition));
        }
        else if (categorical)
        {
            var categories = definition.Categories!;
            if (categories.Count == axis.Categories.Length)
            {
                for (int i = 0; i < categories.Count; i++)
                    if (categories[i] != axis.Categories[i]) ReplaceChartToken(changes, axis.CategoryTokens[i], categories[i]);
            }
            else ReplaceChartToken(changes, axis.ListToken, string.Join(", ", categories));
            // A uniquely named category has one unambiguous annotation anchor.
            foreach (var annotation in chart.Syntax.Annotations)
            {
                string old = annotation.X.StartsWith('"') ? StructuredText.Unquote(annotation.X) : annotation.X;
                int index = Array.IndexOf(axis.Categories, old);
                if (index >= 0 && index < categories.Count && axis.Categories.Count(c => c == old) == 1 && categories[index] != old)
                    ReplaceChartToken(changes, annotation.XToken, annotation.X.StartsWith('"') ? StructuredText.Quote(categories[index]) : categories[index]);
            }
        }
        else
        {
            if (axis.Min != definition.Minimum) ReplaceChartToken(changes, axis.MinToken, StructuredText.Format(definition.Minimum));
            if (axis.Max != definition.Maximum) ReplaceChartToken(changes, axis.MaxToken, StructuredText.Format(definition.Maximum));
        }
        // New options use upstream command order; existing tokens retain their positions and spelling.
        if (!axis.Ticks.Select(t => new ChartTick(t.Value, t.Label)).SequenceEqual(definition.Ticks))
            AxisOption(changes, axis, "ticks", definition.Ticks.Count == 0 ? null : TickDeclaration(definition.Ticks));
        double? oldSpacing = axis.Options.ContainsKey("spacing") ? axis.Categorical ? axis.CategorySpacing : axis.Step : null;
        if (oldSpacing != definition.Spacing) AxisOption(changes, axis, "spacing", definition.Spacing is { } spacing ? StructuredText.Format(spacing) : null);
        string labelPosition = definition.LabelPosition switch { ChartAxisLabelPosition.Top => "label-top", ChartAxisLabelPosition.Right => "label-right", _ => "" };
        if (axis.LabelPosition != labelPosition)
        {
            if (axis.LabelPosition.Length > 0) AxisOption(changes, axis, axis.LabelPosition, null);
            if (labelPosition.Length > 0) AxisOption(changes, axis, labelPosition, "");
        }
        if (axis.Grid != definition.Grid) AxisOption(changes, axis, "grid", definition.Grid ? "" : null);
        return CommitChart(chart, changes);
    }

    /// <summary>Removes an explicit axis. A secondary axis can atomically return its series to the primary axis.</summary>
    public bool RemoveChartAxis(ChartDiagramEdit chart, ChartAxisKind kind, bool usePrimaryForSecondarySeries = false)
    {
        if (!Current(chart) || !Enum.IsDefined(kind) || Axis(chart, kind)?.Row is not { } row) return false;
        var changes = new List<Change> { new(row.Start, row.Next, "") };
        if (kind == ChartAxisKind.Secondary && usePrimaryForSecondarySeries)
            foreach (var series in chart.Syntax.Series.Where(s => s.Secondary))
            {
                var token = series.Flags.TryGetValue("v2", out var v2) ? v2 : series.Flags["y2"];
                changes.Add(new(token.Start, token.End, ""));
            }
        return CommitChart(chart, changes);
    }

    /// <summary>Updates title, legend, orientation and stacking as one transaction. Null orientation/stacking removes that explicit setting.</summary>
    public bool ApplyChartSettings(ChartDiagramEdit chart, ChartSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Current(chart) || settings.Title == null || !StructuredText.Plain(settings.Title) || !StructuredText.Unquoted(settings.Title) || !Enum.IsDefined(settings.Legend) ||
            settings.Orientation is { } orientation && !Enum.IsDefined(orientation) || settings.StackMode is { } stack && !Enum.IsDefined(stack)) return false;
        bool horizontal = settings.Orientation == ChartOrientation.Horizontal;
        var changes = new List<Change>();
        if (horizontal != chart.IsHorizontal)
        {
            if (!chart.Syntax.CategoryAxis.Categorical) return false;
            foreach (var axis in new[] { chart.Syntax.Horizontal, chart.Syntax.Vertical })
            {
                if (axis.Row == null) continue;
                ReplaceChartToken(changes, axis.KindToken, ReferenceEquals(axis, chart.Syntax.Horizontal) ? "v-axis" : "h-axis");
                if (axis.LabelPosition.Length > 0)
                {
                    var token = axis.Options[axis.LabelPosition].Token;
                    ReplaceChartToken(changes, token, axis.LabelPosition == "label-top" ? "label-right" : "label-top");
                }
            }
        }
        ChartSettingChange(chart, changes, "title", settings.Title.Length == 0 ? null : settings.Title);
        ChartSettingChange(chart, changes, "legend", settings.Legend == ChartLegend.None ? null : settings.Legend.ToString().ToLowerInvariant());
        ChartSettingChange(chart, changes, "orientation", settings.Orientation?.ToString().ToLowerInvariant());
        ChartSettingChange(chart, changes, "stackmode", settings.StackMode?.ToString().ToLowerInvariant());
        return CommitChart(chart, changes);
    }
    public bool RemoveChartSetting(ChartDiagramEdit chart, ChartSetting setting)
    {
        if (!Current(chart) || !Enum.IsDefined(setting) || !chart.Syntax.Settings.ContainsKey(setting.ToString().ToLowerInvariant())) return false;
        var settings = chart.Settings;
        return ApplyChartSettings(chart, setting switch
        {
            ChartSetting.Title => settings with { Title = "" }, ChartSetting.Legend => settings with { Legend = ChartLegend.None },
            ChartSetting.Orientation => settings with { Orientation = null }, _ => settings with { StackMode = null }
        });
    }

    public bool InsertChartAnnotation(ChartDiagramEdit chart, int index, string label, string x, double y, bool arrow = false)
    {
        if (!Current(chart) || index < 0 || index > chart.Annotations.Count || !ValidAnnotation(chart, label, x, y)) return false;
        int at = index < chart.Annotations.Count ? chart.Syntax.Annotations[index].Row.Start : chart.Syntax.InsertAt;
        string indent = chart.Annotations.Count == 0 ? "" : Indent(chart.Syntax.Annotations[Math.Min(index, chart.Annotations.Count - 1)].Row);
        string declaration = indent + "annotation " + StructuredText.Quote(label) + " at (" + x + "," + StructuredText.Format(y) + ")" + (arrow ? " <<arrow>>" : "");
        return CommitChart(chart, [new(at, at, declaration + StructuredNewLine(at))]);
    }
    public bool UpdateChartAnnotation(ChartAnnotationEdit annotation, string label, string x, double y, bool arrow)
    {
        var chart = annotation.Diagram;
        if (!Current(chart) || !ValidAnnotation(chart, label, x, y)) return false;
        var syntax = annotation.Syntax; var changes = new List<Change>();
        if (annotation.Label != label) ReplaceChartToken(changes, syntax.LabelToken, StructuredText.Quote(label));
        if (annotation.X != x) ReplaceChartToken(changes, syntax.XToken, syntax.X.StartsWith('"') ? StructuredText.Quote(x) : x);
        if (annotation.Y != y) ReplaceChartToken(changes, syntax.YToken, StructuredText.Format(y));
        if (annotation.Arrow != arrow)
        {
            if (arrow) { int at = syntax.Row.Start + syntax.Row.Code.TrimEnd().Length; changes.Add(new(at, at, " <<arrow>>")); }
            else changes.Add(new(syntax.ArrowToken.Start, syntax.ArrowToken.End, ""));
        }
        return CommitChart(chart, changes);
    }
    public bool RemoveChartAnnotation(ChartAnnotationEdit annotation) => Current(annotation.Diagram) &&
        CommitChart(annotation.Diagram, [new(annotation.Syntax.Row.Start, annotation.Syntax.Row.Next, "")]);

    private static bool ValidAnnotation(ChartDiagramEdit chart, string label, string x, double y) => StructuredText.Plain(label) && label.Length > 0 &&
        Finite(y) && (chart.Syntax.CategoryAxis.Categorical ? chart.Categories.Contains(x) : StructuredText.Number(x, out _));
    private static ChartSourceAxis? Axis(ChartDiagramEdit chart, ChartAxisKind kind) => kind switch
    { ChartAxisKind.Horizontal => chart.Syntax.Horizontal, ChartAxisKind.Vertical => chart.Syntax.Vertical, ChartAxisKind.Secondary => chart.Syntax.Secondary, _ => null };
    private static string AxisName(ChartAxisKind kind) => kind switch { ChartAxisKind.Horizontal => "h-axis", ChartAxisKind.Vertical => "v-axis", _ => "v2-axis" };
    private static bool ValidChartAxisDefinition(ChartAxisKind kind, ChartAxisDefinition definition) => Enum.IsDefined(kind) && StructuredText.Plain(definition.Label) &&
        Enum.IsDefined(definition.LabelPosition) && definition.Ticks != null &&
        (definition.Categories is { } categories ? categories.Count is > 0 and <= 512 && categories.All(StructuredText.Category) && definition.Ticks.Count == 0 : Finite(definition.Minimum) && Finite(definition.Maximum) && definition.Minimum < definition.Maximum) &&
        (definition.Spacing == null || Finite(definition.Spacing.Value) && definition.Spacing > 0 && (definition.Categories == null || definition.Spacing.Value == Math.Floor(definition.Spacing.Value))) &&
        definition.Ticks.Count <= 128 && definition.Ticks.All(t => Finite(t.Value) && StructuredText.Plain(t.Label));
    private static string AxisData(ChartAxisDefinition definition) => definition.Categories is { } categories ? "[" + string.Join(", ", categories) + "]" : StructuredText.Format(definition.Minimum) + " --> " + StructuredText.Format(definition.Maximum);
    private static string TickDeclaration(IReadOnlyList<ChartTick> ticks) => "[" + string.Join(", ", ticks.Select(t => StructuredText.Format(t.Value) + ":" + StructuredText.Quote(t.Label))) + "]";
    private static string AxisDeclaration(ChartAxisDefinition definition) => (definition.Label.Length == 0 ? "" : StructuredText.Quote(definition.Label) + " ") + AxisData(definition) +
        (definition.Ticks.Count == 0 ? "" : " ticks " + TickDeclaration(definition.Ticks)) +
        (definition.Spacing is { } spacing ? " spacing " + StructuredText.Format(spacing) : "") +
        (definition.LabelPosition == ChartAxisLabelPosition.Default ? "" : definition.LabelPosition == ChartAxisLabelPosition.Top ? " label-top" : " label-right") +
        (definition.Grid ? " grid" : "");
    private void ChartSettingChange(ChartDiagramEdit chart, List<Change> changes, string key, string? value)
    {
        if (chart.Syntax.Settings.TryGetValue(key, out var setting))
        {
            if (value == null) changes.Add(new(setting.Row.Start, setting.Row.Next, ""));
            else if (setting.Row.Code.Substring(setting.Value.Start - setting.Row.Start, setting.Value.Length) != value) ReplaceChartToken(changes, setting.Value, value);
        }
        else if (value != null)
        {
            int at = chart.Syntax.BodyStart;
            changes.Add(new(at, at, (key == "stackmode" ? "stackMode" : key) + " " + value + StructuredNewLine(at)));
        }
    }
    private void AxisOption(List<Change> changes, ChartSourceAxis axis, string name, string? value)
    {
        if (axis.Options.TryGetValue(name, out var option))
        {
            if (value == null) ReplaceChartToken(changes, option.Token, "");
            else if (option.Value.Length > 0) ReplaceChartToken(changes, option.Value, value);
        }
        else if (value != null)
        {
            int end = axis.Row!.Start + axis.Row.Code.TrimEnd().Length;
            int at = axis.Options.Where(o => AxisOptionOrder(o.Key) > AxisOptionOrder(name)).Select(o => o.Value.Token.Start).DefaultIfEmpty(end).Min();
            string text = name + (value.Length == 0 ? "" : " " + value);
            changes.Add(new(at, at, at == end ? " " + text : text + " "));
        }
    }
    private static int AxisOptionOrder(string name) => name switch { "ticks" => 0, "spacing" => 1, "grid" => 3, _ => 2 };
    private void ReplaceChartToken(List<Change> changes, SourceToken token, string replacement)
    {
        string original = Page.Source.Substring(token.Start, token.Length);
        // Comment text inside a changed compound span survives even when its numeric/list syntax changes.
        var comments = new StringBuilder(); bool quoted = false;
        for (int i = 0; i + 1 < original.Length; i++)
        {
            if (quoted && original[i] == '\\') { i++; continue; }
            if (original[i] == '"') { quoted = !quoted; continue; }
            if (quoted || original[i] != '/' || original[i + 1] != '\'') continue;
            int end = original.IndexOf("'/", i + 2, StringComparison.Ordinal);
            if (end < 0) break;
            comments.Append(' ').Append(original, i, end + 2 - i); i = end + 1;
        }
        changes.Add(new(token.Start, token.End, replacement + comments));
    }
}
