using System.Text.RegularExpressions;

namespace UnoUML.Core;

internal static partial class SequenceSyntax
{
    internal const string ReturnKind = "Return", ReferenceKind = "Reference";
    private const string Token = """(?:"(?:\\.|[^"\\])*"|[\p{L}_$][\w.$]*)""";
    private const RegexOptions Grammar = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^\s*create\s+(?:(?<kind>participant|actor|boundary|control|entity|database|collections|queue)\s+)?(?<name>" + Token + @")(?:\s+as\s+(?<alias>" + Token + @"))?(?=$|[\s<#])", Grammar)]
    internal static partial Regex Creation();

    [GeneratedRegex(@"^\s*(?<from>" + Token + @")\s*(?<arrow>-{1,2}(?:\[#[a-zA-Z0-9]+\]-?)?>{1,2}|<{1,2}(?:-?\[#[a-zA-Z0-9]+\])?-{1,2})\s*(?<to>" + Token + @")\s*(?:(?<modifier>--\+\+|\+\+--|\+\+|--|\*\*|!!)(?:\s*#[\w]+)?|#[\w]+)\s*(?::\s*(?<label>.*))?$", Grammar)]
    internal static partial Regex Relation();

    [GeneratedRegex(@"^\s*ref(?:#[\w]+)?\s+over\s+" + Token + @"(?:\s*,\s*" + Token + @")*\s*(?::\s*(?<label>.*))?$", Grammar)]
    internal static partial Regex Reference();

    [GeneratedRegex(@"^\s*return(?=$|\s)(?:\s+(?:#[\w]+\s+)?(?<label>.*))?$", Grammar)]
    internal static partial Regex Return();

    [GeneratedRegex(@"^\s*destroy\s+(?<name>" + Token + @")(?:\s+#[\w]+)?\s*$", Grammar)]
    internal static partial Regex Destroy();

    internal static bool TextKind(string kind) => kind is ReturnKind or ReferenceKind;
    internal static string Identity(string token) => token.StartsWith('"') && token.EndsWith('"') ? token[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : token;
    internal static string Receiver(DiagramObject message) => Identity(message.Arrow.StartsWith('<') ? message.From : message.To);
    internal static bool CreationBound(DiagramObject item) => item.SequenceCreation || item.SequenceCreatedBy >= 0 || item.SequenceModifier == "**";
}

internal sealed partial class DiagramIndex
{
    private DiagramObject? SequenceText(int line, int end, int parent, string code)
    {
        // A multiline reference remains one opaque source block. Its body may
        // contain arbitrary PlantUML-looking text, URLs or formatting commands.
        if (line != end) return null;
        var match = SequenceSyntax.Return().Match(code);
        string kind = SequenceSyntax.ReturnKind;
        if (!match.Success)
        {
            match = SequenceSyntax.Reference().Match(code);
            kind = SequenceSyntax.ReferenceKind;
            if (!match.Success || !match.Groups["label"].Success) return null;
        }
        var label = match.Groups["label"];
        return new(_source, Lines[line].Start, Lines[end].End, line, end, parent, kind, label.Value)
        {
            LabelAt = label.Success ? label.Index : code.Length,
            LabelLength = label.Length
        };
    }
}

public sealed partial class StudioDocument
{
    private bool SequenceProtectedSelection(List<DiagramObject> selection) =>
        (Page.Family is "Sequence" or "UML") && Objects.Any(item => SequenceSyntax.CreationBound(item) && Inside(item, selection));

    private bool SequenceParticipantAvailable(string identifier, int line)
    {
        if (Page.Family is not ("Sequence" or "UML")) return true;
        string name = SequenceSyntax.Identity(identifier);
        foreach (var item in Objects)
        {
            if (item.SequenceCreation && SequenceSyntax.Identity(item.Alias) == name &&
                (item.SequenceCreationCall < 0 || line < item.SequenceCreationCall)) return false;
            if (item.SequenceModifier == "**" && SequenceSyntax.Receiver(item) == name && line < item.Line) return false;
            if (item.Line >= line) continue;
            if (item.SequenceModifier == "!!" && SequenceSyntax.Receiver(item) == name) return false;
            var destroy = SequenceSyntax.Destroy().Match(Index.Lines[item.Line].Code);
            if (destroy.Success && SequenceSyntax.Identity(destroy.Groups["name"].Value) == name) return false;
        }
        return true;
    }

    private bool SequenceConnectionEdit(DiagramObject item, DiagramObject? from, DiagramObject? to, string arrow)
    {
        if (Page.Family is not ("Sequence" or "UML")) return true;
        string left = from?.Alias ?? item.From, right = to?.Alias ?? item.To;
        if (left == item.From && right == item.To && arrow == item.Arrow) return true;
        string receiver = SequenceSyntax.Identity(arrow.StartsWith('<') ? left : right);
        string sender = SequenceSyntax.Identity(arrow.StartsWith('<') ? right : left);
        string previousSender = SequenceSyntax.Identity(item.Arrow.StartsWith('<') ? item.To : item.From);
        if (item.SequenceModifier.Contains("--", StringComparison.Ordinal) && sender != previousSender) return false;
        if ((item.SequenceCreatedBy >= 0 || item.SequenceModifier == "**") &&
            (receiver != SequenceSyntax.Receiver(item) || SequenceSyntax.Identity(left) == SequenceSyntax.Identity(right))) return false;
        return SequenceParticipantAvailable(left, item.Line) && SequenceParticipantAvailable(right, item.Line);
    }

    private bool SequenceMove(DiagramObject from, DiagramObject to)
    {
        if (Page.Family is not ("Sequence" or "UML")) return true;
        if (SequenceProtectedSelection([from])) return false;
        foreach (var item in Objects)
        {
            // Keep the next receiving message paired with its create statement.
            if (item.SequenceCreation && to.Line > item.Line && to.Line <= item.SequenceCreationCall) return false;
            if (item.Kind == "Relationship" && item.Line >= from.Line && item.EndLine <= from.EndLine &&
                (!SequenceParticipantAvailable(item.From, to.Line) || !SequenceParticipantAvailable(item.To, to.Line))) return false;
        }
        return true;
    }

    private bool RenameSequenceText(DiagramObject item, string label)
    {
        if (!Current(item) || !item.CanRename || !OneLine(label) || label.Length == 0 || label == item.Label) return false;
        bool separator = item.Kind == SequenceSyntax.ReturnKind && item.LabelLength == 0 && item.LabelAt > 0 &&
            !char.IsWhiteSpace(Source[item.Offset + item.LabelAt - 1]);
        return Patch(item.Offset + item.LabelAt, item.Offset + item.LabelAt + item.LabelLength, (separator ? " " : "") + label);
    }
}
