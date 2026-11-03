using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private sealed class DotFailure : Exception;
    private static DotFailure InvalidDot() => new();

    private enum DotTokenKind { Symbol, Id, Quoted, Html }
    private readonly record struct DotToken(string Value, int Start, int Length, int Line, int EndLine, DotTokenKind Kind, string? Source = null, bool Composite = false)
    {
        internal int End => Start + Length;
        internal bool Is(string value) => Kind == DotTokenKind.Symbol && Value == value;
        internal bool Keyword(string value) => Kind == DotTokenKind.Id && Value.Equals(value, StringComparison.OrdinalIgnoreCase);
        internal bool Identifier => Kind is DotTokenKind.Quoted or DotTokenKind.Html || Kind == DotTokenKind.Id &&
            !(Keyword("strict") || Keyword("graph") || Keyword("digraph") || Keyword("node") || Keyword("edge") || Keyword("subgraph"));
        internal static DotToken Literal(string value) => new(value, -1, 0, -1, -1, DotTokenKind.Quoted);
    }

    private sealed class DotAttributes : Dictionary<string, DotToken>
    {
        internal DotAttributes() : base(StringComparer.Ordinal) { }
        internal DotAttributes(DotAttributes source) : base(source, StringComparer.Ordinal) { }
        internal string Get(string name, string fallback = "") => TryGetValue(name, out var value) ? value.Value : fallback;
        internal DotToken Token(string name, string fallback = "") => TryGetValue(name, out var value) ? value : DotToken.Literal(fallback);
        internal void Apply(DotAttributes values) { foreach (var (key, value) in values) this[key] = value; }
    }

    private readonly record struct DotSpan(int Start, int End, int Line, int EndLine)
    {
        internal static DotSpan From(DotToken first, DotToken last) => new(first.Start, last.End, first.Line, last.EndLine);
    }

    private sealed class DotScope(string id, DotScope? parent, DotSpan span)
    {
        internal readonly string Id = id;
        internal readonly DotScope? Parent = parent;
        internal readonly DotAttributes Attributes = parent == null ? new() : new(parent.Attributes);
        internal readonly DotAttributes NodeDefaults = parent == null ? new() : new(parent.NodeDefaults);
        internal readonly DotAttributes EdgeDefaults = parent == null ? new() : new(parent.EdgeDefaults);
        internal readonly List<DotScope> Children = [];
        internal readonly HashSet<string> Nodes = new(StringComparer.Ordinal);
        internal DotSpan Span = span;
        internal DotToken? LabelToken;
        internal int Insert;
        internal bool IsCluster => Parent != null && (Id.StartsWith("cluster", StringComparison.Ordinal) || DotBool(Attributes.Get("cluster")));
        internal Node? Box;
        internal DotVisual? Visual;
    }

    private sealed class DotNode(string id, DotAttributes attributes, DotScope scope, DotSpan span, DotToken nameToken)
    {
        internal readonly string Id = id;
        internal readonly DotToken NameToken = nameToken;
        internal readonly DotAttributes Attributes = new(attributes);
        internal DotScope Scope = scope;
        internal DotSpan Span = span;
        internal DotToken? LabelToken;
        internal int Insert;
        internal bool Explicit;
        internal Node Box = null!;
        internal DotVisual Visual = null!;
    }

    private readonly record struct DotEndpoint(DotNode Node, string Port, string Compass, DotSpan Span, bool Literal);
    private sealed class DotEdge(DotEndpoint from, DotEndpoint to, DotToken arrow, DotAttributes defaults, DotSpan span, int index)
    {
        internal DotEndpoint From = from, To = to;
        internal DotToken Arrow = arrow;
        internal readonly DotAttributes Attributes = new(defaults);
        internal DotSpan Span = span;
        internal readonly int Index = index;
        internal DotToken? LabelToken;
        internal int Insert;
        internal bool Merged;
        internal Edge Layout = null!;
        internal DotVisual Visual = null!;
        internal DotLabel? HeadLabel, TailLabel, ExternalLabel;
    }

    private sealed class DotDocument(DotScope root, bool directed, bool strict)
    {
        internal readonly DotScope Root = root;
        internal readonly bool Directed = directed, Strict = strict;
        internal readonly Dictionary<string, DotNode> Nodes = new(StringComparer.Ordinal);
        internal readonly List<DotEdge> Edges = [];
        internal readonly List<DotScope> Scopes = [root];
        internal readonly Graph Graph = new();
        internal readonly Dictionary<Edge, DotEdge> EdgeMap = new(ReferenceEqualityComparer.Instance);
        internal string RankDirection = "TB", Splines = "spline";
        internal double Scale = 1;
        internal int Padding = 4;
    }

    internal static string? TryRenderDot(string source)
    {
        if (source.Length > 1_048_576) return null;
        try
        {
            var document = new DotParser(source).Parse();
            return ArrangeDot(document) ? DrawDot(document) : null;
        }
        catch (DotFailure) { return null; }
        catch (System.Xml.XmlException) { return null; }
        catch (OverflowException) { return null; }
    }

    private sealed class DotParser
    {
        private readonly List<DotToken> tokens;
        private readonly Dictionary<string, DotScope> namedScopes = new(StringComparer.Ordinal);
        private readonly Dictionary<(string From, string To), DotEdge> strictEdges = [];
        private DotDocument document = null!;
        private int position, anonymous;
        internal DotParser(string source) => tokens = LexDot(source);
        private DotToken Current => position < tokens.Count ? tokens[position] : throw InvalidDot();
        private DotToken Previous => tokens[position - 1];
        private bool At(string symbol) => position < tokens.Count && tokens[position].Is(symbol);
        private bool Eat(string symbol) { if (!At(symbol)) return false; position++; return true; }
        private bool Keyword(string keyword)
        {
            if (position == tokens.Count || !tokens[position].Keyword(keyword)) return false;
            position++; return true;
        }
        private void Require(string symbol) { if (!Eat(symbol)) throw InvalidDot(); }
        private DotToken Name()
        {
            var token = Current;
            if (!token.Identifier) throw InvalidDot();
            position++;
            if (token.Kind != DotTokenKind.Quoted) return token;
            if (!At("+")) return token;
            var value = new StringBuilder(token.Value);
            do
            {
                position++;
                var next = Current;
                if (next.Kind != DotTokenKind.Quoted) throw InvalidDot();
                position++;
                value.Append(next.Value);
                if (value.Length > 65536) throw InvalidDot();
                token = token with { Length = next.End - token.Start, EndLine = next.EndLine };
            } while (At("+"));
            return token with { Value = value.ToString(), Composite = true };
        }

        internal DotDocument Parse()
        {
            if (tokens.Count == 0) throw InvalidDot();
            bool strict = Keyword("strict"), directed = Keyword("digraph");
            if (!directed && !Keyword("graph")) throw InvalidDot();
            string id = Current.Identifier ? Name().Value : "";
            var first = Current;
            Require("{");
            var root = new DotScope(id, null, DotSpan.From(first, first));
            document = new(root, directed, strict);
            Statements(root, 0);
            if (position != tokens.Count) throw InvalidDot();
            return document;
        }

        private void Statements(DotScope scope, int depth)
        {
            if (depth > 48) throw InvalidDot();
            while (!At("}"))
            {
                if (Eat(";") || Eat(",")) continue;
                var first = Current;
                if (Keyword("graph") || Keyword("node") || Keyword("edge"))
                {
                    string category = first.Value.ToLowerInvariant();
                    if (!At("[")) throw InvalidDot();
                    var attributes = Attributes();
                    (category == "graph" ? scope.Attributes : category == "node" ? scope.NodeDefaults : scope.EdgeDefaults).Apply(attributes);
                    if (category == "graph" && attributes.TryGetValue("label", out var label)) scope.LabelToken = label;
                    continue;
                }
                if (first.Identifier && position + 1 < tokens.Count && tokens[position + 1].Is("="))
                {
                    string key = Name().Value;
                    Require("=");
                    var value = Name();
                    scope.Attributes[key] = value;
                    if (key == "label") scope.LabelToken = value;
                    continue;
                }
                bool subgraph = At("{") || Current.Keyword("subgraph");
                var endpoints = Endpoints(scope, depth);
                var added = new List<DotEdge>();
                while (At("->") || At("--"))
                {
                    var arrow = Current;
                    position++;
                    if (arrow.Value != (document.Directed ? "->" : "--")) throw InvalidDot();
                    var next = Endpoints(scope, depth);
                    if ((long)endpoints.Count * next.Count + document.Edges.Count > 6000) throw InvalidDot();
                    foreach (var from in endpoints)
                        foreach (var to in next)
                        {
                            var key = (from.Node.Id, to.Node.Id);
                            if (!document.Directed && StringComparer.Ordinal.Compare(key.Item1, key.Item2) > 0) key = (key.Item2, key.Item1);
                            if (!document.Strict || !strictEdges.TryGetValue(key, out var edge))
                            {
                                edge = new(from, to, arrow, scope.EdgeDefaults, DotSpan.From(first, Previous), document.Edges.Count);
                                document.Edges.Add(edge);
                                if (document.Strict) strictEdges.Add(key, edge);
                            }
                            else
                            {
                                edge.Merged = true;
                                var firstEndpoint = edge.From.Node == from.Node ? from : to;
                                var secondEndpoint = edge.From.Node == from.Node ? to : from;
                                edge.From = firstEndpoint.Port.Length == 0 && firstEndpoint.Compass.Length == 0 ? firstEndpoint with { Port = edge.From.Port, Compass = edge.From.Compass } : firstEndpoint;
                                edge.To = secondEndpoint.Port.Length == 0 && secondEndpoint.Compass.Length == 0 ? secondEndpoint with { Port = edge.To.Port, Compass = edge.To.Compass } : secondEndpoint;
                                edge.Arrow = arrow;
                            }
                            added.Add(edge);
                        }
                    endpoints = next;
                }
                var explicitAttributes = Attributes();
                int insert = Previous.End;
                var span = DotSpan.From(first, Previous);
                if (added.Count == 0)
                {
                    if (subgraph)
                    {
                        if (explicitAttributes.Count != 0) throw InvalidDot();
                        continue;
                    }
                    foreach (var endpoint in endpoints)
                    {
                        var node = endpoint.Node;
                        node.Attributes.Apply(explicitAttributes);
                        node.Span = span;
                        node.Insert = insert;
                        node.Explicit = true;
                        if (explicitAttributes.TryGetValue("label", out var label)) node.LabelToken = label;
                    }
                }
                else foreach (var edge in added)
                    {
                        edge.Attributes.Apply(explicitAttributes);
                        edge.Span = span;
                        edge.Insert = insert;
                        if (explicitAttributes.TryGetValue("label", out var label)) edge.LabelToken = label;
                    }
            }
            scope.Insert = Current.Start;
            var close = Current;
            position++;
            scope.Span = scope.Span with { End = close.End, EndLine = close.EndLine };
        }

        private List<DotEndpoint> Endpoints(DotScope scope, int depth)
        {
            if (At("{") || Current.Keyword("subgraph"))
            {
                var first = Current;
                string id = Keyword("subgraph") && Current.Identifier ? Name().Value : "";
                Require("{");
                DotScope child;
                if (id.Length != 0 && namedScopes.TryGetValue(id, out var existing))
                {
                    if (existing.Parent != scope) throw InvalidDot();
                    child = existing;
                    child.Span = DotSpan.From(first, first);
                }
                else
                {
                    if (document.Scopes.Count >= 1024) throw InvalidDot();
                    child = new(id.Length == 0 ? "_anonymous_dot_" + anonymous++ : id, scope, DotSpan.From(first, first));
                    document.Scopes.Add(child);
                    scope.Children.Add(child);
                    if (id.Length != 0) namedScopes.Add(id, child);
                }
                Statements(child, depth + 1);
                return child.Nodes.Select(id => new DotEndpoint(document.Nodes[id], "", "", DotSpan.From(first, Previous), false)).ToList();
            }
            var result = new List<DotEndpoint>();
            do
            {
                var name = Name();
                string port = "", compass = "";
                if (Eat(":"))
                {
                    port = Name().Value;
                    if (Eat(":")) compass = Name().Value;
                }
                if (compass.Length != 0 && !DotCompass(compass)) throw InvalidDot();
                var span = DotSpan.From(name, Previous);
                if (!document.Nodes.TryGetValue(name.Value, out var node))
                {
                    if (document.Nodes.Count >= 1800) throw InvalidDot();
                    node = new(name.Value, scope.NodeDefaults, scope, span, name) { Insert = scope.Insert };
                    document.Nodes.Add(name.Value, node);
                }
                for (var ancestor = scope; ancestor != null; ancestor = ancestor.Parent) ancestor.Nodes.Add(name.Value);
                result.Add(new(node, port, compass, span, true));
            } while (Eat(","));
            return result;
        }

        private DotAttributes Attributes()
        {
            var attributes = new DotAttributes();
            while (Eat("["))
            {
                while (!Eat("]"))
                {
                    if (Eat(",") || Eat(";")) continue;
                    string key = Name().Value;
                    Require("=");
                    attributes[key] = Name();
                    if (attributes.Count > 512) throw InvalidDot();
                }
            }
            return attributes;
        }
    }

    private static bool DotCompass(string value) => value is "n" or "ne" or "e" or "se" or "s" or "sw" or "w" or "nw" or "c" or "_";
    private static bool DotBool(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static List<DotToken> LexDot(string source)
    {
        var tokens = new List<DotToken>();
        int position = 0, line = 0;
        if (position < source.Length && source[position] == '\uFEFF') position++;
        while (position < source.Length && char.IsWhiteSpace(source[position]))
        {
            if (source[position] == '\n' || source[position] == '\r' && (position + 1 == source.Length || source[position + 1] != '\n')) line++;
            position++;
        }
        if (!source.AsSpan(position).StartsWith("@startdot")) throw InvalidDot();
        position += 9;
        while (position < source.Length && source[position] is ' ' or '\t') position++;
        if (position == source.Length || source[position] is not ('\r' or '\n')) throw InvalidDot();
        bool ended = false;
        while (position < source.Length)
        {
            char c = source[position];
            if (char.IsWhiteSpace(c)) { if (c == '\n' || c == '\r' && (position + 1 == source.Length || source[position + 1] != '\n')) line++; position++; continue; }
            if (source.AsSpan(position).StartsWith("@enddot"))
            {
                position += 7;
                if (!source.AsSpan(position).Trim().IsEmpty) throw InvalidDot();
                ended = true;
                break;
            }
            if (c == '#' || source.AsSpan(position).StartsWith("//"))
            {
                while (position < source.Length && source[position] is not ('\r' or '\n')) position++;
                continue;
            }
            if (source.AsSpan(position).StartsWith("/*"))
            {
                position += 2;
                while (position + 1 < source.Length && !source.AsSpan(position).StartsWith("*/"))
                {
                    if (source[position] == '\n' || source[position] == '\r' && source[position + 1] != '\n') line++;
                    position++;
                }
                if (position + 1 == source.Length) throw InvalidDot();
                position += 2;
                continue;
            }
            int start = position, firstLine = line;
            DotTokenKind kind = DotTokenKind.Symbol;
            string value;
            if (c == '-' && position + 1 < source.Length && source[position + 1] is '>' or '-')
            { value = source.Substring(position, 2); position += 2; }
            else if ("{}[]=;,:+".Contains(c)) { value = c.ToString(); position++; }
            else if (c == '"')
            {
                kind = DotTokenKind.Quoted;
                position++;
                var text = new StringBuilder();
                while (position < source.Length && source[position] != '"')
                {
                    c = source[position++];
                    if (c == '\\' && position < source.Length)
                    {
                        char escaped = source[position];
                        if (escaped is '\r' or '\n')
                        {
                            position++;
                            if (escaped == '\r' && position < source.Length && source[position] == '\n') position++;
                            line++;
                            continue;
                        }
                        if (escaped is '"' or '\\') { text.Append(c == '\\' && escaped == '\\' ? "\\\\" : "\""); position++; continue; }
                    }
                    if (c == '\n' || c == '\r' && (position == source.Length || source[position] != '\n')) line++;
                    text.Append(c);
                    if (text.Length > 65536) throw InvalidDot();
                }
                if (position == source.Length) throw InvalidDot();
                position++;
                value = text.ToString();
            }
            else if (c == '<')
            {
                kind = DotTokenKind.Html;
                int nested = 0;
                char quote = '\0';
                do
                {
                    if (quote == '\0' && source.AsSpan(position).StartsWith("<!--"))
                    {
                        int close = source.IndexOf("-->", position + 4, StringComparison.Ordinal);
                        if (close < 0) throw InvalidDot();
                        while (position < close + 3)
                        {
                            if (source[position] == '\n' || source[position] == '\r' && (position + 1 == source.Length || source[position + 1] != '\n')) line++;
                            position++;
                        }
                        continue;
                    }
                    c = source[position++];
                    if (quote != '\0') { if (c == quote) quote = '\0'; }
                    else if (nested > 1 && c is ('"' or '\'')) quote = c;
                    else if (c == '<') nested++;
                    else if (c == '>') nested--;
                    if (c == '\n' || c == '\r' && (position == source.Length || source[position] != '\n')) line++;
                    if (nested > 64 || position - start > 65536) throw InvalidDot();
                } while (nested != 0 && position < source.Length);
                if (nested != 0 || quote != '\0') throw InvalidDot();
                value = source[(start + 1)..(position - 1)];
            }
            else
            {
                kind = DotTokenKind.Id;
                if (c == '-' || c == '.' || char.IsAsciiDigit(c))
                {
                    if (c == '-') position++;
                    bool digits = false;
                    while (position < source.Length && char.IsAsciiDigit(source[position])) { digits = true; position++; }
                    if (position < source.Length && source[position] == '.')
                    {
                        position++;
                        while (position < source.Length && char.IsAsciiDigit(source[position])) { digits = true; position++; }
                    }
                    if (!digits) throw InvalidDot();
                }
                else
                {
                    if (!(char.IsAsciiLetter(c) || c == '_' || c >= 128)) throw InvalidDot();
                    do { position++; }
                    while (position < source.Length && (char.IsAsciiLetterOrDigit(source[position]) || source[position] == '_' || source[position] >= 128));
                }
                if (position - start > 65536) throw InvalidDot();
                value = source[start..position];
            }
            tokens.Add(new(value, start, position - start, firstLine, line, kind, source));
            if (tokens.Count > 100_000) throw InvalidDot();
        }
        if (!ended) throw InvalidDot();
        return tokens;
    }
}
