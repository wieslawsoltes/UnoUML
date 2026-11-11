# Uno Platform 7 and WebGPU migration

## Purpose

Migrate the browser workspace to Uno Platform 7 and make WebGPU the preferred
presentation backend for both the application UI and the diagram viewport.
Preserve the standalone C# PlantUML engine, source mappings, structural editing,
free canvas geometry, automatic layout, accessibility, and vector exports.

This document is the migration log for the implementation PR. Status and
validation evidence are updated with the implementation commits; planned work
below is not a claim of completed support or measured performance.

## Starting point

The starting revision is `6f456893930e6b45d1b7bb37cf1cbe07a018d93a`.
The app targets `net10.0-browserwasm`, uses Uno.Sdk 6.7.30 and the
`NativeRenderer` feature, and displays sanitized source-mapped SVG in a native
HTML host. Source input, accessibility, drag/drop, and scroll adapters include
dependencies on the old per-control DOM implementation.

Uno 7 removes that DOM renderer. The migration must replace those adapters,
configure the supported application host, and preserve native HTML input and
diagram interoperability. A framework version change alone is insufficient.

## Rendering design

* Prefer Uno's WebGPU composition backend for the XAML workspace, with an
  explicit supported recovery path when WebGPU initialization is unavailable.
* Retain the SVG semantic projection for source addressing, precise geometry
  queries, interaction, and portable SVG/print export.
* Compile diagram paint into a retained GPU scene. Vector geometry, strokes,
  paint, image/text resources, transforms, and clipping must have explicit
  representation and resource ownership.
* Present the diagram through WebGPU shaders with device-pixel-aware sizing,
  antialiasing, transparent composition, bounded caches, and demand-driven
  frames. Panning and zooming should update the camera rather than recreate
  the entire source diagram.
* Preserve unsupported SVG paint faithfully through an explicit compatibility
  path; never silently omit visible content or misreport the active backend.
* Handle initialization failure, device loss, stale asynchronous compilation,
  resize, and disposal without corrupting source or editor history.

## Validation plan

1. Build and publish the Uno 7 browser app and reusable C# libraries.
2. Run the existing source, native renderer, catalog, and workspace regressions.
3. Add focused GPU geometry, paint, lifecycle, and browser rendering checks.
4. Exercise source editing, pointer gestures, keyboard input, accessibility,
   exports, imports, and mobile interaction with the new host.
5. Record repeatable rendering-quality comparisons and performance measurements
   with the backend, adapter, workload, DPR, and warm/cold state identified.

No performance multiple or production hardware result is claimed until measured.

## Official references

* [Uno 7 migration guide](https://platform.uno/docs/articles/migrating-to-uno-7.html)
* [Uno WebGPU rendering backend](https://platform.uno/docs/articles/features/using-webgpu-rendering.html)
* [WebGPU specification](https://www.w3.org/TR/webgpu/)
* [WGSL specification](https://www.w3.org/TR/WGSL/)

## Progress

* Migration branch and review document created.
* Framework, browser-host, rendering, and regression contracts under inspection.
