using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UnoUML.Syntax;

/// <summary>A lossless, bounded grammar for literal named networks and their shared hosts.</summary>
internal sealed class NetworkSource
{
    internal sealed record Attribute(string Name, string Value, SourceToken NameToken, SourceToken EqualToken, SourceToken ValueToken)
    {
        internal SourceToken Separator { get; set; }
    }
    internal sealed class Bus(string alias, SourceToken aliasToken, int line, int start, int bodyStart)
    {
        internal string Alias { get; } = alias;
        internal SourceToken AliasToken { get; } = aliasToken;
        internal int Line { get; } = line;
        internal int Start { get; } = start;
        internal int BodyStart { get; } = bodyStart;
        internal int BodyEnd { get; set; }
        internal int End { get; set; }
        internal List<Attribute> Properties { get; } = [];
        internal List<Interface> Interfaces { get; } = [];
        internal string? Property(string name) => Properties.LastOrDefault(a => a.Name == name)?.Value;
        internal string CanvasId => Identity("network-bus", Alias);
    }
    internal sealed class Host(string alias)
    {
        internal string Alias { get; } = alias;
        internal List<Interface> Interfaces { get; } = [];
        internal string? Property(string name) => Interfaces.SelectMany(i => i.Attributes).LastOrDefault(a => a.Name == name)?.Value;
        internal string CanvasId => Identity("network-host", Alias);
    }
    internal sealed class Interface(Bus bus, Host host, SourceToken aliasToken, int line, int start)
    {
        internal Bus Bus { get; } = bus;
        internal Host Host { get; } = host;
        internal SourceToken AliasToken { get; } = aliasToken;
        internal int Line { get; } = line;
        internal int Start { get; } = start;
        internal int End { get; set; }
        internal SourceToken OpenBracket { get; set; }
        internal SourceToken CloseBracket { get; set; }
        internal List<Attribute> Attributes { get; } = [];
        internal string? Address => Attributes.FirstOrDefault(a => a.Name == "address")?.Value;
        internal string CanvasId => Identity("network-interface", Bus.CanvasId, Host.CanvasId);
    }

    internal string Source { get; }
    internal IReadOnlyList<SourceRow> Rows { get; }
    internal List<Bus> Networks { get; } = [];
    internal List<Host> Hosts { get; } = [];
    internal string Title { get; private set; } = "";
    internal bool Wrapped { get; private set; }
    internal int BodyStart { get; private set; }
    internal int BodyEnd { get; private set; }
    private NetworkSource(string source, IReadOnlyList<SourceRow> rows) { Source = source; Rows = rows; }

    internal static string Identity(string family, params string[] components)
    {
        var value = new StringBuilder();
        foreach (string part in components) value.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
        return family + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())).AsSpan(0, 16));
    }
    internal static bool IsAlias(string? value) => value is { Length: > 0 and <= 256 } &&
        (char.IsLetterOrDigit(value[0]) || value[0] == '_') && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-') &&
        value is not ("network" or "nwdiag" or "group");
    internal static bool IsValue(string? value) => value is null || value.Length <= 4096 && !value.Any(char.IsControl);
    internal static bool IsColor(string? value) => value is null || value.Length is > 0 and <= 128 &&
        (value[0] == '#' ? value.Length is 4 or 5 or 7 or 9 && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 : value.All(char.IsLetter));
    internal static bool IsShape(string? value) => value is null || value is "actor" or "agent" or "artifact" or "boundary" or "card" or "cloud" or
        "collections" or "component" or "control" or "database" or "entity" or "file" or "folder" or "frame" or "hexagon" or "interface" or
        "label" or "node" or "package" or "person" or "queue" or "stack" or "rectangle" or "storage" or "usecase";

    internal static NetworkSource? Parse(string source)
    {
        if (source.Length > 512000 || StructuredText.Rows(source) is not { } rawRows) return null;
        // Network uses // comments in addition to PlantUML's apostrophe comments.
        // Masking preserves UTF-16 offsets and every original line ending.
        var rows = rawRows.Select(row => row with { Code = MaskSlashComment(row.Code) }).ToArray();
        var meaningful = rows.Where(r => r.Trimmed.Length != 0).ToArray();
        if (meaningful.Length < 2 || !StructuredText.Header(meaningful[0].Trimmed, "nwdiag") || meaningful[^1].Trimmed != "@endnwdiag") return null;
        var result = new NetworkSource(source, rows);
        int body = meaningful[0].Next, end = meaningful[^1].Start;
        int first = 1;
        if (first < meaningful.Length - 1 && meaningful[first].Trimmed.StartsWith("title ", StringComparison.Ordinal))
        {
            result.Title = meaningful[first].Trimmed[6..]; body = meaningful[first++].Next;
        }
        if (!IsValue(result.Title) || !Lex(rows, body, end, out var tokens)) return null;
        int cursor = 0;
        SkipNewLines();
        result.Wrapped = Peek("nwdiag");
        if (result.Wrapped)
        {
            cursor++; SkipNewLines();
            if (!Take("{", out var brace)) return null;
            result.BodyStart = brace.Span.End;
        }
        else result.BodyStart = body;
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new Dictionary<string, Host>(StringComparer.Ordinal);
        int interfaceCount = 0;
        while (true)
        {
            SkipNewLines();
            if (cursor == tokens.Count)
            {
                if (result.Wrapped) return null;
                result.BodyEnd = end; break;
            }
            if (result.Wrapped && Take("}", out var rootClose))
            {
                result.BodyEnd = rootClose.Span.Start;
                if (Peek(";")) cursor++;
                SkipNewLines();
                if (cursor != tokens.Count) return null;
                break;
            }
            if (!Take("network", out var networkWord) || !ReadValue(out var name) || name.Quoted || !IsAlias(name.Value) ||
                !aliases.Add(name.Value) || result.Networks.Count >= 220) return null;
            SkipNewLines();
            if (!Take("{", out var open)) return null;
            var bus = new Bus(name.Value, name.Span, networkWord.Line, networkWord.Span.Start, open.Span.End);
            result.Networks.Add(bus);
            var attached = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                SkipNewLines();
                if (Take("}", out var close))
                {
                    bus.BodyEnd = close.Span.Start; bus.End = close.Span.End;
                    if (Take(";", out var semicolon)) bus.End = semicolon.Span.End;
                    break;
                }
                if (!ReadValue(out var word) || word.Quoted) return null;
                if (Peek("="))
                {
                    if (word.Value is not ("address" or "description" or "color" or "width") || !ReadAttribute(word, out var property)) return null;
                    if (property.Name == "color" && !IsColor(property.Value) || property.Name == "width" && property.Value != "full") return null;
                    if (Take(";", out var separator)) property.Separator = separator.Span;
                    else if (!EndStatement()) return null;
                    bus.Properties.Add(property); continue;
                }
                if (!IsAlias(word.Value) || !attached.Add(word.Value) || ++interfaceCount > 4096) return null;
                if (!hosts.TryGetValue(word.Value, out var host))
                {
                    if (hosts.Count >= 1024) return null;
                    hosts[word.Value] = host = new(word.Value); result.Hosts.Add(host);
                }
                var connection = new Interface(bus, host, word.Span, word.Line, word.Span.Start) { End = word.Span.End };
                if (Take("[", out var listOpen))
                {
                    connection.OpenBracket = listOpen.Span;
                    var properties = new HashSet<string>(StringComparer.Ordinal);
                    SkipNewLines();
                    while (!Peek("]"))
                    {
                        if (!ReadValue(out var key) || key.Quoted || key.Value is not ("address" or "description" or "color" or "shape") ||
                            !properties.Add(key.Value) || !ReadAttribute(key, out var property)) return null;
                        if (key.Value == "color" && !IsColor(property.Value) || key.Value == "shape" && !IsShape(property.Value)) return null;
                        connection.Attributes.Add(property); SkipNewLines();
                        if (Take(",", out var comma)) { property.Separator = comma.Span; SkipNewLines(); if (Peek("]")) return null; }
                        else if (!Peek("]")) return null;
                    }
                    if (!Take("]", out var listClose)) return null;
                    connection.CloseBracket = listClose.Span; connection.End = listClose.Span.End;
                }
                if (Take(";", out var hostEnd)) connection.End = hostEnd.Span.End;
                else if (!EndStatement()) return null;
                bus.Interfaces.Add(connection); host.Interfaces.Add(connection);
            }
        }
        return result;

        bool Peek(string value) => cursor < tokens.Count && !tokens[cursor].Quoted && tokens[cursor].Value == value;
        bool Take(string value, out Lexeme token)
        {
            if (Peek(value)) { token = tokens[cursor++]; return true; }
            token = default; return false;
        }
        bool ReadValue(out Lexeme token)
        {
            if (cursor < tokens.Count && (tokens[cursor].Quoted || tokens[cursor].Value.Length > 0 && tokens[cursor].Value[0] is not ('\n' or '{' or '}' or '[' or ']' or '=' or ',' or ';')))
            { token = tokens[cursor++]; return true; }
            token = default; return false;
        }
        bool ReadAttribute(Lexeme key, out Attribute attribute)
        {
            attribute = null!; SkipNewLines();
            if (!Take("=", out var equals)) return false;
            SkipNewLines();
            if (!ReadValue(out var value) || !IsValue(value.Value)) return false;
            attribute = new(key.Value, value.Value, key.Span, equals.Span, value.Span); return true;
        }
        bool EndStatement() => cursor == tokens.Count || Peek("\n") || Peek("}");
        void SkipNewLines() { while (Peek("\n")) cursor++; }
    }

    private readonly record struct Lexeme(string Value, SourceToken Span, int Line, bool Quoted = false);
    private static bool Lex(IReadOnlyList<SourceRow> rows, int start, int end, out List<Lexeme> tokens)
    {
        tokens = [];
        foreach (var row in rows)
        {
            if (row.Start < start || row.Start >= end) continue;
            string code = row.Code;
            for (int i = 0; i < code.Length;)
            {
                if (char.IsWhiteSpace(code[i])) { i++; continue; }
                int at = i;
                if (code[i] == '"')
                {
                    var value = new StringBuilder(); bool closed = false;
                    for (i++; i < code.Length; i++)
                    {
                        char c = code[i];
                        if (c == '"') { i++; closed = true; break; }
                        if (c == '\\')
                        {
                            if (++i >= code.Length) return false;
                            c = code[i];
                            if (c is not ('"' or '\\')) value.Append('\\');
                        }
                        if (char.IsControl(c)) return false;
                        value.Append(c);
                    }
                    if (!closed || value.Length > 4096) return false;
                    tokens.Add(new(value.ToString(), new(row.Start + at, i - at), row.Index, true));
                }
                else if (code[i] is '{' or '}' or '[' or ']' or '=' or ',' or ';')
                    tokens.Add(new(code[i++].ToString(), new(row.Start + at, 1), row.Index));
                else
                {
                    while (i < code.Length && !char.IsWhiteSpace(code[i]) && code[i] is not ('{' or '}' or '[' or ']' or '=' or ',' or ';')) i++;
                    if (i == at || i - at > 4096) return false;
                    string word = code[at..i];
                    if (word.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '#'))) return false;
                    tokens.Add(new(word, new(row.Start + at, i - at), row.Index));
                }
                if (tokens.Count > 65536) return false;
            }
            tokens.Add(new("\n", new(row.End, row.Next - row.End), row.Index));
            if (tokens.Count > 65536) return false;
        }
        return true;
    }
    private static string MaskSlashComment(string code)
    {
        bool quoted = false;
        for (int i = 0; i + 1 < code.Length; i++)
        {
            if (quoted && code[i] == '\\') { i++; continue; }
            if (code[i] == '"') quoted = !quoted;
            if (!quoted && code[i] == '/' && code[i + 1] == '/') return code[..i] + new string(' ', code.Length - i);
        }
        return code;
    }
}
