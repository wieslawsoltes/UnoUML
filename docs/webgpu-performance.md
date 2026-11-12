# WebGPU performance work

## Baseline and scope

This work follows [the Uno 7 migration](uno7-webgpu-migration.md), merged in
[PR #1](https://github.com/wieslawsoltes/UnoUML/pull/1) at
`b25c990167b13db19dca318ca2f25420085e3518`. The merge has the same application
tree as accepted revision `3c1dac1310b2bb880831cf0ef484e33c737bab89`. The
[post-merge workflow](https://github.com/wieslawsoltes/UnoUML/actions/runs/43310975870)
passed all three test jobs and deployed the merged application to GitHub Pages.

The optimization keeps the shared Uno WebGPU device, 4× MSAA, source-aware
authoring, canonical exports, exact-frame acceptance, resource budgets and
explicit fallback contracts. Changes must remove work while preserving pixels
and editor behavior.

## Existing measured baseline

The [saved application measurements](https://github.com/wieslawsoltes/UnoUML/actions/runs/43170302022)
use Chromium 156, SwiftShader/Vulkan, device pixel ratio 1 and a 928 × 614
viewport. Rendering code is identical to the merged baseline; the subsequent
accepted revision changed test synchronization and documentation.

| Measurement | Recorded value |
| --- | ---: |
| Starter commands / geometry buffers / image textures | 52 / 35 / 17 |
| Starter draw calls, including background | 88 |
| Starter mean absolute RGB-channel error against SVG | 0.7336 / 255 |
| Pixels with maximum-channel difference above 64 | 0.2425% |
| Selection comparison mean channel error | 0.7868 / 255 |
| Selection-mask pixels closer to painted than absent reference | 98.1651% |
| Camera updates with unchanged scene resources | 24 |
| Camera upload for those updates | 1,536 bytes |
| Additional geometry, texture, paint uploads or bundle rebuilds during camera updates | 0 |
| Draw-uniform upload when clearing selection | 256 bytes |
| Final CPU encoding sample | 0.7999 ms |
| Camera acknowledgement wall-time median / p95 | 1,484.4 / 1,956.2 ms |

The CPU sample and browser wall durations measure different boundaries.
The software-driver result is not hardware frame time. It does not establish
which portion of the wall duration belongs to GPU work, validation callbacks,
the UI dispatcher or browser scheduling.

## Implemented changes

| Path | Previous work | Current behavior |
| --- | --- | --- |
| Sparse native command patches | Revisited every retained vertex to establish local bounds | Reuse validated bounds unless vertices or declared bounds change |
| Native resource statistics | Sum all retained vertex and image payloads each frame | Read transactionally maintained totals |
| SVG replacement | Clear raster cache and submitted display-list baseline | Diff fresh command owners against the submitted scene and reuse exact raster snapshots and current native images |
| Superseded source compilation | Continue remaining owners and tiles before rejecting the result | Check the current generation, source version and DOM identity around asynchronous work |
| Opaque unclipped solid mesh | Stencil union plus paint cover, two draws | Direct paint with existing WGSL, one draw |
| Frame latency diagnostics | Native CPU encoding and browser wall duration | Also report scope callback, dispatcher, submission and deferred-request measurements |

Resource totals are staged alongside commands, images, painter order, camera and
viewport dimensions. Rejection leaves the accepted state and its totals unchanged.
Command `kind` remains a full replacement; replacing an image with vector geometry
still requires fresh vertices and bounds.

Raster reuse depends on the existing complete frozen signature, including styles,
referenced definitions, font data, resolution and tile geometry. New DOM owners
keep distinct command IDs. Removing the last image owner removes its uploaded
status, so later reuse retransmits its cached payload. Font availability changes
invalidate captures, and backend rejection still forces a full scene reset.

The direct path requires `Mesh`, no clip, solid paint, opacity exactly `1f` and
color alpha exactly `1f`. Overlapping samples write the same opaque color.
General fill fans, gradients, fractional opacity and clipped meshes retain stencil
coverage. Four-sample MSAA, color conversion and painter order remain identical;
the route is part of retained bundle identity. The renderer now has seven pipelines.

`validationScopeMilliseconds` spans the first error-scope pop request through
both native callback completions. `validationDispatchMilliseconds` spans callback
completion through entry into the dispatcher action. Submission and deferred
render-request counters are cumulative. Frame-validation sequencing remains
unchanged: this work does not assume that the callback gate causes the previously
measured wall latency.

Regression coverage adds native atomic-total, conservative-bound, owner-lifetime
and direct-route transitions; browser cancellation, exact pixel reuse and
invalidation cases; and eight full-frame direct/stencil RGBA comparisons with
zero tolerance. Runtime acceptance is recorded below when CI completes.

## Comparison protocol

The [paired benchmark workflow](../.github/workflows/gpu-performance.yml) runs
when an exact reviewed candidate is pushed to
`codex/webgpu-performance-benchmark`. It independently checks out and builds
the immutable merged baseline and the candidate push SHA, then uses the
candidate's common [harness](../tests/browser/gpu-performance.mjs) against both.
Normal PR pushes continue to run the existing correctness suites.

The run order is baseline, candidate, candidate, baseline. Each workload uses a
fresh browser context with a 1,390 × 860 desktop viewport, DPR 1, light color
scheme, Chromium, the explicit software driver and 4× MSAA. The starter and
64/256-class diagrams exercise camera changes; the class scenes additionally
measure distinct same-length label edits and saved-state translation of an
interior unconnected node. Camera scale starts at 1.5, an explicitly characterized setup that passes the
unchanged quality gates for all three workloads and stays inside resolution
bucket 2 during the small camera updates. Failed scale cases remain recorded
below. Warmups are excluded from the reported distributions.

Each camera phase has six measured samples after two warmups; source and transform
phases have four after two warmups. Source strings are distinct to bypass the
viewport's SVG cache. Public renderer and canvas-state APIs exercise source
layout and scene updates; this is not a measurement of editor keystroke latency.
The starter also exercises real pointer selection and Escape with the existing
source/history checks and absent-overlay pixel control.

Passive wrappers observe the application's original request-device, queue-submit
and error-scope calls. They return the original values/promises, create no
additional GPU device and submit no profiling work. Device identity and adapter
information are recorded from the actual application request. The same observer
runs on both revisions; shared-device events may include Uno UI work.
Only promises completed by the acknowledgement snapshot contribute duration
samples; observed, completed and pending counts make that boundary explicit.
Raster cache hit/miss summaries use per-operation differences of lifetime
counters. Camera compilation/cache summaries stay null because camera movement
retains the last compilation snapshot without creating a new compilation.

Baseline and candidate SVG reference pixels must match exactly, and paired GPU
RGB channels may differ by at most one quantization unit. The existing GPU/SVG
quality budgets remain unchanged. Timing distributions are descriptive; they are
not a pass/fail performance threshold. The paired harness uses nearest-rank
p50/p95 order statistics. Its legacy JSON fields named `median` contain p50;
with six/four measured samples, p50 selects the lower middle observation and
p95 is the sample maximum. Raw samples remain available for other estimators.

Measurements separate:

1. Browser interaction-to-accepted-frame wall duration.
2. JavaScript compilation and cache counters.
3. Native command encoding.
4. Error-scope promise/callback and dispatcher timing where observable.
5. Draw calls, live resources, uploads and render-bundle rebuilds.
6. Paired GPU/SVG pixel comparisons and selection-mask evidence.

Compilation time is elapsed wall time inside the scene compiler, including its
raster awaits; it excludes the preceding `document.fonts.ready` wait. Public
operation, compilation, native encoding, scope and dispatcher measurements have
different boundaries and can overlap. They must not be added as disjoint stages.

The pinned Uno 7.0.32 implementation has a separate compositor limit:
[`WebGpuBrowserGraphicsContext`](https://github.com/unoplatform/uno/blob/78c746d28293975e64ba0f162af6f44f3c3daaab/src/Uno.UI.Composition.WebGpu.Init/WebGpuBrowserGraphicsContext.cs)
allows at most two presented frames in flight, counted until the application's
shared [queue completion promise](https://github.com/unoplatform/uno/blob/78c746d28293975e64ba0f162af6f44f3c3daaab/src/Uno.UI.Composition.WebGpu/ts/Runtime/WebGpuInit.ts)
settles. The [custom canvas paint hook](https://github.com/unoplatform/uno/blob/78c746d28293975e64ba0f162af6f44f3c3daaab/src/AddIns/Uno.WinUI.Graphics3DWebGpu/WebGpuCanvasElement.cs)
runs while the Uno frame is recorded. Operation-to-acknowledgement wall time can
therefore include a wait before diagram encoding begins. Short native scope and
dispatcher intervals would not establish that removing the diagram validation
gate improves that wait.

Error scopes are popped inside that paint hook, before Uno finishes recording
and submitting the overall frame. Their callbacks cannot run while the same
browser JavaScript/WebAssembly stack remains busy, so scope duration can also
contain subsequent Uno CPU work and event-loop delay. Scope completion is not
a GPU execution-completion measurement.

A pass must retain current scene revision and surface-size agreement. Existing
source/history assertions, unsupported-adapter behavior and pixel thresholds
remain in force. Profiling must use Uno's existing device and must not add a
second renderer or GPU submissions merely to obtain a timing number.

The accepted migration baseline passed [full CI #72](https://github.com/wieslawsoltes/UnoUML/actions/runs/43172243733):
981 GPU model assertions, 4,065 core assertions, 296 renderer fixtures, shader
and attached-browser checks, the full desktop/mobile sequence and all six
independent authoring suites. Its [focused import/native run](https://github.com/wieslawsoltes/UnoUML/actions/runs/43173095882)
also passed held-write read-only transitions, byte-exact conflicts and all
28 native compatibility cases.

## Results

### Full implementation acceptance

[Full integration CI](https://github.com/wieslawsoltes/UnoUML/actions/runs/43316750481)
passed on `d304eab058143c49f8274a1cd98da5a1f4e41771`. The subsequent
SDK-selection workflow fix changes no application code or measurement logic.

| Gate | Result |
| --- | --- |
| Native GPU scene model | 1,291 assertions, 22 cases |
| Core model | 4,065 assertions, 33 templates, 149 tools |
| Node tests | 99 tests across eight suites |
| Managed catalog | 296 fixtures |
| Additional renderer checks | 161 DOT sizing, 302 canvas metadata, 73 structured and 87 Gantt assertions |
| Production shaders | Seven pipelines, 4× MSAA, 29 existing pixel checks |
| Direct/stencil equivalence | Eight complete frames, zero differing RGBA channels |
| Attached-browser compiler | 265 assertions across 18 scenarios |
| Retained overlays | 67 assertions across ten cases |
| Native application | 24 camera acknowledgements with retained resources, visible selection paint and no-WebGPU fallback |
| Published workspace | Desktop/mobile flow plus all six independent structured, DOT, files, network, canvas and Gantt scenarios |

The full browser suite also covers source/history preservation, imports and ZIP
members, native input/focus/modal semantics, native compatibility, free geometry,
automatic layout, save/open and draft restoration.

### Accepted starter and retained-camera evidence

The [passing artifact extraction](https://github.com/wieslawsoltes/UnoUML/actions/runs/43322866007)
reads the actual `gpu-presentation.json` from the successful integration job,
artifact `13989416760`.

The starter uses **65 draws instead of 88**, a **26.1% reduction**. All 52 commands
remain visible, including 23 eligible direct meshes; 35 geometry buffers,
17 image textures and 52 retained bundles remain present. Vertex/image payloads
are 25,008 and 247,368 bytes. Its recorded mean channel error is exactly
`0.733578218016399`, and its major-difference fraction is exactly
`0.0024254464787150398`, matching the documented pre-optimization starter
metrics. This confirms reduced draw work without changing those quality results.

Across 24 accepted camera updates, uploads total **1,536 camera bytes**—64 per
update—and **zero** geometry, texture, draw-uniform or gradient bytes. No render
bundle is rebuilt. All 24 frames are submitted and acknowledged, with zero
additional deferred render requests in this interval. This retention behavior
was already established by the baseline and remains intact.

| Candidate camera diagnostic | Recorded p50 | Recorded p95 |
| --- | ---: | ---: |
| Native encoding | 0.5999 ms | 2.2 ms |
| Native scope callback interval | 42 ms | 212.1 ms |
| Native dispatcher interval | 0.3 ms | 45.7999 ms |
| Operation-to-acknowledgement wall interval | 1,522.8 ms | 2,893.5 ms |

These are descriptive samples from Chromium 156 on SwiftShader, not a paired
latency comparison or a hardware FPS result. They preserve the existing smoke
test's sorted-index convention, `floor(n / 2)` and `floor(0.95 * n)`, rather than
recomputing its stored summaries with the paired harness's nearest-rank method. The boundaries differ and may overlap.
For example, revision 22 has 2,893.5 ms wall time but only 35.9 ms scope and
0.3999 ms dispatcher intervals; revision 14 has 1,522.8 ms wall time with 33.0999 ms
scope and 0.2 ms dispatch. Removing the local validation gate would not explain
most of the delay in those samples. It remains unchanged.

Selection retains the same 654-pixel negative-control mask, with 98.1651% of those
pixels closer to the painted reference than to the absent-overlay control.
Clearing selection changes viewport command count from ten to one, uploads
exactly one 256-byte viewport uniform and rebuilds one bundle. Geometry and
textures remain resident. The captured browser error list is empty.

### Benchmark setup and fractional-scale evidence

The first paired attempt stopped before measurement because workload installation
ran above both checkouts and selected the runner's .NET 11 SDK. The application
checkouts both select .NET 10. The correction in `c119325f` verifies equal
SDK selection and installs `wasm-tools` inside the candidate checkout.

[The corrected run](https://github.com/wieslawsoltes/UnoUML/actions/runs/43318194480)
built both immutable applications successfully. At the initially selected
0.9 camera scale (resolution bucket 1), all 12 contexts failed the existing
hard ink-count check before timed phases. The same failure occurs in the
baseline and candidate; no latency or throughput gain can be inferred from it.

[Artifact inspection](https://github.com/wieslawsoltes/UnoUML/actions/runs/43320896666)
confirmed matching geometry/camera positions, native acknowledgement and 4× MSAA,
with no GPU scope errors. The screenshots show sharper threshold-crossing GPU
vector edges than the SVG reference at that fractional scale.

| Workload at 0.9× | SVG ink pixels | GPU ink pixels | GPU/SVG ink | Mean channel error / 255 | Major difference fraction |
| --- | ---: | ---: | ---: | ---: | ---: |
| Starter | 3,030 | 3,481 | 1.148845 | 0.34039 | 0.00015795 |
| 64 classes | 20,162 | 28,559 | 1.416477 | 1.52437 | 0.00210077 |
| 256 classes | 18,764 | 29,831 | 1.589800 | 1.52605 | 0.00210252 |

The second baseline 256-class reference contained 18,790 ink pixels; GPU ink
remained 29,831. That small reference variation also requires investigation before
claiming exact repeated-reference agreement. The mean-error and major-difference
budgets pass, but the ink-ratio budget does not. No quality threshold has been
relaxed. The initial 0.9× setup is not an accepted performance benchmark.

The saved cold frames record the intended draw reductions (88→65, 577→457 and
641→505), matching 23, 120 and 136 visible direct meshes. These are structural
draw-count observations from a failed-quality setup, not warmed latency results.

### Fixed-scale and coverage characterization

The [focused diagnostic](https://github.com/wieslawsoltes/UnoUML/actions/runs/43324405731)
uses the deployed immutable baseline, verifies its content-directory assets
against `b25c9901`, and records 18 captures on Chromium 157.0.8103.78.
Every capture has an accepted native revision, 4× MSAA and unchanged scene
geometry during comparison. The diagnostic deliberately retains all ten failing
quality cases and exits unsuccessfully; it does not turn them into passes.

| Scale | Starter GPU/SVG ink | 64 classes GPU/SVG ink | 256 classes GPU/SVG ink |
| --- | --- | --- | --- |
| 0.9 | 1.1488 — fails | 1.4165 — fails | 1.5898 — fails |
| 1.0 | 0.9927 — passes | 0.6902 — fails | 0.6884 — fails |
| 1.25 | 0.9599 — passes | 0.8679 — fails | 0.8682 — fails |
| 1.5 | 0.9761 — passes | 1.0052 — passes | 1.0108 — passes |
| 1.65 | 0.9911 — passes | 0.9942 — passes | 0.9950 — passes |

All listed failures are the existing binary ink-ratio gate. The mean-error and
major-difference budgets pass at these scales. At 1.5, mean channel errors are
0.6820, 1.4302 and 1.3873 respectively, with major-difference fractions 0.0026,
0.0069 and 0.0066.

The recorded RGB profiles explain why a binary ink count must not be interpreted
as geometric coverage. At the sampled 64-class edge at scale 1.0, adjacent GPU
rows are `[150,151,160]` and `[147,147,155]`; SVG rows are
`[149,151,159]` and `[146,147,154]`. The strict minimum-channel threshold
below 150 counts one GPU row and two SVG rows, although their integrated
coverage is 0.9956 and 1.0026. At 0.9, the same edge's integrated coverage is
0.9956 versus 0.9487; integrated darkness over the whole scene differs by about
1%, while the binary ink ratio differs by 41.6%. This establishes threshold
amplification in the inspected pixels; it does not establish universal
cross-scale equivalence.

A separate, restored reference-only experiment changes `will-change: transform`
to `auto` without changing SVG geometry or camera matrices. It changes 64,651
reference pixels for the 64-class scene and 65,201 for 256 classes. Those
captures are diagnostic controls only; the shared comparison helper, application
CSS and benchmark quality budgets are unchanged.

Immediate, scale-roundtrip and fresh-context repetitions of the 256-class
0.9 scene have zero differing SVG or GPU pixels. The earlier 26-ink-pixel
reference variation did not reproduce. The paired benchmark therefore keeps
its exact SVG-reference comparison and records the initial SVG, camera and font
state so a recurrence can be inspected.

The common measurement scale is now **1.5**, with resolution bucket 2. It passes
every existing gate for all three scenes and gives sampled edge coverage
1.5083 versus 1.5134. The 64/256-class scenes each expose 144 visible commands
while retaining 384/1,536 total commands, so scene size and visible work are
reported separately. This is an accepted setup for the comparison, not a fix
for all fractional-scale rasterization differences. Camera normalization also
accepts an already matching validated frame instead of waiting for a revision
that an unchanged camera deliberately does not emit.

Paired measurements at this characterized setup are pending.
