using System.Text.RegularExpressions;

namespace UnoUML.Core;

/// <summary>One independently renderable @start / @end block. Line numbers are zero-based and inclusive.</summary>
public sealed record DiagramPage(string Title, string Family, string Source, int StartLine, int EndLine);

/// <summary>A source-backed outline entry. Edits are accepted only on objects from the current index.</summary>
public sealed class DiagramObject
{
    private readonly string _document;
    internal DiagramObject(string source, int start, int end, int line, int endLine, int parent, string kind, string label)
    {
        _document = source;
        Offset = start;
        End = end;
        Line = line;
        EndLine = endLine;
        ParentLine = parent;
        Kind = kind;
        Label = label;
    }

    public string Kind { get; }
    public string Label { get; internal set; }
    public string Alias { get; internal set; } = "";
    public int Line { get; }
    public int EndLine { get; }
    public string Statement => _document[Offset..End];
    public bool IsConnectable => Alias.Length > 0 && !Ambiguous;
    public bool CanRename => LabelAt >= 0 && !Ambiguous;
    internal bool Ambiguous { get; set; }
    internal bool PlainName { get; set; }
    internal bool Unclosed { get; set; }
    internal int Offset { get; }
    internal int End { get; }
    internal int ParentLine { get; }
    internal int LabelAt { get; set; } = -1;
    internal int LabelLength { get; set; }
    internal int AliasAt { get; set; } = -1;
    internal int AliasLength { get; set; }
    internal string From { get; set; } = "";
    internal string To { get; set; } = "";
}

internal sealed partial class DiagramIndex
{
    private readonly string _source;
    internal readonly record struct TextLine(int Start, int End, int Next, string Code);
    internal List<TextLine> Lines { get; } = [];
    internal List<DiagramPage> Pages { get; } = [];
    internal string NewLine { get; }
    private readonly Dictionary<int, List<DiagramObject>> _cache = [];

    // These match only an unambiguous editable prefix. Everything else stays a verbatim source entry.
    private const string Token = """(?:"(?:\\.|[^"\\])*"|[\p{L}_$][\w.$]*)""";
    private const string Endpoint = """(?:"(?:\\.|[^"\\])*"|\[\*\]|\[[^\]\r\n]+\]|\([^()\r\n]+\)|[\p{L}_$][\w.$]*)""";
    private const string Kinds = "abstract class|abstract|annotation|metaclass|protocol|stereotype|exception|struct|class|interface|enum|entity|object|participant|actor/|actor|boundary|control|database|collections|queue|usecase/|usecase|component|artifact|cloud|node|package|namespace|rectangle|storage|folder|file|frame|card|agent|person|stack|hexagon|circle|action|process|label|state|concise|robust|clock|binary|analog";

    [GeneratedRegex(@"^\s*(?<kind>" + Kinds + @")\s+(?<name>" + Token + @")(?:\s+as\s+(?<alias>" + Token + @"))?(?=$|[\s<{#])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^\s*(?<from>" + Endpoint + """)\s*(?:"[^"]*"\s*)?(?<arrow>[<|o*x+#^}({]*[-.=/\\]+(?:\[[^\]]+\]|left|right|up|down|[lrud])?[-.=/\\]*[>|o*x+#^}{)]*)\s*(?:"[^"]*"\s*)?(?<to>""" + Endpoint + @")\s*(?::\s*(?<label>.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Relation();

    [GeneratedRegex(@"^\s*@(?<tag>start|end)(?<dialect>\w+)(?:\s+(?<name>.*?))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Boundary();

    public DiagramIndex(string source)
    {
        _source = source;
        bool inComment = false;
        string? newline = null;
        for (int start = 0; ; )
        {
            int stop = source.AsSpan(start).IndexOfAny('\r', '\n');
            int end = stop < 0 ? source.Length : start + stop;
            int next = end;
            if (next < source.Length)
            {
                next += source[next] == '\r' && next + 1 < source.Length && source[next + 1] == '\n' ? 2 : 1;
                newline ??= source[end..next];
            }
            Lines.Add(new(start, end, next, MaskComments(source[start..end], ref inComment)));
            if (stop < 0) break;
            start = next;
        }
        NewLine = newline ?? "\n";
        int first = -1;
        string dialect = "uml", name = "";
        for (int line = 0; line < Lines.Count; line++)
        {
            string code = Lines[line].Code.TrimStart('\uFEFF', ' ', '\t');
            if (!code.StartsWith('@')) continue;
            var match = Boundary().Match(code);
            if (!match.Success) continue;
            string language = match.Groups["dialect"].Value.ToLowerInvariant();
            if (match.Groups["tag"].Value.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                if (first != -1) AddPage(first, line - 1, dialect, name);
                first = line;
                dialect = language;
                name = match.Groups["name"].Value;
            }
            else if (first >= 0 && language == dialect)
            {
                AddPage(first, line, dialect, name);
                first = -1;
            }
        }
        if (first >= 0) AddPage(first, Lines.Count - 1, dialect, name);
        if (Pages.Count == 0) AddPage(0, Lines.Count - 1, "uml", "");
    }

    private static string MaskComments(string line, ref bool block)
    {
        // Keep offsets stable for all following tokens. Ignore comment markers inside display strings.
        bool quoted = false;
        char[]? masked = null;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (block)
            {
                masked ??= line.ToCharArray();
                masked[i] = ' ';
                if (c == '\'' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    masked[++i] = ' ';
                    block = false;
                }
            }
            else if (quoted && c == '\\' && i + 1 < line.Length) i++;
            else if (c == '"') quoted = !quoted;
            else if (!quoted && c == '/' && i + 1 < line.Length && line[i + 1] == '\'')
            {
                masked ??= line.ToCharArray();
                masked[i] = masked[++i] = ' ';
                block = true;
            }
            else if (!quoted && c == '\'' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return masked == null ? line[..i] : new string(masked, 0, i);
            }
        }
        return masked == null ? line : new string(masked);
    }

    private void AddPage(int first, int last, string dialect, string name)
    {
        string family = dialect == "uml" ? "UML" : StudioCatalog.FamilyName(dialect);
        for (int i = first; i <= last; i++)
        {
            string s = Lines[i].Code.Trim();
            if (s.StartsWith("title ", StringComparison.OrdinalIgnoreCase)) name = s[6..].Trim();
            if (family == "UML") family = DetectFamily(s);
        }
        string title = string.IsNullOrWhiteSpace(name) ? $"{family} diagram" : name;
        Pages.Add(new(title, family, _source[Lines[first].Start..Lines[last].End], first, last));
    }

    private static string DetectFamily(string s)
    {
        int sep = s.IndexOfAny([' ', '\t']);
        string word = (sep >= 0 ? s[..sep] : s).ToLowerInvariant();
        if (s.StartsWith("!include <C4/", StringComparison.OrdinalIgnoreCase)) return "C4";
        if (s.StartsWith("!include <archimate/", StringComparison.OrdinalIgnoreCase)) return "ArchiMate";
        return word switch
        {
            "class" or "interface" or "abstract" or "annotation" or "enum" or "struct" or "namespace" => "Class",
            "participant" or "autonumber" or "activate" or "lifeline" or "alt" => "Sequence",
            "component" => "Component",
            "package" => "UML", // used in several grammars
            "object" or "map" => "Object",
            "state" or "hide empty description" => "State",
            "usecase" => "Use case",
            "node" or "artifact" or "cloud" or "storage" => "Deployment",
            "concise" or "robust" or "clock" or "binary" or "analog" => "Timing",
            "start" or "stop" or "fork" or "if" => "Activity",
            "nwdiag" => "Network",
            "salt" => "Wireframe",
            "entity" => "Class",
            _ when s.StartsWith("[*]", StringComparison.Ordinal) => "State",
            _ when s.StartsWith(':') && s.EndsWith(';') => "Activity",
            _ => "UML"
        };
    }

    internal List<DiagramObject> Objects(int pageIndex)
    {
        if (_cache.TryGetValue(pageIndex, out var cached)) return cached;
        var page = Pages[pageIndex];
        var result = new List<DiagramObject>();
        var stack = new Stack<(int Line, string End, bool Opaque)>();
        var ends = new Dictionary<int, int>();
        var parents = new Dictionary<int, int>();
        var unclosed = new HashSet<int>();
        for (int i = page.StartLine; i <= page.EndLine; i++)
        {
            string code = Lines[i].Code.Trim();
            if (code.Length == 0 || code.StartsWith("@start", StringComparison.OrdinalIgnoreCase) || code.StartsWith("@end", StringComparison.OrdinalIgnoreCase)) continue;
            if (stack.TryPeek(out var context))
            {
                if (code.Equals(context.End, StringComparison.OrdinalIgnoreCase) ||
                    (context.End.StartsWith("end", StringComparison.Ordinal) &&
                     code.StartsWith("end ", StringComparison.OrdinalIgnoreCase) &&
                     code.AsSpan(4).Equals(context.End.AsSpan(3), StringComparison.OrdinalIgnoreCase)))
                {
                    ends[stack.Pop().Line] = i;
                    continue;
                }
                if (context.Opaque) continue;
            }
            parents[i] = stack.TryPeek(out var parent) ? parent.Line : -1;
            if (BlockEnd(code, page.Family) is { } block) stack.Push((i, block.End, block.Opaque));
        }
        foreach (var pending in stack) unclosed.Add(pending.Line);
        var aliases = new Dictionary<string, DiagramObject>(StringComparer.Ordinal);
        foreach (var (line, parent) in parents)
        {
            int end = ends.GetValueOrDefault(line, line);
            var item = ParseObject(line, end, parent, page.Family);
            item.Unclosed = unclosed.Contains(line);
            if (item.Alias.Length > 0 && !aliases.TryAdd(item.Alias, item))
            {
                item.Ambiguous = aliases[item.Alias].Ambiguous = true;
            }
            result.Add(item);
        }
        return _cache[pageIndex] = result;
    }

    private static (string End, bool Opaque)? BlockEnd(string code, string family)
    {
        int sep = code.IndexOfAny([' ', '\t']);
        string token = (sep < 0 ? code : code[..sep]).ToLowerInvariant();
        if (code.EndsWith('{')) return ("}", false);
        if (code.EndsWith('[') && Declaration().IsMatch(code)) return ("]", true);
        if (token is "note" or "hnote" or "rnote")
        {
            // A declaration with a colon or quoted display text is entirely on one line.
            if (!code.Contains(':') && !code.Contains('"')) return ("end" + token, true);
        }
        if (code.Equals("<style>", StringComparison.OrdinalIgnoreCase)) return ("</style>", true);
        if (code is "title" or "header" or "footer" or "legend" or "caption") return ("end" + code, true);
        if (token == "!startsub") return ("!endsub", true);
        if ((token is "!procedure" or "!function") && !code.Contains("!return", StringComparison.Ordinal)) return ("!end" + token[1..], true);
        if (family is "Sequence" or "UML")
        {
            if (token is "alt" or "loop" or "opt" or "par" or "group" or "break" or "critical") return ("end", false);
            if (token == "box") return ("end box", false);
            if (token == "ref" && !code.Contains(':')) return ("end ref", true);
        }
        return null;
    }

    private DiagramObject ParseObject(int line, int end, int parent, string family)
    {
        var row = Lines[line];
        string code = row.Code.TrimEnd();
        string text = code.TrimStart();
        DiagramObject New(string kind, string label) => new(_source, row.Start, Lines[end].End, line, end, parent, kind, label);
        if (Declaration().Match(code) is { Success: true } declaration)
        {
            var name = declaration.Groups["name"];
            var alias = declaration.Groups["alias"];
            var item = New(declaration.Groups["kind"].Value, Unquote(name.Value));
            if (alias.Success && alias.Value.StartsWith('"') && !name.Value.StartsWith('"')) (name, alias) = (alias, name);
            item.Label = Unquote(name.Value);
            bool simple = !name.Value.StartsWith('"');
            if (alias.Success || simple)
            {
                item.Alias = alias.Success ? alias.Value : name.Value;
                item.AliasAt = alias.Success ? alias.Index : name.Index;
                item.AliasLength = alias.Success ? alias.Length : name.Length;
                item.LabelAt = name.Index;
                item.LabelLength = name.Length;
                item.PlainName = !alias.Success;
            }
            return item;
        }
        if (Relation().Match(code) is { Success: true } relation)
        {
            var label = relation.Groups["label"];
            var item = New("Relationship", label.Success ? label.Value : text);
            item.From = relation.Groups["from"].Value;
            item.To = relation.Groups["to"].Value;
            item.LabelAt = label.Success ? label.Index : code.Length;
            item.LabelLength = label.Length;
            return item;
        }
        int indent = code.Length - text.Length;
        if (family == "Activity" && text.StartsWith(':') && text.EndsWith(';'))
        {
            var item = New("Activity", text[1..^1]);
            item.LabelAt = indent + 1;
            item.LabelLength = text.Length - 2;
            return item;
        }
        if ((family is "Mind map" or "WBS") && text.StartsWith('*'))
        {
            int at = 0;
            while (at < text.Length && text[at] == '*') at++;
            if (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
                var item = New("Topic", text[at..]);
                item.LabelAt = indent + at;
                item.LabelLength = text.Length - at;
                return item;
            }
        }
        var separator = text.IndexOfAny([' ', '\t']);
        string keyword = separator < 0 ? text : text[..separator];
        string kind = keyword.ToLowerInvariant() switch
        {
            "note" or "rnote" or "hnote" => "Note",
            "skinparam" or "<style>" => "Style",
            "title" or "legend" or "header" or "caption" or "footer" => "Caption",
            "!include" or "!include_once" or "!include_many" => "Library",
            "alt" or "loop" or "opt" or "group" or "par" or "box" or "critical" => "Group",
            _ => "Source"
        };
        return New(kind, text);
    }

    private static string Unquote(string name) => name.StartsWith('"') && name.EndsWith('"') ? name[1..^1].Replace("\\\"", "\"") : name;
}
