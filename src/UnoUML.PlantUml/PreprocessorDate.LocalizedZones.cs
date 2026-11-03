using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace UnoUML.PlantUml;

internal static partial class PreprocessorDate
{
    private sealed record LocalizedZoneData(FrozenDictionary<string, int> Zones, FrozenDictionary<string, int> Locales,
        JsonElement Tables, JsonElement Names);

    private static readonly Lazy<LocalizedZoneData> LocalizedZones = new(ReadLocalizedZones);

    private static LocalizedZoneData ReadLocalizedZones()
    {
        using var resource = typeof(PreprocessorDate).Assembly.GetManifestResourceStream("UnoUML.PlantUml.Resources.timezones.json.gz")!;
        using var decompressor = new GZipStream(resource, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(decompressor);
        var catalog = document.RootElement.Clone();
        var zones = catalog.GetProperty("zones").EnumerateArray().Select((zone, index) => (Name: zone.GetString()!, Index: index))
            .ToFrozenDictionary(zone => zone.Name, zone => zone.Index, StringComparer.Ordinal);
        var locales = catalog.GetProperty("locales").EnumerateObject()
            .ToDictionary(locale => locale.Name, locale => locale.Value.GetInt32(), StringComparer.OrdinalIgnoreCase);
        // ICU retains Java's legacy private-use variants while also inserting
        // the two-letter variant into CultureInfo.Name before its extensions.
        locales.Add("ja-JP-JP-u-ca-japanese-x-lvariant-JP", locales["ja-JP-u-ca-japanese-x-lvariant-JP"]);
        locales.Add("th-TH-TH-u-nu-thai-x-lvariant-TH", locales["th-TH-u-nu-thai-x-lvariant-TH"]);
        return new(zones, locales.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), catalog.GetProperty("tables"), catalog.GetProperty("names"));
    }

    private static Names? LocalizedNames(string zone, CultureInfo culture)
    {
        // The existing English table also serves the invariant formatter, so
        // ordinary dates never load the multilingual catalog.
        if (culture.Name is "" or "en" or "en-US") return null;
        var catalog = LocalizedZones.Value;
        if (!catalog.Zones.TryGetValue(zone, out int zoneIndex)) return null;
        for (var candidate = culture; candidate.Name.Length > 0; candidate = candidate.Parent)
        {
            string name = candidate.Name;
            string language = name.Split('-')[0];
            string? alias = language switch { "iw" => "he", "in" => "id", "ji" => "yi", _ => null };
            if (alias != null) name = alias + name[language.Length..];
            if (!catalog.Locales.TryGetValue(name, out int table)) continue;
            int group = catalog.Tables[table][zoneIndex].GetInt32();
            var values = catalog.Names[group];
            return new(values[0].GetString()!, values[1].GetString()!, values[2].GetString()!, values[3].GetString()!);
        }
        return null;
    }
}
