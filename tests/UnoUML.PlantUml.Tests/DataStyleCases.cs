using System.Xml.Linq;
using System.Text.Json;

namespace UnoUML.PlantUml.Tests;

internal static class DataStyleCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    internal static void Run()
    {
        YamlCompatibilityCases.Run();
        const string body = """
            - name: Mark McGwire
              hr: 65
              avg: 0.278
              tags: [first, 'second, item', "third"]
            - name: Sammy Sosa
              hr: 63
              address:
                city: Chicago
                coordinates: {lat: 41.8, lon: -87.6}
            """;
        var sequence = Render("@startyaml\n" + body + "\n@endyaml");
        foreach (string text in new[] { "Mark McGwire", "Sammy Sosa", "second, item", "Chicago", "41.8", "-87.6" })
            Check(sequence.Value.Contains(text, StringComparison.Ordinal), "nested block and flow value " + text);
        var punctuation = Render("@startyaml\n@fruit: Apple\n$size: Large\n&color: Red\n❤: Heart\n\"key: with colon\": 'it''s quoted' # comment\n@endyaml");
        Check(punctuation.Value.Contains("key: with colon") && punctuation.Value.Contains("it's quoted") && !punctuation.Value.Contains("comment"), "symbol keys and quote-aware colon/comment scanning");
        var literal = Render("@startyaml\ntext: |-\n  first line\n  second line\nnext: >-\n  a folded\n  paragraph\n@endyaml");
        Check(literal.Value.Contains("first line") && literal.Value.Contains("second line") && literal.Value.Contains("a folded paragraph"), "literal and folded block strings");
        Check(literal.Descendants().Count(e => (string?)e.Attribute("data-edit-kind") == "yaml-block") == 2, "block string canvas edit targets");
        var commandText = Render("@startyaml\ntext: |\n  # retained comment\n  title retained content\n  #highlight \"path\"\nnext: value\n@endyaml");
        Check(commandText.Value.Contains("# retained comment") && commandText.Value.Contains("title retained content") && commandText.Value.Contains("#highlight \"path\""), "indented block contents retain command-like and comment-like text");
        Check(!commandText.Descendants().Any(element => (string?)element.Attribute("class") == "title"), "block contents do not become a diagram title");
        QuotedAndPlainScalars();
        FoldedBlocks();

        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string source = "\uFEFF@startyaml" + newline + "\"描画🪴\": [\"same\", 'same', 12] # keep" + newline + "other: \"same\"" + newline + "@endyaml";
            var svg = Render(source);
            var tokens = svg.Descendants().Where(e => e.Attribute("data-edit-start") != null).ToArray();
            var slices = tokens.Select(e => source.Substring((int)e.Attribute("data-edit-start")!, (int)e.Attribute("data-edit-length")!)).ToArray();
            Check(slices.SequenceEqual(new[] { "\"描画🪴\"", "other", "\"same\"", "\"same\"", "'same'", "12" }), "original UTF-16 scalar/key ranges across line endings");
            Check(tokens[0].Attribute("data-edit-role")!.Value == "key" && tokens[^1].Attribute("data-edit-role")!.Value == "value", "scalar roles");
            var crStyle = Render("@startyaml" + newline + "<style>" + newline + "node { FontSize 22 }" + newline + "</style>" + newline + "key: value" + newline + "@endyaml");
            Check(crStyle.Descendants(Ns + "text").Any(e => e.Value == "value" && (int?)e.Attribute("font-size") == 22), "style scanning retains every supported line ending");
        }

        foreach (string dialect in new[] { "yaml", "json" })
        {
            string data = dialect == "yaml" ? "label: '**Rich text** and <math>sqrt x</math>'\nlong: 'alpha bravo charlie delta echo foxtrot'" : "{\"label\":\"**Rich text** and <math>sqrt x</math>\",\"long\":\"alpha bravo charlie delta echo foxtrot\"}";
            var small = Render("@start" + dialect + "\n" + data + "\n@end" + dialect);
            var styled = Render("@start" + dialect + "\n<style>\n" + dialect + "Diagram { node { FontSize 24; Padding 8 12; MaximumWidth 160; BackgroundColor #112233; FontColor automatic; FontStyle bold; separator { LineColor red } } }\n</style>\n" + data + "\n@end" + dialect);
            Check((int)styled.Attribute("height")! > (int)small.Attribute("height")!, "measured data row style height");
            Check(styled.Descendants(Ns + "text").Any(e => (int?)e.Attribute("font-size") == 24), "data font size consumed during layout");
            Check(styled.Descendants(Ns + "path").Any(e => (string?)e.Attribute("stroke") == "red"), "data separator nested style");
            Check(!styled.Descendants().Attributes().Any(a => a.Value == "automatic"), "automatic data ink resolves to a concrete color");
            Check(styled.Descendants(Ns + "g").Any(e => ((string?)e.Attribute("data-sprite"))?.StartsWith("$math/", StringComparison.Ordinal) == true), "rich formula in data table");
        }
        var highlight = Render("""
            @startyaml
            <style>
            yamlDiagram { highlight { BackgroundColor red; FontColor white; FontSize 22 } }
            .important { BackgroundColor green }
            </style>
            #highlight "nested" / "target" <<important>>
            nested:
              target: highlighted
              untouched: unchanged
            @endyaml
            """);
        var selected = highlight.Descendants(Ns + "g").Single(e => (string?)e.Attribute("data-style-stereotype") == "important");
        Check(selected.Elements(Ns + "rect").Any(e => (string?)e.Attribute("fill") == "green") && selected.Descendants(Ns + "text").All(e => (string?)e.Attribute("fill") == "white"), "path highlight and stereotype cascade");
        var jsonHeading = Render("@startjson\r\ntitle **Native JSON**\r\n#highlight \"nested\" / \"value\"\r\n{\"nested\":{\"value\":42}}\r\n@endjson");
        Check(jsonHeading.Descendants(Ns + "g").Any(e => (string?)e.Attribute("class") == "title" && e.Value == "Native JSON") &&
            jsonHeading.Descendants(Ns + "g").Any(e => (string?)e.Attribute("data-style-name") == "node highlight" && e.Value.Contains("42")), "JSON title and path highlight share measured data rendering");
        Render("@startyaml\n<style>\nroot { FontSize 23; FontColor blue }\n</style>\nformula: '<math>sqrt(x)</math>'\n@endyaml");

        foreach (string bad in new[] { "list: [one, 'unterminated]", "key: {a: 1", "key:\n child: 1\n  bad: 2", new string(' ', 1) + "key: [" + new string('[', 80) + "x" + new string(']', 81) })
            Check(!PlantUmlRenderer.TryRender("@startyaml\n" + bad + "\n@endyaml", out _), "malformed/overdeep YAML declines atomically");
    }

    private static void QuotedAndPlainScalars()
    {
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string source = "\uFEFF@startyaml" + newline + "\"\": \"\"" + newline + "nested:" + newline + "  '': ''" + newline + "items: [\"\", '']" + newline + "@endyaml";
            var empty = Render(source);
            var fields = empty.Descendants().Where(element => element.Attribute("data-edit-start") != null).ToArray();
            var quoted = fields.Where(element => (string?)element.Attribute("data-edit-kind") is "yaml-double" or "yaml-quoted").ToArray();
            Check(quoted.Length == 6, "empty quoted keys and values retain typed source spans");
            foreach (var field in quoted)
            {
                int start = (int)field.Attribute("data-edit-start")!, length = (int)field.Attribute("data-edit-length")!;
                string token = source.Substring(start, length);
                string expected = (string?)field.Attribute("data-edit-kind") == "yaml-double" ? "\"\"" : "''";
                Check(length == 2 && token == expected, "empty quoted token retains both original delimiters");
            }
            Check(quoted.Count(field => (string?)field.Attribute("data-edit-role") == "key") == 2, "empty quoted mapping keys remain editable");
        }
        var plain = Render("""
            @startyaml
            John's key: John's book # discarded
            saying: Say, "hello
            possessive: parents'
            items: [John's book, Say "hello", "quoted # text", 'single # text', [John's item, "hash # nested"]]
            @endyaml
            """);
        foreach (string value in new[] { "John's key", "John's book", "Say, \"hello", "parents'", "Say \"hello\"", "quoted # text", "single # text", "John's item", "hash # nested" })
            Check(plain.Value.Contains(value, StringComparison.Ordinal), "plain and quoted scalar boundary " + value);
        Check(!plain.Value.Contains("discarded", StringComparison.Ordinal), "plain scalar comments remain excluded");
    }

    private static void FoldedBlocks()
    {
        (string Content, string Clipped, string Kept)[] cases =
        [
            ("  first\n  second\n", "first second\n", "first second\n"),
            ("  first\n\n  second\n", "first\nsecond\n", "first\nsecond\n"),
            ("  first\n\n  second\n\n\n", "first\nsecond\n", "first\nsecond\n\n\n"),
            ("  first\n\n    more\n\n  last\n", "first\n\n  more\n\nlast\n", "first\n\n  more\n\nlast\n"),
            ("  first\n  \n  second\n", "first\nsecond\n", "first\nsecond\n"),
            ("\n\n", "", "\n\n"),
            ("  \n    \n", "", "\n\n")
        ];
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        foreach (string header in new[] { ">", ">-", ">+" })
        foreach (var sample in cases)
        {
            string value = header switch { ">-" => sample.Clipped.TrimEnd('\n'), ">+" => sample.Kept, _ => sample.Clipped };
            string source = ("@startyaml\ntext: " + header + "\n" + sample.Content + "next: retained\n@endyaml").ReplaceLineEndings(newline);
            var block = Render(source);
            var expected = Render("@startyaml\ntext: " + JsonSerializer.Serialize(value) + "\nnext: retained\n@endyaml");
            var actualField = block.Descendants().Single(element => (string?)element.Attribute("data-edit-kind") == "yaml-block");
            var expectedField = expected.Descendants().Single(element => (string?)element.Attribute("data-edit-kind") == "yaml-double");
            static IEnumerable<(string Text, string? X, string? Y)> Rows(XElement element) => element.DescendantsAndSelf()
                .Where(child => child.Name.LocalName == "text").Select(child => (child.Value, (string?)child.Attribute("x"), (string?)child.Attribute("y")));
            Check(Rows(actualField).SequenceEqual(Rows(expectedField)) && (string?)block.Attribute("height") == (string?)expected.Attribute("height"), "folded block decoded layout " + header);
            string token = source.Substring((int)actualField.Attribute("data-edit-start")!, (int)actualField.Attribute("data-edit-length")!);
            string expectedToken = (header + "\n" + sample.Content[..^1]).ReplaceLineEndings(newline);
            Check(token == expectedToken, "folded block keeps exact physical source span " + header);
        }
    }

    private static XElement Render(string source)
    {
        if (!PlantUmlRenderer.TryRender(source, out var diagram)) throw new Exception("Data compatibility declined " + source);
        return XElement.Parse(diagram.Svg);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Data compatibility " + message);
    }
}
