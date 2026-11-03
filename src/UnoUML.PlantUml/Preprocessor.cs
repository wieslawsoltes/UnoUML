using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace UnoUML.PlantUml;

/// <summary>A bounded, render-local TIM interpreter. The host supplies any imported file text.</summary>
internal sealed partial class Preprocessor(Func<string,string?>? resolver)
{
    private const int MaxLine = 8192, MaxBytes = 1_048_576;
    private readonly record struct Origin(int Root, string? File, int Line, bool Generated);
    private sealed record Routine(string[] Parameters, string[] Lines, int Begin, int End, string? File, string? Return);
    private sealed class Unhandled : Exception { }
    private static Unhandled Unsupported() => new();
    private readonly Dictionary<string,Value> globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string,Routine> routines = new(StringComparer.Ordinal);
    private readonly HashSet<string> included = new(StringComparer.Ordinal), visiting = new(StringComparer.Ordinal);
    private readonly StringBuilder output = new();
    private readonly List<Origin> origins = [];
    private int fuel = 100_000, sourceChars;
    private bool library;

    internal static bool Needed(string source)
    {
        if (HasBuiltin(source)) return true;
        if (!source.Contains('!')) return false;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            var line = raw.TrimStart();
            // Standard-library macros have dedicated renderers or belong to the reference engine.
            if (line.StartsWith("!") && !line.StartsWith("!include <")) return true;
        }
        return false;
    }

    private static bool HasBuiltin(string source)
    {
        for (int i = source.IndexOf('%'); i >= 0; i = source.IndexOf('%', i + 1))
        {
            int end = i + 1;
            if (end >= source.Length || !(char.IsAsciiLetter(source[end]) || source[end] == '_')) continue;
            while (end < source.Length && (char.IsAsciiLetterOrDigit(source[end]) || source[end] == '_')) end++;
            if (end < source.Length && source[end] == '(') return true;
        }
        return false;
    }

    internal bool Expand(string source, out string expanded)
    {
        expanded = "";
        try
        {
            if (source.Contains("/'", StringComparison.Ordinal)) return false;
            string[] lines = source.Split('\n');
            if (lines.Length > 32000) return false;
            Run(lines, 0, lines.Length, null, null, globals, 0);
            expanded = output.ToString();
            return true;
        }
        catch (Unhandled) { return false; }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (OverflowException) { return false; }
    }

    internal string Remap(string svg)
    {
        var root = XElement.Parse(svg,LoadOptions.PreserveWhitespace);
        foreach (var node in root.DescendantsAndSelf())
        {
            foreach (var attribute in node.Attributes().ToArray())
            {
                if (attribute.Name.LocalName is not ("data-source-line" or "data-start-line" or "data-end-line") ||
                    !int.TryParse(attribute.Value, CultureInfo.InvariantCulture, out var index) || (uint)index >= (uint)origins.Count) continue;
                var origin = origins[index];
                attribute.Value = origin.Root.ToString(CultureInfo.InvariantCulture);
                if (attribute.Name.LocalName != "data-source-line" || !origin.Generated) continue;
                node.SetAttributeValue("data-source-generated","true");
                if (origin.File == null) continue;
                node.SetAttributeValue("data-source-file",origin.File);
                node.SetAttributeValue("data-file-line",origin.Line);
            }
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    internal int SourceLine(int renderedLine) =>
        (uint)renderedLine < (uint)origins.Count ? origins[renderedLine].Root : renderedLine;

    private void Charge() { if (--fuel < 0) throw Unsupported(); }

    private void Emit(string line, Origin origin)
    {
        if (line.Length > MaxLine || line.AsSpan().IndexOfAny('\u0085', '\u2028', '\u2029') >= 0 || output.Length + line.Length + 1 > MaxBytes) throw Unsupported();
        // JSON strings and function results may introduce physical lines. Every
        // generated line keeps the invocation's origin, including included files.
        if (line.Contains('\r')) line = line.ReplaceLineEndings("\n");
        foreach (var physical in line.AsSpan().EnumerateLines())
        {
            if (origins.Count >= 32000) throw Unsupported();
            if (origins.Count > 0) output.Append('\n');
            output.Append(physical);
            origins.Add(origin);
        }
    }

    private static readonly string[] IfStops = ["!elseif","!else","!endif"];
    private static string Keyword(string line)
    {
        int space = line.AsSpan().IndexOfAny(' ','\t','(');
        return space < 0 ? line : line[..space];
    }
    private static int NameEnd(string text, int pos)
    {
        if (pos + 1 >= text.Length || text[pos] is not ('$' or '%') ||
            !(char.IsAsciiLetter(text[pos+1]) || text[pos+1] == '_')) throw Unsupported();
        pos += 2;
        while (pos < text.Length && (char.IsAsciiLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
        return pos;
    }

    private int Match(string[] lines, int begin, int end, string opening, params string[] stops)
    {
        int level = 0;
        for (int i = begin; i < end; i++)
        {
            Charge();
            string command = Keyword(lines[i].Trim());
            if (command == opening) level++;
            else if (command == stops[^1] && level > 0) level--;
            else if (level == 0 && Array.IndexOf(stops,command) >= 0) return i;
        }
        throw Unsupported();
    }

    private void Run(string[] lines, int begin, int end, string? file, int? callLine, Dictionary<string,Value> locals, int depth)
    {
        if (depth > 16) throw Unsupported();
        for (int i = begin; i < end; i++)
        {
            Charge();
            string raw = lines[i], text = raw.Trim();
            if (raw.Length > MaxLine) throw Unsupported();
            var origin = new Origin(callLine ?? i,file,i,callLine.HasValue);
            if (text.Length == 0 || text[0] == '\'') { Emit(raw,origin); continue; }
            if (text.StartsWith("@start",StringComparison.OrdinalIgnoreCase) || text.StartsWith("@end",StringComparison.OrdinalIgnoreCase))
            {
                if (depth != 0) throw Unsupported();
                Emit(raw,origin); continue;
            }
            if (text[0] == '!')
            {
                string cmd = Keyword(text), rest = text[cmd.Length..].TrimStart();
                if (text.StartsWith("!$") || cmd is "!local" or "!global")
                {
                    if (text.StartsWith("!$")) rest = text[1..];
                    // Decline implicit updates of a global from a local scope: explicit !global is unambiguous.
                    int at = NameEnd(rest,0);
                    if (rest[0] != '$') throw Unsupported();
                    var target = cmd == "!global" ? globals : locals;
                    string name = rest[..at], expr = rest[at..].TrimStart();
                    bool optional = expr.StartsWith("?=");
                    if (!optional && !expr.StartsWith('=')) throw Unsupported();
                    if (optional && (locals.ContainsKey(name) || globals.ContainsKey(name))) continue;
                    if (depth > 0 && locals != globals && cmd != "!local" && cmd != "!global" && globals.ContainsKey(name) && !locals.ContainsKey(name)) throw Unsupported();
                    target[name] = Evaluate(expr[(optional ? 2 : 1)..],locals,depth);
                    continue;
                }
                if (cmd == "!if")
                {
                    bool taken = false; string? condition = rest;
                    while (true)
                    {
                        int stop = Match(lines,i+1,end,"!if",IfStops);
                        if (!taken && (condition == null || Evaluate(condition,locals,depth).True))
                        {
                            Run(lines,i+1,stop,file,callLine,locals,depth+1); taken = true;
                        }
                        i = stop; string next = lines[i].Trim(); string boundary = Keyword(next);
                        if (boundary == "!endif" && next == boundary) break;
                        if (condition == null || boundary == "!endif") throw Unsupported();
                        condition = boundary == "!elseif" ? next[boundary.Length..].Trim() : next == "!else" ? null : throw Unsupported();
                    }
                    continue;
                }
                if (cmd == "!foreach")
                {
                    int n = NameEnd(rest,0);
                    string variable = rest[..n], expression = rest[n..].Trim();
                    if (!expression.StartsWith("in ",StringComparison.Ordinal) || variable[0] != '$') throw Unsupported();
                    var array = Evaluate(expression[3..],locals,depth).Container;
                    if (array == null || array.Value.ValueKind != JsonValueKind.Array || array.Value.GetArrayLength() > 1024) throw Unsupported();
                    int stop = Match(lines,i+1,end,"!foreach","!endfor");
                    if (lines[stop].Trim() != "!endfor" || locals != globals && globals.ContainsKey(variable) && !locals.ContainsKey(variable)) throw Unsupported();
                    foreach (var element in array.Value.EnumerateArray())
                    {
                        locals[variable] = Value.Json(element);
                        Run(lines,i+1,stop,file,origin.Root,locals,depth+1);
                    }
                    i = stop; continue;
                }
                if (cmd == "!while")
                {
                    int stop = Match(lines,i+1,end,"!while","!endwhile");
                    if (lines[stop].Trim() != "!endwhile") throw Unsupported();
                    int iterations = 0;
                    while (Evaluate(rest,locals,depth).True)
                    {
                        if (++iterations > 1024) throw Unsupported();
                        Run(lines,i+1,stop,file,origin.Root,locals,depth+1);
                    }
                    i = stop; continue;
                }
                if (cmd is "!procedure" or "!function")
                {
                    if (locals != globals) throw Unsupported();
                    bool function = cmd == "!function";
                    int n = NameEnd(rest,0), close = rest.IndexOf(')',n);
                    if (close < 0 || rest[n] != '(' || rest[0] != '$') throw Unsupported();
                    string[] args = n+1 == close ? [] : rest[(n+1)..close].Split(',',StringSplitOptions.TrimEntries);
                    if (args.Length > 24 || args.Any(a => a.Length == 0 || a[0] != '$' || NameEnd(a,0) != a.Length) || args.Distinct().Count() != args.Length) throw Unsupported();
                    string trailing = rest[(close+1)..].Trim();
                    string? result = null;
                    int start = i+1, stop = start;
                    if (trailing.Length == 0)
                    {
                        string terminator = function ? "!endfunction" : "!endprocedure";
                        stop = Match(lines,start,end,cmd,terminator);
                        if (lines[stop].Trim() != terminator) throw Unsupported();
                        if (function)
                        {
                            if (stop != start+1) throw Unsupported();
                            trailing = lines[start].Trim();
                        }
                        i = stop;
                    }
                    if (function && trailing.StartsWith("!return ",StringComparison.Ordinal)) result = trailing[8..];
                    else if (function || trailing.Length > 0) throw Unsupported();
                    if (!routines.TryAdd(rest[..n],new(args,lines,start,stop,file,result))) throw Unsupported();
                    continue;
                }
                if (cmd is "!include" or "!include_many" or "!include_once")
                {
                    if (cmd == "!include" && rest.StartsWith('<') && rest.EndsWith('>')) { library = true; Emit("!include " + rest,origin); continue; }
                    Include(rest,cmd,file,origin.Root,locals,depth+1); continue;
                }
                throw Unsupported();
            }
            if (text.StartsWith('$'))
            {
                int n = NameEnd(text,0);
                if (routines.TryGetValue(text[..n],out var proc) && proc.Return == null)
                {
                    var parser = new Expression(this,text[n..],locals,depth);
                    var parameters = parser.Arguments();
                    if (n + parser.Position != text.Length) throw Unsupported();
                    Run(proc.Lines,proc.Begin,proc.End,proc.File,origin.Root,Bind(proc,parameters),depth+1);
                    continue;
                }
            }
            if (raw.Contains("##",StringComparison.Ordinal) && raw.Contains('$')) throw Unsupported();
            var replacement = library && raw.Contains('$') ? LibraryStatement(raw,locals,depth) : Substitute(raw,locals,depth);
            if (replacement.TrimStart().StartsWith('!') || replacement.TrimStart().StartsWith('@') && !raw.TrimStart().StartsWith('@')) throw Unsupported();
            Emit(replacement,origin with { Generated = origin.Generated || replacement != raw });
        }
    }

    private Dictionary<string,Value> Bind(Routine target, List<Value> args)
    {
        if (target.Parameters.Length != args.Count) throw Unsupported();
        var local = new Dictionary<string,Value>(StringComparer.Ordinal);
        for (int i = 0; i < args.Count; i++) local.Add(target.Parameters[i],args[i]);
        return local;
    }

    private string LibraryStatement(string raw, Dictionary<string,Value> locals, int depth)
    {
        // TIM passes an expression's typed result as one argument. Preserve the
        // argument boundary for labels containing e.g. commas in C4 and ArchiMate calls.
        string call = raw.Trim();
        bool opensGroup = call.EndsWith('{');
        if (opensGroup) call = call[..^1].TrimEnd();
        int open = call.IndexOf('(');
        if (open <= 0 || !call.EndsWith(')') || !LibraryMacro.Alias(call[..open])) return Substitute(raw,locals,depth);
        string name = call[..open];
        int identifiers = name.StartsWith("Rel",StringComparison.Ordinal) || name.StartsWith("BiRel",StringComparison.Ordinal) ||
            name.StartsWith("Lay",StringComparison.Ordinal) ? 2 : 1;
        var arguments = new List<string>(); int start = open+1, parens = 0; char quote = '\0';
        for (int i = start; i < call.Length; i++)
        {
            char c = call[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '(') { parens++; continue; }
            if (c == ')' && parens > 0) { parens--; continue; }
            if (parens != 0 || c != ',' && i != call.Length-1) continue;
            string arg = call[start..i].Trim();
            if (arg.StartsWith('$') || arg.StartsWith('%'))
            {
                var value = Evaluate(arg,locals,depth);
                if (arguments.Count < identifiers) { if (!LibraryMacro.Alias(value.Scalar)) throw Unsupported(); arg = value.Scalar; }
                else
                {
                    if (value.Scalar.IndexOfAny(['"', '\\', '\n', '\r']) >= 0) throw Unsupported();
                    arg = '"' + value.Scalar + '"';
                }
            }
            else arg = Substitute(arg,locals,depth);
            arguments.Add(arg); start = i+1;
        }
        if (parens != 0 || quote != '\0' || start != call.Length) throw Unsupported();
        return name + "(" + string.Join(",", arguments) + ")" + (opensGroup ? " {" : "");
    }

    private string Substitute(string raw, Dictionary<string,Value> locals, int depth)
    {
        int pos = 0; StringBuilder? result = null;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] is not ('$' or '%') || i+1 == raw.Length || !(char.IsAsciiLetter(raw[i+1]) || raw[i+1] == '_')) continue;
            var expr = new Expression(this,raw[i..],locals,depth);
            var value = expr.Reference();
            (result ??= new()).Append(raw,pos,i-pos).Append(value.Scalar);
            if (result.Length > MaxLine) throw Unsupported();
            pos = i + expr.Position;
            if (pos < raw.Length && raw[pos] == '[') throw Unsupported();
            i = pos - 1;
        }
        return result == null ? raw : result.Append(raw,pos,raw.Length-pos).ToString();
    }

    private void Include(string requested, string mode, string? parent, int rootLine, Dictionary<string,Value> locals, int depth)
    {
        if (resolver == null || depth > 16) throw Unsupported();
        string path = requested.Trim();
        if (path.StartsWith('"') && path.EndsWith('"') && path.Length > 1) path = path[1..^1];
        if (path.Length == 0 || path.StartsWith('/') || path.IndexOfAny([':', '\\', '$', '%', '!', '?', '#', '<', '>', '"']) >= 0) throw Unsupported();
        var parts = new List<string>();
        if (parent != null) { var parentParts = parent.Split('/'); parts.AddRange(parentParts[..^1]); }
        foreach (string part in path.Split('/'))
        {
            if (part == ".") continue;
            if (part.Length == 0) throw Unsupported();
            if (part == "..") { if (parts.Count == 0) throw Unsupported(); parts.RemoveAt(parts.Count-1); }
            else parts.Add(part);
        }
        string key = string.Join('/',parts);
        if (key.Length == 0 || visiting.Contains(key) || included.Count >= 128) throw Unsupported();
        if (!included.Add(key) && mode != "!include_many") { if (mode == "!include_once") throw Unsupported(); return; }
        string body = resolver(key) ?? throw Unsupported();
        if (body.Length > MaxBytes || (sourceChars += body.Length) > MaxBytes || body.Contains("/'",StringComparison.Ordinal)) throw Unsupported();
        visiting.Add(key);
        var lines = body.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n');
        int begin = 0, end = lines.Length;
        var start = Array.FindIndex(lines,l => l.TrimStart().StartsWith("@start",StringComparison.OrdinalIgnoreCase));
        if (start >= 0)
        {
            if (lines[start].Trim() != "@startuml") throw Unsupported();
            int close = Array.FindIndex(lines,start+1,l => l.Trim() == "@enduml");
            if (close < 0 || Array.Exists(lines[(close+1)..],l=>l.TrimStart().StartsWith("@start",StringComparison.OrdinalIgnoreCase))) throw Unsupported();
            begin = start+1; end = close;
        }
        Run(lines,begin,end,key,rootLine,locals,depth); visiting.Remove(key);
    }
}
