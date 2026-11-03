using UnoUML.Core;

internal static class DotEditCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("DOT editing " + message); checks++; }
        const string source = "@startdot\r\ndigraph G {\r\n  \"a\\\\path\":port -> b [label=\"First\"]; // keep\r\n  b [label=\"B\",color=red];\r\n  c;\r\n}\r\n@enddot";
        int aAt = source.IndexOf("\"a", StringComparison.Ordinal), bAt = source.IndexOf("b [label=\"B\"", StringComparison.Ordinal), cAt = source.IndexOf("c;", StringComparison.Ordinal);
        int close = source.LastIndexOf('}'), edgeEnd = source.IndexOf(';', aAt), bEnd = source.IndexOf(';', bAt);
        var a = new DotSourceLocation { Id = "node:a\\\\path", Kind = "node", Name = "a\\\\path", Text = "a\\path", NameStart = aAt, NameLength = "\"a\\\\path\"".Length,
            Start = aAt, Length = "\"a\\\\path\":port".Length, LabelInsert = close, Directed = true };
        var b = new DotSourceLocation { Id = "node:b", Kind = "node", Name = "b", Text = "B", NameStart = bAt, NameLength = 1,
            Start = bAt, Length = bEnd - bAt, LabelStart = source.IndexOf("\"B\"", bAt, StringComparison.Ordinal), LabelLength = 3, LabelKind = "quoted", Explicit = true, Directed = true };
        var c = new DotSourceLocation { Id = "node:c", Kind = "node", Name = "c", Text = "c", NameStart = cAt, NameLength = 1,
            Start = cAt, Length = 1, LabelInsert = cAt + 1, Explicit = true, Directed = true };
        int arrow = source.IndexOf("->", StringComparison.Ordinal), first = source.IndexOf("\"First\"", StringComparison.Ordinal);
        var edge = new DotSourceLocation { Id = "edge:0", Kind = "edge", Start = aAt, Length = edgeEnd - aAt, Explicit = true, Directed = true,
            Text = "First", LabelStart = first, LabelLength = 7, LabelKind = "quoted", ArrowStart = arrow, ArrowLength = 2,
            FromStart = aAt, FromLength = "\"a\\\\path\":port".Length, ToStart = arrow + 3, ToLength = 1, FromId = a.Name, ToId = b.Name };
        var doc = new StudioDocument(source);
        DotObjectEdit Open(DotSourceLocation location) => doc.OpenDotObject(location) ?? throw new Exception("Missing DOT projection");
        var original = Open(b);
        Check(doc.RenameDotObject(original, "New \"caption\"\nLine") && doc.Source == source.Replace("label=\"B\"", "label=\"New \\\"caption\\\"\\nLine\"", StringComparison.Ordinal), "renames exact quoted labels and preserves formatting");
        Check(!doc.RenameDotObject(original, "stale") && doc.Undo() && doc.Source == source, "guards stale snapshots and undoes once");
        Check(doc.RenameDotObject(Open(a), "Visible") && doc.Source.Contains("\"a\\\\path\" [label=\"Visible\"]", StringComparison.Ordinal), "implicit override preserves original escaped identifier");
        Check(doc.Undo() && doc.RenameDotObject(Open(c), "C"), "adds label attribute to explicit node");
        Check(doc.Source == source.Replace("  c;", "  c [label=\"C\"];", StringComparison.Ordinal) && doc.Undo(), "preserves surrounding declaration syntax");
        Check(doc.ConnectDot(Open(a), Open(c), "Calls") && doc.Source.Contains("\"a\\\\path\" -> c [label=\"Calls\"]", StringComparison.Ordinal), "connects original identifiers");
        Check(doc.Undo() && doc.RetargetDot(Open(edge), Open(c), true) && doc.Source.Contains("c:port -> b [label=\"First\"]", StringComparison.Ordinal), "retargets exact endpoint and retains port");
        Check(doc.Undo() && doc.RenameDotObject(Open(edge), "Sends") && doc.Source == source.Replace("\"First\"", "\"Sends\"", StringComparison.Ordinal), "edits a relationship label");
        Check(doc.Undo() && doc.RemoveDotEdge(Open(edge)) && doc.Source.Contains("\"a\\\\path\"; b;", StringComparison.Ordinal) &&
            !doc.Source.Contains("->", StringComparison.Ordinal) && doc.Source.Contains("// keep", StringComparison.Ordinal), "removes one relationship while retaining endpoint declarations and comments");
        Check(doc.Undo() && doc.Source == source, "relationship edit undo restores comments and CRLF");
        var merged = Open(edge with { Merged = true });
        Check(!merged.CanEditConnection && !doc.RemoveDotEdge(merged) && !doc.RetargetDot(merged, Open(c), false) && doc.Source == source, "merged strict declarations cannot be partially deleted or reconnected");
        Check(merged.CanRename && doc.RenameDotObject(merged, "Merged label") && doc.Undo(), "merged relationship labels retain exact-token editing");
        Check(doc.OpenDotObject(b with { NameStart = -1 }) == null && doc.OpenDotObject(edge with { ArrowStart = 0 }) == null, "rejects invalid metadata");
        const string shared = "@startdot\ndigraph { a,b [label=\"Both\"]; }\n@enddot";
        var common = new StudioDocument(shared);
        int at = shared.IndexOf("a,b", StringComparison.Ordinal), end = shared.IndexOf(';', at);
        var sharedLocation = new DotSourceLocation { Id = "node:b", Kind = "node", Name = "b", Text = "Both", NameStart = at + 2, NameLength = 1,
            Start = at, Length = end - at, LabelStart = shared.IndexOf("\"Both\"", StringComparison.Ordinal), LabelLength = 6, LabelKind = "quoted", Explicit = true, Directed = true };
        Check(common.RenameDotObject(common.OpenDotObject(sharedLocation)!, "Only B") && common.Source.Contains("a,b [label=\"Both\"];\n  b [label=\"Only B\"];", StringComparison.Ordinal), "materializes one shared-node label override");
        const string chain = "@startdot\ndigraph { a -> b -> c [label=\"Shared\"]; }\n@enddot";
        var chained = new StudioDocument(chain);
        int chainStart = chain.IndexOf("a ->", StringComparison.Ordinal), chainEnd = chain.IndexOf(';', chainStart);
        var sharedEdge = edge with { Start = chainStart, Length = chainEnd - chainStart, ArrowStart = chainStart + 2,
            LabelStart = chain.IndexOf("\"Shared\"", StringComparison.Ordinal), LabelLength = 8, FromStart = chainStart, FromLength = 1, ToStart = chainStart + 5, ToLength = 1 };
        var chainItem = chained.OpenDotObject(sharedEdge)!;
        Check(!chainItem.CanRename && !chained.RemoveDotEdge(chainItem) && chained.Source == chain, "shared chain edits stay transactional");
        var singleLine = new StudioDocument("@startdot\ngraph { a -- b; }\n@enddot");
        var nodeTool = StudioCatalog.Tools.First(tool => tool.Group == "Topology" && tool.Name == "Node");
        var edgeTool = StudioCatalog.Tools.First(tool => tool.Group == "Topology" && tool.Name == "Edge");
        Check(singleLine.Add(nodeTool) && singleLine.Source.Contains("\"Node\" [label=\"Node\"];\n}", StringComparison.Ordinal), "toolbox inserts within a one-line DOT graph");
        Check(singleLine.Add(edgeTool) && singleLine.Source.Contains("client -- \"Edge\"", StringComparison.Ordinal), "toolbox respects undirected graph syntax");
        const string concatenated = "@startdot\ndigraph { \"part\" + \"two\" [label=\"One\" + \" Two\"]; }\n@enddot";
        var joined = new StudioDocument(concatenated);
        int joinedName = concatenated.IndexOf("\"part\"", StringComparison.Ordinal), joinedLabel = concatenated.IndexOf("\"One\"", StringComparison.Ordinal);
        var joinedLocation = new DotSourceLocation { Id = "node:parttwo", Kind = "node", Name = "parttwo", Text = "One Two", NameStart = joinedName, NameLength = "\"part\" + \"two\"".Length,
            Start = joinedName, Length = concatenated.IndexOf(';') - joinedName, LabelStart = joinedLabel, LabelLength = "\"One\" + \" Two\"".Length, LabelKind = "quoted", Explicit = true, Directed = true };
        Check(joined.RenameDotObject(joined.OpenDotObject(joinedLocation)!, "Joined") && joined.Source.Contains("\"part\" + \"two\" [label=\"Joined\"", StringComparison.Ordinal) &&
            !joined.Source.Contains("\" Two\"", StringComparison.Ordinal), "replaces concatenated label tokens while retaining concatenated identifiers");
        Check(joined.Undo() && joined.Source == concatenated, "concatenated-label replacement undoes byte for byte");
        return checks + DotStructureCases.Run();
    }
}
