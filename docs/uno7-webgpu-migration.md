# Uno Platform 7 and WebGPU migration

## Scope and resolved versions

The migration starts at `6f456893930e6b45d1b7bb37cf1cbe07a018d93a` and is reviewed
in [PR #1](https://github.com/wieslawsoltes/UnoUML/pull/1). It changes the browser
application host and diagram presentation. The standalone C# PlantUML parser,
layout engines, source mappings, structural authoring, automatic layout, saved
canvas geometry and portable SVG output retain their existing contracts.

| Layer | Selection |
| --- | --- |
| SDK | Uno.Sdk 7.0.5, pinned in global.json |
| Framework | Uno 7.0.32, verified from the restored package graph in CI |
| Target | net10.0-browserwasm |
| Application composition | Uno WebGPU, followed by explicit Skia initialization fallback |
| Diagram canvas | Uno.WinUI.Graphics3DWebGpu.WebGpuCanvasElement |
| Native API binding | Silk.NET.WebGPU 2.23.0 supplied by Uno |
| Diagram shaders | WGSL, six explicit-layout 2D pipelines, 4× MSAA |
| Native build dependency | .NET wasm-tools workload |

The package audit asserts that Uno.WinUI, Runtime.WebAssembly.Browser,
Composition.WebGpu, Composition.Skia and Graphics3DWebGpu all resolve to Uno 7.
It prints exact versions rather than treating a requested SDK version as proof.

## Application host and input migration

Uno 7 removes the old per-control DOM UI renderer. The application now uses
`UnoPlatformHostBuilder` with WebGPU registered first and Skia second. The project
selects `Skia;WebGpu` instead of `NativeRenderer`. Both application composition
and the custom diagram canvas use Uno's device; the scene compiler never calls
`navigator.gpu.requestDevice()`.

Native Uno accessibility is enabled before host startup. XAML names and dialog
helpers remain in the application, while the old accessibility DOM adapter and
scroll synchronization workaround have been removed. The source bridge no longer
uses `UIElement.Handle` or private control implementation details.

Source text uses public automation identity and managed focus ownership.
A string-only JSON envelope preserves a leading U+FEFF. Repair is limited to the
known missing-leading-BOM projection, preserves UTF-16 selection and scroll
position, accounts for DOM CRLF normalization, and never rewrites active IME
composition or an intentional user deletion. Lifecycle/text changes are coalesced
onto the dispatcher so the browser receives final text, selection and focus state.

## Presentation architecture

The viewport layers `DiagramGpuCanvas` below a native `BrowserHtmlElement`.
The HTML host retains the installed SVG for precise measurement, source identity,
pointer hit testing, label editing, editor controls and vector export.

The installed, projected SVG is compiled into an incremental retained display list:

1. Resolve computed styles, transforms, geometry, paint, clipping and raster resources.
2. Emit changed commands, removals, painter order and new texture resources.
3. Validate the entire transaction atomically in the C# scene model.
4. Synchronize only changed GPU resources and recorded render bundles.
5. Cull commands against the current viewport and execute visible bundles in painter order.
6. Resolve the multisample target into Uno's composition texture.
7. Acknowledge the exact accepted revision before suppressing the SVG's paint.

The semantic SVG is never detached or hidden with `display:none` or
`visibility:hidden`. The `.paper` element becomes transparent only after a matching
GPU frame. New geometry restores SVG paint until its corresponding frame is ready.
Source-version guards, monotonic transaction IDs and physical-target generations
reject stale compilation and frame acknowledgements. Multiple-selection and hover
decorations remain in the interactive overlay outside suppressed SVG paint.

The source renderer (`C#`, bundled PlantUML JavaScript, or server JVM) is separate
from the presentation backend (`webgpu` or `svg`). Choosing a language compatibility
renderer does not by itself disable GPU presentation.

## Geometry, shaders and quality

| Feature | Implementation |
| --- | --- |
| SVG paths | M/L/H/V/C/S/Q/T/A/Z, relative coordinates, adaptive cubic/quadratic/arc flattening |
| Curve quality | Screen-space tolerance and resolution buckets account for camera scale and DPR |
| Fills | Stencil-and-cover with nonzero/evenodd winding; holes and self-intersections are retained |
| Strokes | Triangle coverage with caps, joins, miter limits, dash arrays and offsets |
| Stroke overlap | Equal-zero/increment-clamp stencil union; translucent overlaps blend once |
| Paint | Solid, linear and two-circle radial gradients with ordered stops and pad/repeat/reflect |
| Transform | Local-to-world command transforms plus a separate world-to-viewport camera |
| Antialiasing | Four-sample color and depth/stencil attachments, explicit resolve |
| Alpha | Premultiplied output; straight RGBA resources are filtered in premultiplied form |
| Color | Output conversion handles linear/sRGB target formats explicitly |
| Clipping | Rectangular world clips in the paint shader; complex composition uses bounded resources |
| Text | Browser-shaped text/tspan snapshots cached at resolution buckets |
| Images/effects | Bounded raster resources with explicit compatibility diagnostics |
| Background | Shader-rendered grid matching the 16 CSS-pixel tile and colors of the SVG presentation |

Text resources preserve browser shaping instead of rebuilding a second incomplete
font, bidi, ligature and baseline engine. Common diagram geometry and paint are
shader-native; text and complex SVG compositing can use cached raster resources.
This is not a claim that every SVG filter has an independent WGSL implementation.

Painter-order-sensitive commands use retained WebGPU render bundles. These record
vertex buffers, pipeline changes, bindings and offsets once. Pan/zoom updates reuse
the graph, geometry, textures, paint uniforms and bundles, uploading a 64-byte camera
uniform. Transform-only edits can update a 256-byte draw uniform while retaining
geometry and bundles. The visible bundle list is reused across frames.

## Ownership, limits and fallback

Uno owns the device, queue and composition target. The custom renderer owns and
releases its pipelines, bind groups, buffers, image textures, render bundles and
multisample attachments. It uses the context's existing API dispatch rather than
loading a second native WebGPU instance. Initialization, unload and reinitialization
have explicit generation guards. Validation and allocation error scopes do not
replace Uno's shared-device callbacks.

| Resource | Guard |
| --- | ---: |
| Retained commands | 32,768 |
| Retained vertex coordinates | 8,388,608 floats |
| Gradient stops | 262,144 |
| Retained GPU image payloads | 96 MiB |
| Browser raster cache | 64 MiB, up to 8,192 entries |
| Individual texture side | 8,192 pixels and the adapter's limit |
| Rasterized owner | 16,777,216 pixels, at most 64 tiles |
| Raster tile | 2,048 pixels per side |
| Bridge JSON | 48 Mi characters |
| Multisample attachment estimate | 512 MiB and the adapter's limits |

Invalid transactions do not partially mutate the retained scene. Unsupported or
unbounded SVG composition, exceeded budgets, unavailable adapters and owned GPU
validation failures restore visible SVG presentation. Diagnostics report the reason;
visible content is never silently omitted.

Skia fallback is selected during application backend initialization. Recovery from
loss of Uno's shared application device remains governed by Uno's own composition
backend; this PR does not create an independent replacement device or promise
unverified application-wide hot recovery. A reload can re-negotiate the backend.

SVG and print exports continue using the projected vector document. PNG export
continues using the canonical SVG raster encoder. GPU screenshot tests are
validation artifacts and do not change the exported document format.

## Building and running validation

Install .NET 10, Node.js 22 and Chromium, then run from the repository root:

~~~sh
dotnet workload install wasm-tools --skip-manifest-update
npm ci --ignore-scripts
npm run prepare:renderer
dotnet build src/UnoUML.App -f net10.0-browserwasm -c Release
node scripts/verify-uno7-assets.mjs src/UnoUML.App/obj/project.assets.json
node --test tests/source-text.mjs tests/gpu-geometry.mjs tests/gpu-host.mjs
dotnet run --project tests/UnoUML.Gpu.Tests -c Release
dotnet run --project tests/UnoUML.Core.Tests -c Release
dotnet run --project tests/UnoUML.PlantUml.Tests -c Release
node tests/browser/gpu-shaders.mjs
dotnet publish src/UnoUML.App -f net10.0-browserwasm -c Release -o artifacts/site
UNOUML_REQUIRE_WEBGPU=1 SCREENSHOT_DIR=artifacts/graphics node tests/browser/workspace.mjs artifacts/site/wwwroot
~~~

Set `CHROMIUM_PATH` to an installed Chrome/Chromium executable, or install Playwright
Chromium. On Linux, graphics validation uses an explicit SwiftShader/Vulkan
configuration. This executes the real WebGPU API, shader compilation, render
bundles, multisampling and texture readback on a software adapter. Set
`UNOUML_GPU_DRIVER=hardware` to measure an available hardware adapter without the
software-driver flags, and `UNOUML_HEADED=1` for a visible browser.

The independent shader CI job compiles the production WGSL and creates all six
pipelines. Pixel checks cover winding holes, translucent stroke overlap, linear and
radial gradients, image alpha, clipping/stencil cleanup, affine transforms and grid
placement. The application check requires a matching WebGPU frame, verifies visible
GPU output against the SVG reference, and asserts camera updates do not upload new
geometry/textures/paint or rebuild bundles. Unsupported-adapter startup is exercised
with `navigator.gpu` unavailable.

CI uploads graphics metrics, paired screenshots and browser console diagnostics.
Pull requests run these gates without deploying the production Pages site.
Deployment on main depends on both the application and shader jobs.

## Diagnostics and evidence

Import `uml/viewport.js` relative to `document.baseURI` and call
`rendererState(nativeHostId)`. The returned state reports actual accepted
presentation, pending work, source-independent scene revision, fallback reason,
compiler counters and C# graphics statistics.

Statistics include command visibility, draw counts, live geometry/image/bundle
counts, buffer/texture bytes, cumulative upload bytes, bundle builds, CPU encoding
time, target dimensions, sample count and frame count. GPU-frame time is not
inferred from CPU encoding time. Browser camera wall-time measurements include
scheduling and software-driver costs.

Verified before live viewport integration:

- Release Uno browser build and native graphics link succeeded.
- Restored Uno framework packages are 7.0.32; Silk.NET.WebGPU is 2.23.0.
- GPU model tests passed 687 assertions in 11 cases.
- Geometry tests passed 16 cases; source projection passed nine cases.
- Production WGSL passed six pipeline creations and 15 offscreen pixel checks.

The final PR validation section records subsequent integrated browser results.
Earlier layout benchmark numbers in README are historical source-layout timings,
not measurements of this GPU engine. No discrete-GPU speedup, universal frame-rate
guarantee or completed “AAA” hardware qualification is claimed without measurements.

## Primary references

- [Uno 7 migration guide](https://github.com/unoplatform/uno/blob/7.0.32/doc/articles/migrating-to-uno-7.md)
- [Uno WebGPU backend](https://github.com/unoplatform/uno/blob/7.0.32/doc/articles/features/using-webgpu-rendering.md)
- [Custom WebGpuCanvasElement](https://github.com/unoplatform/uno/blob/7.0.32/doc/articles/controls/WebGpuCanvasElement.md)
- [Uno 7.0.32 release](https://github.com/unoplatform/uno/releases/tag/7.0.32)
- [WebGPU specification](https://www.w3.org/TR/webgpu/)
- [WGSL specification](https://www.w3.org/TR/WGSL/)
- [Chromium SwiftShader configuration](https://chromium.googlesource.com/chromium/src/+/HEAD/docs/gpu/swiftshader.md)
- [Chrome headless WebGPU testing](https://developer.chrome.com/blog/supercharge-web-ai-testing)
