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

Reference the project directly or download the package into a local NuGet source. [UnoUML.PlantUml.0.5.0.nupkg](https://wieslawsoltes.github.io/UnoUML/packages/UnoUML.PlantUml.0.5.0.nupkg)

```sh
dotnet add package UnoUML.PlantUml --version 0.5.0 --source ./packages
```

Build a local package with `dotnet pack src/UnoUML.PlantUml -c Release`.

## Rendering contract

Pass a single, complete `@start…` / `@end…` block. The method returns a typed diagram and a complete SVG document on success. An unrecognized statement, unsupported dialect or layout that exceeds a managed limit returns `false`. A host can use the official PlantUML renderer at that boundary. Hosts supply filesystem, network, environment and archive capabilities through render-local callbacks.

The native grammars cover common sequence, structural UML, state, activity, timing, chart, Gantt, network, Salt wireframes, mind map, WBS, packet, file tree, JSON, block/flow YAML, DOT, Creole, EBNF and regex diagrams. Chen ER, Ditaa, AsciiMath and LaTeX are rendered as vector diagrams. Built-in C4 Context, Container, Component, Deployment and ArchiMate calls have source-mapped native renderers for the supported standard-library signatures. The general TIM runtime expands additional host-provided library definitions into these grammars. Remaining diagram syntax and external engines can be delegated to the reference renderer. Geometry and typography can differ between implementations. The output includes a viewBox, dimensions, escaped user text, zero-based `data-source-line` attributes and qualified identifiers for addressable UML objects.

The sequence engine measures lanes and message spans before emitting SVG. Structural diagrams lay out nested containers, assign ranks and route connectors. Hierarchies use iterative subtree packing. The JSON renderer streams tokens directly into field tables and tracks source lines across UTF-8 input. The native engines execute directly in the calling process.

Sequence lifecycle support includes reference frames, participant creation and destruction, activation suffixes, automatic activation and nested returns. Page splitting clips lifetimes and frames while preserving message numbering and participant identity. Ditaa uses iterative bridge detection and planar face walks to distinguish open connectors from closed shapes. Formula renderers share a baseline-aware layout tree.

Managed styling includes nested and comma-separated selectors, variables, specificity, stereotypes, depth and descendant inheritance, gradients, shadows and deterministic handwritten paths. Graph, sequence, tree, activity, timing, JSON, YAML and Salt painters integrate their applicable font metrics, wrapping and spacing into layout. All 44 embedded themes are regression-tested on class and sequence diagrams. Other painters accept their integrated paint properties and decline layout-sensitive rules that they cannot measure.

Integrated graph, sequence, mind-map/WBS and Creole painters support HTML and Creole font styles, headings, links, alignment, Unicode escapes, inline data-URI raster images and custom grayscale, compressed, 4096-color and SVG sprites. Declared sprites can resolve local and archive assets through host callbacks. Filename and URL forms of inline `<img>` remain on the reference path. The assembly embeds 223 OpenIconic glyphs, 1,174 Twemoji images and their named shortcuts. Sprite definitions decode lazily, repeated SVG artwork receives unique local definition IDs, and concurrent render calls have isolated resource scopes.

Rich labels can embed native `<math>` and `<latex>` vectors. The formula layout supports fractions, roots, scripts, large operators, aligned arrays, colors, annotations, accents, braces, cancellation, mathematical alphabets and phantom spacing. YAML supports nested collections, quoted keys, comments, literal/folded strings and path highlighting. DOT supports scoped defaults, strict edge merging, subgraphs, nested clusters, rank constraints, ports, record labels, HTML tables, common shapes, compound arrows, gradients and hyperlinks.

Literal JSON, YAML, Salt, DOT, chart, packet, timing and activity fields carry exact UTF-16 token ranges for canvas editing, including BOM-prefixed inputs and CR/LF line endings. Generated statements retain their physical-source provenance, while exact token metadata is emitted only when it maps to the source being edited.

Salt uses a bounded recursive parser and intrinsic layout for nested grids, titled groups, horizontal spans, separators, tree tables, tabs, menus and scroll areas. Open menus and dropdowns paint in a separate pass over the grid, with their drawn extents included in the SVG dimensions. Widget captions, group titles, tree labels, menu items and checkbox/radio markers expose separate exact token ranges. The editor uses these ranges for label changes, direct state toggles and dropdown selection.

Timing supports robust, concise, rectangle, binary, analog and clock tracks with exact decimal and relative times, participant-oriented transitions, named anchors, clock references, date/hour axes, manual axes and explicit scale. State definitions, notes, constraints, highlights and messages use measured layout. Message endpoints follow the waveform, including analog interpolation before the first timed sample. The parser and layout bound track, transition and image sizes and decline inputs that exceed those limits.

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

The resolver receives normalized virtual paths or explicit absolute resource identifiers and returns text or `null`. `SourceFileName` sets the base for relative includes and assets. Source rows retain their original indices. Generated statements carry `data-source-generated="true"`, and included statements also carry `data-source-file` and `data-file-line`. Archive members additionally carry `data-source-archive`. Missing dependencies, invalid syntax, cycles and bounded resource limits return `false` for the complete diagram.

## Host capabilities and archives

This example uses a `symbols.zip` archive containing `model/entities.iuml`, which declares the `Invoice` class.

```csharp
using var archive = File.OpenRead("symbols.zip");
var symbols = PlantUmlResourceLibrary.FromZip(archive);
var options = new PlantUmlRenderOptions
{
    SourceFileName = "main.puml",
    ImportLibrary = path => path == "symbols.zip" ? symbols : null,
    ResolveEnvironment = name => name == "PRODUCT" ? "UnoUML" : null,
    Log = message => Console.WriteLine(message)
};

const string archiveInput = """
    @startuml
    !import symbols.zip
    !include model/entities.iuml
    class Order
    Order --> Invoice
    @enduml
    """;
if (PlantUmlRenderer.TryRender(archiveInput, out var diagram, options))
    File.WriteAllText("model.svg", diagram.Svg);

if (PlantUmlPreprocessor.TryProcess(archiveInput, out var expanded, options))
{
    Console.WriteLine(expanded.Source);
    foreach (var location in expanded.SourceMap)
        Console.WriteLine($"{location.SourceLine} {location.File} {location.FileLine}");
}
```

`PlantUmlResourceLibrary` stores immutable virtual files and can be shared across renders. `FromZip` reads source, JSON, SVG and supported raster resources, skips opaque binary entries, retains the caller's stream, and rejects entries that escape the virtual root. Raster entries resolve to data URIs. `!import` and archive sprite paths use `ImportLibrary`. The options also expose named `Definitions` for `!includedef`, file metadata, a clock, timezone, culture and a random-integer provider for reproducible execution. `SequencePages.TryRender(input, out sheets, options)` uses the same capabilities.

The TIM interpreter supports default/named/overloaded routines, recursive and multi-statement return functions, procedure retrieval and invocation, local/global scopes, legacy macros, JSON mutation and iteration, loops, conditional compilation, include selectors, subparts and themes. Its 74 built-ins cover boolean/string/Unicode operations, JSON, dates, environment and file metadata, library/theme discovery, dynamic calls, colors and HSLuv. `PreprocessedSource.Options` contains the resulting directive options.

Dynamic library expansion executes TIM once and records a native C4/ArchiMate layout projection from already-evaluated arguments. Clock, random-number, environment, resource and diagnostic callbacks preserve their execution order. A literal fast path verifies the exact pinned stock dependency chain before using direct layout. Customized dependencies enter the interpreter with the already-read prefix, preserving resolver call counts. The assembly embeds the pinned C4/ArchiMate source subset needed for standalone expansion.

## Compatibility boundaries

The application also supplies the official JavaScript renderer and an optional JVM endpoint. Non-dot Graphviz engines, overlapping clusters, some HTML-table and graph-layout attributes, extended TeX packages, wider diagram-specific grammars and some layout-sensitive style combinations remain on those paths. YAML anchor-, alias- and tag-looking values follow the pinned PlantUML parser's literal-text behavior. Native geometry uses the library's own layout and generic font measurements. Complete upstream language and layout parity remains unfinished. Regression counts describe exercised fixtures and edits.

Localized timezone display names are pinned to the OpenJDK 17.0.20 catalog across 1,015 locale tags and 632 zone IDs. Other date fields honor the supplied `CultureInfo`. UTC offsets and historical transitions follow the host CLR `TimeZoneInfo` database. Regenerate the portable catalog with `java -Xmx512m scripts/GenerateTimezoneCatalog.java` using that pinned runtime.

The NuGet package includes upstream resource notices and the original OpenIconic, Twemoji and CLDR license texts under `licenses/`. The managed source continues to build with the .NET SDK and its runtime libraries.
