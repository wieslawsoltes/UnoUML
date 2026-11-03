using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UnoUML.App.Rendering;
using UnoUML.Core;
using Windows.System;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private readonly Dictionary<string, StudioDocument> includeHistory = new(StringComparer.Ordinal);
    private DiagramHit? selectedGenerated;
    private int generatedRevision = -1;
    private DiagramHit? GeneratedSelection => generatedRevision == revision ? selectedGenerated : null;

    private async Task BrowseIncludesAsync()
    {
        try
        {
            var paths = await BrowserIncludes.PathsAsync();
            if (paths.Length == 0) { Status("Load include files, a folder or a ZIP archive to edit imported sources."); return; }
            var picker = new ComboBox { Header = "Imported file", ItemsSource = paths, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (await AskAsync("Imported include files", picker, "Edit") == ContentDialogResult.Primary && picker.SelectedIndex >= 0)
                await EditIncludeAsync(paths[picker.SelectedIndex].Path, archive: paths[picker.SelectedIndex].Archive);
        }
        catch (Exception error) { Status(error.Message, true); }
    }

    private async Task EditIncludeAsync(string path, int sourceLine = -1, string? archive = null)
    {
        try
        {
            string? original = BrowserIncludes.Read(path, archive);
            if (original == null) { Status("This source belongs to a standard library. Edit its invocation or import a local copy."); return; }
            var origin = new ImportedSource(path, archive);
            var document = includeHistory.TryGetValue(origin.Key, out var previous) && previous.Source == original ? previous.Fork() : new StudioDocument(original);
            var initial = document.Fork();
            string text = original.ReplaceLineEndings("\n");
            string newline = "\n";
            int newlineAt = original.IndexOfAny(['\r', '\n']);
            if (newlineAt >= 0 && original[newlineAt] == '\r')
                newline = newlineAt + 1 < original.Length && original[newlineAt + 1] == '\n' ? "\r\n" : "\r";
            var field = StatementField(text, "Imported PlantUML source");
            field.Header = origin.ToString();
            field.MinHeight = 220; field.MaxHeight = 420;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, "Imported include source");
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            Button? undo = null, redo = null;
            async Task DownloadAsync()
            {
                try { await BrowserFiles.SaveAsync(System.IO.Path.GetFileName(path), document.Source); }
                catch (Exception error) { Status(error.Message, true); }
            }
            void UpdateHistory()
            {
                undo!.IsEnabled = document.CanUndo; redo!.IsEnabled = document.CanRedo;
            }
            void Undo() { if (document.Undo()) field.Text = document.Source.ReplaceLineEndings("\n"); UpdateHistory(); }
            void Redo() { if (document.Redo()) field.Text = document.Source.ReplaceLineEndings("\n"); UpdateHistory(); }
            undo = Command("Undo include edit", "undo", Undo);
            redo = Command("Redo include edit", "redo", Redo);
            actions.Children.Add(undo); actions.Children.Add(redo);
            actions.Children.Add(Command("Download include", "save", () => _ = DownloadAsync(), true));
            async Task DownloadZipAsync()
            {
                try { await BrowserIncludes.DownloadArchiveAsync(archive!, path, document.Source); }
                catch (Exception error) { Status(error.Message, true); }
            }
            if (archive != null) actions.Children.Add(Command("Download ZIP", "save", () => _ = DownloadZipAsync(), true));
            void Shortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
            {
                var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
                shortcut.Invoked += (_, args) => { args.Handled = true; action(); };
                field.KeyboardAccelerators.Add(shortcut);
            }
            Shortcut(VirtualKey.Z, VirtualKeyModifiers.Control, Undo);
            Shortcut(VirtualKey.Y, VirtualKeyModifiers.Control, Redo);
            Shortcut(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, Redo);
            Shortcut(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = DownloadAsync());
            field.TextChanged += (_, _) => { document.SetSource(field.Text.ReplaceLineEndings(newline)); UpdateHistory(); };
            UpdateHistory();
            field.Loaded += (_, _) =>
            {
                int start = 0;
                for (int line = 0; line < sourceLine && start < text.Length; line++)
                {
                    int end = text.IndexOf('\n', start);
                    start = end < 0 ? text.Length : end + 1;
                }
                int stop = text.IndexOf('\n', start);
                field.Select(start, (stop < 0 ? text.Length : stop) - start);
            };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(Caption(archive == null ? "Changes update every diagram that includes this file. Download creates a separate source file." :
                "Apply replaces this member in the imported archive. Download ZIP includes your edits and preserves the archive's other resources."));
            content.Children.Add(actions); content.Children.Add(field);
            var errorMessage = Caption("");
            errorMessage.Foreground = Brush(183, 58, 58);
            content.Children.Add(errorMessage);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Edit imported include",
                Content = content,
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            bool applied = false, persisted = false;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                if (document.Source == original) return;
                args.Cancel = true;
                var deferral = args.GetDeferral();
                var committed = document.Fork();
                field.IsEnabled = false; undo.IsEnabled = false; redo.IsEnabled = false;
                try
                {
                    var result = await BrowserIncludes.UpdateAsync(path, original, committed.Source, archive);
                    if (!result.Updated)
                    {
                        errorMessage.Text = "The include changed while this editor was open. Download your edits, then reopen the current source.";
                        return;
                    }
                    includeHistory[origin.Key] = committed;
                    RecordResourceEdit(origin, initial, committed);
                    applied = true; persisted = result.Persisted; args.Cancel = false;
                }
                catch (Exception error) { errorMessage.Text = error.Message; }
                finally { field.IsEnabled = true; UpdateHistory(); deferral.Complete(); }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !applied) return;
            selectedGenerated = null; revision++;
            Refresh(); await RenderPageAsync();
            Status(persisted ? $"Updated {origin}" : $"Updated {origin} for this session. Download to retain a separate copy.");
        }
        catch (Exception error) { Status(error.Message, true); }
    }

    private void EditGeneratedSource(DiagramHit hit)
    {
        if (hit.SourceFile is { Length: > 0 } path && BrowserIncludes.Read(path, hit.SourceArchive) != null)
        {
            _ = EditIncludeAsync(path, hit.FileLine, hit.SourceArchive);
            return;
        }
        SetMode("Split");
        if (doc.Find(null, doc.Page.StartLine + hit.Line) is { } origin) SelectSource(origin);
    }

    private async Task ExportArchiveAsync()
    {
        try
        {
            string[] names = await BrowserIncludes.ArchivePathsAsync();
            if (names.Length == 0) { Status("Load a ZIP archive before downloading it."); return; }
            var picker = new ComboBox { Header = "Archive", ItemsSource = names, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (await AskAsync("Download imported archive", picker, "Download") == ContentDialogResult.Primary && picker.SelectedIndex >= 0)
                await BrowserIncludes.DownloadArchiveAsync(names[picker.SelectedIndex]);
        }
        catch (Exception error) { Status(error.Message, true); }
    }
}
