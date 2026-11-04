using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private JsonFieldEdit? selectedJsonField;
    private SourceFieldEdit? selectedSourceField;
    private sealed record ImportedLabel(ImportedSource Origin, StudioDocument Document, DiagramObject Item);
    private abstract record CanvasLabelEdit;
    private sealed record ObjectLabelEdit(DiagramObject Item) : CanvasLabelEdit;
    private sealed record JsonLabelEdit(JsonFieldEdit Field) : CanvasLabelEdit;
    private sealed record SourceLabelEdit(SourceFieldEdit Field) : CanvasLabelEdit;
    private sealed record DotLabelEdit(DotObjectEdit Item) : CanvasLabelEdit;
    private sealed record ImportedLabelEdit(ImportedLabel Label) : CanvasLabelEdit;
    private CanvasLabelEdit? editingCanvasLabel;
    private int jsonRevision = -1, jsonStart;
    private int fieldRevision = -1, fieldStart;
    private JsonFieldEdit? JsonSelection => jsonRevision == revision ? selectedJsonField : null;
    private SourceFieldEdit? FieldSelection => fieldRevision == revision ? selectedSourceField : null;

    private void SelectJsonField(JsonHit? token)
    {
        selectedJsonField = token == null ? null : doc.OpenJsonField(token.Start, token.Length, token.Kind);
        jsonStart = token?.Start ?? -1;
        jsonRevision = revision;
    }

    private void SelectSourceField(SourceFieldHit? token)
    {
        selectedSourceField = token == null ? null : doc.OpenSourceField(token.Start, token.Length, token.Kind, token.Role);
        fieldStart = token?.Start ?? -1;
        fieldRevision = revision;
    }

    private void BeginCanvasEdit(DiagramObject? item)
    {
        editingCanvasLabel = null;
        if (renderedRevision != revision || resourceHistoryBusy) return;
        if (JsonSelection is { } json && mode != "Source")
        {
            if (viewport.BeginLabelEdit(json.Text, revision, language: json.Kind == "scalar" ? "text" : "creole")) editingCanvasLabel = new JsonLabelEdit(json);
            return;
        }
        if (FieldSelection is { } field && mode != "Source")
        {
            if (field.Kind == "salt-state")
            {
                ToggleSaltState(field);
                return;
            }
            bool plain = field.Kind is "number" or "integer" or "date" or "boolean" or "json-scalar" or "chart-category" or "packet-label" or "timing-binary" or "files-name" or "gantt-label" ||
                field.Kind.StartsWith("dot-", StringComparison.Ordinal) || doc.Page.Family == "Chart";
            if (viewport.BeginLabelEdit(field.Text, revision, field.Multiline, plain ? "text" : "creole")) editingCanvasLabel = new SourceLabelEdit(field);
            return;
        }
        if (DotSelection is { } dot && mode != "Source")
        {
            if (!dot.CanRename) { Status("Double-click a table cell or record field to edit its displayed text."); return; }
            if (viewport.BeginLabelEdit(dot.Text, revision, dot.Text.Contains('\n'))) editingCanvasLabel = new DotLabelEdit(dot);
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
            editingCanvasLabel = new ObjectLabelEdit(item);
        else
            _ = EditStatementAsync(item);
    }

    private void CommitCanvasLabel(int startRevision, string label)
    {
        var edit = editingCanvasLabel;
        editingCanvasLabel = null;
        if (startRevision != revision || renderedRevision != revision) return;
        if (edit is DotLabelEdit { Item: var dot })
        {
            if (dot != DotSelection || dot.Text == label) return;
            Edit(() => doc.RenameDotObject(dot, label), "Enter a valid DOT label.");
            return;
        }
        if (edit is ImportedLabelEdit { Label: var imported })
        {
            if (imported.Item.EditableLabel == label) return;
            _ = CommitImportedLabelAsync(imported, label);
            return;
        }
        if (edit is SourceLabelEdit { Field: var field })
        {
            if (field != FieldSelection || label == field.Text) return;
            Edit(() => doc.ApplySourceField(field, label), "Enter a valid literal value for this field.");
            return;
        }
        if (edit is JsonLabelEdit { Field: var json })
        {
            if (json != JsonSelection || label == json.Text) return;
            Edit(() => doc.ApplyJsonField(json, label), "Enter a valid JSON scalar. String values and keys accept ordinary text.");
            return;
        }
        if (edit is not ObjectLabelEdit { Item: var item } || item != selected || item.EditableLabel == label) return;
        EditProperty(item, () => item.Kind == "Relationship"
            ? doc.UpdateConnection(item, null, null, item.Arrow, label) : doc.Rename(item, label));
    }

    private ImportedLabel? OpenImportedLabel(DiagramHit hit)
    {
        if (hit.SourceFile is not { Length: > 0 } path || hit.FileLine < 0 || BrowserIncludes.Read(path, hit.SourceArchive) is not { } source) return null;
        var origin = new ImportedSource(path, hit.SourceArchive);
        var imported = includeHistory.TryGetValue(origin.Key, out var previous) && previous.Source == source ? previous.Fork() : new StudioDocument(source);
        int page = -1;
        for (int i = 0; i < imported.Pages.Count; i++)
            if (hit.FileLine >= imported.Pages[i].StartLine && hit.FileLine <= imported.Pages[i].EndLine) { page = i; break; }
        if (page < 0) return null;
        imported.ActivePage = page;
        var item = imported.Find(hit.Id, hit.FileLine);
        if (item is not { CanRename: true } || hit.FileLine < item.Line || hit.FileLine > item.EndLine ||
            item.EditableLabel.Contains('$') || item.EditableLabel.Contains('%')) return null;
        if (item.IsConnectable && (hit.Id.Length == 0 || hit.Id != item.Alias && !hit.Id.EndsWith("." + item.Alias, StringComparison.Ordinal))) return null;
        return new(origin, imported, item);
    }

    private bool BeginImportedLabel(DiagramHit hit)
    {
        if (resourceHistoryBusy || renderedRevision != revision) return false;
        var imported = OpenImportedLabel(hit);
        if (imported == null || mode == "Source") return false;
        var item = imported.Item;
        string language = item.EditorLanguage == "text" ? "creole" : item.EditorLanguage;
        if (!viewport.BeginLabelEdit(item.EditableLabel, revision, item.MultilineLabel, language)) return false;
        editingCanvasLabel = new ImportedLabelEdit(imported);
        return true;
    }

    private async Task CommitImportedLabelAsync(ImportedLabel edit, string label)
    {
        if (resourceHistoryBusy) return;
        resourceHistoryBusy = true; SourceEditor.IsReadOnly = true;
        var owner = doc;
        int ownerRevision = revision, ownerRenderedRevision = renderedRevision, ownerPage = doc.ActivePage;
        int ownerDialogGeneration = dialogGeneration;
        var originSelection = GeneratedSelection;
        var initial = edit.Document.Fork();
        try
        {
            bool changed = edit.Item.Kind == "Relationship" ? edit.Document.UpdateConnection(edit.Item, null, null, edit.Item.Arrow, label) : edit.Document.Rename(edit.Item, label);
            if (!changed) { Status("Enter a valid label for this imported object.", true); return; }
            var result = await BrowserIncludes.UpdateAsync(edit.Origin.Path, initial.Source, edit.Document.Source, edit.Origin.Archive);
            if (!result.Updated)
            {
                // Selection, page navigation and dialogs remain available while the resource write is pending.
                // Recover the draft only on the same rendered object that initiated the update.
                string language = edit.Item.EditorLanguage == "text" ? "creole" : edit.Item.EditorLanguage;
                bool recovered = ReferenceEquals(owner, doc) && revision == ownerRevision && renderedRevision == ownerRenderedRevision &&
                    renderedRevision == revision && doc.ActivePage == ownerPage && mode != "Source" &&
                    dialogGeneration == ownerDialogGeneration && activeDialogs == 0 &&
                    originSelection != null && ReferenceEquals(originSelection, GeneratedSelection) && editingCanvasLabel == null &&
                    viewport.BeginLabelEdit(label, ownerRevision, edit.Item.MultilineLabel, language);
                if (recovered)
                    editingCanvasLabel = new ImportedLabelEdit(edit with { Document = initial, Item = initial.Find(edit.Item.Alias, edit.Item.Line)! });
                Status(recovered ? "The imported source changed. Copy your label before reopening its current version." :
                    "The imported source changed. Its current version was kept, and your label was not applied.", true);
                return;
            }
            includeHistory[edit.Origin.Key] = edit.Document;
            if (ReferenceEquals(owner, doc)) RecordResourceEdit(edit.Origin, initial, edit.Document);
            selectedGenerated = null; revision++;
            Refresh(); await RenderPageAsync();
            Status(result.Persisted ? $"Updated {edit.Origin}" : $"Updated {edit.Origin} for this session. Download to retain a copy.");
        }
        catch (Exception error) { Status(error.Message, true); }
        finally { resourceHistoryBusy = false; SourceEditor.IsReadOnly = false; }
    }

    private void SelectCanvasRegion(DiagramHit[] hits, bool add)
    {
        if (renderedRevision != revision) return;
        ClearNetworkCanvasSelection(fromOutline: false);
        if (doc.Page.Family == "DOT") { SelectDotRegion(hits, add); return; }
        selectedGenerated = null; selectedJsonField = null; selectedSourceField = null; selectedDot = null;
        if (add && selected != null) additionalSelection.Add(selected);
        else if (!add) additionalSelection.Clear();
        foreach (var hit in hits)
            if (hit.Token == null && hit.Dot == null && Resolve(hit) is { } item &&
                (hit.Field == null || hit.Field.Kind == "activity-label" && item.Kind == "Activity")) additionalSelection.Add(item);
        selected = additionalSelection.OrderBy(o => o.Line).FirstOrDefault();
        if (selected != null) additionalSelection.Remove(selected);
        ShowInspector();
        SyncCanvasSelection();
    }
}
