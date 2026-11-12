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

The application uses Uno's native on-demand accessibility entry point. Screen
readers and keyboard users activate **Enable accessibility** after the window is
ready. This avoids the unsuccessful early automatic activation observed with
7.0.32 during this migration. XAML names and dialog helpers remain in the
application, while the old accessibility DOM adapter and scroll synchronization
workaround have been removed. The source bridge no longer uses `UIElement.Handle`
or private control implementation details.

Native semantic controls carry accessible roles and names, while pointer events
pass through to Uno's composition canvas. Browser tests activate the public
accessibility entry with Enter. Native command checks resolve the live role,
require exposed ancestry and enabled state, focus the actual control through its
semantic element, verify that focus, and send one real Space or Enter key.
`activateControl` never dispatches a synthetic click or repeats an activation
after sending the key. Dedicated button checks can request Enter or Space;
other native roles retain their defined activation key. The requested key is
validated again against the current role after focus. SVG and HTML editor controls retain ordinary browser
actionability; diagram dragging, resizing, docking and routing use real pointer
input. A separate native toolbar check clicks Split and Design through the
composition canvas and verifies the source pane opens and closes.

Native semantic rectangles are not a reliable pointer target for every control
in Uno 7.0.32. Captured ContentDialog buttons retained template-local coordinates
at `(24, 25)` while their painted buttons were centered in the window. A dynamic
inspector button retained a stale ancestor offset and pointed at the next row.
These rectangles remained stable, so waiting for two equal layout samples could
not validate their location. Native command automation therefore uses the public
keyboard path; this does not claim to repair upstream semantic geometry. The
explicit `clickControl` pointer helper still checks visibility, bounds stability,
viewport inclusion and an actual hit on Uno's canvas for the toolbar smoke test.

Automation waits for Uno's visible startup cover to disappear and re-resolves
native semantic controls if a framework subtree rebuild replaces them. Text input
classification reads connection and semantic ancestry together: a detached
subtree is re-resolved before either the native or DOM edit path starts. No input
is retried after dispatch. Native text replacement uses the real Select All
keyboard command before insertion,
then verifies the complete resulting value. This updates the managed TextBox
selection as well as its DOM projection; a browser-only selection could instead
prepend text to the existing document.

Native fill failures include opt-in snapshots of the public `FocusManager` owner
and target TextBox at keyboard capture and bubble. A source-transaction trace can
also sample beforeinput, input and composition-end boundaries until the source and
render acknowledge the edit. They record original/current DOM identity and
semantic ancestry, loaded, focus and enabled state, text length/BOM, and selection. Readers exist only while
their TextBox is loaded and use `Utf8JsonWriter` for trimming-safe Release builds.
These snapshots observe existing state without setting focus or changing input.

Scoped compatibility repairs cover native accessibility defects
observed in Uno 7.0.32:

- The public accessibility activation button handles Enter/Space, removes itself
  and changes managed focus before the same key reaches Uno's document keyboard
  listener. The bridge routes that activation through the button's normal click
  handler and consumes only its matching key sequence, including repeats and
  release. This prevents activation from also invoking the newly focused XAML
  command. ComboBox/menu semantic actions have a related duplicate-forwarding
  path; only their explicitly handled keys are consumed after the existing
  semantic handler runs. Unhandled keys and ordinary buttons/options retain
  native dispatch. Text inputs deliberately suppress browser insertion before
  managed editing, so their `defaultPrevented` events must continue to reach Uno.
- The modal focus trap sets `aria-hidden=true` on the dialog's own Popup and
  application ancestors. An active native scope can also become inconsistent
  with newly created semantic nodes: the visible dialog retains its role while
  `aria-modal` and background isolation are missing. Public
  `ContentDialog.Opened`/`Closed` events bound a reconciliation scope identified
  by AutomationId. It restores current modal attributes, exposes the required
  ancestry and isolates background nodes as the semantic subtree changes. The
  observer is confined to Uno's stable semantic root and exists only while app
  dialogs are open; live `aria-controls` relationships admit owned popup regions.
  Pre-native attribute baselines and conditional restoration handle replacement
  nodes and nested/out-of-order closure without restoring stale hidden states or
  overwriting later external attribute changes. Generation checks reject stale
  callbacks. Native Tab trapping and focus restoration remain in place.
- Virtualized ComboBox option creation omits the initial `aria-selected` value;
  only a later selection property change normally supplies it. The application
  snapshots the real control's public `SelectedIndex` on `DropDownOpened` and
  updates that snapshot through `SelectionChanged`. A scoped projector follows
  the head's live `aria-controls` relationship and each option's `aria-posinset`
  to provide selected and unselected metadata. `DropDownClosed` and `Unloaded`
  end observation. Generation checks protect reopened controls, and conditional
  restoration preserves later native metadata changes. Focus and selection
  continue through the native control; `aria-activedescendant` identifies focus
  and is not treated as the selected index.
- TextBox semantic creation omits its initial disabled state even when the real
  control's effective `IsEnabled` is false. Public `Loaded` and
  `IsEnabledChanged` snapshots project that effective value onto the current
  input/textarea's reflected `disabled` property and `aria-disabled` attribute.
  `Unloaded` ends the scope. Pre-tree identity, generation checks, replacement
  node tracking and conditional restoration preserve lifecycle correctness;
  readonly state, tab order, text and selection remain native.

ComboBox registration is idempotent and assigns `AutomationId` before the native
semantic head is created: Uno 7.0.32 does not project subsequent AutomationId
changes onto an existing head. Dialog preparation walks public content, border
children and panel children before `ShowAsync`; both inspector picker factories
attach before insertion, and the persistent page picker attaches during page
construction. The later dialog `Opened` traversal also names generated controls.

TextBox registration uses the same pre-tree identity rule. Dialog content and
named inspector fields attach before insertion; the source and library-search
editors attach during page construction. Observation is confined to the stable
semantic root while registered controls are loaded, so late accessibility
activation and replaced semantic nodes receive the current effective state.

Semantic TextBox replacement is distinct from a XAML control unload. A captured
native input trace showed Select All succeeding in both managed and browser
editors, followed by replacement of the semantic textarea with the same public
identity. The new node retained its text and selection, while browser focus moved
to the document body and the loaded XAML TextBox remained the current focus owner.

Loaded TextBox scopes now retain evidence of actual DOM focus and consult a
synchronous `Func<bool>` reader of public `FocusManager.GetFocusedElement` before
transferring focus to a unique replacement. The reader verifies the captured load
generation, loading and enabled state. The browser requires the old node to be
disconnected, the new node to be exposed and enabled, and current focus to be the
document fallback. Explicit blur, foreign focus, pointer or Tab navigation,
window blur, active composition and unloaded/new generations cancel the old
witness. A completed composition can re-arm only the still-focused editor after
a fresh native-owner check. Transfer uses `focus({ preventScroll: true })` without
rewriting value or selection. Ordinary typing reuses an established focus witness,
avoiding style walks and layout reads on each key. The native focus authority and
normal input dispatch remain unchanged.

The framework's native focus resolver also omits ComboBox-specific virtualized
regions and can focus a semantic ancestor instead of the actual option. While a
picker is open, public `FocusManager.GotFocus`/`LostFocus` events schedule a
coalesced snapshot of `GetFocusedElement` for its XAML root. Projection requires
an actual `ComboBoxItem`, matching `ItemsControlFromItemContainer` ownership and
a `ContainerFromIndex` round trip. The focused index is independent of
`SelectedIndex`; moving keyboard focus alone never changes selected metadata.
Generation and focus-revision checks cancel stale callbacks, and foreign native
focus clears pending work. Deferred DOM focus waits for the corresponding
option, preserves scroll position and is cancelled by intervening DOM focus or
window blur. The observer and public focus subscriptions end on popup close or
unload. The real application test waits for this known queued projection while
requiring focus to remain inside the modal or its controlled popup. After one
explicit option focus request, it waits for the current semantic option to become
`:focus` before the existing exact focus assertion and one Enter dispatch. Passive
failure tracing can inspect the modal-isolated source editor's public native-owner
reader; interaction locators continue to enforce modal exclusion.

Uno's virtualized listbox container is intentionally a logical accessibility root
without its own visual rectangle. Individual option nodes can also retain zero
projected bounds after deferred creation while the native popup is fully painted.
The migration's failure screenshot confirmed that distinction. Browser tests wait
for accessible roles, names, relationships and selected-state metadata on these
logical nodes. Focusing an option invokes Uno's native `ComboBoxItem` focus path;
a real Enter key commits the focused index through the native popup handler and
closes it. Reopening verifies both the committed selection and deselected peers.
Command activation and pointer coverage follow the separate contracts above.

The bridge does not invoke private framework callbacks or replace its focus trap.
Its small module is installed before the native host starts, so keyboard
activation is protected even before the larger viewport modules have loaded.
Isolated browser fixtures reproduce these event and ancestry contracts, while the
published application gate checks keyboard activation, reopening, popup selection,
modal background isolation and focus restoration. These compatibility repairs
should be re-evaluated when upgrading the pinned Uno packages.

Arrange, Export and Library share a public `MenuFlyout` lifecycle coordinator.
Uno 7.0.32's `FlyoutBase.RemoveFromOpenFlyouts` raises `Closed` in a queued
callback and then hides the next open flyout. An immediately opened replacement
could therefore disappear after successful activation. The coordinator retains the
latest requested menu until a dispatcher continuation after the complete close,
creates its contents at that point, and cancels pending work on page/target unload.
The existing real-browser Collapse-to-Expand sequence exercises this ordering.

Source text uses public automation identity and managed focus ownership.
A string-only JSON envelope preserves a leading U+FEFF in the managed-to-browser
projection. A trusted browser-input trace also exposed a synchronous native echo:
the capture phase received the leading BOM, but Uno's UTF-16 decoder removed it
before that same input event reached the bubble phase. The next input event then
read the shortened value back into the document. Loaded source editors now retain
a scoped input registration even when their current text has no BOM. It captures
trusted committed input and repairs only that event's exact missing-leading-BOM
echo before subsequent input can observe it. Ordinary registrations use a constant
empty envelope rather than serializing the document on every text change.

Every document refresh invalidates the previous native text expectation before
binding and publishes the final text, selection and focus. This also runs when
LF/CRLF normalization leaves the TextBox value unchanged, so reopening equivalent
source still refreshes its browser projection. Opening plain text cannot inherit
an older document's BOM expectation. Async generation guards discard superseded
projection work while the loaded editor keeps its composition registration.

A restored draft can precede native accessibility's creation of its textarea.
While at least one current source registration expects a BOM, a child-list
observer watches only Uno's stable semantic root for added editor nodes. It
resolves the current AutomationId, registration and connected node at delivery,
then applies the same exact-echo repair and composition/selection guards. Plain
source, committed input that invalidates the expectation, and unload end unused
observation. This handles late creation and replacement after the initial frame
without intercepting value properties or polling future frames.

Both repair paths preserve UTF-16 selection and scroll position, account for DOM
CRLF normalization, and leave active IME composition, deliberate BOM deletion,
nonmatching transformations and untrusted input untouched. Element/registration
identity guards reject stale work after replacement or unload. Queued managed
projection remains coalesced on the dispatcher to observe final text, selection
and focus. Browser fixtures use real keyboard insertion and Chromium composition,
including a negative control with the native UTF-16 decoder's BOM behavior.

Chromium's [queued composition-end dispatch](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/editing/ime/input_method_controller.cc)
can leave `isTrusted` false, unlike its ordinary native
[event-target dispatch](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/dom/events/event_target.cc).
The IME repair therefore also accepts a completed sequence whose trusted start
and latest trusted composing input identify the same loaded editor, registration
and exact text value. This evidence is consumed at the first end event; edits,
owner replacement or a changed value cannot reuse it. Standalone synthetic input
and synthetic composition sequences remain excluded. The end event supplies the
current native selection, so committing a reading preserves its caret.

Uno's multiline text box normalizes CRLF and LF to CR and delivers text changes
asynchronously. The document therefore has a separate text projection: framework
normalization is a no-op for source and history, real edits replace the smallest
changed source interval, and UTF-16 selections map across collapsed CRLF pairs.
Unchanged mixed line endings and BOMs remain verbatim. Imported-source editors use
the same projection. Unchanged source-statement dialogs are no-ops; an explicit
semantic statement replacement retains the existing document newline policy.

## Presentation architecture

The viewport layers `DiagramGpuCanvas` below a native `BrowserHtmlElement`.
The HTML host retains the installed SVG for precise measurement, source identity,
pointer hit testing, label editing, editor controls and vector export.

Canvas history shortcuts dispatch directly to the document history bridge:
Control/Command+Z undoes, Shift+Control/Command+Z redoes, and Control/Command+Y
also redoes. Claimed keys stop before native document dispatch because the XAML
focus owner can still be the source editor while the HTML canvas has browser
focus. Inline text editors retain their own undo; composition, Alt combinations
and already-handled events do not invoke canvas commands. Document history remains
available while a new frame is pending and for literal-only canvas selections.

The camera owns the viewport's complete pan/zoom transform. The HTML surface uses
`overflow: clip`: `overflow: hidden` would still permit programmatic or
focus-driven scrolling, introducing a second transform that displaced selection
handles from the GPU camera. A real-browser regression reproduces that displacement
with hidden overflow, verifies clip rejects implicit scrolling, and checks pointer
resize dimensions and the fixed opposite anchor. Integrated gesture checks wait
for the current native GPU dimensions and accepted scene/camera revision before
fitting, then require the actual fitted frame before measuring pointer motion.

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
Paths exceeding the conservative nonzero winding budget use a bounded raster
resource, preserving their pixels without overflowing the eight-bit stencil.

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
| Embedded font | 4 MiB per fetched face; 64 matching faces per snapshot |
| Embedded font cache | 24 Mi characters of encoded data, up to 64 entries |
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
node --test tests/source-text.mjs tests/gpu-geometry.mjs tests/gpu-host.mjs tests/gpu-paint.mjs tests/gpu-fonts.mjs
dotnet run --project tests/UnoUML.Gpu.Tests -c Release
dotnet run --project tests/UnoUML.Core.Tests -c Release
dotnet run --project tests/UnoUML.PlantUml.Tests -c Release
node tests/browser/gpu-shaders.mjs
node tests/browser/gpu-scene.mjs
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
Deployment on main depends on the application, shader and independent GPU workspace jobs.
The six isolated authoring suites run even when the primary desktop/mobile flow
fails, and report all failures before browser cleanup. The independent GPU job
also collects compiler and unsupported-adapter results after an accessibility
failure, so an early native-control regression cannot conceal those checks.

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

Verified rendering gates:

- Release Uno browser build and native graphics link succeeded.
- Restored Uno framework packages are 7.0.32; Silk.NET.WebGPU is 2.23.0.
- GPU model tests passed 738 assertions in 12 cases.
- Geometry, source-input projection and presentation-host tests cover 16, 23 and eight cases respectively. The source cases include stale BOM invalidation, equivalent LF/CRLF refresh without a native text-change event, and semantic editors created after the initial frame.
- Native semantic fixtures passed 82 assertions across 22 cases, including modal, ComboBox focus/selection and TextBox enabled-state lifecycles. The published application passed the corresponding reopened-dialog and renderer-toggle checks.
- Separate native TextBox focus fixtures passed **30 assertions across 11 cases** in both the fast Chromium gate and the published application. They cover real replacement and delayed insertion, current selection/scroll and subsequent keyboard input, fresh native ownership, explicit user focus changes, composition, hidden/inert/disabled replacements and loaded-generation cleanup.
- Independent Canvas, DOT, structured-diagram, Network and Gantt authoring suites passed in CI #66, including free geometry and automatic layout, shared-edge and table restructuring, chart axes, task dragging/resizing, dependency propagation, byte-exact BOM/CRLF Save/Open/draft restoration and atomic undo.
- The Core suite passed 4,065 assertions, including 103 new source-projection checks and unchanged startup history for all 33 starter templates.
- Production WGSL passed six pipeline creations and 17 offscreen pixel checks.
- The attached Chromium compiler suite passed 159 assertions and a native non-scaling-stroke measurement, across 14 scenarios.
- The published Uno application presented through its shared WebGPU device and completed 24 camera updates without additional geometry, texture, paint or gradient uploads, or render-bundle rebuilds.

The starter sequence comparison at 928 × 614 pixels measured a mean absolute RGB
channel error of 0.7336 on the 0–255 scale. A maximum channel difference above 64
occurred in 0.2425% of pixels. Its visible-ink counts were 11,537 GPU pixels and
11,748 SVG reference pixels. The comparison uses the actual composed viewport and
the same attached SVG, camera, fonts and browser. These figures characterize this
fixture and configuration; they do not establish universal SVG pixel equivalence.

The scene retained 35 geometry buffers, 17 image textures and 52 ordered render
bundles. Across the 24 camera updates, geometry uploads remained 25,008 bytes,
texture uploads remained 247,372 bytes, draw-uniform uploads remained 13,312 bytes,
gradient uploads remained 32 bytes and bundle builds remained 52. The only changing
upload counter was the camera buffer: 128 to 1,664 bytes, exactly 24 × 64 bytes.
Resolution-bucket changes can legitimately recompile curves and text at a higher
quality level; this retention check stays within one bucket.

The final PR validation section records subsequent integrated browser results.
Earlier layout benchmark numbers in README are historical source-layout timings,
not measurements of this GPU engine. No discrete-GPU speedup, universal frame-rate
guarantee or completed “AAA” hardware qualification is claimed without measurements.

## Primary references

- [Uno 7 migration guide](https://github.com/unoplatform/uno/blob/7.0.32/doc/articles/migrating-to-uno-7.md)
- [Uno native screen-reader activation](https://github.com/unoplatform/uno/blob/7.0.32/doc/articles/features/accessibility/testing-with-screen-readers.md)
- [Uno 7.0.32 accessibility activation](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/ts/Runtime/Accessibility.ts)
- [Uno 7.0.32 native keyboard forwarding](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/ts/Runtime/BrowserKeyboardInputSource.ts)
- [Uno 7.0.32 semantic control actions](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/ts/Runtime/SemanticElements.ts)
- [Uno 7.0.32 ComboBox semantic lifecycle](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/Accessibility/WebAssemblyAccessibility.ComboBox.cs)
- [Uno 7.0.32 virtualized semantic regions](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/Accessibility/VirtualizedSemanticRegion.cs)
- [Uno 7.0.32 semantic element identity](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/Accessibility/SemanticElementFactory.cs)
- [Uno 7.0.32 native state updates](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/Accessibility/WebAssemblyAccessibility.cs)
- [Uno 7.0.32 native focus synchronization](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/Accessibility/FocusSynchronizer.cs)
- [Uno 7.0.32 deferred flyout close](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI/UI/Xaml/Controls/Flyout/FlyoutBase.cs)
- [Uno 7.0.32 public focus manager](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI/UI/Xaml/Input/FocusManager.cs)
- [Uno 7.0.32 modal focus isolation](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Runtime.WebAssembly.Browser/ts/Runtime/FocusTrap.ts)
- [Uno WebGPU backend](https://github.com/unoplatform/uno/blob/7.0.32/doc/articles/features/using-webgpu-rendering.md)
- [Custom WebGpuCanvasElement](https://github.com/unoplatform/uno/blob/7.0.32/doc/articles/controls/WebGpuCanvasElement.md)
- [Uno 7.0.32 release](https://github.com/unoplatform/uno/releases/tag/7.0.32)
- [WebGPU specification](https://www.w3.org/TR/webgpu/)
- [WGSL specification](https://www.w3.org/TR/WGSL/)
- [Chromium SwiftShader configuration](https://chromium.googlesource.com/chromium/src/+/HEAD/docs/gpu/swiftshader.md)
- [Chrome headless WebGPU testing](https://developer.chrome.com/blog/supercharge-web-ai-testing)
