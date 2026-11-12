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
        // History belongs to the document, including while a new frame is being
        // prepared or a literal-only selection has no structural commands.
        if (action is "undo" or "redo") { History(action == "redo"); return; }
        if (renderedRevision != revision) return;
        if (action is "delete" or "cut" && !CanDeleteCanvasSelection()) return;
        if (TryHandleNetworkCanvasCommand(action, text) || TryHandleFilesCanvasCommand(action, text) || TryHandleGanttCanvasCommand(action, text)) return;
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

    private bool CanDeleteCanvasSelection()
    {
        if (mode == "Source" || renderedRevision != revision || viewport.GetCanvasInfo() is not { } canvas) return true;
        var nodes = canvas.Nodes.ToHashSet(StringComparer.Ordinal);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var node in canvas.State.Nodes)
                if (node.Parent != null && nodes.Contains(node.Parent) && nodes.Add(node.Id)) changed = true;
        }
        if (!canvas.State.Nodes.Any(node => node.Locked && nodes.Contains(node.Id)) &&
            !canvas.State.Edges.Any(edge => edge.Locked && (canvas.Edges.Contains(edge.Id) || nodes.Contains(edge.Source) || nodes.Contains(edge.Target)))) return true;
        Status("Unlock the selected objects, their children and incident connections before deleting them.");
        return false;
    }

    private void DeleteCanvasObjects(Func<bool> operation, string failure = "This operation is unavailable for the selected syntax.")
    {
        if (CanDeleteCanvasSelection()) Edit(operation, failure);
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
        Properties.Children.Add(Item("Delete selection", "delete", () => DeleteCanvasObjects(() => doc.RemoveMany(items))));
    }
}
