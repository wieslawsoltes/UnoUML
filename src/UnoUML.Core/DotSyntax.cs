using System.Text;

namespace UnoUML.Core;

/// <summary>A bounded source projection for DOT edits. Tokens retain their original UTF-16 spans.</summary>
internal sealed class DotSyntax
{
    internal enum TokenKind { Symbol, Identifier, Quoted, Html }
    internal readonly record struct Token(string Value, int Start, int End, TokenKind Kind)
    {
        internal bool Is(string value) => Kind == TokenKind.Symbol && Value == value;
        internal bool Keyword(string value) => Kind == TokenKind.Identifier && Value.Equals(value, StringComparison.OrdinalIgnoreCase);
        internal bool IsName => Kind is TokenKind.Quoted or TokenKind.Html || Kind == TokenKind.Identifier &&
            !Keywords.Contains(Value);
    }

    internal sealed class Scope(string name, Scope? parent)
    {
        internal string Name = name;
        internal readonly Scope? Parent = parent;
        internal readonly Dictionary<string, Token> Attributes = parent == null ? new(StringComparer.Ordinal) : new(parent.Attributes, StringComparer.Ordinal);
        internal readonly Dictionary<string, Token> NodeDefaults = parent == null ? new(StringComparer.Ordinal) : new(parent.NodeDefaults, StringComparer.Ordinal);
        internal readonly Dictionary<string, Token> EdgeDefaults = parent == null ? new(StringComparer.Ordinal) : new(parent.EdgeDefaults, StringComparer.Ordinal);
        internal readonly HashSet<string> Members = new(StringComparer.Ordinal);
        internal readonly List<ScopeOccurrence> Occurrences = [];
        internal bool IsCluster => Parent != null && (Name.StartsWith("cluster", StringComparison.Ordinal) ||
            Attributes.TryGetValue("cluster", out var value) && (value.Value == "1" || value.Value.Equals("true", StringComparison.OrdinalIgnoreCase)));
    }

    internal sealed class ScopeOccurrence(Scope scope, Statement? owner, Token? name, int start, int open)
    {
        internal readonly Scope Scope = scope;
        internal readonly Statement? Owner = owner;
        internal readonly Token? Name = name;
        internal readonly int Start = start, Open = open;
        internal int Close, End;
    }

    internal sealed class Statement(Scope scope, int start)
    {
        internal readonly Scope Scope = scope;
        internal readonly int Start = start;
        internal readonly List<NodeList> Lists = [];
        internal int End, After;
        internal bool Edge;
    }

    internal sealed class Node(string name, Token token, Statement first, Dictionary<string, Token> defaults)
    {
        internal readonly string Name = name;
        internal readonly Token Token = token;
        internal readonly Statement First = first;
        internal readonly Dictionary<string, Token> Attributes = new(defaults, StringComparer.Ordinal);
        internal readonly Dictionary<Scope, Statement> Appearances = [];
    }

    internal readonly record struct Endpoint(Node Node, Token Name, int End, string Port, string Compass);
    internal sealed class NodeList(Statement owner)
    {
        internal readonly Statement Owner = owner;
        internal readonly List<Endpoint> Nodes = [];
        internal readonly List<Token> Commas = [];
    }

    internal sealed class Edge(Endpoint from, Endpoint to, Statement first)
    {
        internal Endpoint From = from, To = to;
        internal readonly Statement First = first;
        internal readonly Dictionary<string, Token> Attributes = new(first.Scope.EdgeDefaults, StringComparer.Ordinal);
    }

    private sealed class InvalidSyntax : Exception;
    private static readonly HashSet<string> Keywords = new(["graph", "digraph", "subgraph", "node", "edge", "strict"], StringComparer.OrdinalIgnoreCase);
    private readonly List<Token> tokens;
    private readonly Dictionary<string, Scope> namedScopes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To), Edge> strictEdges = [];
    private int cursor, anonymous;
    internal readonly string Source;
    internal readonly Dictionary<string, Node> Nodes = new(StringComparer.Ordinal);
    internal readonly List<Edge> Edges = [];
    internal readonly List<NodeList> Lists = [];
    internal readonly List<Scope> Scopes = [];
    internal readonly List<(string Name, Token Value)> Attributes = [];
    internal Scope Root = null!;
    internal bool Directed, Strict;
    internal IReadOnlyList<Token> Tokens => tokens;

    private DotSyntax(string source) => (Source, tokens) = (source, Lex(source));

    internal static DotSyntax? Parse(string source)
    {
        if (source.Length > 1_048_576) return null;
        try
        {
            var syntax = new DotSyntax(source);
            syntax.Graph();
            return syntax;
        }
        catch (InvalidSyntax) { return null; }
    }

    internal string Text(Token token) => Source[token.Start..token.End];
    internal static bool Contains(Scope ancestor, Scope child)
    {
        for (var scope = child; scope != null; scope = scope.Parent)
            if (scope == ancestor) return true;
        return false;
    }

    private Token Current => cursor < tokens.Count ? tokens[cursor] : throw new InvalidSyntax();
    private Token Previous => tokens[cursor - 1];
    private bool At(string value) => cursor < tokens.Count && tokens[cursor].Is(value);
    private bool Eat(string value) { if (!At(value)) return false; cursor++; return true; }
    private void Require(string value) { if (!Eat(value)) throw new InvalidSyntax(); }
    private bool Keyword(string value)
    {
        if (cursor == tokens.Count || !Current.Keyword(value)) return false;
        cursor++; return true;
    }

    private Token Name()
    {
        var token = Current;
        if (!token.IsName) throw new InvalidSyntax();
        cursor++;
        if (token.Kind != TokenKind.Quoted || !At("+")) return token;
        var value = new StringBuilder(token.Value);
        while (Eat("+"))
        {
            var next = Current;
            if (next.Kind != TokenKind.Quoted) throw new InvalidSyntax();
            cursor++;
            value.Append(next.Value);
            if (value.Length > 65536) throw new InvalidSyntax();
            token = token with { End = next.End };
        }
        return token with { Value = value.ToString() };
    }

    private void Graph()
    {
        Strict = Keyword("strict");
        Directed = Keyword("digraph");
        if (!Directed && !Keyword("graph")) throw new InvalidSyntax();
        Token? name = Current.IsName ? Name() : null;
        var open = Current;
        Require("{");
        Root = new(name?.Value ?? "", null);
        Scopes.Add(Root);
        var occurrence = new ScopeOccurrence(Root, null, name, open.Start, open.End);
        Root.Occurrences.Add(occurrence);
        Statements(occurrence, 0);
        if (cursor != tokens.Count) throw new InvalidSyntax();
    }

    private void Statements(ScopeOccurrence occurrence, int depth)
    {
        if (depth > 48) throw new InvalidSyntax();
        var scope = occurrence.Scope;
        while (!At("}"))
        {
            if (Eat(";") || Eat(",")) continue;
            var statement = new Statement(scope, Current.Start);
            var first = Current;
            if (Keyword("graph") || Keyword("node") || Keyword("edge"))
            {
                if (!At("[")) throw new InvalidSyntax();
                var attributes = ReadAttributes();
                Apply(first.Keyword("node") ? scope.NodeDefaults : first.Keyword("edge") ? scope.EdgeDefaults : scope.Attributes, attributes);
            }
            else if (first.IsName && cursor + 1 < tokens.Count && tokens[cursor + 1].Is("="))
            {
                string key = Name().Value;
                Require("=");
                var value = Name();
                scope.Attributes[key] = value;
                Attributes.Add((key, value));
            }
            else
            {
                bool subgraph = At("{") || Current.Keyword("subgraph");
                var endpoints = Endpoints(statement, depth);
                var added = new List<Edge>();
                while (At("->") || At("--"))
                {
                    statement.Edge = true;
                    if (Current.Value != (Directed ? "->" : "--")) throw new InvalidSyntax();
                    cursor++;
                    var next = Endpoints(statement, depth);
                    if ((long)endpoints.Count * next.Count + Edges.Count > 6000) throw new InvalidSyntax();
                    foreach (var from in endpoints)
                        foreach (var to in next)
                        {
                            var key = (from.Node.Name, to.Node.Name);
                            if (!Directed && StringComparer.Ordinal.Compare(key.Item1, key.Item2) > 0) key = (key.Item2, key.Item1);
                            if (!Strict || !strictEdges.TryGetValue(key, out var edge))
                            {
                                edge = new(from, to, statement);
                                Edges.Add(edge);
                                if (Strict) strictEdges.Add(key, edge);
                            }
                            else
                            {
                                edge.From = MergeEndpoint(edge.From, edge.From.Node == from.Node ? from : to);
                                edge.To = MergeEndpoint(edge.To, edge.To.Node == to.Node ? to : from);
                            }
                            added.Add(edge);
                        }
                    endpoints = next;
                }
                var attributes = ReadAttributes();
                if (statement.Edge)
                {
                    foreach (var edge in added) Apply(edge.Attributes, attributes);
                }
                else if (subgraph)
                {
                    if (attributes.Count != 0) throw new InvalidSyntax();
                }
                else foreach (var endpoint in endpoints) Apply(endpoint.Node.Attributes, attributes);
            }
            statement.End = Previous.End;
            statement.After = At(";") || At(",") ? Current.End : statement.End;
        }
        occurrence.Close = Current.Start;
        occurrence.End = Current.End;
        cursor++;
    }

    private static Endpoint MergeEndpoint(Endpoint previous, Endpoint current) =>
        current.Port.Length == 0 && current.Compass.Length == 0 ? current with { Port = previous.Port, Compass = previous.Compass } : current;

    private List<Endpoint> Endpoints(Statement owner, int depth)
    {
        var scope = owner.Scope;
        if (At("{") || Current.Keyword("subgraph"))
        {
            int start = Current.Start;
            Token? name = Keyword("subgraph") && Current.IsName ? Name() : null;
            // DOT treats an explicitly empty subgraph name as a fresh anonymous scope.
            if (name is { Value.Length: 0 }) name = null;
            var open = Current;
            Require("{");
            Scope child;
            if (name is { } named && namedScopes.TryGetValue(named.Value, out var previous))
            {
                if (previous.Parent != scope) throw new InvalidSyntax();
                child = previous;
            }
            else
            {
                if (Scopes.Count >= 1024) throw new InvalidSyntax();
                child = new(name?.Value ?? "_anonymous_dot_" + anonymous++, scope);
                Scopes.Add(child);
                if (name is { } identifier) namedScopes.Add(identifier.Value, child);
            }
            var occurrence = new ScopeOccurrence(child, owner, name, start, open.End);
            child.Occurrences.Add(occurrence);
            Statements(occurrence, depth + 1);
            return child.Members.Select(id => new Endpoint(Nodes[id], default, 0, "", "")).ToList();
        }
        var list = new NodeList(owner);
        owner.Lists.Add(list);
        Lists.Add(list);
        do
        {
            var name = Name();
            string port = "", compass = "";
            if (Eat(":"))
            {
                port = Text(Name());
                if (Eat(":")) compass = Text(Name());
            }
            if (!Nodes.TryGetValue(name.Value, out var node))
            {
                if (Nodes.Count >= 1800) throw new InvalidSyntax();
                node = new(name.Value, name, owner, scope.NodeDefaults);
                Nodes.Add(name.Value, node);
            }
            node.Appearances.TryAdd(scope, owner);
            for (var ancestor = scope; ancestor != null; ancestor = ancestor.Parent) ancestor.Members.Add(node.Name);
            list.Nodes.Add(new(node, name, Previous.End, port, compass));
            if (!At(",")) break;
            list.Commas.Add(Current);
            cursor++;
        } while (true);
        return list.Nodes;
    }

    private Dictionary<string, Token> ReadAttributes()
    {
        var result = new Dictionary<string, Token>(StringComparer.Ordinal);
        while (Eat("["))
        {
            while (!Eat("]"))
            {
                if (Eat(",") || Eat(";")) continue;
                string name = Name().Value;
                Require("=");
                var value = Name();
                result[name] = value;
                Attributes.Add((name, value));
                if (result.Count > 512) throw new InvalidSyntax();
            }
        }
        return result;
    }

    private static void Apply(Dictionary<string, Token> target, Dictionary<string, Token> values)
    {
        foreach (var (name, value) in values) target[name] = value;
    }

    private static List<Token> Lex(string source)
    {
        var result = new List<Token>();
        int at = 0;
        if (at < source.Length && source[at] == '\uFEFF') at++;
        while (at < source.Length && char.IsWhiteSpace(source[at])) at++;
        if (!source.AsSpan(at).StartsWith("@startdot", StringComparison.OrdinalIgnoreCase)) throw new InvalidSyntax();
        at += 9;
        if (at < source.Length && !char.IsWhiteSpace(source[at])) throw new InvalidSyntax();
        while (at < source.Length && source[at] is not ('\r' or '\n')) at++;
        bool ended = false;
        while (at < source.Length)
        {
            char c = source[at];
            if (char.IsWhiteSpace(c)) { at++; continue; }
            if (source.AsSpan(at).StartsWith("@enddot", StringComparison.OrdinalIgnoreCase))
            {
                at += 7;
                if (!source.AsSpan(at).Trim().IsEmpty) throw new InvalidSyntax();
                ended = true; break;
            }
            if (c == '#' || source.AsSpan(at).StartsWith("//"))
            {
                while (at < source.Length && source[at] is not ('\r' or '\n')) at++;
                continue;
            }
            if (source.AsSpan(at).StartsWith("/*"))
            {
                int close = source.IndexOf("*/", at + 2, StringComparison.Ordinal);
                if (close < 0) throw new InvalidSyntax();
                at = close + 2; continue;
            }
            int start = at;
            TokenKind kind = TokenKind.Symbol;
            string value;
            if (c == '-' && at + 1 < source.Length && source[at + 1] is '>' or '-')
            { value = source.Substring(at, 2); at += 2; }
            else if ("{}[]=;,:+".Contains(c)) { value = c.ToString(); at++; }
            else if (c == '"')
            {
                kind = TokenKind.Quoted;
                at++;
                var text = new StringBuilder();
                while (at < source.Length && source[at] != '"')
                {
                    c = source[at++];
                    if (c == '\\' && at < source.Length)
                    {
                        char next = source[at];
                        if (next is '\r' or '\n')
                        {
                            at++;
                            if (next == '\r' && at < source.Length && source[at] == '\n') at++;
                            continue;
                        }
                        if (next is '"' or '\\') { text.Append(next == '\\' ? "\\\\" : "\""); at++; continue; }
                    }
                    text.Append(c);
                    if (text.Length > 65536) throw new InvalidSyntax();
                }
                if (at == source.Length) throw new InvalidSyntax();
                at++;
                value = text.ToString();
            }
            else if (c == '<')
            {
                kind = TokenKind.Html;
                int nested = 0;
                char quote = '\0';
                do
                {
                    if (quote == '\0' && source.AsSpan(at).StartsWith("<!--"))
                    {
                        int close = source.IndexOf("-->", at + 4, StringComparison.Ordinal);
                        if (close < 0) throw new InvalidSyntax();
                        at = close + 3; continue;
                    }
                    c = source[at++];
                    if (quote != '\0') { if (c == quote) quote = '\0'; }
                    else if (nested > 1 && c is ('"' or '\'')) quote = c;
                    else if (c == '<') nested++;
                    else if (c == '>') nested--;
                    if (nested > 64 || at - start > 65536) throw new InvalidSyntax();
                } while (nested != 0 && at < source.Length);
                if (nested != 0 || quote != '\0') throw new InvalidSyntax();
                value = source[(start + 1)..(at - 1)];
            }
            else
            {
                kind = TokenKind.Identifier;
                if (c == '-' || c == '.' || char.IsAsciiDigit(c))
                {
                    if (c == '-') at++;
                    bool digits = false;
                    while (at < source.Length && char.IsAsciiDigit(source[at])) { digits = true; at++; }
                    if (at < source.Length && source[at] == '.')
                    {
                        at++;
                        while (at < source.Length && char.IsAsciiDigit(source[at])) { digits = true; at++; }
                    }
                    if (!digits) throw new InvalidSyntax();
                }
                else
                {
                    if (!(char.IsAsciiLetter(c) || c == '_' || c >= 128)) throw new InvalidSyntax();
                    do { at++; }
                    while (at < source.Length && (char.IsAsciiLetterOrDigit(source[at]) || source[at] == '_' || source[at] >= 128));
                }
                if (at - start > 65536) throw new InvalidSyntax();
                value = source[start..at];
            }
            result.Add(new(value, start, at, kind));
            if (result.Count > 100_000) throw new InvalidSyntax();
        }
        if (!ended) throw new InvalidSyntax();
        return result;
    }
}
