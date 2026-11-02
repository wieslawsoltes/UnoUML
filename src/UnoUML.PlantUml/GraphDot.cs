namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private sealed class DotNode(string id, string shape, int line)
    {
        internal string Label = id, Shape = shape;
        internal int Line = line;
    }

    private readonly record struct DotToken(string Value, int Line, bool Quoted = false)
    {
        internal bool Is(string text) => !Quoted && Value == text;
    }

    internal static string? TryRenderDot(string source)
    {
        var tokens = new List<DotToken>();
        bool open = false, end = false;
        int line = -1;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var s = raw.Trim();
            if (s.IsEmpty) continue;
            if (!open) { if (!s.SequenceEqual("@startdot")) return null; open = true; continue; }
            if (end) return null;
            if (s.SequenceEqual("@enddot")) { end = true; continue; }
            if (!LexDot(s, line, tokens) || tokens.Count > 32000) return null;
        }
        if (!end || tokens.Count < 4) return null;
        int pos = 0;
        bool Eat(string token) { if (pos >= tokens.Count || !tokens[pos].Is(token)) return false; pos++; return true; }
        static bool Identifier(DotToken token) => token.Quoted || char.IsLetter(token.Value[0]) || token.Value[0] == '_' || char.IsDigit(token.Value[0]);
        bool NextName(out DotToken token)
        {
            if (pos < tokens.Count && Identifier(tokens[pos])) { token = tokens[pos++]; return true; }
            token = default; return false;
        }
        bool directed = Eat("digraph");
        if (!directed && !Eat("graph")) return null;
        NextName(out _); // Optional graph ID.
        if (!Eat("{")) return null;
        bool horizontal = false;
        string defaultShape = "ellipse";
        var nodes = new Dictionary<string, DotNode>(StringComparer.Ordinal);
        var edges = new List<(string A, string B, string Caption, int Line)>();
        void Ensure(DotToken name) => nodes.TryAdd(name.Value, new(name.Value, defaultShape, name.Line));
        bool Attrs(out string? label, out string? shape)
        {
            label = shape = null;
            if (!Eat("[")) return true;
            while (!Eat("]"))
            {
                if (!NextName(out var key) || !Eat("=") || !NextName(out var val)) return false;
                if (key.Is("label")) label = val.Value;
                else if (key.Is("shape") && val.Value is "ellipse" or "oval" or "box" or "rect" or "rectangle" or "plaintext") shape = val.Value;
                else return false;
                if (!Eat(",")) Eat(";");
            }
            return true;
        }
        while (pos < tokens.Count && !Eat("}"))
        {
            if (Eat(";")) continue;
            if (!NextName(out var from)) return null;
            if (from.Is("rankdir") && Eat("="))
            {
                if (Eat("LR")) horizontal = true; else if (Eat("TB")) horizontal = false; else return null;
                continue;
            }
            if (from.Is("node"))
            {
                if (pos >= tokens.Count || !tokens[pos].Is("[") || !Attrs(out var label, out var shape) || label != null || shape == null) return null;
                defaultShape = shape;
                continue;
            }
            if (!from.Quoted && from.Value is "graph" or "subgraph" or "edge" or "digraph" or "strict") return null;
            Ensure(from);
            int first = edges.Count;
            var cursor = from;
            while (Eat(directed ? "->" : "--"))
            {
                if (!NextName(out var to)) return null;
                if (!to.Quoted && to.Value is "node" or "edge" or "subgraph" or "graph" or "digraph" or "strict") return null;
                Ensure(to);
                edges.Add((cursor.Value, to.Value, "", from.Line));
                cursor = to;
            }
            if (!Attrs(out var caption, out var geometry)) return null;
            if (first == edges.Count)
            {
                var node = nodes[from.Value];
                node.Line = from.Line;
                if (caption != null) node.Label = caption;
                if (geometry != null) node.Shape = geometry;
            }
            else
            {
                if (geometry != null) return null;
                if (caption != null) for (int i = first; i < edges.Count; i++) edges[i] = (edges[i].A, edges[i].B, caption, edges[i].Line);
            }
            if (edges.Count > 6000 || nodes.Count > 1800) return null;
        }
        if (pos != tokens.Count || !tokens[^1].Is("}") || nodes.Count == 0) return null;
        var graph = new Graph { Horizontal = horizontal };
        var parts = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var (id, info) in nodes)
        {
            string kind = info.Shape switch { "ellipse" or "oval" => "usecase", "plaintext" => "label", _ => "rectangle" };
            var node = new Node(kind, id, info.Label, info.Line, graph.Root) { Fill = "none" };
            graph.Root.Children.Add(node); graph.Nodes.Add(node); parts.Add(id, node);
        }
        foreach (var e in edges) graph.Edges.Add(new(parts[e.A], parts[e.B], directed ? "->" : "--", e.Caption, "", "", e.Line, "", false));
        return Arrange(graph) ? Draw(graph) : null;
    }

    private static bool LexDot(ReadOnlySpan<char> s, int line, List<DotToken> tokens)
    {
        for (int p = 0; p < s.Length;)
        {
            char c = s[p];
            if (char.IsWhiteSpace(c)) { p++; continue; }
            if (s[p..].StartsWith("//")) break;
            if (p + 1 < s.Length && c == '-' && s[p + 1] is '>' or '-') { tokens.Add(new(s.Slice(p, 2).ToString(), line)); p += 2; continue; }
            if ("{}[]=;,".Contains(c)) { tokens.Add(new(c.ToString(), line)); p++; continue; }
            if (c == '"')
            {
                int first = ++p;
                while (p < s.Length && s[p] != '"') { if (s[p] == '\\') return false; p++; }
                if (p == s.Length || p - first > 2048) return false;
                tokens.Add(new(s[first..p++].ToString(), line, true));
                continue;
            }
            int start = p;
            while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
            if (start == p || p-start > 2048) return false;
            if (char.IsDigit(s[start]) && s[start..p].ContainsAnyExcept("0123456789")) return false;
            tokens.Add(new(s[start..p].ToString(), line));
        }
        return true;
    }
}
