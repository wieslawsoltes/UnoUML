using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;

namespace UnoUML.Core;

/// <summary>An immutable source archive. Replacements retain the original ZIP entries and metadata.</summary>
public sealed class SourceArchive
{
    private const int MaximumEntryLength = 4 * 1024 * 1024;
    private const int MaximumArchiveLength = 64 * 1024 * 1024;
    private sealed record TextEntry(string Name, string Text, Encoding Encoding, byte[] Preamble);
    private readonly byte[] bytes;
    private readonly Dictionary<string, TextEntry> textEntries = new(StringComparer.Ordinal);
    public IReadOnlyList<string> TextPaths { get; }

    public SourceArchive(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumArchiveLength) throw new InvalidDataException("The source archive exceeds 64 MiB.");
        this.bytes = bytes.ToArray();
        using var stream = new MemoryStream(this.bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string path = Normalize(entry.FullName);
            if (!paths.Add(path)) throw new InvalidDataException("The source archive contains duplicate paths.");
            if (paths.Count > 32768 || entry.Length > MaximumEntryLength || (total += entry.Length) > MaximumArchiveLength)
                throw new InvalidDataException("The source archive exceeds its resource limits.");
            if (Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp") continue;
            using var input = entry.Open();
            using var content = new MemoryStream();
            byte[] buffer = new byte[4096];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (content.Length + count > MaximumEntryLength) throw new InvalidDataException("The source entry exceeds 4 MiB.");
                content.Write(buffer, 0, count);
            }
            if (Decode(content.ToArray(), out string text, out var encoding, out var preamble))
                textEntries.Add(path, new(entry.FullName, text, encoding, preamble));
        }
        TextPaths = new ReadOnlyCollection<string>(textEntries.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    public string? ReadText(string path) => textEntries.GetValueOrDefault(path)?.Text;
    public byte[] ToArray() => bytes.ToArray();

    /// <summary>Replaces one member against its expected text. Other entries retain their original contents.</summary>
    public SourceArchive? ReplaceText(string path, string expected, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!textEntries.TryGetValue(path, out var entry) || entry.Text != expected) return null;
        if (replacement == expected) return this;
        byte[] text = entry.Encoding.GetBytes(replacement);
        if (text.Length + entry.Preamble.Length > MaximumEntryLength) throw new InvalidDataException("The source entry exceeds 4 MiB.");
        using var output = new MemoryStream();
        output.Write(bytes);
        output.Position = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var member = archive.GetEntry(entry.Name)!.Open();
            member.SetLength(0);
            member.Write(entry.Preamble);
            member.Write(text);
        }
        return new(output.ToArray());
    }

    private static bool Decode(byte[] bytes, out string text, out Encoding encoding, out byte[] preamble)
    {
        encoding = new UTF8Encoding(false, true);
        preamble = [];
        int offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) offset = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        try
        {
            text = encoding.GetString(bytes.AsSpan(offset));
            if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) { text = ""; return false; }
            preamble = bytes[..offset];
            return true;
        }
        catch (DecoderFallbackException) { text = ""; return false; }
    }

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl))
            throw new InvalidDataException("Archive paths must be relative.");
        var parts = new List<string>();
        foreach (string part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("An archive path escapes its root.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        if (parts.Count == 0) throw new InvalidDataException("An archive path is empty.");
        return string.Join('/', parts);
    }
}
