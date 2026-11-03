using System.IO.Compression;
using System.Collections.Frozen;
using System.Text.Json;

namespace UnoUML.PlantUml;

internal static class BuiltinResources
{
    private static readonly Lazy<JsonElement> Catalog = new(() => ReadCatalog("catalog.json.gz"));
    private static readonly Lazy<FrozenDictionary<string, string>> NativeLibraries = new(() => ReadCatalog("libraries.json.gz")
        .EnumerateObject().ToFrozenDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal));
    private static readonly Lazy<Artwork> Icons = new(() =>
    {
        var document = ReadCatalog("artwork.json.gz");
        FrozenDictionary<string, string> Map(string name) => document.GetProperty(name).EnumerateObject()
            .ToFrozenDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        return new(Map("openiconic"), Map("emoji"), Map("shortcuts"));
    });

    private sealed record Artwork(FrozenDictionary<string, string> OpenIconic, FrozenDictionary<string, string> Emoji,
        FrozenDictionary<string, string> Shortcuts);

    private static JsonElement ReadCatalog(string name)
    {
        using var resource = typeof(BuiltinResources).Assembly.GetManifestResourceStream("UnoUML.PlantUml.Resources." + name)!;
        using var decompressor = new GZipStream(resource, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(decompressor);
        return document.RootElement.Clone();
    }

    internal static string? Icon(string name) => Icons.Value.OpenIconic.GetValueOrDefault(name);
    internal static string? Emoji(string name)
    {
        name = name.ToLowerInvariant();
        var artwork = Icons.Value;
        return artwork.Emoji.GetValueOrDefault(artwork.Shortcuts.GetValueOrDefault(name) ?? name);
    }

    internal static JsonElement Themes => Catalog.Value.GetProperty("themes");
    internal static JsonElement Libraries => Catalog.Value.GetProperty("libraries");

    internal static bool TryGetLibrary(string name, out JsonElement library)
    {
        library = default;
        for (int hop = 0; hop < 32; hop++)
        {
            if (!Libraries.TryGetProperty(name, out library)) return false;
            if (!library.TryGetProperty("link", out var link) || string.IsNullOrEmpty(link.GetString())) return true;
            name = link.GetString()!;
        }
        return false;
    }

    internal static string? Resolve(string path)
    {
        if (!path.EndsWith('>')) return null;
        if (path.StartsWith("<theme/", StringComparison.Ordinal))
        {
            var name = path[7..^1];
            return Themes.TryGetProperty(name, out var theme) ? theme.GetString() : null;
        }
        return NativeLibrary(path);
    }

    internal static string? NativeLibrary(string path)
    {
        if (!path.StartsWith("<c4/", StringComparison.Ordinal) && !path.StartsWith("<archimate/", StringComparison.Ordinal) || !path.EndsWith('>')) return null;
        string key = path[1..^1];
        if (key.EndsWith(".puml", StringComparison.Ordinal) || key.EndsWith(".iuml", StringComparison.Ordinal)) key = key[..key.LastIndexOf('.')];
        return NativeLibraries.Value.GetValueOrDefault(key);
    }
}
