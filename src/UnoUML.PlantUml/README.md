# UnoUML.PlantUml

A dependency-free .NET 10 library that parses one PlantUML diagram, lays it out and renders SVG. It runs in a .NET process and in WebAssembly. Its source-map attributes let the UnoUML designer associate visible shapes, edges, members and messages with their original statements.

```csharp
using UnoUML.PlantUml;

const string source = """
    @startuml
    actor Customer
    participant Checkout
    Customer -> Checkout : Place order
    Checkout --> Customer : Confirmed
    @enduml
    """;

if (PlantUmlRenderer.TryRender(source, out var diagram))
{
    File.WriteAllText("checkout.svg", diagram.Svg);
    Console.WriteLine($"Rendered {diagram.Kind}");
}
```

Reference the project directly or download the package into a local NuGet source. [UnoUML.PlantUml.0.1.0.nupkg](https://wieslawsoltes.github.io/UnoUML/packages/UnoUML.PlantUml.0.1.0.nupkg)

```sh
dotnet add package UnoUML.PlantUml --version 0.1.0 --source ./packages
```

Build a local package with `dotnet pack src/UnoUML.PlantUml -c Release`.

## Rendering contract

Pass a single, complete `@start…` / `@end…` block. The method returns a typed diagram and a complete SVG document on success. An unrecognized statement, unsupported dialect or layout that exceeds a managed limit returns `false`. A host can use the official PlantUML renderer at that boundary. The library performs no network access.

The native grammars cover common sequence, structural UML, state, activity, timing, chart, Gantt, network, Salt wireframes, mind map, WBS, packet, file tree, JSON, block YAML, DOT, Creole, EBNF and regex diagrams. Built-in C4 context and ArchiMate calls have source-mapped native renderers for the supported standard-library signatures. Macros, standard-library expansion and advanced styling remain the responsibility of the host's reference renderer. Geometry and typography can differ between implementations. The output includes a viewBox, dimensions, escaped user text, zero-based `data-source-line` attributes and qualified identifiers for addressable UML objects.

The sequence engine measures lanes and message spans before emitting SVG. Structural diagrams lay out nested containers, assign ranks and route connectors. Hierarchies use iterative subtree packing. The JSON renderer streams tokens directly into field tables and tracks source lines across UTF-8 input. No diagram engine or browser process needs to be started for a managed render.
