using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class CanvasMetadataCases
{
    public static int Run()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Canvas metadata: " + message);
        }
        void Valid(XDocument document)
        {
            var nodes = Nodes(document).ToArray();
            var edges = Edges(document).ToArray();
            var ids = nodes.Select(Id).ToHashSet(StringComparer.Ordinal);
            Check(nodes.Length > 0 && nodes.Length == ids.Count, "unique nonempty native node identities");
            Check(edges.Select(Id).Distinct(StringComparer.Ordinal).Count() == edges.Length, "unique native edge identities");
            Check(nodes.Concat(edges).All(element => Id(element).Length is > 0 and <= 512), "bounded identities");
            Check(edges.All(edge => ids.Contains((string?)edge.Attribute("data-entity-1") ?? "") &&
                ids.Contains((string?)edge.Attribute("data-entity-2") ?? "")), "every edge references real nodes");
            Check(nodes.All(node => node.Attribute("data-canvas-parent") is not { } parent || ids.Contains(parent.Value) && parent.Value != Id(node)), "valid containment owners");
            Check(edges.All(edge => edge.Attribute("data-source-line") == null), "edge wrappers do not impersonate source declarations");
            Check(document.Descendants().Where(element => element.Attribute("data-canvas-decoration-for") != null)
                .All(element => ids.Contains((string)element.Attribute("data-canvas-decoration-for")!) && element.Attribute("data-canvas-id") == null),
                "dangling ornaments reference real owners without becoming nodes");
        }

        foreach (string dialect in new[] { "mindmap", "wbs" })
        {
            string source = "@start" + dialect + "\n* Root\n** Alpha\n*** Leaf\n** Beta\n*** Leaf\n** Repeat\n** Repeat\n@end" + dialect;
            var before = Render(source);
            Valid(before);
            var nodes = Nodes(before).ToArray();
            Check(nodes.Length == 7 && Edges(before).Count() == 6, dialect + " node and edge cardinality");
            Check(nodes.All(node => node.Attribute("data-canvas-parent") == null), "tree ancestry is topology, not geometric containment");
            Check(nodes.Select(node => (int)node.Attribute("data-source-line")!).SequenceEqual(Enumerable.Range(1, 7)), "tree physical source mapping is unchanged");
            Check(Edges(before).All(edge => (string?)edge.Attribute("data-style-name") == "arrow" && edge.Attribute("data-style-id") != null), "tree edge styling retained");
            var after = Render(source.Replace("** Alpha", "' unrelated comment\n** Added\n** Alpha", StringComparison.Ordinal));
            foreach (string label in new[] { "Root", "Alpha", "Beta" })
                Check(Id(WithText(before, label)) == Id(WithText(after, label)), dialect + " stable identity for " + label);
            var oldLeaves = nodes.Where(node => Text(node) == "Leaf").Select(Id).Order(StringComparer.Ordinal).ToArray();
            var newLeaves = Nodes(after).Where(node => Text(node) == "Leaf").Select(Id).Order(StringComparer.Ordinal).ToArray();
            Check(oldLeaves.SequenceEqual(newLeaves) && oldLeaves.Distinct().Count() == 2, "equal labels under different tree parents remain distinct and stable");
            Check(Edges(before).Select(Id).All(id => Edges(after).Any(edge => Id(edge) == id)), "unrelated insertion preserves existing tree edge identities");
            Check(before.ToString() == Render(source).ToString(), "tree metadata is deterministic");
        }
        var twoSided = Render("@startmindmap\n* Root\n** East\nleft side\n** West\n***_ Boxless\n@endmindmap");
        Valid(twoSided);
        Check(Nodes(twoSided).Count() == 4 && Edges(twoSided).Count() == 3, "west and boxless branches retain complete graph metadata");

        var simple = Activity("start\n:Prepare;\n:Publish;\nstop");
        Valid(simple);
        Check(Nodes(simple).Count() == 4 && Edges(simple).Count() == 3, "simple activity flow");
        Check(Linked(simple, Kind(simple, "Start"), WithText(simple, "Prepare")) &&
            Linked(simple, WithText(simple, "Prepare"), WithText(simple, "Publish")) &&
            Linked(simple, WithText(simple, "Publish"), Kind(simple, "Stop")), "activity links follow semantic order");

        const string branchBody = "start\nif (Ready?) then (yes)\n:Publish;\nelse (no)\n:Revise;\nendif\nstop";
        var branch = Activity(branchBody);
        Valid(branch);
        var decision = Kind(branch, "Decision"); var join = Kind(branch, "Join");
        Check(Nodes(branch).Count() == 6 && Edges(branch).Count() == 6, "branch header, actions and join remain separate native nodes");
        Check(Linked(branch, decision, WithText(branch, "Publish")) && Linked(branch, decision, WithText(branch, "Revise")) &&
            Linked(branch, WithText(branch, "Publish"), join) && Linked(branch, WithText(branch, "Revise"), join), "branch links refer to the real join");
        Check(Edge(branch, decision, WithText(branch, "Publish")).Descendants().Any(element => element.Name.LocalName == "text" && element.Value == "yes") &&
            Edge(branch, decision, WithText(branch, "Revise")).Descendants().Any(element => element.Name.LocalName == "text" && element.Value == "no"), "branch captions belong to movable edges");
        var inserted = Activity(branchBody.Replace(":Publish;", ":Extra;\n:Publish;", StringComparison.Ordinal));
        foreach (string label in new[] { "Ready?", "Publish", "Revise" })
            Check(Id(WithText(branch, label)) == Id(WithText(inserted, label)), "unrelated action insertion retains " + label + " identity");
        Check(Id(Kind(branch, "Join")) == Id(Kind(inserted, "Join")), "generated join identity is tied to its decision");
        var renamed = Activity(branchBody.Replace(":Publish;", ":Release;", StringComparison.Ordinal));
        Check(Id(WithText(branch, "Publish")) != Id(WithText(renamed, "Release")) && Id(WithText(branch, "Revise")) == Id(WithText(renamed, "Revise")), "label rename cannot silently attach another shape's stored geometry");

        var empty = Activity("if (Ready?) then (yes)\nelse (no)\nendif\nstop");
        Valid(empty);
        var emptyBranches = Edges(empty).Where(edge => (string?)edge.Attribute("data-entity-1") == Id(Kind(empty, "Decision")) &&
            (string?)edge.Attribute("data-entity-2") == Id(Kind(empty, "Join"))).ToArray();
        Check(emptyBranches.Length == 2 && emptyBranches.Select(Id).Distinct().Count() == 2, "empty branches are distinct complete parallel edges");
        Check(emptyBranches.All(edge => MainPath(edge).Count(character => character == 'M') == 1), "empty lanes do not emit disconnected route pieces");

        foreach (string end in new[] { "end fork", "end merge" })
        {
            var fork = Activity("start\nfork\n:One;\nfork again\n:Two;\n" + end + "\nstop");
            Valid(fork);
            Check(Linked(fork, Kind(fork, "Fork"), WithText(fork, "One")) && Linked(fork, WithText(fork, "Two"), Kind(fork, "Join")), "fork and merge graph topology");
        }
        var switched = Activity("start\nswitch (Choice)\ncase (a)\n:One;\ncase (b)\nstop\nendswitch\n:Continue;\nend");
        Valid(switched);
        Check(!Edges(switched).Any(edge => (string?)edge.Attribute("data-entity-1") == Id(Kind(switched, "Stop"))), "terminal branch has no outgoing edge");
        Check(Linked(switched, Kind(switched, "Join"), WithText(switched, "Continue")), "switch continuation begins at generated join");

        foreach (string body in new[]
        {
            "while (Again?) is (yes)\n:Work;\nendwhile (no)\n:After;\nstop",
            "repeat\n:Work;\nrepeat while (Again?) is (yes) not (no)\n:After;\nstop",
            "while (Again?) is (yes)\nendwhile (no)\n:After;\nstop",
            "repeat\nrepeat while (Again?) is (yes) not (no)\n:After;\nstop",
            "while (Outer?) is (yes)\nrepeat\n:Work;\nrepeat while (Inner?) is (yes) not (no)\nendwhile (no)\n:After;\nstop"
        })
        {
            var loop = Activity(body);
            Valid(loop);
            Check(Edges(loop).All(edge => MainPath(edge).Count(character => character == 'M') == 1), "nested and empty loops keep complete connected routes");
            var source = Nodes(loop).First(node => (string?)node.Attribute("class") is "activity-While" or "activity-LoopTest");
            var after = WithText(loop, "After");
            var exit = Edge(loop, source, after);
            var targetBox = after.Elements().First(element => element.Name.LocalName == "rect");
            var end = LastPoint(MainPath(exit));
            Check(end == ((int)targetBox.Attribute("x")! + (int)targetBox.Attribute("width")! / 2, (int)targetBox.Attribute("y")!), "loop exit tail joins the next action's exact entry");
            Check(exit.Descendants().Any(element => element.Name.LocalName == "text" && element.Value == "no"), "loop exit caption moves with its complete route");
            Check(!loop.Descendants().Any(element => element.Attribute("data-canvas-decoration-for") != null), "connected loop has no orphan exit fragments");
        }

        foreach (string body in new[] { "while (Again?) is (yes)\n:Work;\nendwhile (no)", "repeat\n:Work;\nrepeat while (Again?) is (yes) not (no)" })
        {
            var terminal = Activity(body);
            Valid(terminal);
            var tail = terminal.Descendants().Single(element => element.Attribute("data-canvas-decoration-for") != null);
            Check(tail.Descendants().Any(element => element.Name.LocalName == "text" && element.Value == "no"), "terminal open exit and label stay source-owned");
        }

        var partition = Activity("start\npartition Outer {\n:Before;\npartition Inner {\nif (Ready?) then (yes)\n:Work;\nendif\n}\n:After;\n}\nstop");
        Valid(partition);
        var outer = WithText(partition, "Outer"); var inner = WithText(partition, "Inner");
        Check((string?)inner.Attribute("data-canvas-parent") == Id(outer) &&
            (string?)WithText(partition, "Before").Attribute("data-canvas-parent") == Id(outer) &&
            (string?)WithText(partition, "Work").Attribute("data-canvas-parent") == Id(inner) &&
            (string?)Kind(partition, "Join").Attribute("data-canvas-parent") == Id(inner), "nested partition ownership includes generated join nodes");
        Check(Linked(partition, outer, WithText(partition, "Before")) && Linked(partition, inner, Kind(partition, "Decision")) &&
            Linked(partition, WithText(partition, "After"), Kind(partition, "Stop")), "partition entry and exit use actual semantic endpoints");
        Check(Edges(partition).All(edge => MainPath(edge).Count(character => character == 'M') == 1), "nested partition tails are merged into complete edges");

        var lanes = Activity("|Author|\nstart\n:Write;\n|Reviewer|\n:Review;\n|Author|\n:Publish;\nstop\nlegend\nDescription\nendlegend");
        Valid(lanes);
        Check(Nodes(lanes).Count() == 5 && Edges(lanes).Count() == 4, "swimlane headers and legend stay annotations");
        Check(MainPath(Edge(lanes, WithText(lanes, "Write"), WithText(lanes, "Review"))).Contains('H'), "swimlane transition remains one complete edge");
        var duplicates = Activity(":Same;\nif (Again?) then\n:Same;\nelse\n:Same;\nendif\n:Same;");
        Valid(duplicates);
        Check(Nodes(duplicates).Where(node => Text(node) == "Same").Select(Id).Distinct().Count() == 4, "repeated activity labels are unambiguous");
        Check(branch.ToString() == Activity(branchBody).ToString(), "activity metadata is deterministic");
        var sequence = Render("@startuml\nAlice -> Bob: hello\n@enduml");
        Check(!sequence.Descendants().Any(element => element.Attribute("data-canvas-id") != null), "sequence semantics stay under the sequence editor");

        const string networkSource = "@startnwdiag\nnwdiag {\nnetwork shared {\naddress = \"192.0.2.0/24\";\nshared [address=\"192.0.2.2\",description=\"Gateway\"];\nleaf [address=\"192.0.2.3\"];\n}\nnetwork inside {\nshared [address=\"10.0.0.1\"];\ndb [address=\"10.0.0.2\"];\n}\nnetwork storage {\nshared [address=\"172.16.0.1\"];\n}\n}\n@endnwdiag";
        var network = Render(networkSource);
        Valid(network);
        Check(Nodes(network).Count() == 6 && Edges(network).Count() == 5, "network buses and shared hosts have one node, with one complete edge per interface");
        var networkIds = Nodes(network).ToDictionary(Id, StringComparer.Ordinal);
        XElement NetworkNode(XDocument document, string kind, string alias) => Nodes(document).Single(n => (string?)n.Attribute("class") == kind && (string?)n.Attribute("data-qualified-name") == alias);
        Check(Id(NetworkNode(network, "network", "shared")) != Id(NetworkNode(network, "entity", "shared")), "network and host alias namespaces cannot collide");
        Check(Nodes(network).All(n => n.Attribute("data-canvas-parent") == null), "network membership is connectivity, not containment");
        foreach (var edge in Edges(network))
        {
            var bus = networkIds[(string)edge.Attribute("data-entity-1")!];
            var host = networkIds[(string)edge.Attribute("data-entity-2")!];
            var rail = bus.Elements().Single(n => n.Name.LocalName == "rect");
            var shape = host.Elements().Single(n => n.Name.LocalName == "rect");
            string route = MainPath(edge); var endpoint = LastPoint(route);
            var start = Regex.Match(route, @"^M(-?\d+) (-?\d+)", RegexOptions.CultureInvariant);
            int x = int.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture), y = int.Parse(start.Groups[2].Value, CultureInfo.InvariantCulture);
            Check((int)rail.Attribute("height")! >= 1 && (int)rail.Attribute("width")! >= 1 &&
                x >= (int)rail.Attribute("x")! && x <= (int)rail.Attribute("x")! + (int)rail.Attribute("width")! &&
                (y == (int)rail.Attribute("y")! || y == (int)rail.Attribute("y")! + (int)rail.Attribute("height")!), "network route starts on the finite bus rail boundary");
            Check(endpoint.X == (int)shape.Attribute("x")! + (int)shape.Attribute("width")! / 2 &&
                (endpoint.Y == (int)shape.Attribute("y")! || endpoint.Y == (int)shape.Attribute("y")! + (int)shape.Attribute("height")!), "network interface ends at the shared host boundary");
            Check(route.Count(c => c == 'M') == 1 && edge.Descendants().Count(n => (string?)n.Attribute("class") == "interface") == 1 &&
                edge.Descendants().Any(n => (string?)n.Attribute("class") == "interface-address" && n.Attribute("data-source-line") != null), "interface route, port and address remain individually source mapped");
            var attachments = edge.Elements().Where(n => (string?)n.Attribute("class") is "interface" or "interface-address").ToArray();
            Check(attachments.Length == 2 && attachments.All(n => (string?)n.Attribute("data-canvas-fraction") == "0"),
                "network ports and interface addresses explicitly stay at the bus/source attachment for short and long routes");
        }
        var busCaption = network.Descendants().Single(n => (string?)n.Attribute("data-canvas-decoration-for") == Id(NetworkNode(network, "network", "shared")));
        Check(Text(busCaption) == "shared192.0.2.0/24" && (int?)busCaption.Attribute("data-source-line") == 2 &&
            busCaption.Descendants().Any(n => (int?)n.Attribute("data-source-line") == 3), "bus caption and address move together without changing source lines or docking bounds");
        var addedNetwork = Render(networkSource.Replace("network shared {", "network unrelated {\nisolated;\n}\nnetwork shared {", StringComparison.Ordinal));
        foreach (var original in Nodes(network))
            Check(Id(original) == Id(NetworkNode(addedNetwork, (string)original.Attribute("class")!, (string)original.Attribute("data-qualified-name")!)), "network alias identity survives an unrelated earlier network");
        Check(Edges(network).Select(Id).All(id => Edges(addedNetwork).Any(edge => Id(edge) == id)), "existing interface identities survive inserted buses and hosts");
        var describedNetwork = Render(networkSource.Replace("Gateway", "Renamed gateway", StringComparison.Ordinal));
        Check(Id(NetworkNode(network, "entity", "shared")) == Id(NetworkNode(describedNetwork, "entity", "shared")), "network aliases retain geometry across description changes");
        Check(network.ToString() == Render(networkSource).ToString(), "network metadata is deterministic");

        const string filesSource = "@startfiles\n/src/A.cs\n/tests/A.cs\n/src/\n/README.md\n/src/nested/Leaf.txt\n@endfiles";
        var files = Render(filesSource);
        Valid(files);
        Check(Nodes(files).Count() == 7 && !Edges(files).Any() && Nodes(files).All(n => n.Attribute("data-canvas-parent") == null), "files expose individual rows without invented connectors or enclosing folder geometry");
        Check((int?)WithText(files, "src").Attribute("data-source-line") == 3 && WithText(files, "tests").Attribute("data-source-line") == null,
            "explicit and implicit file directories preserve source ownership");
        Check(Nodes(files).Where(n => Text(n) == "A.cs").Select(Id).Distinct().Count() == 2, "identical file names under different paths remain distinct");
        var filesAdded = Render(filesSource.Replace("/src/A.cs", "' independent comment\n/docs/Guide.md\n/src/A.cs", StringComparison.Ordinal));
        Check(Nodes(files).Select(Id).All(id => Nodes(filesAdded).Any(n => Id(n) == id)), "unrelated file path insertion preserves every existing identity");
        var filesImplicit = Render(filesSource.Replace("/src/\n", "", StringComparison.Ordinal));
        Check(Id(WithText(files, "src")) == Id(WithText(filesImplicit, "src")), "making a directory implicit does not change path identity");
        var filesRenamed = Render(filesSource.Replace("/src/A.cs", "/src/B.cs", StringComparison.Ordinal));
        Check(!Nodes(files).Any(n => Id(n) == Id(WithText(filesRenamed, "B.cs"))) && Id(WithText(files, "src")) == Id(WithText(filesRenamed, "src")) &&
            Id(WithText(files, "Leaf.txt")) == Id(WithText(filesRenamed, "Leaf.txt")), "renamed file gets new identity while unrelated paths remain stable");
        Check(files.ToString() == Render(filesSource).ToString(), "file metadata is deterministic");
        foreach (int size in new[] { 2048, 2049 })
        {
            string largeFiles = "@startfiles\n<style>\nfile {\nFontSize 8\nPadding 0\n}\n</style>\n" +
                string.Join("\n", Enumerable.Range(0, size).Select(i => "/file" + i.ToString(CultureInfo.InvariantCulture))) + "\n@endfiles";
            var listing = Render(largeFiles);
            Check(listing.Descendants().Count(n => n.Name.LocalName == "text") == size, "large native listing retains every file");
            Check(Nodes(listing).Count() == (size <= 2048 ? size : 0), "files metadata respects the editor's 2048-node bound without rejecting native rendering");
        }
        return assertions;
    }

    private static XDocument Activity(string body) => Render("@startuml\n" + body + "\n@enduml");
    private static XDocument Render(string source)
    {
        if (!PlantUmlRenderer.TryRender(source, out var result)) throw new InvalidOperationException("Native canvas fixture rejected: " + source);
        return XDocument.Parse(result.Svg);
    }
    private static IEnumerable<XElement> Nodes(XDocument document) => document.Descendants().Where(element => element.Attribute("data-canvas-id") != null && (string?)element.Attribute("class") != "link");
    private static IEnumerable<XElement> Edges(XDocument document) => document.Descendants().Where(element => (string?)element.Attribute("class") == "link");
    private static string Id(XElement element) => (string)element.Attribute("data-canvas-id")!;
    private static string Text(XElement element) => string.Join("", element.Descendants().Where(child => child.Name.LocalName == "text").Select(child => child.Value));
    private static XElement WithText(XDocument document, string text) => Nodes(document).Single(node => Text(node) == text);
    private static XElement Kind(XDocument document, string kind) => Nodes(document).Single(node => (string?)node.Attribute("class") == "activity-" + kind);
    private static bool Linked(XDocument document, XElement source, XElement target) => Edges(document).Any(edge => (string?)edge.Attribute("data-entity-1") == Id(source) && (string?)edge.Attribute("data-entity-2") == Id(target));
    private static XElement Edge(XDocument document, XElement source, XElement target) => Edges(document).Single(edge => (string?)edge.Attribute("data-entity-1") == Id(source) && (string?)edge.Attribute("data-entity-2") == Id(target));
    private static string MainPath(XElement edge) => (string)edge.Elements().First(element => element.Name.LocalName == "path").Attribute("d")!;
    private static (int X, int Y) LastPoint(string route)
    {
        var parts = Regex.Matches(route, @"[MLHV]|-?\d+", RegexOptions.CultureInvariant).Select(match => match.Value).ToArray();
        int x = 0, y = 0;
        for (int i = 0; i < parts.Length;)
        {
            string command = parts[i++];
            int value = int.Parse(parts[i++], CultureInfo.InvariantCulture);
            if (command is "M" or "L") { x = value; y = int.Parse(parts[i++], CultureInfo.InvariantCulture); }
            else if (command == "H") x = value;
            else if (command == "V") y = value;
            else throw new InvalidOperationException("Unexpected activity route command");
        }
        return (x, y);
    }
}
