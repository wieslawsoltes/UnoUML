using System.Xml.Linq;
using UnoUML.Core;
using UnoUML.PlantUml;

internal static class DotStructureCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("DOT structure " + message); checks++; }
        void Graph(StudioDocument document, string[] nodes, string[] edges, string message)
        {
            var svg = Render(document);
            Check(NodeNames(svg).SequenceEqual(nodes.Order(StringComparer.Ordinal)) && EdgeNames(svg).SequenceEqual(edges.Order(StringComparer.Ordinal)), message);
        }

        foreach (string ending in new[] { "\n", "\r\n", "\r" })
        {
            string source = ("\uFEFF@startdot first\ndigraph {\n node [shape=box,color=blue];\n a [label=\"A\"]; // a remains in the comment\n" +
                " a:out -> b -> c:in -> d [color=red,label=\"a -> b\"];\n c [label=\"C\"];\n}\n@enddot").ReplaceLineEndings(ending);
            var document = new StudioDocument(source);
            var b = Open(document, "node", "b");
            Check(b.CanRemove && b.CanDuplicate && document.RemoveDotObject(b), "deletes an implicit middle node " + ending.Length);
            Graph(document, ["a", "c", "d"], ["c->d"], "retains separate chain segments and isolated survivors");
            var svg = Render(document);
            Check(Node(svg, "a").Descendants().Any(element => (string?)element.Attribute("stroke") == "#0000ff") &&
                document.Source.Contains("// a remains in the comment", StringComparison.Ordinal) && document.Source.Contains("label=\"a -> b\"", StringComparison.Ordinal),
                "retains comments, unrelated labels and node defaults");
            Check(!document.RemoveDotObject(b) && document.Undo() && document.Source == source, "stale deletion is rejected and one undo restores every byte");
            Check(document.Redo() && document.Source.StartsWith('\uFEFF') && document.Undo(), "redo retains the BOM and source grammar");
        }

        const string lists = "@startdot\ndigraph { node [color=blue]; a /* first */ , b /* middle */ , c -> d,e,f [color=red]; }\n@enddot";
        for (int mask = 1; mask < 64; mask++)
        {
            var document = new StudioDocument(lists);
            string[] names = ["a", "b", "c", "d", "e", "f"];
            var removed = names.Where((_, index) => (mask & 1 << index) != 0).ToHashSet(StringComparer.Ordinal);
            Check(document.RemoveDotObjects(removed.Select(name => Open(document, "node", name)).ToArray()), "deletes cartesian endpoint subset " + mask);
            var edges = names[..3].Where(name => !removed.Contains(name)).SelectMany(from => names[3..].Where(name => !removed.Contains(name)).Select(to => from + "->" + to)).ToArray();
            Graph(document, names.Where(name => !removed.Contains(name)).ToArray(), edges, "preserves cartesian endpoint semantics " + mask);
            Check(document.Source.Contains("/* first */", StringComparison.Ordinal) && document.Source.Contains("/* middle */", StringComparison.Ordinal), "retains endpoint comments " + mask);
            foreach (var node in Render(document).Descendants().Where(element => (string?)element.Attribute("data-dot-kind") == "node"))
                Check(node.Descendants().Any(element => (string?)element.Attribute("stroke") == "#0000ff"), "edge attributes never become node attributes after endpoint removal");
        }

        var nested = Document("digraph { { a; b } -> { c; d } -> e [label=Shared]; a -> z; }");
        Check(nested.RemoveDotObjects([Open(nested, "node", "b"), Open(nested, "node", "c")]), "deletes nested edge-product members");
        Graph(nested, ["a", "d", "e", "z"], ["a->d", "d->e", "a->z"], "retains unaffected subgraph products");

        var escaped = Document("digraph { \"a\" + /* join */ \"b\":p -> \"a\\\\path\":q; ab [label=\"ab\"]; \"a\\\\path\" -> 日本語; 日本語 -> -1.5; }");
        Check(escaped.RemoveDotObject(Open(escaped, "node", "ab")), "deletes canonical identities across concatenated and unquoted spellings");
        Graph(escaped, ["a\\\\path", "日本語", "-1.5"], ["a\\\\path->日本語", "日本語->-1.5"], "preserves escaped Unicode and numeric identities");
        Check(escaped.Source.Contains("/* join */", StringComparison.Ordinal), "retains comments within concatenated identifiers");

        var strict = Document("strict digraph { a:p -> b:q [color=red]; a -> b [label=Merged]; b -> c [color=blue]; b -> c [label=Keep]; }");
        Check(strict.RemoveDotObject(Open(strict, "node", "a")), "deletes all merged strict-edge declarations incident to a node");
        Graph(strict, ["b", "c"], ["b->c"], "retains unrelated strict merged edges");
        Check(Render(strict).Value.Contains("Keep", StringComparison.Ordinal), "retains strict-edge final attributes");

        var clusterDelete = Document("digraph { node[color=blue]; subgraph cluster_outer { label=Outer; a; subgraph cluster_inner { b; c } } outside -> a; b -> outside; outside -> keep; }");
        Check(clusterDelete.RemoveDotObject(Open(clusterDelete, "cluster", "cluster_outer")), "deletes a cluster and its nested node identities");
        Graph(clusterDelete, ["outside", "keep"], ["outside->keep"], "cluster deletion removes incident edges and preserves outside nodes");
        Check(!Render(clusterDelete).Descendants().Any(element => (string?)element.Attribute("data-dot-kind") == "cluster"), "cluster deletion removes nested boundaries");
        var endpointCluster = Document("digraph { a -> subgraph cluster_group { b; c } -> d; e; }");
        Check(endpointCluster.RemoveDotObject(Open(endpointCluster, "cluster", "cluster_group")), "deletes a cluster used as an edge endpoint");
        Graph(endpointCluster, ["a", "d", "e"], [], "empty cluster endpoints retain isolated outside nodes");

        var duplicated = Document("digraph { node[shape=box,color=red]; a; node[color=blue]; b; a[fontcolor=green]; a:out -> b:in [label=Calls,color=purple]; a -> outside; aCopy; }");
        var original = duplicated.Source;
        Check(duplicated.DuplicateDotObjects([Open(duplicated, "node", "a"), Open(duplicated, "node", "b")]), "duplicates nodes and their internal edge");
        Graph(duplicated, ["a", "b", "outside", "aCopy", "aCopy2", "bCopy"], ["a->b", "a->outside", "aCopy2->bCopy"], "uses collision-free identities and excludes external relationships");
        var duplicatesSvg = Render(duplicated);
        Check(Node(duplicatesSvg, "aCopy2").Descendants().Any(element => (string?)element.Attribute("stroke") == "#ff0000") &&
            Node(duplicatesSvg, "bCopy").Descendants().Any(element => (string?)element.Attribute("stroke") == "#0000ff") &&
            Node(duplicatesSvg, "aCopy2").Descendants().Any(element => (string?)element.Attribute("fill") == "#00ff00"), "materializes final node attributes in the original creation scopes");
        Check(duplicated.Source.Contains("aCopy2:out -> bCopy:in", StringComparison.Ordinal), "duplicate relationships retain ports");
        Check(duplicated.Undo() && duplicated.Source == original && duplicated.Redo(), "duplication is one source transaction");

        var shared = Document("digraph { node[color=blue]; a,b [label=Same]; node[label=Later,color=red]; a[label=Updated]; a->a [label=Self]; }");
        Check(shared.DuplicateDotObject(Open(shared, "node", "a")), "duplicates shared declarations without changing their other nodes");
        Graph(shared, ["a", "b", "aCopy"], ["a->a", "aCopy->aCopy"], "duplicates self-loops once");
        Check(Node(Render(shared), "aCopy").Value.Contains("Updated", StringComparison.Ordinal) && Node(Render(shared), "b").Value.Contains("Same", StringComparison.Ordinal), "retains shared and later label overrides");
        var quoted = Document("digraph { \"one,two\" -> \"a\\\\path\"; }");
        Check(quoted.DuplicateDotObjects([Open(quoted, "node", "one,two"), Open(quoted, "node", "a\\\\path")]), "duplicates quoted and escaped identities");
        Graph(quoted, ["one,two", "a\\\\path", "one,twoCopy", "a\\\\pathCopy"], ["one,two->a\\\\path", "one,twoCopy->a\\\\pathCopy"], "duplicate identifier escaping retains canonical names");

        var unicodeWhitespace = Document("digraph { \"\u00a0alpha\" -> \"\u2003beta\" [label=Keep]; }");
        string whitespaceSource = unicodeWhitespace.Source;
        var whitespaceNodes = new[] { Open(unicodeWhitespace, "node", "\u00a0alpha"), Open(unicodeWhitespace, "node", "\u2003beta") };
        Check(unicodeWhitespace.DuplicateDotObjects(whitespaceNodes), "duplicates quoted identifiers beginning with Unicode whitespace");
        Graph(unicodeWhitespace, ["\u00a0alpha", "\u2003beta", "\u00a0alphaCopy", "\u2003betaCopy"],
            ["\u00a0alpha->\u2003beta", "\u00a0alphaCopy->\u2003betaCopy"], "duplicate identifiers retain their exact Unicode identities");
        Check(unicodeWhitespace.Undo() && unicodeWhitespace.Source == whitespaceSource, "Unicode duplication undoes without normalizing the source");
        string whitespaceCopy = unicodeWhitespace.CopyDotSelection([Open(unicodeWhitespace, "node", "\u00a0alpha"), Open(unicodeWhitespace, "node", "\u2003beta")])!;
        Graph(new(whitespaceCopy), ["\u00a0alpha", "\u2003beta"], ["\u00a0alpha->\u2003beta"], "copied Unicode identifiers remain valid standalone DOT");
        Check(unicodeWhitespace.PasteDotSelection(whitespaceCopy), "pastes Unicode identities with collisions");
        Graph(unicodeWhitespace, ["\u00a0alpha", "\u2003beta", "\u00a0alpha2", "\u2003beta2"],
            ["\u00a0alpha->\u2003beta", "\u00a0alpha2->\u2003beta2"], "paste remaps Unicode identities and their endpoints together");

        var scoped = Document("digraph { subgraph cluster_a { node[color=blue]; a; } node[color=red]; subgraph cluster_a { node[color=green]; a[shape=box]; b; } a->b; }");
        Check(scoped.DuplicateDotObject(Open(scoped, "node", "a")), "duplicates nodes with reopened subgraph declarations");
        Check(Node(Render(scoped), "aCopy").Descendants().Any(element => (string?)element.Attribute("stroke") == "#0000ff"), "reopened defaults do not change duplicate creation attributes");
        var compound = Document("digraph { subgraph cluster_a { a } -> subgraph cluster_b { b } [label=Flow]; }");
        Check(!Open(compound, "node", "a").CanDuplicate && !compound.DuplicateDotObjects([Open(compound, "node", "a"), Open(compound, "node", "b")]) && !compound.CanUndo,
            "rejects duplication that would change an existing subgraph edge product atomically");
        Graph(compound, ["a", "b"], ["a->b"], "rejected subgraph-product duplication retains every original connection");

        var group = Document("digraph { node[color=blue]; a [label=A]; node[color=red]; b [label=B]; a->b [label=Calls]; c; }");
        string beforeGroup = group.Source;
        var groupItems = new[] { Open(group, "node", "a"), Open(group, "node", "b") };
        Check(group.CanGroupDotObjects(groupItems) && group.GroupDotObjects(groupItems, "A \"group\"\nSecond line"), "groups selected nodes with escaped multiline labels");
        Graph(group, ["a", "b", "c"], ["a->b"], "grouping preserves identities and relationships");
        var grouped = Render(group);
        Check(grouped.Descendants().Count(element => (string?)element.Attribute("data-dot-kind") == "cluster") == 1 &&
            Node(grouped, "a").Descendants().Any(element => (string?)element.Attribute("stroke") == "#0000ff"), "grouping retains original node defaults");
        var cluster = Open(group, "cluster", "cluster_Group");
        Check(cluster.CanUngroup && group.UngroupDotObject(cluster), "ungroups a cluster through its native metadata");
        Check(!Render(group).Descendants().Any(element => (string?)element.Attribute("data-dot-kind") == "cluster"), "ungrouping removes the boundary");
        Check(group.Source.Contains("node[color=blue]; a [label=A]; node[color=red]; b [label=B]; a->b [label=Calls]; c;", StringComparison.Ordinal), "group and ungroup leave original declarations in place");
        Check(group.Undo() && group.Undo() && group.Source == beforeGroup, "grouping and ungrouping each have independent undo transactions");

        var ranks = Document("digraph { {rank=same; a;b} c; }");
        string beforeRank = ranks.Source;
        Check(!ranks.CanGroupDotObjects([Open(ranks, "node", "a")]) && !ranks.GroupDotObjects([Open(ranks, "node", "a")], "Subset") && ranks.Source == beforeRank && !ranks.CanUndo,
            "rejects a grouping that splits an existing rank constraint atomically");
        Check(ranks.GroupDotObjects([Open(ranks, "node", "a"), Open(ranks, "node", "b")], "Ranked"), "groups every member of an existing rank constraint");
        Render(ranks);
        var siblings = Document("digraph { subgraph cluster_a { a; } subgraph cluster_b { b; } }");
        Check(!siblings.GroupDotObjects([Open(siblings, "node", "a"), Open(siblings, "node", "b")], "Cross") && !siblings.CanUndo, "rejects overlapping sibling cluster memberships");

        var ungroup = Document("digraph { compound=true; node[color=blue]; subgraph cluster_a { node[color=red]; label=Local; a; subgraph cluster_child { b; } } outside; subgraph cluster_a { c; } a->outside[ltail=\"cluster_a\"]; }");
        Check(ungroup.UngroupDotObject(Open(ungroup, "cluster", "cluster_a")), "ungroups all occurrences of a reopened cluster");
        var ungrouped = Render(ungroup);
        Check(ungrouped.Descendants().Count(element => (string?)element.Attribute("data-dot-kind") == "cluster") == 1 &&
            Node(ungrouped, "a").Descendants().Any(element => (string?)element.Attribute("stroke") == "#ff0000") &&
            Node(ungrouped, "outside").Descendants().Any(element => (string?)element.Attribute("stroke") == "#0000ff"), "ungrouping retains child clusters and local default isolation");
        Check(ungroup.Source.Contains("ltail=\"\"", StringComparison.Ordinal) && ungroup.Source.Split("subgraph group_cluster_a", StringSplitOptions.None).Length == 3,
            "ungrouping renames every scope occurrence and clears obsolete compound endpoints");
        var anonymous = Document("digraph { subgraph { cluster=true; a; b; } c; }");
        var unnamedCluster = Render(anonymous).Descendants().Single(element => (string?)element.Attribute("data-dot-kind") == "cluster");
        Check(anonymous.UngroupDotObject(Open(anonymous, unnamedCluster)) && !Render(anonymous).Descendants().Any(element => (string?)element.Attribute("data-dot-kind") == "cluster"),
            "ungroups anonymous attribute-enabled clusters");

        var emptyNames = Document("digraph { subgraph \"\" { cluster=true; a; } subgraph \"\" { cluster=true; b; } }");
        string emptySource = emptyNames.Source;
        var emptyFirst = Open(emptyNames, "cluster", "_anonymous_dot_0");
        Check(emptyFirst.CanUngroup && emptyNames.UngroupDotObject(emptyFirst), "explicitly empty subgraph names remain independent anonymous scopes");
        Check(Render(emptyNames).Descendants().Where(element => (string?)element.Attribute("data-dot-kind") == "cluster")
            .Select(element => (string?)element.Attribute("data-qualified-name")).SequenceEqual(new[] { "_anonymous_dot_1" }),
            "ungrouping one empty-named scope retains its sibling and stable anonymous identity");
        Check(emptyNames.Source.Split("subgraph \"\"", StringSplitOptions.None).Length == 3, "ungrouping retains empty source names");
        Check(emptyNames.RemoveDotObject(Open(emptyNames, "cluster", "_anonymous_dot_1")), "deletes another empty-named cluster independently");
        Graph(emptyNames, ["a"], [], "empty-named cluster deletion retains the ungrouped sibling");
        Check(emptyNames.Undo() && emptyNames.Undo() && emptyNames.Source == emptySource, "anonymous scope edits each undo in one transaction");

        var commentNames = Document("digraph { compound=true; subgraph \"cluster_\" + /* scope comment */ \"a\" { a; } b; a->b[ltail=\"cluster_\"+/* endpoint comment */\"a\"]; }");
        string commentsSource = commentNames.Source;
        Check(commentNames.UngroupDotObject(Open(commentNames, "cluster", "cluster_a")), "ungroups a concatenated cluster identity");
        Graph(commentNames, ["a", "b"], ["a->b"], "ungrouping concatenated identifiers retains graph relationships");
        Check(commentNames.Source.Contains("/* scope comment */", StringComparison.Ordinal) && commentNames.Source.Contains("/* endpoint comment */", StringComparison.Ordinal),
            "ungrouping retains comments between concatenated name and compound-endpoint tokens");
        Check(commentNames.Undo() && commentNames.Source == commentsSource, "concatenated cluster ungrouping undoes byte for byte");
        Check(commentNames.RemoveDotObject(Open(commentNames, "cluster", "cluster_a")), "deletes a concatenated cluster identity");
        Graph(commentNames, ["b"], [], "cluster deletion retains outside identities");
        Check(commentNames.Source.Contains("/* scope comment */", StringComparison.Ordinal) && commentNames.Source.Contains("/* endpoint comment */", StringComparison.Ordinal),
            "cluster deletion preserves comments when clearing compound endpoint references");

        var clipboard = Document("digraph { node[color=blue]; a[label=A]; b[label=B]; a:p->b:q[label=Inside]; a->outside; }");
        string fragment = clipboard.CopyDotSelection([Open(clipboard, "node", "a"), Open(clipboard, "node", "b")])!;
        Check(fragment.StartsWith("@startdot\ndigraph", StringComparison.Ordinal), "copies a standalone DOT document");
        Graph(new(fragment), ["a", "b"], ["a->b"], "copied nodes include only internal edges");
        Check(clipboard.PasteDotSelection(fragment), "pastes copied nodes through the public source-edit API");
        Graph(clipboard, ["a", "b", "outside", "a2", "b2"], ["a->b", "a->outside", "a2->b2"], "paste remaps colliding identities and endpoints together");
        Check(clipboard.Source.Contains("a2:p -> b2:q", StringComparison.Ordinal), "paste retains endpoint ports");
        var defaultsTarget = Document("digraph { node[color=red,label=Default]; target; }");
        string plain = "@startdot\ndigraph { pasted; }\n@enddot";
        Check(defaultsTarget.PasteDotSelection(plain) && Node(Render(defaultsTarget), "pasted").Descendants().Where(element => element.Name.LocalName == "text").Any(element => element.Value == "pasted"),
            "pasted nodes retain built-in defaults without changing target defaults");
        Check(Node(Render(defaultsTarget), "target").Value.Contains("Default", StringComparison.Ordinal), "paste retains existing target styles");
        var compoundCopy = Document("digraph { compound=true; subgraph cluster_from { a; } subgraph cluster_to { b; } a->b[ltail=cluster_from,lhead=cluster_to]; }");
        string compoundFragment = compoundCopy.CopyDotSelection([Open(compoundCopy, "node", "a"), Open(compoundCopy, "node", "b")])!;
        Check(!compoundFragment.Contains("ltail", StringComparison.Ordinal) && !compoundFragment.Contains("lhead", StringComparison.Ordinal),
            "copied node fragments omit cluster-boundary references outside the fragment");
        Graph(new(compoundFragment), ["a", "b"], ["a->b"], "flattened compound copies retain their actual endpoints");
        var compoundTarget = Document("digraph { compound=true; subgraph cluster_from { outside; } }");
        Check(compoundTarget.PasteDotSelection(compoundCopy.Source) && !compoundTarget.Source.Contains("lhead", StringComparison.Ordinal) && !compoundTarget.Source.Contains("ltail", StringComparison.Ordinal),
            "pasted nodes cannot attach compound edges to unrelated target clusters");
        Graph(compoundTarget, ["outside", "a", "b"], ["a->b"], "compound paste retains endpoint identities");
        string unchanged = defaultsTarget.Source;
        Check(!defaultsTarget.PasteDotSelection("@startdot\ngraph { a -- b }\n@enddot") && !defaultsTarget.PasteDotSelection("@startdot\ndigraph { a -> }\n@enddot") && defaultsTarget.Source == unchanged,
            "rejects incompatible or malformed pasted graphs atomically");

        var pages = new StudioDocument("@startuml\nclass Before\n@enduml\r\n@startdot second\r\ndigraph { a->b; }\r\n@enddot\r\n@startuml\nclass After\n@enduml");
        pages.ActivePage = 1;
        string pageSource = pages.Source;
        Check(pages.DuplicateDotObject(Open(pages, "node", "a")) && pages.Source.StartsWith("@startuml\nclass Before\n@enduml\r\n", StringComparison.Ordinal) &&
            pages.Source.EndsWith("@startuml\nclass After\n@enduml", StringComparison.Ordinal), "DOT structural edits remain inside the active named block");
        Check(pages.Undo() && pages.Source == pageSource && pages.ActivePage == 1, "undo preserves active named page and mixed line endings");

        var sharedLabel = Document("digraph { a,b [label=Shared]; a[color=red]; }");
        string sharedLabelSource = sharedLabel.Source;
        Check(sharedLabel.RenameDotObject(Open(sharedLabel, "node", "a"), "Only A"), "renames a shared label after a later singleton declaration");
        Check(Node(Render(sharedLabel), "a").Value.Contains("Only A", StringComparison.Ordinal) && Node(Render(sharedLabel), "b").Value.Contains("Shared", StringComparison.Ordinal),
            "a singleton override cannot mutate another node's earlier shared label");
        Check(sharedLabel.Undo() && sharedLabel.Source == sharedLabelSource, "shared label override undoes in one transaction");

        var sharedEdgeLabel = Document("strict digraph { a->b->c[label=Shared]; a->b[color=red]; }");
        string sharedEdgeSource = sharedEdgeLabel.Source;
        Check(sharedEdgeLabel.RenameDotObject(Open(sharedEdgeLabel, "edge", "a->b"), "Only AB"), "renames a strict edge with a shared earlier chain label");
        Check(Render(sharedEdgeLabel).Descendants().Single(element => (string?)element.Attribute("data-qualified-name") == "a->b").Value.Contains("Only AB", StringComparison.Ordinal) &&
            Render(sharedEdgeLabel).Descendants().Single(element => (string?)element.Attribute("data-qualified-name") == "b->c").Value.Contains("Shared", StringComparison.Ordinal),
            "strict singleton label overrides leave other chain segments unchanged");
        Check(sharedEdgeLabel.Undo() && sharedEdgeLabel.Source == sharedEdgeSource, "strict shared-label override undoes byte for byte");

        var labelComments = Document("digraph { a[label=\"First\" + /* caption: retain */ \"Second\"]; }");
        string labelCommentSource = labelComments.Source;
        Check(labelComments.RenameDotObject(Open(labelComments, "node", "a"), "Changed") &&
            labelComments.Source.Contains("/* caption: retain */", StringComparison.Ordinal) && Node(Render(labelComments), "a").Value.Contains("Changed", StringComparison.Ordinal),
            "renaming a concatenated label preserves comments within its token span");
        Check(labelComments.Undo() && labelComments.Source == labelCommentSource, "comment-preserving label edit is one reversible source transaction");

        var htmlEndpoint = Document("digraph { c; <a:b>:w -> b[label=Flow]; }");
        string htmlEndpointSource = htmlEndpoint.Source;
        Check(htmlEndpoint.RetargetDot(Open(htmlEndpoint, "edge", "a:b->b"), Open(htmlEndpoint, "node", "c"), true), "retargets HTML identifiers containing a colon");
        Graph(htmlEndpoint, ["a:b", "b", "c"], ["c->b"], "retargeting retains old implicit nodes and ignores non-port colons");
        Check(htmlEndpoint.Source.Contains("c:w -> b", StringComparison.Ordinal), "HTML identifier retargeting retains the actual port suffix");
        Check(htmlEndpoint.Undo() && htmlEndpoint.Source == htmlEndpointSource, "HTML endpoint retargeting undoes byte for byte");

        var commentEndpoint = Document("digraph { c; \"a\" + /* name: retain */ \"b\":/* port: retain */\"input:name\":e -> other[label=Flow]; }");
        string commentEndpointSource = commentEndpoint.Source;
        Check(commentEndpoint.RetargetDot(Open(commentEndpoint, "edge", "ab->other"), Open(commentEndpoint, "node", "c"), true), "retargets concatenated identifiers with colon-bearing comments");
        Graph(commentEndpoint, ["ab", "c", "other"], ["c->other"], "comment-aware retargeting preserves all node identities");
        Check(commentEndpoint.Source.Contains("/* name: retain */", StringComparison.Ordinal) &&
            commentEndpoint.Source.Contains(":/* port: retain */\"input:name\":e", StringComparison.Ordinal), "retargeting retains identifier comments and exact port tokens");
        Check(commentEndpoint.Undo() && commentEndpoint.Source == commentEndpointSource, "comment-aware retargeting undoes exactly");

        var forwardTarget = Document("digraph { node[color=red]; a:p->b:q[color=blue,label=Flow]; node[color=green,shape=box]; c; }");
        string forwardSource = forwardTarget.Source;
        Check(forwardTarget.RetargetDot(Open(forwardTarget, "edge", "a->b"), Open(forwardTarget, "node", "c"), true), "retargets an edge to a node declared under later defaults");
        Graph(forwardTarget, ["a", "b", "c"], ["c->b"], "retargeting preserves original endpoint identities");
        var forwardSvg = Render(forwardTarget);
        Check(Node(forwardSvg, "a").Descendants().Any(element => (string?)element.Attribute("stroke") == "#ff0000") &&
            Node(forwardSvg, "b").Descendants().Any(element => (string?)element.Attribute("stroke") == "#ff0000") &&
            Node(forwardSvg, "c").Descendants().Any(element => (string?)element.Attribute("stroke") == "#00ff00"),
            "forward retargeting preserves the old endpoints' defaults and the target's later defaults");
        Check(forwardTarget.Source.Contains("c:p->b:q[color=blue,label=Flow]", StringComparison.Ordinal) &&
            forwardTarget.Undo() && forwardTarget.Source == forwardSource, "retargeting retains ports and edge attributes with exact undo");

        var strictRetarget = Document("strict digraph { a->b[label=First]; a->c[label=Second]; }");
        string strictRetargetSource = strictRetarget.Source;
        Check(!strictRetarget.RetargetDot(Open(strictRetarget, "edge", "a->b"), Open(strictRetarget, "node", "c"), false) &&
            strictRetarget.Source == strictRetargetSource && !strictRetarget.CanUndo, "rejects a retarget that would merge distinct strict edges and discard their independent attributes");
        Check(!strictRetarget.ConnectDot(Open(strictRetarget, "node", "a"), Open(strictRetarget, "node", "b"), "Overwrite") &&
            strictRetarget.Source == strictRetargetSource && !strictRetarget.CanUndo, "connecting a strict duplicate cannot overwrite the existing relationship's label");
        Check(strictRetarget.ConnectDot(Open(strictRetarget, "node", "b"), Open(strictRetarget, "node", "a"), "Reverse"), "strict directed graphs allow a distinct reverse connection");
        Graph(strictRetarget, ["a", "b", "c"], ["a->b", "a->c", "b->a"], "strict connection insertion preserves existing edge identities");
        Check(strictRetarget.Undo() && strictRetarget.Source == strictRetargetSource, "a new strict connection remains one reversible edit");

        var strictUndirected = Document("strict graph { a--b[label=Keep]; c; }");
        string strictUndirectedSource = strictUndirected.Source;
        Check(!strictUndirected.ConnectDot(Open(strictUndirected, "node", "a"), Open(strictUndirected, "node", "b"), "Changed") &&
            !strictUndirected.ConnectDot(Open(strictUndirected, "node", "b"), Open(strictUndirected, "node", "a"), "Reversed") &&
            strictUndirected.Source == strictUndirectedSource && !strictUndirected.CanUndo,
            "strict undirected duplicates are rejected in either endpoint order without mutating history");
        Check(strictUndirected.ConnectDot(Open(strictUndirected, "node", "b"), Open(strictUndirected, "node", "c"), "New") &&
            strictUndirected.Source.Contains("b -- c [label=\"New\"]", StringComparison.Ordinal), "new strict undirected connections retain the graph's operator");

        var explicitRetarget = Document("digraph { a; b; c; a->b[label=Keep]; }");
        string explicitRetargetSource = explicitRetarget.Source;
        Check(explicitRetarget.RetargetDot(Open(explicitRetarget, "edge", "a->b"), Open(explicitRetarget, "node", "c"), false) &&
            explicitRetarget.Source == explicitRetargetSource.Replace("a->b", "a->c", StringComparison.Ordinal),
            "retargeting already-declared nodes changes only the requested identifier token");

        var clusterRetarget = Document("digraph { subgraph cluster_first { a->b; } subgraph cluster_second { c; } }");
        string clusterRetargetSource = clusterRetarget.Source;
        Check(!clusterRetarget.RetargetDot(Open(clusterRetarget, "edge", "a->b"), Open(clusterRetarget, "node", "c"), false) &&
            clusterRetarget.Source == clusterRetargetSource && !clusterRetarget.CanUndo, "rejects an edge edit that would silently add its target to another cluster");

        var unsafeDefaults = Document("digraph { subgraph cluster_first { node[color=red]; a->b; } c; subgraph cluster_first { c; } }");
        string unsafeDefaultsSource = unsafeDefaults.Source;
        Check(!unsafeDefaults.RetargetDot(Open(unsafeDefaults, "edge", "a->b"), Open(unsafeDefaults, "node", "c"), false) &&
            unsafeDefaults.Source == unsafeDefaultsSource && !unsafeDefaults.CanUndo, "rejects an unrepresentable inherited-default change without changing history");

        foreach (string ending in new[] { "\n", "\r\n", "\r" })
        {
            string connectionSource = ("\uFEFF@startdot\ndigraph { node[color=blue]; a/* left */:p -> /* arrow */b:q [label=/* label */\"Flow\",color=red];\n" +
                "node[color=green]; b[shape=box]; b->c[color=purple]; }\n@enddot").ReplaceLineEndings(ending);
            var connection = new StudioDocument(connectionSource);
            Check(connection.RemoveDotEdge(Open(connection, "edge", "a->b")), "deletes a connector while retaining implicit nodes " + ending.Length);
            Graph(connection, ["a", "b", "c"], ["b->c"], "connector deletion affects no other nodes or edges");
            var connectionSvg = Render(connection);
            Check(Node(connectionSvg, "a").Descendants().Any(element => (string?)element.Attribute("stroke") == "#0000ff") &&
                Node(connectionSvg, "b").Descendants().Any(element => (string?)element.Attribute("stroke") == "#0000ff") &&
                connection.Source.Contains("/* left */", StringComparison.Ordinal) && connection.Source.Contains("/* arrow */", StringComparison.Ordinal) &&
                connection.Source.Contains("/* label */", StringComparison.Ordinal), "connector deletion retains creation defaults and all interleaved comments");
            Check(connection.Undo() && connection.Source == connectionSource && connection.Redo() && connection.Source.StartsWith('\uFEFF'),
                "connector deletion preserves BOM and line endings across undo and redo");
        }

        var selfLoop = Document("digraph { a->a[label=Loop]; }");
        Check(selfLoop.RemoveDotEdge(Open(selfLoop, "edge", "a->a")), "deletes a self-loop without deleting its implicit endpoint");
        Graph(selfLoop, ["a"], [], "self-loop deletion retains exactly one node");
        var parallelEdges = Document("digraph { a->b[label=First]; a->b[label=Second,color=blue]; }");
        Check(parallelEdges.RemoveDotEdge(Open(parallelEdges, Render(parallelEdges).Descendants().Single(element =>
            (string?)element.Attribute("data-dot-kind") == "edge" && (string?)element.Attribute("data-dot-label-text") == "First"))),
            "deletes only the selected non-strict parallel edge");
        Graph(parallelEdges, ["a", "b"], ["a->b"], "parallel-edge deletion retains endpoint identities and the other edge");
        Check(Render(parallelEdges).Descendants().Single(element => (string?)element.Attribute("data-dot-kind") == "edge").Value.Contains("Second", StringComparison.Ordinal),
            "parallel-edge deletion retains the other relationship's attributes");

        checks += DotSharedCases.Run();
        checks += DotLabelCases.Run();
        Console.WriteLine($"DOT structural editing passed {checks} integration checks.");
        return checks;
    }

    private static StudioDocument Document(string body) => new("@startdot\n" + body + "\n@enddot");
    internal static XElement Render(StudioDocument document)
    {
        if (!PlantUmlRenderer.TryRender(document.Page.Source, out var result) || result.Kind != DiagramKind.Dot)
            throw new Exception("Native DOT declined structural edit result\n" + document.Page.Source);
        return XElement.Parse(result.Svg);
    }
    private static IEnumerable<string> NodeNames(XElement svg) => svg.Descendants().Where(element => (string?)element.Attribute("data-dot-kind") == "node")
        .Select(element => (string)element.Attribute("data-qualified-name")!).Order(StringComparer.Ordinal);
    private static IEnumerable<string> EdgeNames(XElement svg) => svg.Descendants().Where(element => (string?)element.Attribute("data-dot-kind") == "edge")
        .Select(element => (string?)element.Attribute("data-dot-from-id") + "->" + (string?)element.Attribute("data-dot-to-id")).Order(StringComparer.Ordinal);
    private static XElement Node(XElement svg, string name) => svg.Descendants().Single(element => (string?)element.Attribute("data-dot-kind") == "node" &&
        (string?)element.Attribute("data-qualified-name") == name);
    internal static DotObjectEdit Open(StudioDocument document, string kind, string name) => Open(document, Render(document).Descendants().Single(element =>
        (string?)element.Attribute("data-dot-kind") == kind && (string?)element.Attribute("data-qualified-name") == name));
    internal static DotObjectEdit Open(StudioDocument document, XElement element)
    {
        string Text(string key) => (string?)element.Attribute("data-dot-" + key) ?? "";
        int Number(string key) => int.TryParse(Text(key), out int result) ? result : -1;
        var location = new DotSourceLocation
        {
            Id = Text("id"), Kind = Text("kind"), Name = (string?)element.Attribute("data-qualified-name") ?? "", Text = Text("label-text"), Format = Text("label-format"),
            Start = Number("start"), Length = Number("length"), NameStart = Number("name-start"), NameLength = Number("name-length"),
            LabelStart = Number("label-start"), LabelLength = Number("label-length"), LabelInsert = Number("label-insert"), LabelKind = Text("label-kind"),
            Explicit = Text("explicit") == "true", Directed = (string?)element.Document?.Root?.Attribute("data-dot-directed") == "true" || (string?)element.Ancestors().Last().Attribute("data-dot-directed") == "true",
            Merged = Text("merged") == "true", FromStart = Number("from-start"), FromLength = Number("from-length"), FromId = Text("from-id"),
            ToStart = Number("to-start"), ToLength = Number("to-length"), ToId = Text("to-id"), ArrowStart = Number("arrow-start"), ArrowLength = Number("arrow-length")
        };
        return document.OpenDotObject(location) ?? throw new Exception("Native DOT metadata did not open\n" + element);
    }
}
