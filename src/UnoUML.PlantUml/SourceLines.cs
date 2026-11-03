namespace UnoUML.PlantUml;

internal static class SourceLines
{
    // Some parsers index LF-separated strings. Replacing only lone carriage
    // returns supplies that delimiter while keeping every UTF-16 source offset.
    internal static string WithLineFeeds(string source)
    {
        char[]? scanned = null;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != '\r' || i + 1 < source.Length && source[i + 1] == '\n') continue;
            scanned ??= source.ToCharArray();
            scanned[i] = '\n';
        }
        return scanned == null ? source : new(scanned);
    }
}
