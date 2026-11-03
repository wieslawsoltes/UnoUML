using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class RailroadCases
{
    public static void Run()
    {
        const string grammar = "digit = \"0\" | \"1\";\nsequence = digit, { digit };";
        var ebnf = Render("ebnf", grammar);
        string[] Labels(XElement diagram) => diagram.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToArray();
        Verify(Labels(ebnf).SequenceEqual(new[] { "digit", "0", "1", "sequence", "digit", "digit" }), "EBNF reference labels");
        var rules = ebnf.Descendants().Where(e => (string?)e.Attribute("class") == "production").ToArray();
        Verify(rules.Length == 2 && (int?)rules[0].Attribute("data-source-line") == 1 && (int?)rules[1].Attribute("data-source-line") == 2, "production source lines");
        var multi = Render("ebnf", "tag = name,\n [ \"=\", value ];\nname = 'n';");
        Verify(multi.Descendants().Any(e => (string?)e.Attribute("class") == "rail-nonterminal" && (int?)e.Attribute("data-source-line") == 2 && e.Value == "value"), "multiline token mapping");
        var simple = Render("regex", "[A-Z][a-z]+");
        Verify(Labels(simple).SequenceEqual(new[] { "A-Z", "a-z" }), "regex reference classes");
        var branches = Render("regex", @"^(?:foo|bar)[0-9]{2,4}\s?$");
        foreach (var label in new[] { "^", "foo", "bar", "0-9", "{2,4}", @"\s", "$" })
            Verify(Labels(branches).Contains(label), "regex label " + label);
        var precedence = Labels(Render("regex", "abc+"));
        foreach (var (language, expected) in new[]
        {
            ("en", "Digit"), ("fr", "Chiffre"), ("fr-CA", "Chiffre"), ("de", "Ziffer"),
            ("ja", "数字"), ("ko", "숫자"), ("zh-CN", "数字"), ("zh-TW", "數字"), ("unknown", "Digit")
        })
        {
            var localized = Render("regex", "!option use_descriptive_names true\n!option language \"" + language + "\"\n\\d+");
            Verify(Labels(localized).Contains(expected), "localized regex token " + language);
            Verify(localized.Descendants().Any(e => (string?)e.Attribute("class") == "rail-token" &&
                (int?)e.Attribute("data-source-line") == 3), "localized token source mapping");
        }
        var descriptions = Labels(Render("regex", "!option use_descriptive_names true\n!option language en\n" + @"\b\B\d\D\w\W\s\S."));
        Verify(descriptions.SequenceEqual(new[] { "WordBoundary", "NonWordBoundary", "Digit", "NonDigit", "Word", "NonWord", "Whitespace", "NonWhitespace", "AnyChar" }), "all descriptive regex token names");
        Verify(Labels(Render("regex", "!option use_descriptive_names false\n\\d")).Contains(@"\d"), "descriptive names disabled");
        Verify(Labels(Render("regex", "!option use_descriptive_names true\n!option language fr\n[\\d\\w]")).SequenceEqual(new[] { @"\d", @"\w" }), "character class tokens retain regex spelling");
        Verify(Labels(Render("ebnf", "!option use_descriptive_names true\n!option language fr\nperiod = \".\";")).Contains("ToutCaractère"), "descriptive EBNF terminal");
        Verify(precedence.SequenceEqual(new[] { "ab", "c" }), "quantifier binds to last character");
        Verify(Labels(Render("regex", "[a-zA-Z0-9_]+")).SequenceEqual(new[] { "a-z", "A-Z", "0-9", "_" }), "class alternatives");
        Verify(Render("regex", @"(ab)\.[^x]*").Descendants().Any(e => e.Name.LocalName == "title" && e.Value == "Capture 1"), "capture group");
        Verify(Labels(Render("ebnf", "tag = '<&>';")).Contains("<&>"), "escaped grammar literal");
        foreach (var bad in new[] { ("ebnf", "x = \"a\" - \"b\";"), ("ebnf", "x = [ symbol;"), ("regex", "[unterminated"), ("regex", @"(a)\1"), ("regex", "a+?"), ("regex", "a{5,2}"), ("regex", "[z-a]"), ("regex", "a\nb"), ("regex", "(?=a)b"), ("regex", new string('(', 70) + "a" + new string(')', 70)) })
            Verify(!PlantUmlRenderer.TryRender("@start" + bad.Item1 + "\n" + bad.Item2 + "\n@end" + bad.Item1, out _), "unsupported syntax " + bad);
    }

    private static void Verify(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("Railroad check failed: " + name);
    }
    private static XElement Render(string tag, string body)
    {
        if (!PlantUmlRenderer.TryRender("@start" + tag + "\n" + body + "\n@end" + tag, out var diagram))
            throw new InvalidOperationException("Missing railroad output: " + body);
        return XElement.Parse(diagram.Svg);
    }
}
