# Gantt schedule authoring

Gantt editing uses a schedule model shared by `UnoUML.Core` and the standalone C# renderer. Canvas bars represent inclusive calendar-day intervals. Their geometry is derived from task dates, durations and dependencies; a timeline gesture updates those source constraints through the document's normal undo history.

## Timeline gestures

| Action | Result |
| --- | --- |
| Drag a task bar horizontally | Move its start by whole days. The task receives an explicit `D+N` start instead of its previous start dependency. |
| Drag the finish handle | Change duration while retaining the task's start dependency. |
| Drag the start handle | Keep the finish date, change duration and establish an explicit start. |
| Drag a milestone | Move its single calendar day. Milestones have no resize handles. |
| Arrow left/right | Move the selected task by one day. Shift moves seven days. A held-key sequence is one edit. |
| Arrow keys on a resize handle | Adjust that endpoint using the same interval rules as pointer resizing. |
| Escape or pointer cancellation | Restore the preview without changing source or history. |
| Double-click a task caption or press F2 | Rename the exact task, preserving its alias and updating applicable caption references. |

The inspector exposes schedule settings, task/milestone insertion, dates, duration, predecessor and predecessor endpoint, completion, fill color, task rename, row reordering and removal. Day zero is project start. A project date is optional; an undated diagram uses numbered days. The inspector exposes a calendar footer toggle and a schedule title.

Deleting a predecessor is guarded. The removal dialog can detach direct dependents and keep them at their current scheduled dates; their downstream dependencies continue to resolve normally. Structural deletion never leaves an undeclared dependency silently attached to a different task.

## Supported source model

The shared parser handles:

* Tasks and explicit aliases, including different tasks with the same caption and distinct aliases.
* `lasts`/`requires` durations in whole days or weeks, using a seven-day calendar week.
* Inclusive absolute start/end dates and relative `D+N` dates.
* Start-to-start and finish-to-start constraints; milestones at a predecessor's start or finish.
* Compact and spaced arrow dependencies, `and` clauses, and inline or standalone `then` chains.
* Forward relative constraints that resolve to declared tasks, with cycle validation.
* Completion percentages, six-digit hexadecimal task colors, a title, project start and `hide footbox`.
* Line comments and block comments, with ordinary apostrophes and conjunctions inside bracketed names retained.

Task names and aliases use exact source spans. An explicit alias is retained even when it initially matches the caption. Renaming a caption does not accidentally create a second task through an old arrow or dependency reference. A bar and its label select the same exact task token, including when several tasks share one physical `then` line.

## Source and history guarantees

Each operation validates the resulting complete schedule before committing. Stale handles, cycles, invalid dates, conflicting constraints, invalid percentages and exceeded limits leave the source and history unchanged. BOMs, UTF-16 offsets, CR/LF/CRLF and other diagram pages are retained.

Simple name edits patch individual tokens. Schedule restructuring normalizes the affected task contributions into explicit statements. Reordering normalizes task contributions into the requested display order. Comments on normalized rows remain in the document exactly once, and unrelated directives and rows retain their source. Implicit `then` relationships are materialized when needed so removal of an earlier contribution cannot change what a surviving task depends on. An edit that would detach a delimiter from a multi-line block comment is declined.

Native SVG exposes the actual task identity, start offset, duration, day scale and exact label token. The browser previews a gesture and sends one revision-bound command; C# performs the semantic mutation and reschedules dependents. Selection handles are outside the SVG and do not appear in SVG, PNG or print export. Generic free-canvas geometry metadata is not added to a schedule.

## C# API

```csharp
using UnoUML.Core;

var document = new StudioDocument("""
    @startgantt
    Project starts 2026-01-05
    [Design] as [D] lasts 3 days
    [Build] starts at [D]'s end and lasts 5 days
    @endgantt
    """);

var chart = document.OpenGanttDiagram()
    ?? throw new InvalidOperationException("Unsupported schedule.");
var build = chart.Tasks.Single(task => task.Id == "Build");

// Resizing the finish preserves Build's dependency on D.
if (!document.ResizeGanttTask(build, 8))
    throw new InvalidOperationException("The schedule change was rejected.");

// Handles are immutable source snapshots; reopen after a mutation.
chart = document.OpenGanttDiagram()!;
if (!document.InsertGanttTask(chart, chart.Tasks.Count, "Release", "R",
    new GanttTaskSchedule(0, IsMilestone: true,
        PredecessorId: "Build", Color: "#4266a8")))
    throw new InvalidOperationException("The milestone could not be inserted.");
```

Other operations are `RenameGanttTask`, `SetGanttTaskSchedule`, `MoveGanttTask`, `ReorderGanttTask`, `RemoveGanttTask` and `SetGanttSettings`. `GanttTaskEdit` exposes resolved start/end offsets, duration, milestone state, predecessor, completion and color.

## Limits and compatibility

The native model supports at most 760 tasks and a 1,096-day horizon. It uses calendar days. Working-day calendars, holidays, resource workload scheduling, multiple incoming schedule constraints, forward arrow targets later redefined with different captions, extended styling, hyperlinks and non-daily print scales remain with the compatibility renderer and source editor. Captions transformed by preprocessing, including block comments embedded inside bracketed names, do not receive literal canvas gestures. Cut/paste of complete scheduled tasks remains a source operation; a caption-only clipboard does not authorize removing a task. Unsupported pages are not partially interpreted by this editor.

The syntax and inclusive-date behavior follow the [PlantUML Gantt documentation](https://plantuml.com/gantt-diagram), with focused native and browser regression cases against the repository's pinned compatibility stack.
