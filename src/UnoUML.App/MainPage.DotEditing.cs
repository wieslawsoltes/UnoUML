using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private DotObjectEdit? selectedDot;
    private readonly Dictionary<string, DotObjectEdit> additionalDots = new(StringComparer.Ordinal);
    private int dotRevision = -1;
    private DotObjectEdit? DotSelection => dotRevision == revision ? selectedDot : null;

    private DotObjectEdit[] DotSelectionItems() => DotSelection is not { } selected ? [] :
        additionalDots.Values.Append(selected).OrderBy(item => item.Location.NameStart).ToArray();

    private void SelectDot(DotSourceLocation? location, bool toggle = false)
    {
        if (dotRevision != revision) additionalDots.Clear();
        var item = location == null ? null : doc.OpenDotObject(location);
        if (toggle && item?.IsConnectable == true && DotSelection?.IsConnectable == true)
        {
            if (selectedDot!.Location.Id == item.Location.Id)
            {
                selectedDot = additionalDots.Values.FirstOrDefault();
                if (selectedDot != null) additionalDots.Remove(selectedDot.Location.Id);
            }
            else if (!additionalDots.Remove(item.Location.Id))
            {
                additionalDots[selectedDot.Location.Id] = selectedDot;
                selectedDot = item;
            }
        }
        else
        {
            additionalDots.Clear();
            selectedDot = item;
        }
        dotRevision = revision;
    }

    private void ShowDotInspector(DotObjectEdit item)
    {
        var items = DotSelectionItems();
        if (items.Length > 1)
        {
            SelectionType.Text = "DOT MULTIPLE SELECTION";
            SelectionTitle.Text = $"{items.Length} nodes";
            Properties.Children.Add(Caption("Shift-click a node to add or remove it."));
            AddDotNodeActions(items);
            return;
        }
        SelectionType.Text = "DOT " + item.Location.Kind.ToUpperInvariant();
        SelectionTitle.Text = item.Text.Length == 0 ? item.Location.Name : item.Text;
        if (item.CanRename)
        {
            var field = new TextBox { Style = (Style)Resources["Field"], Text = item.Text, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 180 };
            BrowserAccessibility.SetName(field, "Object display name");
            Properties.Children.Add(Caption("Display label"));
            Properties.Children.Add(field);
            Properties.Children.Add(Item("Apply label", "edit", () => Edit(() => doc.RenameDotObject(item, field.Text))));
            Properties.Children.Add(Item("Edit on canvas…", "edit", () => BeginCanvasEdit(null)));
        }
        else Properties.Children.Add(Caption("Double-click an individual table cell or record field to edit its displayed text."));
        AddDotStructureActions(item);
        if (item.IsConnectable)
        {
            Properties.Children.Add(Caption("Drag the connection handle to another node to add an edge. Shift-click to select several nodes."));
            if (item.CanRemove) AddDotNodeActions(items);
            if (!item.CanDuplicate) Properties.Children.Add(Caption("This node belongs to a subgraph used in an edge product. Edit its copies in Source to control the resulting connections."));
        }
        if (item.CanUngroup)
        {
            Properties.Children.Add(Item("Ungroup cluster", "layers", () => Edit(() => doc.UngroupDotObject(item))));
            Properties.Children.Add(Item("Delete cluster and nodes", "delete", () => DeleteCanvasObjects(() => doc.RemoveDotObject(item))));
        }
        if (item.CanEditConnection)
        {
            Properties.Children.Add(Caption("Drag an endpoint handle onto another node to reconnect this edge."));
            Properties.Children.Add(Item("Delete edge", "delete", () => DeleteCanvasObjects(() => doc.RemoveDotEdge(item), "Select an editable DOT edge to delete it.")));
        }
        else if (item.Location.Kind == "edge") Properties.Children.Add(Caption("This edge metadata no longer matches the source. Select it again after rendering."));
    }

    private void AddDotNodeActions(DotObjectEdit[] items)
    {
        Properties.Children.Add(Item("Group in cluster…", "layers", () => _ = GroupDotSelectionAsync(items)));
        if (items.All(item => item.CanDuplicate))
            Properties.Children.Add(Item(items.Length == 1 ? "Duplicate node" : "Duplicate selected nodes", "plus", () => Edit(() => doc.DuplicateDotObjects(items))));
        Properties.Children.Add(Item(items.Length == 1 ? "Delete node" : "Delete selected nodes", "delete", () => DeleteCanvasObjects(() => doc.RemoveDotObjects(items))));
    }

    private async Task GroupDotSelectionAsync(DotObjectEdit[] items)
    {
        if (!doc.CanGroupDotObjects(items)) { Status("Choose nodes in the same cluster, including every member of a shared rank constraint.", true); return; }
        var name = new TextBox { Header = "Cluster label", Text = "Group" };
        if (await AskAsync("Group selected DOT nodes", name, "Group") == ContentDialogResult.Primary)
            Edit(() => doc.GroupDotObjects(items, name.Text), "Choose nodes in the same cluster and enter a label.");
    }

    private void OnDotCanvasCommand(string action, string text)
    {
        var items = DotSelectionItems();
        switch (action)
        {
            case "cut":
            case "delete": Edit(() => doc.RemoveDotObjects(items), "Choose nodes, a cluster, or an edge to delete."); break;
            case "duplicate": Edit(() => doc.DuplicateDotObjects(items), "Choose nodes whose subgraph memberships are independent of edge products."); break;
            case "group": _ = GroupDotSelectionAsync(items); break;
            case "ungroup":
                if (items.Length == 1) Edit(() => doc.UngroupDotObject(items[0]), "Choose a cluster to ungroup.");
                break;
            case "paste": Edit(() => doc.PasteDotSelection(text), "Paste a complete DOT document with the same graph direction."); break;
        }
    }

    private void SelectDotRegion(DiagramHit[] hits, bool add)
    {
        var items = hits.Where(hit => hit.Dot?.Kind == "node" && hit.Field == null).Select(hit => doc.OpenDotObject(hit.Dot!))
            .OfType<DotObjectEdit>().ToArray();
        if (add && DotSelection?.IsConnectable == true) additionalDots[selectedDot!.Location.Id] = selectedDot;
        else additionalDots.Clear();
        foreach (var item in items) additionalDots[item.Location.Id] = item;
        selectedDot = additionalDots.Values.FirstOrDefault();
        if (selectedDot != null) additionalDots.Remove(selectedDot.Location.Id);
        dotRevision = revision;
        selected = null; selectedGenerated = null; selectedJsonField = null; selectedSourceField = null;
        additionalSelection.Clear();
        ShowInspector(); SyncCanvasSelection();
    }

    private bool HandleDotGesture(DiagramGesture gesture)
    {
        if (gesture.From.Dot == null && gesture.To.Dot == null) return false;
        var from = gesture.From.Dot == null ? null : doc.OpenDotObject(gesture.From.Dot);
        var to = gesture.To.Dot == null ? null : doc.OpenDotObject(gesture.To.Dot);
        if (from == null || to == null) { Status("Choose two editable DOT objects.", true); return true; }
        if (gesture.Kind == "connect") Edit(() => doc.ConnectDot(from, to), "Choose two DOT nodes to connect.");
        else if (gesture.Kind is "retargetFrom" or "retargetTo")
            Edit(() => doc.RetargetDot(from, to, gesture.Kind == "retargetFrom"), "Choose an editable edge and a node in its scope to reconnect it.");
        else Status("DOT layout follows its graph attributes and edge constraints.");
        return true;
    }
}
