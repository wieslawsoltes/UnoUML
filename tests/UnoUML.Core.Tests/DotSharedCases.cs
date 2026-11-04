using System.Xml.Linq;
using UnoUML.Core;

internal static class DotSharedCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("DOT shared statements: " + message); checks++; }
        static StudioDocument Doc(string text) => new("@startdot\n" + text + "\n@enddot");
        static DotObjectEdit Edge(StudioDocument doc, string name) => DotStructureCases.Open(doc, "edge", name);
        static DotObjectEdit Node(StudioDocument doc, string name) => DotStructureCases.Open(doc, "node", name);
        static XElement Svg(StudioDocument doc) => DotStructureCases.Render(doc);
        static string[] Edges(StudioDocument doc) => Svg(doc).Descendants().Where(x => (string?)x.Attribute("data-dot-kind") == "edge")
            .Select(x => (string)x.Attribute("data-qualified-name")!).Order(StringComparer.Ordinal).ToArray();
        foreach (var body in new[]
        {
            "digraph { node[color=red]; edge[color=blue]; a:p -> /* keep arrow */ b:q -> c [label=\"One\"+/* keep label */\"Two\"]; node[color=green]; d; }",
            "strict digraph { node[color=red]; edge[color=blue]; a:p -> b:q -> c [label=Shared]; edge[color=green]; a->b[penwidth=3]; b->c[label=Other]; d; }",
            "digraph { a -> subgraph cluster_middle { node[color=red]; b; c; b->c[label=inside]; } -> d [label=Outer]; }",
            "strict graph { a:p--b:q--c[label=First]; b--a[color=red]; }",
            "digraph { a,b -> c,d [label=Each]; }"
        })
        {
            var doc = Doc(body); string original = doc.Source;
            var names = Edges(doc);
            var chosen = Svg(doc).Descendants().First(x => (string?)x.Attribute("data-dot-kind") == "edge" &&
                ((string?)x.Attribute("data-dot-label-text") != "inside"));
            var item = DotStructureCases.Open(doc, chosen);
            Check(item.CanEditConnection && item.CanRemove, "chain/product/merged edges expose safe structural editing");
            Check(doc.NormalizeDotEdge(item), "normalization succeeds: " + body);
            Check(Edges(doc).SequenceEqual(names), "normalization retains every logical edge");
            string normalized = doc.Source;
            Check(!doc.NormalizeDotEdge(Edge(doc, item.Location.Name)) && doc.Source == normalized,
                "normalization is idempotent even when strict edges have repeated contributions");
            foreach (var comment in new[] { "/* keep arrow */", "/* keep label */" })
                if (original.Contains(comment, StringComparison.Ordinal))
                    Check(doc.Source.Split(comment, StringSplitOptions.None).Length == 2, "preserves each inter-token comment exactly once");
            Check(!doc.NormalizeDotEdge(item) && doc.Undo() && doc.Source == original && doc.Redo() && doc.Undo(), "normalization guards stale snapshots and has byte-exact undo/redo");
            item = DotStructureCases.Open(doc, chosen);
            Check(doc.RemoveDotEdge(item), "selected shared edge is deleted atomically");
            var removed = names.ToList(); removed.Remove(item.Location.Name);
            Check(Edges(doc).SequenceEqual(removed), "other chain/product/merged edges remain unchanged");
            Check(doc.Undo() && doc.Source == original, "shared deletion has exact single undo");
        }
        var rename = Doc("digraph { a->b->c[label=Shared]; }");
        Check(rename.RenameDotObject(Edge(rename, "a->b"), "Only AB"), "single shared-chain display label can be changed");
        Check(Svg(rename).Descendants().Single(x => (string?)x.Attribute("data-qualified-name") == "b->c").Value.Contains("Shared", StringComparison.Ordinal),
            "renaming one expanded segment does not rename the others");
        var retarget = Doc("strict digraph { d; a:p->b:q->c[label=Shared]; a->b[color=red]; }");
        string source = retarget.Source;
        Check(retarget.RetargetDot(Edge(retarget, "a->b"), Node(retarget, "d"), true), "reconnects every declaration of a merged edge");
        Check(Edges(retarget).SequenceEqual(new[] { "b->c", "d->b" }), "shared retarget leaves other segments intact");
        Check(retarget.Source.Contains("d:p -> b:q", StringComparison.Ordinal) && retarget.Undo() && retarget.Source == source,
            "shared retarget retains ports and exact undo");
        var collision = Doc("strict digraph { a->b->c; a->c; }");
        source = collision.Source;
        Check(!collision.RetargetDot(Edge(collision, "a->b"), Node(collision, "c"), false) && collision.Source == source && !collision.CanUndo,
            "strict collisions reject the entire normalized transaction");
        var scope = Doc("digraph { subgraph cluster_one { a->b->c; } subgraph cluster_two { d; } }");
        Check(!scope.RetargetDot(Edge(scope, "a->b"), Node(scope, "d"), false) && !scope.CanUndo, "does not silently change cluster membership");
        return checks;
    }
}
