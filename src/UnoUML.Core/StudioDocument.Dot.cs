using System.Text;

namespace UnoUML.Core;

public sealed record DotSourceLocation
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
    public string Format { get; init; } = "text";
    public int NameStart { get; init; } = -1;
    public int NameLength { get; init; }
    public int Start { get; init; }
    public int Length { get; init; }
    public int LabelStart { get; init; } = -1;
    public int LabelLength { get; init; }
    public int LabelInsert { get; init; } = -1;
    public string LabelKind { get; init; } = "";
    public bool Explicit { get; init; }
    public bool Directed { get; init; }
    public bool Merged { get; init; }
    public int FromStart { get; init; } = -1;
    public int FromLength { get; init; }
    public string FromId { get; init; } = "";
    public int ToStart { get; init; } = -1;
    public int ToLength { get; init; }
    public string ToId { get; init; } = "";
    public int ArrowStart { get; init; } = -1;
    public int ArrowLength { get; init; }
}

public sealed class DotObjectEdit
{
    internal DotObjectEdit(string document, int page, int offset, DotSourceLocation location, bool canRename, bool canEditConnection,
        bool canRemove, bool canDuplicate, bool canUngroup)
        => (Document, Page, Offset, Location, CanRename, CanEditConnection, CanRemove, CanDuplicate, CanUngroup) =
            (document, page, offset, location, canRename, canEditConnection, canRemove, canDuplicate, canUngroup);
    internal string Document { get; }
    internal int Page { get; }
    public DotSourceLocation Location { get; }
    public int Offset { get; }
    public string Text => Location.Text;
    public bool CanRename { get; }
    public bool CanEditConnection { get; }
    public bool CanRemove { get; }
    public bool CanDuplicate { get; }
    public bool CanUngroup { get; }
    public bool IsConnectable => Location.Kind == "node";
}

public sealed partial class StudioDocument
{
    public DotObjectEdit? OpenDotObject(DotSourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (Page.Family != "DOT" || location.Kind is not ("node" or "edge" or "graph" or "cluster") || location.Id.Length == 0 ||
            !DotRange(location.Start, location.Length)) return null;
        if (location.Kind == "node" && !DotRange(location.NameStart, location.NameLength)) return null;
        if (location.LabelStart >= 0)
        {
            if (!DotRange(location.LabelStart, location.LabelLength) || location.LabelKind is not ("id" or "quoted" or "html")) return null;
        }
        else if ((location.LabelInsert < location.Start && location.Explicit) || location.LabelInsert < 0 || location.LabelInsert > Page.Source.Length) return null;
        if (location.Kind == "edge" && (!DotRange(location.ArrowStart, location.ArrowLength) ||
            Page.Source.Substring(location.ArrowStart, location.ArrowLength) is not ("->" or "--"))) return null;
        bool singleEdge = location.Kind == "edge" && SingleDotEdge(location);
        bool editableEdge = singleEdge && !location.Merged && DotRange(location.FromStart, location.FromLength) && DotRange(location.ToStart, location.ToLength);
        bool editableNode = DotStructure is { } syntax && DotNodeMatches(syntax, location);
        bool editableCluster = DotStructure is { } parsed && DotCluster(parsed, location) != null;
        return new(Source, ActivePage, Index.Lines[Page.StartLine].Start + location.Start, location,
            location.Format == "text" && (location.Kind != "edge" || singleEdge),
            editableEdge, editableNode || editableCluster || editableEdge,
            editableNode && CanDuplicateDotNode(DotStructure!, DotStructure!.Nodes[location.Name]), editableCluster);
    }

    public bool RenameDotObject(DotObjectEdit item, string label)
    {
        if (!CurrentDot(item) || !item.CanRename || label == item.Text || label.Length > 65536 || label.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) return false;
        var location = item.Location;
        string value = QuoteDot(label);
        if (location.Kind == "node" && location.Explicit && MultipleDotNodes(location))
            return PatchDot(location.Start + location.Length, 0, ";" + Index.NewLine + "  " + DotIdentifier(item) + " [label=" + value + "]");
        if (location.LabelStart >= 0) return PatchDot(location.LabelStart, location.LabelLength, value);
        string replacement = location.Kind is "graph" or "cluster" ? "label=" + value + ";" :
            location.Explicit ? " [label=" + value + "]" : DotIdentifier(item) + " [label=" + value + "];";
        if (location.Kind is "graph" or "cluster" || !location.Explicit) replacement = Index.NewLine + "  " + replacement + Index.NewLine;
        return PatchDot(location.LabelInsert, 0, replacement);
    }

    public bool ConnectDot(DotObjectEdit from, DotObjectEdit to, string label = "")
    {
        if (!CurrentDot(from) || !CurrentDot(to) || !from.IsConnectable || !to.IsConnectable || label.Length > 65536 ||
            label.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) return false;
        int close = DotRootClose();
        if (close < 0) return false;
        string edge = DotIdentifier(from) + (from.Location.Directed ? " -> " : " -- ") + DotIdentifier(to);
        if (label.Length > 0) edge += " [label=" + QuoteDot(label) + "]";
        return PatchDot(close, 0, Index.NewLine + "  " + edge + ";" + Index.NewLine);
    }

    public bool RetargetDot(DotObjectEdit edge, DotObjectEdit target, bool from)
    {
        if (!CurrentDot(edge) || !CurrentDot(target) || !edge.CanEditConnection || !target.IsConnectable) return false;
        var location = edge.Location;
        int start = from ? location.FromStart : location.ToStart, length = from ? location.FromLength : location.ToLength;
        string endpoint = Page.Source.Substring(start, length);
        int port = DotPortOffset(endpoint);
        return PatchDot(start, length, DotIdentifier(target) + (port < 0 ? "" : endpoint[port..]));
    }

    public bool RemoveDotEdge(DotObjectEdit edge)
    {
        if (!CurrentDot(edge) || !edge.CanEditConnection) return false;
        return PatchDot(edge.Location.Start, edge.Location.Length, "");
    }

    private bool CurrentDot(DotObjectEdit item) => ReferenceEquals(item.Document, Source) && item.Page == ActivePage;
    private string DotIdentifier(DotObjectEdit item) => Page.Source.Substring(item.Location.NameStart, item.Location.NameLength);
    private bool DotRange(int start, int length) => start >= 0 && length > 0 && start <= Page.Source.Length - length;
    private bool PatchDot(int start, int length, string value)
    {
        int offset = Index.Lines[Page.StartLine].Start + start;
        return Patch(offset, offset + length, value);
    }

    private static string QuoteDot(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .ReplaceLineEndings("\\n").Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    private static int DotPortOffset(string endpoint)
    {
        bool quote = false;
        for (int i = 0; i < endpoint.Length; i++)
        {
            if (quote && endpoint[i] == '\\') { i++; continue; }
            if (endpoint[i] == '"') quote = !quote;
            else if (!quote && endpoint[i] == ':') return i;
        }
        return -1;
    }

    private bool SingleDotEdge(DotSourceLocation location)
    {
        int arrows = 0, attributes = 0;
        foreach (var token in DotSymbols(Page.Source, location.Start, location.Start + location.Length))
        {
            if (token.Text == "[") attributes++;
            else if (token.Text == "]") attributes--;
            else if (attributes == 0 && token.Text is "," or "{") return false;
            else if (attributes == 0 && token.Text is "->" or "--") arrows++;
        }
        return arrows == 1;
    }

    private bool MultipleDotNodes(DotSourceLocation location)
    {
        int attributes = 0;
        foreach (var token in DotSymbols(Page.Source, location.Start, location.Start + location.Length))
        {
            if (token.Text == "[") attributes++;
            else if (token.Text == "]") attributes--;
            else if (attributes == 0 && token.Text == ",") return true;
        }
        return false;
    }

    private int DotRootClose() => DotSymbols(Page.Source, 0, Page.Source.Length).Where(token => token.Text == "}").Select(token => token.Offset).DefaultIfEmpty(-1).Last();

    private static IEnumerable<(int Offset, string Text)> DotSymbols(string text, int begin, int end)
    {
        for (int i = begin; i < end; i++)
        {
            char c = text[i];
            if (c == '#' || c == '/' && i + 1 < end && text[i + 1] == '/')
            {
                while (i < end && text[i] is not ('\r' or '\n')) i++;
            }
            else if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < end && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i++;
            }
            else if (c == '"')
            {
                while (++i < end && text[i] != '"') if (text[i] == '\\') i++;
            }
            else if (c == '<')
            {
                int depth = 1;
                while (++i < end && depth > 0) { if (text[i] == '<') depth++; else if (text[i] == '>') depth--; }
                i--;
            }
            else if (c == '-' && i + 1 < end && text[i + 1] is '>' or '-') { yield return (i, text.Substring(i, 2)); i++; }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i + 1 < end && (char.IsAsciiLetterOrDigit(text[i + 1]) || text[i + 1] == '_')) i++;
                yield return (start, text[start..(i + 1)]);
            }
            else if ("[]{};,".Contains(c)) yield return (i, c.ToString());
        }
    }
}
