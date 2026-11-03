namespace UnoUML.Core;

internal static class C4Syntax
{
    internal readonly record struct Argument(int Start, int Length, string Text, string Name = "");

    internal static string IncludeModule(string code)
    {
        var line = code.AsSpan().Trim();
        if (line.Length <= 8 || !line[..8].Equals("!include", StringComparison.OrdinalIgnoreCase) || !char.IsWhiteSpace(line[8])) return "";
        return line[8..].Trim().ToString().ToLowerInvariant() switch
        {
            "<c4/c4_context>" => "Context",
            "<c4/c4_container>" => "Container",
            "<c4/c4_component>" => "Component",
            "<c4/c4_deployment>" => "Deployment",
            _ => ""
        };
    }

    internal static string IncludedModule(string source)
    {
        bool comment = false;
        foreach (var line in source.AsSpan().EnumerateLines())
        {
            string module = IncludeModule(DiagramIndex.MaskComments(line.ToString(), ref comment));
            if (module.Length > 0) return module;
        }
        return "";
    }

    internal static bool ToolAvailable(ToolItem tool, DiagramPage page)
    {
        string module = IncludedModule(page.Source);
        int open = tool.Snippet.IndexOf('(');
        if (module.Length == 0 || open < 1) return false;
        string macro = tool.Snippet[..open];
        if (macro.StartsWith("Container", StringComparison.Ordinal)) return module is "Container" or "Component" or "Deployment";
        if (macro.StartsWith("Component", StringComparison.Ordinal)) return module == "Component";
        if (macro is "Node" or "Deployment_Node") return module == "Deployment";
        return IsNode(macro);
    }

    internal static bool IsAlias(string text) => text.Length > 0 && (char.IsAsciiLetter(text[0]) || text[0] == '_') &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.') && !text.EndsWith('.') && !text.Contains("..", StringComparison.Ordinal);

    internal static bool IsNode(string macro)
    {
        string name = macro.EndsWith("_Ext", StringComparison.Ordinal) ? macro[..^4] : macro;
        return name is "Person" or "System" or "SystemDb" or "SystemQueue" or
            "Container" or "ContainerDb" or "ContainerQueue" or "Component" or "ComponentDb" or "ComponentQueue" ||
            macro is "Boundary" or "Enterprise_Boundary" or "System_Boundary" or "Container_Boundary" or "Node" or "Deployment_Node";
    }

    internal static bool IsGroup(string macro) => macro is "Boundary" or "Enterprise_Boundary" or "System_Boundary" or
        "Container_Boundary" or "Node" or "Deployment_Node";

    internal static bool IsRelationship(string macro)
    {
        bool bidirectional = macro.StartsWith("BiRel", StringComparison.Ordinal);
        if (!bidirectional && !macro.StartsWith("Rel", StringComparison.Ordinal)) return false;
        string suffix = macro[(bidirectional ? 5 : 3)..];
        return suffix is "" or "_R" or "_Right" or "_L" or "_Left" or "_U" or "_Up" or "_D" or "_Down" or "_Neighbor" ||
            !bidirectional && suffix is "_Back" or "_Back_Neighbor";
    }

    internal static string? RelationshipMacro(string arrow) => IsRelationship(arrow) ? arrow : arrow switch
    {
        "->" or "-->" => "Rel",
        "<-" or "<--" => "Rel_Back",
        "<->" or "<-->" => "BiRel",
        _ => null
    };

    internal static bool Literal(string value, out string text)
    {
        text = "";
        if (value.Length < 2 || value[0] is not ('"' or '\'') || value[^1] != value[0]) return false;
        text = value[1..^1];
        // Evaluated text cannot be rewritten as a literal while preserving its source expression.
        if (text.Any(c => c == value[0] || c is '$' or '%' || char.IsControl(c))) return false;
        text = text.Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("<U+0022>", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("<U+0024>", "$", StringComparison.OrdinalIgnoreCase)
            .Replace("<U+0025>", "%", StringComparison.OrdinalIgnoreCase)
            .Replace("<U+005C>", "\\", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    internal static string? Quote(string text)
    {
        if (text.Length > 16384 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r'))) return null;
        // TIM substitutes variables inside quoted arguments. Unicode escapes are
        // interpreted by the text painter after preprocessing and preserve literal glyphs.
        return "\"" + text.ReplaceLineEndings("\n").Replace("\\", "<U+005C>", StringComparison.Ordinal)
            .Replace("\"", "<U+0022>", StringComparison.Ordinal).Replace("$", "<U+0024>", StringComparison.Ordinal)
            .Replace("%", "<U+0025>", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    internal static bool LiteralOption(string argument)
    {
        if (argument.StartsWith('$'))
        {
            int assignment = argument.IndexOf('=');
            if (assignment < 2 || !IsAlias(argument[1..assignment].Trim())) return false;
            argument = argument[(assignment + 1)..].Trim();
        }
        return Literal(argument, out _) || argument.Length > 0 && argument.All(char.IsAsciiDigit);
    }

    internal static bool Call(string code, out string macro, out int macroStart, out bool group, out List<Argument> arguments)
    {
        macro = "";
        macroStart = 0;
        group = false;
        arguments = [];
        if (code.Length > 32768) return false;
        while (macroStart < code.Length && char.IsWhiteSpace(code[macroStart])) macroStart++;
        int position = macroStart;
        while (position < code.Length && (char.IsAsciiLetterOrDigit(code[position]) || code[position] == '_')) position++;
        if (position == macroStart) return false;
        macro = code[macroStart..position];
        while (position < code.Length && char.IsWhiteSpace(code[position])) position++;
        if (position == code.Length || code[position++] != '(') return false;
        int start = position;
        char quote = '\0';
        while (position < code.Length)
        {
            char character = code[position];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else if (character == '\\' && position + 1 < code.Length && code[position + 1] == quote) return false;
            }
            else if (character is '"' or '\'') quote = character;
            else
            {
                if (character == '(') return false;
                if (character is ',' or ')')
                {
                    int end = position;
                    while (start < end && char.IsWhiteSpace(code[start])) start++;
                    while (end > start && char.IsWhiteSpace(code[end - 1])) end--;
                    if (end == start || arguments.Count >= 16) return false;
                    string name = "";
                    if (code[start] == '$')
                    {
                        int assignment = code.IndexOf('=', start, end - start);
                        if (assignment > start + 1 && IsAlias(code[(start + 1)..assignment].Trim()))
                        {
                            name = code[(start + 1)..assignment].Trim();
                            start = assignment + 1;
                            while (start < end && char.IsWhiteSpace(code[start])) start++;
                        }
                    }
                    arguments.Add(new(start, end - start, code[start..end], name));
                    start = position + 1;
                    if (character == ')')
                    {
                        string tail = code[(position + 1)..].Trim();
                        group = tail == "{";
                        return tail.Length == 0 || group;
                    }
                }
            }
            position++;
        }
        return false;
    }

    internal static string MaskComments(string source)
    {
        char[] code = source.ToCharArray();
        char quote = '\0';
        int parentheses = 0;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '"' || c == '\'' && parentheses > 0) { quote = c; continue; }
            if (c == '(') parentheses++;
            else if (c == ')') parentheses--;
            else if (c == '\'')
            {
                while (i < code.Length && code[i] is not ('\r' or '\n')) code[i++] = ' ';
            }
        }
        return new string(code);
    }

    internal static Argument? Parameter(List<Argument> arguments, int position, string name)
    {
        Argument? result = null;
        int ordinal = 0;
        bool named = false;
        foreach (var argument in arguments)
        {
            if (argument.Name.Length > 0) named = true;
            else if (named) return null;
            bool matches = argument.Name.Length == 0 ? ordinal++ == position : argument.Name == name;
            if (!matches) continue;
            if (result.HasValue) return null;
            result = argument;
        }
        return result;
    }
}

internal sealed partial class DiagramIndex
{
    private string C4Code(int line, int limit, out int last)
    {
        last = line;
        string initial = Lines[line].Code.TrimEnd();
        int open = initial.IndexOf('(');
        string name = open > 0 ? initial[..open].Trim() : "";
        if (!C4Syntax.IsNode(name) && !C4Syntax.IsRelationship(name)) return initial;
        var code = new System.Text.StringBuilder();
        for (int index = line; index <= limit && index < line + 256; index++)
        {
            var row = Lines[index];
            string part = _source[row.Start..row.End];
            int end = part.AsSpan().TrimEnd().Length;
            bool continuation = end > 0 && part[end - 1] == '\\';
            if (continuation) part = part[..(end - 1)] + " " + part[end..];
            code.Append(part);
            last = index;
            if (!continuation) break;
            code.Append(_source.AsSpan(row.End, row.Next - row.End));
        }
        return C4Syntax.MaskComments(code.ToString()).TrimEnd();
    }

    private DiagramObject? C4Object(int line, int end, int parent, string code)
    {
        if (!C4Syntax.Call(code, out string macro, out int macroStart, out bool group, out var arguments)) return null;
        bool relation = C4Syntax.IsRelationship(macro);
        if (!relation && !C4Syntax.IsNode(macro) || group && !C4Syntax.IsGroup(macro)) return null;
        int labelIndex = relation ? 2 : 1;
        var alias = C4Syntax.Parameter(arguments, 0, relation ? "from" : "alias");
        var target = relation ? C4Syntax.Parameter(arguments, 1, "to") : null;
        var caption = C4Syntax.Parameter(arguments, labelIndex, "label");
        if (alias is not { } identity || caption is not { } labelArgument || !C4Syntax.IsAlias(identity.Text) ||
            relation && (target is not { } destination || !C4Syntax.IsAlias(destination.Text)) ||
            !C4Syntax.Literal(labelArgument.Text, out string label) || !relation && label.Length == 0 ||
            arguments.Any(a => a != identity && a != target && a != labelArgument && !C4Syntax.LiteralOption(a.Text))) return null;
        var row = Lines[line];
        var item = new DiagramObject(_source, row.Start, Lines[end].End, line, end, parent, relation ? "Relationship" : macro, label)
        {
            C4Macro = macro,
            LabelAt = labelArgument.Start,
            LabelLength = labelArgument.Length,
            MultilineLabel = label.Contains('\n')
        };
        if (relation)
        {
            item.From = identity.Text;
            item.FromAt = identity.Start;
            item.To = target!.Value.Text;
            item.ToAt = target.Value.Start;
            item.Arrow = macro;
            item.ArrowAt = macroStart;
        }
        else
        {
            item.Alias = identity.Text;
            item.AliasAt = identity.Start;
            item.AliasLength = identity.Length;
        }
        return item;
    }
}

public sealed partial class StudioDocument
{
    private bool C4Endpoint(DiagramObject item) => Current(item) && item.IsConnectable && C4Syntax.IsNode(item.C4Macro);

    private bool RenameC4Label(DiagramObject item, string label)
    {
        if (!Current(item) || !item.CanRename || item.C4Macro.Length == 0 || label == item.Label) return false;
        if (item.Kind == "Relationship") return UpdateC4Connection(item, null, null, item.Arrow, label);
        string? replacement = C4Syntax.Quote(label);
        return label.Length > 0 && replacement != null &&
            Patch(item.Offset + item.LabelAt, item.Offset + item.LabelAt + item.LabelLength, replacement);
    }

    private bool ConnectC4(DiagramObject from, DiagramObject to, string arrow, string label)
    {
        string? macro = C4Syntax.RelationshipMacro(arrow);
        string? caption = C4Syntax.Quote(label);
        return macro != null && C4Endpoint(from) && C4Endpoint(to) && caption != null &&
            Insert($"{macro}({from.Alias}, {to.Alias}, {caption})");
    }

    private bool UpdateC4Connection(DiagramObject item, DiagramObject? from, DiagramObject? to, string arrow, string? label)
    {
        string? macro = C4Syntax.RelationshipMacro(arrow);
        if (!Current(item) || item.Kind != "Relationship" || !C4Syntax.IsRelationship(item.C4Macro) || macro == null ||
            from != null && !C4Endpoint(from) || to != null && !C4Endpoint(to)) return false;
        string? caption = label == null ? null : C4Syntax.Quote(label);
        if (label != null && caption == null) return false;
        var changes = new List<Change> { new(item.Offset + item.ArrowAt, item.Offset + item.ArrowAt + item.C4Macro.Length, macro) };
        if (caption != null) changes.Add(new(item.Offset + item.LabelAt, item.Offset + item.LabelAt + item.LabelLength, caption));
        if (to != null) changes.Add(new(item.Offset + item.ToAt, item.Offset + item.ToAt + item.To.Length, to.Alias));
        if (from != null) changes.Add(new(item.Offset + item.FromAt, item.Offset + item.FromAt + item.From.Length, from.Alias));
        return ApplyChanges(changes);
    }
}
