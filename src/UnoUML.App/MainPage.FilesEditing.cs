using Microsoft.UI.Xaml.Controls;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private const string FilesFailure = "The source changed, the destination conflicts with another path, or a row is locked. Use absolute paths without . or .. components.";

    private void AddFilesDiagramActions()
    {
        if (doc.OpenFilesDiagram() is not { } files) return;
        Properties.Children.Add(Caption($"{files.Entries.Count(entry => !entry.IsDirectory)} files · {files.Entries.Count(entry => entry.IsDirectory)} folders"));
        Properties.Children.Add(Item("Insert file…", "add", () => _ = InsertFilesEntryAsync(files, false, "/")));
        Properties.Children.Add(Item("Insert folder…", "add", () => _ = InsertFilesEntryAsync(files, true, "/")));
        if (files.Entries.Count == 0) return;
        var entry = StructuredPicker("File or folder", files.Entries.Select(item => item.Path), 0);
        Properties.Children.Add(entry);
        Properties.Children.Add(Item("Rename file or folder…", "edit", () => _ = RenameFilesEntryAsync(files.Entries[entry.SelectedIndex])));
        Properties.Children.Add(Item("Move file or folder…", "move", () => _ = MoveFilesEntryAsync(files.Entries[entry.SelectedIndex], false)));
        Properties.Children.Add(Item("Duplicate file or folder…", "copy", () => _ = MoveFilesEntryAsync(files.Entries[entry.SelectedIndex], true)));
        Properties.Children.Add(Item("Reorder file or folder…", "up", () => _ = ReorderFilesEntryAsync(files.Entries[entry.SelectedIndex])));
        Properties.Children.Add(Item("Remove file or folder", "delete", () => RemoveFilesEntry(files.Entries[entry.SelectedIndex])));
    }

    private void AddFilesFieldActions(SourceFieldEdit field)
    {
        if (doc.OpenFilesEntry(field) is { } entry) AddFilesEntryActions(entry);
    }

    private void AddFilesObjectActions(DiagramObject item)
    {
        if (doc.OpenFilesEntry(item) is { } entry) AddFilesEntryActions(entry);
    }

    private void AddFilesEntryActions(FilesEntryEdit entry)
    {
        Properties.Children.Add(Caption(entry.Path + (entry.IsDirectory && !entry.IsExplicit ? "\nFolder inferred from its contents." : "")));
        Properties.Children.Add(Item("Rename file or folder…", "edit", () => _ = RenameFilesEntryAsync(entry)));
        Properties.Children.Add(Item("Move file or folder…", "move", () => _ = MoveFilesEntryAsync(entry, false)));
        Properties.Children.Add(Item("Duplicate file or folder…", "copy", () => _ = MoveFilesEntryAsync(entry, true)));
        Properties.Children.Add(Item("Reorder file or folder…", "up", () => _ = ReorderFilesEntryAsync(entry)));
        string parent = entry.IsDirectory ? entry.Path : entry.ParentPath;
        Properties.Children.Add(Item("Insert file…", "add", () => _ = InsertFilesEntryAsync(entry.Diagram, false, parent)));
        Properties.Children.Add(Item("Insert folder…", "add", () => _ = InsertFilesEntryAsync(entry.Diagram, true, parent)));
        Properties.Children.Add(Item(entry.IsDirectory ? "Remove folder and contents" : "Remove file", "delete", () => RemoveFilesEntry(entry)));
    }

    private async Task InsertFilesEntryAsync(FilesDiagramEdit files, bool directory, string parent)
    {
        var document = doc;
        var path = StructuredTextInput("Path", parent + (directory ? "new-folder/" : "new-file.txt"));
        var position = StructuredPicker("Insert position", files.Declarations.Select(entry => "Before " + entry.Path).Append("After last path"), files.Declarations.Count);
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(path); content.Children.Add(position);
        content.Children.Add(Caption("Intermediate folders are created automatically. A trailing slash identifies a folder."));
        if (await AskAsync(directory ? "Insert folder" : "Insert file", content, "Insert") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        string value = path.Text;
        if (directory && !value.EndsWith('/')) value += "/";
        if (!directory && value.EndsWith('/')) { Status("A file path must include its filename after the last slash.", true); return; }
        Edit(() => doc.InsertFilesEntry(files, value, position.SelectedIndex), FilesFailure);
    }

    private async Task RenameFilesEntryAsync(FilesEntryEdit entry)
    {
        var document = doc;
        var name = StructuredTextInput("Name", entry.Name);
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(name);
        content.Children.Add(Caption(entry.IsDirectory ? "Every descendant path is updated. Authored canvas positions are retained." : "The filename changes; its authored canvas position is retained."));
        if (await AskAsync("Rename file or folder", content, "Rename") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.RenameFilesEntry(entry, name.Text), FilesFailure);
    }

    private async Task MoveFilesEntryAsync(FilesEntryEdit entry, bool duplicate)
    {
        var document = doc;
        var path = StructuredTextInput(duplicate ? "Copy path" : "Destination path", duplicate ? doc.SuggestFilesCopyPath(entry) ?? entry.Path : entry.Path);
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(path);
        content.Children.Add(Caption(duplicate ? "The selected subtree and its authored geometry are copied. Existing files stay in place." : "Enter the complete destination path. Folder contents move together and retain their canvas positions."));
        if (await AskAsync(duplicate ? "Duplicate file or folder" : "Move file or folder", content, duplicate ? "Duplicate" : "Move") != ContentDialogResult.Primary || !StructuredStillCurrent(document)) return;
        string destination = path.Text;
        if (entry.IsDirectory && !destination.EndsWith('/')) destination += "/";
        Edit(() => duplicate ? doc.DuplicateFilesEntry(entry, destination) : doc.MoveFilesEntry(entry, destination), FilesFailure);
    }

    private async Task ReorderFilesEntryAsync(FilesEntryEdit entry)
    {
        var document = doc;
        var siblings = entry.Diagram.Entries.Where(item => item.ParentPath == entry.ParentPath).ToArray();
        if (siblings.Length < 2) { Status("This folder has only one child."); return; }
        var position = StructuredPicker("Sibling position", siblings.Select((item, index) => $"{index + 1}: {item.Name}"), Array.FindIndex(siblings, item => item.Path == entry.Path));
        if (await AskAsync("Reorder file or folder", position, "Move") == ContentDialogResult.Primary && StructuredStillCurrent(document))
            Edit(() => doc.ReorderFilesEntry(entry, position.SelectedIndex), FilesFailure);
    }

    private void RemoveFilesEntry(FilesEntryEdit entry) => DeleteCanvasObjects(() => doc.RemoveFilesEntry(entry, recursive: true), FilesFailure);

    private FilesEntryEdit? SelectedFilesEntry() => FieldSelection is { } field ? doc.OpenFilesEntry(field) : selected is { } item ? doc.OpenFilesEntry(item) : null;

    private string? FilesCanvasSelectionSource() => doc.Page.Family == "Files" && SelectedFilesEntry() is { } entry ? doc.CopyFilesEntry(entry) : null;

    private bool TryHandleFilesCanvasCommand(string action, string text)
    {
        if (doc.Page.Family != "Files") return false;
        if (doc.OpenFilesDiagram() is not { } files)
        {
            Status("Use Source to edit this generated or extended files diagram."); return true;
        }
        var entry = SelectedFilesEntry();
        switch (action)
        {
            case "cut":
                if (entry != null && doc.CopyFilesEntry(entry) != null) RemoveFilesEntry(entry);
                break;
            case "delete":
                if (entry != null) RemoveFilesEntry(entry);
                break;
            case "duplicate":
                if (entry != null && doc.SuggestFilesCopyPath(entry) is { } copy) Edit(() => doc.DuplicateFilesEntry(entry, copy), FilesFailure);
                break;
            case "paste":
                if (text.Length > 0) Edit(() => doc.PasteFilesEntries(files, text, entry is { IsDirectory: true } ? entry.Path : entry?.ParentPath ?? "/"), FilesFailure);
                break;
            case "group":
                Status("Use Move file or folder to place paths in a directory.");
                break;
        }
        return true;
    }
}
