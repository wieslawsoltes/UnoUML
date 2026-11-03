using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using UnoUML.Core;
using UnoUML.PlantUml;

namespace UnoUML.App.Rendering;

internal static partial class BrowserBridge
{
    [JSImport("listIncludes", Module)] internal static partial Task<string> ListIncludes();
    [JSImport("updateInclude", Module)] internal static partial Task<string> UpdateInclude(string path, string expectedText, string text);
    [JSImport("openArchive", Module)] internal static partial Task<string?> OpenArchive();
    [JSImport("saveArchive", Module)] internal static partial Task<string> SaveArchive(string path, string data);
    [JSImport("readArchive", Module)] internal static partial string? ReadArchive(string path);
    [JSImport("readArchiveVersion", Module)] internal static partial int ReadArchiveVersion(string path);
    [JSImport("listArchives", Module)] internal static partial Task<string> ListArchives();
    [JSImport("updateArchive", Module)] internal static partial Task<string> UpdateArchive(string path, string expectedData, string data);
    [JSImport("downloadArchive", Module)] internal static partial void DownloadArchive(string path, string? data);
}

internal sealed record ImportedSource(string Path, string? Archive = null)
{
    internal string Key => Archive == null ? Path : Archive + "\0" + Path;
    public override string ToString() => Archive == null ? Path : Archive + " / " + Path;
}

internal static class BrowserIncludes
{
    private sealed record Archive(int Version, string Data, SourceArchive? Source, PlantUmlResourceLibrary? Library);
    private static readonly Dictionary<string, Archive> Archives = new(StringComparer.Ordinal);

    internal static async Task<ImportedSource[]> PathsAsync()
    {
        await BrowserBridge.ReadyAsync();
        using var json = JsonDocument.Parse(await BrowserBridge.ListIncludes());
        var paths = json.RootElement.EnumerateArray().Select(path => new ImportedSource(path.GetString()!)).ToList();
        foreach (string name in await ArchivePathsAsync())
            if (GetArchive(name)?.Source is { } archive)
                paths.AddRange(archive.TextPaths.Select(path => new ImportedSource(path, name)));
        return paths.ToArray();
    }

    internal static async Task<string[]> ArchivePathsAsync()
    {
        await BrowserBridge.ReadyAsync();
        using var json = JsonDocument.Parse(await BrowserBridge.ListArchives());
        return json.RootElement.EnumerateArray().Select(path => path.GetString()!).ToArray();
    }

    internal static string? Read(string path, string? archive = null) => archive == null ? BrowserBridge.ReadInclude(path) : GetArchive(archive)?.Source?.ReadText(path);

    internal static PlantUmlResourceLibrary? ImportLibrary(string path) => GetArchive(path)?.Library;

    private static Archive? GetArchive(string path)
    {
        int version = BrowserBridge.ReadArchiveVersion(path);
        if (version == 0) return null;
        if (Archives.TryGetValue(path, out var cached) && cached.Version == version) return cached;
        string data = BrowserBridge.ReadArchive(path) ?? "";
        try
        {
            return Archives[path] = DecodeArchive(data, version);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or FormatException or NotSupportedException)
        {
            // The parser can decline a malformed resource without interrupting editing.
        }
        return Archives[path] = new(version, data, null, null);
    }

    private static Archive DecodeArchive(string data, int version)
    {
        byte[] bytes = Convert.FromBase64String(data);
        using var stream = new MemoryStream(bytes, writable: false);
        return new(version, data, new SourceArchive(bytes), PlantUmlResourceLibrary.FromZip(stream));
    }

    internal static async Task<(string? Name, int Count, bool Persisted)> LoadArchiveAsync()
    {
        await BrowserBridge.ReadyAsync();
        string? selected = await BrowserBridge.OpenArchive();
        if (selected == null) return (null, 0, false);
        using var json = JsonDocument.Parse(selected);
        string name = json.RootElement.GetProperty("name").GetString()!;
        string data = json.RootElement.GetProperty("data").GetString()!;
        var archive = DecodeArchive(data, 0);
        using var saved = JsonDocument.Parse(await BrowserBridge.SaveArchive(name, data));
        bool persisted = saved.RootElement.GetProperty("persisted").GetBoolean();
        Archives[name] = archive with { Version = saved.RootElement.GetProperty("version").GetInt32() };
        return (name, archive.Library!.Paths.Count, persisted);
    }

    internal static async Task<(bool Updated, bool Persisted)> UpdateAsync(string path, string original, string replacement, string? archive = null)
    {
        await BrowserBridge.ReadyAsync();
        string result;
        Archive? revised = null;
        if (archive == null) result = await BrowserBridge.UpdateInclude(path, original, replacement);
        else
        {
            var current = GetArchive(archive);
            if (current?.Source?.ReplaceText(path, original, replacement) is not { } changed) return (false, false);
            revised = DecodeArchive(Convert.ToBase64String(changed.ToArray()), 0);
            result = await BrowserBridge.UpdateArchive(archive, current.Data, revised.Data);
        }
        using var json = JsonDocument.Parse(result);
        if (revised != null && json.RootElement.GetProperty("updated").GetBoolean())
            Archives[archive!] = revised with { Version = json.RootElement.GetProperty("version").GetInt32() };
        return (json.RootElement.GetProperty("updated").GetBoolean(), json.RootElement.GetProperty("persisted").GetBoolean());
    }

    internal static async Task DownloadArchiveAsync(string name, string? member = null, string? text = null)
    {
        await BrowserBridge.ReadyAsync();
        var archive = GetArchive(name) ?? throw new InvalidOperationException("The imported archive is unavailable.");
        string data = archive.Data;
        if (member != null && text != null)
        {
            var source = archive.Source ?? throw new InvalidOperationException("The source archive is invalid.");
            var changed = source.ReplaceText(member, source.ReadText(member) ?? "", text)
                ?? throw new InvalidOperationException("The archive member is unavailable.");
            data = Convert.ToBase64String(changed.ToArray());
        }
        BrowserBridge.DownloadArchive(name, data);
    }
}
