# Packet and chart authoring

`StudioDocument` exposes structural packet and chart edits using immutable source snapshots. A successful operation changes only the necessary source spans and creates one undo entry. Failed operations leave source and history unchanged. Handles carry their page and exact source identity; reopen them after changing the document or active page.

```csharp
var packet = document.OpenPacketDiagram()!;
var header = packet.Fields[0];
document.ResizePacketField(header, bitCount: 16, shiftFollowing: true);

var chart = document.OpenChartDiagram()!;
document.InsertChartCategory(chart, index: 1, label: "Q2", values: [42, 51]);
```

The final argument in the category example supplies one value per series, in source order. Category operations change the category axis and every matching series column together.

## Packet operations

| API | Behavior |
| --- | --- |
| `OpenPacketDiagram()` | Parses the active packet page, including an empty packet. |
| `OpenPacketField(SourceFieldEdit)` | Resolves a renderer's exact label token to a typed field. |
| `InsertPacketField(packet, index, bitCount, label)` | Inserts at a final index and shifts following addresses by the inserted width. Existing gaps remain. |
| `RemovePacketField(field, closeGap: false)` | Removes the declaration. With `closeGap: true`, following addresses move backward by its width. |
| `SetPacketFieldRange(field, firstBit, lastBit)` | Changes the range while keeping other fields at their existing addresses. |
| `ResizePacketField(field, bitCount, shiftFollowing: true)` | Changes the field width. Shifting retains gaps; disabling it requires the expanded range to fit. |
| `SplitPacketField(field, firstBitCount, secondLabel)` | Creates adjacent first and second parts. Attributes are retained on both parts; an inline comment stays on the first. |
| `MovePacketField(field, index)` | Reorders declarations, retaining their widths and the gaps associated with each position. Addresses are recomputed. |

All ranges must be nonnegative, ordered and nonoverlapping. The native parser bounds packets to 128 columns, 360 rows and 2,048 fields. These limits are validated before a source change is committed.

Automatic `*` declarations and `len` attributes retain their spelling when they still express the intended address. A structural change that would otherwise alter an automatic field's address writes an explicit range. The following example becomes a three-field packet in one transaction:

```csharp
var packet = document.OpenPacketDiagram()!;
document.SplitPacketField(packet.Fields[0], firstBitCount: 7, secondLabel: "Flag");
```

```plantuml
@startpacketdiag
* Header [len=8]
* Payload [len=8]
@endpacketdiag
```

Native rendering accepts explicit single-bit/range and automatic fields, optional packet braces, `len`, `height`, `rotate`, `colwidth`, `node_height`, `same_height`, `scale_interval` and `scale_direction`. Rotation supports 0, 90, 180 and 270 degrees. Field labels and their exact source spans remain editable when a field crosses rows.

## Chart operations

| API | Behavior |
| --- | --- |
| `OpenChartDiagram()` | Opens an immutable chart snapshot with series, categories and mode information. |
| `InsertChartSeries(chart, index, kind, label, values, ...)` | Inserts an indexed series. Optional arguments set color, secondary axis, visible labels, interpolation and connection across missing values. |
| `InsertChartIndexedSeries(chart, index, kind, label, values, ...)` | Accepts nullable values; a null value creates an empty slot in a line series. |
| `InsertChartCoordinateSeries(chart, index, kind, label, points, ...)` | Inserts a line/scatter series containing explicit `(x, y)` points. |
| `RemoveChartSeries(series)` / `MoveChartSeries(series, index)` | Removes or reorders the complete declaration and its attached inline comment. Separate comment lines stay in place. |
| `InsertChartCategory(chart, index, label, values)` | Inserts one category and one value into every series. |
| `RemoveChartCategory(chart, index)` | Removes a category and its entire data column. An annotation anchored exclusively to that category prevents removal. |
| `MoveChartCategory(chart, index, targetIndex)` | Moves a category and the corresponding value in every series. |
| `RenameChartCategory(chart, index, label)` | Updates the label and any annotation references to that uniquely named category. |
| `InsertChartPoint(series, index, point)` | Inserts into an explicit-coordinate or automatically indexed series. |
| `RemoveChartPoint(point)` / `MoveChartPoint(point, index)` | Removes or reorders a point in an explicit-coordinate or automatically indexed series. |
| `SetChartPoint(point, value)` | Updates Y and, for coordinate series, X in one transaction. |
| `SetChartLineOptions(series, interpolate, connect)` | Changes the line flags while preserving its data, color, axis choice, labels and comments. |

Charts with an explicit category axis use category-column operations for inserting, removing and moving points. They allow individual Y updates through `SetChartPoint`. Numeric-axis indexed series and coordinate series permit independent point counts. A series retains at least one point, and an explicit axis retains at least one category. The final series may be removed; the inspector can insert a replacement.

The shared parser verifies finite coordinates, axis bounds, secondary-axis selection, matching category counts, stacked positive/negative totals, and annotation anchors. The existing direct label/value editor uses these same checks. Thus a direct numeric edit cannot leave an invalid stacked chart, and changing a category caption updates its annotation anchor atomically.

Untouched numeric literals retain their precision and notation. Reordering keeps tuples together. Insertions reuse list separators without duplicating comments; removal retains block comments between values. Source outside the active page, BOMs, line endings, style declarations, series flags and annotations are retained.

### Axes, settings and annotations

`ChartDiagramEdit` exposes immutable `HorizontalAxis`, `VerticalAxis`, `SecondaryAxis`, `Settings` and `Annotations` snapshots. Inferred axes have `IsExplicit == false`; defining one writes its declaration without rewriting the series.

| API | Behavior |
| --- | --- |
| `SetChartAxis(chart, kind, definition)` | Creates or edits an axis label, numeric range or categories, spacing, grid, named ticks and label position. Unique category renames also update annotation anchors. |
| `RemoveChartAxis(chart, kind, usePrimaryForSecondarySeries: false)` | Removes an explicit declaration and returns to inferred values. The optional argument removes secondary-axis flags in the same transaction, provided every series fits the primary domain. |
| `ApplyChartSettings(chart, settings)` | Applies title, legend, orientation and bar stacking together. Changing orientation exchanges the axis declarations and their label positions atomically. |
| `RemoveChartSetting(chart, setting)` | Removes one explicit setting. Removing horizontal orientation also restores the axes to vertical orientation. |
| `InsertChartAnnotation(chart, index, label, x, y, arrow)` | Inserts an annotation at an existing category or numeric coordinate within the axis bounds. |
| `UpdateChartAnnotation(annotation, label, x, y, arrow)` | Changes annotation text, anchor and arrow in one undo entry. |
| `RemoveChartAnnotation(annotation)` | Removes its declaration while retaining independent comments. |

Axis changes preserve unaffected literal notation and options. Category-count changes still use the column operations so every series stays aligned. Removing or changing an axis is rejected if an annotation or series would lose its valid anchor or domain. An empty title or `ChartLegend.None` removes the corresponding declaration; null orientation or stacking selects the implicit default.

```csharp
var chart = document.OpenChartDiagram()!;
document.SetChartAxis(chart, ChartAxisKind.Vertical,
    chart.VerticalAxis.Definition with
    {
        Label = "Requests",
        Minimum = 0,
        Maximum = 100,
        Spacing = 20,
        Grid = true
    });

chart = document.OpenChartDiagram()!;
document.ApplyChartSettings(chart,
    chart.Settings with { Title = "Traffic", Legend = ChartLegend.Right });
```

### Native chart grammar

The native renderer supports bar, line, area and scatter series; explicit categorical or numeric axes; automatically inferred axes; horizontal orientation; grouped and stacked bars; coordinate-pair line/scatter data; secondary vertical axes; named or hexadecimal colors; visible data labels; axis grids, spacing and named ticks; axis label placement; category/numeric annotations and optional arrows; and legends at all four sides. It retains the original categorical renderer's geometry for previously supported inputs.

Indexed line data may contain empty slots. By default those slots break the polyline, while `connect` joins the available points. `interpolate` uses a natural cubic spline through the available points, with zero second derivatives at its endpoints. Successful interpolation joins gaps independently of `connect`. Fewer than two available points or repeated/decreasing X coordinates use the ordinary segment behavior. Coordinate pairs require both coordinates; the options and missing-value syntax belong only to line series. A final missing slot is written with whitespace so it survives the upstream Java list parser.

```plantuml
@startchart
h-axis [Mon, Tue, Wed, Thu, Fri]
v-axis 0 --> 100
line "Requests" [20, 35, , 70, 55] #336699 labels interpolate connect
@endchart
```

A chart can contain up to 64 series, 512 points per series, 8,192 total points, 512 categories, 256 annotations and 128 tick intervals per axis. Explicit numeric tokens are finite and limited to magnitude `1e12`.

Other syntax, including per-series scatter marker suffixes and style properties that require unsupported text/layout metrics, continues through the existing compatibility renderer. The structural parser also declines unsupported commands rather than editing a partially understood page.

## Inspector and verification

The diagram inspector exposes insertion and selection controls. Selecting a packet field, chart category, series label or point exposes the matching operations. Dialog controls have accessible names and support keyboard acceptance. The chart point dialog edits both coordinates and can mark indexed line values as missing; series insertion exposes data format, color, labels, secondary-axis choice and line options. Chart axes, settings and annotations have dedicated inspector dialogs, including creation and removal of explicit axes and annotations.

Focused regressions live in `StructuredEditCases`, `StructuredRendererCases` and `tests/browser/structured-editing.mjs`. They cover exact source/undo behavior, stale handles, mixed line endings, comments, packet ranges, coordinate tuples, category alignment, stacked totals, missing data, pinned spline geometry, axis and setting transactions, annotation references, native metadata and inspector round trips. Browser assertions wait for the rendered semantic change after a dialog commits.

## Grammar references

- [Official PlantUML chart documentation](https://plantuml.com/chart-diagram)
- [Official line command grammar](https://github.com/plantuml/plantuml/blob/master/src/main/java/net/sourceforge/plantuml/chart/command/CommandChartLine.java)
- [Official line rendering and gap behavior](https://github.com/plantuml/plantuml/blob/master/src/main/java/net/sourceforge/plantuml/chart/LineRenderer.java)
- [Official natural spline geometry](https://github.com/plantuml/plantuml/blob/master/src/main/java/net/sourceforge/plantuml/klimt/geom/NaturalSpline.java)
- [Official annotation command grammar](https://github.com/plantuml/plantuml/blob/master/src/main/java/net/sourceforge/plantuml/chart/command/CommandChartAnnotation.java)
- [Official packet command implementation](https://github.com/plantuml/plantuml/tree/master/src/main/java/net/sourceforge/plantuml/packetdiag/command)
- [Official packet diagram API](https://plantuml.github.io/plantuml/javadoc/net/sourceforge/plantuml/packetdiag/PacketDiagramFactory.html)

The lexical models are shared source compiled into the Core and PlantUml assemblies. The renderer and editor therefore validate the same grammar without introducing a runtime dependency between those assemblies.
