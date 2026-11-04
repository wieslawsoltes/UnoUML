# Network structure and canvas authoring

The network inspector edits the topology of literal `@startnwdiag` diagrams. Networks, shared hosts, and each network-to-host interface have independent identities. Moving a shape on the canvas changes geometry; attaching or moving an interface changes topology.

## Inspector operations

Select a network rail or its caption to edit its description, subnet address, color, identifier, or order. **Insert network host** adds a new host and its first interface. The diagram inspector also provides **Insert network** and network/host pickers, including when the diagram is empty.

Select a host or interface address to access these operations:

| Operation | Result |
| --- | --- |
| Host properties | Changes the shared description, color, and shape across every attachment. |
| Rename host identifier | Renames every occurrence and remaps stored node and connector geometry. |
| Interface address | Changes only the selected network attachment, including a comma-separated address string. |
| Attach host to network | Adds an interface to another network while reusing the existing host. |
| Move interface to network | Moves one attachment and its attributes to another network. Other attachments survive. |
| Reorder interface | Changes source order within its current network. |
| Detach from this network | Removes one interface. A host with no remaining interfaces disappears. |
| Remove host everywhere | Removes every attachment of the selected host. |
| Manage host interfaces | Chooses an attachment explicitly when the host spans several networks. |

Network deletion removes that bus and its interfaces. A host connected to another bus remains. Deleting a canvas selection resolves node and edge identities to these same structural operations; it does not delete arbitrary source lines. Locked affected nodes and connections prevent deletion or detachment.

The native captions expose source fields. Descriptions and addresses edit their exact literal properties. Editing an alias caption invokes identifier rename, including geometry remapping and collision checks. Displayed labels and geometry therefore remain synchronized with the source editor.

Blank optional values in the property dialogs remove the corresponding property and restore its default. Duplicate network identifiers, duplicate host identifiers, duplicate attachments on one bus, malformed values, and stale edit handles are rejected without partial changes.

## Shared-host semantics

PlantUML network diagrams describe a host on several network lines, but its description, color, and shape belong to the shared host. Later property contributions take precedence. An address belongs to one interface.

Removing the last property contribution must not change the appearance of a surviving host accidentally. Reordering networks must not expose an obsolete color or description. Structural operations compare effective shared-host properties before and after the edit. When an operation would change those properties unintentionally, they materialize only the necessary values on a surviving declaration. The source and resulting visual state are committed as one undo operation.

For example, removing the `internal` network below retains the blue gateway on `dmz` by preserving its effective color:

```plantuml
@startnwdiag
network dmz {
  gateway [description="Gateway", color="red", address="192.0.2.1"];
}
network internal {
  gateway [color="blue", address="10.0.0.1"];
}
@endnwdiag
```

The parser recognizes both the optional `nwdiag { ... }` wrapper and the documented wrapperless form. Semicolon-delimited statements can share a line, and attribute lists can span lines. Quoted strings, optional semicolons at line endings, `//` comments, PlantUML comments, BOMs, and CR/LF/CRLF source conventions retain their original spans. Edits replace only the affected tokens or statements. Comments outside a removed declaration survive; comments inside a removed network belong to that removed block.

## Canvas identity and transactions

The core and native renderer compile the same bounded network syntax model. Identity derives from length-prefixed semantic components and a SHA-256 digest:

* A network bus uses its network alias in a bus-specific namespace.
* A host uses its alias in a separate host namespace.
* An interface uses its bus identity and host identity.

Line insertion, description changes, and reordering do not change these identities. Identifier rename updates the relevant identities and every affected stored endpoint in the same transaction. Dimensions, rotation, stacking, pins, locks, routes, ports, and label offsets remain attached to their owners.

Deletion prunes geometry for removed objects. Moving an interface to a different bus discards the old attachment's route, allowing the new attachment to route against its new endpoints. Routes whose stored identity and endpoints do not match the source model are not transferred to another connection. Malformed canvas metadata blocks a structural commit instead of being silently replaced.

The existing document history, browser draft, Save/Open, and export paths retain these operations. A dialog keeps a snapshot of the document it opened; changing documents or changing the source invalidates that handle.

## C# API

```csharp
using UnoUML.Core;

var document = new StudioDocument("""
    @startnwdiag
    network outside {
      gateway [address="192.0.2.1"];
    }
    network inside {
    }
    @endnwdiag
    """);

var diagram = document.OpenNetworkDiagram()!;
document.AttachNetworkHost(diagram.Hosts[0], diagram.Networks[1], "10.0.0.1");

// Handles are immutable snapshots; reopen after each successful mutation.
diagram = document.OpenNetworkDiagram()!;
document.SetNetworkHostProperties(
    diagram.Hosts[0], new NetworkHostProperties("Edge gateway", "#DDEEFF", "node"));

diagram = document.OpenNetworkDiagram()!;
document.RenameNetworkHost(diagram.Hosts[0], "edge_gateway");

document.Undo(); // Restores aliases and any authored canvas metadata together.
```

The public snapshot types are `NetworkDiagramEdit`, `NetworkEdit`, `NetworkHostEdit`, and `NetworkInterfaceEdit`. Other operations include `InsertNetwork`, `SetNetworkProperties`, `RenameNetwork`, `MoveNetwork`, `RemoveNetwork`, `InsertNetworkHost`, `SetNetworkInterfaceAddress`, `MoveNetworkInterface`, `ReorderNetworkInterface`, `DetachNetworkHost`, `RemoveNetworkHost`, and `RemoveNetworkSelection`.

## Native rendering and remaining boundaries

The native renderer now shares the author's parser, including wrapperless and compact syntax, quoted literals, comments, and named colors recognized by the managed color catalog. Empty diagrams retain a blank native canvas so that the inspector can insert the first network. Native host shapes remain rectangle, node, database, folder, and artifact. Other documented host shapes can be authored and use the compatibility renderer when necessary.

This structural model intentionally requires named literal networks and explicit host attachments. Groups, anonymous networks, peer-network links, standalone host declarations outside networks, generated macro declarations, unknown attributes, and ambiguous duplicate declarations remain source/compatibility operations. Sprite and rich-text descriptions retain their existing compatibility behavior. Network clipboard restructuring, grouping, and generic duplication use source workflows; the canvas prevents those generic commands from corrupting network references.

Parsing is bounded to 512,000 source characters, 65,536 lexical tokens, 220 networks, 1,024 shared hosts, and 4,096 interfaces. Existing canvas metadata limits apply separately. These bounds and explicit grammar validation prevent an incomplete topology from being edited as if it were complete.

Grammar reference: [PlantUML network diagrams](https://plantuml.com/nwdiag), including the optional wrapper, shared hosts, interface addresses, colors, descriptions, and shapes. See also [Canvas authoring](canvas-authoring.md) for manipulation, routing, layout, and geometry persistence.
