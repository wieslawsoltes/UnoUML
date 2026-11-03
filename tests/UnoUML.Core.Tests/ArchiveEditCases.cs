using System.IO.Compression;
using System.Text;
using UnoUML.Core;

internal static class ArchiveEditCases
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("Archive editing " + message); checks++; }
        const string original = "' preserved\r\nclass ArchiveNode\r\n";
        byte[] binary = [0, 255, 13, 0, 128, 12, 43];
        using var input = new MemoryStream();
        using (var zip = new ZipArchive(input, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.Comment = "archive comment";
            Write(zip, "model/main.iuml", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(original)).ToArray());
            Write(zip, "model/utf16.iuml", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("class Unicode\r\n")).ToArray());
            Write(zip, "assets/opaque.bin", binary);
            zip.CreateEntry("empty/");
        }
        var archive = new SourceArchive(input.ToArray());
        Check(archive.TextPaths.SequenceEqual(new[] { "model/main.iuml", "model/utf16.iuml" }), "enumerates editable text members");
        Check(archive.ReadText("model/main.iuml") == original, "decodes BOM without changing CRLF");
        Check(archive.ReplaceText("model/main.iuml", "stale", "class Other") == null, "rejects stale member text");
        var session = new StudioDocument(original);
        session.SetSource(original.Replace("ArchiveNode", "RenamedArchiveNode", StringComparison.Ordinal));
        var changed = archive.ReplaceText("model/main.iuml", original, session.Source)!;
        Check(changed.ReadText("model/main.iuml") == session.Source && archive.ReadText("model/main.iuml") == original, "replacement is an immutable transaction");
        using (var zip = new ZipArchive(new MemoryStream(changed.ToArray()), ZipArchiveMode.Read))
        {
            Check(zip.Comment == "archive comment" && zip.Entries.Count == 4, "preserves archive comment and directories");
            Check(Read(zip.GetEntry("assets/opaque.bin")!).SequenceEqual(binary), "preserves opaque binary bytes");
            var member = zip.GetEntry("model/main.iuml")!;
            Check(member.Comment == "member comment" && member.ExternalAttributes == 0x41ed0000 && member.LastWriteTime.Year == 2020, "preserves member metadata");
            Check(Read(member).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "preserves UTF8 BOM");
            Check(Read(zip.GetEntry("model/utf16.iuml")!).SequenceEqual(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("class Unicode\r\n"))), "preserves untouched UTF16 resource bytes");
        }
        Check(session.Undo() && session.Source == original, "member undo is atomic");
        var restored = changed.ReplaceText("model/main.iuml", changed.ReadText("model/main.iuml")!, session.Source)!;
        Check(restored.ReadText("model/main.iuml") == original, "undo repacks the original source");
        Check(session.Redo() && session.Source.Contains("RenamedArchiveNode", StringComparison.Ordinal), "member redo restores edited source");
        var wide = archive.ReplaceText("model/utf16.iuml", "class Unicode\r\n", "class 東京\r\n")!;
        using (var zip = new ZipArchive(new MemoryStream(wide.ToArray()), ZipArchiveMode.Read))
            Check(Read(zip.GetEntry("model/utf16.iuml")!).SequenceEqual(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("class 東京\r\n"))), "preserves encoding on edited UTF16 member");
        var history = new StudioDocument("start");
        long initialState = history.HistoryState;
        history.SetSource("changed");
        long changedState = history.HistoryState;
        history.SetSource("start");
        Check(history.HistoryState != initialState && history.HistoryState != changedState, "identical source text retains distinct transaction identity");
        Check(history.Undo() && history.HistoryState == changedState && history.Undo() && history.HistoryState == initialState, "undo restores transaction identity for resource interleaving");
        Check(history.Redo() && history.Fork().HistoryState == changedState, "fork and redo retain transaction identity");
        history.DiscardRedo(); history.SetSource("branch");
        Check(history.HistoryState > changedState && !history.CanRedo, "new resource branch cannot reuse discarded history state");
        return checks + ResourceHistoryCases.Run();
    }

    private static void Write(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.SmallestSize);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero);
        entry.ExternalAttributes = 0x41ed0000;
        entry.Comment = "member comment";
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }
}
