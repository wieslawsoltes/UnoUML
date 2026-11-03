using System.Text;

namespace UnoUML.PlantUml;

internal sealed partial class Preprocessor
{
    private sealed record Parameter(string Name, Value? Default);
    private sealed record Routine(string Name, Parameter[] Parameters, string[] Lines, int Begin, int End,
        string? File, bool Returns, bool Unquoted, bool Final, string? LegacyText = null);

    private static int TopLevelDelimiter(string text, char delimiter)
    {
        int level = 0; char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (level == 0 && c == delimiter) return i;
            if (c is '(' or '[' or '{') level++;
            else if (c is ')' or ']' or '}') level--;
            if (level < 0) throw Unsupported();
        }
        if (level != 0 || quote != '\0') throw Unsupported();
        return -1;
    }

    private void DeclareRoutine(string[] lines, ref int line, int end, string? file, string declaration,
        Dictionary<string, Value> locals, int depth)
    {
        string rest = declaration[1..].TrimStart();
        bool unquoted = false, final = false;
        string kind = Keyword(rest);
        while (kind is "unquoted" or "final")
        {
            unquoted |= kind == "unquoted";
            final |= kind == "final";
            rest = rest[kind.Length..].TrimStart();
            kind = Keyword(rest);
        }
        if (kind is not ("function" or "procedure" or "define" or "definelong")) throw Unsupported();
        rest = rest[kind.Length..].TrimStart();
        int nameEnd = NameEnd(rest, 0);
        string name = rest[..nameEnd];
        if (name[0] == '%') throw Unsupported();
        rest = rest[nameEnd..].TrimStart();
        if (kind == "define" && !rest.StartsWith('('))
        {
            SetVariable(name, Value.String(Substitute(rest, locals, depth)), locals, "global");
            return;
        }
        var parameters = new List<Parameter>();
        if (rest.StartsWith('('))
        {
            int close = TopLevelDelimiter(rest[1..], ')') + 1;
            if (close <= 0) throw Unsupported();
            string list = rest[1..close];
            while (list.Length > 0)
            {
                int comma = TopLevelDelimiter(list, ',');
                string argument = (comma < 0 ? list : list[..comma]).Trim();
                int parameterEnd = NameEnd(argument, 0);
                string parameterName = argument[..parameterEnd];
                string value = argument[parameterEnd..].TrimStart();
                if (parameterName[0] == '%' || parameters.Any(p => p.Name == parameterName) || parameters.Count >= 64) throw Unsupported();
                Value? defaultValue = null;
                if (value.Length > 0)
                {
                    if (!value.StartsWith('=')) throw Unsupported();
                    defaultValue = Evaluate(value[1..], locals, depth);
                }
                parameters.Add(new(parameterName, defaultValue));
                if (comma < 0) break;
                list = list[(comma + 1)..].TrimStart();
                if (list.Length == 0) throw Unsupported();
            }
            rest = rest[(close + 1)..].Trim();
        }
        else if (kind != "definelong") throw Unsupported();
        bool returns = kind is "function" or "define";
        string? legacy = kind == "define" ? rest : null;
        string[] body = lines;
        int begin = line + 1, stop = begin;
        if (kind == "function" && Keyword(rest) is "!return" or "return")
        {
            body = [rest.StartsWith('!') ? rest : "!" + rest];
            begin = 0; stop = 1;
        }
        else if (kind != "define")
        {
            if (rest.Length != 0) throw Unsupported();
            string terminator = "!end" + kind;
            stop = Match(lines, begin, end, "!" + kind, terminator);
            if (CommandText(lines[stop]) != terminator) throw Unsupported();
            line = stop;
            if (kind == "definelong" && stop == begin + 1) { legacy = lines[begin]; returns = true; }
            if (kind == "function" && !lines[begin..stop].Any(s => Keyword(s.TrimStart()) == "!return")) throw Unsupported();
            if (kind == "procedure" && lines[begin..stop].Any(s => Keyword(s.TrimStart()) == "!return")) throw Unsupported();
        }
        if (!routines.TryGetValue(name, out var overloads)) routines[name] = overloads = [];
        int existing = overloads.FindIndex(r => r.Parameters.Length == parameters.Count);
        if (existing >= 0)
        {
            if (kind is "function" or "procedure" && (final || overloads[existing].Final)) throw Unsupported();
            overloads.RemoveAt(existing);
        }
        if (routines.Count > 4096) throw Unsupported();
        if (currentNativeLibrary) nativeLibrarySources.Add(body);
        overloads.Add(new(name, [.. parameters], body, begin, stop, file, returns,
            unquoted || kind is "define" or "definelong", final, legacy));
    }

    private bool FunctionExists(string name) => routines.ContainsKey(name) || IsBuiltin(name);
    private bool RoutineIsUnquoted(string name) => routines.TryGetValue(name, out var list) && list.Any(routine => routine.Unquoted);

    private Routine SelectRoutine(string name, CallArguments args)
    {
        if (!routines.TryGetValue(name, out var candidates)) throw Unsupported("Unknown routine " + name);
        Routine? selected = null;
        foreach (var candidate in candidates)
        {
            int positional = 0;
            if (args.Named.Keys.Any(n => !candidate.Parameters.Any(p => p.Name == n))) continue;
            bool fits = true;
            foreach (var parameter in candidate.Parameters)
            {
                if (args.Named.ContainsKey(parameter.Name)) continue;
                if (positional < args.Positional.Count) positional++;
                else if (parameter.Default == null) { fits = false; break; }
            }
            if (!fits || positional != args.Positional.Count) continue;
            if (candidate.Parameters.Length == args.Positional.Count && args.Named.Count == 0) return candidate;
            if (selected == null || candidate.Parameters.Length < selected.Parameters.Length) selected = candidate;
        }
        return selected ?? throw Unsupported();
    }

    private Dictionary<string, Value> Bind(Routine routine, CallArguments args)
    {
        var locals = new Dictionary<string, Value>(StringComparer.Ordinal);
        int positional = 0;
        foreach (var parameter in routine.Parameters)
        {
            Value value = args.Named.TryGetValue(parameter.Name, out var named) ? named :
                positional < args.Positional.Count ? args.Positional[positional++] : parameter.Default ?? throw Unsupported();
            locals.Add(parameter.Name, value);
        }
        localOverrides.Add(locals, new(locals.Keys, StringComparer.Ordinal));
        return locals;
    }

    private Value InvokeFunction(string name, CallArguments args, Dictionary<string, Value> caller, int depth)
    {
        var routine = SelectRoutine(name, args);
        if (currentNativeLibrary && !nativeLibrarySources.Contains(routine.Lines)) DisableProjection();
        if (!routine.Returns || depth > 64) throw Unsupported();
        var locals = Bind(routine, args);
        try
        {
            if (routine.LegacyText != null) return Value.String(Substitute(routine.LegacyText, locals, depth));
            return Run(routine.Lines, routine.Begin, routine.End, routine.File, currentOrigin.Root, locals, depth, true) ?? throw Unsupported();
        }
        finally { localOverrides.Remove(locals); }
    }

    private Value CallFunction(string name, IReadOnlyList<Value> args, Dictionary<string, Value> locals, int depth)
    {
        if (name.StartsWith('%') && !routines.ContainsKey(name)) return InvokeBuiltin(name, args, locals, depth + 1);
        return InvokeFunction(name, new([.. args], new(StringComparer.Ordinal)), locals, depth + 1);
    }

    private void InvokeProcedure(string name, IReadOnlyList<Value> args, Dictionary<string, Value> locals, int depth) =>
        ExecuteProcedure(SelectRoutine(name, new([.. args], new(StringComparer.Ordinal))),
            new([.. args], new(StringComparer.Ordinal)), depth + 1);

    private void ExecuteProcedure(Routine routine, CallArguments args, int depth)
    {
        if (currentNativeLibrary && !nativeLibrarySources.Contains(routine.Lines)) DisableProjection();
        if (routine.Returns || depth > 64) throw Unsupported();
        var locals = Bind(routine, args);
        try { Run(routine.Lines, routine.Begin, routine.End, routine.File, currentOrigin.Root, locals, depth); }
        finally { localOverrides.Remove(locals); }
    }

    private Value RetrieveProcedure(string name, IReadOnlyList<Value> args, Dictionary<string, Value> locals, int depth)
    {
        DisableProjection();
        int length = output.Length, count = origins.Count;
        try
        {
            InvokeProcedure(name, args, locals, depth);
            int begin = length + (count > 0 && origins.Count > count ? 1 : 0);
            return Value.String(output.ToString(begin, output.Length - begin));
        }
        finally { output.Length = length; origins.RemoveRange(count, origins.Count - count); }
    }

    private void ExpandStatement(string raw, Origin origin, Dictionary<string, Value> locals, int depth, bool emitLiteral = true)
    {
        string? replacement = SubstituteText(raw, locals, depth, origin);
        if (replacement == null || !emitLiteral) return;
        if (replacement.TrimStart().StartsWith('@') && !raw.TrimStart().StartsWith('@')) throw Unsupported();
        Emit(replacement, origin with { Generated = origin.Generated || replacement != raw });
    }

    private bool Defined(string expression, Dictionary<string, Value> locals)
    {
        int pos = 0;
        void Space() { while (pos < expression.Length && char.IsWhiteSpace(expression[pos])) pos++; }
        bool Consume(char token)
        {
            Space();
            if (pos == expression.Length || expression[pos] != token) return false;
            pos++;
            if (token is '&' or '|' && pos < expression.Length && expression[pos] == token) pos++;
            return true;
        }
        bool Term(int nesting)
        {
            if (nesting > 64) throw Unsupported();
            if (Consume('!')) return !Term(nesting + 1);
            if (Consume('(')) { bool value = Or(nesting + 1); if (!Consume(')')) throw Unsupported(); return value; }
            Space();
            int end = NameEnd(expression, pos);
            string name = expression[pos..end];
            pos = end;
            return TryGetVariable(name, locals, out _) || FunctionExists(name);
        }
        bool And(int nesting) { bool result = Term(nesting); while (Consume('&')) { bool next = Term(nesting); result &= next; } return result; }
        bool Or(int nesting) { bool result = And(nesting); while (Consume('|')) { bool next = And(nesting); result |= next; } return result; }
        bool answer = Or(0);
        Space();
        if (pos != expression.Length) throw Unsupported();
        return answer;
    }

    private void ReportDiagnostic(string directive, string message, Dictionary<string, Value> locals)
    {
        if (options?.Log == null) return;
        if (directive == "!log") { options.Log(message); return; }
        var names = globals.Keys.Concat(locals.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var report = new StringBuilder(message);
        foreach (string name in names)
            if (TryGetVariable(name, locals, out var value)) report.Append('\n').Append(name).Append(" = ").Append(value.Scalar);
        options.Log(report.ToString());
    }
}
