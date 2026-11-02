namespace UnoUML.PlantUml;

/// <summary>Parses positional library arguments without evaluating macros, variables or embedded PlantUML.</summary>
internal static class LibraryMacro
{
    internal static bool Alias(string value) => value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    internal static bool Literal(string value, out string text)
    {
        text = "";
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return false;
        text = value[1..^1]; return text.Length <= 1024 && !text.Contains('"') && SvgText.IsPlain(text);
    }
    internal static bool Arguments(ReadOnlySpan<char> input, out List<string> result)
    {
        result = new(5);
        bool quoted = false;
        int start = 0;
        for (int i = 0; i <= input.Length; i++)
        {
            if (i < input.Length && input[i] == '"') { quoted = !quoted; continue; }
            if (i < input.Length && (input[i] != ',' || quoted)) continue;
            if (result.Count >= 5) return false;
            result.Add(input[start..i].Trim().ToString());
            start = i + 1;
        }
        return !quoted;
    }
}
