namespace UnoUML.Core;

internal static class C4Syntax
{
    internal readonly record struct Argument(int Start, int Length, string Text);

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
        text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

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
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return false;
        text = value[1..^1];
        // Evaluated text cannot be rewritten as a literal while preserving its source expression.
        return !text.Any(c => c is '"' or '\\' or '$' or '%' || char.IsControl(c));
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
        if (code.Length > 4096) return false;
        while (macroStart < code.Length && char.IsWhiteSpace(code[macroStart])) macroStart++;
        int position = macroStart;
        while (position < code.Length && (char.IsAsciiLetterOrDigit(code[position]) || code[position] == '_')) position++;
        if (position == macroStart) return false;
        macro = code[macroStart..position];
        while (position < code.Length && char.IsWhiteSpace(code[position])) position++;
        if (position == code.Length || code[position++] != '(') return false;
        int start = position;
        bool quoted = false;
        while (position < code.Length)
        {
            char character = code[position];
            if (character == '"') quoted = !quoted;
            else if (quoted && character == '\\') return false;
            else if (!quoted)
            {
                if (character == '(') return false;
                if (character is ',' or ')')
                {
                    int end = position;
                    while (start < end && char.IsWhiteSpace(code[start])) start++;
                    while (end > start && char.IsWhiteSpace(code[end - 1])) end--;
                    if (end == start || arguments.Count >= 16) return false;
                    arguments.Add(new(start, end - start, code[start..end]));
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
}

internal sealed partial class DiagramIndex
{
    private DiagramObject? C4Object(int line, int end, int parent, string code)
    {
        if (!C4Syntax.Call(code, out string macro, out int macroStart, out bool group, out var arguments)) return null;
        bool relation = C4Syntax.IsRelationship(macro);
        if (!relation && !C4Syntax.IsNode(macro) || group && !C4Syntax.IsGroup(macro)) return null;
        int labelIndex = relation ? 2 : 1;
        if (arguments.Count <= labelIndex || !C4Syntax.IsAlias(arguments[0].Text) ||
            relation && !C4Syntax.IsAlias(arguments[1].Text) ||
            !C4Syntax.Literal(arguments[labelIndex].Text, out string label) || !relation && label.Length == 0 ||
            arguments.Skip(labelIndex + 1).Any(a => !C4Syntax.LiteralOption(a.Text))) return null;
        var row = Lines[line];
        var item = new DiagramObject(_source, row.Start, Lines[end].End, line, end, parent, relation ? "Relationship" : macro, label)
        {
            C4Macro = macro,
            LabelAt = arguments[labelIndex].Start,
            LabelLength = arguments[labelIndex].Length
        };
        if (relation)
        {
            item.From = arguments[0].Text;
            item.FromAt = arguments[0].Start;
            item.To = arguments[1].Text;
            item.ToAt = arguments[1].Start;
            item.Arrow = macro;
            item.ArrowAt = macroStart;
        }
        else
        {
            item.Alias = arguments[0].Text;
            item.AliasAt = arguments[0].Start;
            item.AliasLength = arguments[0].Length;
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
        string replacement = "\"" + label + "\"";
        return label.Length > 0 && C4Syntax.Literal(replacement, out _) &&
            Patch(item.Offset + item.LabelAt, item.Offset + item.LabelAt + item.LabelLength, replacement);
    }

    private bool ConnectC4(DiagramObject from, DiagramObject to, string arrow, string label)
    {
        string? macro = C4Syntax.RelationshipMacro(arrow);
        string caption = "\"" + label + "\"";
        return macro != null && C4Endpoint(from) && C4Endpoint(to) && C4Syntax.Literal(caption, out _) &&
            Insert($"{macro}({from.Alias}, {to.Alias}, {caption})");
    }

    private bool UpdateC4Connection(DiagramObject item, DiagramObject? from, DiagramObject? to, string arrow, string? label)
    {
        string? macro = C4Syntax.RelationshipMacro(arrow);
        if (!Current(item) || item.Kind != "Relationship" || !C4Syntax.IsRelationship(item.C4Macro) || macro == null ||
            from != null && !C4Endpoint(from) || to != null && !C4Endpoint(to)) return false;
        string? caption = label == null ? null : "\"" + label + "\"";
        if (caption != null && !C4Syntax.Literal(caption, out _)) return false;
        var text = new System.Text.StringBuilder(item.Statement);
        if (caption != null) text.Remove(item.LabelAt, item.LabelLength).Insert(item.LabelAt, caption);
        if (to != null) text.Remove(item.ToAt, item.To.Length).Insert(item.ToAt, to.Alias);
        if (from != null) text.Remove(item.FromAt, item.From.Length).Insert(item.FromAt, from.Alias);
        text.Remove(item.ArrowAt, item.C4Macro.Length).Insert(item.ArrowAt, macro);
        return Patch(item.Offset, item.End, text.ToString());
    }
}
