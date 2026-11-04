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
| `InsertChartSeries(chart, index, kind, label, values, ...)` | Inserts an indexed series. Optional arguments set color, secondary axis and visible labels. |
| `InsertChartCoordinateSeries(chart, index, kind, label, points, ...)` | Inserts a line/scatter series containing explicit `(x, y)` points. |
| `RemoveChartSeries(series)` / `MoveChartSeries(series, index)` | Removes or reorders the complete declaration and its attached inline comment. Separate comment lines stay in place. |
| `InsertChartCategory(chart, index, label, values)` | Inserts one category and one value into every series. |
| `RemoveChartCategory(chart, index)` | Removes a category and its entire data column. An annotation anchored exclusively to that category prevents removal. |
| `MoveChartCategory(chart, index, targetIndex)` | Moves a category and the corresponding value in every series. |
| `RenameChartCategory(chart, index, label)` | Updates the label and any annotation references to that uniquely named category. |
| `InsertChartPoint(series, index, point)` | Inserts into an explicit-coordinate or automatically indexed series. |
| `RemoveChartPoint(point)` / `MoveChartPoint(point, index)` | Removes or reorders a point in an explicit-coordinate or automatically indexed series. |
| `SetChartPoint(point, value)` | Updates Y and, for coordinate series, X in one transaction. |

Charts with an explicit category axis use category-column operations for inserting, removing and moving points. They allow individual Y updates through `SetChartPoint`. Numeric-axis indexed series and coordinate series permit independent point counts. A series retains at least one point, and an explicit axis retains at least one category. The final series may be removed; the inspector can insert a replacement.

The shared parser verifies finite coordinates, axis bounds, secondary-axis selection, matching category counts, stacked positive/negative totals, and annotation anchors. The existing direct label/value editor uses these same checks. Thus a direct numeric edit cannot leave an invalid stacked chart, and changing a category caption updates its annotation anchor atomically.

Untouched numeric literals retain their precision and notation. Reordering keeps tuples together. Insertions reuse list separators without duplicating comments; removal retains block comments between values. Source outside the active page, BOMs, line endings, style declarations, series flags and annotations are retained.

### Native chart grammar

The native renderer supports bar, line, area and scatter series; explicit categorical or numeric axes; automatically inferred axes; horizontal orientation; grouped and stacked bars; coordinate-pair line/scatter data; secondary vertical axes; named or hexadecimal colors; visible data labels; axis grids, spacing and named ticks; axis label placement; category/numeric annotations and optional arrows; and legends at all four sides. It retains the original categorical renderer's geometry for previously supported inputs.

A chart can contain up to 64 series, 512 points per series, 8,192 total points, 512 categories, 256 annotations and 128 tick intervals per axis. Explicit numeric tokens are finite and limited to magnitude `1e12`.

Other syntax, including interpolation/connect options and style properties that require unsupported text/layout metrics, continues through the existing compatibility renderer. The structural parser also declines unsupported commands rather than editing a partially understood page.

## Inspector and verification

The diagram inspector exposes insertion and selection controls. Selecting a packet field, chart category, series label or point exposes the matching operations. Dialog controls have accessible names and support keyboard acceptance. The chart point dialog edits both coordinates; series insertion exposes data format, color, labels and secondary-axis choice.

Focused regressions live in `StructuredEditCases`, `StructuredRendererCases` and `tests/browser/structured-editing.mjs`. They cover exact source/undo behavior, stale handles, mixed line endings, comments, packet ranges, coordinate tuples, category alignment, stacked totals, annotation references, native metadata and inspector round trips.

## Grammar references

- [Official PlantUML chart documentation](https://plantuml.com/chart-diagram)
- [Official packet command implementation](https://github.com/plantuml/plantuml/tree/master/src/main/java/net/sourceforge/plantuml/packetdiag/command)
- [Official packet diagram API](https://plantuml.github.io/plantuml/javadoc/net/sourceforge/plantuml/packetdiag/PacketDiagramFactory.html)

The lexical models are shared source compiled into the Core and PlantUml assemblies. The renderer and editor therefore validate the same grammar without introducing a runtime dependency between those assemblies.
