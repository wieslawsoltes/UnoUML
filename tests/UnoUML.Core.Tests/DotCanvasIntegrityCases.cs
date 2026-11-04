using System.Globalization;
using System.Xml.Linq;
using UnoUML.Core;

internal static class DotCanvasIntegrityCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("DOT canvas identity: " + message); checks++; }
        static StudioDocument Doc(string body) => new("@startdot\n" + body + "\n@enddot");
        static XElement[] Edges(StudioDocument doc) => DotStructureCases.Render(doc).Descendants()
            .Where(element => (string?)element.Attribute("data-dot-kind") == "edge").ToArray();
        static DotObjectEdit Edge(StudioDocument doc, int index) => DotStructureCases.Open(doc,
            Edges(doc).Single(element => (string?)element.Attribute("data-dot-id") == "edge:" + index));
        static DotObjectEdit Node(StudioDocument doc, string name) => DotStructureCases.Open(doc, "node", name);
        static CanvasLayoutState Read(StudioDocument doc) => doc.TryGetCanvasLayout(out var layout, out var error) ? layout! : throw new Exception(error);
        static CanvasLayoutState Seed(StudioDocument doc, int[]? selected = null)
        {
            var svg = DotStructureCases.Render(doc);
            var nodes = svg.Descendants().Where(element => (string?)element.Attribute("data-dot-kind") == "node")
                .Select((element, index) => new CanvasNode((string)element.Attribute("data-dot-id")!, index * 100, 20, 60, 40)).ToArray();
            var edges = svg.Descendants().Where(element => (string?)element.Attribute("data-dot-kind") == "edge")
                .Select((element, index) => new CanvasEdge((string)element.Attribute("data-dot-id")!,
                    "node:" + (string?)element.Attribute("data-dot-from-id"), "node:" + (string?)element.Attribute("data-dot-to-id"),
                    [new(10, 20 + index), new(50 + index, 80 - index), new(100, 20 + index)],
                    "polyline", true, index % 2 == 0, new(1, 0.5), new(0, 0.5), new(index + 11, index + 22)))
                .Where((_, index) => selected == null || selected.Contains(index)).ToArray();
            if (!doc.SetCanvasLayout(new(nodes, edges))) throw new Exception("Could not install DOT canvas fixture.");
            return Read(doc);
        }
        void Routes(StudioDocument doc, CanvasLayoutState before, int?[] origins, string message)
        {
            var actual = Read(doc);
            var expected = origins.Select((origin, index) => (origin, index)).Where(pair => pair.origin != null)
                .Select(pair => (Original: before.Edges.SingleOrDefault(edge => edge.Id == "edge:" + pair.origin), pair.index))
                .Where(pair => pair.Original != null).Select(pair => pair.Original! with { Id = "edge:" + pair.index }).ToArray();
            Check(actual.Edges.Count == expected.Length, message + " drops deleted/stale route records");
            foreach (var expectedEdge in expected)
            {
                var edge = actual.Edges.Single(candidate => candidate.Id == expectedEdge.Id);
                Check(edge with { Waypoints = null } == expectedEdge with { Waypoints = null } &&
                    edge.Waypoints!.SequenceEqual(expectedEdge.Waypoints!), message + " keeps the exact route, lock, ports and label offset for " + edge.Id);
            }
            var rendered = Edges(doc);
            Check(actual.Edges.All(edge => rendered.Any(element => (string?)element.Attribute("data-dot-id") == edge.Id &&
                "node:" + (string?)element.Attribute("data-dot-from-id") == edge.Source && "node:" + (string?)element.Attribute("data-dot-to-id") == edge.Target)),
                message + " agrees with current native edge IDs");
            Check(actual.Nodes.SequenceEqual(before.Nodes) && actual.Settings == before.Settings, message + " leaves node geometry and settings unchanged");
        }
        void Undo(StudioDocument doc, string before, string message)
        {
            string after = doc.Source;
            Check(doc.Undo() && doc.Source == before, message + " restores source and metadata in one undo");
            Check(doc.Redo() && doc.Source == after, message + " restores source and metadata in one redo");
        }
        void Remove(string body, int index, int?[] survivors, string message, int[]? saved = null)
        {
            var doc = Doc(body); var layout = Seed(doc, saved); string before = doc.Source;
            var edge = Edge(doc, index);
            Check(doc.RemoveDotEdge(edge), message + " edits source");
            Routes(doc, layout, survivors, message);
            string after = doc.Source; long history = doc.HistoryState;
            Check(!doc.RemoveDotEdge(edge) && doc.Source == after && doc.HistoryState == history, message + " stale selection changes neither source nor history");
            Undo(doc, before, message);
        }

        const string parallels = "digraph { a -> b [label=Same]; a -> b [label=Same]; a -> b [label=Same]; }";
        Remove(parallels, 0, [1, 2], "delete first identical parallel");
        Remove(parallels, 1, [0, 2], "delete middle identical parallel");
        Remove(parallels, 2, [0, 1], "delete last identical parallel");
        Remove(parallels, 0, [1, 2], "partial layout containing only deleted route", [0]);
        Remove(parallels, 0, [1, 2], "partial layout containing only later route", [2]);
        Remove("digraph { a->b->c[label=Same]; a->b[label=Same]; }", 0, [1, 2], "delete a shared-chain edge beside a parallel");
        Remove("strict digraph { a->b->c; a->b[color=red]; b->c[color=blue]; c->d; }", 0, [1, 2], "delete a strict edge with multiple contributions");
        Remove("strict graph { a--b--c; b--a[color=red]; c--b[color=blue]; c--d; }", 0, [1, 2], "delete a reversed strict undirected edge");
        Remove("digraph { \"a:b\"->\"a:b\"; \"a:b\"->\"a:b\"; }", 0, [1], "delete a quoted self-loop occurrence");

        const string nested = "digraph { a -> b -> { a -> b; c; }; }";
        {
            var doc = Doc(nested); var layout = Seed(doc); string before = doc.Source;
            Check(doc.NormalizeDotEdge(Edge(doc, 0)), "normalize nested product");
            Routes(doc, layout, [1, 0, 2, 3, 4], "normalization moves outer and inner parallel occurrences past each other");
            Undo(doc, before, "nested normalization");
        }
        Remove(nested, 0, [1, 2, 3, 4], "delete outer edge without stealing its identical inner edge route");
        {
            var doc = Doc(nested); var layout = Seed(doc); string before = doc.Source;
            Check(doc.RenameDotObject(Edge(doc, 0), "Renamed"), "shared label edit normalizes source");
            Routes(doc, layout, [1, 0, 2, 3, 4], "shared label edit retains provenance despite changed text and edge order");
            Undo(doc, before, "shared label edit");
        }
        {
            var doc = Doc("digraph { { x; a; } -> { b; c; }; a->b; }"); var layout = Seed(doc); string before = doc.Source;
            Check(doc.RemoveDotObjects([Node(doc, "x")]), "node deletion removes product members");
            Routes(doc, layout, [2, 3, 4], "node deletion reindexes surviving product occurrences and outside parallels");
            Undo(doc, before, "node deletion");
        }
        {
            var doc = Doc("digraph { subgraph cluster_box { x; y; } x->y; a->b; a->b; }"); var layout = Seed(doc); string before = doc.Source;
            Check(doc.RemoveDotObject(DotStructureCases.Open(doc, "cluster", "cluster_box")), "cluster deletion removes its edge");
            Routes(doc, layout, [1, 2], "cluster deletion preserves outside parallel occurrences");
            Undo(doc, before, "cluster deletion");
        }
        {
            var doc = Doc("digraph { a->b; a->b; c; }"); var layout = Seed(doc); string before = doc.Source;
            Check(doc.RetargetDot(Edge(doc, 0), Node(doc, "c"), false), "retarget first parallel");
            Routes(doc, layout, [null, 1], "retarget drops the old endpoint route without touching the parallel");
            Undo(doc, before, "retarget first parallel");
            Check(doc.RemoveDotEdge(Edge(doc, 0)), "delete previously retargeted edge");
            Routes(doc, layout, [1], "retarget then deletion cannot revive a stale route");
        }
        {
            var doc = Doc("digraph { a->b; }"); var layout = Seed(doc);
            Check(doc.SetCanvasLayout(layout with { Edges = [layout.Edges[0], layout.Edges[0] with { Id = "edge:1", LabelOffset = new(999, 999) }] }), "install stale unused ordinal");
            string before = doc.Source;
            Check(doc.ConnectDot(Node(doc, "a"), Node(doc, "b")), "append parallel connection");
            Routes(doc, layout, [0, null], "new connection cannot inherit an old unused ordinal");
            Undo(doc, before, "append connection");
        }
        {
            var doc = Doc("digraph { a[shape=record,label=\"<p> Port|Body\"]; a:p->b->c[tailport=p]; }"); var layout = Seed(doc); string before = doc.Source;
            var label = doc.OpenDotLabel(Node(doc, "a"))!;
            Check(doc.SetDotRecordFieldPort(label, "0", "renamed"), "port rename normalizes edge attributes");
            Routes(doc, layout, [0, 1], "port rename keeps the logical edges and their canvas state");
            Undo(doc, before, "port rename");
        }
        {
            var doc = new StudioDocument(StudioDocument.CanvasLayoutMarker + "broken\n@startdot\ndigraph { a->b; a->b; }\n@enddot");
            string before = doc.Source; long history = doc.HistoryState;
            Check(!doc.RemoveDotEdge(Edge(doc, 0)) && doc.Source == before && doc.HistoryState == history,
                "invalid canvas metadata refuses reindexing without publishing source or history");
        }
        return checks;
    }
}
