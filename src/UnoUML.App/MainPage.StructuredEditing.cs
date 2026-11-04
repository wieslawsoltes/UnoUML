using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private const string StructuredFailure = "The source changed, or these values would create an invalid range or mismatched chart data.";

    private void AddStructuredDiagramActions()
    {
        if (doc.OpenPacketDiagram() is { } packet)
        {
            Properties.Children.Add(Caption($"{packet.Fields.Count} fields · {packet.Columns} bits per row"));
            Properties.Children.Add(Item("Insert packet field…", "add", () => _ = InsertPacketAsync(packet, packet.Fields.Count)));
            if (packet.Fields.Count > 0)
            {
                var selectedField = StructuredPicker("Packet field", packet.Fields.Select(f => $"{f.FirstBit}–{f.LastBit}: {f.Label}").ToArray(), 0);
                Properties.Children.Add(selectedField);
                Properties.Children.Add(Item("Edit packet field…", "edit", () => _ = ResizePacketAsync(packet.Fields[selectedField.SelectedIndex])));
                Properties.Children.Add(Item("Set packet bit range…", "edit", () => _ = SetPacketRangeAsync(packet.Fields[selectedField.SelectedIndex])));
                Properties.Children.Add(Item("Split packet field…", "edit", () => _ = SplitPacketAsync(packet.Fields[selectedField.SelectedIndex])));
                Properties.Children.Add(Item("Reorder packet field…", "up", () => _ = MovePacketAsync(packet.Fields[selectedField.SelectedIndex])));
                Properties.Children.Add(Item("Remove packet field", "delete", () => Edit(() => doc.RemovePacketField(packet.Fields[selectedField.SelectedIndex]), StructuredFailure)));
            }
        }
        if (doc.OpenChartDiagram() is { } chart)
        {
            AddChartDiagramActions(chart);
            if (chart.Series.Count > 0)
            {
                var series = StructuredPicker("Chart series", chart.Series.Select(s => s.Label.Length > 0 ? s.Label : $"{s.Kind} {s.Index + 1}").ToArray(), 0);
                Properties.Children.Add(series);
                Properties.Children.Add(Item("Line interpolation and gaps…", "edit", () => _ = EditChartLineOptionsAsync(chart.Series[series.SelectedIndex])));
                Properties.Children.Add(Item("Reorder chart series…", "up", () => _ = MoveChartSeriesAsync(chart.Series[series.SelectedIndex])));
                Properties.Children.Add(Item("Remove chart series", "delete", () => Edit(() => doc.RemoveChartSeries(chart.Series[series.SelectedIndex]), StructuredFailure)));
                Properties.Children.Add(Item("Edit chart data point…", "edit", () => _ = ChooseChartPointAsync(chart.Series[series.SelectedIndex])));
                if (!chart.HasCategoryLabels) Properties.Children.Add(Item("Insert chart data point…", "add", () => _ = InsertChartPointAsync(chart.Series[series.SelectedIndex], chart.Series[series.SelectedIndex].Points.Count)));
            }
            if (chart.HasCategoryLabels)
            {
                var category = StructuredPicker("Chart category", chart.Categories, 0);
                Properties.Children.Add(category);
                Properties.Children.Add(Item("Reorder chart category…", "up", () => _ = MoveChartCategoryAsync(chart, category.SelectedIndex)));
                Properties.Children.Add(Item("Remove chart category", "delete", () => Edit(() => doc.RemoveChartCategory(chart, category.SelectedIndex), StructuredFailure)));
            }
        }
    }

    private void AddStructuredFieldActions(SourceFieldEdit field)
    {
        if (doc.OpenPacketField(field) is { } packet)
        {
            Properties.Children.Add(Caption($"Bits {packet.FirstBit}–{packet.LastBit} · {packet.BitCount} bits"));
            Properties.Children.Add(Item("Resize packet field…", "edit", () => _ = ResizePacketAsync(packet)));
            Properties.Children.Add(Item("Set packet bit range…", "edit", () => _ = SetPacketRangeAsync(packet)));
            Properties.Children.Add(Item("Split packet field…", "edit", () => _ = SplitPacketAsync(packet)));
            Properties.Children.Add(Item("Insert packet field…", "add", () => _ = InsertPacketAsync(packet.Diagram, packet.Index + 1)));
            Properties.Children.Add(Item("Reorder packet field…", "up", () => _ = MovePacketAsync(packet)));
            Properties.Children.Add(Item("Remove packet field", "delete", () => Edit(() => doc.RemovePacketField(packet), StructuredFailure)));
        }
        if (doc.OpenChartDiagram() is not { } chart) return;
        AddChartDiagramActions(chart);
        if (doc.OpenChartAnnotation(field) is { } annotation)
            Properties.Children.Add(Item("Edit chart annotation…", "edit", () => _ = ManageChartAnnotationsAsync(annotation.Diagram, annotation.Index)));
        int categoryIndex = doc.ChartCategoryIndex(chart, field);
        if (categoryIndex >= 0)
        {
            Properties.Children.Add(Caption("Category operations update the matching value in every series."));
            Properties.Children.Add(Item("Insert category here…", "add", () => _ = InsertChartCategoryAsync(chart, categoryIndex)));
            Properties.Children.Add(Item("Reorder chart category…", "up", () => _ = MoveChartCategoryAsync(chart, categoryIndex)));
            Properties.Children.Add(Item("Remove chart category", "delete", () => Edit(() => doc.RemoveChartCategory(chart, categoryIndex), StructuredFailure)));
        }
        if (doc.OpenChartSeries(field) is not { } series) return;
        Properties.Children.Add(Caption($"{series.Kind} · {series.Points.Count} data points"));
        if (series.Kind == "line") Properties.Children.Add(Item("Line interpolation and gaps…", "edit", () => _ = EditChartLineOptionsAsync(series)));
        Properties.Children.Add(Item("Reorder chart series…", "up", () => _ = MoveChartSeriesAsync(series)));
        Properties.Children.Add(Item("Remove chart series", "delete", () => Edit(() => doc.RemoveChartSeries(series), StructuredFailure)));
        if (doc.OpenChartPoint(field) is { } point)
        {
            Properties.Children.Add(Item("Edit chart data point…", "edit", () => _ = EditChartPointAsync(point)));
            if (!chart.HasCategoryLabels)
            {
                Properties.Children.Add(Item("Insert chart data point…", "add", () => _ = InsertChartPointAsync(series, point.Index + 1)));
                Properties.Children.Add(Item("Reorder chart data point…", "up", () => _ = MoveChartPointAsync(point)));
                Properties.Children.Add(Item("Remove chart data point", "delete", () => Edit(() => doc.RemoveChartPoint(point), StructuredFailure)));
            }
            else
            {
                Properties.Children.Add(Item("Reorder category and values…", "up", () => _ = MoveChartCategoryAsync(chart, point.Index)));
                Properties.Children.Add(Item("Remove category and values", "delete", () => Edit(() => doc.RemoveChartCategory(chart, point.Index), StructuredFailure)));
            }
        }
        else Properties.Children.Add(Item("Edit chart data point…", "edit", () => _ = ChooseChartPointAsync(series)));
    }

    private void AddChartDiagramActions(ChartDiagramEdit chart)
    {
        Properties.Children.Add(Item("Chart axes…", "edit", () => _ = ManageChartAxesAsync(chart)));
        Properties.Children.Add(Item("Chart settings…", "edit", () => _ = EditChartSettingsAsync(chart)));
        Properties.Children.Add(Item("Chart annotations…", "edit", () => _ = ManageChartAnnotationsAsync(chart, chart.Annotations.Count)));
        Properties.Children.Add(Item("Insert chart series…", "add", () => _ = InsertChartSeriesAsync(chart)));
        if (chart.HasCategoryLabels) Properties.Children.Add(Item("Insert chart category…", "add", () => _ = InsertChartCategoryAsync(chart, chart.Categories.Count)));
    }
    private static TextBox StructuredTextInput(string name, string value = "")
    {
        var input = new TextBox { Header = name, Text = value, MinWidth = 260 };
        AutomationProperties.SetName(input, name); return input;
    }
    private static ComboBox StructuredPicker(string name, IEnumerable<string> values, int selected)
    {
        var input = new ComboBox { Header = name, ItemsSource = values.ToArray(), SelectedIndex = selected, MinWidth = 240 };
        AutomationProperties.SetName(input, name); return input;
    }
    private static bool StructuredInteger(TextBox input, out int value) => int.TryParse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    private static bool StructuredNumber(TextBox input, out double value) => double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static string StructuredNumberText(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private bool StructuredStillCurrent(StudioDocument document) => ReferenceEquals(document, doc);
    private void StructuredInvalid() => Status("Enter valid numeric values using a decimal point. Bit widths and positions must be whole numbers.", true);

    private async Task InsertPacketAsync(PacketDiagramEdit packet, int index)
    {
        var document = doc;
        var label = StructuredTextInput("Field label", "New field"); var width = StructuredTextInput("Bit count", "8");
        var position = StructuredPicker("Insert position", packet.Fields.Select(f => "Before " + f.Label).Append("After last field"), index);
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(label); fields.Children.Add(width); fields.Children.Add(position);
        if (await AskAsync("Insert packet field", fields, "Insert") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        if (StructuredInteger(width, out int bits)) Edit(() => doc.InsertPacketField(packet, position.SelectedIndex, bits, label.Text), StructuredFailure); else StructuredInvalid();
    }
    private async Task ResizePacketAsync(PacketFieldEdit field)
    {
        var document = doc;
        var width = StructuredTextInput("Bit count", field.BitCount.ToString(CultureInfo.InvariantCulture));
        var shift = new CheckBox { Content = "Shift following fields", IsChecked = true }; AutomationProperties.SetName(shift, "Shift following fields");
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(width); fields.Children.Add(shift);
        fields.Children.Add(Caption("Shifting preserves the existing gaps. Without shifting, the new range must fit before the next field."));
        if (await AskAsync("Resize packet field", fields, "Apply") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        if (StructuredInteger(width, out int bits)) Edit(() => doc.ResizePacketField(field, bits, shift.IsChecked == true), StructuredFailure); else StructuredInvalid();
    }
    private async Task SetPacketRangeAsync(PacketFieldEdit field)
    {
        var document = doc;
        var first = StructuredTextInput("First bit", field.FirstBit.ToString(CultureInfo.InvariantCulture)); var last = StructuredTextInput("Last bit", field.LastBit.ToString(CultureInfo.InvariantCulture));
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(first); fields.Children.Add(last); fields.Children.Add(Caption("Other fields keep their addresses. Overlapping or reversed ranges are rejected."));
        if (await AskAsync("Set packet bit range", fields, "Apply") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        if (StructuredInteger(first, out int from) && StructuredInteger(last, out int to)) Edit(() => doc.SetPacketFieldRange(field, from, to), StructuredFailure); else StructuredInvalid();
    }
    private async Task SplitPacketAsync(PacketFieldEdit field)
    {
        var document = doc;
        var first = StructuredTextInput("First part bit count", Math.Max(1, field.BitCount / 2).ToString(CultureInfo.InvariantCulture)); var label = StructuredTextInput("Second part label", field.Label + " 2");
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(first); fields.Children.Add(label);
        if (await AskAsync("Split packet field", fields, "Split") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        if (StructuredInteger(first, out int bits)) Edit(() => doc.SplitPacketField(field, bits, label.Text), StructuredFailure); else StructuredInvalid();
    }
    private async Task MovePacketAsync(PacketFieldEdit field)
    {
        var document = doc; var position = StructuredPicker("Final position", field.Diagram.Fields.Select((f, i) => $"{i + 1}: {f.Label}"), field.Index);
        if (await AskAsync("Reorder packet field", position, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document)) Edit(() => doc.MovePacketField(field, position.SelectedIndex), StructuredFailure);
    }
    private async Task InsertChartSeriesAsync(ChartDiagramEdit chart)
    {
        var document = doc;
        var kind = StructuredPicker("Series kind", chart.UsesCoordinates ? ["line", "scatter"] : ["bar", "line", "area", "scatter"], chart.UsesCoordinates ? 1 : 0);
        var format = StructuredPicker("Data format", ["Values", "Coordinates"], chart.UsesCoordinates ? 1 : 0);
        format.IsEnabled = chart.Series.Count == 0 && !chart.HasCategoryLabels && !chart.IsHorizontal;
        var label = StructuredTextInput("Series name", "New series");
        double value = Math.Clamp(0, chart.MinimumValue, chart.MaximumValue);
        int count = chart.HasCategoryLabels ? chart.Categories.Count : chart.Series.FirstOrDefault()?.Points.Count ?? 1;
        var data = StructuredTextInput(chart.UsesCoordinates ? "Coordinates (x, y); (x, y)" : "Values (comma separated)", chart.UsesCoordinates ? $"({StructuredNumberText(chart.MinimumX)}, {StructuredNumberText(value)})" : string.Join(", ", Enumerable.Repeat(StructuredNumberText(value), count)));
        var color = StructuredTextInput("Series color (optional)");
        var secondary = new CheckBox { Content = "Use secondary axis", IsChecked = false, IsEnabled = chart.HasSecondaryAxis };
        AutomationProperties.SetName(secondary, "Use secondary axis");
        var labels = new CheckBox { Content = "Show data labels", IsChecked = false }; AutomationProperties.SetName(labels, "Show data labels");
        var interpolate = new CheckBox { Content = "Interpolate line", IsEnabled = false }; AutomationProperties.SetName(interpolate, "Interpolate line");
        var connect = new CheckBox { Content = "Connect across missing values", IsEnabled = false }; AutomationProperties.SetName(connect, "Connect across missing values");
        kind.SelectionChanged += (_, _) => { bool line = (string?)kind.SelectedItem == "line"; interpolate.IsEnabled = connect.IsEnabled = line; if (!line) { interpolate.IsChecked = false; connect.IsChecked = false; } };
        format.SelectionChanged += (_, _) =>
        {
            bool coordinates = format.SelectedIndex == 1;
            kind.ItemsSource = coordinates ? new[] { "line", "scatter" } : new[] { "bar", "line", "area", "scatter" }; kind.SelectedIndex = 0;
            string name = coordinates ? "Coordinates (x, y); (x, y)" : "Values (comma separated)";
            data.Header = name; AutomationProperties.SetName(data, name);
            data.Text = coordinates ? $"({StructuredNumberText(chart.MinimumX)}, {StructuredNumberText(value)})" : string.Join(", ", Enumerable.Repeat(StructuredNumberText(value), count));
        };
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(format); fields.Children.Add(kind); fields.Children.Add(label); fields.Children.Add(data); fields.Children.Add(color);
        if (chart.HasSecondaryAxis) fields.Children.Add(secondary); fields.Children.Add(labels); fields.Children.Add(interpolate); fields.Children.Add(connect);
        if (await AskAsync("Insert chart series", StructuredScroll(fields), "Insert") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        if (format.SelectedIndex == 1)
        {
            var points = new List<ChartPoint>();
            foreach (string pair in data.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Trim().Trim('(', ')').Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) { StructuredInvalid(); return; }
                points.Add(new(x, y));
            }
            Edit(() => doc.InsertChartCoordinateSeries(chart, chart.Series.Count, (string)kind.SelectedItem, label.Text, points, string.IsNullOrWhiteSpace(color.Text) ? null : color.Text.Trim(), secondary.IsChecked == true, labels.IsChecked == true, interpolate.IsChecked == true, connect.IsChecked == true), StructuredFailure);
        }
        else
        {
            var values = new List<double?>();
            foreach (string part in data.Text.Split(',', StringSplitOptions.TrimEntries))
            { if (part.Length == 0 && (string?)kind.SelectedItem == "line") { values.Add(null); continue; } if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) { StructuredInvalid(); return; } values.Add(number); }
            Edit(() => doc.InsertChartIndexedSeries(chart, chart.Series.Count, (string)kind.SelectedItem, label.Text, values, string.IsNullOrWhiteSpace(color.Text) ? null : color.Text.Trim(), secondary.IsChecked == true, labels.IsChecked == true, interpolate.IsChecked == true, connect.IsChecked == true), StructuredFailure);
        }
    }
    private async Task InsertChartCategoryAsync(ChartDiagramEdit chart, int index)
    {
        var document = doc; var label = StructuredTextInput("Category label", "New category");
        var position = StructuredPicker("Insert position", chart.Categories.Select(c => "Before " + c).Append("After last category"), index);
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(label); fields.Children.Add(position);
        var values = chart.Series.Select(s => StructuredTextInput(s.Label.Length > 0 ? "Value for " + s.Label : $"Value for series {s.Index + 1}", StructuredNumberText(s.Points[0].Y))).ToArray();
        foreach (var input in values) fields.Children.Add(input);
        if (await AskAsync("Insert chart category", StructuredScroll(fields), "Insert") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        var numbers = new double[values.Length];
        for (int i = 0; i < numbers.Length; i++) if (!StructuredNumber(values[i], out numbers[i])) { StructuredInvalid(); return; }
        Edit(() => doc.InsertChartCategory(chart, position.SelectedIndex, label.Text, numbers), StructuredFailure);
    }
    private async Task MoveChartSeriesAsync(ChartSeriesEdit series)
    {
        var document = doc; var position = StructuredPicker("Final position", series.Diagram.Series.Select((s, i) => $"{i + 1}: {s.Label}"), series.Index);
        if (await AskAsync("Reorder chart series", position, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document)) Edit(() => doc.MoveChartSeries(series, position.SelectedIndex), StructuredFailure);
    }
    private async Task MoveChartCategoryAsync(ChartDiagramEdit chart, int index)
    {
        var document = doc; var position = StructuredPicker("Final position", chart.Categories.Select((c, i) => $"{i + 1}: {c}"), index);
        if (await AskAsync("Reorder chart category", position, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document)) Edit(() => doc.MoveChartCategory(chart, index, position.SelectedIndex), StructuredFailure);
    }
    private async Task ChooseChartPointAsync(ChartSeriesEdit series)
    {
        var document = doc; var point = StructuredPicker("Data point", series.Points.Select(p => $"{p.Index + 1}: " + (series.UsesCoordinates ? $"({StructuredNumberText(p.X)}, {StructuredNumberText(p.Y)})" : p.IsMissing ? "Missing" : StructuredNumberText(p.Y))), 0);
        if (await AskAsync("Choose chart data point", point, "Edit") == ContentDialogResult.Primary && StructuredStillCurrent(document)) await EditChartPointAsync(series.Points[point.SelectedIndex]);
    }
    private async Task EditChartPointAsync(ChartPointEdit point)
    {
        var document = doc; var x = StructuredTextInput("X value", StructuredNumberText(point.X)); var y = StructuredTextInput("Y value", StructuredNumberText(point.Y));
        var missing = new CheckBox { Content = "Missing value", IsChecked = point.IsMissing }; AutomationProperties.SetName(missing, "Missing value");
        var fields = new StackPanel { Spacing = 10 }; if (point.Series.UsesCoordinates) fields.Children.Add(x); fields.Children.Add(y);
        if (point.Series.Kind == "line" && !point.Series.UsesCoordinates)
        {
            fields.Children.Add(missing); y.IsEnabled = !point.IsMissing;
            missing.Checked += (_, _) => y.IsEnabled = false; missing.Unchecked += (_, _) => y.IsEnabled = true;
        }
        if (await AskAsync("Edit chart data point", fields, "Apply") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        if (missing.IsChecked == true) { Edit(() => doc.SetChartPoint(point, new(point.X, 0, true)), StructuredFailure); return; }
        if (StructuredNumber(x, out double nx) && StructuredNumber(y, out double ny)) Edit(() => doc.SetChartPoint(point, new(nx, ny, missing.IsChecked == true)), StructuredFailure); else StructuredInvalid();
    }
    private async Task InsertChartPointAsync(ChartSeriesEdit series, int index)
    {
        var document = doc; var x = StructuredTextInput("X value", StructuredNumberText(series.Points[^1].X)); var y = StructuredTextInput("Y value", StructuredNumberText(series.Points[^1].Y));
        var position = StructuredPicker("Insert position", series.Points.Select(p => "Before point " + (p.Index + 1)).Append("After last point"), index);
        var fields = new StackPanel { Spacing = 10 }; if (series.UsesCoordinates) fields.Children.Add(x); fields.Children.Add(y); fields.Children.Add(position);
        var missing = new CheckBox { Content = "Missing value" }; AutomationProperties.SetName(missing, "Missing value");
        if (series.Kind == "line" && !series.UsesCoordinates)
        {
            fields.Children.Add(missing); missing.Checked += (_, _) => y.IsEnabled = false; missing.Unchecked += (_, _) => y.IsEnabled = true;
        }
        if (await AskAsync("Insert chart data point", fields, "Insert") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        if (missing.IsChecked == true) { Edit(() => doc.InsertChartPoint(series, position.SelectedIndex, new(0, 0, true)), StructuredFailure); return; }
        if (StructuredNumber(x, out double nx) && StructuredNumber(y, out double ny)) Edit(() => doc.InsertChartPoint(series, position.SelectedIndex, new(nx, ny)), StructuredFailure); else StructuredInvalid();
    }
    private async Task EditChartLineOptionsAsync(ChartSeriesEdit series)
    {
        if (series.Kind != "line") { Status("Select a line series to change interpolation and gap handling."); return; }
        var document = doc;
        var interpolate = new CheckBox { Content = "Interpolate line", IsChecked = series.Interpolate }; AutomationProperties.SetName(interpolate, "Interpolate line");
        var connect = new CheckBox { Content = "Connect across missing values", IsChecked = series.ConnectMissingValues }; AutomationProperties.SetName(connect, "Connect across missing values");
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(interpolate); fields.Children.Add(connect);
        fields.Children.Add(Caption("Interpolation draws a natural cubic spline through available points. Repeated or decreasing X values use ordinary segments. Empty indexed values create gaps unless connection or interpolation joins them."));
        if (await AskAsync("Line interpolation and gaps", fields, "Apply") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.SetChartLineOptions(series, interpolate.IsChecked == true, connect.IsChecked == true), StructuredFailure);
    }
    private async Task MoveChartPointAsync(ChartPointEdit point)
    {
        var document = doc; var position = StructuredPicker("Final position", point.Series.Points.Select(p => "Point " + (p.Index + 1)), point.Index);
        if (await AskAsync("Reorder chart data point", position, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document)) Edit(() => doc.MoveChartPoint(point, position.SelectedIndex), StructuredFailure);
    }
    private static ScrollViewer StructuredScroll(UIElement content) => new()
    { Content = content, MaxHeight = 540, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private async Task EditChartSettingsAsync(ChartDiagramEdit chart)
    {
        var document = doc; var settings = chart.Settings;
        var title = StructuredTextInput("Chart title", settings.Title);
        var legend = StructuredPicker("Legend position", ["None", "Left", "Right", "Top", "Bottom"], (int)settings.Legend);
        var orientation = StructuredPicker("Chart orientation", ["Default (vertical)", "Vertical", "Horizontal"], settings.Orientation is { } direction ? (int)direction + 1 : 0);
        var stack = StructuredPicker("Bar stacking", ["Default (grouped)", "Grouped", "Stacked"], settings.StackMode is { } mode ? (int)mode + 1 : 0);
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(title); fields.Children.Add(legend); fields.Children.Add(orientation); fields.Children.Add(stack);
        fields.Children.Add(Caption("Default removes the explicit setting. Changing orientation also exchanges the horizontal and vertical axes. The complete chart must remain valid."));
        if (await AskAsync("Chart settings", fields, "Apply") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        Edit(() => doc.ApplyChartSettings(chart, new(title.Text, (ChartLegend)legend.SelectedIndex,
            orientation.SelectedIndex == 0 ? null : (ChartOrientation)(orientation.SelectedIndex - 1), stack.SelectedIndex == 0 ? null : (ChartStackMode)(stack.SelectedIndex - 1))), StructuredFailure);
    }

    private async Task ManageChartAxesAsync(ChartDiagramEdit chart)
    {
        var document = doc;
        var selection = StructuredPicker("Axis", ["Horizontal (h-axis)", "Vertical (v-axis)", "Secondary (v2-axis)"], 0);
        var operation = StructuredPicker("Axis operation", ["Define or update", "Remove explicit axis"], 0);
        var label = StructuredTextInput("Axis label"); var mode = StructuredPicker("Axis data", ["Numeric range", "Categories"], 0);
        var categories = StructuredTextInput("Categories (comma separated)");
        var minimum = StructuredTextInput("Axis minimum"); var maximum = StructuredTextInput("Axis maximum");
        var spacing = StructuredTextInput("Tick spacing (blank for automatic)");
        var grid = new CheckBox { Content = "Show grid" }; AutomationProperties.SetName(grid, "Show grid");
        var labelPosition = StructuredPicker("Axis label position", ["Default", "Top", "Right"], 0);
        var ticks = StructuredTextInput("Named ticks (value: label per line)"); ticks.AcceptsReturn = true; ticks.MinHeight = 80;
        var reassign = new CheckBox { Content = "Move secondary series to the primary axis" }; AutomationProperties.SetName(reassign, "Move secondary series to the primary axis");
        var description = Caption("");
        var definition = new StackPanel { Spacing = 10 };
        foreach (UIElement input in new UIElement[] { label, mode, categories, minimum, maximum, spacing, grid, labelPosition, ticks }) definition.Children.Add(input);
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(selection); fields.Children.Add(operation); fields.Children.Add(description); fields.Children.Add(definition); fields.Children.Add(reassign);
        fields.Children.Add(Caption("Changing category count requires the category controls, which update every series. Removing an axis restores automatic values; dependent annotations and series must still fit."));
        void UpdateVisibility()
        {
            bool categorical = mode.SelectedIndex == 1, remove = operation.SelectedIndex == 1;
            categories.Visibility = categorical ? Visibility.Visible : Visibility.Collapsed;
            minimum.Visibility = maximum.Visibility = ticks.Visibility = categorical ? Visibility.Collapsed : Visibility.Visible;
            definition.Visibility = remove ? Visibility.Collapsed : Visibility.Visible;
            reassign.Visibility = remove && selection.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }
        void LoadAxis()
        {
            var axis = selection.SelectedIndex switch { 0 => chart.HorizontalAxis, 1 => chart.VerticalAxis, _ => chart.SecondaryAxis };
            var value = axis?.Definition ?? new ChartAxisDefinition { Minimum = chart.MinimumValue, Maximum = chart.MaximumValue };
            label.Text = value.Label; mode.SelectedIndex = value.Categories == null ? 0 : 1;
            categories.Text = string.Join(", ", value.Categories ?? chart.Categories);
            minimum.Text = StructuredNumberText(value.Minimum); maximum.Text = StructuredNumberText(value.Maximum);
            spacing.Text = value.Spacing is { } step ? StructuredNumberText(step) : "";
            grid.IsChecked = value.Grid; labelPosition.SelectedIndex = (int)value.LabelPosition;
            ticks.Text = string.Join("\n", value.Ticks.Select(t => StructuredNumberText(t.Value) + ": " + t.Label));
            description.Text = axis?.IsExplicit == true ? "This axis is declared in the source." : "This axis is currently automatic or absent. Applying these values creates its declaration.";
            operation.SelectedIndex = 0; UpdateVisibility();
        }
        selection.SelectionChanged += (_, _) => LoadAxis(); mode.SelectionChanged += (_, _) => UpdateVisibility(); operation.SelectionChanged += (_, _) => UpdateVisibility(); LoadAxis();
        if (await AskAsync("Chart axes", StructuredScroll(fields), "Apply") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        var kind = (ChartAxisKind)selection.SelectedIndex;
        if (operation.SelectedIndex == 1) { Edit(() => doc.RemoveChartAxis(chart, kind, reassign.IsChecked == true), StructuredFailure); return; }
        bool categorical = mode.SelectedIndex == 1;
        double min = 0, max = 100; double? stepValue = null;
        if (!categorical && (!StructuredNumber(minimum, out min) || !StructuredNumber(maximum, out max))) { StructuredInvalid(); return; }
        if (!string.IsNullOrWhiteSpace(spacing.Text)) { if (!StructuredNumber(spacing, out double step)) { StructuredInvalid(); return; } stepValue = step; }
        var namedTicks = new List<ChartTick>();
        if (!categorical)
        foreach (string row in ticks.Text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = row.IndexOf(':');
            if (colon < 0 || !double.TryParse(row[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out double at)) { StructuredInvalid(); return; }
            namedTicks.Add(new(at, row[(colon + 1)..].Trim()));
        }
        var changed = new ChartAxisDefinition { Label = label.Text, Categories = categorical ? categories.Text.Split(',', StringSplitOptions.TrimEntries) : null,
            Minimum = min, Maximum = max, Spacing = stepValue, Grid = grid.IsChecked == true, LabelPosition = (ChartAxisLabelPosition)labelPosition.SelectedIndex, Ticks = namedTicks };
        Edit(() => doc.SetChartAxis(chart, kind, changed), StructuredFailure);
    }

    private async Task ManageChartAnnotationsAsync(ChartDiagramEdit chart, int index)
    {
        var document = doc;
        var selection = StructuredPicker("Annotation", chart.Annotations.Select((a, i) => $"{i + 1}: {a.Label}").Append("New annotation"), index);
        var operation = StructuredPicker("Annotation operation", ["Create or update", "Remove"], 0);
        var label = StructuredTextInput("Annotation text");
        var category = StructuredPicker("Annotation category", chart.Categories, 0);
        var x = StructuredTextInput("Annotation X"); var y = StructuredTextInput("Annotation Y");
        var arrow = new CheckBox { Content = "Show annotation arrow" }; AutomationProperties.SetName(arrow, "Show annotation arrow");
        bool categorical = chart.HorizontalAxis.Definition.Categories != null || chart.IsHorizontal;
        var fields = new StackPanel { Spacing = 10 }; fields.Children.Add(selection); fields.Children.Add(operation); fields.Children.Add(label);
        fields.Children.Add(categorical ? category : x); fields.Children.Add(y); fields.Children.Add(arrow);
        void LoadAnnotation()
        {
            var selected = selection.SelectedIndex < chart.Annotations.Count ? chart.Annotations[selection.SelectedIndex] : null;
            label.Text = selected?.Label ?? "Annotation";
            x.Text = selected?.X ?? StructuredNumberText(chart.MinimumX); y.Text = StructuredNumberText(selected?.Y ?? Math.Clamp(0, chart.MinimumValue, chart.MaximumValue));
            category.SelectedIndex = selected == null ? 0 : chart.Categories.ToList().IndexOf(selected.X);
            arrow.IsChecked = selected?.Arrow ?? false; operation.SelectedIndex = 0;
            operation.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
        }
        selection.SelectionChanged += (_, _) => LoadAnnotation(); LoadAnnotation();
        if (await AskAsync("Chart annotations", StructuredScroll(fields), "Apply") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        var annotation = selection.SelectedIndex < chart.Annotations.Count ? chart.Annotations[selection.SelectedIndex] : null;
        if (operation.SelectedIndex == 1 && annotation != null) { Edit(() => doc.RemoveChartAnnotation(annotation), StructuredFailure); return; }
        if (!StructuredNumber(y, out double value) || categorical && category.SelectedIndex < 0) { StructuredInvalid(); return; }
        string position = categorical ? chart.Categories[category.SelectedIndex] : x.Text.Trim();
        Edit(() => annotation == null ? doc.InsertChartAnnotation(chart, chart.Annotations.Count, label.Text, position, value, arrow.IsChecked == true) :
            doc.UpdateChartAnnotation(annotation, label.Text, position, value, arrow.IsChecked == true), StructuredFailure);
    }

}
