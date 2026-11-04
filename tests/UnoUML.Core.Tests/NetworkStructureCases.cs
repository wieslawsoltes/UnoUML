using System.Xml.Linq;
using UnoUML.Core;
using UnoUML.PlantUml;

internal static class NetworkStructureCases
{
    internal static int Run()
    {
        int assertions = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("Network structure: " + message); assertions++; }
        void Atomic(StudioDocument document, string before, string message)
        {
            string after = document.Source;
            Check(document.Undo() && document.Source == before, message + " exact undo");
            Check(document.Redo() && document.Source == after, message + " exact redo");
        }
        XElement Render(string source)
        {
            Check(PlantUmlRenderer.TryRender(source, out var rendered), "native render accepts authored network");
            return XElement.Parse(rendered!.Svg);
        }
        const string body = """
            @startnwdiag topology
            nwdiag {
              network dmz {
                address = "192.0.2.0/24"; // subnet comment
                shared [address=".1", description="Gateway", color="#112233"];
                client [address=".2"]; // client comment
              }
              ' independent comment
              network lan {
                shared [address="10.0.0.1", color="#445566"];
                db [shape=database];
              }
              network spare {
              }
            }
            @endnwdiag
            """;
        foreach (string nl in new[] { "\n", "\r\n", "\r" })
        foreach (bool wrapped in new[] { true, false })
        {
            string page = body.ReplaceLineEndings(nl);
            if (!wrapped) page = page.Replace("nwdiag {" + nl, "", StringComparison.Ordinal).Replace("}" + nl + "@endnwdiag", "@endnwdiag", StringComparison.Ordinal);
            string prefix = "\uFEFF@startuml prior\nclass Untouched\n@enduml\n";
            var doc = new StudioDocument(prefix + page) { ActivePage = 1 };
            var network = doc.OpenNetworkDiagram()!;
            Check(network.Networks.Count == 3 && network.Hosts.Count == 3 && network.Interfaces.Count == 4, "networks, shared hosts and interfaces indexed independently");
            Check(network.Hosts[0].Properties == new NetworkHostProperties("Gateway", "#445566"), "shared host attributes use last property contribution");
            var native = Render(page);
            Check(native.Descendants().Count(e => (string?)e.Attribute("class") == "entity") == 3, "one shape per shared host");
            foreach (var bus in network.Networks) Check(native.Descendants().Any(e => (string?)e.Attribute("data-canvas-id") == bus.CanvasId), "bus identity matches authoring model");
            foreach (var host in network.Hosts) Check(native.Descendants().Any(e => (string?)e.Attribute("data-canvas-id") == host.CanvasId), "host identity matches authoring model");
            foreach (var edge in network.Interfaces) Check(native.Descendants().Any(e => (string?)e.Attribute("data-canvas-id") == edge.CanvasId), "interface identity matches authoring model");

            Check(doc.InsertNetwork(network, 1, "storage", new("Storage", "172.16.0.0/24", "#aabbcc")), "insert network");
            Check(doc.OpenNetworkDiagram()!.Networks.Select(n => n.Alias).SequenceEqual(new[] { "dmz", "storage", "lan", "spare" }), "inserted network order");
            Check(doc.Source.StartsWith(prefix, StringComparison.Ordinal) && doc.Source.Contains("// subnet comment", StringComparison.Ordinal), "page prefix and comments preserved");
            Check(!doc.InsertNetwork(network, 0, "stale"), "stale snapshot rejected");
            Atomic(doc, prefix + page, "insert network"); doc.Undo();

            network = doc.OpenNetworkDiagram()!; string before = doc.Source;
            Check(doc.RenameNetworkHost(network.Hosts[0], "gateway"), "rename shared host");
            Check(doc.Source == before.Replace("shared [", "gateway [", StringComparison.Ordinal), "rename changes all aliases and no unrelated text");
            Check(doc.OpenNetworkDiagram()!.Hosts[0].Interfaces.Count == 2, "renamed shared host retains all interfaces");
            Atomic(doc, before, "rename host"); doc.Undo();
            network = doc.OpenNetworkDiagram()!;
            Check(!doc.RenameNetworkHost(network.Hosts[0], "client") && !doc.RenameNetworkHost(network.Hosts[0], "x; injected"), "collision and syntax injection rejected");
            Check(doc.RenameNetwork(network.Networks[0], "public_net"), "rename network");
            Check(doc.Source == before.Replace("network dmz", "network public_net", StringComparison.Ordinal), "network rename localized");
            Atomic(doc, before, "rename network"); doc.Undo();

            network = doc.OpenNetworkDiagram()!;
            Check(doc.InsertNetworkHost(network.Networks[2], "cache", "172.16.0.10", new("Cache", "lightblue", "node")), "insert host with properties");
            var created = doc.OpenNetworkDiagram()!;
            Check(created.Hosts.Single(h => h.Alias == "cache").Interfaces.Single().Address == "172.16.0.10", "new interface address");
            Check(Render(doc.Page.Source).Descendants().Any(e => (string?)e.Attribute("data-qualified-name") == "cache"), "named colors render natively");
            Atomic(doc, before, "insert host"); doc.Undo();
            network = doc.OpenNetworkDiagram()!;
            Check(doc.AttachNetworkHost(network.Hosts[0], network.Networks[2], "172.16.0.1"), "attach shared host to third bus");
            created = doc.OpenNetworkDiagram()!;
            Check(created.Hosts.Count == 3 && created.Hosts[0].Interfaces.Count == 3 && created.Interfaces.Count == 5, "attachment reuses host identity");
            Check(!doc.AttachNetworkHost(created.Hosts[0], created.Networks[2], "duplicate"), "duplicate attachment rejected atomically");
            Atomic(doc, before, "attach host"); doc.Undo();

            network = doc.OpenNetworkDiagram()!;
            Check(doc.MoveNetworkInterface(network.Interfaces[0], network.Networks[2]), "move interface to another bus");
            created = doc.OpenNetworkDiagram()!;
            Check(created.Networks[0].Interfaces.All(i => i.Host.Alias != "shared") && created.Networks[2].Interfaces.Single().Address == ".1", "interface moved with its address");
            Check(created.Hosts.Single(h => h.Alias == "shared").Properties == new NetworkHostProperties("Gateway", "#445566"), "moving earlier override does not change shared host appearance");
            Atomic(doc, before, "move interface"); doc.Undo();

            network = doc.OpenNetworkDiagram()!;
            Check(doc.MoveNetwork(network.Networks[0], 2), "reorder whole bus block");
            created = doc.OpenNetworkDiagram()!;
            Check(created.Networks.Select(n => n.Alias).SequenceEqual(new[] { "lan", "spare", "dmz" }), "network final position");
            Check(created.Hosts.Single(h => h.Alias == "shared").Properties.Color == "#445566", "network reorder preserves effective shared-host color");
            Check(doc.Source.Contains("// subnet comment", StringComparison.Ordinal) && doc.Source.Contains("' independent comment", StringComparison.Ordinal), "block move retains comments");
            Atomic(doc, before, "reorder network"); doc.Undo();

            network = doc.OpenNetworkDiagram()!;
            Check(doc.DetachNetworkHost(network.Hosts[0].Interfaces[1]), "detach last overriding interface");
            created = doc.OpenNetworkDiagram()!;
            Check(created.Hosts[0].Properties.Color == "#445566" && created.Hosts[0].Interfaces.Count == 1, "detach materializes shared properties on surviving declaration");
            Check(created.Networks[1].Interfaces.Single().Host.Alias == "db", "other host on bus retained");
            Atomic(doc, before, "detach interface"); doc.Undo();

            network = doc.OpenNetworkDiagram()!;
            Check(doc.RemoveNetwork(network.Networks[1]), "remove bus");
            created = doc.OpenNetworkDiagram()!;
            Check(created.Hosts.Count == 2 && created.Hosts[0].Properties.Color == "#445566" && created.Networks.Count == 2, "bus deletion prunes orphan host and preserves shared host");
            Atomic(doc, before, "remove bus"); doc.Undo();
            network = doc.OpenNetworkDiagram()!;
            Check(doc.RemoveNetworkHost(network.Hosts[0]), "remove shared host everywhere");
            Check(doc.OpenNetworkDiagram()!.Interfaces.Count == 2 && !doc.Source.Contains("shared [", StringComparison.Ordinal), "all host attachments removed");
            Atomic(doc, before, "remove host"); doc.Undo();

            network = doc.OpenNetworkDiagram()!;
            Check(doc.SetNetworkProperties(network.Networks[0], new("Internet", "203.0.113.0/24", "#abcdef")), "edit bus properties");
            Check(doc.Source.Contains("address = \"203.0.113.0/24\"; // subnet comment", StringComparison.Ordinal), "network property edit retains original spacing and comment");
            Atomic(doc, before, "network properties"); doc.Undo();
            network = doc.OpenNetworkDiagram()!;
            Check(doc.SetNetworkInterfaceAddress(network.Interfaces[1], "203.0.113.20, 203.0.113.21"), "multiple interface addresses");
            Check(doc.Source == before.Replace("address=\".2\"", "address=\"203.0.113.20, 203.0.113.21\"", StringComparison.Ordinal), "interface address edit localized");
            Atomic(doc, before, "interface properties"); doc.Undo();
            network = doc.OpenNetworkDiagram()!;
            Check(doc.ReorderNetworkInterface(network.Interfaces[1], 0), "reorder interfaces");
            Check(doc.OpenNetworkDiagram()!.Networks[0].Interfaces[0].Host.Alias == "client", "interface reordered within its own bus");
            Check(doc.Source.Contains("// client comment", StringComparison.Ordinal), "interface reordering retains inline comment");
            Atomic(doc, before, "reorder interfaces");
        }

        const string compact = "@startnwdiag\nnwdiag { network dmz { a [address=\"one\", description=\"A\", color=red, shape=node]; b; } network lan { a; } }\n@endnwdiag";
        for (int mask = 0; mask < 8; mask++)
        {
            var doc = new StudioDocument(compact);
            var host = doc.OpenNetworkDiagram()!.Hosts[0];
            var properties = new NetworkHostProperties((mask & 1) != 0 ? "A" : null, (mask & 2) != 0 ? "red" : null, (mask & 4) != 0 ? "node" : null);
            bool changed = doc.SetNetworkHostProperties(host, properties);
            Check(changed == (mask != 7), "no-op host properties and all attribute removal combinations");
            Check(doc.OpenNetworkDiagram()!.Hosts[0].Properties == properties, "attribute separators remain valid after every removal combination");
            Check(doc.OpenNetworkDiagram()!.Interfaces[0].Address == "one", "host properties never change per-interface address");
            if (changed) Atomic(doc, compact, "compact attribute edit");
        }
        {
            var doc = new StudioDocument("\uFEFF" + compact); var diagram = doc.OpenNetworkDiagram()!;
            var host = diagram.Hosts[0]; var network = diagram.Networks[0];
            var nodes = diagram.Networks.Select((n, i) => new CanvasNode(n.CanvasId, i * 200, 0, 180, 8, Pinned: true))
                .Concat(diagram.Hosts.Select((h, i) => new CanvasNode(h.CanvasId, i * 160, 100, 100, 50, Rotation: 15, ZIndex: 2))).ToArray();
            var edges = diagram.Interfaces.Select(i => new CanvasEdge(i.CanvasId, i.Network.CanvasId, i.Host.CanvasId,
                [new(20, 20), new(90, 20), new(90, 120)], "polyline", true, SourcePort: new(.25, .5), TargetPort: new(.5, 0), LabelOffset: new(6, -4))).ToArray();
            Check(doc.SetCanvasLayout(new(nodes, edges)), "seed authored network geometry");
            string before = doc.Source; diagram = doc.OpenNetworkDiagram()!;
            Check(doc.RenameNetworkHost(diagram.Hosts[0], "renamed"), "rename host with stored canvas state");
            Check(doc.TryGetCanvasLayout(out var layout, out _) && layout!.Nodes.Any(n => n.Id == doc.OpenNetworkDiagram()!.Hosts[0].CanvasId && n.Rotation == 15 && n.ZIndex == 2), "renamed node retains exact geometry and stacking");
            Check(layout!.Edges.Count == 3 && layout.Edges.All(e => e.Manual && e.SourcePort == new CanvasPoint(.25, .5) && e.LabelOffset == new CanvasPoint(6, -4)), "host rename preserves every manual route and port");
            Check(layout.Edges.All(e => e.Target != host.CanvasId), "old host endpoint identity removed");
            Atomic(doc, before, "host identity and geometry"); doc.Undo();
            diagram = doc.OpenNetworkDiagram()!;
            Check(doc.RenameNetwork(diagram.Networks[0], "public"), "rename bus with stored geometry");
            Check(doc.TryGetCanvasLayout(out layout, out _) && layout!.Nodes.All(n => n.Id != network.CanvasId) && layout.Edges.All(e => e.Source != network.CanvasId), "bus rename remaps all affected endpoints");
            Atomic(doc, before, "bus identity and geometry"); doc.Undo();
            diagram = doc.OpenNetworkDiagram()!;
            Check(doc.DetachNetworkHost(diagram.Interfaces[0]), "detach prunes stored edge geometry");
            Check(doc.TryGetCanvasLayout(out layout, out _) && layout!.Edges.Count == 2 && layout.Nodes.Count == 4, "surviving shared host geometry retained");
            Atomic(doc, before, "detach with geometry"); doc.Undo();
            diagram = doc.OpenNetworkDiagram()!;
            Check(doc.SetCanvasLayout(new(nodes, edges.Select((e, i) => i == 0 ? e with { Locked = true } : e).ToArray())), "lock interface");
            before = doc.Source; diagram = doc.OpenNetworkDiagram()!;
            Check(!doc.RemoveNetwork(diagram.Networks[0]) && !doc.RemoveNetworkHost(diagram.Hosts[0]) && !doc.DetachNetworkHost(diagram.Interfaces[0]) && doc.Source == before, "locked incident edge guards structural deletion");
            Check(doc.RenameNetworkHost(diagram.Hosts[0], "safeRename"), "locked identity may be renamed without changing geometry");
            var renamedDiagram = doc.OpenNetworkDiagram()!;
            string lockedId = renamedDiagram.Interfaces.Single(i => i.Network.Alias == "dmz" && i.Host.Alias == "safeRename").CanvasId;
            Check(doc.TryGetCanvasLayout(out layout, out _) && layout!.Edges.Single(e => e.Id == lockedId).Locked && layout.Edges.Count(e => e.Locked) == 1, "rename retains the lock on its exact interface after canonical ID sorting");
            Atomic(doc, before, "locked interface rename");
        }
        {
            var doc = new StudioDocument(body); var native = Render(doc.Page.Source);
            var editable = native.Descendants().Where(e => e.Attribute("data-edit-start") != null).ToArray();
            Check(editable.Length >= 10, "native network aliases, descriptions and interface addresses expose typed fields");
            foreach (var element in editable)
                Check(doc.OpenSourceField((int)element.Attribute("data-edit-start")!, (int)element.Attribute("data-edit-length")!, (string)element.Attribute("data-edit-kind")!) != null, "native token range opens in source editor");
            var description = editable.Single(e => e.Value == "Gateway");
            var field = doc.OpenSourceField((int)description.Attribute("data-edit-start")!, (int)description.Attribute("data-edit-length")!, (string)description.Attribute("data-edit-kind")!)!;
            Check(doc.ApplySourceField(field, "Edge gateway"), "native canvas description edits shared host through structural model");
            Check(doc.OpenNetworkDiagram()!.Hosts[0].Properties.Description == "Edge gateway", "direct label updates correct global property");
            Atomic(doc, body, "direct network field"); doc.Undo();
            native = Render(doc.Page.Source);
            var bus = native.Descendants().Single(e => e.Attribute("data-edit-start") != null && e.Value == "dmz");
            field = doc.OpenSourceField((int)bus.Attribute("data-edit-start")!, (int)bus.Attribute("data-edit-length")!, (string)bus.Attribute("data-edit-kind")!)!;
            Check(!doc.ApplySourceField(field, "bad alias"), "direct identifier injection rejected");
            Check(doc.ApplySourceField(field, "outside"), "direct bus alias rename supported");
            Atomic(doc, body, "direct bus alias");
        }
        {
            var empty = new StudioDocument("@startnwdiag\nnwdiag {\n}\n@endnwdiag");
            Check(empty.OpenNetworkDiagram() is { Networks.Count: 0 } && PlantUmlRenderer.TryRender(empty.Source, out _), "empty network remains editable and renderable");
            Check(empty.InsertNetwork(empty.OpenNetworkDiagram()!, 0, "first"), "insert into empty diagram");
            Check(empty.RemoveNetwork(empty.OpenNetworkDiagram()!.Networks[0]) && empty.OpenNetworkDiagram()!.Networks.Count == 0, "remove final bus");
        }
        foreach (string invalid in new[]
        {
            "network A { a; a; }", "network A { a [color=red, color=blue]; }", "network A { a [unknown=1]; }",
            "network A { a [shape=unknown]; }", "network A { a [color=\"url(evil)\"]; }", "network A { a; } network A { b; }",
            "network A { group X { a; } }", "a -- b;", "!include <generated>\nnetwork A { a; }", "network A { a [description=\"unterminated]; }"
        }) Check(new StudioDocument("@startnwdiag\n" + invalid + "\n@endnwdiag").OpenNetworkDiagram() == null, "unsupported or ambiguous network syntax rejected without partial model");
        {
            var doc = new StudioDocument("' @unouml:canvas {bad}\n" + body);
            string before = doc.Source;
            Check(!doc.RenameNetworkHost(doc.OpenNetworkDiagram()!.Hosts[0], "other") && doc.Source == before && !doc.CanUndo, "malformed geometry prevents partial structural commit");
        }
        Console.WriteLine($"PASS {assertions} network structural authoring checks.");
        return assertions;
    }
}
