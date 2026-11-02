using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    string? libraryFamily;

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
            LibraryFooter.Children.Add(Item("Add custom PlantUML…", "source", () => _ = InsertCustomAsync()));
        }
        else if (tab == "Diagrams")
        {
            LibraryCaption.Text = "DIAGRAM TEMPLATES";
            foreach (var template in StudioCatalog.Templates.Where(t => Matches(t.Name) || Matches(t.Family)))
            {
                Group(template.Family);
                LibraryItems.Children.Add(Item(template.Name, "theme", () => AppendPage(template), true)); count++;
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
        if (item == null)
        {
            var info = new Grid { ColumnSpacing = 6 };
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var type = Caption("Type");
            var value = Caption(doc.Page.Family); value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 1); info.Children.Add(type); info.Children.Add(value);
            Properties.Children.Add(info);
            Properties.Children.Add(Caption("Select an object on the canvas to edit its properties, or choose it from the outline."));
        }
        else
        {
            if (item.CanRename)
            {
                Properties.Children.Add(Caption(item.Kind == "Relationship" ? "Relationship label" : "Display name"));
                var rename = new TextBox { Text = item.Label, Style = (Style)Resources["Field"], PlaceholderText = "Display name" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rename, "Object display name");
                Properties.Children.Add(rename);
                Properties.Children.Add(Item("Apply label", "edit", () => EditProperty(item, () => doc.Rename(item, rename.Text))));
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
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, name + " fill");
                    ToolTipService.SetToolTip(swatch, name + " " + hex);
                    swatch.Click += (_, _) => EditProperty(item, () => doc.SetFill(item, hex));
                    palette.Children.Add(swatch);
                }
                Properties.Children.Add(palette);
            }
            if (item.CanInsertMember) Properties.Children.Add(Item("Add member…", "plus", () => _ = AddMemberAsync(item)));
            if (item.Kind == "Relationship") Properties.Children.Add(Item("Edit connection…", "connect", () => _ = EditConnectionAsync(item)));
            if (!string.IsNullOrWhiteSpace(item.Alias)) Properties.Children.Add(Caption($"Identifier   {item.Alias}"));
            Properties.Children.Add(Caption($"Source   L{item.Line + 1}" + (item.EndLine > item.Line ? $"–{item.EndLine + 1}" : "")));
            Properties.Children.Add(Item("Edit statement…", "source", () => _ = EditStatementAsync(item)));
            if (item.IsConnectable) Properties.Children.Add(Item("Connect to…", "connect", () => _ = ConnectAsync(item)));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            actions.Children.Add(Command("Duplicate object", "copy", () => Edit(() => doc.Duplicate(item))));
            actions.Children.Add(Command("Move up", "up", () => Edit(() => doc.Move(item, -1))));
            actions.Children.Add(Command("Move down", "down", () => Edit(() => doc.Move(item, 1))));
            actions.Children.Add(Command("Delete object", "delete", () => Edit(() => doc.Remove(item))));
            Properties.Children.Add(actions);
        }
        ObjectCount.Text = doc.Objects.Count.ToString();
        foreach (var obj in doc.Objects.Take(outlineLimit))
        {
            var b = Item(string.IsNullOrEmpty(obj.Label) ? obj.Statement : obj.Label, ShapeIcon(obj.Kind), () => Select(obj));
            if (obj == item) Highlight(b, true);
            ToolTipService.SetToolTip(b, $"{obj.Kind} · line {obj.Line + 1}");
            OutlineItems.Children.Add(b);
        }
        if (doc.Objects.Count > outlineLimit)
            OutlineItems.Children.Add(Item($"Show next {Math.Min(40, doc.Objects.Count - outlineLimit)}", "down", () => { outlineLimit += 40; ShowInspector(); }));
    }

    void EditProperty(DiagramObject item, Func<bool> edit)
    {
        if (!edit()) { Status("This value is unchanged or unsupported for the selected syntax."); return; }
        // Re-acquire the entry from the new source index, preserving inspector focus across property edits.
        selected = doc.Find(item.Alias, item.Line);
        dirty = true; revision++; changeTimer.Stop(); Refresh(); _ = RenderPageAsync();
    }

    async Task AddMemberAsync(DiagramObject item)
    {
        var field = new TextBox { Header = "Member declaration", PlaceholderText = "+operation() : Result" };
        if (await AskAsync("Add member", field, "Add") == ContentDialogResult.Primary)
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
        if (!arrows.Contains(item.Arrow)) arrows.Insert(0, item.Arrow);
        var arrow = new ComboBox { Header = "Connector", ItemsSource = arrows, SelectedIndex = arrows.IndexOf(item.Arrow), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(from); fields.Children.Add(to); fields.Children.Add(arrow);
        if (await AskAsync("Edit connection", fields, "Apply") != ContentDialogResult.Primary) return;
        bool changed = false;
        if (from.SelectedIndex >= 0 && to.SelectedIndex >= 0 && 
            (endpoints[from.SelectedIndex].Alias != item.From || endpoints[to.SelectedIndex].Alias != item.To))
            changed = doc.Retarget(item, endpoints[from.SelectedIndex], endpoints[to.SelectedIndex]);
        var current = changed ? doc.Find(null, item.Line) : item;
        if (current != null && arrow.SelectedIndex >= 0 && arrows[arrow.SelectedIndex] != item.Arrow)
            changed |= doc.SetArrow(current, arrows[arrow.SelectedIndex]);
        if (changed)
        {
            selected = doc.Find(null, item.Line); dirty = true; revision++; Refresh(); _ = RenderPageAsync();
        }
    }

    async Task<ContentDialogResult> AskAsync(string title, UIElement content, string accept)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content,
            PrimaryButtonText = accept, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync();
    }

    TextBox StatementField(string text, string hint) => new() { Text = text, PlaceholderText = hint, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 250, IsSpellCheckEnabled = false, FontFamily = new FontFamily("monospace"), FontSize = 13 };

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
        var targets = doc.Objects.Where(o => o.IsConnectable).ToList();
        var target = new ComboBox { Header = "Target", ItemsSource = targets.Select(o => $"{o.Label}  ·  {o.Alias}").ToList(),
            SelectedIndex = Math.Max(0, targets.FindIndex(o => o != from)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var arrow = new TextBox { Header = "Arrow", Text = doc.Page.Family.Equals("Sequence", StringComparison.OrdinalIgnoreCase) ? "->" : "-->" };
        var label = new TextBox { Header = "Label", PlaceholderText = "Optional" };
        var fields = new StackPanel { Spacing = 8 }; fields.Children.Add(target); fields.Children.Add(arrow); fields.Children.Add(label);
        if (await AskAsync($"Connect {from.Label}", fields, "Connect") == ContentDialogResult.Primary && target.SelectedIndex >= 0)
            Edit(() => doc.Connect(from, targets[target.SelectedIndex], arrow.Text, label.Text));
    }
}
