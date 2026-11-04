using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private Button arrangeButton = null!;
    private long canvasMergeState, lastCanvasRenderHistory;
    private string lastCanvasSemanticSource = "";
    private (double X, double Y, string[] Existing, long History)? pendingCanvasPlacement;

    private void CommitCanvasLayout(int expectedRevision, string json, bool merge)
    {
        void Restore()
        {
            doc.TryGetCanvasLayout(out var prior, out _);
            viewport.SetCanvasLayout(prior, revision);
            viewport.CompleteCanvasLayoutCommit(false);
        }
        if (resourceHistoryBusy || expectedRevision != revision || renderedRevision != revision)
        {
            Restore();
            Status("The source changed during this gesture. The current diagram layout was restored.", true);
            return;
        }
        CanvasLayoutState? layout = null;
        if (json != "null" && !CanvasLayoutJson.TryParse(json, out layout, out var parseError))
        {
            Restore();
            Status(parseError ?? "Invalid canvas geometry.", true);
            return;
        }
        string semanticSource = doc.Page.Source;
        bool changed = merge && canvasMergeState != 0 && doc.HistoryState == canvasMergeState ?
            doc.TrySetCanvasLayout(layout, canvasMergeState, out var error) : doc.TrySetCanvasLayout(layout, out error);
        if (!changed)
        {
            Restore();
            if (error != null) Status(error, true);
            return;
        }
        DiscardResourceRedo();
        canvasMergeState = 0;
        bool refreshSourceMap = semanticSource != doc.Page.Source;
        revision++; renderedRevision = refreshSourceMap ? -1 : revision; dirty = true;
        changeTimer.Stop();
        // The comment precedes @start: the rendered semantic source and its hit
        // offsets are unchanged. JS replays its selection against the new index.
        selected = null; additionalSelection.Clear();
        selectedGenerated = null; selectedJsonField = null; selectedSourceField = null; selectedDot = null;
        Refresh();
        viewport.SetCanvasLayout(layout, revision);
        viewport.CompleteCanvasLayoutCommit(true);
        // Moving metadata ahead of a leading BOM changes page-relative token
        // offsets. Rebuild those hits before accepting another source edit.
        if (refreshSourceMap) _ = RenderPageAsync();
    }

    private void DropCanvasTool(CanvasToolDrop drop)
    {
        if (drop.Revision != revision || resourceHistoryBusy || !double.IsFinite(drop.X) || !double.IsFinite(drop.Y)) return;
        const string prefix = "unouml:tool:";
        if (!drop.Text.StartsWith(prefix, StringComparison.Ordinal)) return;
        var entry = StudioCatalog.ForPage(doc.Page).FirstOrDefault(t => t.Group + "/" + t.Name == drop.Text[prefix.Length..]);
        if (entry == null) return;
        var existing = viewport.GetCanvasInfo()?.State.Nodes.Select(n => n.Id).ToArray() ?? [];
        if (!doc.Add(entry)) return;
        pendingCanvasPlacement = (drop.X, drop.Y, existing, doc.HistoryState);
        if (lastWidth < 620) { libraryOpen = false; ArrangePanels(); }
        Changed();
    }

    private void CanvasRendered(string source)
    {
        canvasMergeState = source != lastCanvasSemanticSource && doc.HistoryState != lastCanvasRenderHistory ? doc.HistoryState : 0;
        lastCanvasRenderHistory = doc.HistoryState; lastCanvasSemanticSource = source;
        ApplyCanvasLayout();
        if (pendingCanvasPlacement is not { } placement) return;
        pendingCanvasPlacement = null;
        if (doc.HistoryState != placement.History) return;
        viewport.ArrangeCanvas("place", CanvasOptions(w =>
        {
            w.WriteNumber("x", placement.X); w.WriteNumber("y", placement.Y); w.WriteBoolean("merge", true);
            w.WriteStartArray("existing"); foreach (var id in placement.Existing) w.WriteStringValue(id); w.WriteEndArray();
        }));
    }

    private void ApplyCanvasLayout()
    {
        doc.TryGetCanvasLayout(out var layout, out var error);
        viewport.SetCanvasLayout(layout, revision);
        if (error != null) Status(error, true);
    }

    private static string CanvasOptions(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void CanvasAction(string action, string? value = null)
    {
        if (renderedRevision != revision || resourceHistoryBusy) { Status("Wait for the current diagram to finish rendering."); return; }
        viewport.ArrangeCanvas(action, value == null ? "{}" : CanvasOptions(w => w.WriteString("value", value)));
    }

    private void ShowArrangeMenu()
    {
        var menu = new MenuFlyout();
        MenuAction(menu, "Automatic layout…", () => _ = ShowCanvasLayoutAsync());
        MenuAction(menu, "Exact position, size and rotation…", () => _ = ShowCanvasBoundsAsync());
        void Group(string title, string action, params (string Title, string Value)[] options)
        {
            var group = new MenuFlyoutSubItem { Text = title };
            foreach (var option in options)
            {
                var item = new MenuFlyoutItem { Text = option.Title };
                item.Click += (_, _) => CanvasAction(action, option.Value);
                group.Items.Add(item);
            }
            menu.Items.Add(group);
        }
        Group("Align selection", "align", ("Left edges", "left"), ("Horizontal centers", "center"), ("Right edges", "right"),
            ("Top edges", "top"), ("Vertical centers", "middle"), ("Bottom edges", "bottom"));
        Group("Distribute selection", "distribute", ("Equal horizontal gaps", "horizontal"), ("Equal vertical gaps", "vertical"));
        Group("Match size", "matchSize", ("Width", "width"), ("Height", "height"), ("Width and height", "both"));
        Group("Connector routing", "routing", ("Orthogonal", "orthogonal"), ("Straight", "straight"), ("Rounded", "curved"), ("Polyline", "polyline"));
        MenuAction(menu, "Reroute connections / reset bends", () => CanvasAction("route"));
        menu.Items.Add(new MenuFlyoutSeparator());
        MenuAction(menu, "Bring to front", () => CanvasAction("front"));
        MenuAction(menu, "Send to back", () => CanvasAction("back"));
        MenuAction(menu, "Pin selection for automatic layout", () => CanvasAction("pin"));
        MenuAction(menu, "Unpin selection", () => CanvasAction("unpin"));
        MenuAction(menu, "Lock selection", () => CanvasAction("lock"));
        MenuAction(menu, "Unlock selection", () => CanvasAction("unlock"));
        MenuAction(menu, "Collapse selected containers", () => CanvasAction("collapse"));
        MenuAction(menu, "Expand selected containers", () => CanvasAction("expand"));
        menu.Items.Add(new MenuFlyoutSeparator());
        MenuAction(menu, "Select all shapes", () => CanvasAction("selectAll"));
        MenuAction(menu, "Restore renderer layout", () => CanvasAction("reset"));
        menu.ShowAt(arrangeButton);
    }

    private void AddCanvasInspectorActions()
    {
        if (renderedRevision != revision || viewport.GetCanvasInfo() is not { State.Nodes.Count: > 0 }) return;
        Properties.Children.Add(Caption("CANVAS GEOMETRY"));
        Properties.Children.Add(Item("Automatic layout…", "layers", () => _ = ShowCanvasLayoutAsync()));
        Properties.Children.Add(Item("Exact position and size…", "move", () => _ = ShowCanvasBoundsAsync()));
        Properties.Children.Add(Item("Arrange, route, pin or lock…", "more", ShowArrangeMenu));
        Properties.Children.Add(Caption("Drag to move. Handles resize and rotate. Arrow keys nudge; Shift-arrow moves a grid step. Alt bypasses snapping. Alt-double-click a connector inserts a bend."));
    }

    private async Task ShowCanvasLayoutAsync()
    {
        if (viewport.GetCanvasInfo() is not { State.Nodes.Count: > 0 } info) { Status("Open a structural graph to arrange shapes."); return; }
        int expected = revision;
        var current = info.State.Settings;
        var algorithm = new ComboBox { Header = "Layout algorithm", ItemsSource = new[] { "Layered flow", "Tree", "Radial tree", "Circle", "Grid", "Force directed" }, SelectedIndex = (int)current.Algorithm, HorizontalAlignment = HorizontalAlignment.Stretch };
        var direction = new ComboBox { Header = "Flow direction", ItemsSource = new[] { "Down", "Right", "Up", "Left" }, SelectedIndex = (int)current.Direction, HorizontalAlignment = HorizontalAlignment.Stretch };
        var scope = new ComboBox { Header = "Layout scope", ItemsSource = new[] { "Entire diagram", "Selected shapes (keep other shapes fixed)" }, SelectedIndex = info.Nodes.Length > 0 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        TextBox Number(string title, double value) => new() { Header = title, Text = value.ToString("0.###", CultureInfo.InvariantCulture) };
        var spacing = Number("Space between shapes", current.NodeSpacing);
        var layers = Number("Space between layers", current.LayerSpacing);
        var grid = Number("Grid spacing", current.GridSize);
        var columns = Number("Grid columns (0 = automatic)", current.GridColumns);
        var snapGrid = new CheckBox { Content = "Snap gestures to grid", IsChecked = current.SnapToGrid };
        var snapObjects = new CheckBox { Content = "Snap to shape edges and centers", IsChecked = current.SnapToObjects };
        var automatic = new CheckBox { Content = "Automatically place new source objects", IsChecked = current.Automatic };
        var applyNow = new CheckBox { Content = "Apply layout now", IsChecked = true };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Caption("Pinned and locked shapes stay fixed. A selection layout preserves the rest of the diagram; groups move with their children."));
        foreach (var item in new UIElement[] { algorithm, direction, scope, spacing, layers, grid, columns, snapGrid, snapObjects, automatic, applyNow }) content.Children.Add(item);
        var scroll = new ScrollViewer { Content = content, MaxHeight = 470, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (await AskAsync("Automatic layout", scroll, "Apply") != ContentDialogResult.Primary) return;
        if (expected != revision) { Status("The document changed while this dialog was open. Reopen layout settings.", true); return; }
        bool Read(TextBox field, double min, double max, out double number) => double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && number >= min && number <= max;
        if (!Read(spacing, 0, 10000, out double ns) || !Read(layers, 0, 10000, out double ls) || !Read(grid, 1, 10000, out double gs) ||
            !Read(columns, 0, 2048, out double cols) || cols != Math.Truncate(cols))
        { Status("Enter finite nonnegative spacing values, a grid of at least 1, and a whole column count.", true); return; }
        string[] names = ["layered", "tree", "radial", "circle", "grid", "forceDirected"];
        string[] directions = ["down", "right", "up", "left"];
        string options = CanvasOptions(w =>
        {
            w.WriteBoolean("all", scope.SelectedIndex == 0);
            if (scope.SelectedIndex == 1) { w.WriteStartArray("selection"); foreach (var id in info.Nodes) w.WriteStringValue(id); w.WriteEndArray(); }
            w.WriteStartObject("settings");
            w.WriteString("algorithm", names[algorithm.SelectedIndex]); w.WriteString("direction", directions[direction.SelectedIndex]);
            w.WriteNumber("nodeSpacing", ns); w.WriteNumber("layerSpacing", ls); w.WriteNumber("gridSize", gs); w.WriteNumber("gridColumns", (int)cols);
            w.WriteBoolean("snapToGrid", snapGrid.IsChecked == true); w.WriteBoolean("snapToObjects", snapObjects.IsChecked == true);
            w.WriteBoolean("automatic", automatic.IsChecked == true); w.WriteEndObject();
        });
        viewport.ArrangeCanvas(applyNow.IsChecked == true ? "layout" : "settings", options);
    }

    private async Task ShowCanvasBoundsAsync()
    {
        if (viewport.GetCanvasInfo() is not { Nodes.Length: 1 } info) { Status("Select one shape to edit its exact bounds."); return; }
        var node = info.State.Nodes.Single(n => n.Id == info.Nodes[0]);
        if (node.Locked) { Status("Unlock the shape before changing its geometry."); return; }
        int expected = revision;
        TextBox Number(string label, double value) => new() { Header = label, Text = value.ToString("0.###", CultureInfo.InvariantCulture) };
        var x = Number("X position", node.X); var y = Number("Y position", node.Y);
        var width = Number("Width", node.Width); var height = Number("Height", node.Height); var rotation = Number("Rotation in degrees", node.Rotation);
        var content = new StackPanel { Spacing = 10 };
        foreach (var field in new[] { x, y, width, height, rotation }) content.Children.Add(field);
        if (await AskAsync("Exact canvas geometry", content, "Apply") != ContentDialogResult.Primary) return;
        if (expected != revision) { Status("The diagram changed while this dialog was open.", true); return; }
        var fields = new[] { x, y, width, height, rotation }; var values = new double[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            if (!double.TryParse(fields[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
            { Status("Enter finite numeric geometry values.", true); return; }
        viewport.ArrangeCanvas("bounds", CanvasOptions(w =>
        {
            w.WriteStartArray("selection"); w.WriteStringValue(node.Id); w.WriteEndArray();
            w.WriteStartObject("bounds"); string[] names = ["x", "y", "width", "height", "rotation"];
            for (int i = 0; i < names.Length; i++) w.WriteNumber(names[i], values[i]); w.WriteEndObject();
        }));
    }
}
