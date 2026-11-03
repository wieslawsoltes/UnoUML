namespace UnoUML.PlantUml;

// Translate the CSS-like selectors understood by the managed painters, retaining
// source positions and declaration order. Unknown properties reject the whole input.
internal static class ModernStyle
{
    private sealed record Rule(string[] Selectors, string Property, string Value, int Priority);

    internal static bool TryExtract(string source, bool sequence, ManagedSkin skin, out string cleaned)
    {
        cleaned = source;
        if (!source.Contains("<style", StringComparison.OrdinalIgnoreCase)) return true;
        var lines = source.Split('\n');
        var stack = new List<string[]>();
        var rules = new List<Rule>();
        bool inside = false, found = false, opened = false, closed = false;
        string? literalEnd = null;
        int start = 0, braces = 0;
        for (int line = 0; line < lines.Length; line++)
        {
            string text = lines[line].Trim();
            if (!inside)
            {
                if (!text.Equals("<style>", StringComparison.OrdinalIgnoreCase))
                {
                    if (text.Equals("@startuml", StringComparison.OrdinalIgnoreCase)) opened = true;
                    if (text.Equals("@enduml", StringComparison.OrdinalIgnoreCase)) closed = true;
                    if (literalEnd != null)
                    {
                        if (text.Equals(literalEnd, StringComparison.OrdinalIgnoreCase) ||
                            literalEnd == "end ref" && text.Equals("endref", StringComparison.OrdinalIgnoreCase) ||
                            literalEnd == "end ref" && text.Equals("end", StringComparison.OrdinalIgnoreCase)) literalEnd = null;
                    }
                    else if (text.Length != 0 && text[0] != '\'')
                    {
                        literalEnd = LiteralEnd(text);
                        braces += CountOutsideQuotes(text, '{') - CountOutsideQuotes(text, '}');
                    }
                    continue;
                }
                // The same tags inside a note, legend or class body are literal text.
                // Delegate these inputs with their original text and source positions intact.
                if (!opened || closed || literalEnd != null || braces != 0) return false;
                inside = true;
                found = true;
                start = line;
                lines[line] = "";
                continue;
            }
            lines[line] = "";
            if (line - start > 2048) return false;
            if (text.Equals("</style>", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count != 0) return false;
                inside = false;
                continue;
            }
            if (text.Length == 0 || text[0] == '\'' || text.StartsWith("//", StringComparison.Ordinal)) continue;
            int position = 0;
            while (position < text.Length)
            {
                int delimiter = position;
                bool quoted = false;
                while (delimiter < text.Length)
                {
                    char value = text[delimiter];
                    if (value == '"') quoted = !quoted;
                    if (!quoted && value is '{' or '}' or ';') break;
                    delimiter++;
                }
                if (quoted) return false;
                string statement = text[position..delimiter].Trim();
                char terminator = delimiter < text.Length ? text[delimiter] : '\n';
                if (terminator == '{')
                {
                    string[] selectors = statement.Split(',', StringSplitOptions.TrimEntries);
                    if (stack.Count >= 4 || selectors.Length > 8 || selectors.Any(s => !Selector(s))) return false;
                    stack.Add(selectors);
                }
                else
                {
                    if (statement.Length > 0)
                    {
                        int separator = statement.IndexOfAny([' ', '\t', ':']);
                        if (separator <= 0 || stack.Count == 0 || rules.Count >= 512) return false;
                        string property = statement[..separator].ToLowerInvariant();
                        string value = statement[separator..].TrimStart();
                        if (value.StartsWith(':')) value = value[1..].TrimStart();
                        if (value.StartsWith(':')) return false;
                        if (value.Length == 0 || value.Contains('!') || value.Contains("/*", StringComparison.Ordinal)) return false;
                        var paths = new List<string[]> { Array.Empty<string>() };
                        foreach (var level in stack)
                        {
                            var next = new List<string[]>();
                            foreach (var path in paths)
                                foreach (var selector in level)
                                {
                                    if (next.Count >= 128) return false;
                                    next.Add([..path, selector]);
                                }
                            paths = next;
                        }
                        foreach (var path in paths)
                        {
                            if (rules.Count >= 512) return false;
                            // Reserve declaration order within one physical source line.
                            rules.Add(new(path, property, value, line * 1024 + rules.Count));
                        }
                    }
                    if (terminator == '}')
                    {
                        if (stack.Count == 0) return false;
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                position = delimiter + 1;
            }
        }
        if (inside || !found) return false;
        foreach (var rule in rules)
            if (!Apply(rule, sequence, skin)) return false;
        skin.HasModernStyles = true;
        cleaned = string.Join('\n', lines);
        return true;
    }

    private static string? LiteralEnd(string line)
    {
        string lower = line.ToLowerInvariant();
        if (lower is "legend" or "legend left" or "legend right" or "legend center") return "endlegend";
        if (lower is "title" or "header" or "footer" or "caption") return "end " + lower;
        if (lower.StartsWith("ref over ", StringComparison.Ordinal) && CountOutsideQuotes(line, ':') == 0) return "end ref";
        if ((lower == "note" || lower.StartsWith("note ", StringComparison.Ordinal)) &&
            !lower.StartsWith("note \"", StringComparison.Ordinal) && CountOutsideQuotes(line, ':') == 0) return "end note";
        return null;
    }

    private static int CountOutsideQuotes(string text, char character)
    {
        bool quoted = false;
        int count = 0;
        foreach (char value in text)
        {
            if (value == '"') quoted = !quoted;
            else if (!quoted && value == character) count++;
        }
        return count;
    }

    private static bool Selector(string text)
    {
        if (text.StartsWith('.'))
            return text.Length is > 1 and <= 80 && text.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-") < 0;
        return text.ToLowerInvariant() is "root" or "document" or "classdiagram" or "sequencediagram" or
            "class" or "object" or "package" or "note" or "arrow" or "participant" or "actor" or "lifeline" or "header" or "title";
    }

    private static bool Apply(Rule rule, bool sequence, ManagedSkin skin)
    {
        string diagram = "", target = "", stereotype = "";
        bool root = false, document = false, header = false;
        foreach (string selector in rule.Selectors)
        {
            string name = selector.ToLowerInvariant();
            if (name == "root")
            {
                if (root || diagram.Length != 0 || target.Length != 0 || document) return false;
                root = true;
            }
            else if (name is "classdiagram" or "sequencediagram")
            {
                if (diagram.Length != 0 || target.Length != 0 || document) return false;
                diagram = name;
            }
            else if (name == "document")
            {
                if (document || diagram.Length != 0 || target.Length != 0) return false;
                document = true;
            }
            else if (name.StartsWith('.'))
            {
                if (stereotype.Length != 0 || document || header) return false;
                stereotype = name[1..];
            }
            else if (name == "header")
            {
                if (target != "class" || header) return false;
                header = true;
            }
            else
            {
                if (target.Length != 0 || stereotype.Length != 0 || document && name != "title") return false;
                target = name;
            }
        }
        if (stereotype.Length != 0 && target is "arrow" or "participant" or "actor" or "lifeline") return false;
        if (diagram == "sequencediagram" && (target is "class" or "object" or "package" || stereotype.Length != 0) ||
            diagram == "classdiagram" && target is "participant" or "actor" or "lifeline") return false;

        bool active = diagram.Length == 0 || (sequence ? diagram == "sequencediagram" : diagram == "classdiagram");
        var keys = Keys(target, rule.Property, root, document, header, stereotype.Length != 0);
        if (keys == null) return false;
        if (rule.Property == "shadowing") return rule.Value is "0" or "0.0" or "false";
        if (keys.Length == 0) return false;
        bool sequenceTarget = target is "participant" or "actor" or "lifeline";
        bool graphTarget = target is "class" or "object" or "package" || stereotype.Length != 0;
        active &= sequence ? !graphTarget : !sequenceTarget;
        // Validate inactive scopes as well, so malformed or unsupported rules never disappear.
        var receiver = active ? skin : new ManagedSkin();
        foreach (string key in keys)
            if (!receiver.SetStyle(key + (stereotype.Length == 0 ? "" : "<<" + stereotype + ">>"), rule.Value, rule.Priority)) return false;
        if (active && target == "participant") skin.ModernParticipantStyle = true;
        return true;
    }

    private static string[]? Keys(string target, string property, bool root, bool document, bool header, bool stereotyped)
    {
        if (property == "shadowing") return [];
        if (document)
            return (target, property) switch
            {
                ("", "backgroundcolor") => ["backgroundcolor"],
                ("title", "fontcolor") => ["titlefontcolor"],
                _ => null
            };
        if (target.Length == 0 && root && !stereotyped)
            return property switch { "fontcolor" => ["defaultfontcolor"], "fontname" => ["defaultfontname"], _ => null };
        if (target.Length == 0 && stereotyped)
        {
            string[]? categories = property switch
            {
                "backgroundcolor" => ["classbackgroundcolor", "objectbackgroundcolor", "packagebackgroundcolor", "notebackgroundcolor"],
                "linecolor" => ["classbordercolor", "objectbordercolor", "packagebordercolor", "notebordercolor"],
                "linethickness" => ["classborderthickness", "objectborderthickness", "packageborderthickness", "noteborderthickness"],
                "fontcolor" => ["classfontcolor", "classmemberfontcolor", "classstereotypefontcolor", "objectfontcolor", "objectmemberfontcolor", "objectstereotypefontcolor", "packagefontcolor", "packagestereotypefontcolor", "notefontcolor"],
                _ => null
            };
            return categories;
        }
        if (header) return property == "fontcolor" ? ["classfontcolor"] : null;
        if (target is "class" or "object" or "package" or "note")
            return property switch
            {
                "backgroundcolor" => [target + "backgroundcolor"],
                "linecolor" => [target + "bordercolor"],
                "linethickness" when target != "note" => [target + "borderthickness"],
                "fontcolor" => [target + "fontcolor", target + "memberfontcolor", target + "stereotypefontcolor"],
                _ => null
            };
        if (target == "arrow")
            return property switch
            {
                "linecolor" => ["arrowcolor"], "linethickness" => ["arrowthickness"],
                "fontcolor" => ["arrowfontcolor"], "fontsize" => ["arrowfontsize"], _ => null
            };
        if (target is "participant" or "actor")
            return property switch
            {
                "backgroundcolor" => [target + "backgroundcolor"], "linecolor" => [target + "bordercolor"],
                "linethickness" when target == "participant" => ["participantborderthickness"],
                "fontcolor" => [target + "fontcolor"], "fontsize" => [target + "fontsize"], _ => null
            };
        if (target == "lifeline")
            return property switch { "linecolor" => ["lifelinebordercolor"], "backgroundcolor" => ["lifelinebackgroundcolor"], _ => null };
        return null;
    }
}
