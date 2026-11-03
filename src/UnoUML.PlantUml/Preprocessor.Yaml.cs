namespace UnoUML.PlantUml;

internal sealed partial class Preprocessor
{
    private bool yamlDocument;

    private static bool IsYamlDocument(string source)
    {
        var header = source.AsSpan().TrimStart("\uFEFF \t\r\n");
        return header.StartsWith("@startyaml", StringComparison.Ordinal) &&
            (header.Length == 10 || char.IsWhiteSpace(header[10]));
    }

    // YAML permits bang-prefixed keys and literal tag text. Keep actual TIM keywords
    // on the interpreter path even when followed by punctuation or malformed arguments.
    private static bool IsTimDirective(ReadOnlySpan<char> text, out int length)
    {
        length = 1;
        while (length < text.Length && (char.IsLetterOrDigit(text[length]) || text[length] == '_')) length++;
        return text[1..length].ToString().ToLowerInvariant() is
            "if" or "ifdef" or "ifndef" or "elseif" or "else" or "endif" or
            "while" or "endwhile" or "foreach" or "endfor" or
            "function" or "procedure" or "unquoted" or "final" or "return" or
            "end" or "endfunction" or "endprocedure" or "define" or "definelong" or "enddefinelong" or
            "include" or "includeurl" or "include_many" or "include_once" or "includesub" or "includedef" or
            "startsub" or "endsub" or "theme" or "undef" or "assert" or "log" or "dump_memory" or
            "import" or "option" or "pragma" or "assume";
    }

    private bool IsIdentityExpansion(string source, string expanded)
    {
        var input = source.AsSpan().EnumerateLines();
        var output = expanded.AsSpan().EnumerateLines();
        int line = 0;
        while (input.MoveNext())
        {
            if (!output.MoveNext() || line >= origins.Count || !input.Current.SequenceEqual(output.Current)) return false;
            var origin = origins[line];
            if (origin.Root != line || origin.File != null || origin.Generated) return false;
            line++;
        }
        return !output.MoveNext() && line == origins.Count;
    }
}
