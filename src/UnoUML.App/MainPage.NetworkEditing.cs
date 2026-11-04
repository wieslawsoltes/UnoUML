using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private const string NetworkFailure = "The network source changed, an identifier is already used, or a locked object would be removed. Reopen the editor and check the selected network and host.";
    private static string? NetworkOptional(TextBox input) => input.Text.Length == 0 ? null : input.Text;
    private (string Source, int Revision, string Node, string Edge)? networkCanvasSelection;
    private bool networkSelectionFromOutline;

    private void CaptureNetworkCanvasSelection(DiagramHit hit)
    {
        networkCanvasSelection = null; networkSelectionFromOutline = false;
        if (doc.Page.Family != "Network" || hit.Generated || viewport.GetCanvasInfo() is not { } canvas) return;
        if (canvas.Nodes.Length + canvas.Edges.Length == 1)
            networkCanvasSelection = (doc.Source, revision, canvas.Nodes.FirstOrDefault() ?? "", canvas.Edges.FirstOrDefault() ?? "");
    }
    private void ClearNetworkCanvasSelection(bool fromOutline = true)
    {
        networkCanvasSelection = null; networkSelectionFromOutline = fromOutline;
    }
    private bool AddExactNetworkCanvasActions(NetworkDiagramEdit diagram)
    {
        if (networkCanvasSelection is not { } selection || selection.Revision != revision || !ReferenceEquals(selection.Source, doc.Source)) return false;
        if (diagram.Networks.FirstOrDefault(n => n.CanvasId == selection.Node) is { } network) { AddNetworkActions(network); return true; }
        if (diagram.Hosts.FirstOrDefault(h => h.CanvasId == selection.Node) is { } host) { AddNetworkHostActions(host); return true; }
        if (diagram.Interfaces.FirstOrDefault(i => i.CanvasId == selection.Edge) is { } connection) { AddNetworkInterfaceActions(connection); return true; }
        return false;
    }

    private void AddNetworkDiagramActions()
    {
        if (doc.OpenNetworkDiagram() is not { } network) return;
        Properties.Children.Add(Caption($"{network.Networks.Count} networks · {network.Hosts.Count} shared hosts · {network.Interfaces.Count} interfaces"));
        Properties.Children.Add(Item("Insert network…", "plus", () => _ = InsertNetworkAsync(network)));
        if (network.Networks.Count > 0)
        {
            var buses = StructuredPicker("Network", network.Networks.Select(n => n.Properties.Description ?? n.Alias), 0);
            Properties.Children.Add(buses);
            Properties.Children.Add(Item("Network properties…", "edit", () => _ = EditNetworkAsync(network.Networks[buses.SelectedIndex])));
            Properties.Children.Add(Item("Insert network host…", "plus", () => _ = InsertNetworkHostAsync(network.Networks[buses.SelectedIndex])));
            Properties.Children.Add(Item("Rename network identifier…", "edit", () => _ = RenameNetworkAsync(network.Networks[buses.SelectedIndex])));
            Properties.Children.Add(Item("Reorder network…", "up", () => _ = MoveNetworkAsync(network.Networks[buses.SelectedIndex])));
            Properties.Children.Add(Item("Remove network", "delete", () => Edit(() => doc.RemoveNetwork(network.Networks[buses.SelectedIndex]), NetworkFailure)));
        }
        if (network.Hosts.Count > 0)
        {
            var hosts = StructuredPicker("Shared host", network.Hosts.Select(h => h.Properties.Description is { } description ? description + " · " + h.Alias : h.Alias), 0);
            Properties.Children.Add(hosts);
            Properties.Children.Add(Item("Host properties…", "edit", () => _ = EditNetworkHostAsync(network.Hosts[hosts.SelectedIndex])));
            Properties.Children.Add(Item("Rename host identifier…", "edit", () => _ = RenameNetworkHostAsync(network.Hosts[hosts.SelectedIndex])));
            Properties.Children.Add(Item("Attach host to network…", "connect", () => _ = AttachNetworkHostAsync(network.Hosts[hosts.SelectedIndex])));
            Properties.Children.Add(Item("Manage host interfaces…", "connect", () => _ = ChooseNetworkInterfaceAsync(network.Hosts[hosts.SelectedIndex])));
            Properties.Children.Add(Item("Remove host everywhere", "delete", () => Edit(() => doc.RemoveNetworkHost(network.Hosts[hosts.SelectedIndex]), NetworkFailure)));
        }
    }

    private void AddNetworkObjectActions(DiagramObject item)
    {
        if (doc.OpenNetworkDiagram() is not { } network) return;
        if (AddExactNetworkCanvasActions(network)) return;
        // Several networks and hosts may share one physical source line. An
        // outline row cannot identify one of them: expose the explicit pickers.
        var interfaces = network.Interfaces.Where(i => i.Line == item.Line || i.Offset <= item.Offset && i.EndOffset >= item.End).ToArray();
        var buses = network.Networks.Where(n => n.Line == item.Line).ToArray();
        if (interfaces.Length == 1 && buses.Length == 0) AddNetworkInterfaceActions(interfaces[0]);
        else if (buses.Length == 1 && interfaces.Length == 0) AddNetworkActions(buses[0]);
        else if (interfaces.Length == 0 && buses.Length == 0 && network.Networks.SingleOrDefault(n => n.Offset <= item.Offset && n.EndOffset >= item.End) is { } owner) AddNetworkActions(owner);
        else AddNetworkDiagramActions();
    }
    private void AddNetworkFieldActions(SourceFieldEdit field)
    {
        if (doc.OpenNetworkDiagram() is not { } network) return;
        if (AddExactNetworkCanvasActions(network)) return;
        if (network.Interfaces.FirstOrDefault(i => i.Offset <= field.Offset && i.EndOffset >= field.Offset + field.Length) is { } connection) AddNetworkInterfaceActions(connection);
        else if (network.Networks.FirstOrDefault(n => n.Offset <= field.Offset && n.EndOffset >= field.Offset + field.Length) is { } bus) AddNetworkActions(bus);
    }
    private void AddNetworkActions(NetworkEdit network)
    {
        Properties.Children.Add(Caption($"Network {network.Alias} · {network.Interfaces.Count} interfaces"));
        Properties.Children.Add(Item("Network properties…", "edit", () => _ = EditNetworkAsync(network)));
        Properties.Children.Add(Item("Rename network identifier…", "edit", () => _ = RenameNetworkAsync(network)));
        Properties.Children.Add(Item("Insert network host…", "plus", () => _ = InsertNetworkHostAsync(network)));
        Properties.Children.Add(Item("Reorder network…", "up", () => _ = MoveNetworkAsync(network)));
        Properties.Children.Add(Item("Remove network", "delete", () => Edit(() => doc.RemoveNetwork(network), NetworkFailure)));
    }
    private void AddNetworkInterfaceActions(NetworkInterfaceEdit connection)
    {
        Properties.Children.Add(Caption($"Host {connection.Host.Alias} · network {connection.Network.Alias}"));
        Properties.Children.Add(Item("Interface address…", "edit", () => _ = EditNetworkInterfaceAsync(connection)));
        Properties.Children.Add(Item("Move interface to network…", "connect", () => _ = MoveNetworkInterfaceAsync(connection)));
        Properties.Children.Add(Item("Reorder interface…", "up", () => _ = ReorderNetworkInterfaceAsync(connection)));
        Properties.Children.Add(Item("Detach from this network", "delete", () => Edit(() => doc.DetachNetworkHost(connection), NetworkFailure)));
        AddNetworkHostActions(connection.Host);
    }
    private void AddNetworkHostActions(NetworkHostEdit host)
    {
        Properties.Children.Add(Caption($"Shared host {host.Alias} · {host.Interfaces.Count} interfaces"));
        Properties.Children.Add(Item("Host properties…", "edit", () => _ = EditNetworkHostAsync(host)));
        Properties.Children.Add(Item("Rename host identifier…", "edit", () => _ = RenameNetworkHostAsync(host)));
        Properties.Children.Add(Item("Attach host to network…", "connect", () => _ = AttachNetworkHostAsync(host)));
        Properties.Children.Add(Item("Manage host interfaces…", "connect", () => _ = ChooseNetworkInterfaceAsync(host)));
        Properties.Children.Add(Item("Remove host everywhere", "delete", () => Edit(() => doc.RemoveNetworkHost(host), NetworkFailure)));
    }

    private async Task InsertNetworkAsync(NetworkDiagramEdit network)
    {
        var document = doc; string name = "network1";
        for (int i = 2; network.Networks.Any(n => n.Alias == name); i++) name = "network" + i;
        TextBox alias = StructuredTextInput("Network identifier", name), description = StructuredTextInput("Network description"), address = StructuredTextInput("Network address");
        var position = StructuredPicker("Insert position", network.Networks.Select(n => "Before " + n.Alias).Append("After last network"), network.Networks.Count);
        var fields = NetworkFields(alias, description, address, position);
        if (await AskAsync("Insert network", fields, "Insert") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.InsertNetwork(network, position.SelectedIndex, alias.Text, new(NetworkOptional(description), NetworkOptional(address))), NetworkFailure);
    }
    private async Task EditNetworkAsync(NetworkEdit network)
    {
        var document = doc; var current = network.Properties;
        TextBox description = StructuredTextInput("Network description", current.Description ?? ""), address = StructuredTextInput("Network address", current.Address ?? ""), color = StructuredTextInput("Network color", current.Color ?? "");
        var fields = NetworkFields(description, address, color);
        fields.Children.Add(Caption("Blank values restore the network defaults. Use a named color or a hex color such as #AAB6CE."));
        if (await AskAsync("Network properties", fields, "Apply") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.SetNetworkProperties(network, new(NetworkOptional(description), NetworkOptional(address), NetworkOptional(color))), NetworkFailure);
    }
    private async Task RenameNetworkAsync(NetworkEdit network)
    {
        var document = doc; var alias = StructuredTextInput("Network identifier", network.Alias);
        if (await AskAsync("Rename network identifier", alias, "Rename") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.RenameNetwork(network, alias.Text), NetworkFailure);
    }
    private async Task MoveNetworkAsync(NetworkEdit network)
    {
        var document = doc; var position = StructuredPicker("Final position", network.Diagram.Networks.Select((n, i) => $"{i + 1}: {n.Alias}"), network.Index);
        if (await AskAsync("Reorder network", position, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.MoveNetwork(network, position.SelectedIndex), NetworkFailure);
    }
    private async Task InsertNetworkHostAsync(NetworkEdit network)
    {
        var document = doc; string name = "host1";
        for (int i = 2; network.Diagram.Hosts.Any(h => h.Alias == name); i++) name = "host" + i;
        TextBox alias = StructuredTextInput("Host identifier", name), description = StructuredTextInput("Host description"), address = StructuredTextInput("Interface address");
        if (await AskAsync("Insert network host", NetworkFields(alias, description, address), "Insert") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.InsertNetworkHost(network, alias.Text, NetworkOptional(address), new(NetworkOptional(description))), NetworkFailure);
    }
    private async Task EditNetworkHostAsync(NetworkHostEdit host)
    {
        var document = doc; var current = host.Properties;
        TextBox description = StructuredTextInput("Host description", current.Description ?? ""), color = StructuredTextInput("Host color", current.Color ?? "");
        string[] shapes = ["Default", "rectangle", "node", "database", "folder", "artifact", "cloud", "actor", "agent", "boundary", "card", "collections", "component", "control", "entity", "file", "frame", "hexagon", "interface", "label", "package", "person", "queue", "stack", "storage", "usecase"];
        var shape = StructuredPicker("Host shape", shapes, Math.Max(0, Array.IndexOf(shapes, current.Shape)));
        var fields = NetworkFields(description, color, shape);
        fields.Children.Add(Caption("These properties belong to the shared host and apply on every network. Blank values restore defaults."));
        if (await AskAsync("Host properties", fields, "Apply") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.SetNetworkHostProperties(host, new(NetworkOptional(description), NetworkOptional(color), shape.SelectedIndex > 0 ? shapes[shape.SelectedIndex] : null)), NetworkFailure);
    }
    private async Task RenameNetworkHostAsync(NetworkHostEdit host)
    {
        var document = doc; var alias = StructuredTextInput("Host identifier", host.Alias);
        var fields = NetworkFields(alias); fields.Children.Add(Caption("Every attachment is renamed, and saved geometry and connector routes are retained."));
        if (await AskAsync("Rename host identifier", fields, "Rename") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.RenameNetworkHost(host, alias.Text), NetworkFailure);
    }
    private async Task AttachNetworkHostAsync(NetworkHostEdit host)
    {
        var document = doc; var destinations = host.Diagram.Networks.Where(n => !host.Interfaces.Any(i => i.Network.Alias == n.Alias)).ToArray();
        if (destinations.Length == 0) { Status("This host is already attached to every network. Insert another network first."); return; }
        var destination = StructuredPicker("Destination network", destinations.Select(n => n.Alias), 0); var address = StructuredTextInput("Interface address");
        if (await AskAsync("Attach host to network", NetworkFields(destination, address), "Attach") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.AttachNetworkHost(host, destinations[destination.SelectedIndex], NetworkOptional(address)), NetworkFailure);
    }
    private async Task EditNetworkInterfaceAsync(NetworkInterfaceEdit connection)
    {
        var document = doc; var address = StructuredTextInput("Interface address", connection.Address ?? "");
        if (await AskAsync("Interface address", address, "Apply") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.SetNetworkInterfaceAddress(connection, NetworkOptional(address)), NetworkFailure);
    }
    private async Task MoveNetworkInterfaceAsync(NetworkInterfaceEdit connection)
    {
        var document = doc; var destinations = connection.Diagram.Networks.Where(n => !connection.Host.Interfaces.Any(i => i.Network.Alias == n.Alias)).ToArray();
        if (destinations.Length == 0) { Status("No unattached network is available for this host."); return; }
        var destination = StructuredPicker("Destination network", destinations.Select(n => n.Alias), 0);
        if (await AskAsync("Move interface to network", destination, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.MoveNetworkInterface(connection, destinations[destination.SelectedIndex]), NetworkFailure);
    }
    private async Task ReorderNetworkInterfaceAsync(NetworkInterfaceEdit connection)
    {
        var document = doc; var interfaces = connection.Network.Interfaces;
        var position = StructuredPicker("Final position", interfaces.Select((i, index) => $"{index + 1}: {i.Host.Alias}"), interfaces.ToList().FindIndex(i => i.CanvasId == connection.CanvasId));
        if (await AskAsync("Reorder interface", position, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.ReorderNetworkInterface(connection, position.SelectedIndex), NetworkFailure);
    }
    private async Task ChooseNetworkInterfaceAsync(NetworkHostEdit host)
    {
        var document = doc; var interfaces = host.Interfaces;
        var choice = StructuredPicker("Host interface", interfaces.Select(i => i.Network.Alias + (i.Address == null ? "" : " · " + i.Address)), 0);
        string[] operations = ["Edit address", "Move to another network", "Detach"];
        var operation = StructuredPicker("Interface operation", operations, 0);
        if (await AskAsync("Manage host interfaces", NetworkFields(choice, operation), "Continue") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        var connection = interfaces[choice.SelectedIndex];
        if (operation.SelectedIndex == 0) await EditNetworkInterfaceAsync(connection);
        else if (operation.SelectedIndex == 1) await MoveNetworkInterfaceAsync(connection);
        else Edit(() => doc.DetachNetworkHost(connection), NetworkFailure);
    }
    private static StackPanel NetworkFields(params Microsoft.UI.Xaml.UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private bool TryHandleNetworkCanvasCommand(string action, string text)
    {
        if (doc.Page.Family != "Network") return false;
        if (doc.OpenNetworkDiagram() is not { } network)
        {
            Status("Use Source for this network construct; its references cannot be edited as literal networks and hosts.", true); return true;
        }
        if (action == "delete")
        {
            if (!networkSelectionFromOutline && viewport.GetCanvasInfo() is { } canvas && (canvas.Nodes.Length > 0 || canvas.Edges.Length > 0))
                Edit(() => doc.RemoveNetworkSelection(network, canvas.Nodes, canvas.Edges), NetworkFailure);
            else if (FieldSelection is { } field && network.Interfaces.FirstOrDefault(i => i.Offset <= field.Offset && i.EndOffset >= field.Offset + field.Length) is { } connection)
                Edit(() => doc.DetachNetworkHost(connection), NetworkFailure);
            else if (selected is { } item)
            {
                var attachments = network.Interfaces.Where(i => i.Line == item.Line).ToArray();
                var buses = network.Networks.Where(n => n.Line == item.Line).ToArray();
                if (attachments.Length == 1 && buses.Length == 0) Edit(() => doc.RemoveNetworkHost(attachments[0].Host), NetworkFailure);
                else if (buses.Length == 1 && attachments.Length == 0) Edit(() => doc.RemoveNetwork(buses[0]), NetworkFailure);
                else Status("This source line contains several network objects. Select the exact shape or interface on the canvas.");
            }
            return true;
        }
        Status("Use the network inspector to insert hosts, attach interfaces, rename identifiers, and reorder networks. Source remains available for clipboard restructuring.");
        return true;
    }
}
