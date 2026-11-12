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
interior unconnected node. Camera scale starts at 0.9 to avoid resolution-bucket
crossings. Warmups are excluded from the reported distributions.

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

Baseline and candidate SVG reference pixels must match exactly, and paired GPU
RGB channels may differ by at most one quantization unit. The existing GPU/SVG
quality budgets remain unchanged. Timing distributions are descriptive; they are
not a pass/fail performance threshold.

Measurements separate:

1. Browser interaction-to-accepted-frame wall duration.
2. JavaScript compilation and cache counters.
3. Native command encoding.
4. Error-scope promise/callback and dispatcher timing where observable.
5. Draw calls, live resources, uploads and render-bundle rebuilds.
6. Paired GPU/SVG pixel comparisons and selection-mask evidence.

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

### Implementation correctness

At implementation commit `bcd6446cb3181b360266144178ce34ae71f2b292`,
the [shader/compiler job](https://github.com/wieslawsoltes/UnoUML/actions/runs/43315281818/job/131521570365)
passed seven pipeline creations, all 29 existing pixel checks and eight full-frame
direct/stencil comparisons with **zero differing RGBA channels**. The attached
browser compiler passed **265 assertions across 18 scenarios**, including the
new cancellation, captured-pixel reuse and invalidation cases. Existing native
input, focus, coordinates and retained-overlay checks also passed.

Native/application integration CI and paired performance measurements are pending.
These correctness results do not yet quantify latency or throughput improvements.
