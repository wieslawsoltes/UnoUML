using System.Text;
using System.Text.Json;

namespace UnoUML.Core;

public sealed class JsonFieldEdit
{
    internal JsonFieldEdit(string document, int page, int at, int length, string kind, string text)
        => (Document, Page, Offset, Length, Kind, Text) = (document, page, at, length, kind, text);
    internal string Document { get; }
    internal int Page { get; }
    public int Offset { get; }
    public int Length { get; }
    public string Kind { get; }
    public string Text { get; }
}

public sealed partial class StudioDocument
{
    public JsonFieldEdit? OpenJsonField(int start, int length, string kind)
    {
        if (Page.Family != "JSON" || start < 0 || length <= 0 || start > Page.Source.Length - length) return null;
        if (!JsonBody(out int body, out int end) || start < body || start + length > end) return null;
        var text = Page.Source;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[body..end]));
        int target = Encoding.UTF8.GetByteCount(text.AsSpan(body,start-body));
        int size = Encoding.UTF8.GetByteCount(text.AsSpan(start,length));
        try
        {
            while (reader.Read() && reader.TokenStartIndex <= target)
            {
                if (reader.TokenStartIndex != target) continue;
                string actual = reader.TokenType switch {
                    JsonTokenType.PropertyName => "key", JsonTokenType.String => "string",
                    JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null => "scalar", _ => ""
                };
                if (kind != actual || actual.Length == 0 || reader.ValueSpan.Length + (actual == "scalar" ? 0 : 2) != size) return null;
                string value = actual == "scalar" ? text.Substring(start,length) : reader.GetString()!;
                return new(Source, ActivePage, Index.Lines[Page.StartLine].Start + start, length, actual, value);
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    public bool ApplyJsonField(JsonFieldEdit edit, string text)
    {
        if (!ReferenceEquals(edit.Document, Source) || edit.Page != ActivePage || text == edit.Text || !JsonBody(out int body,out int end)) return false;
        string replacement;
        if (edit.Kind == "scalar")
        {
            replacement = text.Trim();
            try
            {
                using var scalar = JsonDocument.Parse(replacement);
                if (scalar.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return false;
            }
            catch (JsonException) { return false; }
        }
        else
        {
            replacement = "\"" + JsonEncodedText.Encode(text) + "\"";
        }
        int at = edit.Offset - Index.Lines[Page.StartLine].Start;
        string candidate = string.Concat(Page.Source.AsSpan(body,at-body),replacement,Page.Source.AsSpan(at+edit.Length,end-at-edit.Length));
        try { using var check = JsonDocument.Parse(candidate); }
        catch (JsonException) { return false; }
        return Patch(edit.Offset,edit.Offset+edit.Length,replacement);
    }

    private bool JsonBody(out int body,out int end)
    {
        int first = Page.StartLine;
        body = Index.Lines[first].Next - Index.Lines[first].Start;
        end = Index.Lines[Page.EndLine].Start - Index.Lines[first].Start;
        return Page.Family == "JSON" && end > body;
    }
}
