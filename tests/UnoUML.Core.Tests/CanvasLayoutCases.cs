using UnoUML.Core;

internal static class CanvasLayoutCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("Canvas layout: " + message); checks++; }
        void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); } catch (T) { Check(true, message); return; }
            throw new Exception("Canvas layout: expected " + typeof(T).Name + " — " + message);
        }
        static CanvasNode Node(CanvasLayoutState state, string id) => state.Nodes.Single(node => node.Id == id);
        static CanvasEdge Edge(CanvasLayoutState state, string id) => state.Edges.Single(edge => edge.Id == id);
        static CanvasLayoutState Move(CanvasLayoutState state, double x, double y, params string[] ids) =>
            state with { Nodes = state.Nodes.Select(node => ids.Contains(node.Id) ? node with { X = node.X + x, Y = node.Y + y } : node).ToArray() };
        static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;
        static (double X, double Y, double W, double H) Box(CanvasNode node)
        {
            double angle = node.Rotation * Math.PI / 180;
            double width = Math.Abs(Math.Cos(angle)) * node.Width + Math.Abs(Math.Sin(angle)) * node.Height;
            double height = Math.Abs(Math.Sin(angle)) * node.Width + Math.Abs(Math.Cos(angle)) * node.Height;
            return (node.X + (node.Width - width) / 2, node.Y + (node.Height - height) / 2, width, height);
        }
        void ClearNodes(CanvasLayoutState state, string message)
        {
            for (int i = 0; i < state.Nodes.Count; i++)
            for (int j = i + 1; j < state.Nodes.Count; j++)
            {
                var a = Box(state.Nodes[i]); var b = Box(state.Nodes[j]);
                Check(a.X + a.W <= b.X + 0.001 || b.X + b.W <= a.X + 0.001 || a.Y + a.H <= b.Y + 0.001 || b.Y + b.H <= a.Y + 0.001, message + " does not overlap");
            }
        }
        void ClearRoute(CanvasEdge edge, IEnumerable<CanvasNode> obstacles, double padding, string message)
        {
            var path = edge.Waypoints!;
            Check(path.Count >= 2 && path.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)), message + " has finite geometry");
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i - 1]; var b = path[i];
                Check(Near(a.X, b.X) || Near(a.Y, b.Y), message + " is orthogonal");
                foreach (var node in obstacles)
                {
                    var rect = Box(node);
                    double left = rect.X - padding, right = rect.X + rect.W + padding, top = rect.Y - padding, bottom = rect.Y + rect.H + padding;
                    bool intersection = Near(a.Y, b.Y)
                        ? a.Y > top + 0.001 && a.Y < bottom - 0.001 && Math.Max(a.X, b.X) > left + 0.001 && Math.Min(a.X, b.X) < right - 0.001
                        : a.X > left + 0.001 && a.X < right - 0.001 && Math.Max(a.Y, b.Y) > top + 0.001 && Math.Min(a.Y, b.Y) < bottom - 0.001;
                    Check(!intersection, message + " clears obstacle " + node.Id);
                }
            }
        }

        var preferences = new CanvasLayoutSettings
        {
            Algorithm = CanvasLayoutAlgorithm.ForceDirected, Direction = CanvasLayoutDirection.Left, NodeSpacing = 23.5, LayerSpacing = 75,
            RoutingPadding = 9, GridColumns = 3, Iterations = 17, Seed = -14, GridSize = 8, SnapToGrid = false, SnapToObjects = false, Automatic = true
        };
        var metadata = new CanvasLayoutState([
            new("容器", -30.5, 10, 400, 250, Rotation: 10, ZIndex: 3, Collapsed: true),
            new("A", 20, 40, 60, 45, "容器", Locked: true, Pinned: true), new("B", 200, 100, 50, 40, "容器")
        ], [new("edge", "A", "B", [new(80, 60), new(200, 60), new(200, 120)], "curved", true, true, new(1, 0.5), new(0, 0.5), new(3, -12))]) { Settings = preferences };
        string json = CanvasLayoutJson.Serialize(metadata);
        Check(json.StartsWith("{\"version\":1,") && json.Contains("\"forceDirected\"") && json.Contains("\"snapToGrid\":false"), "JSON has explicit version and camel-case schema");
        Check(CanvasLayoutJson.TryParse(json, out var parsed, out var error) && error == null, "JSON parses without reflection");
        Check(CanvasLayoutJson.Serialize(parsed!) == json && parsed!.Settings == preferences, "all properties survive canonical round-trip");
        Check(Node(parsed!, "A").Parent == "容器" && Edge(parsed!, "edge").SourcePort == new CanvasPoint(1, 0.5), "parent and normalized ports survive");
        Check(!CanvasLayoutJson.TryParse(json.Replace("\"version\":1", "\"version\":2"), out _, out _), "future version is rejected");
        Check(!CanvasLayoutJson.TryParse(json.Replace("\"version\":1", "\"version\":1,\"version\":1"), out _, out _), "duplicate JSON property is rejected");
        Check(!CanvasLayoutJson.TryParse(json.Replace("\"version\":1", "\"version\":1,\"future\":true"), out _, out _), "unknown data is not silently discarded");
        Check(!CanvasLayoutJson.TryParse("{\"version\":1,\"nodes\":[{\"id\":\"A\",\"x\":1e999,\"y\":0,\"width\":20,\"height\":20}]}", out _, out _), "nonfinite geometry is rejected");
        Check(!CanvasLayoutJson.TryParse("{\"version\":1,\"nodes\":[{\"id\":\"A\",\"x\":0,\"y\":0,\"width\":-1,\"height\":20}]}", out _, out _), "negative node size is rejected");
        Check(!CanvasLayoutJson.TryParse("{\"version\":1,\"nodes\":null}", out _, out _), "null collection is rejected");
        Check(!CanvasLayoutJson.TryParse(new string(' ', CanvasLayoutLimits.MaxMetadataCharacters + 1), out _, out _), "metadata size is bounded");
        Throws<ArgumentException>(() => CanvasLayoutJson.Serialize(new([new("A", 0, 0, 20, 20), new("A", 30, 0, 20, 20)], [])), "duplicate node identity");
        Throws<ArgumentException>(() => CanvasLayoutJson.Serialize(new([new("A", 0, 0, 20, 20, "B"), new("B", 30, 0, 20, 20, "A")], [])), "cyclic parent identity");
        Throws<ArgumentException>(() => CanvasLayoutJson.Serialize(new([new("A", 0, 0, 20, 20)], [new("e", "A", "missing")])), "missing edge endpoint");

        var simple = new CanvasLayoutState([new("A", 10, 20, 80, 40), new("B", 160, 20, 60, 40)], [new("AB", "A", "B")]);
        const string source = "\uFEFF@startuml first\r\nclass A\r\nclass B\r\nA --> B\r\n@enduml\r\n\r\n@startjson second\n{\"keep\": 1}\n@endjson\n";
        var doc = new StudioDocument(source);
        Check(!doc.TryGetCanvasLayout(out _, out error) && error == null, "missing metadata is distinguishable from invalid metadata");
        Check(doc.SetCanvasLayout(simple), "insert canvas comment");
        string withLayout = doc.Source;
        Check(withLayout.StartsWith("\uFEFF" + StudioDocument.CanvasLayoutMarker) && withLayout.Contains("\r\n@startuml first\r\n"), "insertion preserves BOM and local CRLF");
        Check(doc.Page.Source.Contains("class A\r\n") && !doc.Page.Source.Contains("@unouml:canvas"), "metadata stays outside page grammar");
        Check(doc.TryGetCanvasLayout(out var saved, out error) && CanvasLayoutJson.Serialize(saved!) == CanvasLayoutJson.Serialize(simple), "source metadata restores complete snapshot");
        long state = doc.HistoryState;
        Check(!doc.SetCanvasLayout(simple) && doc.HistoryState == state, "identical layout has no history entry");
        Check(doc.Undo() && doc.Source == source && !doc.CanUndo, "canvas insertion is one exact undo");
        Check(doc.Redo() && doc.Source == withLayout, "canvas insertion redoes exactly");
        Check(doc.SetCanvasLayout(null) && doc.Source == source, "removal restores original BOM and bytes");
        Check(doc.Undo() && doc.Source == withLayout, "removal is undoable");
        doc.ActivePage = 1;
        Check(doc.SetCanvasLayout(simple with { Settings = preferences }), "second page has independent metadata");
        Check(doc.Source.Contains("\n@startjson second\n") && !doc.Source.Contains("\r\n@startjson second"), "second page retains its LF convention");
        string second = doc.Source;
        doc.SetSource("@startuml inserted\nclass New\n@enduml\n" + second.TrimStart('\uFEFF'));
        doc.ActivePage = 2;
        Check(doc.TryGetCanvasLayout(out saved, out error) && saved!.Settings == preferences, "inserting an earlier page does not reassign metadata");
        doc.ActivePage = 1;
        Check(doc.TryGetCanvasLayout(out saved, out error) && saved!.Settings != preferences, "duplicate adjacency is independent of ordinal page numbers");
        var corrupt = new StudioDocument("' @unouml:canvas {broken}\r\n@startuml\r\nclass A\r\n@enduml");
        string corruptSource = corrupt.Source;
        Check(!corrupt.TryGetCanvasLayout(out _, out error) && error != null && !corrupt.SetCanvasLayout(simple) && !corrupt.SetCanvasLayout(null) && corrupt.Source == corruptSource && !corrupt.CanUndo, "malformed metadata is never overwritten or deleted silently");
        var duplicate = new StudioDocument(StudioDocument.CanvasLayoutMarker + json + "\n" + StudioDocument.CanvasLayoutMarker + json + "\n@startuml\n@enduml");
        Check(!duplicate.TryGetCanvasLayout(out _, out error) && error != null && !duplicate.SetCanvasLayout(simple), "ambiguous adjacent comments are rejected");
        var unattached = new StudioDocument(StudioDocument.CanvasLayoutMarker + json + "\n\n@startuml\n@enduml");
        Check(!unattached.TryGetCanvasLayout(out _, out error) && error == null, "metadata requires physical adjacency");
        var implicitPage = new StudioDocument("class A");
        Check(!implicitPage.SetCanvasLayout(simple) && implicitPage.Source == "class A", "implicit page is not wrapped or rewritten");

        var transaction = new StudioDocument(source);
        Check(transaction.Insert("class Added"), "source insertion precedes automatic placement");
        long insertionState = transaction.HistoryState;
        Check(transaction.TrySetCanvasLayout(simple, insertionState, out error) && error == null, "automatic placement merges with the expected source transaction");
        string combinedSource = transaction.Source;
        Check(transaction.HistoryState != insertionState && !transaction.CanReachHistoryState(insertionState) && transaction.CanReachHistoryState(0), "amendment assigns a new state and removes only the amended state");
        Check(transaction.Undo() && transaction.Source == source && !transaction.CanUndo, "source insertion and placement undo atomically with BOM and CRLF");
        Check(transaction.Redo() && transaction.Source == combinedSource, "combined transaction redoes exactly");
        var staleTransaction = new StudioDocument(source);
        Check(!staleTransaction.TrySetCanvasLayout(simple, staleTransaction.HistoryState, out error) && error != null && !staleTransaction.CanUndo, "amendment requires a preceding history item");
        staleTransaction.Insert("class First"); long expectedState = staleTransaction.HistoryState;
        staleTransaction.Insert("class Second"); string interveningSource = staleTransaction.Source; long currentState = staleTransaction.HistoryState;
        Check(!staleTransaction.TrySetCanvasLayout(simple, expectedState, out error) && error != null && staleTransaction.Source == interveningSource && staleTransaction.HistoryState == currentState, "stale expected state cannot amend an intervening edit");
        Check(staleTransaction.TrySetCanvasLayout(simple, currentState, out error) && staleTransaction.Undo() && staleTransaction.Source.Contains("class First") && !staleTransaction.Source.Contains("class Second") && staleTransaction.CanUndo, "amendment retains earlier source history");
        Check(staleTransaction.Undo() && staleTransaction.Source == source, "earlier source edit remains byte-exact after amendment");
        var branching = new StudioDocument(source);
        branching.Insert("class First"); long branchState = branching.HistoryState;
        branching.Insert("class Abandoned"); branching.Undo();
        Check(branching.CanRedo && branching.TrySetCanvasLayout(simple, branchState, out error) && !branching.CanRedo, "amendment invalidates the old redo branch");
        Check(branching.Undo() && branching.Source == source, "branched amendment still has one undo");
        var inverse = new StudioDocument(source);
        inverse.SetCanvasLayout(simple);
        Check(inverse.TrySetCanvasLayout(null, inverse.HistoryState, out error) && inverse.Source == source && !inverse.CanUndo && inverse.HistoryState == 0, "inverse amendment removes an empty transaction");
        var invalidTransaction = new StudioDocument(source);
        invalidTransaction.Insert("class Added"); long validState = invalidTransaction.HistoryState; string validSource = invalidTransaction.Source;
        Check(!invalidTransaction.TrySetCanvasLayout(simple with { Nodes = [Node(simple, "A"), Node(simple, "A")] }, validState, out error) && error != null &&
            invalidTransaction.Source == validSource && invalidTransaction.HistoryState == validState, "invalid layout leaves source and history unchanged before amendment");

        var graph = new CanvasLayoutState([
            new("A", 20, 30, 70, 35, Rotation: 15, ZIndex: 4), new("B", 20, 30, 110, 40),
            new("C", 20, 30, 45, 70), new("D", 20, 30, 75, 45), new("E", 20, 30, 60, 32)
        ], [new("ab", "A", "B", Routing: "straight"), new("bc", "B", "C", Routing: "straight"), new("ca", "C", "A", Routing: "straight"), new("bd", "B", "D", Routing: "straight")]);
        string original = CanvasLayoutJson.Serialize(graph);
        foreach (var algorithm in Enum.GetValues<CanvasLayoutAlgorithm>())
        {
            var options = new CanvasLayoutSettings { Algorithm = algorithm, SnapToGrid = false, Iterations = 60 };
            var arranged = CanvasLayoutEngine.Layout(graph, options);
            Check(CanvasLayoutJson.Serialize(CanvasLayoutEngine.Layout(graph, options)) == CanvasLayoutJson.Serialize(arranged), algorithm + " is deterministic");
            var permutation = graph with { Nodes = graph.Nodes.Reverse().ToArray(), Edges = graph.Edges.Reverse().ToArray() };
            Check(CanvasLayoutJson.Serialize(CanvasLayoutEngine.Layout(permutation, options)) == CanvasLayoutJson.Serialize(arranged), algorithm + " is independent of input ordering");
            Check(arranged.Nodes.All(node => double.IsFinite(node.X) && double.IsFinite(node.Y)) && Node(arranged, "A").Rotation == 15 && Node(arranged, "A").ZIndex == 4, algorithm + " preserves non-layout properties");
            ClearNodes(arranged, algorithm.ToString());
        }
        Check(CanvasLayoutJson.Serialize(graph) == original, "layout does not mutate input arrays");
        var dag = new CanvasLayoutState([new("A", 0, 0, 40, 30), new("B", 0, 0, 40, 30), new("C", 0, 0, 40, 30), new("D", 0, 0, 40, 30)],
            [new("ad", "A", "D", Routing: "straight"), new("bc", "B", "C", Routing: "straight")]);
        var uncrossed = CanvasLayoutEngine.Layout(dag, new() { SnapToGrid = false });
        Check((Node(uncrossed, "A").X - Node(uncrossed, "B").X) * (Node(uncrossed, "D").X - Node(uncrossed, "C").X) > 0, "barycenter sweeps remove the two-layer crossing");
        foreach (var direction in Enum.GetValues<CanvasLayoutDirection>())
        {
            var directed = CanvasLayoutEngine.Layout(simple, new() { Direction = direction, SnapToGrid = false });
            var a = Node(directed, "A"); var b = Node(directed, "B");
            Check(direction switch { CanvasLayoutDirection.Down => b.Y > a.Y, CanvasLayoutDirection.Up => b.Y < a.Y, CanvasLayoutDirection.Right => b.X > a.X, _ => b.X < a.X }, direction + " preserves DAG flow");
        }
        var tight = CanvasLayoutEngine.Layout(simple, new() { LayerSpacing = 0, NodeSpacing = 40, SnapToGrid = false });
        Check(Node(tight, "B").Y >= Node(tight, "A").Y + Node(tight, "A").Height, "zero layer spacing does not reverse DAG flow during packing");
        var fixedGraph = new CanvasLayoutState([new("A", 0, 0, 40, 40, Locked: true), new("B", 0, 0, 40, 40), new("C", 200, 20, 40, 40, Pinned: true), new("D", 300, 20, 40, 40)], []);
        var scoped = CanvasLayoutEngine.Layout(fixedGraph, new() { Algorithm = CanvasLayoutAlgorithm.Grid }, ["A", "B", "C"]);
        Check(Node(scoped, "A") == Node(fixedGraph, "A") && Node(scoped, "C") == Node(fixedGraph, "C") && Node(scoped, "D") == Node(fixedGraph, "D"), "locked, pinned and unselected coordinates are exact anchors");
        ClearNodes(scoped, "scoped layout");
        Check(CanvasLayoutJson.Serialize(CanvasLayoutEngine.Layout(fixedGraph, selection: [])) == CanvasLayoutJson.Serialize(fixedGraph), "empty selection means no nodes");
        Throws<ArgumentException>(() => CanvasLayoutEngine.Layout(simple, selection: ["absent"]), "unknown selected node is rejected");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Throws<OperationCanceledException>(() => CanvasLayoutEngine.Layout(simple, cancellationToken: cancelled.Token), "layout cancellation");
            Throws<OperationCanceledException>(() => CanvasLayoutEngine.RouteEdges(simple, cancellationToken: cancelled.Token), "router cancellation");
        }
        var tooMany = new CanvasLayoutState(Enumerable.Range(0, CanvasLayoutLimits.MaxNodes + 1).Select(i => new CanvasNode("n" + i, 0, 0, 20, 20)).ToArray(), []);
        Throws<ArgumentException>(() => CanvasLayoutEngine.Layout(tooMany), "graph resource bounds");
        var heavy = new CanvasLayoutState(Enumerable.Range(0, 512).Select(i => new CanvasNode("n" + i, 0, 0, 20, 20)).ToArray(), []);
        Throws<InvalidOperationException>(() => CanvasLayoutEngine.Layout(heavy, new() { Algorithm = CanvasLayoutAlgorithm.ForceDirected, Iterations = 1000 }), "force interaction budget");

        var compound = new CanvasLayoutState([new("Group", 100, 100, 180, 120), new("Child", 120, 130, 60, 40, "Group"), new("Remote", 100, 100, 80, 50)], []);
        var grouped = CanvasLayoutEngine.Layout(compound, new() { Algorithm = CanvasLayoutAlgorithm.Grid, SnapToGrid = false });
        Check(Near(Node(grouped, "Child").X - Node(grouped, "Group").X, 20) && Near(Node(grouped, "Child").Y - Node(grouped, "Group").Y, 30), "compound subtree moves rigidly");
        Check(CanvasLayoutJson.Serialize(CanvasLayoutEngine.Layout(compound, selection: ["Group"])) == CanvasLayoutJson.Serialize(compound), "group selection never implicitly moves unselected children");
        var collapsed = compound with { Nodes = compound.Nodes.Select(node => node.Id == "Group" ? node with { Collapsed = true } : node).ToArray() };
        var collapsedLayout = CanvasLayoutEngine.Layout(collapsed, new() { Algorithm = CanvasLayoutAlgorithm.Grid, SnapToGrid = false });
        Check(Near(Node(collapsedLayout, "Child").X - Node(collapsedLayout, "Group").X, 20) && Node(collapsedLayout, "Remote").X > Node(collapsedLayout, "Group").X, "collapsed movable subtree remains an arrangement unit");
        var lockedChild = compound with { Nodes = compound.Nodes.Select(node => node.Id == "Child" ? node with { Locked = true } : node).ToArray() };
        Throws<ArgumentException>(() => CanvasLayoutEngine.UpdateGeometry(lockedChild, Move(lockedChild, 10, 0, "Group")), "ancestor movement cannot bypass a locked descendant");
        var lockedGroup = compound with { Nodes = compound.Nodes.Select(node => node.Id == "Group" ? node with { Locked = true } : node).ToArray() };
        Throws<ArgumentException>(() => CanvasLayoutEngine.UpdateGeometry(lockedGroup, Move(lockedGroup, 10, 0, "Child")), "descendant movement cannot bypass an ancestor lock");
        var hiddenGraph = new CanvasLayoutState([new("Group", 0, 0, 200, 100), new("A", 20, 20, 40, 40, "Group"), new("B", 400, 20, 40, 40)],
            [new("AB", "A", "B", SourcePort: new(1, 0.5), TargetPort: new(0, 0.5))]);
        var visibleGraph = CanvasLayoutEngine.RouteEdges(hiddenGraph);
        var hidden = CanvasLayoutEngine.UpdateGeometry(visibleGraph, visibleGraph with { Nodes = visibleGraph.Nodes.Select(node => node.Id == "Group" ? node with { Collapsed = true } : node).ToArray() });
        var hiddenMoved = CanvasLayoutEngine.UpdateGeometry(hidden, Move(hidden, 100, 0, "Group", "A"));
        var expanded = CanvasLayoutEngine.UpdateGeometry(hiddenMoved, hiddenMoved with { Nodes = hiddenMoved.Nodes.Select(node => node.Id == "Group" ? node with { Collapsed = false } : node).ToArray() });
        Check(Edge(expanded, "AB").Waypoints![0] == new CanvasPoint(160, 40), "expanding regenerates automatic routes skipped while endpoints were hidden");

        var alignment = new CanvasLayoutState([new("A", 20, 20, 40, 30), new("B", 100, 100, 60, 40), new("Reference", 0, 200, 90, 50, Pinned: true)], []);
        var aligned = CanvasLayoutEngine.Align(alignment, CanvasAlignment.Left, ["A", "B", "Reference"]);
        Check(Node(aligned, "A").X == 0 && Node(aligned, "B").X == 0 && Node(aligned, "Reference") == Node(alignment, "Reference"), "alignment uses fixed anchors without moving them");
        var matched = CanvasLayoutEngine.MatchSize(alignment, CanvasSize.Both, ["A", "B"], "Reference");
        Check(Node(matched, "A").Width == 90 && Node(matched, "B").Height == 50 && Node(matched, "Reference") == Node(alignment, "Reference"), "match size has an explicit reference");
        var distribution = new CanvasLayoutState([new("A", 0, 0, 20, 20), new("B", 50, 0, 30, 20), new("C", 130, 0, 40, 20), new("D", 250, 0, 50, 20)], []);
        var distributed = CanvasLayoutEngine.Distribute(distribution, CanvasAxis.Horizontal, ["A", "B", "C", "D"]);
        double gap1 = Node(distributed, "B").X - Node(distributed, "A").X - Node(distributed, "A").Width;
        double gap2 = Node(distributed, "C").X - Node(distributed, "B").X - Node(distributed, "B").Width;
        double gap3 = Node(distributed, "D").X - Node(distributed, "C").X - Node(distributed, "C").Width;
        Check(Near(gap1, gap2) && Near(gap2, gap3) && Node(distributed, "A") == Node(distribution, "A") && Node(distributed, "D") == Node(distribution, "D"), "distribution equalizes gaps with fixed extremes");
        var anchored = distribution with { Nodes = distribution.Nodes.Select(node => node.Id == "C" ? node with { Pinned = true } : node).ToArray() };
        distributed = CanvasLayoutEngine.Distribute(anchored, CanvasAxis.Horizontal, ["A", "B", "C", "D"]);
        Check(Node(distributed, "C") == Node(anchored, "C") && Near(Node(distributed, "B").X, 60), "interior pinned node partitions distribution intervals");

        var routedGraph = new CanvasLayoutState([new("A", 0, 0, 40, 40), new("B", 300, 0, 40, 40), new("Obstacle", 130, -30, 80, 100)],
            [new("AB", "A", "B", SourcePort: new(1, 0.5), TargetPort: new(0, 0.5))]);
        var routed = CanvasLayoutEngine.RouteEdges(routedGraph);
        var route = Edge(routed, "AB");
        Check(route.Waypoints![0] == new CanvasPoint(40, 20) && route.Waypoints[^1] == new CanvasPoint(300, 20), "explicit ports dock exactly");
        Check(route.Waypoints.Count >= 4, "obstacle requires routed bends");
        ClearRoute(route, [Node(routed, "Obstacle")], 12, "visibility route");
        Check(CanvasLayoutJson.Serialize(CanvasLayoutEngine.RouteEdges(routedGraph)) == CanvasLayoutJson.Serialize(routed), "route is deterministic");
        var curve = CanvasLayoutEngine.RouteEdges(routedGraph with { Edges = routedGraph.Edges.Select(edge => edge with { Routing = "curved" }).ToArray() });
        Check(Edge(curve, "AB").Routing == "curved", "curved rendering preference survives orthogonal skeleton routing");
        ClearRoute(Edge(curve, "AB"), [Node(curve, "Obstacle")], 12, "curve skeleton");
        var straight = CanvasLayoutEngine.RouteEdges(routedGraph with { Edges = routedGraph.Edges.Select(edge => edge with { Routing = "straight" }).ToArray() });
        Check(Edge(straight, "AB").Waypoints!.Count == 2, "straight routing deliberately retains a direct segment");
        var maze = new CanvasLayoutState([new("A", 0, 100, 40, 40), new("B", 600, 100, 40, 40), new("Wall1", 110, 20, 50, 190), new("Wall2", 260, 100, 50, 180), new("Wall3", 410, 20, 50, 190)],
            [new("AB", "A", "B", SourcePort: new(1, 0.5), TargetPort: new(0, 0.5))]);
        var mazeRoute = CanvasLayoutEngine.RouteEdges(maze);
        ClearRoute(Edge(mazeRoute, "AB"), maze.Nodes.Where(node => node.Id.StartsWith("Wall")), 12, "multiple obstacles");
        var rotated = CanvasLayoutEngine.RouteEdges(simple with { Nodes = simple.Nodes.Select(node => node with { Rotation = node.Id == "A" ? 25 : -35 }).ToArray() });
        ClearRoute(Edge(rotated, "AB"), [], 12, "rotated port stubs");
        var loop = new CanvasLayoutState([new("A", 0, 0, 80, 40)], [new("AA", "A", "A")]);
        Check(Edge(CanvasLayoutEngine.RouteEdges(loop), "AA").Waypoints!.Count >= 4, "automatic self-loop has exterior geometry");
        var samePort = loop with { Edges = [new("AA", "A", "A", SourcePort: new(1, 0.5), TargetPort: new(1, 0.5))] };
        var loopPath = Edge(CanvasLayoutEngine.RouteEdges(samePort), "AA").Waypoints!;
        Check(loopPath.Count >= 5 && loopPath[0] == loopPath[^1], "self-loop supports an explicit shared port");
        var parallel = CanvasLayoutEngine.RouteEdges(simple with { Edges = [new("AB1", "A", "B"), new("AB2", "A", "B"), new("AB3", "A", "B")] });
        Check(parallel.Edges.Select(edge => edge.Waypoints![0]).Distinct().Count() == 3, "parallel edges receive distinct automatic ports");

        CanvasPoint[] manualPath = [new(40, 20), new(100, 20), new(100, 80), new(200, 80), new(200, 20)];
        var manual = new CanvasLayoutState([new("A", 0, 0, 40, 40), new("B", 200, 0, 40, 40)],
            [new("AB", "A", "B", manualPath, Manual: true, SourcePort: new(1, 0.5), TargetPort: new(0, 0.5), LabelOffset: new(4, -8))]);
        Check(Edge(CanvasLayoutEngine.RouteEdges(manual), "AB").Waypoints!.SequenceEqual(manualPath), "route command respects manual bends");
        var translated = CanvasLayoutEngine.UpdateGeometry(manual, Move(manual, 10, 25, "A", "B"));
        Check(Edge(translated, "AB").Waypoints!.SequenceEqual(manualPath.Select(point => new CanvasPoint(point.X + 10, point.Y + 25))), "common endpoint translation moves complete manual route once");
        Check(Edge(translated, "AB").LabelOffset == new CanvasPoint(4, -8), "label displacement survives geometry changes");
        var oneEnd = CanvasLayoutEngine.UpdateGeometry(manual, Move(manual, 15, 30, "A"));
        var attached = Edge(oneEnd, "AB").Waypoints!;
        Check(attached[0] == new CanvasPoint(55, 50) && attached[^1] == new CanvasPoint(200, 20), "one-end movement reattaches dock points");
        Check(manualPath.Skip(1).SkipLast(1).All(attached.Contains), "one-end movement preserves interior manual bends");
        ClearRoute(Edge(oneEnd, "AB"), [], 0, "manual end attachment");
        var repointed = CanvasLayoutEngine.UpdateGeometry(manual, manual with { Edges = [Edge(manual, "AB") with { SourcePort = new(0.5, 1) }] });
        Check(Edge(repointed, "AB").Waypoints![0] == new CanvasPoint(20, 40), "explicit manual port change reattaches without resetting bends");
        var reset = CanvasLayoutEngine.ResetRoutes(manual, ["AB"]);
        Check(!Edge(reset, "AB").Manual && Edge(reset, "AB").Waypoints!.Count >= 2 && Edge(reset, "AB").LabelOffset == new CanvasPoint(4, -8), "explicit route reset clears manual mode and preserves labels");
        var locked = manual with { Edges = [Edge(manual, "AB") with { Locked = true }] };
        Check(Edge(CanvasLayoutEngine.ResetRoutes(locked), "AB").Manual && Edge(CanvasLayoutEngine.UpdateGeometry(locked, Move(locked, 10, 10, "A", "B")), "AB").Waypoints!.SequenceEqual(manualPath), "locked edge geometry survives reset and node motion");
        var lockedNode = manual with { Nodes = manual.Nodes.Select(node => node.Id == "A" ? node with { Locked = true } : node).ToArray() };
        Throws<ArgumentException>(() => CanvasLayoutEngine.UpdateGeometry(lockedNode, Move(lockedNode, 10, 0, "A")), "geometry transaction respects prior node locks");
        var pinnedNode = manual with { Nodes = manual.Nodes.Select(node => node.Id == "A" ? node with { Pinned = true } : node).ToArray() };
        Check(Node(CanvasLayoutEngine.UpdateGeometry(pinnedNode, Move(pinnedNode, 10, 0, "A")), "A").X == 10, "pinning permits explicit manual movement");
        Throws<ArgumentException>(() => CanvasLayoutEngine.UpdateGeometry(manual, manual with { Nodes = [.. manual.Nodes, new("New", 0, 0, 20, 20)] }), "gesture cannot replace graph topology");
        Check(manualPath[0] == new CanvasPoint(40, 20) && Node(manual, "A").X == 0, "geometry operations do not mutate input snapshots");
        return checks;
    }
}
