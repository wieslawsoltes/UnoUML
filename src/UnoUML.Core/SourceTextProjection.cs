using System.Text;

namespace UnoUML.Core;

/// <summary>
/// Projects verbatim source into the CR-only text used by the native multiline
/// editor, without making the editor's newline coercion a document edit.
/// All positions are UTF-16 boundaries, as used by TextBox and source spans.
/// </summary>
public sealed class SourceTextProjection
{
    // Only CRLF loses a code unit. Keep its removed LF positions rather than
    // allocating a source-length array for every character of a large file.
    private readonly int[] removedLineFeeds;

    public string Source { get; }
    public string EditorText { get; }

    public SourceTextProjection(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        if (!source.Contains('\n'))
        {
            EditorText = source;
            removedLineFeeds = [];
            return;
        }

        var text = new StringBuilder(source.Length);
        var removed = new List<int>();
        for (int i = 0; i < source.Length; i++)
        {
            char value = source[i];
            text.Append(value == '\n' ? '\r' : value);
            if (value == '\r' && i + 1 < source.Length && source[i + 1] == '\n')
                removed.Add(++i);
        }
        EditorText = text.ToString();
        removedLineFeeds = [.. removed];
    }

    /// <summary>
    /// Maps a source boundary to the editor. Both the boundary inside CRLF and
    /// the boundary after it map to the end of the editor's single CR.
    /// </summary>
    public int ToEditorOffset(int sourceOffset)
    {
        sourceOffset = Math.Clamp(sourceOffset, 0, Source.Length);
        int low = 0, high = removedLineFeeds.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (removedLineFeeds[middle] < sourceOffset) low = middle + 1;
            else high = middle;
        }
        return sourceOffset - low;
    }

    /// <summary>
    /// Maps an editor boundary to verbatim source. A boundary following CR maps
    /// after the complete original newline, so an edit cannot split a CRLF.
    /// </summary>
    public int ToSourceOffset(int editorOffset)
    {
        editorOffset = Math.Clamp(editorOffset, 0, EditorText.Length);
        int low = 0, high = removedLineFeeds.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (removedLineFeeds[middle] - middle <= editorOffset) low = middle + 1;
            else high = middle;
        }
        return editorOffset + low;
    }

    public (int Start, int Length) ToEditorRange(int sourceStart, int sourceLength)
    {
        var (start, end) = ClampRange(sourceStart, sourceLength, Source.Length);
        int projected = ToEditorOffset(start);
        return (projected, ToEditorOffset(end) - projected);
    }

    public (int Start, int Length) ToSourceRange(int editorStart, int editorLength)
    {
        var (start, end) = ClampRange(editorStart, editorLength, EditorText.Length);
        int projected = ToSourceOffset(start);
        return (projected, ToSourceOffset(end) - projected);
    }

    /// <summary>
    /// Applies the smallest changed editor interval to the original source.
    /// Unchanged prefix/suffix bytes, including mixed newlines and a BOM, stay
    /// verbatim. New line breaks use the edited line's physical newline, the
    /// previous line's newline at EOF, or LF for a document without newlines.
    /// </summary>
    public string ApplyEditorText(string editorText)
    {
        ArgumentNullException.ThrowIfNull(editorText);
        // ReplaceLineEndings also rewrites Unicode separators; native TextBox
        // only coerces CR/LF, so preserve all other source characters exactly.
        string next = editorText.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (next == EditorText) return Source;

        int start = 0, sharedLength = Math.Min(EditorText.Length, next.Length);
        while (start < sharedLength && EditorText[start] == next[start]) start++;
        int oldEnd = EditorText.Length, newEnd = next.Length;
        while (oldEnd > start && newEnd > start && EditorText[oldEnd - 1] == next[newEnd - 1])
        {
            oldEnd--; newEnd--;
        }

        int sourceStart = ToSourceOffset(start), sourceEnd = ToSourceOffset(oldEnd);
        string inserted = next[start..newEnd];
        if (inserted.Contains('\r'))
            inserted = inserted.Replace("\r", NewLineAt(sourceStart), StringComparison.Ordinal);
        return string.Concat(Source.AsSpan(0, sourceStart), inserted, Source.AsSpan(sourceEnd));
    }

    private string NewLineAt(int offset)
    {
        for (int i = offset; i < Source.Length; i++)
        {
            if (Source[i] == '\r') return i + 1 < Source.Length && Source[i + 1] == '\n' ? "\r\n" : "\r";
            if (Source[i] == '\n') return "\n";
        }
        for (int i = offset - 1; i >= 0; i--)
        {
            if (Source[i] == '\n') return i > 0 && Source[i - 1] == '\r' ? "\r\n" : "\n";
            if (Source[i] == '\r') return "\r";
        }
        return "\n";
    }

    private static (int Start, int End) ClampRange(int start, int length, int maximum)
    {
        int first = Math.Clamp(start, 0, maximum);
        int end = (int)Math.Clamp((long)start + Math.Max(0, length), first, maximum);
        return (first, end);
    }
}
