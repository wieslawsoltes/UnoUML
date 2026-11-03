# UnoUML Studio

[Open UnoUML Studio](https://wieslawsoltes.github.io/UnoUML/) · [Download the .NET library](https://wieslawsoltes.github.io/UnoUML/packages/UnoUML.PlantUml.0.2.0.nupkg) · [Build and deployment](https://github.com/wieslawsoltes/UnoUML/actions/workflows/pages.yml)

A visual PlantUML editor for the browser, built with Uno Platform 6.7 and .NET 10 WebAssembly. The file on disk is ordinary PlantUML. The default renderer is a standalone C# library. When a diagram needs additional syntax, the workspace loads the official PlantUML JavaScript renderer and any requested standard-library bundles.

![UnoUML Studio with a checkout sequence diagram, toolbox and property inspector](docs/studio.png)

## Working on a diagram

- Open a UTF-8 PlantUML document, start from a template or add another diagram block to the current file. Design, Split and Source share the same edit history. The browser keeps a recovery draft. **Save** downloads the document.
- Click a rendered declaration or message to inspect it. Double-click a supported label or press F2 to edit it on the diagram. Enter commits, Escape cancels and Undo restores the original source. Other statements open in the source editor. The inspector exposes applicable names, colors, class members, endpoints, arrow styles and labels. The outline also lets you reach statements that have no selectable SVG geometry.
- Drag a selected declaration’s connection handle to create an edge. Select an existing edge and drag its source or target handle to reconnect it. The arrow style, multiplicities and label stay in the source. The placement tool adds a directional constraint for supported graph objects and reorders compatible declarations in order-sensitive diagrams. PlantUML computes the final geometry.
- Shift-click objects or drag a selection rectangle to select several at once. Copy, cut and paste use PlantUML text. The inspector can group compatible declarations into a package, duplicate a selection or remove it. Structural edits with ambiguous references are restricted.
- Pan with the background or trackpad, zoom around the pointer with Ctrl/Cmd-wheel, or pinch on a touch screen. On a focused canvas use the arrow keys to navigate objects and Enter to open a statement.
- Open **Standard library** to search package and include paths. Import local include files or a directory from the library menu. Save reusable snippets individually or exchange a library as JSON.
- Export the active diagram as SVG or PNG, or open the browser print dialog for PDF. Export uses the same render as the preview and omits editor overlays.

## Renderer coverage

[UnoUML.PlantUml](src/UnoUML.PlantUml/README.md) is a reusable .NET 10 library. It parses a complete diagram, computes its geometry and emits source-mapped SVG. The editor uses the same in-browser C# assembly for preview, picking and export.

| Rendering path | Coverage |
| --- | --- |
| Managed C# | Supported UML and C4 grammars, ArchiMate, Gantt, charts, network, Salt, mind map, WBS, packet, file tree, JSON, block YAML, DOT, Creole, EBNF, regex, Chen ER, Ditaa, AsciiMath and LaTeX |
| Bundled PlantUML 1.2026.9 and Viz.js | Preprocessor operations, standard libraries, advanced Graphviz and rich styling outside the managed grammars. The bundle is loaded only when needed or explicitly selected. |
| Optional JVM endpoint | Server extensions, advanced syntax and external engines available on the selected JVM installation |

The status bar shows the engine that produced the current preview. **Library → … → Rendering engine** can force the JavaScript implementation or select a CORS-enabled JVM endpoint for a document. The server receives the active page. Imported local include files remain on the device. Opening another file clears the server selection.

Every catalog template and compatible toolbox insertion must render in C# for CI to pass. Coverage is a fixture count. The compatibility engines remain available for more extensive syntax in each dialect. CI also exercises 1,080 partially edited inputs. The library index contains 27,780 include paths. Generated macro output and bitmap-only objects can be edited in source. Native output uses its own layout and font metrics. Native JSON fields can be edited individually even when a document is minified or contains identical values on one line.

## Native rendering and editing in 0.2

| Area | Added support |
| --- | --- |
| Sequence | `newpage`, reference frames, participant creation and destruction, activation suffixes, automatic activation and returns. Sheet navigation and standalone sheet exports share the same source map. |
| Chen ER | Entities, relationships, composite/key/derived/multivalued attributes, participation and specialization. Canvas edits preserve aliases, cardinalities and source formatting. Subgraph copy and duplication remap their endpoints together. |
| Ditaa | Grid topology, nested/shared boundaries, branches, rounded corners, semantic shape tags and colors. Label edits use line and column identities and preserve the surrounding grid. |
| Formulas | Vector AsciiMath and TeX layout with fractions, roots, scripts, limits, matrices and mathematical symbols. Formula changes use the source editor. |
| C4 | Context, Container, Component and Deployment calls, nested boundaries, technology labels, external/database/queue elements and directional relationships. Literal macro labels and endpoints can be edited without rewriting other arguments. |
| Styling | Supported `skinparam` properties and modern `<style>` rules for class and sequence diagrams, including declaration order, stereotypes, fonts, colors and line thickness. |
| Preprocessing | Typed scalar expressions, JSON values and iteration, conditions, bounded loops, procedures, simple return functions and local includes with provenance. |

Ditaa geometry, multiline reference text and generated diagram elements use source editing. Full TIM function syntax, arbitrary standard-library macros, advanced style selectors, rich reference frames and external engines continue through the compatibility renderer. Unknown managed syntax declines the complete render so the original source can reach the selected reference implementation.

## Sequence sheets and generated diagrams

A sequence containing `newpage` opens as an overview with a sheet selector. Choose a sheet to inspect it, download its SVG/PNG or print it. Printing the overview puts each sheet on its own printed page. The combined SVG retains titles, source lines and unique element IDs.

The managed preprocessor supports scalar assignment, arithmetic, string concatenation, conditions, bounded loops, JSON-array iteration, named procedures and simple return functions. Import local include files or a folder from the Library menu. Relative includes share a virtual filesystem with the reference renderer. Generated elements point back to their invocation or include. Double-clicking one opens its source so an edit cannot accidentally replace a macro with its output.

## Build and test

Install the .NET 10 SDK, Node.js 22 or later, curl and Chromium. From the repository root run

```sh
npm ci --ignore-scripts
npm run prepare:renderer
dotnet run --project tests/UnoUML.Core.Tests -c Release
dotnet run --project tests/UnoUML.PlantUml.Tests -c Release
npx playwright install chromium
npm run test:renderer
dotnet publish src/UnoUML.App -f net10.0-browserwasm -c Release -o artifacts/site
python3 -m http.server 8080 --directory artifacts/site/wwwroot
```

Open http://localhost:8080/. To use an existing Chromium installation, set `CHROMIUM_PATH` before running the browser tests. Preparing the renderer downloads the locked upstream bundles and verifies each blob. The published directory is self-contained.

## Implementation

| Component | Responsibility |
| --- | --- |
| `UnoUML.PlantUml` | Standalone C# diagram parsers, layout, SVG and source mapping |
| `UnoUML.Core` | Lazy document indexing, exact source spans, typed projections, localized mutations, bounded undo and redo, templates and tools |
| `UnoUML.App` | Uno XAML workspace, property inspector, responsive panels, library picker, navigation and file commands |
| `wwwroot/uml` | On-demand renderer and SVG scene, source hit mapping, gesture handling, clean exports and local include storage |
| `scripts` | Reproducible asset bundling and standard-library indexing |

The initial checkout diagram renders with C# and downloads no PlantUML JavaScript engine. Later edits debounce source changes and discard superseded layouts. Pan and zoom transform the existing scene. A bounded cache reuses recent SVG results. GitHub Actions tests the project, publishes the WebAssembly app and deploys it to Pages on pushes to `main`.

## Reproducing browser timings

After publishing, run `node tests/browser/workspace.mjs artifacts/site/wwwroot` with Playwright Chromium installed. The test runs both engines in the same Uno WebAssembly viewport, alternates their order, warms each implementation and times nine unique versions of each diagram. The reported medians include SVG generation, sanitization, source indexing and DOM installation. GPU painting and app startup are outside the timing window.


Example medians on local headless Chromium with a Release WebAssembly build and trimming disabled

| Uncached diagram | C# | PlantUML JavaScript |
| --- | ---: | ---: |
| 20 classes, 19 relationships | 16.3 ms | 45.3 ms |
| 6 lifelines, 25 messages | 8.7 ms | 30.9 ms |
| 3 C4 context elements | 3.2 ms | 158.5 ms |

The C4 comparison uses the standard context library on both sides. The C# path recognizes the supported macro signatures directly. Each Pages build repeats the benchmark against the production artifact and prints its own results in the workspace test log.
