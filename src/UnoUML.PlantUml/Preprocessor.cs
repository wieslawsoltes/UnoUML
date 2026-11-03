using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace UnoUML.PlantUml;

/// <summary>A bounded, render-local TIM interpreter. The host supplies imported resource text.</summary>
internal sealed partial class Preprocessor(Func<string, string?>? resolver, PlantUmlRenderOptions? options = null,
    bool preserveStandardLibraries = false)
{
    private const int MaxLine = 65536, MaxBytes = 1_048_576;
    private readonly record struct Origin(int Root, string? File, int Line, bool Generated, string? ResourceFile = null, string? Archive = null,
        bool LiteralContinuation = false);
    private sealed class Unhandled(string? message) : Exception(message) { }
    private static Unhandled Unsupported(string? message = null) => new(message);
    private readonly Dictionary<string, Value> globals = new(StringComparer.Ordinal);
    private readonly Dictionary<Dictionary<string, Value>, HashSet<string>> localOverrides = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<Routine>> routines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Subpart>> subparts = new(StringComparer.Ordinal);
    private readonly Dictionary<string[], HashSet<int>> transformedLines = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string[], HashSet<int>> continuedLines = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> included = new(StringComparer.Ordinal), visiting = new(StringComparer.Ordinal);
    private readonly StringBuilder output = new();
    private readonly List<Origin> origins = [];
    private readonly Dictionary<string, string> renderingOptions = new(StringComparer.Ordinal);
    private int fuel = 100_000, nativeLibraryFuel = 2_000_000, sourceChars;
    private bool preservesOffsets;
    private string? currentFile;
    private Origin currentOrigin;
    private string? CurrentFile => currentFile ?? options?.SourceFileName;

    internal static bool Needed(string source)
    {
        if (source.Contains("/'", StringComparison.Ordinal) || HasBuiltin(source)) return true;
        bool yaml = IsYamlDocument(source);
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            var text = raw.TrimStart();
            if (text.StartsWith("!") && (!yaml || IsTimDirective(text, out _) || text.Contains('='))) return true;
        }
        return false;
    }

    private static bool HasBuiltin(string source)
    {
        for (int i = source.IndexOf('%'); i >= 0; i = source.IndexOf('%', i + 1))
        {
            int end = i + 1;
            if (end >= source.Length || !IsNameStart(source[end])) continue;
            while (end < source.Length && IsNamePart(source[end])) end++;
            if (end < source.Length && source[end] == '(') return true;
        }
        return false;
    }

    internal bool Expand(string source, out string expanded)
    {
        expanded = "";
        try
        {
            yamlDocument = IsYamlDocument(source);
            string[] lines = PrepareLines(source);
            Run(lines, 0, lines.Length, null, null, globals, 0);
            expanded = output.ToString();
            preservesOffsets = string.Equals(source, expanded, StringComparison.Ordinal);
            if (!preservesOffsets && yamlDocument && IsIdentityExpansion(source, expanded))
            {
                expanded = source;
                preservesOffsets = true;
            }
            return true;
        }
        catch (Exception error) when (IsHandledFailure(error)) { return false; }
    }

    internal static bool IsHandledFailure(Exception error) => error is Unhandled or JsonException or InvalidOperationException or
        OverflowException or ArgumentException or FormatException or System.Text.RegularExpressions.RegexMatchTimeoutException or TimeZoneNotFoundException;

    internal string Remap(string svg, bool projectedLibrary = false)
    {
        var sourceOrigins = projectedLibrary ? libraryProjectionOrigins ?? origins : origins;
        var root = XElement.Parse(svg, LoadOptions.PreserveWhitespace);
        foreach (string option in new[] { "svg_desc", "svg_title" })
        {
            if (!renderingOptions.TryGetValue(option, out string? value)) continue;
            XName name = root.Name.Namespace + option[4..];
            root.Elements(name).Remove();
            root.AddFirst(new XElement(name, value));
        }
        foreach (var node in root.DescendantsAndSelf())
        {
            if (!preservesOffsets && node.Attribute("data-json-start") != null)
            {
                node.SetAttributeValue("data-json-start", null);
                node.SetAttributeValue("data-json-length", null);
                node.SetAttributeValue("data-json-kind", null);
                node.SetAttributeValue("data-source-generated", "true");
            }
            if (!preservesOffsets && (node.Attribute("data-dot-start") != null || node.Attribute("data-dot-label-start") != null || node.Attribute("data-dot-label-insert") != null))
            {
                node.Attributes().Where(attribute => attribute.Name.LocalName.StartsWith("data-dot-", StringComparison.Ordinal)).Remove();
                node.SetAttributeValue("data-source-generated", "true");
            }
            if (!preservesOffsets && node.Attributes().Any(attribute => attribute.Name.LocalName.StartsWith("data-edit-", StringComparison.Ordinal)))
            {
                node.Attributes().Where(attribute => attribute.Name.LocalName.StartsWith("data-edit-", StringComparison.Ordinal)).Remove();
                node.SetAttributeValue("data-source-generated", "true");
            }
            foreach (var attribute in node.Attributes().ToArray())
            {
                if (attribute.Name.LocalName is not ("data-source-line" or "data-source-end-line" or "data-start-line" or "data-end-line") ||
                    !int.TryParse(attribute.Value, CultureInfo.InvariantCulture, out int index) || (uint)index >= (uint)sourceOrigins.Count) continue;
                var origin = sourceOrigins[index];
                attribute.Value = origin.Root.ToString(CultureInfo.InvariantCulture);
                if (attribute.Name.LocalName != "data-source-line" || !origin.Generated) continue;
                node.SetAttributeValue("data-source-generated", "true");
                if (origin.File == null) continue;
                node.SetAttributeValue("data-source-file", origin.File);
                node.SetAttributeValue("data-file-line", origin.Line);
                node.SetAttributeValue("data-source-archive", origin.Archive);
            }
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    internal int SourceLine(int renderedLine) =>
        (uint)renderedLine < (uint)origins.Count ? origins[renderedLine].Root : renderedLine;

    private void Charge()
    {
        if (currentNativeLibrary ? --nativeLibraryFuel < 0 : --fuel < 0) throw Unsupported();
    }

    private void Emit(string line, Origin origin)
    {
        if (line.Length > MaxLine || line.AsSpan().IndexOfAny('\u0085', '\u2028', '\u2029') >= 0 || output.Length + line.Length + 1 > MaxBytes) throw Unsupported();
        if (line.Contains('\r')) line = line.ReplaceLineEndings("\n");
        if (projectionSuppression == 0) AppendProjection(line, origin);
        foreach (var physical in line.AsSpan().EnumerateLines())
        {
            if (origins.Count >= 32000) throw Unsupported();
            if (origins.Count > 0) output.Append('\n');
            output.Append(physical);
            origins.Add(origin);
        }
    }

    private static readonly string[] IfStops = ["!elseif", "!else", "!endif"];
    private static bool IsNameStart(char c) => char.IsLetter(c) || char.IsSurrogate(c) || c is '_' or '$' or '%';
    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || char.IsSurrogate(c) || c == '_';

    private static string Keyword(string line)
    {
        int space = line.AsSpan().IndexOfAny(' ', '\t', '(');
        return space < 0 ? line : line[..space];
    }

    private static string CommandText(string line)
    {
        string text = line.Trim();
        if (text.StartsWith("!end ", StringComparison.Ordinal) || text.StartsWith("!end\t", StringComparison.Ordinal))
        {
            string ending = text[4..].TrimStart();
            if (ending is "function" or "procedure") return "!end" + ending;
        }
        return text;
    }

    private static int NameEnd(string text, int pos)
    {
        if (pos >= text.Length || !IsNameStart(text[pos])) throw Unsupported();
        pos++;
        while (pos < text.Length && IsNamePart(text[pos])) pos++;
        return pos;
    }

    private string[] PrepareLines(string source)
    {
        var lines = source.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n');
        if (lines.Length > 32000) throw Unsupported();
        var transformed = new HashSet<int>();
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].EndsWith('\\')) continue;
            int start = i;
            var joined = new StringBuilder();
            while (i + 1 < lines.Length && lines[i].EndsWith('\\'))
            {
                joined.Append(lines[i].AsSpan(0, lines[i].Length - 1));
                transformed.Add(i);
                lines[i++] = "";
            }
            joined.Append(lines[i]);
            if (joined.Length > MaxLine) throw Unsupported();
            lines[i] = "";
            transformed.Add(i);
            lines[start] = joined.ToString();
        }
        var continued = new HashSet<int>(transformed);
        bool comment = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > MaxLine) throw Unsupported();
            StringBuilder? filtered = null;
            int start = 0;
            char quote = '\0';
            for (int pos = 0; pos < line.Length; pos++)
            {
                if (comment)
                {
                    int end = line.IndexOf("'/", pos, StringComparison.Ordinal);
                    if (end < 0) { start = line.Length; break; }
                    comment = false;
                    pos = end + 1;
                    start = pos + 1;
                    continue;
                }
                if (quote != '\0') { if (line[pos] == quote) quote = '\0'; continue; }
                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '\'')
                {
                    (filtered ??= new()).Append(line, start, pos - start);
                    comment = true;
                    pos++;
                    start = pos + 1;
                }
                else if (line[pos] is '\'' or '"') quote = line[pos];
            }
            if (filtered != null || comment || start != 0)
            {
                lines[i] = (filtered ?? new()).Append(line, start, line.Length - start).ToString();
                transformed.Add(i);
                continued.Remove(i);
            }
        }
        if (comment) throw Unsupported();
        if (transformed.Count > 0) transformedLines.Add(lines, transformed);
        if (continued.Count > 0) continuedLines.Add(lines, continued);
        return lines;
    }

    private int Match(string[] lines, int begin, int end, string opening, params string[] stops)
    {
        int level = 0;
        for (int i = begin; i < end; i++)
        {
            Charge();
            string command = Keyword(CommandText(lines[i]));
            bool nested = command == opening || opening == "!if" && command is "!ifdef" or "!ifndef";
            if (nested) level++;
            else if (command == stops[^1] && level > 0) level--;
            else if (level == 0 && Array.IndexOf(stops, command) >= 0) return i;
        }
        throw Unsupported();
    }

    private Value? Run(string[] lines, int begin, int end, string? file, int? callLine,
        Dictionary<string, Value> locals, int depth, bool returning = false)
    {
        if (depth > 64) throw Unsupported();
        string? previousFile = currentFile;
        bool previousNativeLibrary = currentNativeLibrary;
        var previousOrigin = currentOrigin;
        currentFile = resourceParents.TryGetValue(lines, out var resourceParent) ? resourceParent : file;
        currentNativeLibrary = nativeLibrarySources.Contains(lines);
        try
        {
            for (int i = begin; i < end; i++)
            {
                Charge();
                string raw = lines[i], text = CommandText(raw);
                currentOrigin = new(callLine ?? i, resourceMembers.GetValueOrDefault(lines) ?? file, i,
                    callLine.HasValue || transformedLines.TryGetValue(lines, out var transformed) && transformed.Contains(i), CurrentFile,
                    resourceArchives.GetValueOrDefault(lines),
                    !callLine.HasValue && continuedLines.TryGetValue(lines, out var continued) && continued.Contains(i));
                var origin = currentOrigin;
                if (text.Length == 0 || text[0] == '\'') { if (!returning && text.Length == 0) Emit(raw, origin); continue; }
                if (text.StartsWith("@start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("@end", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth != 0) throw Unsupported();
                    Emit(raw, origin);
                    continue;
                }
                if (text[0] != '!')
                {
                    ExpandStatement(raw, origin, locals, depth, !returning);
                    continue;
                }
                string cmd = yamlDocument && IsTimDirective(text, out int keywordLength) ? text[..keywordLength] : Keyword(text);
                string rest = text[cmd.Length..].TrimStart();
                if (TryAssignment(text, out string variable, out string? scope, out string expression, out bool optional))
                {
                    if (optional && TryGetVariable(variable, locals, out _)) continue;
                    if (expression.StartsWith('{') || expression.StartsWith('['))
                    {
                        while (!CompleteJson(expression))
                        {
                            if (++i >= end || expression.Length + lines[i].Length > MaxBytes) throw Unsupported();
                            expression += lines[i];
                        }
                    }
                    SetVariable(variable, Evaluate(expression, locals, depth), locals, scope);
                    continue;
                }
                if (cmd is "!if" or "!ifdef" or "!ifndef")
                {
                    bool taken = false;
                    string? condition = rest;
                    string conditionKind = cmd;
                    while (true)
                    {
                        int stop = Match(lines, i + 1, end, "!if", IfStops);
                        bool take = !taken && (condition == null || (conditionKind == "!if" ? Evaluate(condition, locals, depth).True :
                            Defined(condition, locals) != (conditionKind == "!ifndef")));
                        if (take)
                        {
                            var result = Run(lines, i + 1, stop, file, callLine, locals, depth + 1, returning);
                            if (result.HasValue) return result;
                            taken = true;
                        }
                        i = stop;
                        string next = lines[i].Trim(), boundary = Keyword(next);
                        if (boundary == "!endif" && next == boundary) break;
                        if (condition == null || boundary == "!endif") throw Unsupported();
                        condition = boundary == "!elseif" ? next[boundary.Length..].Trim() : next == "!else" ? null : throw Unsupported();
                        conditionKind = "!if";
                    }
                    continue;
                }
                if (cmd == "!foreach")
                {
                    int n = NameEnd(rest, 0);
                    string name = rest[..n], expressionText = rest[n..].TrimStart();
                    if (!expressionText.StartsWith("in", StringComparison.Ordinal) || expressionText.Length < 3 || !char.IsWhiteSpace(expressionText[2])) throw Unsupported();
                    var data = Evaluate(expressionText[3..], locals, depth).Container;
                    int stop = Match(lines, i + 1, end, "!foreach", "!endfor");
                    if (lines[stop].Trim() != "!endfor") throw Unsupported();
                    if (data is { } container)
                    {
                        IEnumerable<Value> values = container.ValueKind switch
                        {
                            JsonValueKind.Array => container.EnumerateArray().Select(Value.Json),
                            JsonValueKind.Object => container.EnumerateObject().Select(p => JsonString(p.Name)),
                            _ => throw Unsupported()
                        };
                        foreach (var value in values)
                        {
                            Charge();
                            SetVariable(name, value, locals, "global");
                            var result = Run(lines, i + 1, stop, file, origin.Root, locals, depth + 1, returning);
                            if (result.HasValue) return result;
                        }
                    }
                    i = stop;
                    continue;
                }
                if (cmd == "!while")
                {
                    int stop = Match(lines, i + 1, end, "!while", "!endwhile");
                    if (lines[stop].Trim() != "!endwhile") throw Unsupported();
                    while (Evaluate(rest, locals, depth).True)
                    {
                        Charge();
                        var result = Run(lines, i + 1, stop, file, origin.Root, locals, depth + 1, returning);
                        if (result.HasValue) return result;
                    }
                    i = stop;
                    continue;
                }
                if (cmd is "!function" or "!procedure" or "!unquoted" or "!final" or "!define" or "!definelong")
                {
                    DeclareRoutine(lines, ref i, end, file, text, locals, depth);
                    continue;
                }
                if (cmd == "!return")
                {
                    if (!returning) throw Unsupported();
                    return Evaluate(rest, locals, depth);
                }
                if (cmd is "!include" or "!includeurl" or "!include_many" or "!include_once")
                {
                    string requested = Substitute(rest, locals, depth).Trim();
                    Include(requested, cmd, CurrentFile, origin.Root, locals, depth + 1);
                    continue;
                }
                if (cmd == "!includesub") { IncludeSub(Substitute(rest, locals, depth), origin.Root, locals, depth + 1); continue; }
                if (cmd == "!includedef") { IncludeDefinition(Substitute(rest, locals, depth), origin.Root, locals, depth + 1); continue; }
                if (cmd == "!startsub")
                {
                    int stop = Match(lines, i + 1, end, "!startsub", "!endsub");
                    string name = Substitute(rest, locals, depth);
                    subparts[name] = [new(lines, i + 1, stop, file)];
                    var result = Run(lines, i + 1, stop, file, callLine, locals, depth + 1, returning);
                    if (result.HasValue) return result;
                    i = stop;
                    continue;
                }
                if (cmd == "!theme") { IncludeTheme(Substitute(rest, locals, depth), origin.Root, locals, depth + 1); continue; }
                if (cmd == "!undef") { RemoveVariable(rest, locals); continue; }
                if (cmd == "!assert")
                {
                    int colon = TopLevelDelimiter(rest, ':');
                    if (!Evaluate(colon < 0 ? rest : rest[..colon], locals, depth).True) throw Unsupported();
                    continue;
                }
                if (cmd is "!log" or "!dump_memory") { ReportDiagnostic(cmd, Substitute(rest, locals, depth), locals); continue; }
                if (cmd == "!import") { ImportResource(Substitute(rest, locals, depth)); continue; }
                if (cmd == "!option")
                {
                    int nameEnd = NameEnd(rest, 0);
                    string optionName = new(rest[..nameEnd].Where(char.IsAsciiLetter).Select(char.ToLowerInvariant).ToArray());
                    string valueText = rest[nameEnd..].Trim();
                    string? key = optionName switch
                    {
                        "language" or "handwritten" or "debug" => optionName,
                        "usedescriptivenames" => "use_descriptive_names", "svgdesc" => "svg_desc", "svgtitle" => "svg_title",
                        _ => null
                    };
                    string? value = null;
                    try { if (valueText.Length > 0) value = Evaluate(valueText, locals, depth).Scalar; }
                    catch (Unhandled) { }
                    if (value == null && key is "handwritten" or "debug") value = "true";
                    if (key == null || value == null) { options?.Log?.Invoke("Ignored preprocessing option " + optionName); continue; }
                    renderingOptions[key] = value;
                    if (key == "handwritten") Emit("skinparam handwritten " + value, origin with { Generated = true });
                    continue;
                }
                if (cmd is "!pragma" or "!assume") { if (!returning) Emit(Substitute(raw, locals, depth), origin); continue; }
                if (yamlDocument && !IsTimDirective(text, out _))
                {
                    ExpandStatement(raw, origin, locals, depth, !returning);
                    continue;
                }
                throw Unsupported();
            }
            return null;
        }
        finally { currentFile = previousFile; currentNativeLibrary = previousNativeLibrary; currentOrigin = previousOrigin; }
    }

    private static Value JsonString(string text)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) writer.WriteStringValue(text);
        using var json = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
        return Value.Json(json.RootElement);
    }

    private static bool CompleteJson(string text)
    {
        int level = 0; bool quote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote && c == '\\') { i++; continue; }
            if (c == '"') quote = !quote;
            if (quote) continue;
            if (c is '[' or '{') level++;
            if (c is ']' or '}') level--;
        }
        return !quote && level <= 0;
    }

    private static bool TryAssignment(string text, out string name, out string? scope, out string expression, out bool optional)
    {
        name = expression = ""; scope = null; optional = false;
        string rest = text[1..].TrimStart();
        if (rest.Length == 0 || !IsNameStart(rest[0]) || rest[0] == '%') return false;
        int end = NameEnd(rest, 0);
        name = rest[..end];
        rest = rest[end..].TrimStart();
        if (name is "local" or "global" && rest.Length > 0 && rest[0] is not ('?' or '='))
        {
            scope = name;
            end = NameEnd(rest, 0);
            name = rest[..end];
            rest = rest[end..].TrimStart();
        }
        optional = rest.StartsWith("?=", StringComparison.Ordinal);
        if (!optional && !rest.StartsWith('=')) return false;
        if (name.StartsWith('%')) throw Unsupported();
        expression = rest[(optional ? 2 : 1)..].TrimStart();
        return true;
    }

    private bool TryGetVariable(string name, Dictionary<string, Value> locals, out Value value)
    {
        if (localOverrides.TryGetValue(locals, out var overrides) && overrides.Contains(name) && locals.TryGetValue(name, out value)) return true;
        if (currentNativeLibrary && userGlobals.Contains(name) && globals.ContainsKey(name)) DisableProjection();
        return globals.TryGetValue(name, out value) || locals.TryGetValue(name, out value);
    }

    private void SetVariable(string name, Value value, Dictionary<string, Value> locals, string? scope = null)
    {
        if (NameEnd(name, 0) != name.Length || name[0] == '%') throw Unsupported();
        bool explicitLocal = scope == "local" && locals != globals;
        if (explicitLocal)
        {
            if (!localOverrides.TryGetValue(locals, out var overrides)) localOverrides[locals] = overrides = new(StringComparer.Ordinal);
            overrides.Add(name);
        }
        bool overridden = localOverrides.TryGetValue(locals, out var names) && names.Contains(name);
        var target = scope == "global" || !explicitLocal && !overridden && globals.ContainsKey(name) ? globals : locals;
        if (preserveStandardLibraries && !currentNativeLibrary && target == globals) userGlobals.Add(name);
        target[name] = value;
    }

    private void RemoveVariable(string name, Dictionary<string, Value> locals)
    {
        if (NameEnd(name, 0) != name.Length) throw Unsupported();
        if (localOverrides.TryGetValue(locals, out var overrides) && overrides.Remove(name)) locals.Remove(name);
        else if (!globals.Remove(name)) locals.Remove(name);
    }

    private string Substitute(string raw, Dictionary<string, Value> locals, int depth) =>
        SubstituteText(raw, locals, depth) ?? throw Unsupported();

    private string? SubstituteText(string raw, Dictionary<string, Value> locals, int depth, Origin? statementOrigin = null)
    {
        if (depth > 64) throw Unsupported();
        int copied = 0; StringBuilder? result = null;
        for (int i = 0; i < raw.Length; i++)
        {
            if (!IsNameStart(raw[i])) continue;
            int end = NameEnd(raw, i);
            string name = raw[i..end];
            bool boundary = i == 0 || raw[i] is '$' or '%' || !IsNamePart(raw[i - 1]) || i > 1 && raw[i - 2] == '\\' && raw[i - 1] == 'n';
            bool call = end < raw.Length && raw[end] == '(' && (routines.ContainsKey(name) || IsBuiltin(name));
            if (!boundary || !call && !TryGetVariable(name, locals, out _))
            {
                if (raw[i] is not ('$' or '%')) i = end - 1;
                continue;
            }
            Value value;
            int consumed;
            if (call && statementOrigin is { } origin &&
                (name == "%invoke_procedure" || routines.TryGetValue(name, out var candidates) && candidates.Any(routine => !routine.Returns)))
            {
                var parser = new Expression(this, raw[end..], locals, depth);
                var args = parser.Arguments(unquoted: RoutineIsUnquoted(name));
                var routine = name == "%invoke_procedure" ? null : SelectRoutine(name, args);
                consumed = end - i + parser.Position;
                if (routine?.Returns == true) value = InvokeFunction(name, args, locals, depth + 1);
                else
                {
                    result ??= new();
                    result.Append(raw, copied, i - copied);
                    string prefix = result.ToString();
                    string suffix = raw[(i + consumed)..];
                    int length = output.Length, count = origins.Count;
                    if (routine == null)
                    {
                        if (args.Named.Count != 0 || args.Positional.Count == 0) throw Unsupported();
                        string procedureName = args.Positional[0].Scalar;
                        args = new(args.Positional.Skip(1).ToList(), new(StringComparer.Ordinal));
                        routine = SelectRoutine(procedureName, args);
                    }
                    bool projected = ProjectProcedure(routine, args, prefix, suffix, origin);
                    if (projected) projectionSuppression++;
                    else if (projectionSuppression == 0 && (!string.IsNullOrWhiteSpace(prefix) || !string.IsNullOrWhiteSpace(suffix))) DisableProjection();
                    try { ExecuteProcedure(routine, args, depth + 1); }
                    finally { if (projected) projectionSuppression--; }
                    if (output.Length + prefix.Length + suffix.Length > MaxBytes) throw Unsupported();
                    if (origins.Count > count)
                    {
                        output.Insert(length + (count > 0 ? 1 : 0), prefix);
                        output.Append(suffix);
                    }
                    else if (prefix.Length + suffix.Length > 0) Emit(prefix + suffix, origin with { Generated = true });
                    return null;
                }
            }
            else
            {
                var expr = new Expression(this, raw[i..], locals, depth);
                value = expr.Reference();
                consumed = expr.Position;
            }
            result ??= new();
            result.Append(raw, copied, i - copied);
            if (result.Length >= 2 && result[^1] == '#' && result[^2] == '#') result.Length -= 2;
            result.Append(value.Scalar);
            if (result.Length > MaxBytes) throw Unsupported();
            copied = i + consumed;
            if (copied + 1 < raw.Length && raw[copied] == '#' && raw[copied + 1] == '#') copied += 2;
            i = copied - 1;
        }
        return result == null ? raw : result.Append(raw, copied, raw.Length - copied).ToString();
    }

}
