# UnoUML Studio

[Open UnoUML Studio](https://wieslawsoltes.github.io/UnoUML/) · [Build and deployment](https://github.com/wieslawsoltes/UnoUML/actions/workflows/pages.yml)

A visual PlantUML editor for the browser, built with Uno Platform 6.7 and .NET 10 WebAssembly. The file on disk is ordinary PlantUML. The local renderer runs on the device and loads standard-library bundles when a diagram needs them.

![UnoUML Studio with a checkout sequence diagram, toolbox and property inspector](docs/studio.png)

## Working on a diagram

- Open a UTF-8 PlantUML document, start from a template or add another diagram block to the current file. Design, Split and Source share the same edit history. The browser keeps a recovery draft. **Save** downloads the document.
- Click a rendered declaration or message to inspect it. Double-click to edit its statement. The inspector exposes applicable names, colors, class members, endpoints, arrow styles and labels. The outline also lets you reach statements that have no selectable SVG geometry.
- Use **Connect** between two declarations. The placement tool adds a directional constraint for supported graph objects and reorders compatible declarations in order-sensitive diagrams. PlantUML computes the final geometry.
- Shift-click to select several objects. Copy, cut and paste use PlantUML text. The inspector can group compatible declarations into a package, duplicate a selection or remove it. Structural edits with ambiguous references are restricted.
- Pan with the background or trackpad, zoom around the pointer with Ctrl/Cmd-wheel, or pinch on a touch screen. On a focused canvas use the arrow keys to navigate objects and Enter to open a statement.
- Open **Standard library** to search package and include paths. Import local include files or a directory from the library menu. Save reusable snippets individually or exchange a library as JSON.
- Export the active diagram as SVG or PNG, or open the browser print dialog for PDF. Export uses the same render as the preview and omits editor overlays.

## Renderer coverage

The catalog contains 29 diagram templates and 118 contextual tools. The browser path uses the official PlantUML 1.2026.9 engine and Viz.js, with a dedicated Graphviz path for DOT. It includes 34 lazy standard-library bundles and a searchable index of 27,780 paths.

| Renderer | Examples | Editing |
| --- | --- | --- |
| Local browser engine | Sequence, class, object, use case, state, activity, component, deployment, timing, C4, ArchiMate, mind map, WBS, Gantt, JSON, YAML, Salt, network, grammar, regex, DOT, chart and packet diagrams | Structured controls for recognized declarations and relationships. Source editing for every statement. |
| Optional JVM server | Chen, Ditaa, Files, math and LaTeX | Source and statement editing. SVG element selection depends on the backend’s metadata. A raster diagram such as Ditaa has no individual SVG shapes. |

Choose **Library → … → Rendering engine** to use a server. Enter its SVG endpoint. The server must support CORS and the requested dialect. POST sends the active page as plain text. Choose encoded GET for servers that implement PlantUML’s URL format. The choice applies to the current document. Switching files resets it. Local include files stay in the browser.

A visual edit patches the original source range. Macros, preprocessor expressions, comments and extension syntax outside the edit are preserved. The shipped fixture suite covers the catalog combinations. PlantUML extensions and generated macro objects do not all have dedicated property editors.

## Build and test

Install the .NET 10 SDK, Node.js 22 or later, curl and Chromium. From the repository root run

```sh
npm ci --ignore-scripts
npm run prepare:renderer
dotnet run --project tests/UnoUML.Core.Tests -c Release
npx playwright install chromium
npm run test:renderer
dotnet publish src/UnoUML.App -f net10.0-browserwasm -c Release -o artifacts/site
python3 -m http.server 8080 --directory artifacts/site/wwwroot
```

Open http://localhost:8080/. To use an existing Chromium installation, set `CHROMIUM_PATH` before running the browser tests. Preparing the renderer downloads the locked upstream bundles and verifies each blob. The published directory is self-contained.

## Implementation

| Component | Responsibility |
| --- | --- |
| `UnoUML.Core` | Lazy document indexing, exact source spans, typed projections, localized mutations, bounded undo and redo, templates and tools |
| `UnoUML.App` | Uno XAML workspace, property inspector, responsive panels, library picker, navigation and file commands |
| `wwwroot/uml` | On-demand renderer and SVG scene, source hit mapping, gesture handling, clean exports and local include storage |
| `scripts` | Reproducible asset bundling and standard-library indexing |

The initial preview loads the layout engine on demand. Later edits debounce source changes and discard superseded layouts. Pan and zoom transform the existing scene. A bounded cache reuses recent SVG results. GitHub Actions tests the project, publishes the WebAssembly app and deploys it to Pages on pushes to `main`.
