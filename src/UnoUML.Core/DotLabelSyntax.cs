using System.Globalization;
using System.Net;
using System.Text;

namespace UnoUML.Core;

/// <summary>An immutable structured-label selection tied to its source document revision.</summary>
public sealed class DotLabelEdit
{
    internal DotLabelEdit(DotObjectEdit item, DotLabelSyntax syntax) => (Item, Syntax) = (item, syntax);
    internal DotObjectEdit Item { get; }
    internal DotLabelSyntax Syntax { get; }
    public string Kind => Syntax.Kind;
    public IReadOnlyList<DotRecordField> RecordFields => Syntax.Fields;
    public IReadOnlyList<DotTable> Tables => Syntax.Tables;
}

public sealed record DotRecordField(string Path, string ParentPath, int Index, bool IsGroup, string Text, string Port, int Start, int Length);
public sealed record DotTableCell(int Row, int Column, int RowSpan, int ColumnSpan, string Text, string Port, int Start, int Length);
public sealed record DotTable(int Index, int Rows, int Columns, bool Rectangular, IReadOnlyList<DotTableCell> Cells);

/// <summary>Bounded lossless views over record and HTML labels. All editing positions address the original label value.</summary>
internal sealed class DotLabelSyntax
{
    internal sealed class Field(string path, string parent, int index, int start)
    {
        internal readonly string Path = path, Parent = parent;
        internal readonly int Index = index, Start = start;
        internal int End, ContentStart, ContentEnd, PortStart = -1, PortEnd = -1, TextStart;
        internal string Port = "", Text = "";
        internal bool IsGroup;
        internal readonly List<Field> Children = [];
    }
    internal sealed record Attribute(string Name, string Value, int Start, int End, int ValueStart, int ValueEnd, char Quote);
    internal sealed class Element(string name, int start)
    {
        internal readonly string Name = name;
        internal readonly int Start = start;
        internal int OpenEnd, CloseStart, End;
        internal bool SelfClosing;
        internal readonly List<Attribute> Attributes = [];
        internal readonly List<Element> Children = [];
        internal Attribute? Find(string name) => Attributes.FirstOrDefault(attribute => attribute.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        internal string Value(string name) => Find(name)?.Value ?? "";
    }
    internal sealed record Cell(Element Element, int Row, int Column, int RowSpan, int ColumnSpan);
    internal sealed class Table(Element element, int index)
    {
        internal readonly Element Element = element;
        internal readonly int Index = index;
        internal readonly List<Element> Rows = [];
        internal readonly List<Cell> Cells = [];
        internal int Columns;
        internal bool Rectangular;
    }
    private sealed class InvalidLabel : Exception;
    internal readonly DotSyntax.Token Token;
    internal readonly string Kind, Value;
    internal readonly List<Field> Record = [];
    internal readonly List<Table> HtmlTables = [];
    internal readonly HashSet<string> Ports = new(StringComparer.Ordinal);
    internal IReadOnlyList<DotRecordField> Fields = [];
    internal IReadOnlyList<DotTable> Tables = [];
    private readonly int[] offsets;

    private DotLabelSyntax(DotSyntax syntax, DotSyntax.Token token, string kind)
    {
        (Token, Kind, Value) = (token, kind, token.Value);
        offsets = CharacterOffsets(syntax, token);
    }

    internal static DotLabelSyntax? Parse(DotSyntax syntax, DotSyntax.Token token, string kind)
    {
        try
        {
            if (token.Value.Length > 65536) return null;
            var result = new DotLabelSyntax(syntax, token, kind);
            if (kind == "record") result.ParseRecord();
            else if (kind == "html") result.ParseHtml();
            else return null;
            return result;
        }
        catch (InvalidLabel) { return null; }
    }

    internal DotLabelSyntax? WithValue(string value) => ParseValue(value, Kind);
    internal static DotLabelSyntax? ParseValue(string value, string kind)
    {
        string literal = kind == "html" ? "<" + value + ">" : QuoteRecord(value);
        var syntax = DotSyntax.Parse("@startdot\ndigraph { n [label=" + literal + "]; }\n@enddot");
        return syntax == null ? null : Parse(syntax, syntax.Nodes["n"].Attributes["label"], kind);
    }

    internal static string QuoteRecord(string text) => "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal).ReplaceLineEndings("\\n") + "\"";
    internal static string RecordText(string text)
    {
        var output = new StringBuilder();
        foreach (char c in text.ReplaceLineEndings("\n"))
        {
            if (c == '\n') output.Append("\\n");
            else { if ("\\{}|<>".Contains(c)) output.Append('\\'); output.Append(c); }
        }
        return output.ToString();
    }

    internal static bool ValidPort(string port) => port.Length <= 256 && port == port.Trim() &&
        !port.Any(c => char.IsControl(c) || "<>{}|\\".Contains(c));

    private void Port(string port)
    {
        if (port.Length > 0 && (!ValidPort(port) || !Ports.Add(port))) throw new InvalidLabel();
    }

    private void ParseRecord()
    {
        int at = 0, count = 0;
        List<Field> Read(string parent, int depth)
        {
            if (depth > 32) throw new InvalidLabel();
            var fields = new List<Field>();
            do
            {
                if (++count > 1024) throw new InvalidLabel();
                string path = parent.Length == 0 ? fields.Count.ToString(CultureInfo.InvariantCulture) : parent + "." + fields.Count;
                var field = new Field(path, parent, fields.Count, at);
                if (at < Value.Length && Value[at] == '{')
                {
                    field.IsGroup = true; field.ContentStart = ++at;
                    field.Children.AddRange(Read(path, depth + 1));
                    field.ContentEnd = at;
                    if (at >= Value.Length || Value[at++] != '}') throw new InvalidLabel();
                }
                else
                {
                    field.TextStart = at;
                    bool text = false;
                    while (at < Value.Length && Value[at] is not ('|' or '}'))
                    {
                        char c = Value[at++];
                        if (c == '\\') { if (at == Value.Length) throw new InvalidLabel(); at++; text = true; }
                        else if (c == '<' && !text && field.PortStart < 0)
                        {
                            field.PortStart = at - 1;
                            int close = Value.IndexOf('>', at);
                            if (close < 0) throw new InvalidLabel();
                            field.Port = Value[at..close].Trim(); Port(field.Port);
                            at = close + 1; field.PortEnd = at; field.TextStart = at;
                        }
                        else if (c is '{' or '<' or '>') throw new InvalidLabel();
                        else if (!char.IsWhiteSpace(c)) text = true;
                    }
                    field.Text = DisplayText(Value[field.TextStart..at].Trim());
                }
                field.End = at; fields.Add(field); Record.Add(field);
                if (at == Value.Length || Value[at] != '|') break;
                at++;
            } while (true);
            return fields;
        }
        Read("", 0);
        if (at != Value.Length) throw new InvalidLabel();
        Fields = Record.OrderBy(field => field.Start).ThenByDescending(field => field.End).Select(field =>
            new DotRecordField(field.Path, field.Parent, field.Index, field.IsGroup, field.Text, field.Port,
                Offset(field.Start), Offset(field.End) - Offset(field.Start))).ToArray();
    }

    internal List<Field>? Siblings(string parent) => parent.Length == 0 ? Record.Where(field => field.Parent.Length == 0).OrderBy(field => field.Index).ToList() :
        Record.FirstOrDefault(field => field.Path == parent && field.IsGroup)?.Children;

    private void ParseHtml()
    {
        var roots = new List<Element>();
        var stack = new Stack<Element>();
        int at = 0, count = 0;
        string Name()
        {
            int start = at;
            while (at < Value.Length && (char.IsAsciiLetterOrDigit(Value[at]) || Value[at] is '_' or '-' or ':')) at++;
            if (start == at) throw new InvalidLabel();
            return Value[start..at];
        }
        void Space() { while (at < Value.Length && char.IsWhiteSpace(Value[at])) at++; }
        while (at < Value.Length)
        {
            if (Value[at] != '<') { at++; continue; }
            if (Value.AsSpan(at).StartsWith("<!--"))
            {
                int end = Value.IndexOf("-->", at + 4, StringComparison.Ordinal);
                if (end < 0) throw new InvalidLabel();
                at = end + 3; continue;
            }
            int start = at++;
            if (at == Value.Length || Value[at] is '!' or '?') throw new InvalidLabel();
            bool closing = Value[at] == '/'; if (closing) at++;
            string name = Name(); Space();
            if (closing)
            {
                if (at >= Value.Length || Value[at++] != '>' || stack.Count == 0) throw new InvalidLabel();
                var open = stack.Pop();
                if (!open.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidLabel();
                open.CloseStart = start; open.End = at; continue;
            }
            if (++count > 4096 || stack.Count >= 32) throw new InvalidLabel();
            var element = new Element(name.ToUpperInvariant(), start);
            while (at < Value.Length && Value[at] is not ('>' or '/'))
            {
                int attrStart = at;
                string attrName = Name(); Space();
                if (at >= Value.Length || Value[at++] != '=') throw new InvalidLabel();
                Space();
                if (at >= Value.Length || Value[at] is not ('\'' or '"')) throw new InvalidLabel();
                char quote = Value[at++]; int valueStart = at;
                while (at < Value.Length && Value[at] != quote) at++;
                if (at == Value.Length || element.Find(attrName) != null) throw new InvalidLabel();
                int valueEnd = at++;
                element.Attributes.Add(new(attrName, WebUtility.HtmlDecode(Value[valueStart..valueEnd]), attrStart, at, valueStart, valueEnd, quote));
                Space();
            }
            if (at >= Value.Length) throw new InvalidLabel();
            if (Value[at] == '/') { element.SelfClosing = true; at++; }
            if (at >= Value.Length || Value[at++] != '>') throw new InvalidLabel();
            element.OpenEnd = at;
            if (stack.Count > 0) stack.Peek().Children.Add(element); else roots.Add(element);
            Port(element.Value("PORT"));
            if (element.SelfClosing) element.CloseStart = element.End = at; else stack.Push(element);
            if (element.Name == "TABLE") HtmlTables.Add(new(element, HtmlTables.Count));
        }
        if (stack.Count != 0 || HtmlTables.Count == 0) throw new InvalidLabel();
        foreach (var table in HtmlTables) BuildTable(table);
        Tables = HtmlTables.Select(table => new DotTable(table.Index, table.Rows.Count, table.Columns, table.Rectangular,
            table.Cells.Select(cell => new DotTableCell(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan,
                HtmlText(Value[cell.Element.OpenEnd..cell.Element.CloseStart]), cell.Element.Value("PORT"), Offset(cell.Element.Start),
                Offset(cell.Element.End) - Offset(cell.Element.Start))).ToArray())).ToArray();
    }

    private void BuildTable(Table table)
    {
        var occupied = new HashSet<(int Row, int Column)>();
        table.Rows.AddRange(table.Element.Children);
        if (table.Rows.Count == 0 || table.Rows.Count > 128 || table.Rows.Any(row => row.Name != "TR" || row.SelfClosing || row.Children.Count == 0)) throw new InvalidLabel();
        int Span(Element element, string name)
        {
            var attr = element.Find(name);
            if (attr == null) return 1;
            return int.TryParse(attr.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int span) && span is >= 1 and <= 128 ? span : throw new InvalidLabel();
        }
        for (int row = 0; row < table.Rows.Count; row++)
        {
            int column = 0;
            foreach (var element in table.Rows[row].Children)
            {
                if (element.Name != "TD" || element.SelfClosing) throw new InvalidLabel();
                while (occupied.Contains((row, column))) column++;
                int rows = Span(element, "ROWSPAN"), columns = Span(element, "COLSPAN");
                if (row + rows > table.Rows.Count || column + columns > 128 || table.Cells.Count >= 1024) throw new InvalidLabel();
                for (int r = row; r < row + rows; r++) for (int c = column; c < column + columns; c++)
                    if (!occupied.Add((r, c))) throw new InvalidLabel();
                table.Cells.Add(new(element, row, column, rows, columns));
                column += columns; table.Columns = Math.Max(table.Columns, column);
            }
        }
        table.Rectangular = occupied.Count == table.Rows.Count * table.Columns;
    }

    internal static string HtmlText(string value)
    {
        var text = new StringBuilder(); bool tag = false; char quote = '\0';
        foreach (char c in value)
        {
            if (tag)
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '\'' or '"') quote = c;
                else if (c == '>') tag = false;
            }
            else if (c == '<') tag = true;
            else text.Append(c);
        }
        return WebUtility.HtmlDecode(text.ToString()).Trim();
    }

    private static string DisplayText(string value)
    {
        var text = new StringBuilder();
        for (int at = 0; at < value.Length; at++)
        {
            char c = value[at];
            if (c == '\\' && at + 1 < value.Length)
            { c = value[++at]; if (c is 'n' or 'l' or 'r') c = '\n'; }
            text.Append(c);
        }
        return text.ToString();
    }

    private int Offset(int at) => offsets.Length == Value.Length + 1 ? offsets[at] : -1;
    private static int[] CharacterOffsets(DotSyntax syntax, DotSyntax.Token token)
    {
        if (token.Start < 0) return [];
        if (token.Kind == DotSyntax.TokenKind.Html) return Enumerable.Range(token.Start + 1, token.Value.Length + 1).ToArray();
        if (token.Kind == DotSyntax.TokenKind.Identifier) return Enumerable.Range(token.Start, token.Value.Length + 1).ToArray();
        var positions = new List<int>(); int end = token.Start + 1;
        foreach (var part in syntax.Tokens.Where(part => part.Kind == DotSyntax.TokenKind.Quoted && part.Start >= token.Start && part.End <= token.End))
        {
            int at = part.Start + 1;
            while (at < part.End - 1)
            {
                int start = at; char c = syntax.Source[at++];
                if (c == '\\' && at < part.End - 1)
                {
                    char next = syntax.Source[at];
                    if (next is '\r' or '\n') { at++; if (next == '\r' && at < part.End - 1 && syntax.Source[at] == '\n') at++; continue; }
                    if (next == '"') { at++; positions.Add(start); end = at; continue; }
                }
                positions.Add(start); end = at;
            }
        }
        positions.Add(end);
        return positions.Count == token.Value.Length + 1 ? positions.ToArray() : [];
    }
}
