using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
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
}

internal static class BrowserIncludes
{
    private sealed record Archive(int Version, PlantUmlResourceLibrary? Library);
    private static readonly Dictionary<string, Archive> Archives = new(StringComparer.Ordinal);

    internal static async Task<string[]> PathsAsync()
    {
        await BrowserBridge.ReadyAsync();
        using var json = JsonDocument.Parse(await BrowserBridge.ListIncludes());
        return json.RootElement.EnumerateArray().Select(path => path.GetString()!).ToArray();
    }

    internal static string? Read(string path) => BrowserBridge.ReadInclude(path);

    internal static PlantUmlResourceLibrary? ImportLibrary(string path)
    {
        int version = BrowserBridge.ReadArchiveVersion(path);
        if (version == 0) return null;
        if (Archives.TryGetValue(path, out var cached) && cached.Version == version) return cached.Library;
        PlantUmlResourceLibrary? library = null;
        try
        {
            if (BrowserBridge.ReadArchive(path) is { } data) library = DecodeArchive(data);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or FormatException or NotSupportedException)
        {
            // The parser can decline a malformed resource without interrupting editing.
        }
        Archives[path] = new(version, library);
        return library;
    }

    private static PlantUmlResourceLibrary DecodeArchive(string data)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(data), writable: false);
        return PlantUmlResourceLibrary.FromZip(stream);
    }

    internal static async Task<(string? Name, int Count, bool Persisted)> LoadArchiveAsync()
    {
        await BrowserBridge.ReadyAsync();
        string? selected = await BrowserBridge.OpenArchive();
        if (selected == null) return (null, 0, false);
        using var json = JsonDocument.Parse(selected);
        string name = json.RootElement.GetProperty("name").GetString()!;
        string data = json.RootElement.GetProperty("data").GetString()!;
        var library = DecodeArchive(data);
        using var saved = JsonDocument.Parse(await BrowserBridge.SaveArchive(name, data));
        bool persisted = saved.RootElement.GetProperty("persisted").GetBoolean();
        Archives[name] = new(saved.RootElement.GetProperty("version").GetInt32(), library);
        return (name, library.Paths.Count, persisted);
    }

    internal static async Task<(bool Updated, bool Persisted)> UpdateAsync(string path, string original, string replacement)
    {
        await BrowserBridge.ReadyAsync();
        using var json = JsonDocument.Parse(await BrowserBridge.UpdateInclude(path, original, replacement));
        return (json.RootElement.GetProperty("updated").GetBoolean(), json.RootElement.GetProperty("persisted").GetBoolean());
    }
}
