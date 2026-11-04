using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoUML.App.Rendering;
using UnoUML.Core;

namespace UnoUML.App;

internal sealed record LibrarySnippet(string Name, string Source);
internal sealed record StudioDraft(string FileName, string Source, int Page, bool Dirty = false);

[JsonSerializable(typeof(List<LibrarySnippet>))]
[JsonSerializable(typeof(StudioDraft))]
internal partial class StudioJson : JsonSerializerContext { }

public sealed partial class MainPage
{
    void RestoreDraft()
    {
        try
        {
            if (BrowserFiles.ReadDraft(DraftKey) is { Length: > 0 } json &&
                JsonSerializer.Deserialize(json, StudioJson.Default.StudioDraft) is { Source: not null, FileName: not null } draft)
            {
                doc = new(draft.Source); dirty = draft.Dirty; fileName = draft.FileName; doc.ActivePage = Math.Clamp(draft.Page, 0, doc.Pages.Count - 1);
            }
            if (BrowserFiles.ReadDraft(LibraryKey) is { Length: > 0 } library &&
                JsonSerializer.Deserialize(library, StudioJson.Default.ListLibrarySnippet) is { } stored)
                snippets.AddRange(stored.Where(ValidSnippet));
        }
        catch (Exception ex) { Status($"Could not restore browser data. {ex.Message}", true); }
    }

    void SaveDraft()
    {
        if (!ready) return;
        try
        {
            var draft = new StudioDraft(fileName, doc.Source, doc.ActivePage, dirty);
            BrowserFiles.WriteDraft(DraftKey, JsonSerializer.Serialize(draft, StudioJson.Default.StudioDraft));
            draftSaved = true;
        }
        catch (Exception ex) { draftSaved = false; Status($"Browser draft could not be saved. {ex.Message}", true); }
    }

    void PersistLibrary() => BrowserFiles.WriteDraft(LibraryKey, JsonSerializer.Serialize(snippets, StudioJson.Default.ListLibrarySnippet));

    static bool ValidSnippet(LibrarySnippet? value) => value is not null && !string.IsNullOrWhiteSpace(value.Name) && !string.IsNullOrWhiteSpace(value.Source);

    async Task<bool> CanReplaceAsync()
    {
        if (!dirty) return true;
        return await AskAsync("Replace the current document?", Caption("Download your .puml file with Save if you want to keep a separate copy. This replaces the local draft."), "Replace") == ContentDialogResult.Primary;
    }

    async Task NewAsync()
    {
        if (!await CanReplaceAsync()) return;
        var name = new TextBox { Header = "File name", Text = "Untitled.puml" };
        var picker = new ComboBox { Header = "Starting diagram", ItemsSource = StudioCatalog.Templates.Select(t => t.Name).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var fields = new StackPanel { Spacing = 8 }; fields.Children.Add(name); fields.Children.Add(picker);
        if (await AskAsync("New document", fields, "Create") != ContentDialogResult.Primary || picker.SelectedIndex < 0) return;
        string path = name.Text.Trim();
        if (string.IsNullOrEmpty(path)) path = "Untitled.puml";
        if (!path.Contains('.')) path += ".puml";
        OpenDocument(path, StudioCatalog.Templates[picker.SelectedIndex].Source);
    }

    async Task OpenAsync()
    {
        try
        {
            if (!await CanReplaceAsync()) return;
            if (await BrowserFiles.OpenAsync() is { } opened) OpenDocument(opened.Name, opened.Text);
        }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    void OpenDocument(string name, string source)
    {
        changeTimer.Stop();
        ResetRenderer();
        resourceHistory.Clear(); resourceCursor = 0;
        fitAfterRender = true; doc = new(source); fileName = name; additionalSelection.Clear(); selected = null; revision++; dirty = false; outlineLimit = 40;
        Refresh(); _ = RenderPageAsync();
    }

    async void SaveFile()
    {
        try
        {
            var snapshot = doc.Source;
            var name = fileName;
            await BrowserFiles.SaveAsync(name, snapshot);
            if (fileName == name && doc.Source == snapshot) { dirty = false; SaveDraft(); }
            Status($"Downloaded {name}");
        }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    static void MenuAction(MenuFlyout flyout, string title, Action callback)
    {
        var entry = new MenuFlyoutItem { Text = title };
        entry.Click += (_, _) => callback();
        flyout.Items.Add(entry);
    }

    void ShowExportMenu()
    {
        var menu = new MenuFlyout();
        MenuAction(menu, "PlantUML source (.puml)", SaveFile);
        MenuAction(menu, "Vector image (.svg)", () => _ = ExportAsync("svg"));
        MenuAction(menu, "Raster image (.png)", () => _ = ExportAsync("png"));
        MenuAction(menu, "Print / save PDF…", () => _ = ExportAsync("print"));
        menu.ShowAt(exportButton);
    }

    async Task ExportAsync(string format)
    {
        try
        {
            // Flush debounced source input and force the exact current page before exporting.
            int request = revision;
            viewport.SourceFileName = fileName;
            if (renderedRevision != request) await viewport.RenderAsync(doc.Page.Source);
            if (revision != request) { Status("The diagram changed during export. Export the updated version.", true); return; }
            ApplyCanvasLayout();
            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            if (doc.Pages.Count > 1) stem += $"-{doc.ActivePage + 1}";
            await viewport.ExportAsync(format, stem + "." + format);
            Status(format == "print" ? "Print view opened" : $"Exported {format.ToUpperInvariant()}");
        }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    void ShowLibraryMenu()
    {
        var menu = new MenuFlyout();
        MenuAction(menu, "Browse standard library…", () => _ = BrowseStandardLibraryAsync());
        MenuAction(menu, "Create reusable snippet…", () => _ = AddSnippetAsync());
        MenuAction(menu, "Import snippet library…", () => _ = ImportLibraryAsync());
        MenuAction(menu, "Export snippet library", ExportLibrary);
        MenuAction(menu, "Remove snippet…", () => _ = RemoveSnippetAsync());
        menu.Items.Add(new MenuFlyoutSeparator());
        MenuAction(menu, "Load include files…", () => _ = LoadIncludesAsync(false));
        MenuAction(menu, "Load include folder…", () => _ = LoadIncludesAsync(true));
        MenuAction(menu, "Load ZIP archive…", () => _ = LoadArchiveAsync());
        MenuAction(menu, "Download imported archive…", () => _ = ExportArchiveAsync());
        MenuAction(menu, "Edit imported include…", () => _ = BrowseIncludesAsync());
        MenuAction(menu, "Rendering engine…", ShowRendererSettings);
        menu.ShowAt((FrameworkElement)LibraryActions.Children[0]);
    }

    async Task LoadIncludesAsync(bool folder)
    {
        try
        {
            int count = folder ? await BrowserFiles.ImportIncludeFolderAsync() : await BrowserFiles.ImportIncludesAsync();
            if (count == 0) return;
            revision++; await RenderPageAsync();
            if (renderedRevision == revision || mode == "Source") Status($"Loaded {count} include file(s)");
        }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    async Task LoadArchiveAsync()
    {
        try
        {
            var archive = await BrowserIncludes.LoadArchiveAsync();
            if (archive.Name == null) return;
            revision++; await RenderPageAsync();
            string session = archive.Persisted ? "" : " for this session";
            Status($"Loaded {archive.Name} · {archive.Count} resources{session}. Use !import {archive.Name} in a diagram.");
        }
        catch (Exception error) { Status(error.Message, true); }
    }

    async Task AddSnippetAsync()
    {
        var name = new TextBox { Header = "Name", Text = selected?.Label ?? "" };
        var source = StatementField(selected?.Statement ?? "", "PlantUML snippet"); source.Header = "Source";
        var fields = new StackPanel { Spacing = 8 }; fields.Children.Add(name); fields.Children.Add(source);
        if (await AskAsync("Save reusable snippet", fields, "Save") != ContentDialogResult.Primary) return;
        var snippet = new LibrarySnippet(name.Text.Trim(), source.Text);
        if (!ValidSnippet(snippet)) { Status("Enter a name and PlantUML source.", true); return; }
        snippets.Add(snippet);
        try { PersistLibrary(); tab = "Saved"; ShowLibrary(); Status($"Saved {snippet.Name}"); }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    async Task RemoveSnippetAsync()
    {
        if (snippets.Count == 0) { Status("The snippet library is empty."); return; }
        var choices = new ComboBox { ItemsSource = snippets.Select(s => s.Name).ToList(), SelectedIndex = 0 };
        if (await AskAsync("Remove from library", choices, "Remove") != ContentDialogResult.Primary || choices.SelectedIndex < 0) return;
        snippets.RemoveAt(choices.SelectedIndex);
        try { PersistLibrary(); ShowLibrary(); } catch (Exception ex) { Status(ex.Message, true); }
    }

    async Task ImportLibraryAsync()
    {
        try
        {
            if (await BrowserFiles.OpenAsync(".json") is not { } opened) return;
            // Source files retain their BOM; JSON consumes it as an encoding
            // signature before parsing rather than as document content.
            ReadOnlySpan<char> json = opened.Text.AsSpan();
            if (json.StartsWith("\uFEFF", StringComparison.Ordinal)) json = json[1..];
            var list = JsonSerializer.Deserialize(json, StudioJson.Default.ListLibrarySnippet);
            if (list == null || list.Any(s => !ValidSnippet(s))) throw new FormatException("Use an array of snippets with Name and Source fields.");
            int count = snippets.Count;
            foreach (var s in list.Distinct()) if (!snippets.Contains(s)) snippets.Add(s);
            PersistLibrary(); tab = "Saved"; ShowLibrary(); Status($"Imported {snippets.Count - count} snippets");
        }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    async void ExportLibrary()
    {
        try { await BrowserFiles.SaveAsync("unouml-library.json", JsonSerializer.Serialize(snippets, StudioJson.Default.ListLibrarySnippet), "application/json"); Status("Exported snippet library"); }
        catch (Exception ex) { Status(ex.Message, true); }
    }
}
