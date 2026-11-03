using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>An immutable, shareable virtual filesystem for source libraries, JSON and embedded artwork.</summary>
public sealed class PlantUmlResourceLibrary
{
    private const int MaximumFileSize = 4_194_304;
    private const int MaximumArchiveSize = 67_108_864;
    private readonly FrozenDictionary<string, string> files;
    private readonly record struct StandardFile(string Path, string Text);
    private readonly FrozenDictionary<string, StandardFile?> standardFiles;

    public PlantUmlResourceLibrary(IEnumerable<KeyValuePair<string, string>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var (path, text) in files)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length > MaximumFileSize || (total += text.Length) > MaximumArchiveSize || entries.Count >= 32768)
                throw new ArgumentException("The resource library exceeds its size limit.", nameof(files));
            if (!entries.TryAdd(Normalize(path), text)) throw new ArgumentException("Duplicate resource path.", nameof(files));
        }
        this.files = entries.ToFrozenDictionary(StringComparer.Ordinal);
        var standard = new Dictionary<string, StandardFile?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in entries)
        {
            if (!standard.TryAdd(path, new(path, text)) && standard[path]?.Text != text) standard[path] = null;
        }
        standardFiles = standard.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Paths => files.Keys;

    /// <summary>Reads source files and raster artwork without extracting files. Images resolve as data URIs. The input stream remains open.</summary>
    public static PlantUmlResourceLibrary FromZip(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 32768) throw new InvalidDataException("The resource archive exceeds its entry limit.");
        var entries = new List<KeyValuePair<string, string>>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string path = Normalize(entry.FullName);
            if (entry.Length > MaximumFileSize || (total += entry.Length) > MaximumArchiveSize || entries.Count >= 32768)
                throw new InvalidDataException("The resource archive exceeds its size limit.");
            using var input = entry.Open();
            string? mime = Path.GetExtension(entry.FullName).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => null
            };
            if (mime != null)
            {
                using var bytes = new MemoryStream();
                byte[] chunk = new byte[4096];
                int length;
                while ((length = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (bytes.Length + length > MaximumFileSize * 3 / 4 - 64) throw new InvalidDataException("The image resource exceeds its size limit.");
                    bytes.Write(chunk, 0, length);
                }
                string uri = "data:" + mime + ";base64," + Convert.ToBase64String(bytes.ToArray());
                if (!SpriteCatalog.TryImage(uri, out _)) throw new InvalidDataException("Invalid image resource.");
                entries.Add(new(path, uri));
                continue;
            }
            using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var text = new StringBuilder();
            char[] buffer = new char[4096];
            int count;
            bool opaque = false;
            try
            {
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length + count > MaximumFileSize) throw new InvalidDataException("The resource entry exceeds its size limit.");
                    text.Append(buffer, 0, count);
                }
            }
            catch (DecoderFallbackException) when (!SourceExtension(path)) { opaque = true; }
            if (opaque) continue;
            string value = text.ToString();
            if (!SourceExtension(path) && value.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) continue;
            entries.Add(new(path, value));
        }
        return new(entries);
    }

    private static bool SourceExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".puml" or ".plantuml" or ".iuml" or ".uml" or ".pu" or ".inc" or ".txt" or ".iss" or
        ".json" or ".yaml" or ".yml" or ".csv" or ".sprite" or ".svg";

    public string? Resolve(string path) => Resolve(path, out _);

    internal string? Resolve(string path, out string? member)
    {
        ArgumentNullException.ThrowIfNull(path);
        member = null;
        if (path.Length == 0) return null;
        if (path[0] == '<' && path[^1] == '>')
        {
            var libraryPath = path[1..^1].ToLowerInvariant();
            var file = standardFiles.GetValueOrDefault(libraryPath) ?? standardFiles.GetValueOrDefault(libraryPath + ".puml") ??
                standardFiles.GetValueOrDefault(libraryPath + ".iuml");
            member = file?.Path;
            return file?.Text;
        }
        if (!files.TryGetValue(path, out string? text)) return null;
        member = path;
        return text;
    }

    private static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path = path.Replace('\\', '/');
        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl))
            throw new ArgumentException("Resource paths must be relative virtual paths.", nameof(path));
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw new ArgumentException("Resource path escapes the virtual root.", nameof(path));
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        if (segments.Count == 0) throw new ArgumentException("Resource path is empty.", nameof(path));
        return string.Join('/', segments);
    }
}
