# WebGPU performance work

## Baseline and scope

This work follows [the Uno 7 migration](uno7-webgpu-migration.md), merged in
[PR #1](https://github.com/wieslawsoltes/UnoUML/pull/1) at
`b25c990167b13db19dca318ca2f25420085e3518`. The merge has the same application
tree as accepted revision `3c1dac1310b2bb880831cf0ef484e33c737bab89`.

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

## Findings being addressed

- A sparse matrix-only command update currently recomputes local bounds by
  visiting inherited vertices. Unchanged local vertices and bounds can retain
  the previously validated local bounds.
- Frame statistics currently sum all command vertices and image payloads.
  Accepted scene totals can be maintained transactionally and read without
  scanning the scene during every camera frame.
- SVG replacement clears the raster cache and submitted display-list baseline.
  Cached resources with the same complete frozen content, styles, font data,
  scale and tile geometry can remain reusable across source revisions.
- An obsolete asynchronous compilation continues through subsequent resource
  work before the outer generation check rejects its result. Cancellation
  checkpoints can stop this work at asynchronous boundaries.
- Every mesh currently uses stencil geometry followed by paint coverage.
  An unclipped solid fully opaque mesh can potentially use direct paint because
  overlapping samples overwrite the same opaque color. Analytic clipping,
  transparency and gradients require the existing union path unless equivalence
  is separately established. Arbitrary fill fans are not assumed non-overlapping.
- New native frames currently wait for both error-scope callbacks from the prior
  frame and their UI-dispatch completion. This is a profiling target; callback
  latency is not yet established as the dominant cause.

## Comparison protocol

The paired benchmark will use the same harness and runner configuration for
explicit immutable baseline and candidate commits, in ABBA order. Workloads
include the starter and larger diagrams, with the same viewport, pixel density,
4× MSAA and visual thresholds.

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

Implementation measurements and accepted candidate results will be recorded here
as they become available. The findings above are not performance speedup claims.
