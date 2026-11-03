namespace UnoUML.Core;

public sealed partial class StudioDocument
{
    private bool InsertSaltWidget(string snippet)
    {
        int depth = 0, close = -1, preambleDepth = 0;
        bool started = false, style = false;
        for (int line = Page.StartLine; line <= Page.EndLine; line++)
        {
            var record = Index.Lines[line];
            string code = record.Code;
            string trimmed = code.TrimStart('\ufeff', ' ', '\t');
            if (trimmed.StartsWith("<style", StringComparison.OrdinalIgnoreCase)) style = true;
            if (style)
            {
                if (trimmed.Contains("</style>", StringComparison.OrdinalIgnoreCase)) style = false;
                continue;
            }
            if (trimmed.StartsWith('\'')) continue;
            if (!started && (preambleDepth > 0 || trimmed.StartsWith("skinparam", StringComparison.OrdinalIgnoreCase)))
            {
                preambleDepth += code.Count(c => c == '{') - code.Count(c => c == '}');
                continue;
            }
            if (!started && !trimmed.StartsWith('{')) continue;
            started = true;
            for (int i = 0; i < code.Length; i++)
            {
                if (code[i] == '\\' && i + 1 < code.Length && code[i + 1] is '|' or '{' or '}') { i++; continue; }
                if (code[i] == '{') depth++;
                if (code[i] == '}')
                {
                    if (--depth < 0) return false;
                    if (depth == 0) close = record.Start + i;
                }
            }
        }
        if (!started || depth != 0 || close < 0) return false;
        int beginning = close;
        while (beginning > 0 && Source[beginning - 1] is not ('\r' or '\n')) beginning--;
        string indent = Source[beginning..close];
        if (indent.All(c => c is ' ' or '\t'))
            return Patch(beginning, beginning, indent + "  " + LocalNewLines(snippet) + Index.NewLine);
        return Patch(close, close, Index.NewLine + "  " + LocalNewLines(snippet) + Index.NewLine);
    }

    /// <summary>Returns the declared items of the selected Salt droplist without changing the source snapshot.</summary>
    public IReadOnlyList<string> SaltChoices(SourceFieldEdit edit)
    {
        var field = FindSaltLiteral(edit, out var fields);
        if (field?.Kind != SaltLiteralKind.Choice) return [];
        return Array.AsReadOnly(fields!.Where(item => item.TokenStart == field.TokenStart && item.Kind == SaltLiteralKind.ChoiceOption)
            .Select(item => DecodeField(Source.Substring(item.Start, item.Length), "salt-text")!).ToArray());
    }

    /// <summary>Changes only the droplist heading, retaining every declared option and delimiter.</summary>
    public bool SelectSaltChoice(SourceFieldEdit edit, int index)
    {
        var choices = SaltChoices(edit);
        return index >= 0 && index < choices.Count && ApplySourceField(edit, choices[index]);
    }

    private string? EncodeSaltState(SourceFieldEdit edit, string text)
    {
        if (text is not ("true" or "false")) return null;
        string original = Source.Substring(edit.Offset, edit.Length);
        return string.Concat(original[0], text == "true" ? "X" : original.Length == 2 ? "" : " ", original[^1]);
    }

    private bool ApplySaltTextField(SourceFieldEdit edit, string text)
    {
        var field = FindSaltLiteral(edit, out var fields);
        if (field == null || field.Kind == SaltLiteralKind.MenuReference || text.Length > 4096) return false;
        string replacement = text.Replace("|", "\\|", StringComparison.Ordinal).Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal).ReplaceLineEndings("\\n");
        int escapes = 0;
        for (int at = replacement.AsSpan().TrimEnd().Length - 1; at >= 0 && replacement[at] == '\\'; at--) escapes++;
        if ((escapes & 1) != 0) return false;
        int padding = 0;
        if (replacement.Length > 0 && edit.Offset > field.TokenStart &&
            (edit.Length == 0 && field.Kind is SaltLiteralKind.Check or SaltLiteralKind.Radio ||
             field.Kind == SaltLiteralKind.Tree && Source[edit.Offset - 1] == '+' && replacement[0] == '+'))
        {
            replacement = " " + replacement;
            padding = 1;
        }
        // A new trailing escape must not consume the following cell or form delimiter.
        if (escapes > 0 && replacement.EndsWith('\\') && edit.Offset + edit.Length < Source.Length && Source[edit.Offset + edit.Length] is '|' or '{' or '}')
            replacement += " ";
        int lineStart = edit.Offset;
        while (lineStart > 0 && Source[lineStart - 1] is not ('\r' or '\n')) lineStart--;
        int lineEnd = edit.Offset + edit.Length;
        while (lineEnd < Source.Length && Source[lineEnd] is not ('\r' or '\n')) lineEnd++;
        bool oldComment = false, newComment = false;
        string oldCode = DiagramIndex.MaskComments(Source[lineStart..lineEnd], ref oldComment);
        int local = edit.Offset - lineStart;
        if (local + edit.Length > oldCode.Length || !oldCode.AsSpan(local, edit.Length).SequenceEqual(Source.AsSpan(edit.Offset, edit.Length))) return false;
        string newLine = Source[lineStart..edit.Offset] + replacement + Source[(edit.Offset + edit.Length)..lineEnd];
        string newCode = DiagramIndex.MaskComments(newLine, ref newComment);
        if (newLine.Length > 8192 || oldComment != newComment || newCode != oldCode[..local] + replacement + oldCode[(local + edit.Length)..]) return false;
        if (oldCode.AsSpan(0, local).Trim().IsEmpty)
        {
            string leading = replacement.TrimStart();
            if (leading.StartsWith('@') || leading.StartsWith('!') || leading.StartsWith('\'') || leading.StartsWith("<style", StringComparison.OrdinalIgnoreCase) ||
                SaltReserved.Any(prefix => leading.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        }
        string candidate = Source[field.TokenStart..edit.Offset] + replacement + Source[(edit.Offset + edit.Length)..field.TokenEnd];
        var changed = new List<SaltLiteral>();
        if (candidate.Trim().Length == 0 || !ReadSaltLiterals(candidate, field.TokenStart, field.Scope, field.Row, field.Column, field.Title, changed)) return false;
        var literals = fields!;
        var originals = literals.Where(item => item.TokenStart == field.TokenStart).ToArray();
        if (changed.Count != originals.Length || changed.Where((item, index) => item.Kind != originals[index].Kind || item.Depth != originals[index].Depth).Any()) return false;
        // Salt discards whitespace around each token before identifying its widget. Validate
        // that projection as well as the exact edited range, so " [Button] " cannot turn a
        // label into a button. Retain the untrimmed ranges for empty control captions.
        string normalized = candidate.Trim();
        if (normalized.Length != candidate.Length)
        {
            var projected = new List<SaltLiteral>();
            if (!ReadSaltLiterals(normalized, field.TokenStart, field.Scope, field.Row, field.Column, field.Title, projected) ||
                projected.Count != originals.Length || projected.Where((item, index) => item.Kind != originals[index].Kind || item.Depth != originals[index].Depth).Any()) return false;
        }
        var current = changed.FirstOrDefault(item => item.Ordinal == field.Ordinal);
        if (current == null || current.Start != edit.Offset + padding || current.Length != replacement.TrimEnd().Length - padding ||
            field.Kind == SaltLiteralKind.MenuOption && text.Trim() == "-") return false;
        var changes = new List<Change> { new(edit.Offset, edit.Offset + edit.Length, replacement) };
        if (field.Kind == SaltLiteralKind.MenuHeading)
        {
            string name = DecodeSaltText(replacement).TrimEnd();
            if (literals.Any(item => item.Scope == field.Scope && item.Kind == SaltLiteralKind.MenuHeading && item.Start != field.Start &&
                DecodeSaltText(Source.Substring(item.Start, item.Length)) == name)) return false;
            string original = Source.Substring(edit.Offset, edit.Length);
            foreach (var target in literals.Where(item => item.Scope == field.Scope && item.Kind == SaltLiteralKind.MenuReference))
                if (DecodeSaltText(Source.Substring(target.Start, target.Length)) == DecodeSaltText(original))
                    changes.Add(new(target.Start, target.Start + target.Length, replacement));
        }
        return ApplyChanges(changes);
    }

    private static string DecodeSaltText(string text) => text.Replace("\\|", "|", StringComparison.Ordinal)
        .Replace("\\{", "{", StringComparison.Ordinal).Replace("\\}", "}", StringComparison.Ordinal);
}
