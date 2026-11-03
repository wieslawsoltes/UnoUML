using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>A strict recognizer for structural UML. Unknown grammar is delegated before any SVG is emitted.</summary>
internal static partial class GraphRenderer
{
    private sealed class Node(string kind, string alias, string label, int line, Node? parent = null)
    {
        public string Kind { get; } = kind;
        public string Alias { get; } = alias;
        public string Label { get; } = label;
        public int Line { get; } = line;
        public Node? Parent { get; } = parent;
        public string Qualified { get; } = parent?.Parent is null ? alias : parent.Qualified + "." + alias;
        public string Fill = "#f1f1f1", Stereotype = "";
        public List<(int Line, string Text)> Members { get; } = [];
        public List<Node> Children { get; } = [];
        public List<Arc> Arcs { get; } = [];
        public int X, Y, Width, Height;
        public int LabelSize = 14, MemberSize = 13, StereotypeSize = 12, HeaderHeight = 40, MemberHeight = 20;
        public string[] LabelRows = [label];
        public int LabelHeight = 14, SpotRadius = 10, SpotSize = 12;
        public int[] MemberHeights = [];
        public string[][] MemberRows = [];
        public StyleSpacing Padding = new(13, 17, 13, 17), Margin;
        public bool Implicit, ExplicitFill, DeploymentGlyph, CaptionBelow;
        public bool IsStatePort => Kind == "state" && Stereotype is ("entryPoint" or "exitPoint" or "inputPin" or "outputPin");
        public bool IsInputPort => Stereotype is "entryPoint" or "inputPin";
        public bool IsSpecial => Kind == "state" && Stereotype is "choice" or "fork" or "join" or "history" or "history*" or "start" or "end";
        public bool IsType => !DeploymentGlyph && Kind is ("class" or "abstract class" or "abstract" or "interface" or "annotation" or "enum" or "entity" or "object" or "struct" or "exception" or "protocol" or "metaclass" or "stereotype" or "map");
    }

    private sealed record Edge(Node From, Node To, string Arrow, string Caption, string Tail, string Head, int Line, string Direction, bool Hidden, EdgePaint? Paint = null);
    private sealed record Arc(int A, int B, string Direction, bool Hidden, int LabelWidth = 0, int LabelHeight = 0);
    private sealed record Pending(Node Scope, string From, string To, string Arrow, string Caption, string Tail, string Head, int Line, string Direction, bool Hidden, EdgePaint? Paint);
    private sealed class Graph
    {
        public readonly ManagedSkin Skin = new();
        public readonly Node Root = new("root", "", "", -1);
        public readonly List<Edge> Edges = [];
        public readonly List<Node> Nodes = [];
        public string Title = "", LegendAlign = "center";
        public StyleTitle? Heading;
        public int LegendLine = -1;
        public List<string> Legend = [];
        public bool Horizontal, HideCircle, IsState;
    }

    private static readonly string[] DirectionNames = ["left", "right", "up", "down"];
    private const string Identifier = @"[\p{L}_$][\p{L}\p{N}_$]*";
    private const string Name = "(?:\"[^\"\\r\\n]+\"|" + Identifier + ")";
    private const string Terminal = "(?:" + Name + "|\\[\\*\\]|" + Identifier + @"(?:\." + Identifier + ")+)";
    private const string Kinds = "archimate|abstract class|abstract|class|struct|exception|protocol|metaclass|stereotype|object|interface|annotation|enum|entity|map|component|database|rectangle|actor/|actor|usecase/|usecase|state|package|namespace|node|artifact|folder|action|agent|boundary|card|circle|cloud|collections|control|file|frame|hexagon|label|person|process|queue|stack|storage|note";

    private const string DeclarationStereotypes = @"(?:\s*<<(?<stereotype>[^<>\r\n]+)>>)*";
    [GeneratedRegex("^(?<kind>" + Kinds + @")\s+(?:(?<fill>#[\w]+)\s+)?(?<name>" + Name + ")" + DeclarationStereotypes +
        @"(?:\s+as\s+(?<alias>" + Name + "))?" + DeclarationStereotypes + @"(?:\s+(?<fill>#[\w]+))?\s*(?<open>\{)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationPattern();
    [GeneratedRegex("^(" + Terminal + ")\\s*(?:\"([^\"]*)\"\\s*)?([<|o*]*(?>[-.]+)(?:\\[[a-zA-Z0-9#,=. ]+\\])?(?:left|right|up|down)?[-.]*[>|o*]*)\\s*(?:\"([^\"]*)\"\\s*)?(" + Terminal + ")(?:\\s*:\\s*(.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex EdgePattern();

    [GeneratedRegex("^(" + Terminal + @")\s*:\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DescriptionPattern();

    internal static string? TryRender(string source, out bool isState)
    {
        isState = false;
        if (source.Length > 1_000_000) return null;
        var graph = Parse(source);
        if (graph is null || !Arrange(graph)) return null;
        isState = graph.IsState;
        return Draw(graph);
    }

    private static Graph? Parse(string source)
    {
        var graph = new Graph();
        if (!ModernStyle.TryExtract(source, sequence: false, graph.Skin, out source)) return null;
        var aliases = new Dictionary<string, Node>(StringComparer.Ordinal);
        var pending = new List<Pending>();
        var descriptions = new List<(Node Scope, string Id, string Text, int Line)>();
        var parent = graph.Root;
        Node? body = null;
        bool opened = false, closed = false, structural = false, inLegend = false;
        int depth = 0, line = -1;
        string? skinBlock = null;
        foreach (var span in source.AsSpan().EnumerateLines())
        {
            line++;
            var trimmed = span.Trim();
            if (line == 0) trimmed = trimmed.TrimStart('\uFEFF');
            if (trimmed.IsEmpty || trimmed[0] == '\'') continue;
            if (trimmed.Length > 4096 || closed) return null;
            if (!opened)
            {
                if (!trimmed.SequenceEqual("@startuml")) return null;
                opened = true; continue;
            }
            if (inLegend)
            {
                if (trimmed.SequenceEqual("endlegend")) { inLegend = false; continue; }
                string legendLine = trimmed.ToString();
                if (!Plain(legendLine) || graph.Legend.Count >= 32 || legendLine[0] == '@') return null;
                graph.Legend.Add(legendLine); continue;
            }
            if (skinBlock != null || trimmed.StartsWith("skinparam ", StringComparison.OrdinalIgnoreCase))
            {
                if (body != null || !graph.Skin.Read(trimmed.ToString(), ref skinBlock, sequence: false, sourceLine: line)) return null;
                continue;
            }
            if (trimmed.SequenceEqual("@enduml")) { closed = true; continue; }
            if (trimmed.SequenceEqual("}"))
            {
                if (body is not null) body = null;
                else if (parent != graph.Root) { parent = parent.Parent!; depth--; }
                else return null;
                continue;
            }
            var text = trimmed.ToString();
            if (body is not null)
            {
                if (body.Members.Count >= 256) return null;
                if (body.Kind == "map")
                {
                    var parts = text.Split("=>", StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || parts.Any(p => p.Length == 0 || !Plain(p))) return null;
                }
                else if (!PlainMember(text)) return null;
                body.Members.Add((line, text));
                continue;
            }
            if (text.StartsWith("title ", StringComparison.Ordinal) && graph.Title.Length == 0)
            {
                graph.Title = text[6..].Trim();
                if (!Plain(graph.Title)) return null;
                continue;
            }
            if (text is "legend" or "legend left" or "legend right" or "legend center")
            {
                if (graph.LegendLine >= 0) return null;
                graph.LegendLine = line; graph.LegendAlign = text.Length > 7 ? text[7..] : "center";
                inLegend = true; continue;
            }
            if (text == "left to right direction") { graph.Horizontal = true; continue; }
            if (text == "top to bottom direction") { graph.Horizontal = false; continue; }
            if (text == "hide circle") { graph.HideCircle = true; continue; }
            var declaration = DeclarationPattern().Match(text);
            if (declaration.Success)
            {
                string kind = declaration.Groups["kind"].Value, name = declaration.Groups["name"].Value, second = declaration.Groups["alias"].Value;
                string label = Unquote(name), alias = second.Length > 0 ? Unquote(second) : label;
                if (second.StartsWith('"') && !name.StartsWith('"')) (label, alias) = (Unquote(second), name);
                var stereotypes = declaration.Groups["stereotype"].Captures.Select(c => c.Value).ToArray();
                if (stereotypes.Length > 16 || !stereotypes.All(Plain)) return null;
                string stereo = string.Join(">><<", stereotypes);
                if (!Plain(label) || !Plain(alias) || alias.Contains('.') || graph.Nodes.Count >= 1800) return null;
                var node = new Node(kind, alias, label, line, parent) { Stereotype = stereo };
                if (!aliases.TryAdd(alias, node)) return null;
                if (kind == "note") node.Fill = "#fff6bd";
                if (kind == "state")
                {
                    graph.IsState = true;
                    if (stereo is "expansionInput" or "expansionOutput" || node.IsStatePort && parent.Kind != "state")
                        return null; // A root-level border port is invalid PlantUML.
                }
                if (node.Qualified != alias && !aliases.TryAdd(node.Qualified, node)) return null;
                if (declaration.Groups["fill"].Success)
                {
                    if (ManagedSkin.Color(declaration.Groups["fill"].Value) is not { } color) return null;
                    node.Fill = color; node.ExplicitFill = true;
                }
                graph.Nodes.Add(node); parent.Children.Add(node);
                if (declaration.Groups["open"].Success)
                {
                    if (node.IsType) body = node;
                    else if (!node.IsSpecial && !node.IsStatePort && kind is ("package" or "namespace" or "node" or "rectangle" or "folder" or "frame" or "state")) { if (++depth > 12) return null; parent = node; }
                    else return null;
                }
                // actor, entity and database are legal sequence participants as well.
                structural |= kind is not ("actor" or "database" or "entity");
                continue;
            }
            var edge = EdgePattern().Match(text);
            if (!edge.Success)
            {
                var description = DescriptionPattern().Match(text);
                if (!description.Success || !Plain(description.Groups[2].Value)) return null;
                descriptions.Add((parent, Unquote(description.Groups[1].Value), description.Groups[2].Value, line));
                continue;
            }
            if (pending.Count >= 6000) return null;
            var rawArrow = edge.Groups[3].Value;
            bool hidden = false;
            EdgePaint? paint = null;
            int bracket = rawArrow.IndexOf('[');
            if (bracket >= 0)
            {
                int close = rawArrow.IndexOf(']',bracket);
                var modifiers = rawArrow[(bracket + 1)..close];
                if (modifiers == "hidden") hidden = true;
                else if (!EdgePaint.TryParse(modifiers, out paint)) return null;
                rawArrow = rawArrow.Remove(bracket, close - bracket + 1);
            }
            var direction = DirectionNames.FirstOrDefault(rawArrow.Contains) ?? "";
            var arrow = rawArrow;
            if (direction.Length != 0) arrow = arrow.Replace(direction, "");
            if (!SupportedArrow(arrow)) return null;
            string tail = edge.Groups[2].Value, head = edge.Groups[4].Value, caption = edge.Groups[6].Value;
            if (!Plain(tail) || !Plain(head) || !Plain(caption)) return null;
            if (edge.Groups[1].Value == "[*]" || edge.Groups[5].Value == "[*]") graph.IsState = true;
            pending.Add(new(parent, Unquote(edge.Groups[1].Value), Unquote(edge.Groups[5].Value), arrow, caption, tail, head, line, direction, hidden, paint));
        }
        if (!opened || !closed || inLegend || skinBlock != null || depth != 0 || body is not null || (!structural && !graph.IsState)) return null;
        bool architecture = graph.Nodes.Any(n => !n.IsType && n.Kind is not ("package" or "namespace" or "note"))
            && !graph.Nodes.Any(n => n.IsType && n.Kind is not ("interface" or "entity"));
        graph.Skin.Diagram = graph.IsState ? "statediagram" : architecture ? "componentdiagram" :
            graph.Nodes.Any(n => n.Kind == "object") && !graph.Nodes.Any(n => n.IsType && n.Kind != "object") ? "objectdiagram" : "classdiagram";
        if (architecture)
            foreach (var node in graph.Nodes.Where(n => n.Kind is "interface" or "entity" && n.Members.Count == 0))
                node.DeploymentGlyph = true;
        if (graph.IsState && graph.Nodes.Any(n => n.Kind is not ("state" or "package" or "note"))) return null;
        if (!graph.IsState && descriptions.Count != 0) return null;

        Node? Resolve(string id, Node scope, bool start, int sourceLine)
        {
            bool pseudo = id == "[*]";
            string key = pseudo ? scope.Qualified + (start ? "\0initial" : "\0final") : id;
            if (aliases.TryGetValue(key, out var found)) return found;
            if (!graph.IsState || graph.Nodes.Count >= 1800 || (!pseudo && (id.Contains('.') || !Plain(id)))) return null;
            var node = new Node(pseudo ? (start ? "initial" : "final") : "state", pseudo ? "" : id, pseudo ? "" : id, sourceLine, scope) { Implicit = true };
            aliases[key] = node;
            if (!pseudo && node.Qualified != id && !aliases.TryAdd(node.Qualified, node)) return null;
            scope.Children.Add(node); graph.Nodes.Add(node);
            return node;
        }
        foreach (var description in descriptions)
        {
            var node = Resolve(description.Id, description.Scope, true, description.Line);
            if (node is null || node.Kind != "state" || node.IsSpecial || node.IsStatePort || node.Children.Count > 0 || node.Members.Count >= 256) return null;
            node.Members.Add((description.Line, description.Text));
        }
        foreach (var syntax in pending)
        {
            var from = Resolve(syntax.From, syntax.Scope, true, syntax.Line);
            var to = Resolve(syntax.To, syntax.Scope, false, syntax.Line);
            if (from is null || to is null) return null;
            graph.Edges.Add(new(from, to, syntax.Arrow, syntax.Caption, syntax.Tail, syntax.Head, syntax.Line, syntax.Direction, syntax.Hidden, syntax.Paint));
        }
        return graph.Nodes.Count == 0 ? null : graph;
    }

    private static bool SupportedArrow(string token) => token is "--" or "-" or ".." or "-->" or "->" or "<--" or "<-" or "<-->" or "<->"
        or "..>" or "<.." or "<..>" or "--|>" or "<|--" or "..|>" or "<|.." or "*--" or "--*" or "o--" or "--o" or "-->>" or "->>" or "<<--" or "<<-";
    private static string Unquote(string value) => value.StartsWith('"') ? value[1..^1] : value;
    private static bool Plain(string text) => SvgText.IsSupported(text) && !text.Any(char.IsControl);
    private static bool PlainMember(string text) => Plain(text) && !text.StartsWith('!') && !text.StartsWith('@') && !text.Contains('{') && !text.Contains('}')
        && !text.StartsWith("--", StringComparison.Ordinal) && !text.StartsWith("..", StringComparison.Ordinal) && !text.StartsWith("==", StringComparison.Ordinal)
        && !text.StartsWith("__", StringComparison.Ordinal) && !text.StartsWith("/'", StringComparison.Ordinal);
}
