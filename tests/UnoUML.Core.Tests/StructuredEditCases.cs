using UnoUML.Core;

internal static class StructuredEditCases
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool condition, string reason) { if (!condition) throw new Exception("Structured editing: " + reason); count++; }
        void Atomic(StudioDocument doc, string before, string reason)
        { string after = doc.Source; Check(doc.Undo() && doc.Source == before, reason + " exact undo"); Check(doc.Redo() && doc.Source == after, reason + " exact redo"); }
        foreach (string nl in new[] { "\n", "\r\n", "\r" })
        {
            string prefix = "\uFEFF@startuml first\nclass Untouched\n@enduml\n";
            string packet = ("@startpacketdiag layout\n{\n  colwidth = 16;\n  0-3: Version  ' retain\n  ' separator stays\n  6-9: Payload [height=2, rotate=270]\n  * Flags [len=2]\n}\n@endpacketdiag").ReplaceLineEndings(nl);
            var doc = new StudioDocument(prefix + packet) { ActivePage = 1 };
            var snapshot = doc.OpenPacketDiagram()!;
            Check(snapshot.Fields.Count == 3 && snapshot.Fields[2].FirstBit == 10, "packet auto range");
            Check(doc.InsertPacketField(snapshot, 1, 3, "Type"), "insert packet field");
            Check(doc.Source == prefix + packet.Replace("  6-9: Payload", "  6-8: Type" + nl + "  9-12: Payload", StringComparison.Ordinal), "insert preserves gaps, comments, auto spelling and line endings");
            Check(!doc.ResizePacketField(snapshot.Fields[0], 5), "stale packet snapshot rejected");
            Atomic(doc, prefix + packet, "insert packet");
            doc.Undo(); snapshot = doc.OpenPacketDiagram()!;
            Check(doc.ResizePacketField(snapshot.Fields[0], 6), "resize packet");
            Check(doc.Source == prefix + packet.Replace("0-3: Version", "0-5: Version", StringComparison.Ordinal).Replace("6-9: Payload", "8-11: Payload", StringComparison.Ordinal), "resize preserves gaps and automatic successor");
            Atomic(doc, prefix + packet, "resize packet");
            doc.Undo(); snapshot = doc.OpenPacketDiagram()!;
            Check(!doc.SetPacketFieldRange(snapshot.Fields[0], 0, 6) && doc.Source == prefix + packet, "overlap rejected without partial edits");
            Check(!doc.ResizePacketField(snapshot.Fields[1], 16 * 360) && doc.Source == prefix + packet, "packet maximum bound enforced");
            Check(doc.RemovePacketField(snapshot.Fields[1]), "remove packet declaration");
            Check(doc.Source == prefix + packet.Replace("  6-9: Payload [height=2, rotate=270]" + nl, "", StringComparison.Ordinal).Replace("* Flags", "10-11 Flags", StringComparison.Ordinal), "remove retains independent comments and pins automatic successor");
            Atomic(doc, prefix + packet, "remove packet");
            doc.Undo(); snapshot = doc.OpenPacketDiagram()!;
            Check(doc.SplitPacketField(snapshot.Fields[1], 2, "Remainder"), "split packet");
            Check(doc.Source == prefix + packet.Replace("6-9: Payload [height=2, rotate=270]", "6-7: Payload [height=2, rotate=270]" + nl + "  8-9: Remainder [height=2, rotate=270]", StringComparison.Ordinal), "split retains both attributes and exact unrelated source");
            Atomic(doc, prefix + packet, "split packet");
            doc.Undo(); snapshot = doc.OpenPacketDiagram()!;
            Check(doc.MovePacketField(snapshot.Fields[2], 0), "move packet field");
            var moved = doc.OpenPacketDiagram()!;
            Check(moved.Fields.Select(f => f.Label).SequenceEqual(["Flags", "Version", "Payload"]) && moved.Fields.Select(f => f.FirstBit).SequenceEqual([0, 4, 8]), "move retains widths and slot gaps");
            Check(doc.Source.Contains("  ' separator stays" + nl, StringComparison.Ordinal) && doc.Source.StartsWith(prefix, StringComparison.Ordinal), "move retains separate comments and other page");
            Atomic(doc, prefix + packet, "move packet");
            doc.Undo(); snapshot = doc.OpenPacketDiagram()!;
            doc.ActivePage = 0;
            Check(!doc.RemovePacketField(snapshot.Fields[0]), "packet page guard");

            string chart = ("@startchart results\n' keep\n<Style>\nchartdiagram {\n  FontColor blue\n}\n</Style>\nh-axis \"Quarter\" [ A , B , C ] grid\nv-axis \"Count\" -20 --> 100 spacing 20\nbar <<budget>> \"One\" [ 1.00 , 2e0 , -3 ] #red labels ' first\n' stable separator\nline \"Two\" [ 4 , 5 , 6 ] #112233\nlegend right\n@endchart").ReplaceLineEndings(nl);
            doc = new StudioDocument(prefix + chart) { ActivePage = 1 };
            var c = doc.OpenChartDiagram()!;
            Check(c.HasCategoryLabels && c.Series.Count == 2, "chart advanced source grammar");
            Check(doc.InsertChartCategory(c, 1, "New", [7, 8]), "insert chart column");
            Check(doc.Source == prefix + chart.Replace("A , B", "A , New , B", StringComparison.Ordinal).Replace("1.00 , 2e0", "1.00 , 7 , 2e0", StringComparison.Ordinal).Replace("4 , 5", "4 , 8 , 5", StringComparison.Ordinal), "column preserves numeric spelling and separators");
            Check(!doc.RemoveChartCategory(c, 0), "stale chart rejected"); Atomic(doc, prefix + chart, "insert category");
            doc.Undo(); c = doc.OpenChartDiagram()!;
            Check(doc.MoveChartCategory(c, 2, 0), "move category");
            Check(doc.Source == prefix + chart.Replace("A , B , C", "C , A , B", StringComparison.Ordinal).Replace("1.00 , 2e0 , -3", "-3 , 1.00 , 2e0", StringComparison.Ordinal).Replace("4 , 5 , 6", "6 , 4 , 5", StringComparison.Ordinal), "category reorder retains data correspondence and literal precision");
            Atomic(doc, prefix + chart, "move category");
            doc.Undo(); c = doc.OpenChartDiagram()!;
            Check(doc.RemoveChartCategory(c, 1) && doc.OpenChartDiagram()!.Series.All(s => s.Points.Count == 2), "remove category from all series"); Atomic(doc, prefix + chart, "remove category");
            doc.Undo(); c = doc.OpenChartDiagram()!;
            Check(doc.MoveChartSeries(c.Series[1], 0) && doc.Source.Contains("line \"Two\" [ 4 , 5 , 6 ] #112233" + nl + "' stable separator" + nl + "bar <<budget>>", StringComparison.Ordinal), "reorder series carries declaration and attached comment"); Atomic(doc, prefix + chart, "move series");
            doc.Undo(); c = doc.OpenChartDiagram()!;
            Check(doc.InsertChartSeries(c, 1, "scatter", "Measured", [3, 4, 5], "#abc", labels: true), "insert series");
            Check(doc.Source.Contains("scatter \"Measured\" [3, 4, 5] #abc labels" + nl, StringComparison.Ordinal), "series declaration"); Atomic(doc, prefix + chart, "insert series");
            doc.Undo(); c = doc.OpenChartDiagram()!;
            Check(doc.RemoveChartSeries(c.Series[0]) && doc.Source.Contains("' stable separator" + nl, StringComparison.Ordinal), "remove series preserves detached comments"); Atomic(doc, prefix + chart, "remove series");
            doc.Undo(); c = doc.OpenChartDiagram()!;
            Check(!doc.InsertChartCategory(c, 0, "Invalid", [2]) && !doc.InsertChartCategory(c, 0, "Invalid", [200, 2]) && doc.Source == prefix + chart, "mismatched or out-of-domain columns rejected");
            Check(!doc.InsertChartPoint(c.Series[0], 0, new(0, 3)), "categorical points require column operation");
            var point = c.Series[0].Points[0];
            Check(doc.SetChartPoint(point, new(0, 8)) && doc.Source == prefix + chart.Replace("1.00", "8", StringComparison.Ordinal), "numeric update preserves other series"); Atomic(doc, prefix + chart, "point update");
        }
        foreach (string family in new[] { "line", "scatter" })
        {
            string source = "@startchart\nh-axis -10 --> 10 grid\nv-axis 0 --> 100 ticks [0:\"None\",100:\"Full\"]\nv2-axis 0 --> 10\n" + family + " \"Primary\" [(-2.00, 3), (0, 5), (8, 20)] #blue labels\n" + family + " \"Secondary\" [(0, 2), (5, 4)] v2\n@endchart";
            var doc = new StudioDocument(source); var chart = doc.OpenChartDiagram()!;
            Check(chart is { UsesCoordinates: true, HasCategoryLabels: false } && chart.Series[1].UsesSecondaryAxis, "coordinate grammar with unequal series lengths");
            Check(doc.InsertChartPoint(chart.Series[0], 1, new(-1, 4)) && doc.Source == source.Replace("(-2.00, 3), (0", "(-2.00, 3), (-1, 4), (0", StringComparison.Ordinal), "insert coordinate tuple"); Atomic(doc, source, "insert coordinate");
            doc.Undo(); chart = doc.OpenChartDiagram()!;
            Check(doc.MoveChartPoint(chart.Series[0].Points[2], 0) && doc.Source.Contains("[(8, 20), (-2.00, 3), (0, 5)]", StringComparison.Ordinal), "coordinate reorder retains tuple spelling"); Atomic(doc, source, "move coordinate");
            doc.Undo(); chart = doc.OpenChartDiagram()!;
            Check(doc.RemoveChartPoint(chart.Series[0].Points[1]) && doc.Source.Contains("[(-2.00, 3), (8, 20)]", StringComparison.Ordinal), "remove coordinate tuple"); Atomic(doc, source, "remove coordinate");
            doc.Undo(); chart = doc.OpenChartDiagram()!;
            Check(!doc.SetChartPoint(chart.Series[1].Points[0], new(11, 5)) && !doc.SetChartPoint(chart.Series[1].Points[0], new(0, 11)) && !doc.SetChartPoint(chart.Series[0].Points[0], new(double.NaN, 4)) && doc.Source == source, "coordinate and secondary bounds enforced atomically");
        }
        var annotated = new StudioDocument("@startchart\norientation horizontal\nv-axis [A,B]\nh-axis 0 --> 20\nbar [2,3]\nannotation \"Peak\" at (A,2) <<arrow>>\n@endchart");
        var a = annotated.OpenChartDiagram()!;
        Check(a.IsHorizontal && annotated.RenameChartCategory(a, 0, "First") && annotated.Source.Contains("at (First,2)", StringComparison.Ordinal), "horizontal category rename updates annotation anchor");
        a = annotated.OpenChartDiagram()!;
        Check(!annotated.RemoveChartCategory(a, 0), "anchored category removal cannot strand annotation");
        int offset = annotated.Source.IndexOf("First,B", StringComparison.Ordinal);
        var field = annotated.OpenSourceField(offset, 5, "chart-category")!;
        Check(annotated.ApplySourceField(field, "Changed") && annotated.Source.Contains("at (Changed,2)", StringComparison.Ordinal), "inline category editing uses structural invariant");
        var stacked = new StudioDocument("@startchart\nstackMode stacked\nh-axis [A,B]\nv-axis -10 --> 10\nbar [4,-4]\nbar [3,-3]\n@endchart");
        var stack = stacked.OpenChartDiagram()!;
        Check(stack.IsStacked && !stacked.SetChartPoint(stack.Series[0].Points[0], new(0, 8)), "stacked total checked");
        var number = stacked.OpenSourceField(stacked.Source.IndexOf("[4", StringComparison.Ordinal) + 1, 1, "number", "value")!;
        Check(!stacked.ApplySourceField(number, "8"), "inline numeric edit checks stack totals");
        var emptyPacket = new StudioDocument("@startpacketdiag\n{\n}\n@endpacketdiag");
        Check(emptyPacket.InsertPacketField(emptyPacket.OpenPacketDiagram()!, 0, 1, "Flag") && emptyPacket.RemovePacketField(emptyPacket.OpenPacketDiagram()!.Fields[0]), "empty packet can add and remove final field");
        var automatic = new StudioDocument("@startpacketdiag\n* Header [len=8]\n* Payload [len=8]\n@endpacketdiag");
        Check(automatic.SplitPacketField(automatic.OpenPacketDiagram()!.Fields[0], 7, "Flag") && automatic.OpenPacketDiagram()!.Fields.Select(f => f.BitCount).SequenceEqual([7, 1, 8]), "split single-bit tail retains len semantics");
        var indexed = new StudioDocument("@startchart\nline [1,2]\n@endchart");
        Check(indexed.InsertChartPoint(indexed.OpenChartDiagram()!.Series[0], 2, new(0, 3)) && indexed.OpenChartDiagram()!.Series[0].Points.Count == 3, "automatic axes indexed authoring");
        var emptyChart = new StudioDocument("@startchart\n@endchart");
        Check(emptyChart.InsertChartSeries(emptyChart.OpenChartDiagram()!, 0, "bar", "First", [1, 2]), "chart authoring from empty native page");
        foreach (string label in new[] { "Bad ' comment", "Bad /' hidden '/", "Bad [len=99]", "Bad\n1: injected" })
        {
            string before = emptyPacket.Source;
            Check(!emptyPacket.InsertPacketField(emptyPacket.OpenPacketDiagram()!, 0, 1, label) && emptyPacket.Source == before, "packet label cannot inject grammar");
        }
        const string commentedList = "@startchart\nh-axis [A /' axis comment '/, B]\nv-axis 0 --> 10\nbar [1 /' data comment '/, 2]\n@endchart";
        var comments = new StudioDocument(commentedList);
        Check(comments.InsertChartCategory(comments.OpenChartDiagram()!, 1, "C", [3]) && comments.Source.Split("axis comment").Length == 2 && comments.Source.Split("data comment").Length == 2, "inserting a column does not duplicate separator comments");
        comments.Undo();
        Check(comments.RemoveChartCategory(comments.OpenChartDiagram()!, 0) && comments.Source.Contains("/' axis comment '/", StringComparison.Ordinal) && comments.Source.Contains("/' data comment '/", StringComparison.Ordinal), "removing a column retains separator comments");
        Atomic(comments, commentedList, "comment-preserving category removal");
        const string gaps = "@startchart\r\nh-axis [A, B, C]\r\nv-axis 0 --> 10\r\nline \"Gaps\" [1.00, , 8] #blue labels connect ' kept\r\n@endchart";
        var gapDocument = new StudioDocument(gaps); var gapChart = gapDocument.OpenChartDiagram()!;
        Check(gapChart.Series[0].Points[1].IsMissing && gapChart.Series[0].ConnectMissingValues, "line missing slots and connection flag");
        Check(gapDocument.SetChartLineOptions(gapChart.Series[0], true, true) && gapDocument.Source == gaps.Replace("labels connect", "labels interpolate connect", StringComparison.Ordinal), "line option insertion preserves flag order and comments");
        Atomic(gapDocument, gaps, "line options"); gapDocument.Undo(); gapChart = gapDocument.OpenChartDiagram()!;
        Check(gapDocument.SetChartPoint(gapChart.Series[0].Points[1], new(1, 4)) && gapDocument.Source == gaps.Replace("1.00, , 8", "1.00, 4, 8", StringComparison.Ordinal), "fill missing point without rewriting adjacent literals");
        Atomic(gapDocument, gaps, "fill missing"); gapDocument.Undo(); gapChart = gapDocument.OpenChartDiagram()!;
        Check(gapDocument.MoveChartCategory(gapChart, 1, 2) && gapDocument.OpenChartDiagram()!.Series[0].Points[2].IsMissing, "category reordering carries missing values including final slot");
        Atomic(gapDocument, gaps, "move missing category"); gapDocument.Undo(); gapChart = gapDocument.OpenChartDiagram()!;
        Check(gapDocument.SetChartPoint(gapChart.Series[0].Points[2], new(2, 0, true)) && gapDocument.OpenChartDiagram()!.Series[0].Points[2].IsMissing, "clear final line value while retaining its category slot");
        Atomic(gapDocument, gaps, "clear point");
        var insertedGaps = new StudioDocument("@startchart\n@endchart");
        Check(insertedGaps.InsertChartIndexedSeries(insertedGaps.OpenChartDiagram()!, 0, "line", "Sparse", [1, null, 5], interpolate: true, connect: true), "insert sparse interpolated line series");
        Check(insertedGaps.OpenChartDiagram()!.Series[0].Interpolate && insertedGaps.OpenChartDiagram()!.Series[0].Points[1].IsMissing, "inserted line retains options and missing slot");
        foreach (string nl in new[] { "\n", "\r\n", "\r" })
        {
            string configuredSource = ("@startchart\ntitle Daily\nlegend bottom\nh-axis [A, B]\nv-axis \"Count\" 0.00 /' bound '/ --> 20.0 spacing 5 grid ticks [0:\"Zero\",20:\"Twenty\"] label-top ' keep\nbar \"Counts\" [2, 4]\nannotation \"Peak\" at (B,4) <<arrow>>\n' separate comment\n@endchart").ReplaceLineEndings(nl);
            var config = new StudioDocument(configuredSource); var snapshot = config.OpenChartDiagram()!;
            Check(!config.SetChartAxis(snapshot, ChartAxisKind.Vertical, snapshot.VerticalAxis.Definition) && config.Source == configuredSource, "unchanged axis is a no-op");
            Check(config.SetChartAxis(snapshot, ChartAxisKind.Vertical, snapshot.VerticalAxis.Definition with { Label = "Total", Maximum = 30, Spacing = 10, Grid = false }), "edit numeric axis options");
            Check(config.Source == configuredSource.Replace("\"Count\"", "\"Total\"", StringComparison.Ordinal).Replace("20.0 spacing 5 grid", "30 spacing 10 ", StringComparison.Ordinal), "axis edit retains numeric spelling, comments, custom ticks and option positions");
            Atomic(config, configuredSource, "axis update"); config.Undo(); snapshot = config.OpenChartDiagram()!;
            Check(config.SetChartAxis(snapshot, ChartAxisKind.Horizontal, snapshot.HorizontalAxis.Definition with { Categories = new[] { "First", "Second" } }) && config.Source.Contains("at (Second,4)", StringComparison.Ordinal), "axis category replacement updates anchors");
            Atomic(config, configuredSource, "axis category names"); config.Undo(); snapshot = config.OpenChartDiagram()!;
            Check(!config.SetChartAxis(snapshot, ChartAxisKind.Vertical, snapshot.VerticalAxis.Definition with { Maximum = 3 }) && config.Source == configuredSource, "axis bounds reject excluded series and annotations atomically");
            Check(!config.SetChartAxis(snapshot, ChartAxisKind.Horizontal, snapshot.HorizontalAxis.Definition with { Categories = new[] { "Only" } }), "axis change cannot desynchronize series cardinality");
            Check(config.ApplyChartSettings(snapshot, new("Report", ChartLegend.Right, ChartOrientation.Horizontal, ChartStackMode.Stacked)), "chart settings atomic update");
            string changed = config.Source;
            Check(changed.Contains("orientation horizontal" + nl + "stackMode stacked" + nl, StringComparison.Ordinal) && changed.Contains("v-axis [A, B]", StringComparison.Ordinal) && changed.Contains("h-axis \"Count\" 0.00 /' bound '/ --> 20.0", StringComparison.Ordinal) && changed.Contains("label-right ' keep", StringComparison.Ordinal), "orientation swaps axis roles and label placement without data rewrites");
            Atomic(config, configuredSource, "chart settings");
            var horizontal = config.OpenChartDiagram()!;
            Check(config.RemoveChartSetting(horizontal, ChartSetting.Orientation) && !config.OpenChartDiagram()!.IsHorizontal && !config.Source.Contains("orientation ", StringComparison.Ordinal), "remove orientation restores default and swaps axes atomically");
            Atomic(config, changed, "remove orientation setting");
            config = new StudioDocument(configuredSource); snapshot = config.OpenChartDiagram()!;
            Check(config.InsertChartAnnotation(snapshot, 0, "Goal", "A", 8, true) && config.Source.Contains("annotation \"Goal\" at (A,8) <<arrow>>" + nl + "annotation \"Peak\"", StringComparison.Ordinal), "insert annotation at final index");
            Atomic(config, configuredSource, "annotation insert"); config.Undo(); snapshot = config.OpenChartDiagram()!;
            Check(config.UpdateChartAnnotation(snapshot.Annotations[0], "Target", "A", 9, false), "update annotation position, label and arrow");
            Check(config.Source == configuredSource.Replace("annotation \"Peak\" at (B,4) <<arrow>>", "annotation \"Target\" at (A,9) ", StringComparison.Ordinal), "annotation editing keeps unrelated source exact");
            Atomic(config, configuredSource, "annotation update"); config.Undo(); snapshot = config.OpenChartDiagram()!;
            Check(!config.UpdateChartAnnotation(snapshot.Annotations[0], "Outside", "B", 21, true) && !config.InsertChartAnnotation(snapshot, 1, "Missing", "Z", 5), "invalid annotation anchors do not mutate");
            Check(!config.RemoveChartAxis(snapshot, ChartAxisKind.Horizontal), "removing an axis cannot strand category annotations");
            Check(config.RemoveChartAnnotation(snapshot.Annotations[0]) && config.Source.Contains("' separate comment" + nl, StringComparison.Ordinal), "remove annotation preserves independent comments");
            Atomic(config, configuredSource, "annotation remove");
        }
        var axes = new StudioDocument("@startchart\nh-axis [A,B]\nv-axis 0 --> 10\nline [2,4]\n@endchart");
        var axisSnapshot = axes.OpenChartDiagram()!; string withoutSecondary = axes.Source;
        Check(axes.SetChartAxis(axisSnapshot, ChartAxisKind.Secondary, new() { Label = "Rate", Minimum = 0, Maximum = 10, Spacing = 2, Grid = true, Ticks = new[] { new ChartTick(0, "None"), new ChartTick(10, "Full") } }), "create secondary axis");
        Check(axes.Source.Contains("v2-axis \"Rate\" 0 --> 10 ticks [0:\"None\", 10:\"Full\"] spacing 2 grid\n", StringComparison.Ordinal), "created axis uses upstream option order");
        Atomic(axes, withoutSecondary, "secondary axis creation"); axisSnapshot = axes.OpenChartDiagram()!;
        Check(axes.InsertChartSeries(axisSnapshot, 1, "line", "Rate", [3, 5], secondary: true), "series attaches to created secondary axis");
        axisSnapshot = axes.OpenChartDiagram()!; string secondarySource = axes.Source;
        Check(!axes.RemoveChartAxis(axisSnapshot, ChartAxisKind.Secondary), "referenced secondary axis requires reassignment");
        Check(axes.RemoveChartAxis(axisSnapshot, ChartAxisKind.Secondary, true) && axes.OpenChartDiagram()!.SecondaryAxis == null && !axes.OpenChartDiagram()!.Series[1].UsesSecondaryAxis, "remove secondary axis reassigns its series in the same transaction");
        Atomic(axes, secondarySource, "secondary removal and reassignment");
        var numericAnnotation = new StudioDocument("@startchart\nh-axis -10 --> 10\nv-axis 0 --> 10\nline [(0,2),(2,4)] interpolate\n@endchart");
        Check(numericAnnotation.InsertChartAnnotation(numericAnnotation.OpenChartDiagram()!, 0, "Measured", "1.5", 3, true), "numeric annotation creation");
        Check(!numericAnnotation.InsertChartAnnotation(numericAnnotation.OpenChartDiagram()!, 1, "Outside", "11", 3), "numeric annotation x bounds");
        const string punctuation = "@startchart\nh-axis [Input (ms), Unmatched )]\nv-axis 0 --> 10\nline [2,4]\n@endchart";
        var punctuated = new StudioDocument(punctuation); var punctuatedChart = punctuated.OpenChartDiagram()!;
        Check(punctuated.InsertChartAnnotation(punctuatedChart, 0, "Measured", "Unmatched )", 4), "annotation categories may contain parentheses");
        Atomic(punctuated, punctuation, "punctuated category annotation");
        string punctuatedSource = punctuated.Source; punctuatedChart = punctuated.OpenChartDiagram()!;
        Check(punctuated.SetChartAxis(punctuatedChart, ChartAxisKind.Horizontal, punctuatedChart.HorizontalAxis.Definition with { Categories = new[] { "Before (ms)", "After (" } }), "category tokenization treats parentheses as label text");
        Check(punctuated.Source == punctuatedSource.Replace("Input (ms)", "Before (ms)", StringComparison.Ordinal).Replace("Unmatched )", "After (", StringComparison.Ordinal), "punctuated category and annotation rename remains exact");
        Atomic(punctuated, punctuatedSource, "punctuated category rename");
        foreach (bool hasGrid in new[] { false, true })
        {
            string initial = "@startchart\nv-axis 0 --> 10" + (hasGrid ? " grid" : "") + " ' retained\nline [2,4]\n@endchart";
            var ordered = new StudioDocument(initial); var orderedChart = ordered.OpenChartDiagram()!;
            Check(ordered.SetChartAxis(orderedChart, ChartAxisKind.Vertical, orderedChart.VerticalAxis.Definition with { Ticks = new[] { new ChartTick(0, "Zero"), new ChartTick(10, "Ten") }, Spacing = 2, Grid = true, LabelPosition = ChartAxisLabelPosition.Top }), "insert multiple axis options");
            Check(ordered.Source == "@startchart\nv-axis 0 --> 10 ticks [0:\"Zero\", 10:\"Ten\"] spacing 2 label-top grid ' retained\nline [2,4]\n@endchart", "inserted axis options preserve comments and upstream order");
            Atomic(ordered, initial, "ordered axis options");
        }
        var pristine = new StudioDocument("@startchart\nline [1,2]\n@endchart"); var pristineChart = pristine.OpenChartDiagram()!;
        Check(pristine.SetChartAxis(pristineChart, ChartAxisKind.Vertical, new() { Minimum = 0, Maximum = 4 }), "materialize inferred numeric axis");
        var beforeRemoval = pristine.Source;
        Check(pristine.RemoveChartAxis(pristine.OpenChartDiagram()!, ChartAxisKind.Vertical) && pristine.Source == "@startchart\nline [1,2]\n@endchart", "remove explicit axis restores inference");
        Atomic(pristine, beforeRemoval, "remove inferred override");
        return count;
    }
}
