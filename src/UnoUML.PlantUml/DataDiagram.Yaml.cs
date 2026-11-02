using System.Text.Json;

namespace UnoUML.PlantUml;

internal static partial class DataDiagram
{
    // The reference engine handles flow collections, block scalars, explicit typing, aliases,
    // multiline folding and merge keys. The native path only claims complete block documents.
    internal static string? RenderYaml(string source)
    {
        var tables = new List<Table>();
        var scopes = new List<(int Indent, int Table, bool Array)>();
        bool opened = false, closed = false, mayNest = false;
        int count = 0, line = -1;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var text = raw.Trim();
            if (text.IsEmpty || text.StartsWith("#")) continue;
            if (!opened) { if (!text.SequenceEqual("@startyaml")) return null; opened = true; continue; }
            if (closed) return null;
            if (text.SequenceEqual("@endyaml")) { closed = true; continue; }
            if (++count > 4000 || raw.Length > 2048 || raw.Contains('\t')) return null;
            int indent = raw.Length - raw.TrimStart().Length;
            bool array = text.SequenceEqual("-") || text.StartsWith("- ");
            if (scopes.Count == 0)
            {
                scopes.Add((indent, 0, array)); tables.Add(new(0, -1, -1) { Caption = array ? "[]" : "{}" });
            }
            else if (indent > scopes[^1].Indent)
            {
                if (!mayNest || scopes.Count >= 64) return null;
                int parent = scopes[^1].Table;
                var owner = tables[parent];
                int field = owner.Fields.Count - 1;
                string shape = array ? "[]" : "{}";
                owner.Fields[field] = owner.Fields[field] with { Value = shape };
                scopes.Add((indent, tables.Count, array));
                tables.Add(new(scopes.Count - 1, parent, field) { Caption = shape });
            }
            else while (scopes.Count > 1 && scopes[^1].Indent > indent) scopes.RemoveAt(scopes.Count - 1);
            var scope = scopes[^1];
            if (scope.Indent != indent || scope.Array != array) return null;
            var table = tables[scope.Table];
            string key;
            if (array) { key = table.Fields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); text = text[1..].Trim(); }
            else
            {
                int split = text.IndexOf(':');
                if (split <= 0 || split + 1 < text.Length && text[split + 1] != ' ') return null;
                var name = Scalar(text[..split].Trim());
                if (name == null || name.Length == 0) return null;
                key = name; text = text[(split + 1)..].Trim();
            }
            mayNest = text.IsEmpty;
            string? value = mayNest ? "null" : Scalar(text);
            if (value == null) return null;
            table.Fields.Add(new(key, value, line));
            table.Keys = Math.Max(table.Keys, SvgText.Measure(key));
            table.Values = Math.Max(table.Values, SvgText.Measure(value));
        }
        return closed && tables.Count > 0 ? Render(tables) : null;
    }

    private static string? Scalar(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || text.StartsWith("---") || text.StartsWith("...")) return null;
        string value;
        if (text[0] == '"')
        {
            try { using var json = JsonDocument.Parse(text.ToString()); value = json.RootElement.GetString()!; }
            catch (JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        else if (text[0] == '\'')
        {
            if (text.Length < 2 || text[^1] != '\'') return null;
            var quoted = text[1..^1];
            for (int i = 0; i < quoted.Length; i++)
                if (quoted[i] == '\'' && (++i == quoted.Length || quoted[i] != '\'')) return null;
            value = quoted.ToString().Replace("''","'");
        }
        else
        {
            if ("!&*%|>{}[]@`#".Contains(text[0]) || text.Contains(": ", StringComparison.Ordinal) ||
                text.StartsWith("? ") || text.StartsWith("- ") || text.EndsWith(":")) return null;
            int comment = text.IndexOf(" #", StringComparison.Ordinal);
            value = (comment >= 0 ? text[..comment].TrimEnd() : text).ToString();
        }
        return !value.Any(char.IsControl) && SvgText.IsPlain(value) ? value : null;
    }
}
