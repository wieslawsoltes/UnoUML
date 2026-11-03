namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    private enum SaltLiteralKind { Label, Input, Button, Check, Radio, Choice, ChoiceOption, Tree, Tab, MenuHeading, MenuReference, MenuOption, Title }
    private sealed class SaltScope(char kind) { internal char Kind = kind; }
    private sealed record SaltLiteral(SaltLiteralKind Kind, int Start, int Length, int TokenStart, int TokenEnd,
        SaltScope Scope, int Row, int Column, bool Title, int Ordinal = 0, int Depth = 0);
    private readonly record struct SaltLexeme(string Text, int Start, bool NewRow);
    private static readonly string[] SaltReserved = ["title ", "header ", "footer ", "caption ", "legend", "skinparam", "sprite ", "scale ", "hide ", "show "];
    private string? _saltLiteralSource;
    private int _saltLiteralPage = -1;
    private List<SaltLiteral>? _saltLiterals;

    private SaltLiteral? FindSaltLiteral(SourceFieldEdit edit, out List<SaltLiteral>? fields)
    {
        fields = null;
        if (!ReferenceEquals(edit.Document, Source) || edit.Page != ActivePage || edit.Kind != "salt-text" || Page.Family != "Wireframe") return null;
        if (!ReferenceEquals(_saltLiteralSource, Source) || _saltLiteralPage != ActivePage)
        {
            _saltLiteralSource = Source;
            _saltLiteralPage = ActivePage;
            _saltLiterals = ReadSaltStructure();
        }
        fields = _saltLiterals;
        return fields?.FirstOrDefault(item => item.Start == edit.Offset && item.Length == edit.Length);
    }

    // This lexical projection retains literal ranges and form scope; it never lays out or serializes the document.
    private List<SaltLiteral>? ReadSaltStructure()
    {
        var tokens = new List<SaltLexeme>();
        bool started = false, style = false;
        int preambleDepth = 0;
        for (int line = Page.StartLine; line <= Page.EndLine; line++)
        {
            var physical = Index.Lines[line];
            string raw = physical.Code;
            string trimmed = raw.TrimStart('\ufeff', ' ', '\t');
            if (trimmed.StartsWith("<style", StringComparison.OrdinalIgnoreCase)) style = true;
            if (style)
            {
                if (trimmed.Contains("</style>", StringComparison.OrdinalIgnoreCase)) style = false;
                continue;
            }
            if (trimmed.Length == 0 || trimmed[0] is '@' or '!' or '\'') continue;
            if (!started && (preambleDepth > 0 || trimmed.StartsWith("skinparam", StringComparison.OrdinalIgnoreCase)))
            {
                preambleDepth += raw.Count(c => c == '{') - raw.Count(c => c == '}');
                continue;
            }
            if (!started && !trimmed.StartsWith('{')) continue;
            started = true;
            if (raw.Length > 8192) return null;
            int begin = 0;
            for (int cursor = 0; cursor < raw.Length; cursor++)
            {
                char c = raw[cursor];
                if (c == '\\' && cursor + 1 < raw.Length && raw[cursor + 1] is '|' or '{' or '}') { cursor++; continue; }
                if (c is not ('|' or '{' or '}')) continue;
                Add(begin, cursor, false);
                if (c == '{')
                {
                    int end = cursor + 1;
                    if (end < raw.Length && "#!-+^/*".Contains(raw[end])) end++;
                    else if (end < raw.Length && raw[end] is 'S' or 'T')
                    {
                        char kind = raw[end++];
                        if (end < raw.Length && (kind == 'S' ? "I-" : "#!-+").Contains(raw[end])) end++;
                    }
                    Add(cursor, end, raw.AsSpan(end).Trim().IsEmpty);
                    cursor = end - 1;
                }
                else if (c == '}') Add(cursor, cursor + 1, raw.AsSpan(cursor + 1).Trim().IsEmpty);
                begin = cursor + 1;
            }
            Add(begin, raw.Length, true);
            if (tokens.Count > 16384) return null;

            void Add(int start, int end, bool newRow)
            {
                while (start < end && char.IsWhiteSpace(raw[start])) start++;
                while (end > start && char.IsWhiteSpace(raw[end - 1])) end--;
                if (start < end) tokens.Add(new(raw[start..end], physical.Start + start, newRow));
            }
        }
        var fields = new List<SaltLiteral>();
        int position = 0;
        return Visit(0) && position == tokens.Count ? fields : null;

        bool Visit(int depth)
        {
            if (depth >= 32 || position >= tokens.Count) return false;
            var opener = tokens[position++];
            if (!opener.Text.StartsWith('{')) return false;
            var scope = new SaltScope(opener.Text.Length > 1 ? opener.Text[1] : ' ');
            if (scope.Kind == '^' && !opener.NewRow && position < tokens.Count)
            {
                var title = tokens[position++];
                if (!ReadSaltLiterals(title.Text, title.Start, scope, -1, 0, true, fields)) return false;
            }
            if (opener.Text == "{" && position < tokens.Count && tokens[position].Text is "T" or "T#" or "T!" or "T-" or "T+")
            { scope.Kind = 'T'; position++; }
            int row = 0, column = 0;
            while (position < tokens.Count)
            {
                var token = tokens[position];
                if (token.Text == "}") { position++; return true; }
                bool newRow;
                if (token.Text.StartsWith('{'))
                {
                    if (!Visit(depth + 1)) return false;
                    newRow = tokens[position - 1].NewRow;
                }
                else
                {
                    position++;
                    newRow = token.NewRow;
                    if (token.Text != "*" && !ReadSaltLiterals(token.Text, token.Start, scope, row, column, false, fields)) return false;
                }
                if (++column > 128) return false;
                if (newRow) { column = 0; if (++row > 1024) return false; }
            }
            return false;
        }
    }

    private static bool ReadSaltLiterals(string raw, int start, SaltScope scope, int row, int column, bool title, List<SaltLiteral> fields)
    {
        if (!title && raw is "*" or "N=" or "S=" or "E=" or "W=") return false;
        int begin = 0, end = raw.Length, treeDepth = 0;
        SaltLiteralKind kind = title ? SaltLiteralKind.Title : scope.Kind switch
        {
            '/' => SaltLiteralKind.Tab,
            '*' => row == 0 ? SaltLiteralKind.MenuHeading : column == 0 ? SaltLiteralKind.MenuReference : SaltLiteralKind.MenuOption,
            'T' when column == 0 => SaltLiteralKind.Tree,
            _ => SaltLiteralKind.Label
        };
        if (title && raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') { begin++; end--; }
        else if (kind == SaltLiteralKind.Tree)
        {
            while (begin < end && raw[begin] == '+') begin++;
            treeDepth = begin;
            while (begin < end && char.IsWhiteSpace(raw[begin])) begin++;
        }
        else if (kind == SaltLiteralKind.Label)
        {
            if (raw is "" or ".") return true;
            int marker = 0;
            if (raw.Length >= 2 && raw[0] is '[' or '(')
            {
                char close = raw[0] == '[' ? ']' : ')';
                marker = raw[1] == close ? 2 : raw.Length >= 3 && raw[2] == close && raw[1] is 'X' or 'x' or ' ' ? 3 : 0;
            }
            if (marker > 0)
            {
                kind = raw[0] == '[' ? SaltLiteralKind.Check : SaltLiteralKind.Radio;
                begin = marker;
                while (begin < end && char.IsWhiteSpace(raw[begin])) begin++;
            }
            else if (raw.Length > 1 && raw[0] == '[' && raw[^1] == ']' && !raw.StartsWith("[[", StringComparison.Ordinal))
            { kind = SaltLiteralKind.Button; begin++; end--; }
            else if (raw.Length > 1 && raw[0] == '"' && raw[^1] == '"')
            { kind = SaltLiteralKind.Input; begin++; end--; }
            else if (raw.Length > 1 && raw[0] == '^' && raw[^1] == '^')
            { kind = SaltLiteralKind.Choice; begin++; end = raw.IndexOf('^', begin); }
            else if (raw.Length >= 2 && raw[0] is '.' or '-' or '=' or '~' && raw.StartsWith(new string(raw[0], 2), StringComparison.Ordinal) && raw.EndsWith(new string(raw[0], 2), StringComparison.Ordinal)) return true;
            else if (raw == "*" || raw.StartsWith("<<", StringComparison.Ordinal)) return false;
        }
        string content = raw[begin..end];
        if (DecodeSaltText(content).Length > 4096 || treeDepth > 128 || kind == SaltLiteralKind.Input && content.Contains('"') || kind == SaltLiteralKind.Button && content.Contains('[')) return false;
        fields.Add(new(kind, start + begin, content.TrimEnd().Length, start, start + raw.Length, scope, row, column, title, 0, treeDepth));
        if (kind == SaltLiteralKind.Choice)
        {
            int cursor = end + 1, ordinal = 0;
            while (cursor < raw.Length)
            {
                int next = raw.IndexOf('^', cursor);
                if (next < 0) break;
                string item = raw[cursor..next];
                int leading = item.Length - item.TrimStart().Length;
                item = item.Trim();
                if (item.Length > 0)
                {
                    if (++ordinal > 128 || DecodeSaltText(item).Length > 4096) return false;
                    fields.Add(new(SaltLiteralKind.ChoiceOption, start + cursor + leading, item.Length, start, start + raw.Length, scope, row, column, false, ordinal));
                }
                cursor = next + 1;
            }
        }
        return true;
    }
}
