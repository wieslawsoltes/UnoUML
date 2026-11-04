using System.Globalization;
using System.Xml.Linq;
using UnoUML.Core;
using UnoUML.PlantUml;

internal static class FilesStructureCases
{
    internal static int Run()
    {
        int assertions = 0;
        void Check(bool value, string reason) { if (!value) throw new Exception("Files authoring: " + reason); assertions++; }
        void Atomic(StudioDocument document, string before, string reason)
        {
            string after = document.Source;
            Check(document.Undo() && document.Source == before, reason + " exact undo");
            Check(document.Redo() && document.Source == after, reason + " exact redo");
        }
        XDocument Native(string source)
        {
            Check(PlantUmlRenderer.TryRender(source, out var rendered) && rendered.Kind == DiagramKind.Files, "native Files render");
            return XDocument.Parse(rendered!.Svg);
        }
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string prefix = "\uFEFF@startuml other\nclass Untouched\n@enduml\n";
            string page = ("@startfiles paths\n' independent\n  /src/A.cs  ' keep file comment\n/tests/T.cs\n/src/\n/src/nested/B.cs\n/empty/\n' closing comment\n@endfiles").ReplaceLineEndings(newline);
            string original = prefix + page;
            var document = new StudioDocument(original) { ActivePage = 1 };
            var tree = document.OpenFilesDiagram()!;
            Check(tree.Entries.Count == 7 && tree.Declarations.Count == 5, "explicit paths and inferred directories");
            Check(document.FindFilesEntry("/src/")!.IsExplicit && !document.FindFilesEntry("/tests/")!.IsExplicit, "directory provenance");
            Check(document.FindFilesEntry("/src/nested/")!.ParentPath == "/src/", "parent lookup");
            var svg = Native(page);
            var rows = svg.Descendants().Where(element => element.Attribute("data-files-path") != null).ToArray();
            Check(rows.Length == tree.Entries.Count, "renderer and editor share every semantic row");
            foreach (var entry in tree.Entries)
            {
                var row = rows.Single(element => (string?)element.Attribute("data-files-path") == entry.Path);
                int start = (int)row.Attribute("data-edit-start")!, length = (int)row.Attribute("data-edit-length")!;
                Check((string?)row.Attribute("data-canvas-id") == entry.CanvasId, "shared Files canvas identity");
                Check(page.Substring(start, length) == entry.Name && start + prefix.Length == entry.Offset, "exact UTF-16 name token");
                Check((string?)row.Attribute("data-edit-kind") == "files-name", "direct field kind");
                Check(document.OpenSourceField(start, length, "files-name") is { } field && document.OpenFilesEntry(field)?.Path == entry.Path, "native field resolves its complete path");
            }
            Check(rows.Select(row => (int)row.Attribute("data-edit-start")!).Distinct().Count() == rows.Length, "inferred parents and child name tokens never alias");

            var nested = document.FindFilesEntry("/src/nested/")!;
            var nameField = document.OpenSourceField(nested.Offset - prefix.Length, nested.Length, "files-name")!;
            Check(document.ApplySourceField(nameField, "models") && document.Source == original.Replace("/src/nested/B.cs", "/src/models/B.cs", StringComparison.Ordinal), "direct inferred-folder rename updates the path");
            Check(!document.ApplySourceField(nameField, "stale"), "stale field rejected");
            Atomic(document, original, "inferred directory rename"); document.Undo();
            Check(document.MoveFilesEntry(document.FindFilesEntry("/src/")!, "/lib/") && document.Source == original.Replace("/src/", "/lib/", StringComparison.Ordinal), "directory move preserves comments, indentation, line endings and other page");
            Check(!document.MoveFilesEntry(tree.Entries[0], "/stale/"), "stale diagram snapshot rejected");
            Atomic(document, original, "directory move"); document.Undo();

            tree = document.OpenFilesDiagram()!;
            foreach (string invalid in new[] { "/", "//bad", "/../bad", "/a/./bad", "/bad//path", "relative", "/src/A.cs", "/src", "/$generated", "/%call()", "/a\n!include x" })
                Check(!document.InsertFilesEntry(tree, invalid) && document.Source == original, "invalid or conflicting insertion: " + invalid);
            Check(!document.MoveFilesEntry(document.FindFilesEntry("/src/")!, "/tests/") && !document.MoveFilesEntry(document.FindFilesEntry("/src/")!, "/src/nested/new/"), "moves cannot merge folders or create self-descendants");
            Check(document.InsertFilesEntry(tree, "/tests/") && document.FindFilesEntry("/tests/")!.IsExplicit, "materialize inferred directory without duplicating it");
            Atomic(document, original, "explicit directory insertion"); document.Undo();
            tree = document.OpenFilesDiagram()!;
            Check(document.InsertFilesEntry(tree, "/new/deep/Added.cs", 1), "insert into a chosen declaration slot");
            Check(document.Source.Contains("  /new/deep/Added.cs" + newline + "/tests/T.cs", StringComparison.Ordinal) == false &&
                document.Source.Contains("/new/deep/Added.cs" + newline + "/tests/T.cs", StringComparison.Ordinal), "insert uses destination indentation without copying comments");
            Check(document.FindFilesEntry("/new/deep/") is { IsDirectory: true, IsExplicit: false }, "insertion creates inferred ancestors");
            Atomic(document, original, "file insertion"); document.Undo();

            Check(!document.RemoveFilesEntry(document.FindFilesEntry("/src/")!), "nonempty directory requires explicit recursive removal");
            Check(document.RemoveFilesEntry(document.FindFilesEntry("/src/")!, recursive: true), "recursive folder removal");
            Check(document.Source.Contains("  ' keep file comment", StringComparison.Ordinal) && document.Source.Contains("' independent" + newline, StringComparison.Ordinal) && document.FindFilesEntry("/tests/T.cs") != null && document.FindFilesEntry("/src/") == null, "removal retains comments and unrelated paths");
            Atomic(document, original, "subtree removal"); document.Undo();

            Check(document.DuplicateFilesEntry(document.FindFilesEntry("/src/")!, "/copy/") && document.FindFilesEntry("/copy/nested/B.cs") != null && document.FindFilesEntry("/src/nested/B.cs") != null, "duplicate complete subtree");
            Check(document.Source.Split("' independent").Length == 2, "independent comments are not copied");
            Atomic(document, original, "subtree duplication"); document.Undo();

            string fragment = document.CopyFilesEntry(document.FindFilesEntry("/src/nested/" )!)!;
            Check(fragment == "/nested/B.cs" + newline, "copied nested subtree is rebased to its own root");
            Check(document.PasteFilesEntries(document.OpenFilesDiagram()!, fragment, "/src/") && document.FindFilesEntry("/src/nested copy/B.cs") != null, "paste resolves a directory collision");
            Atomic(document, original, "paste subtree"); document.Undo();
            Check(document.PasteFilesEntries(document.OpenFilesDiagram()!, "/new.txt\n/new/Leaf.txt", "/empty/") && document.FindFilesEntry("/empty/new/Leaf.txt") != null, "paste multiple roots into selected directory");
            Atomic(document, original, "paste multiple paths"); document.Undo();
            Check(!document.PasteFilesEntries(document.OpenFilesDiagram()!, "/src/A.cs\n!include malicious") && document.Source == original, "paste rejects generated or partially understood fragments");

            tree = document.OpenFilesDiagram()!; document.ActivePage = 0;
            Check(!document.InsertFilesEntry(tree, "/wrong-page"), "page identity guard");
        }

        const string ordered = "@startfiles\n/a/one\n' stable separator\n/b/two\n/a/three\n/c\n@endfiles";
        var order = new StudioDocument(ordered);
        var identities = order.OpenFilesDiagram()!.Entries.ToDictionary(entry => entry.Path, entry => entry.CanvasId);
        Check(order.ReorderFilesEntry(order.FindFilesEntry("/a/")!, 2), "reorder an interleaved inferred subtree");
        Check(order.Source == "@startfiles\n/b/two\n' stable separator\n/c\n/a/one\n/a/three\n@endfiles", "reordering preserves trivia slots and groups sibling declarations");
        Check(order.OpenFilesDiagram()!.Entries.All(entry => identities[entry.Path] == entry.CanvasId), "reordering retains every canvas identity");
        Atomic(order, ordered, "reordering");

        const string styled = "\uFEFF@startfiles\r\n<style>\r\nfile { FontSize 14 }\r\n</style>\r\n/💾 data/İstanbul.txt\r\n@endfiles";
        var unicode = new StudioDocument(styled); var unicodeEntry = unicode.FindFilesEntry("/💾 data/İstanbul.txt")!;
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Check(new StudioDocument(styled).FindFilesEntry(unicodeEntry.Path)!.CanvasId == unicodeEntry.CanvasId, "Unicode identities are culture independent");
            var unicodeSvg = Native(styled);
            var field = unicodeSvg.Descendants().Single(element => (string?)element.Attribute("data-files-path") == unicodeEntry.Path);
            Check(styled.Substring((int)field.Attribute("data-edit-start")!, (int)field.Attribute("data-edit-length")!) == "İstanbul.txt", "BOM, styles, surrogate pairs and CRLF retain token offsets");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Check(unicode.RenameFilesEntry(unicodeEntry, "Résumé.txt") && unicode.Source.StartsWith('\uFEFF'), "Unicode rename retains BOM");
        Atomic(unicode, styled, "Unicode path rename");

        var empty = new StudioDocument("@startfiles\n/only\n@endfiles");
        Check(empty.RemoveFilesEntry(empty.FindFilesEntry("/only")!) && empty.OpenFilesDiagram()!.Entries.Count == 0, "remove final row");
        Native(empty.Page.Source);
        Check(empty.InsertFilesEntry(empty.OpenFilesDiagram()!, "/restored"), "insert into empty tree");

        foreach (string invalid in new[] { "/x\n/x", "/x\n/x/y", "/../x", "/x//y", "/x/./y", "!include paths.puml", "<note>\ntext\n</note>\n/x", "/a\nunknown command", "/" + string.Join('/', Enumerable.Repeat("d", 129)) })
            Check(new StudioDocument("@startfiles\n" + invalid + "\n@endfiles").OpenFilesDiagram() == null, "unsupported or ambiguous page rejected: " + invalid);

        var geometry = new StudioDocument("@startfiles\n/src/A.cs\n/src/B.cs\n/docs/\n@endfiles");
        var originalTree = geometry.OpenFilesDiagram()!;
        var canvas = new CanvasLayoutState(originalTree.Entries.Select((entry, index) => new CanvasNode(entry.CanvasId, index * 130 - 20, index * 20 + 7, 120, 28,
            Pinned: index == 1, Rotation: index * 5, ZIndex: index)).ToArray(), []) { Settings = new() { GridSize = 24, Algorithm = CanvasLayoutAlgorithm.Grid } };
        Check(geometry.SetCanvasLayout(canvas), "persist source geometry");
        string withGeometry = geometry.Source;
        var oldEntries = geometry.OpenFilesDiagram()!.Entries.ToDictionary(entry => entry.Path, entry => entry.CanvasId);
        Check(geometry.MoveFilesEntry(geometry.FindFilesEntry("/src/")!, "/lib/"), "move folder with canvas metadata");
        Check(geometry.TryGetCanvasLayout(out var moved, out _) && moved!.Nodes.Count == canvas.Nodes.Count, "canvas node count preserved");
        foreach (var current in geometry.OpenFilesDiagram()!.Entries)
        {
            string oldPath = current.Path.StartsWith("/lib/", StringComparison.Ordinal) ? "/src/" + current.Path[5..] : current.Path;
            var before = canvas.Nodes.Single(node => node.Id == oldEntries[oldPath]);
            var after = moved!.Nodes.Single(node => node.Id == current.CanvasId);
            Check(after == before with { Id = current.CanvasId }, "all authored geometry follows the renamed semantic row");
        }
        Atomic(geometry, withGeometry, "geometry remap"); geometry.Undo();
        Check(geometry.DuplicateFilesEntry(geometry.FindFilesEntry("/src/")!, "/copy/"), "duplicate folder with geometry");
        geometry.TryGetCanvasLayout(out var copied, out _);
        foreach (var entry in geometry.OpenFilesDiagram()!.Entries.Where(entry => entry.Path.StartsWith("/copy/", StringComparison.Ordinal)))
        {
            var before = canvas.Nodes.Single(node => node.Id == oldEntries["/src/" + entry.Path[6..]]);
            var after = copied!.Nodes.Single(node => node.Id == entry.CanvasId);
            Check(after == before with { Id = entry.CanvasId, X = before.X + 48, Y = before.Y + 48 }, "copied rows preserve size, pin, rotation and stacking with predictable offset");
        }
        Check(canvas.Nodes.All(original => copied!.Nodes.Single(node => node.Id == original.Id) == original), "duplicate leaves original geometry intact");
        Atomic(geometry, withGeometry, "geometry duplicate"); geometry.Undo();

        string clipboard = geometry.CopyFilesEntry(geometry.FindFilesEntry("/src/")!)!;
        Check(clipboard.StartsWith(StudioDocument.CanvasSelectionMarker, StringComparison.Ordinal), "clipboard carries mapped geometry");
        Check(geometry.PasteFilesEntries(geometry.OpenFilesDiagram()!, clipboard, "/docs/"), "paste with rebased canvas identities");
        Check(geometry.TryGetCanvasLayout(out var pasted, out _) && pasted!.Nodes.Count == canvas.Nodes.Count + 3, "pasted subtree adds three authored rows");
        foreach (var entry in geometry.OpenFilesDiagram()!.Entries.Where(entry => entry.Path.StartsWith("/docs/src/", StringComparison.Ordinal)))
            Check(pasted!.Nodes.Any(node => node.Id == entry.CanvasId), "pasted metadata maps to the actual destination path");
        Atomic(geometry, withGeometry, "geometry paste"); geometry.Undo();

        var locked = canvas with { Nodes = canvas.Nodes.Select(node => node.Id == oldEntries["/src/A.cs"] ? node with { Locked = true } : node).ToArray() };
        Check(geometry.SetCanvasLayout(locked), "lock a file row");
        string lockedSource = geometry.Source;
        Check(!geometry.RemoveFilesEntry(geometry.FindFilesEntry("/src/")!, true) && geometry.Source == lockedSource, "recursive deletion cannot remove a locked descendant");
        Check(geometry.RenameFilesEntry(geometry.FindFilesEntry("/src/A.cs")!, "Renamed.cs"), "semantic rename preserves a locked visual position");
        geometry.TryGetCanvasLayout(out var renamedLock, out _);
        Check(renamedLock!.Nodes.Single(node => node.Id == geometry.FindFilesEntry("/src/Renamed.cs")!.CanvasId).Locked, "lock follows semantic rename");

        const string malformed = "' @unouml:canvas {\"version\":99,\"nodes\":[],\"edges\":[]}\n@startfiles\n/a\n@endfiles";
        var corrupt = new StudioDocument(malformed);
        Check(!corrupt.RenameFilesEntry(corrupt.FindFilesEntry("/a")!, "b") && corrupt.Source == malformed && !corrupt.CanUndo, "malformed existing geometry is never overwritten");

        var bounded = new StudioDocument("@startfiles\n" + string.Join('\n', Enumerable.Range(0, CanvasLayoutLimits.MaxNodes).Select(index => "/f" + index.ToString(CultureInfo.InvariantCulture))) + "\n@endfiles");
        Check(bounded.SetCanvasLayout(new(bounded.OpenFilesDiagram()!.Entries.Select((entry, index) => new CanvasNode(entry.CanvasId, index, 0, 20, 20)).ToArray(), [])), "maximum valid geometry");
        string full = bounded.Source; long history = bounded.HistoryState;
        Check(!bounded.DuplicateFilesEntry(bounded.FindFilesEntry("/f0")!, "/overflow") && bounded.Source == full && bounded.HistoryState == history, "geometry capacity failure leaves source and history untouched");
        Check(!bounded.PasteFilesEntries(bounded.OpenFilesDiagram()!, bounded.CopyFilesEntry(bounded.FindFilesEntry("/f0")!)!) && bounded.Source == full, "paste capacity failure is atomic");
        return assertions;
    }
}
