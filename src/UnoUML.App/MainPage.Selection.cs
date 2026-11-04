using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private readonly HashSet<DiagramObject> additionalSelection = [];

    private DiagramObject[] SelectionItems() => selected is null ? [] :
        additionalSelection.Append(selected).Distinct().OrderBy(x => x.Line).ToArray();

    private void ToggleSelection(DiagramObject? item)
    {
        if (item == null) return;
        if (selected == item)
        {
            selected = additionalSelection.FirstOrDefault();
            if (selected != null) additionalSelection.Remove(selected);
        }
        else if (!additionalSelection.Remove(item))
        {
            if (selected != null) additionalSelection.Add(selected);
            selected = item;
        }
        ShowInspector();
        SyncCanvasSelection();
    }

    private void OnCanvasCommand(string action, string text)
    {
        if (renderedRevision != revision) return;
        if (action is "delete" or "cut" && viewport.GetCanvasInfo() is { } canvas &&
            (canvas.State.Nodes.Any(n => n.Locked && canvas.Nodes.Contains(n.Id)) || canvas.State.Edges.Any(e => e.Locked && canvas.Edges.Contains(e.Id))))
        { Status("Unlock the selected canvas objects before deleting them."); return; }
        if (doc.Page.Family == "DOT" && action == "paste") { OnDotCanvasCommand(action, text); return; }
        if (JsonSelection != null)
        {
            Status("Double-click to edit this JSON field or use Source to change its structure.");
            return;
        }
        if (FieldSelection is { } literal && !(literal.Kind == "activity-label" && selected?.Kind == "Activity"))
        {
            Status("Double-click to edit this field on the canvas.");
            return;
        }
        if (DotSelection is { } dot)
        {
            OnDotCanvasCommand(action, text);
            return;
        }
        var items = SelectionItems();
        switch (action)
        {
            case "cut":
            case "delete":
                if (items.Length != 0) Edit(() => doc.RemoveMany(items));
                break;
            case "duplicate":
                if (items.Length != 0) Edit(() => doc.DuplicateMany(items));
                break;
            case "paste":
                if (text.Length != 0) Edit(() => doc.PasteSelection(text), "This fragment cannot be pasted safely into the current diagram.");
                break;
            case "group":
                if (items.Length != 0) _ = GroupSelectionAsync(items);
                break;
        }
    }

    private async Task GroupSelectionAsync(DiagramObject[] items)
    {
        var name = new TextBox { Header = "Package name", Text = "Group" };
        if (await AskAsync("Group selected objects", name, "Group") == ContentDialogResult.Primary)
            Edit(() => doc.Group(items, name.Text), "Choose sibling declarations in a structural diagram.");
    }

    private void ShowSelectionActions()
    {
        var items = SelectionItems();
        SelectionType.Text = "MULTIPLE SELECTION";
        SelectionTitle.Text = $"{items.Length} objects";
        Properties.Children.Add(Caption("Shift-click an object to add or remove it."));
        Properties.Children.Add(Item("Group in package…", "layers", () => _ = GroupSelectionAsync(items)));
        Properties.Children.Add(Item("Duplicate selection", "plus", () => Edit(() => doc.DuplicateMany(items))));
        Properties.Children.Add(Item("Delete selection", "delete", () => Edit(() => doc.RemoveMany(items))));
    }
}
