using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private DiagramObject? editingLabel;
    private JsonFieldEdit? selectedJsonField, editingJsonField;
    private int jsonRevision = -1, jsonStart;
    private JsonFieldEdit? JsonSelection => jsonRevision == revision ? selectedJsonField : null;

    private void SelectJsonField(JsonHit? token)
    {
        selectedJsonField = token == null ? null : doc.OpenJsonField(token.Start, token.Length, token.Kind);
        jsonStart = token?.Start ?? -1;
        jsonRevision = revision;
    }

    private void BeginCanvasEdit(DiagramObject? item)
    {
        editingLabel = null; editingJsonField = null;
        if (renderedRevision != revision) return;
        if (JsonSelection is { } json && mode != "Source")
        {
            if (viewport.BeginLabelEdit(json.Text, revision)) editingJsonField = json;
            return;
        }
        if (item == null) return;
        if (doc.Page.Family == "Ditaa" && item.Kind == "Source" && doc.Drawing is { } drawing)
        {
            item = selected = drawing;
            ShowInspector();
        }
        string language = item.EditorLanguage;
        if (language == "text" && doc.Page.Family is "Class" or "Component" or "Object" or "Deployment" or "Use case" or "State" or "Sequence" or "Mind map" or "WBS" or "C4" or "ArchiMate" or "Activity")
            language = "creole";
        if (mode != "Source" && item.CanRename && viewport.BeginLabelEdit(item.EditableLabel, revision, item.MultilineLabel, language))
            editingLabel = item;
        else
            _ = EditStatementAsync(item);
    }

    private void CommitCanvasLabel(int startRevision, string label)
    {
        if (editingJsonField is { } json)
        {
            editingJsonField = null;
            if (startRevision != revision || renderedRevision != revision || json != JsonSelection || label == json.Text) return;
            Edit(() => doc.ApplyJsonField(json, label), "Enter a valid JSON scalar. String values and keys accept ordinary text.");
            return;
        }
        var item = editingLabel;
        editingLabel = null;
        if (item == null || startRevision != revision || renderedRevision != revision || item != selected || item.EditableLabel == label) return;
        EditProperty(item, () => item.Kind == "Relationship"
            ? doc.UpdateConnection(item, null, null, item.Arrow, label) : doc.Rename(item, label));
    }

    private void SelectCanvasRegion(DiagramHit[] hits, bool add)
    {
        if (renderedRevision != revision) return;
        selectedGenerated = null; selectedJsonField = null;
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
