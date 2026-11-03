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

Reference the project directly or download the package into a local NuGet source. [UnoUML.PlantUml.0.2.0.nupkg](https://wieslawsoltes.github.io/UnoUML/packages/UnoUML.PlantUml.0.2.0.nupkg)

```sh
dotnet add package UnoUML.PlantUml --version 0.2.0 --source ./packages
```

Build a local package with `dotnet pack src/UnoUML.PlantUml -c Release`.

## Rendering contract

Pass a single, complete `@start…` / `@end…` block. The method returns a typed diagram and a complete SVG document on success. An unrecognized statement, unsupported dialect or layout that exceeds a managed limit returns `false`. A host can use the official PlantUML renderer at that boundary. The library performs no network access.

The native grammars cover common sequence, structural UML, state, activity, timing, chart, Gantt, network, Salt wireframes, mind map, WBS, packet, file tree, JSON, block YAML, DOT, Creole, EBNF and regex diagrams. Chen ER, Ditaa, AsciiMath and LaTeX are rendered as vector diagrams. Built-in C4 Context, Container, Component, Deployment and ArchiMate calls have source-mapped native renderers for the supported standard-library signatures. Other library definitions, macros and styles outside the managed grammar can be delegated to the reference renderer. Geometry and typography can differ between implementations. The output includes a viewBox, dimensions, escaped user text, zero-based `data-source-line` attributes and qualified identifiers for addressable UML objects.

The sequence engine measures lanes and message spans before emitting SVG. Structural diagrams lay out nested containers, assign ranks and route connectors. Hierarchies use iterative subtree packing. The JSON renderer streams tokens directly into field tables and tracks source lines across UTF-8 input. No diagram engine or browser process needs to be started for a managed render.

Sequence lifecycle support includes reference frames, participant creation and destruction, activation suffixes, automatic activation and nested returns. Page splitting clips lifetimes and frames while preserving message numbering and participant identity. Ditaa uses iterative bridge detection and planar face walks to distinguish open connectors from closed shapes. Formula renderers share a baseline-aware layout tree.

Managed styling supports a bounded set of legacy skin parameters and modern class/sequence style selectors. Font colors, supported font sizes, backgrounds, borders, arrows, stereotypes and declaration order are applied by the corresponding painters. Unsupported properties or selector combinations return `false`.

## Multi-page sequences

```csharp
if (SequencePages.TryRender(source, out var sheets))
{
    for (int i = 0; i < sheets.Count; i++)
        File.WriteAllText($"sequence-{i + 1}.svg", sheets[i].Svg);
}
```

Each sheet includes its optional title, dimensions and inclusive original line range. The regular renderer returns a single SVG containing all sheets.

`SequencePages.TryRender(source, resolveInclude, out sheets)` accepts the same optional include resolver as the combined renderer and preserves preprocessing provenance on each standalone SVG.

## Preprocessing and local includes

```csharp
var files = new Dictionary<string, string>
{
    ["model/entities.iuml"] = "class Invoice"
};
var input = "@startuml\n!include model/entities.iuml\nclass Order\nOrder --> Invoice\n@enduml";
if (PlantUmlRenderer.TryRender(input, path => files.GetValueOrDefault(path), out var model))
    File.WriteAllText("model.svg", model.Svg);
```

The optional resolver receives normalized, root-relative virtual paths and returns file contents or `null`. The library does not open paths or URLs itself. Ordinary source lines keep their original indices. Expanded lines have `data-source-generated="true"`. Included statements additionally carry `data-source-file` and `data-file-line`. Unsupported operations, missing includes, cycles and resource limits return `false` for the complete diagram.

The managed preprocessor implements scalar assignment, arithmetic, concatenation, conditionals, bounded `while` loops, JSON-array `foreach`, named procedures and single-expression return functions. Full TIM function syntax, dynamic library definitions, external resources and unsupported built-ins require a reference implementation. This package ships the managed rendering path. The UnoUML application supplies the optional JavaScript and JVM compatibility paths.
