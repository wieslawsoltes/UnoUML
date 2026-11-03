using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private sealed record ResourceEdit(ImportedSource Origin, long MainState, StudioDocument Before, StudioDocument After);
    private readonly List<ResourceEdit> resourceHistory = [];
    private int resourceCursor;
    private bool resourceHistoryBusy;

    private void DiscardResourceRedo()
    {
        if (resourceCursor < resourceHistory.Count) resourceHistory.RemoveRange(resourceCursor, resourceHistory.Count - resourceCursor);
        PruneResourceHistory();
    }

    private void PruneResourceHistory(ImportedSource? conflicted = null)
    {
        for (int i = resourceHistory.Count - 1; i >= 0; i--)
        {
            if (resourceHistory[i].Origin != conflicted && doc.CanReachHistoryState(resourceHistory[i].MainState)) continue;
            resourceHistory.RemoveAt(i);
            if (i < resourceCursor) resourceCursor--;
        }
    }

    private void RecordResourceEdit(ImportedSource origin, StudioDocument before, StudioDocument after)
    {
        DiscardResourceRedo();
        doc.DiscardRedo();
        resourceHistory.Add(new(origin, doc.HistoryState, before.Fork(), after.Fork()));
        resourceCursor++;
        long stored = resourceHistory.Sum(edit => (long)edit.Before.Source.Length + edit.After.Source.Length);
        while (resourceHistory.Count > 1 && (resourceHistory.Count > 200 || stored > 4 * 1024 * 1024))
        {
            stored -= resourceHistory[0].Before.Source.Length + resourceHistory[0].After.Source.Length;
            resourceHistory.RemoveAt(0); resourceCursor--;
        }
    }

    private bool TryResourceHistory(bool redo)
    {
        if (resourceHistoryBusy) return true;
        PruneResourceHistory();
        int next = redo ? resourceCursor : resourceCursor - 1;
        if ((uint)next >= (uint)resourceHistory.Count || resourceHistory[next].MainState != doc.HistoryState) return false;
        _ = ReplayResourceHistoryAsync(resourceHistory[next], redo);
        return true;
    }

    private async Task ReplayResourceHistoryAsync(ResourceEdit edit, bool redo)
    {
        resourceHistoryBusy = true;
        var owner = doc;
        int cursor = resourceCursor;
        bool resume = false;
        SourceEditor.IsReadOnly = true;
        try
        {
            var from = redo ? edit.Before : edit.After;
            var to = redo ? edit.After : edit.Before;
            var result = await BrowserIncludes.UpdateAsync(edit.Origin.Path, from.Source, to.Source, edit.Origin.Archive);
            if (!result.Updated)
            {
                if (ReferenceEquals(owner, doc))
                {
                    PruneResourceHistory(edit.Origin);
                    resume = doc.HistoryState == edit.MainState;
                }
                includeHistory.Remove(edit.Origin.Key);
                Status("The imported source changed. Its earlier edit history was cleared.", true);
                selectedGenerated = null; revision++;
                Refresh(); await RenderPageAsync();
            }
            else
            {
                includeHistory[edit.Origin.Key] = to.Fork();
                if (ReferenceEquals(owner, doc) && resourceCursor == cursor) resourceCursor += redo ? 1 : -1;
                selectedGenerated = null; revision++;
                Refresh(); await RenderPageAsync();
            }
        }
        catch (Exception error) { Status(error.Message, true); }
        finally { resourceHistoryBusy = false; SourceEditor.IsReadOnly = false; }
        if (resume) History(redo);
    }
}
