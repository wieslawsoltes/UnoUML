using System.Globalization;

namespace UnoUML.PlantUml;

internal static partial class RailroadRenderer
{
    // The option applies to ETileBox values in both regular expressions and
    // grammar productions. Locale fallback follows the bundled PlantUML labels.
    private static readonly Dictionary<string, string[]> Translations = new(StringComparer.Ordinal)
    {
        ["en"] = ["WordBoundary", "NonWordBoundary", "Digit", "NonDigit", "Word", "NonWord", "Whitespace", "NonWhitespace", "AnyChar"],
        ["fr"] = ["LimiteMot", "NonLimiteMot", "Chiffre", "NonChiffre", "Mot", "NonMot", "EspaceBlanc", "NonEspaceBlanc", "ToutCaractère"],
        ["de"] = ["WortGrenze", "NichtWortGrenze", "Ziffer", "KeineZiffer", "Wort", "NichtWort", "Leerzeichen", "KeinLeerzeichen", "BeliebigesZeichen"],
        ["ja"] = ["単語境界", "非単語境界", "数字", "非数字", "単語", "非単語", "空白文字", "非空白文字", "任意文字"],
        ["ko"] = ["단어경계", "비단어경계", "숫자", "비숫자", "단어", "비단어", "공백문자", "비공백문자", "임의문자"],
        ["zh-cn"] = ["单词边界", "非单词边界", "数字", "非数字", "单词", "非单词", "空白字符", "非空白字符", "任意字符"],
        ["zh-tw"] = ["單詞邊界", "非單詞邊界", "數字", "非數字", "單詞", "非單詞", "空白字元", "非空白字元", "任意字元"]
    };

    private static string[]? DescriptiveNames(IReadOnlyDictionary<string, string>? options)
    {
        if (options == null || !options.TryGetValue("use_descriptive_names", out string? enabled) ||
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)) return null;
        string language = options.TryGetValue("language", out string? requested) ? requested : CultureInfo.CurrentUICulture.Name;
        string[] parts = language.ToLowerInvariant().Split('-');
        string key = parts[0] == "zh" && parts.Length > 1 ? "zh-" + parts[^1] : parts[0];
        return Translations.GetValueOrDefault(key) ?? Translations["en"];
    }

    private static string Describe(string text, string[]? names)
    {
        int index = text switch
        {
            "\\b" => 0,
            "\\B" => 1,
            "\\d" => 2,
            "\\D" => 3,
            "\\w" => 4,
            "\\W" => 5,
            "\\s" => 6,
            "\\S" => 7,
            "." => 8,
            _ => -1
        };
        return names == null || index < 0 ? text : names[index];
    }
}
