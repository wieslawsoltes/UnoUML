using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private DotObjectEdit? selectedDot, editingDot;
    private int dotRevision = -1;
    private DotObjectEdit? DotSelection => dotRevision == revision ? selectedDot : null;

    private void SelectDot(DotSourceLocation? location)
    {
        selectedDot = location == null ? null : doc.OpenDotObject(location);
        dotRevision = revision;
    }

    private void ShowDotInspector(DotObjectEdit item)
    {
        SelectionType.Text = "DOT " + item.Location.Kind.ToUpperInvariant();
        SelectionTitle.Text = item.Text.Length == 0 ? item.Location.Name : item.Text;
        if (item.CanRename)
        {
            var field = new TextBox { Style = (Style)Resources["Field"], Text = item.Text, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 180 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, "Object display name");
            Properties.Children.Add(Caption("Display label"));
            Properties.Children.Add(field);
            Properties.Children.Add(Item("Apply label", "edit", () => Edit(() => doc.RenameDotObject(item, field.Text))));
            Properties.Children.Add(Item("Edit on canvas…", "edit", () => BeginCanvasEdit(null)));
        }
        else Properties.Children.Add(Caption("Double-click an individual table cell or record field to edit its displayed text."));
        if (item.IsConnectable) Properties.Children.Add(Caption("Drag the connection handle to another node to add an edge."));
        if (item.CanEditConnection)
        {
            Properties.Children.Add(Caption("Drag an endpoint handle onto another node to reconnect a literal edge."));
            Properties.Children.Add(Item("Delete edge", "delete", () => Edit(() => doc.RemoveDotEdge(item), "Select a single literal edge to delete it.")));
        }
        else if (item.Location.Kind == "edge") Properties.Children.Add(Caption("This edge combines multiple source declarations or endpoints. Use Source to change its structure."));
    }

    private bool HandleDotGesture(DiagramGesture gesture)
    {
        if (gesture.From.Dot == null && gesture.To.Dot == null) return false;
        var from = gesture.From.Dot == null ? null : doc.OpenDotObject(gesture.From.Dot);
        var to = gesture.To.Dot == null ? null : doc.OpenDotObject(gesture.To.Dot);
        if (from == null || to == null) { Status("Choose two editable DOT objects.", true); return true; }
        if (gesture.Kind == "connect") Edit(() => doc.ConnectDot(from, to), "Choose two DOT nodes to connect.");
        else if (gesture.Kind is "retargetFrom" or "retargetTo")
            Edit(() => doc.RetargetDot(from, to, gesture.Kind == "retargetFrom"), "Choose a single literal edge and a node to reconnect it.");
        else Status("DOT layout follows its graph attributes and edge constraints.");
        return true;
    }
}
