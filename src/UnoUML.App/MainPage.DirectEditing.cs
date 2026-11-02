using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private DiagramObject? editingLabel;

    private void BeginCanvasEdit(DiagramObject item)
    {
        editingLabel = null;
        if (renderedRevision != revision) return;
        if (mode != "Source" && item.CanRename && viewport.BeginLabelEdit(item.EditableLabel, revision))
            editingLabel = item;
        else
            _ = EditStatementAsync(item);
    }

    private void CommitCanvasLabel(int startRevision, string label)
    {
        var item = editingLabel;
        editingLabel = null;
        if (item == null || startRevision != revision || renderedRevision != revision || item != selected || item.EditableLabel == label) return;
        EditProperty(item, () => item.Kind == "Relationship"
            ? doc.UpdateConnection(item, null, null, item.Arrow, label) : doc.Rename(item, label));
    }

    private void SelectCanvasRegion(DiagramHit[] hits, bool add)
    {
        if (renderedRevision != revision) return;
        if (add && selected != null) additionalSelection.Add(selected);
        else if (!add) additionalSelection.Clear();
        foreach (var hit in hits)
            if (Resolve(hit) is { } item) additionalSelection.Add(item);
        selected = additionalSelection.OrderBy(o => o.Line).FirstOrDefault();
        if (selected != null) additionalSelection.Remove(selected);
        ShowInspector();
        SyncCanvasSelection();
    }
}
