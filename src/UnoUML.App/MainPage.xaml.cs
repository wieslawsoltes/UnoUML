using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UnoUML.App.Rendering;
using UnoUML.Core;
using Windows.System;

namespace UnoUML.App;

public sealed partial class MainPage : Page
{
    const string DraftKey = "unouml.document.v1";
    const string LibraryKey = "unouml.snippets.v1";
    StudioDocument doc = new(StudioCatalog.Templates[0].Source);
    string fileName = "Checkout flow.puml";
    readonly DiagramViewport viewport = new();
    readonly DispatcherTimer changeTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    readonly Dictionary<string, Button> modes = [], tools = [], tabs = [];
    readonly List<LibrarySnippet> snippets = [];
    readonly List<Button> fileActions = [];
    readonly TextBlock zoomLabel = new() { Text = "100%", FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, MinWidth = 36, TextAlignment = TextAlignment.Center };
    Button undoButton = null!, redoButton = null!, libraryToggle = null!, inspectorToggle = null!, exportButton = null!;
    DiagramObject? selected;
    string mode = "Design", tool = "select", tab = "Objects", lastSource = "";
    int revision, renderedRevision = -1, outlineLimit = 40;
    bool ready, syncing, libraryOpen, inspectorOpen, dirty;
    bool draftSaved = true, fitAfterRender = true;
    double lastWidth = -1;

    public MainPage()
    {
        InitializeComponent();
        BrowserAccessibility.Attach(SourceEditor);
        BrowserAccessibility.Attach(SearchBox);
        BrowserAccessibility.Attach(PagePicker);
        BuildCommands();
        ViewportHost.Children.Add(viewport);
        SourceEditor.SelectionChanged += (_, _) => OnSourceSelection();
        viewport.LabelCommitted += CommitCanvasLabel;
        viewport.RegionSelected += SelectCanvasRegion;
        viewport.CanvasCommand += OnCanvasCommand;
        viewport.Hit += (_, hit) => OnHit(hit);
        viewport.Gesture += (_, gesture) => OnGesture(gesture);
        viewport.DropSnippet += (_, payload) => DropTool(payload);
        KeyDown += OnEditorKey;
        viewport.ZoomChanged += (_, zoom) => zoomLabel.Text = $"{zoom:P0}";
        SizeChanged += (_, e) => ArrangeWorkspace(e.NewSize.Width);
        changeTimer.Tick += (_, _) => { changeTimer.Stop(); Refresh(); _ = RenderPageAsync(); };
        Loaded += OnLoaded;
        Unloaded += (_, _) => { changeTimer.Stop(); SaveDraft(); };
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ready)
        {
            RestoreDraft();
            ready = true;
        }
        ArrangeWorkspace(ActualWidth);
        Refresh();
        _ = RenderPageAsync();
    }

    void BuildCommands()
    {
        void File(string name, string icon, Action handler, bool label = false)
        {
            var b = Command(name, icon, handler, label);
            AppCommands.Children.Add(b); fileActions.Add(b);
        }
        File("New", "new", () => _ = NewAsync());
        File("Open", "open", () => _ = OpenAsync(), true);
        File("Save", "save", SaveFile, true);
        exportButton = Command("Export", "export", () => ShowExportMenu(), true, true);
        AppCommands.Children.Add(exportButton);

        libraryToggle = Command("Toggle library", "side", () => TogglePanel(true));
        inspectorToggle = Command("Toggle inspector", "inspector", () => TogglePanel(false));
        PanelToggleActions.Children.Add(libraryToggle);
        PanelToggleActions.Children.Add(inspectorToggle);
        foreach (var name in new[] { "Design", "Split", "Source" })
        {
            var b = Tab(name, () => SetMode(name)); modes.Add(name, b); ViewTabs.Children.Add(b);
        }
        undoButton = Command("Undo", "undo", () => History(false)); HistoryActions.Children.Add(undoButton);
        redoButton = Command("Redo", "redo", () => History(true)); HistoryActions.Children.Add(redoButton);
        foreach (var (key, name) in new[] { ("select","Select"),("pan","Pan"),("connect","Connect two objects"),("move","Move or reorder objects"),("marquee","Box select") })
        {
            var b = Command(name, key, () => SetTool(key)); tools.Add(key, b); CanvasTools.Children.Add(b);
        }
        ZoomActions.Children.Add(Command("Zoom out", "minus", () => viewport.ZoomBy(1 / 1.2)));
        ZoomActions.Children.Add(zoomLabel);
        ZoomActions.Children.Add(Command("Zoom in", "plus", () => viewport.ZoomBy(1.2)));
        ZoomActions.Children.Add(Command("Fit diagram", "fit", viewport.Fit));

        foreach (var name in new[] { "Objects", "Diagrams", "Saved" })
        {
            var b = Tab(name, () => { tab = name; SearchBox.Text = ""; ShowLibrary(); });
            tabs.Add(name, b); LibraryTabs.Children.Add(b);
        }
        var manage = Command("Library actions", "more", () => ShowLibraryMenu());
        LibraryActions.Children.Add(manage);
        Shortcut(VirtualKey.S, VirtualKeyModifiers.Control, SaveFile);
        Shortcut(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = OpenAsync());
        Shortcut(VirtualKey.Z, VirtualKeyModifiers.Control, () => History(false));
        Shortcut(VirtualKey.Y, VirtualKeyModifiers.Control, () => History(true));
        Shortcut(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => History(true));
    }

    void Shortcut(VirtualKey key, VirtualKeyModifiers mods, Action action)
    {
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = mods };
        shortcut.Invoked += (_, e) => { e.Handled = true; action(); };
        KeyboardAccelerators.Add(shortcut);
    }

    void OnEditorKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || selected == null) return;
        for (var element = e.OriginalSource as DependencyObject; element != null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            if (element is TextBox or ComboBox) return;
        var item = selected;
        switch (e.Key)
        {
            case VirtualKey.F2: BeginCanvasEdit(item); break;
            case VirtualKey.Delete: OnCanvasCommand("delete", ""); break;
            case VirtualKey.Escape: additionalSelection.Clear(); selected = null; selectedGenerated = null; selectedJsonField = null; selectedSourceField = null; selectedDot = null; ShowInspector(); SyncCanvasSelection(); break;
            default: return;
        }
        e.Handled = true;
    }

    void ArrangeWorkspace(double width)
    {
        if (width <= 0) return;
        if (lastWidth < 0 || (lastWidth >= 950) != (width >= 950)) { inspectorOpen = width >= 950; }
        if (lastWidth < 0 || (lastWidth >= 710) != (width >= 710)) { libraryOpen = width >= 710; }
        lastWidth = width;
        FileCrumb.Visibility = width >= 780 ? Visibility.Visible : Visibility.Collapsed;
        BrandName.Visibility = width >= 370 ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.MaxWidth = Math.Min(245, Math.Max(0, width - 770));
        foreach (var b in fileActions)
        {
            if (b.Content is StackPanel { Children.Count: > 1 } row) row.Children[1].Visibility = width >= 580 ? Visibility.Visible : Visibility.Collapsed;
        }
        ArrangePanels();
        ApplyMode();
    }

    void TogglePanel(bool library)
    {
        if (library) { libraryOpen = !libraryOpen; if (lastWidth < 950 && libraryOpen) inspectorOpen = false; }
        else { inspectorOpen = !inspectorOpen; if (lastWidth < 950 && inspectorOpen) libraryOpen = false; }
        ArrangePanels(); ApplyMode();
    }

    void ArrangePanels()
    {
        // At phone widths a dock becomes the entire body. Keep its toggle reachable in the header.
        bool compact = lastWidth < 620;
        if (compact)
        {
            LibraryColumn.Width = new GridLength(0); InspectorColumn.Width = new GridLength(0);
            Grid.SetColumn(LibraryPanel, 1); Grid.SetColumn(InspectorPanel, 1);
            Canvas.SetZIndex(LibraryPanel, 3); Canvas.SetZIndex(InspectorPanel, 3);
            LibraryPanel.Margin = InspectorPanel.Margin = new Thickness(0, 54, 0, 0);
        }
        else
        {
            Grid.SetColumn(LibraryPanel, 0); Grid.SetColumn(InspectorPanel, 2);
            LibraryPanel.Margin = InspectorPanel.Margin = new Thickness(0);
            LibraryColumn.Width = new GridLength(libraryOpen ? (lastWidth < 1080 ? 194 : 226) : 0);
            InspectorColumn.Width = new GridLength(inspectorOpen ? (lastWidth < 1080 ? 210 : 236) : 0);
        }
        LibraryPanel.Visibility = libraryOpen ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = inspectorOpen ? Visibility.Visible : Visibility.Collapsed;
        Highlight(libraryToggle, libraryOpen); Highlight(inspectorToggle, inspectorOpen);
        HistoryActions.Visibility = lastWidth < 390 ? Visibility.Collapsed : Visibility.Visible;
        DocumentStats.Visibility = lastWidth < 560 ? Visibility.Collapsed : Visibility.Visible;
    }

    void SetMode(string value)
    {
        mode = value; ApplyMode();
        if (value != "Source" && ready) _ = RenderPageAsync();
    }

    void ApplyMode()
    {
        var star = new GridLength(1, GridUnitType.Star);
        var zero = new GridLength(0);
        double centerWidth = lastWidth - LibraryColumn.Width.Value - InspectorColumn.Width.Value;
        bool vertical = centerWidth < 660;
        bool split = mode == "Split";
        EditorArea.ColumnDefinitions[0].Width = star;
        EditorArea.ColumnDefinitions[1].Width = split && !vertical ? star : zero;
        EditorArea.RowDefinitions[0].Height = star;
        EditorArea.RowDefinitions[1].Height = split && vertical ? star : zero;
        Grid.SetColumn(SourcePanel, split && !vertical ? 1 : 0);
        Grid.SetRow(SourcePanel, split && vertical ? 1 : 0);
        DrawingArea.Visibility = mode == "Source" ? Visibility.Collapsed : Visibility.Visible;
        SourcePanel.Visibility = mode == "Design" ? Visibility.Collapsed : Visibility.Visible;
        CanvasTools.Visibility = mode == "Source" ? Visibility.Collapsed : Visibility.Visible;
        ZoomActions.Visibility = CanvasTools.Visibility;
        if (centerWidth < 390 && CanvasTools.Children.Count > 2)
        {
            CanvasTools.Children[1].Visibility = CanvasTools.Children[3].Visibility = Visibility.Collapsed;
        }
        else if (CanvasTools.Children.Count > 2)
            CanvasTools.Children[1].Visibility = CanvasTools.Children[3].Visibility = Visibility.Visible;
        foreach (var p in modes) Highlight(p.Value, p.Key == mode);
    }

    void SetTool(string value)
    {
        tool = value; viewport.SetTool(value);
        foreach (var p in tools) Highlight(p.Value, p.Key == tool);
        if (value == "connect") Status("Drag between two objects or click the source and target.");
        if (value == "marquee") Status("Drag a box around objects. Hold Shift to add to the selection.");
        if (value == "move") Status("Drop beside an object to guide graph layout, or onto a sibling to reorder its declaration.");
    }

    void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || syncing) return;
        if (SourceEditor.Text == doc.Source) return;
        doc.SetSource(SourceEditor.Text);
        DiscardResourceRedo();
        revision++; dirty = true; additionalSelection.Clear(); selected = null;
        undoButton.IsEnabled = doc.CanUndo || resourceCursor > 0; redoButton.IsEnabled = doc.CanRedo || resourceCursor < resourceHistory.Count;
        Status("Editing source…");
        changeTimer.Stop(); changeTimer.Start();
    }

    void OnSourceSelection()
    {
        if (!ready || syncing || mode == "Design" || SourceEditor.FocusState == FocusState.Unfocused ||
            changeTimer.IsEnabled || SourceEditor.Text != doc.Source) return;
        var item = doc.FindAtOffset(SourceEditor.SelectionStart, out int page);
        if (page < 0) return;
        if (page != doc.ActivePage)
        {
            fitAfterRender = true; doc.ActivePage = page; selected = item; revision++;
            Refresh(); _ = RenderPageAsync();
        }
        else if (selected != item)
        {
            selectedJsonField = null; selectedSourceField = null; selectedDot = null; additionalSelection.Clear(); selected = item; ShowInspector(); SyncCanvasSelection();
        }
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e) { if (ready) ShowLibrary(); }

    void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || syncing || PagePicker.SelectedIndex < 0) return;
        if (doc.ActivePage == PagePicker.SelectedIndex) return;
        fitAfterRender = true; doc.ActivePage = PagePicker.SelectedIndex; additionalSelection.Clear(); selected = null; revision++;
        outlineLimit = 40; Refresh(); _ = RenderPageAsync();
    }

    void Refresh()
    {
        PruneResourceHistory();
        if (!ready) return;
        syncing = true;
        try
        {
            if (SourceEditor.Text != doc.Source) SourceEditor.Text = doc.Source;
            if (lastSource != doc.Source || PagePicker.Items.Count == 0)
            {
                PagePicker.Items.Clear();
                for (var i = 0; i < doc.Pages.Count; i++) PagePicker.Items.Add($"{i + 1} · {doc.Pages[i].Title}");
                lastSource = doc.Source;
            }
            PagePicker.SelectedIndex = doc.ActivePage;
            PagePicker.Visibility = doc.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            FileNameText.Text = fileName;
            CanvasTitle.Text = doc.Page.Title;
            CanvasFamily.Text = $"{doc.Page.Family.ToUpperInvariant()}  /  DIAGRAM {doc.ActivePage + 1:00}";
            DocumentStats.Text = $"{doc.Objects.Count} objects   ·   {doc.Source.Length:N0} characters";
            undoButton.IsEnabled = doc.CanUndo || resourceCursor > 0; redoButton.IsEnabled = doc.CanRedo || resourceCursor < resourceHistory.Count;
            if (libraryFamily != doc.Page.Family) ShowLibrary();
            ShowInspector(); ApplyMode();
            foreach (var p in tools) Highlight(p.Value, p.Key == tool);
        }
        finally { syncing = false; }
        SaveDraft();
    }

    async Task RenderPageAsync()
    {
        if (!ready || mode == "Source" || renderedRevision == revision) return;
        int request = revision;
        string input = doc.Page.Source;
        try
        {
            Status("Rendering…");
            viewport.SourceFileName = fileName;
            await viewport.RenderAsync(input);
            if (request != revision) return;
            renderedRevision = request;
            if (fitAfterRender) { viewport.Fit(); fitAfterRender = false; }
            SyncCanvasSelection();
            if (draftSaved) Status((dirty ? "Draft saved in this browser" : "Ready") + " · " + viewport.ActiveRenderer);
            else Status("Browser draft unavailable. Use Save to download your changes.", true);
        }
        catch (Exception ex) { if (request == revision) Status(ex.Message, true); }
    }

    void Status(string message, bool error = false)
    {
        StatusText.Text = message;
        ToolTipService.SetToolTip(StatusText, message);
        StatusDot.Fill = error ? Brush(198, 77, 67) : Brush(50, 147, 113);
    }

    void History(bool redo)
    {
        if (TryResourceHistory(redo)) return;
        if (!(redo ? doc.Redo() : doc.Undo())) return;
        Changed(history: true);
    }

    void Changed(bool history = false)
    {
        if (!history) DiscardResourceRedo();
        additionalSelection.Clear(); selected = null; revision++; dirty = true;
        changeTimer.Stop(); Refresh(); _ = RenderPageAsync();
    }

    void Edit(Func<bool> operation, string failure = "This operation is unavailable for the selected syntax.")
    {
        if (resourceHistoryBusy) { Status("Finishing the imported source update…"); return; }
        try { if (operation()) Changed(); else Status(failure, true); }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    DiagramObject? Resolve(DiagramHit hit)
    {
        var item = doc.Find(hit.Id, hit.Line >= 0 ? hit.Line + doc.Page.StartLine : null);
        if (hit.Generated && (item is not { CanRename: true, MacroName.Length: > 0 } ||
            item.IsConnectable && (hit.Id.Length == 0 || hit.Id != item.Alias && !hit.Id.EndsWith("." + item.Alias, StringComparison.Ordinal)))) return null;
        // An implicit lifeline may carry the source line of its first message. Its
        // header cannot safely be treated as that message's editable label.
        return hit.Participant && item?.IsConnectable != true ? doc.Find(hit.Id) : item;
    }

    void OnHit(DiagramHit hit)
    {
        if (renderedRevision != revision) return;
        if (hit.Generated && Resolve(hit) == null)
        {
            selectedJsonField = null; selectedSourceField = null; selectedDot = null; additionalSelection.Clear(); selected = null;
            selectedGenerated = hit; generatedRevision = revision;
            ShowInspector(); SyncCanvasSelection();
            if (hit.Kind == "edit" && !BeginImportedLabel(hit)) EditGeneratedSource(hit);
            int line = doc.Page.StartLine + hit.Line;
            if (mode == "Split" && doc.Find(null,line) is { } origin) SelectSource(origin);
            Status(hit.SourceFile is { Length: > 0 } file ? $"Generated from {new ImportedSource(file, hit.SourceArchive)}, line {hit.FileLine + 1}." :
                $"Generated from line {line + 1}. Edit the invocation to change this object.");
            return;
        }
        selectedGenerated = null;
        SelectJsonField(hit.Token);
        SelectSourceField(hit.Field);
        SelectDot(hit.Dot, hit.Kind == "toggle" && hit.Field == null);
        if (hit.Kind == "toggle" && hit.Dot != null && hit.Field == null)
        {
            additionalSelection.Clear(); selected = null;
            ShowInspector(); SyncCanvasSelection();
            return;
        }
        if (hit.Kind == "toggle" && JsonSelection == null && FieldSelection == null && DotSelection == null) { ToggleSelection(Resolve(hit)); return; }
        additionalSelection.Clear();
        selected = DotSelection == null ? Resolve(hit) : null;
        ShowInspector(); SyncCanvasSelection();
        if (mode == "Split" && JsonSelection is { } json) SourceEditor.Select(json.Offset, json.Length);
        else if (mode == "Split" && FieldSelection is { } field) SourceEditor.Select(field.Offset, field.Length);
        else if (mode == "Split" && DotSelection is { } dot) SourceEditor.Select(dot.Offset, dot.Location.Length);
        else if (mode == "Split" && selected != null) SelectSource(selected);
        if (hit.Kind == "edit") BeginCanvasEdit(selected);
    }

    void SyncCanvasSelection()
    {
        var dot = DotSelection;
        var dotItems = DotSelectionItems();
        viewport.SetSelection(dot?.Location.Id ?? selected?.Alias ?? GeneratedSelection?.Id ?? "", selected == null ? GeneratedSelection?.Line ?? -1 : selected.Line - doc.Page.StartLine);
        if (JsonSelection != null) viewport.SelectJsonToken(jsonStart);
        if (FieldSelection != null) viewport.SelectJsonToken(fieldStart);
        viewport.SetConnectable((dot?.IsConnectable == true || selected?.IsConnectable == true) && additionalSelection.Count == 0 && dotItems.Length <= 1 && FieldSelection == null);
        var edge = additionalSelection.Count == 0 && selected?.Kind == "Relationship" ? selected : null;
        if (edge != null && (doc.Find(edge.From)?.IsConnectable != true || doc.Find(edge.To)?.IsConnectable != true)) edge = null;
        viewport.SetEndpoints(dot?.CanEditConnection == true && dot.Location.FromId.Length > 0 ? "node:" + dot.Location.FromId : edge?.From ?? "",
            dot?.CanEditConnection == true && dot.Location.ToId.Length > 0 ? "node:" + dot.Location.ToId : edge?.To ?? "");
        var items = SelectionItems();
        if (dot != null && FieldSelection == null)
        {
            viewport.SetAdditionalDotSelection(string.Join(",", additionalDots.Keys.Select(Uri.EscapeDataString)),
                doc.CopyDotSelection(dotItems) ?? doc.Page.Source.Substring(dot.Location.Start, dot.Location.Length));
            return;
        }
        viewport.SetAdditionalSelection(string.Join(",", additionalSelection.Select(o => o.Line - doc.Page.StartLine)),
            JsonSelection is { } json ? doc.Source.Substring(json.Offset,json.Length) : FieldSelection is { } field && !(field.Kind == "activity-label" && selected?.Kind == "Activity") ? field.Text :
            dot != null ? doc.Page.Source.Substring(dot.Location.Start, dot.Location.Length) :
            items.Length == 0 ? "" : doc.CopySelection(items) ?? (items.Length == 1 ? items[0].Statement : ""));
    }

    void OnGesture(DiagramGesture gesture)
    {
        if (renderedRevision != revision) return;
        if (HandleDotGesture(gesture)) return;
        var from = Resolve(gesture.From); var to = Resolve(gesture.To);
        if (from == null || to == null) { Status("Choose two source-backed objects.", true); return; }
        if (gesture.Kind is "retargetFrom" or "retargetTo")
        {
            if (from.Kind != "Relationship" || !to.IsConnectable) { Status("Choose a declared object as the new endpoint.", true); return; }
            EditProperty(from, () => doc.UpdateConnection(from, gesture.Kind == "retargetFrom" ? to : null,
                gesture.Kind == "retargetTo" ? to : null, from.Arrow));
            return;
        }
        if (gesture.Kind == "connect") Edit(() => doc.Connect(from, to), "These objects cannot be connected with the current diagram syntax.");
        if (gesture.Kind is "moveBefore" or "place")
        {
            if (gesture.Direction.Length != 0 && doc.CanPlaceRelative(from, to))
                Edit(() => doc.PlaceRelative(from, to, gesture.Direction));
            else Edit(() => doc.MoveBefore(from, to), "Only compatible graph objects or sibling declarations can be moved.");
        }
    }

    void Select(DiagramObject item)
    {
        selectedGenerated = null; selectedJsonField = null; selectedSourceField = null; selectedDot = null; additionalSelection.Clear(); selected = item; ShowInspector(); SyncCanvasSelection();
        if (mode != "Design") SelectSource(item);
    }

    void SelectSource(DiagramObject item) =>
        SourceEditor.Select(item.Offset, item.End - item.Offset);
}
