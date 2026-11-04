using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    string? libraryFamily;
    bool includeSourceInOutline;
    int dialogGeneration, activeDialogs;

    void ShowLibrary()
    {
        if (LibraryItems == null) return;
        foreach (var pair in tabs) Highlight(pair.Value, pair.Key == tab);
        LibraryItems.Children.Clear(); LibraryFooter.Children.Clear();
        string query = SearchBox.Text.Trim();
        bool Matches(string text) => text.Contains(query, StringComparison.OrdinalIgnoreCase);
        string previous = "";
        int count = 0;
        void Group(string name)
        {
            if (name == previous) return;
            previous = name;
            var caption = Caption(name);
            caption.FontSize = 11;
            caption.Margin = new Thickness(4, count > 0 ? 8 : 1, 0, 2);
            LibraryItems.Children.Add(caption);
        }
        if (tab == "Objects")
        {
            LibraryCaption.Text = "TOOLBOX";
            foreach (var entry in StudioCatalog.ForPage(doc.Page).Where(t => Matches(t.Name) || Matches(t.Group)))
            {
                Group(entry.Group);
                var button = Item(entry.Name, ShapeIcon(entry.Name), () => InsertTool(entry), true);
                button.CanDrag = true;
                button.DragStarting += (_, e) =>
                {
                    e.Data.SetText($"unouml:tool:{entry.Group}/{entry.Name}");
                    e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                };
                LibraryItems.Children.Add(button);
                count++;
            }
            LibraryFooter.Children.Add(Item("Standard library…", "library", () => _ = BrowseStandardLibraryAsync()));
            LibraryFooter.Children.Add(Item("Add custom PlantUML…", "source", () => _ = InsertCustomAsync()));
        }
        else if (tab == "Diagrams")
        {
            LibraryCaption.Text = "DIAGRAM TEMPLATES";
            foreach (var template in StudioCatalog.Templates.Where(t => Matches(t.Name) || Matches(t.Family)))
            {
                Group(template.Family);
                LibraryItems.Children.Add(Item(template.Name + (template.RequiresServer ? " · JVM" : ""), "theme", () => AppendPage(template), true)); count++;
            }
            LibraryFooter.Children.Add(Caption("Choose a template to add a page to this document."));
        }
        else
        {
            LibraryCaption.Text = "YOUR SNIPPETS";
            foreach (var snippet in snippets.Where(s => Matches(s.Name)))
            {
                LibraryItems.Children.Add(Item(snippet.Name, "note", () => Edit(() => doc.Insert(snippet.Source)), true)); count++;
            }
            LibraryFooter.Children.Add(Item("New snippet…", "plus", () => _ = AddSnippetAsync()));
            LibraryFooter.Children.Add(Item("Import library…", "open", () => _ = ImportLibraryAsync()));
        }
        if (count == 0) LibraryItems.Children.Add(Caption(query.Length > 0 ? "No matches. Try a different search." :
            tab == "Saved" ? "Save a reusable declaration, relation, or fragment here." : "Use custom PlantUML or edit the source for this diagram."));
        libraryFamily = doc.Page.Family;
    }

    void InsertTool(ToolItem entry)
    {
        if (lastWidth < 620) { libraryOpen = false; ArrangePanels(); }
        Edit(() => doc.Add(entry));
    }

    void DropTool(string payload)
    {
        const string prefix = "unouml:tool:";
        if (!payload.StartsWith(prefix, StringComparison.Ordinal)) return;
        string key = payload[prefix.Length..];
        var entry = StudioCatalog.ForPage(doc.Page).FirstOrDefault(t => t.Group + "/" + t.Name == key);
        if (entry != null) InsertTool(entry);
    }

    void AppendPage(DiagramTemplate template)
    {
        int added = doc.Pages.Count;
        string newline = doc.Source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string text = template.Source.ReplaceLineEndings(newline);
        doc.SetSource(doc.Source.TrimEnd('\r','\n') + newline + newline + text);
        fitAfterRender = true;
        doc.ActivePage = Math.Min(added, doc.Pages.Count - 1);
        if (lastWidth < 620) { libraryOpen = false; ArrangePanels(); }
        Changed();
    }

    void ShowInspector()
    {
        if (Properties == null) return;
        Properties.Children.Clear(); OutlineItems.Children.Clear();
        var item = selected;
        SelectionType.Text = item == null ? "DIAGRAM" : item.Kind.ToUpperInvariant();
        SelectionTitle.Text = item?.Label ?? doc.Page.Title;
        if (item == null && GeneratedSelection is { } generated)
        {
            SelectionType.Text = "GENERATED OBJECT";
            SelectionTitle.Text = generated.Label;
            Properties.Children.Add(Caption(generated.SourceFile is { Length: > 0 } file ?
                $"Source file   {new ImportedSource(file, generated.SourceArchive)}\nLine   {generated.FileLine + 1}" : $"Source invocation   L{doc.Page.StartLine + generated.Line + 1}"));
            if (OpenImportedLabel(generated) != null)
                Properties.Children.Add(Item("Edit label on canvas…", "edit", () => BeginImportedLabel(generated)));
            Properties.Children.Add(Item(generated.SourceFile != null && BrowserIncludes.Read(generated.SourceFile, generated.SourceArchive) != null ?
                "Edit included source…" : "Edit source invocation…", "source", () => EditGeneratedSource(generated)));
        }
        else if (JsonSelection is { } field)
        {
            SelectionType.Text = "JSON " + field.Kind.ToUpperInvariant();
            SelectionTitle.Text = field.Text;
            Properties.Children.Add(Caption(field.Kind == "scalar" ? "Enter a JSON scalar such as 42, true, null or a quoted string." : "Enter text directly. Quotes and special characters are escaped when saved."));
            Properties.Children.Add(Item("Edit field…", "edit", () => BeginCanvasEdit(item)));
        }
        else if (FieldSelection is { } literal)
        {
            SelectionType.Text = doc.Page.Family.ToUpperInvariant() + " " + literal.Role.ToUpperInvariant();
            SelectionTitle.Text = literal.Text;
            Properties.Children.Add(Caption(literal.Kind == "salt-state" ? "Double-click the indicator to change its checked state." :
                literal.Kind is "number" or "integer" ? "Enter a numeric value." : "Edit the displayed value on the canvas. Its surrounding syntax and formatting are preserved."));
            Properties.Children.Add(Item(literal.Kind == "salt-state" ? "Toggle checked state" : "Edit field…", "edit", () => BeginCanvasEdit(item)));
            AddSaltFieldActions(literal);
            AddStructuredFieldActions(literal);
            AddNetworkFieldActions(literal);
            AddFilesFieldActions(literal);
            AddGanttFieldActions(literal);
            AddDotFieldStructureActions(literal);
        }
        else if (DotSelection is { } dot) ShowDotInspector(dot);
        else if (additionalSelection.Count > 0 && selected != null) ShowSelectionActions();
        else if (item == null)
        {
            var info = new Grid { ColumnSpacing = 6 };
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var type = Caption("Type");
            var value = Caption(doc.Page.Family); value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 1); info.Children.Add(type); info.Children.Add(value);
            Properties.Children.Add(info);
            Properties.Children.Add(Caption("Select an object on the canvas to edit its properties, or choose it from the outline."));
            if (doc.Drawing is { } drawing) Properties.Children.Add(Item("Edit ASCII drawing…", "edit", () => { selected = drawing; BeginCanvasEdit(drawing); }));
            AddStructuredDiagramActions();
            AddNetworkDiagramActions();
            AddFilesDiagramActions();
            AddGanttDiagramActions();
        }
        else
        {
            if (item.CanRename)
            {
                string labelCaption = item.Kind == "Relationship" ? "Relationship label" : "Display name";
                if (item.Kind == "Relationship" && doc.Page.Family == "Chen") labelCaption = "Cardinality";
                if (item.Kind is "Formula" or "ASCII drawing") labelCaption = item.Kind;
                Properties.Children.Add(Caption(labelCaption));
                var rename = new TextBox { Style = (Style)Resources["Field"], AcceptsReturn = item.MultilineLabel,
                    Text = item.EditableLabel, PlaceholderText = labelCaption, MaxHeight = item.MultilineLabel ? 180 : double.PositiveInfinity,
                    TextWrapping = item.MultilineLabel ? TextWrapping.Wrap : TextWrapping.NoWrap };
                BrowserAccessibility.SetName(rename, "Object display name");
                Properties.Children.Add(rename);
                Properties.Children.Add(Item("Apply label", "edit", () => EditProperty(item, () => doc.Rename(item, rename.Text))));
                if (item.MultilineLabel || item.Kind == "Formula") Properties.Children.Add(Item("Edit on canvas…", "edit", () => BeginCanvasEdit(item)));
            }
            if (item.CanSetFill)
            {
                Properties.Children.Add(Caption("Fill color"));
                var palette = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                foreach (var (name, hex) in new[] { ("Lavender", "#DFDAFB"), ("Blue", "#CCE5FC"), ("Mint", "#CEF0E1"), ("Peach", "#F9DCC8"), ("Rose", "#F4D2DF") })
                {
                    var color = Convert.ToInt32(hex[1..], 16);
                    var swatch = new Button { Width = 26, Height = 24, MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(1),
                        Background = Brush((byte)(color >> 16), (byte)(color >> 8), (byte)color), CornerRadius = new CornerRadius(5),
                        BorderBrush = Brush(207, 204, 219), Padding = new Thickness(0) };
                    BrowserAccessibility.SetName(swatch, name + " fill");
                    ToolTipService.SetToolTip(swatch, name + " " + hex);
                    swatch.Click += (_, _) => EditProperty(item, () => doc.SetFill(item, hex));
                    palette.Children.Add(swatch);
                }
                Properties.Children.Add(palette);
            }
            if (item.CanInsertMember) Properties.Children.Add(Item(doc.Page.Family == "Chen" ? "Add attribute…" : "Add member…", "plus", () => _ = AddMemberAsync(item)));
            if (item.Kind == "Relationship") Properties.Children.Add(Item("Edit connection…", "connect", () => _ = EditConnectionAsync(item)));
            if (!string.IsNullOrWhiteSpace(item.Alias) && item.Kind != "ASCII label") Properties.Children.Add(Caption($"Identifier   {item.Alias}"));
            Properties.Children.Add(Caption($"Source   L{item.Line + 1}" + (item.EndLine > item.Line ? $"–{item.EndLine + 1}" : "")));
            Properties.Children.Add(Item("Edit statement…", "source", () => _ = EditStatementAsync(item)));
            AddNetworkObjectActions(item);
            AddFilesObjectActions(item);
            AddGanttObjectActions(item);
            if (item.IsConnectable) Properties.Children.Add(Item("Connect to…", "connect", () => _ = ConnectAsync(item)));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (item.Kind is not ("ASCII label" or "ASCII drawing" or "Chen attribute"))
                actions.Children.Add(Command("Duplicate object", "copy", () => Edit(() => doc.Duplicate(item))));
            if (item.Kind is not ("ASCII label" or "ASCII drawing") && doc.Page.Family != "C4")
            {
                actions.Children.Add(Command("Move up", "up", () => Edit(() => doc.Move(item, -1))));
                actions.Children.Add(Command("Move down", "down", () => Edit(() => doc.Move(item, 1))));
            }
            actions.Children.Add(Command("Delete object", "delete", () => DeleteCanvasObjects(() => doc.Remove(item))));
            if (doc.Page.Family is not ("Network" or "Files" or "Gantt")) Properties.Children.Add(actions);
        }
        AddCanvasInspectorActions();
        var visualObjects = doc.Objects.Where(o => o.Kind is not ("Source" or "Style" or "Caption" or "Library")).ToList();
        var outline = includeSourceInOutline || visualObjects.Count == 0 ? doc.Objects : visualObjects;
        ObjectCount.Text = outline.Count.ToString();
        foreach (var obj in outline.Take(outlineLimit))
        {
            var b = Item(string.IsNullOrEmpty(obj.Label) ? obj.Statement : obj.Label, ShapeIcon(obj.Kind), () => Select(obj));
            if (obj == item || additionalSelection.Contains(obj)) Highlight(b, true);
            ToolTipService.SetToolTip(b, $"{obj.Kind} · line {obj.Line + 1}");
            OutlineItems.Children.Add(b);
        }
        if (outline.Count > outlineLimit)
            OutlineItems.Children.Add(Item($"Show next {Math.Min(40, outline.Count - outlineLimit)}", "down", () => { outlineLimit += 40; ShowInspector(); }));
        if (visualObjects.Count != 0 && visualObjects.Count != doc.Objects.Count)
            OutlineItems.Children.Add(Item(includeSourceInOutline ? "Show visual objects" : "Include source statements", "list", () => { includeSourceInOutline = !includeSourceInOutline; ShowInspector(); }));
    }

    void EditProperty(DiagramObject item, Func<bool> edit)
    {
        if (!edit()) { Status(item.Kind == "ASCII label" ? "Use a plain-text label that fits the available cells without moving its border." : "This value is unchanged or unsupported for the selected syntax."); return; }
        // Re-acquire the entry from the new source index, preserving inspector focus across property edits.
        selected = doc.Find(item.Alias, item.Line);
        dirty = true; revision++; changeTimer.Stop(); Refresh(); _ = RenderPageAsync();
    }

    async Task AddMemberAsync(DiagramObject item)
    {
        bool chen = doc.Page.Family == "Chen";
        var field = new TextBox { Header = chen ? "Attribute declaration" : "Member declaration", PlaceholderText = chen ? "CustomerId : INTEGER <<key>>" : "+operation() : Result" };
        if (await AskAsync(chen ? "Add attribute" : "Add member", field, "Add") == ContentDialogResult.Primary)
            EditProperty(item, () => doc.InsertMember(item, field.Text));
    }

    async Task EditConnectionAsync(DiagramObject item)
    {
        var endpoints = doc.Objects.Where(o => o.IsConnectable).ToList();
        var labels = endpoints.Select(o => $"{o.Label} · {o.Alias}").ToList();
        ComboBox Picker(string header, string alias) => new() { Header = header, ItemsSource = labels,
            SelectedIndex = endpoints.FindIndex(o => o.Alias == alias), HorizontalAlignment = HorizontalAlignment.Stretch };
        var from = Picker("Source", item.From); var to = Picker("Target", item.To);
        var arrows = new List<string> { "->", "-->", "<-", "<--", "--", "..", "..>", "<|--", "--|>", "*--", "o--", "<|..", "..|>", "->>", "-->>", "<->", "<-->" };
        if (doc.Page.Family == "Chen") arrows = ["-1-", "-N-", "-(0,1)-", "-(0,N)-", "-(1,N)-", "=1=", "=N=", "=(1,N)="];
        if (doc.Page.Family == "C4") arrows = ["Rel", "Rel_R", "Rel_L", "Rel_U", "Rel_D", "Rel_Back", "Rel_Neighbor", "BiRel", "BiRel_R", "BiRel_L", "BiRel_U", "BiRel_D"];
        if (!arrows.Contains(item.Arrow)) arrows.Insert(0, item.Arrow);
        var arrow = new ComboBox { Header = "Connector", ItemsSource = arrows, SelectedIndex = arrows.IndexOf(item.Arrow), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(from); fields.Children.Add(to); fields.Children.Add(arrow);
        if (await AskAsync("Edit connection", fields, "Apply") != ContentDialogResult.Primary) return;
        var source = from.SelectedIndex < 0 ? null : endpoints[from.SelectedIndex];
        var destination = to.SelectedIndex < 0 ? null : endpoints[to.SelectedIndex];
        string connector = arrow.SelectedIndex < 0 ? item.Arrow : arrows[arrow.SelectedIndex];
        EditProperty(item, () => doc.UpdateConnection(item, source, destination, connector));
    }

    async Task<ContentDialogResult> AskAsync(string title, UIElement content, string accept)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content,
            PrimaryButtonText = accept, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        BrowserAccessibility.PrepareDialog(dialog);
        return await ShowDialogAsync(dialog);
    }

    async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialogGeneration++; activeDialogs++;
        try { return await dialog.ShowAsync(); }
        finally { activeDialogs--; dialogGeneration++; }
    }

    TextBox StatementField(string text, string hint)
    {
        var field = new TextBox { AcceptsReturn = true, Text = text, PlaceholderText = hint,
            TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 250, IsSpellCheckEnabled = false, FontFamily = new FontFamily("monospace"), FontSize = 13 };
        BrowserAccessibility.SetName(field, hint);
        return field;
    }

    async Task EditStatementAsync(DiagramObject item)
    {
        var field = StatementField(item.Statement, "PlantUML statement");
        if (await AskAsync("Edit PlantUML statement", field, "Apply") == ContentDialogResult.Primary)
            Edit(() => doc.Replace(item, field.Text));
    }

    async Task InsertCustomAsync()
    {
        var field = StatementField("", "PlantUML to insert on this page");
        if (await AskAsync("Insert PlantUML", field, "Insert") == ContentDialogResult.Primary) Edit(() => doc.Insert(field.Text));
    }

    async Task ConnectAsync(DiagramObject from)
    {
        bool chen = doc.Page.Family == "Chen";
        var targets = doc.Objects.Where(o => o.IsConnectable).ToList();
        if (targets.Count == 0) return;
        var target = new ComboBox { Header = "Target", ItemsSource = targets.Select(o => $"{o.Label}  ·  {o.Alias}").ToList(),
            SelectedIndex = Math.Max(0, targets.FindIndex(o => o != from)), HorizontalAlignment = HorizontalAlignment.Stretch };
        string connector = doc.Page.Family switch { "Chen" => "-1-", "C4" => "Rel", "Sequence" => "->", _ => "-->" };
        var arrow = new TextBox { Header = chen ? "Participation and cardinality" : "Connector", Text = connector };
        var label = new TextBox { Header = "Label", PlaceholderText = "Optional" };
        var fields = new StackPanel { Spacing = 8 }; fields.Children.Add(target); fields.Children.Add(arrow); if (!chen) fields.Children.Add(label);
        if (await AskAsync($"Connect {from.Label}", fields, "Connect") == ContentDialogResult.Primary && target.SelectedIndex >= 0)
            Edit(() => doc.Connect(from, targets[target.SelectedIndex], arrow.Text, label.Text));
    }
}
