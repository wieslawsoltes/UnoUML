using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

// PlantUML style signatures are sets of semantic names. Nesting intersects the
// names, while stereotypes and ancestor inheritance have their own precedence.
internal sealed partial class StyleSheet
{
    internal sealed record Rule(string[] Names, string[] Stereotypes, int Depth, bool Descendants,
        string Property, string Value, long Order, bool Dark);
    internal readonly record struct Value(string Text, int Stereotypes, long Order, int Ancestor = int.MinValue)
    {
        internal bool Wins(Value other) => Ancestor != other.Ancestor ? Ancestor > other.Ancestor :
            Stereotypes != other.Stereotypes ? Stereotypes > other.Stereotypes : Order >= other.Order;
    }

    internal readonly List<Rule> Rules = [];
    private readonly Dictionary<string, Dictionary<string, Value>> cache = new(StringComparer.Ordinal);
    internal bool DarkMode { get; private set; }
    internal bool IsEmpty => Rules.Count == 0;

    private static readonly HashSet<string> Names = new((
        "boarddiagram wirediagram action activationbox activity activitybar activitydiagram actor agent analog archimate arrow artifact binary body boundary box boxless business caption card cardinality circle classdiagram class clickable cloud closed collection collections component composite robust chenattribute cheneerdiagram chenentity chenrelationship concise clock componentdiagram constraintarrow control database day delay description destroy diamond document ebnf element entity end start stop file filesdiagram folder footer frame ganttdiagram generic goto group groupheader header hexagon highlight hnote interface json jsondiagram gitdiagram label leafnode legend lifeline mainframe map milestone mindmapdiagram month name network newpage node note nwdiagdiagram packetdiagdiagram objectdiagram object package participant partition person port process qualified queue rectangle reference referenceheader regex requirement rnote root rootnode saltdiagram separator sequencediagram server stack statediagram state statebody stereotype storage swimlane task timegrid timeline timingdiagram title undone unstarted usecase verticalseparator year visibilityicon private protected public iemandatory spot spotannotation spotinterface spotenum spotprotocol spotstruct spotentity spotexception spotclass spotabstractclass spotmetaclass spotstereotype spotdataclass spotrecord wbsdiagram yamldiagram chartdiagram bar line area scatter axis haxis vaxis grid annotation button input check radio choice widget").Split(' '), StringComparer.Ordinal);
    internal static readonly HashSet<string> Properties = new((
        "shadowing fontname fontcolor fontsize fontstyle fontweight backgroundcolor roundcorner linethickness diagonalcorner hyperlinkcolor hyperlinkunderlinestyle hyperlinkunderlinethickness headcolor linecolor linestyle padding margin maximumwidth minimumwidth exportedname image horizontalalignment showstereotype imageposition markershape markersize markercolor barwidth width").Split(' '), StringComparer.Ordinal);

    internal Dictionary<string, Value> Resolve(string names, string stereotypes = "", int depth = -1, bool ancestorsOnly = false)
    {
        string key = names + "|" + stereotypes + "|" + depth.ToString(CultureInfo.InvariantCulture) + (ancestorsOnly ? "*" : "");
        if (cache.TryGetValue(key, out var existing)) return existing;
        var tags = names.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var classes = stereotypes.Split([">><<", "\u001f"], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            if (rule.Dark && !DarkMode || ancestorsOnly && !rule.Descendants ||
                rule.Depth >= 0 && (depth < 0 || (rule.Descendants ? depth < rule.Depth : depth != rule.Depth)) ||
                !rule.Names.All(tags.Contains) || !rule.Stereotypes.All(classes.Contains)) continue;
            var value = new Value(rule.Value, rule.Stereotypes.Length, rule.Order);
            if (!result.TryGetValue(rule.Property, out var previous) || value.Wins(previous)) result[rule.Property] = value;
        }
        if (cache.Count < 4096) cache[key] = result;
        return result;
    }

    internal static bool TryExtract(string source, out string cleaned, out StyleSheet sheet)
    {
        cleaned = source;
        sheet = new();
        if (!source.Contains("<style", StringComparison.OrdinalIgnoreCase) && !source.Contains("!assume", StringComparison.Ordinal)) return true;
        var mask = source.ToCharArray();
        source = SourceLines.WithLineFeeds(source);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        int offset = 0, braces = 0;
        bool opened = false, closed = false;
        string? literal = null;
        while (offset < source.Length)
        {
            int end = source.IndexOf('\n', offset);
            if (end < 0) end = source.Length;
            string line = source[offset..end].Trim();
            if (line.StartsWith("!assume transparent ", StringComparison.Ordinal))
            {
                if (!opened || closed || braces != 0 || literal != null) return false;
                string scheme = line[20..].Trim();
                if (scheme is not ("light" or "dark")) return false;
                sheet.DarkMode = scheme == "dark";
                for (int i = offset; i < end; i++) if (mask[i] != '\r') mask[i] = ' ';
                offset = end + 1;
                continue;
            }
            if (line.Equals("<style>", StringComparison.OrdinalIgnoreCase))
            {
                if (!opened || closed || braces != 0 || literal != null) return false;
                int finish = source.IndexOf("</style>", end, StringComparison.OrdinalIgnoreCase);
                if (finish < 0 || source.AsSpan(end, finish - end).Count('\n') > 4096) return false;
                int finishLine = source.LastIndexOf('\n', finish);
                if (!source.AsSpan(finishLine + 1, finish - finishLine - 1).Trim().IsEmpty) return false;
                int after = finish + 8;
                int next = source.IndexOf('\n', after);
                if (next < 0) next = source.Length;
                if (!source.AsSpan(after, next - after).Trim().IsEmpty ||
                    !new Parser(source[end..finish], source.AsSpan(0, end).Count('\n'), variables, sheet).Run()) return false;
                for (int i = offset; i < after; i++) if (mask[i] is not ('\n' or '\r')) mask[i] = ' ';
                offset = next + 1;
                continue;
            }
            if (line.StartsWith("@start", StringComparison.OrdinalIgnoreCase)) opened = true;
            else if (line.StartsWith("@end", StringComparison.OrdinalIgnoreCase)) closed = true;
            if (literal != null)
            {
                if (line.Equals(literal, StringComparison.OrdinalIgnoreCase) || literal == "end ref" && line is "endref" or "end") literal = null;
            }
            else if (line.Length != 0 && line[0] != '\'')
            {
                string lower = line.ToLowerInvariant();
                if (lower is "legend" or "legend left" or "legend right" or "legend center") literal = "endlegend";
                else if (lower is "title" or "header" or "footer" or "caption") literal = "end " + lower;
                else if (lower.StartsWith("ref over ", StringComparison.Ordinal) && !line.Contains(':')) literal = "end ref";
                else if ((lower == "note" || lower.StartsWith("note ", StringComparison.Ordinal)) && !lower.StartsWith("note \"", StringComparison.Ordinal) && !line.Contains(':')) literal = "end note";
                bool quote = false;
                foreach (char c in line)
                {
                    if (c == '"') quote = !quote;
                    else if (!quote && c == '{') braces++;
                    else if (!quote && c == '}') braces--;
                }
            }
            offset = end + 1;
        }
        cleaned = new(mask);
        return true;
    }

    private sealed class Parser(string text, int firstLine, Dictionary<string, string> variables, StyleSheet sheet)
    {
        private sealed record Scope(string[] Selectors, bool Star, bool Dark);
        private readonly List<Scope> stack = [];
        private int position, line = firstLine, order;

        internal bool Run()
        {
            while (Skip())
            {
                char c = text[position];
                if (c == '}') { if (stack.Count == 0) return false; stack.RemoveAt(stack.Count - 1); position++; continue; }
                if (c == '@')
                {
                    int open = text.IndexOf('{', position);
                    if (open < 0 || open - position > 160 || stack.Count >= 32) return false;
                    line += text.AsSpan(position, open - position).Count('\n');
                    stack.Add(new([], false, true)); position = open + 1; continue;
                }
                if (c == ':') position++;
                string head = Identifier();
                if (head.Length == 0) return false;
                int headerLine = line;
                Skip();
                if (position < text.Length && text[position] is '{' or ',' or '*')
                {
                    var names = new List<string> { head };
                    while (position < text.Length && text[position] == ',')
                    {
                        position++; Skip();
                        string name = Identifier();
                        if (name.Length == 0 || names.Count >= 32) return false;
                        names.Add(name); Skip();
                    }
                    bool star = position < text.Length && text[position] == '*';
                    if (star) { position++; Skip(); }
                    if (position >= text.Length || text[position++] != '{' || stack.Count >= 32 || !names.All(ValidSelector)) return false;
                    stack.Add(new(names.ToArray(), star, false));
                    continue;
                }
                if (position < text.Length && text[position] == ':') { position++; while (position < text.Length && text[position] is ' ' or '\t') position++; }
                var value = new StringBuilder();
                bool quoted = false;
                while (position < text.Length)
                {
                    c = text[position];
                    if (c == '"') { quoted = !quoted; position++; continue; }
                    if (!quoted && c is '\r' or '\n' or ';' or '}') break;
                    value.Append(c); position++;
                }
                if (quoted) return false;
                if (position < text.Length && text[position] == ';') position++;
                string body = VariablePattern().Replace(value.ToString().Trim(), m => variables.GetValueOrDefault(m.Groups[1].Value, m.Value));
                if (body.Length is 0 or > 4096) return false;
                if (head.StartsWith("--", StringComparison.Ordinal))
                {
                    if (variables.Count >= 256 || head.Length <= 2) return false;
                    variables[head[2..]] = body; continue;
                }
                string property = head.ToLowerInvariant();
                if (stack.Count == 0) return false;
                // The reference grammar deliberately ignores unknown property names.
                if (!Properties.Contains(property)) continue;
                if (!ManagedSkin.Normalize(property, body, out string normalized))
                {
                    // ValueImpl.asColor uses HColorSet.getColorOrWhite. Official themes
                    // retain unresolved variables and even composed invalid color tokens.
                    if (!property.EndsWith("color", StringComparison.Ordinal) || ManagedSkin.IsAutomaticColor(body) || body.Length > 160 ||
                        !body.All(c => char.IsAsciiLetterOrDigit(c) || c is '#' or '$' or '_' or '-' or ' ' or '\t')) return false;
                    normalized = "white";
                }
                body = normalized;
                var paths = new List<string[]> { Array.Empty<string>() };
                foreach (var scope in stack)
                {
                    if (scope.Dark) continue;
                    var next = new List<string[]>();
                    foreach (var path in paths)
                        foreach (string name in scope.Selectors)
                        {
                            if (next.Count >= 256) return false;
                            next.Add([.. path, name.ToLowerInvariant()]);
                        }
                    paths = next;
                }
                foreach (var path in paths)
                {
                    if (sheet.Rules.Count >= 4096) return false;
                    var names = path.Where(n => !n.StartsWith('.') && !n.StartsWith("depth(", StringComparison.Ordinal)).Distinct().ToArray();
                    var stereotypes = path.Where(n => n.StartsWith('.')).Select(n => n[1..]).Distinct().ToArray();
                    int depth = -1;
                    foreach (string name in path.Where(n => n.StartsWith("depth(", StringComparison.Ordinal)))
                        if (!int.TryParse(name.AsSpan(6, name.Length - 7), out depth) || depth is < 0 or > 128) return false;
                    sheet.Rules.Add(new(names, stereotypes, depth, stack.Any(s => s.Star), property, body,
                        headerLine * 4096L + order++, stack.Any(s => s.Dark)));
                }
            }
            return stack.Count == 0;
        }

        private bool Skip()
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (char.IsWhiteSpace(c)) { if (c == '\n') line++; position++; continue; }
                if (c == '\'' || c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                {
                    int end = text.IndexOf('\n', position); position = end < 0 ? text.Length : end; continue;
                }
                if (c == '/' && position + 1 < text.Length && text[position + 1] is '*' or '\'')
                {
                    string endMarker = text[position + 1] == '*' ? "*/" : "'/";
                    int end = text.IndexOf(endMarker, position + 2, StringComparison.Ordinal);
                    if (end < 0) { position = text.Length; stack.Add(new([], false, false)); return false; }
                    line += text.AsSpan(position, end + 2 - position).Count('\n'); position = end + 2; continue;
                }
                return true;
            }
            return false;
        }

        private string Identifier()
        {
            int begin = position;
            bool stereotype = begin < text.Length && text[begin] == '.';
            while (position < text.Length)
            {
                char c = text[position];
                if (c is '\t' or '\n' or '\r' or '{' or '}' or ';' or ',' or ':' or '*' || c == ' ' && !stereotype) break;
                position++;
            }
            return text[begin..position].Trim();
        }
    }

    internal static bool KnownName(string name) => Names.Contains(name);

    private static bool ValidSelector(string name) => Names.Contains(name.ToLowerInvariant()) ||
        name.StartsWith('.') && name.Length is > 1 and < 160 && name.AsSpan(1).IndexOfAny('<', '>', '\\') < 0 ||
        name.StartsWith("depth(", StringComparison.OrdinalIgnoreCase) && name.EndsWith(')') && int.TryParse(name.AsSpan(6, name.Length - 7), out int n) && n is >= 0 and <= 128;

    [GeneratedRegex(@"var\(\s*-*([A-Za-z_][\w-]*)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
