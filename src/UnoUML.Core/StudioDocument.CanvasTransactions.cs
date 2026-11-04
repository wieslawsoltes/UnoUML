namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    /// <summary>
    /// Attach automatic placement to the immediately preceding source edit. The
    /// expected history state is a compare-and-swap guard, not a page or text hash.
    /// Validation happens on an independent fork before the live history changes.
    /// </summary>
    public bool TrySetCanvasLayout(CanvasLayoutState? layout, long mergeWithHistoryState, out string? error)
    {
        if (_cursor == 0 || HistoryState != mergeWithHistoryState)
        {
            error = "The source changed before automatic canvas placement completed.";
            return false;
        }
        var preview = Fork();
        if (!preview.TrySetCanvasLayout(layout, out error)) return false;
        var previous = _history[_cursor - 1];
        string original = string.Concat(Source.AsSpan(0, previous.At), previous.Before, Source.AsSpan(previous.At + previous.After.Length));
        string next = preview.Source;
        int start = 0, originalEnd = original.Length, nextEnd = next.Length;
        while (start < originalEnd && start < nextEnd && original[start] == next[start]) start++;
        while (originalEnd > start && nextEnd > start && original[originalEnd - 1] == next[nextEnd - 1]) { originalEnd--; nextEnd--; }
        var combined = new Edit(start, original[start..originalEnd], next[start..nextEnd], previous.Page);

        // Drop only the edit being amended. Record then applies the same redo
        // invalidation, state numbering, character budget, and history limit as
        // every other source transaction.
        Drop(--_cursor);
        if (combined.Before.Length != 0 || combined.After.Length != 0)
            Record(combined, next, false);
        else
        {
            // A pair of inverse edits has no net effect and no empty undo item.
            for (int i = _history.Count - 1; i >= _cursor; i--) Drop(i);
            Source = next; _index = null; _lastTypingTick = 0;
        }
        error = null;
        return true;
    }
}
