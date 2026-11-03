using System.Text.RegularExpressions;

namespace UnoUML.Core;

internal static partial class LegacySyntax
{
    internal const string AsciiLabel = "ASCII label";
    internal const string ChenEntity = "Chen entity";
    internal const string ChenRelationship = "Chen relationship";
    internal const string ChenAttribute = "Chen attribute";
    private const string Name = """(?:(?<caption>"[^"]+")\s+as\s+)?""";
    private const string Id = @"[\p{L}\p{N}_.]+";
    private const string Card = @"(?:[\p{L}\p{N}]+|\([\p{L}\p{N}]+,\s*[\p{L}\p{N}]+\))?";
    [GeneratedRegex(@"^\s*(?<kind>entity|relationship)\s+" + Name + "(?<id>" + Id + @")(?:\s*<<[\p{L}]+>>)?(?:\s+(?<color>\#[A-Fa-f\d]{6}|\#[A-Fa-f\d]{3}))?\s*(?<brace>\{)$", RegexOptions.CultureInvariant)]
    internal static partial Regex ChenDeclaration();
    [GeneratedRegex(@"^\s*" + Name + @"(?<id>[\p{L}\p{N}_.:][\p{L}\p{N} _.:]*?)(?:\s*<<[\p{L}]+>>)?(?:\s+\#(?:[A-Fa-f\d]{6}|[A-Fa-f\d]{3}))?\s*(?:\{)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex ChenField();
    [GeneratedRegex(@"^\s*(?<from>" + Id + @")\s*(?<arrow>(?<part>[-=])(?<card>" + Card + @")[-=])\s*(?<to>" + Id + ")$", RegexOptions.CultureInvariant)]
    internal static partial Regex ChenAssociation();

    [GeneratedRegex("^" + Card + "$", RegexOptions.CultureInvariant)]
    internal static partial Regex Cardinality();

    [GeneratedRegex(@"^(?<part>[-=])(?<card>" + Card + @")[-=]$", RegexOptions.CultureInvariant)]
    internal static partial Regex ChenArrow();

    [GeneratedRegex(@"c(?:[a-fA-F\d]{3}|GRE|BLU|RED|YEL|PNK|BLK)|\{(?:d|s|io|mo|c|o|tr)\}", RegexOptions.CultureInvariant)]
    internal static partial Regex DitaaTags();

    internal static bool IsChenNode(DiagramObject value) => value.Kind is ChenEntity or ChenRelationship;
    internal static bool SafeDitaaLabel(string value) => value.Length is > 0 and <= 700 && !char.IsWhiteSpace(value[0]) && !char.IsWhiteSpace(value[^1])
        && !value.Contains("  ", StringComparison.Ordinal) && !DitaaTags().IsMatch(value)
        && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or ',' or '_' or '?' or '!' or '(' or ')' or '&' or '\'');

    internal static string AsciiId(int relativeLine, int column) => FormattableString.Invariant($"ditaa:{relativeLine}:{column}");
}

internal sealed partial class DiagramIndex
{
    private DiagramObject? ChenObject(int line, int end, int parent, string code)
    {
        var row = Lines[line];
        DiagramObject Item(string kind, string text) => new(_source, row.Start, Lines[end].End, line, end, parent, kind, text);
        if (parent < 0 && LegacySyntax.ChenAssociation().Match(code) is { Success: true } relation)
        {
            var card = relation.Groups["card"];
            var arrow = relation.Groups["arrow"];
            var obj = Item("Relationship", card.Length == 0 ? arrow.Value : card.Value);
            obj.From = relation.Groups["from"].Value;
            obj.To = relation.Groups["to"].Value;
            obj.FromAt = relation.Groups["from"].Index;
            obj.ToAt = relation.Groups["to"].Index;
            obj.Arrow = arrow.Value;
            obj.ArrowAt = arrow.Index;
            obj.LabelAt = arrow.Index + 1;
            obj.LabelLength = card.Length;
            return obj;
        }
        var match = (parent < 0 ? LegacySyntax.ChenDeclaration() : LegacySyntax.ChenField()).Match(code);
        if (!match.Success) return null;
        var display = match.Groups["caption"];
        var id = match.Groups["id"];
        var label = display.Success ? display : id;
        string kind = parent >= 0 ? LegacySyntax.ChenAttribute :
            match.Groups["kind"].Value == "entity" ? LegacySyntax.ChenEntity : LegacySyntax.ChenRelationship;
        var value = Item(kind, display.Success ? display.Value[1..^1] : id.Value);
        value.LabelAt = label.Index;
        value.LabelLength = label.Length;
        value.PlainName = !display.Success;
        value.Alias = id.Value;
        value.AliasAt = id.Index;
        value.AliasLength = id.Length;
        value.CanInsertMember = end > line;
        if (parent >= 0) return value;
        var fill = match.Groups["color"];
        int insertion = match.Groups["brace"].Index;
        while (insertion > 0 && char.IsWhiteSpace(code[insertion - 1])) insertion--;
        value.ColorAt = fill.Success ? fill.Index : insertion;
        value.ColorLength = fill.Length;
        return value;
    }

    private List<DiagramObject> DitaaObjects(DiagramPage page)
    {
        var result = new List<DiagramObject>();
        if (page.EndLine > page.StartLine + 1)
        {
            int first = page.StartLine + 1, last = page.EndLine - 1;
            int start = Lines[first].Start, end = Lines[last].End;
            result.Add(new(_source, start, end, first, last, -1, "ASCII drawing", "ASCII drawing")
            {
                Alias = "ditaa:drawing", LabelAt = 0, LabelLength = end - start,
                MultilineLabel = true, EditorLanguage = "ditaa", EditableContent = _source[start..end]
            });
        }
        for (int line = page.StartLine + 1; line < page.EndLine; line++)
        {
            var row = Lines[line];
            string text = _source[row.Start..row.End];
            if (text.Length == 0) continue;
            int left = -1, startCount = result.Count;
            for (int column = 0; column < text.Length; column++)
            {
                if (text[column] is not ('|' or ':' or '+' or '*') || !VerticalNeighbor(line, column)) continue;
                if (left >= 0) ReadDitaaInterval(result, page, line, text, left + 1, column);
                left = column;
            }
            if (result.Count == startCount)
                result.Add(new(_source, row.Start, row.End, line, line, -1, "Source", text.Trim()));
        }
        return result;
    }

    private bool VerticalNeighbor(int row, int column)
    {
        bool Vertical(int y) => y >= 0 && y < Lines.Count && Lines[y].End - Lines[y].Start > column &&
            _source[Lines[y].Start + column] is '|' or ':' or '+' or '*' or '/' or '\\';
        return Vertical(row - 1) || Vertical(row + 1);
    }

    private void ReadDitaaInterval(List<DiagramObject> result, DiagramPage page, int line, string text, int from, int to)
    {
        if (to <= from) return;
        // Tags occupy grid cells but aren't label text. Preserve positions when finding runs.
        var chars = text[from..to].ToCharArray();
        foreach (Match tag in LegacySyntax.DitaaTags().Matches(text[from..to])) Array.Fill(chars, ' ', tag.Index, tag.Length);
        for (int start = 0; start < chars.Length;)
        {
            while (start < chars.Length && chars[start] == ' ') start++;
            if (start == chars.Length) break;
            int end = start + 1;
            while (end < chars.Length && !(chars[end] == ' ' && (end + 1 == chars.Length || chars[end + 1] == ' '))) end++;
            string label = new(chars, start, end - start);
            int column = from + start;
            if (LegacySyntax.SafeDitaaLabel(label) && text.AsSpan(column, label.Length).SequenceEqual(label))
            {
                int offset = Lines[line].Start + column;
                result.Add(new DiagramObject(_source, offset, offset + label.Length, line, line, -1, LegacySyntax.AsciiLabel, label)
                {
                    Alias = LegacySyntax.AsciiId(line - page.StartLine, column), LabelAt = 0, LabelLength = label.Length
                });
            }
            start = end;
        }
    }
}

public sealed partial class StudioDocument
{
    private bool RenameDitaaLabel(DiagramObject item, string label)
    {
        if (!Objects.Contains(item) || item.Label == label || !LegacySyntax.SafeDitaaLabel(label)) return false;
        var row = Index.Lines[item.Line];
        int available = item.End;
        while (available < row.End && Source[available] == ' ') available++;
        if (available < row.End && Source[available] is not ('|' or ':' or '+' or '*'))
            available -= Math.Min(2, available - item.End); // maintain separation from the next label or tag
        if (item.Offset + label.Length > available) return false;
        for (int i = 0; i < label.Length; i++)
            if (label[i] is 'v' or 'V' && (i >= item.Label.Length || item.Label[i] != label[i]))
            {
                var previous = Index.Lines[item.Line - 1];
                int column = item.Offset - row.Start + i;
                if (column < previous.End - previous.Start && Source[previous.Start + column] is '|' or ':' or '*' or '+' or '/' or '\\')
                    return false; // would turn a letter into a down arrow on an existing wire
            }
        int cells = Math.Max(item.Label.Length, label.Length);
        return Patch(item.Offset, item.Offset + cells, label.PadRight(cells));
    }

    private bool RenameChenLabel(DiagramObject item, string label)
    {
        if (!Current(item) || !item.CanRename || !OneLine(label) || label == item.Label) return false;
        if (item.Kind == "Relationship") return UpdateChenConnection(item, null, null, item.Arrow, label);
        if (item.Kind is not (LegacySyntax.ChenEntity or LegacySyntax.ChenRelationship or LegacySyntax.ChenAttribute) ||
            string.IsNullOrWhiteSpace(label) || label.Any(char.IsControl) || label.Contains('"') || label.Contains('\\')) return false;
        string replacement = "\"" + label + "\"";
        if (item.PlainName) replacement += " as " + Source.Substring(item.Offset + item.LabelAt, item.LabelLength);
        return Patch(item.Offset + item.LabelAt, item.Offset + item.LabelAt + item.LabelLength, replacement);
    }

    private bool CanAddChenAttribute(DiagramObject owner, string member)
    {
        var field = LegacySyntax.ChenField().Match(member.Trim());
        if ((!LegacySyntax.IsChenNode(owner) && owner.Kind != LegacySyntax.ChenAttribute) || !field.Success || member.TrimEnd().EndsWith('{')) return false;
        string key = owner.Alias + "/" + field.Groups["id"].Value;
        return !Objects.Any(o => o.Alias == key);
    }

    private bool ChenEndpoints(DiagramObject from, DiagramObject to) => Current(from) && Current(to) && from.IsConnectable && to.IsConnectable &&
        LegacySyntax.IsChenNode(from) && LegacySyntax.IsChenNode(to);

    private static string? ChenArrow(string arrow, string? cardinality)
    {
        if (arrow == "-->") arrow = "-1-"; // editor's default connect gesture
        if (!OneLine(arrow) || !LegacySyntax.ChenArrow().IsMatch(arrow)) return null;
        if (cardinality == null) return arrow;
        if (!LegacySyntax.Cardinality().IsMatch(cardinality) || !OneLine(cardinality)) return null;
        return arrow[0] + cardinality + arrow[^1];
    }

    private bool UpdateChenConnection(DiagramObject edge, DiagramObject? from, DiagramObject? to, string arrow, string? cardinality)
    {
        var source = from ?? Find(edge.From);
        var target = to ?? Find(edge.To);
        if (!Current(edge) || edge.Kind != "Relationship" || source == null || target == null || !ChenEndpoints(source, target)) return false;
        var connector = ChenArrow(arrow, cardinality);
        if (connector == null) return false;
        var text = new System.Text.StringBuilder(edge.Statement);
        text.Remove(edge.ToAt, edge.To.Length).Insert(edge.ToAt, target.Alias);
        text.Remove(edge.ArrowAt, edge.Arrow.Length).Insert(edge.ArrowAt, connector);
        text.Remove(edge.FromAt, edge.From.Length).Insert(edge.FromAt, source.Alias);
        return Patch(edge.Offset, edge.End, text.ToString());
    }
}
