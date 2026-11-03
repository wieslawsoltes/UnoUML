using UnoUML.App;
using UnoUML.App.Rendering;
using UnoUML.Core;

internal static class ResourceHistoryCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("Resource history " + message); checks++; }
        MainPage Create()
        {
            BrowserIncludes.Data.Clear(); BrowserIncludes.Data["model.iuml"] = "A";
            return new();
        }
        var normal = Create();
        normal.MainChange("root1"); normal.ResourceChange("B"); normal.MainChange("root2"); normal.ResourceChange("C");
        foreach (var expected in new[] { ("root2", "B"), ("root1", "B"), ("root1", "A"), ("root0", "A") })
        {
            normal.Step(false);
            Check(normal.Content == expected, "undo preserves main/resource chronology");
        }
        foreach (var expected in new[] { ("root1", "A"), ("root1", "B"), ("root2", "B"), ("root2", "C") })
        {
            normal.Step(true);
            Check(normal.Content == expected, "redo preserves main/resource chronology");
        }
        var stale = Create();
        stale.MainChange("root1"); stale.ResourceChange("B"); BrowserIncludes.Data["model.iuml"] = "C";
        stale.Step(false);
        Check(stale.Content == ("root0", "C") && !stale.UndoAvailable, "conflicting resource edit is invalidated and main undo continues");
        stale.Step(true);
        Check(stale.Content == ("root1", "C") && !stale.RedoAvailable, "redo retains the replacement resource");
        var pruned = Create();
        pruned.ResourceChange("B");
        for (int i = 1; i <= 201; i++) pruned.MainChange("root" + i);
        for (int i = 0; i < 202; i++) pruned.Step(false);
        Check(pruned.Content == ("root1", "B") && !pruned.UndoAvailable, "evicted main anchors cannot leave undo enabled");
        var identical = Create();
        identical.MainChange("root1"); identical.ResourceChange("B"); identical.MainChange("root2"); identical.MainChange("root1");
        identical.Step(false);
        Check(identical.Content == ("root2", "B"), "identical text does not reorder resource history");
        identical.Step(false); identical.Step(false);
        Check(identical.Content == ("root1", "A"), "original main state still anchors the resource edit");
        var branch = Create();
        branch.MainChange("root1"); branch.ResourceChange("B"); branch.Step(false); branch.Step(false);
        branch.MainChange("branch");
        Check(!branch.RedoAvailable && branch.Content == ("branch", "A"), "branching drops unreachable resource redo");
        return checks;
    }
}

// Compile the application's actual history partial against a deterministic storage
// boundary. These tests exercise the shipped transaction logic without a browser.
namespace UnoUML.App.Rendering
{
    internal sealed record ImportedSource(string Path, string? Archive = null)
    {
        internal string Key => Archive == null ? Path : Archive + "\0" + Path;
    }
    internal static class BrowserIncludes
    {
        internal static readonly Dictionary<string, string> Data = [];
        internal static Task<(bool Updated, bool Persisted)> UpdateAsync(string path, string expected, string replacement, string? archive = null)
        {
            string key = new ImportedSource(path, archive).Key;
            if (Data.GetValueOrDefault(key) != expected) return Task.FromResult((false, false));
            Data[key] = replacement;
            return Task.FromResult((true, true));
        }
    }
}

namespace UnoUML.App
{
    public sealed partial class MainPage
    {
        private sealed class Editor { public bool IsReadOnly { get; set; } }
        private readonly Editor SourceEditor = new();
        private readonly StudioDocument doc = new("root0");
        private readonly Dictionary<string, StudioDocument> includeHistory = [];
        private object? selectedGenerated;
        private int revision;
        private void Status(string text, bool error = false) { }
        private void Refresh() => PruneResourceHistory();
        private Task RenderPageAsync() => Task.CompletedTask;
        internal (string Main, string Resource) Content => (doc.Source, BrowserIncludes.Data["model.iuml"]);
        internal bool UndoAvailable { get { PruneResourceHistory(); return doc.CanUndo || resourceCursor > 0; } }
        internal bool RedoAvailable { get { PruneResourceHistory(); return doc.CanRedo || resourceCursor < resourceHistory.Count; } }
        internal void MainChange(string value) { doc.SetSource(value); DiscardResourceRedo(); }
        internal void ResourceChange(string value)
        {
            var origin = new ImportedSource("model.iuml");
            var before = new StudioDocument(BrowserIncludes.Data[origin.Key]);
            var after = before.Fork(); after.SetSource(value);
            BrowserIncludes.Data[origin.Key] = value;
            RecordResourceEdit(origin, before, after);
        }
        private void History(bool redo)
        {
            if (TryResourceHistory(redo)) return;
            if (redo) doc.Redo(); else doc.Undo();
            Refresh();
        }
        internal void Step(bool redo)
        {
            History(redo);
            if (resourceHistoryBusy || SourceEditor.IsReadOnly || selectedGenerated != null || revision < 0)
                throw new Exception("Resource history did not finish its synchronous storage transaction");
        }
    }
}
