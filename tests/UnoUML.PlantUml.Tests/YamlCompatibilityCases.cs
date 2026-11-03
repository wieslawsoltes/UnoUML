using System.Xml.Linq;
using System.Text.Json;

namespace UnoUML.PlantUml.Tests;

internal static class YamlCompatibilityCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    internal static void Run()
    {
        // Expected trees were checked with PlantUML 1.2026.9's YamlParser/MonomorphToJson.
        // That grammar preserves anchor/alias/tag/merge indicators as ordinary scalar text.
        (string Source, string Json)[] oracleCases =
        [
            ("base: &base editor\ncopy: *base", "{\"base\":\"&base editor\",\"copy\":\"*base\"}"),
            ("value: !!str 012\ncustom: !local value", "{\"value\":\"!!str 012\",\"custom\":\"!local value\"}"),
            ("copy:\n  <<: *base\n  name: local", "{\"copy\":{\"<<\":\"*base\",\"name\":\"local\"}}"),
            ("list: [*base, &second abc, !!int 123]", "{\"list\":[\"*base\",\"&second abc\",\"!!int 123\"]}"),
            ("value:\n  first line\n  second line\nnext: other", "{\"value\":\"first line second line\",\"next\":\"other\"}"),
            ("value:\n  first line\n\n  # skipped comment\n  second line\nnext: other", "{\"value\":\"first line second line\",\"next\":\"other\"}"),
            ("value:\n  'first # inside' # outside\n  \"second\"\nnext: kept", "{\"value\":\"first # inside second\",\"next\":\"kept\"}"),
            ("value:\n  &literal abc\n  *literal\n  !!str 42\nnext: kept", "{\"value\":\"&literal abc *literal !!str 42\",\"next\":\"kept\"}"),
            ("list:\n- first\n- second\nlast: tail", "{\"list\":[\"first\",\"second\"],\"last\":\"tail\"}"),
            ("items:\n- key: value\n  next: other\n- key: second", "{\"items\":[{\"key\":\"value\",\"next\":\"other\"},{\"key\":\"second\"}]}"),
            ("root:\n\tchild:\n\t\t- first\n\t\t- second\nlast: tail", "{\"root\":{\"child\":[\"first\",\"second\"]},\"last\":\"tail\"}"),
            ("first: previous\nsecond: middle\nfirst: current", "{\"first\":\"current\",\"second\":\"middle\"}"),
            ("key: one\nkey:\n  nested: two", "{\"key\":{\"nested\":\"two\"}}")
        ];
        foreach (var sample in oracleCases)
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        foreach (string prefix in new[] { "", "\uFEFF" })
        {
            string source = prefix + ("@startyaml\n" + sample.Source + "\n@endyaml").ReplaceLineEndings(newline);
            var yaml = Render(source);
            using var expected = JsonDocument.Parse(sample.Json);
            Check(Text(yaml).SequenceEqual(ExpectedText(expected.RootElement)), "pinned parser tree " + sample.Source);
            foreach (var field in yaml.Descendants().Where(element => element.Attribute("data-edit-start") != null))
            {
                int start = (int)field.Attribute("data-edit-start")!, length = (int)field.Attribute("data-edit-length")!;
                Check(start >= 0 && length >= 0 && start + length <= source.Length, "literal source range");
                int line = CountLines(source.AsSpan(0, start));
                Check((int)field.Attribute("data-source-line")! == line, "physical source line across CR/LF and tab indentation");
            }
        }
        LiteralTokens();
        FoldedTokens();
        DuplicateMappings();
        PreprocessingBoundaries();
        BoundsAndMalformedInputs();
        Console.WriteLine($"Native YAML {oracleCases.Length * 6} pinned-tree/line-ending comparisons and editing regressions passed.");
    }

    private static void LiteralTokens()
    {
        const string source = "\uFEFF@startyaml\r\nbase: &base café 🪴\r\ncopy: *base\r\nvalue: !!str 012\r\n'<<': *base\r\n@endyaml";
        var svg = Render(source);
        foreach (string value in new[] { "&base café 🪴", "*base", "!!str 012" })
        {
            var tokens = svg.Descendants().Where(element => (string?)element.Attribute("data-edit-role") == "value" && element.Value == value).ToArray();
            Check(tokens.Length == (value == "*base" ? 2 : 1), "indicator value is displayed literally");
            foreach (var token in tokens)
                Check(source.Substring((int)token.Attribute("data-edit-start")!, (int)token.Attribute("data-edit-length")!) == value,
                    "indicator literal retains its complete token");
        }
        Check(svg.Descendants().Where(element => element.Value == "*base" && element.Attribute("data-edit-start") != null)
            .Select(element => (int)element.Attribute("data-edit-start")!).Distinct().Count() == 2, "repeated aliases retain distinct literal targets");
        Check(!PlantUmlRenderer.TryRender("@startyaml\nbase: &base\n  child: value\ncopy: *base\n@endyaml", out _),
            "anchored containers do not acquire invented reference expansion");
        var verbatim = Render("@startyaml\nvalue: !<tag:example.org,2026:item> data\n@endyaml");
        Check(verbatim.Value.Contains("!<tag:example.org,2026:item> data", StringComparison.Ordinal), "verbatim tag indicators remain literal displayed text");
    }

    private static void FoldedTokens()
    {
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string source = ("\uFEFF@startyaml\nvalue:\n\tFirst 🪴 # first comment\n\t# middle comment\n\n\t  'second''s' # next comment\n\t\"third \\\"quoted\\\"\" # last comment\nnext: retained\n@endyaml").ReplaceLineEndings(newline);
            var svg = Render(source);
            var field = svg.Descendants().Single(element => (string?)element.Attribute("data-edit-kind") == "yaml-folded-text");
            Check(field.Value == "First 🪴 second's third \"quoted\"", "folded lines decode quoted scalars and skip comments");
            int start = source.IndexOf("First", StringComparison.Ordinal), end = source.IndexOf(" # last comment", StringComparison.Ordinal);
            Check((int)field.Attribute("data-edit-start")! == start && (int)field.Attribute("data-edit-length")! == end - start,
                "folded source span retains all intermediate comments and physical whitespace");
            Check((int)field.Attribute("data-source-line")! == 2, "folded value starts at its first literal source line");

            string tabs = ("@startyaml\nroot:\n\tbody: >-\n\t\tfirst\n\t\tsecond\n\t\t\tindented\nlast: retained\n@endyaml").ReplaceLineEndings(newline);
            var tabbed = Render(tabs);
            var spaced = Render(tabs.Replace("\t", "    ", StringComparison.Ordinal));
            Check(Text(tabbed).SequenceEqual(Text(spaced)) && (string?)tabbed.Attribute("height") == (string?)spaced.Attribute("height"),
                "tab indentation uses four columns for block folding and measured layout");
        }
        foreach (var sample in new[] { (Prefix: "- body: ", Body: "    "), (Prefix: "  - body: ", Body: "      "),
            (Prefix: "\t-   body: ", Body: "\t      "), (Prefix: "- ", Body: "  "), (Prefix: "  body: ", Body: "    ") })
        foreach (string header in new[] { "|2-", ">2-" })
        {
            string source = "@startyaml\n" + sample.Prefix + header + " # retained\n" + sample.Body + "first\n" + sample.Body + "second\n@endyaml";
            var block = Render(source);
            var expected = Render("@startyaml\n" + sample.Prefix + (header[0] == '|' ? "\"first\\nsecond\"" : "\"first second\"") + "\n@endyaml");
            var field = block.Descendants().Single(element => (string?)element.Attribute("data-edit-kind") == "yaml-block");
            var expectedField = expected.Descendants().Single(element => (string?)element.Attribute("data-edit-kind") == "yaml-double");
            Check(field.Value == expectedField.Value && (string?)block.Attribute("height") == (string?)expected.Attribute("height"),
                "explicit block indentation accounts for list-mapping prefixes");
            int start = source.IndexOf(header, StringComparison.Ordinal), end = source.IndexOf("\n@endyaml", StringComparison.Ordinal);
            Check((int)field.Attribute("data-edit-start")! == start && (int)field.Attribute("data-edit-length")! == end - start,
                "list block edit metadata includes the header comment and exact content lines");
        }
        var styled = Render("@startyaml\n<style>\nyamlDiagram { highlight { BackgroundColor red; FontSize 22 } }\n</style>\n#highlight \"body\"\nbody:\n  First\n  second\n@endyaml");
        Check(styled.Descendants(Ns + "g").Any(element => (string?)element.Attribute("data-style-name") == "node highlight" && element.Value.Contains("First second")),
            "folded values participate in path highlights and measured styles");

        const string included = "@startyaml\n!include data.yml\n@endyaml";
        Check(PlantUmlRenderer.TryRender(included, name => name == "data.yml" ? "body:\n  First\n  second\n" : null, out var generated), "included folded data renders");
        var mapped = XElement.Parse(generated!.Svg);
        Check(!mapped.Descendants().Any(element => element.Attribute("data-edit-start") != null) &&
              mapped.Descendants().Any(element => (string?)element.Attribute("data-source-file") == "data.yml"),
            "expanded includes retain provenance without incorrect document-relative token ranges");
    }

    private static void DuplicateMappings()
    {
        const string source = "@startyaml\nfirst: removed\nsecond: retained\nfirst: final\nnested: {value: removed, another: retained, value: final}\n@endyaml";
        var svg = Render(source);
        Check(!Text(svg).Contains("removed") && Text(svg).Count(text => text == "final") == 2, "duplicate map entries keep the last value");
        foreach (var field in svg.Descendants().Where(element => (string?)element.Attribute("data-edit-role") == "value" && element.Value == "final"))
            Check(source.Substring((int)field.Attribute("data-edit-start")!, (int)field.Attribute("data-edit-length")!) == "final", "only effective duplicate values expose edit metadata");
        var root = svg.Descendants(Ns + "g").First(element => (string?)element.Attribute("class") == "data-table");
        var keys = root.Elements(Ns + "g").Where(element => (string?)element.Attribute("class") == "data-field")
            .Select(element => element.Descendants().First(child => (string?)child.Attribute("data-edit-role") == "key").Value);
        Check(keys.SequenceEqual(new[] { "first", "second", "nested" }), "duplicate replacements retain original mapping position");
        var empty = Render("@startyaml\n\"\": old\n'': current\n@endyaml");
        Check(!empty.Value.Contains("old") && empty.Descendants().Count(element => (string?)element.Attribute("data-edit-role") == "key") == 1,
            "decoded duplicate empty keys follow the same mapping rule");
    }

    private static void BoundsAndMalformedInputs()
    {
        foreach (string header in new[] { "|0", "|++", "|12", ">+-", ">--", "|00" })
            Check(!PlantUmlRenderer.TryRender("@startyaml\nvalue: " + header + "\n  text\n@endyaml", out _), "invalid block-scalar modifier declines " + header);
        foreach (string header in new[] { "|2-", "|-2", ">2+", ">+2" })
            Render("@startyaml\nvalue: " + header + "\n  first\n  second\n@endyaml");
        string deep = string.Join('\n', Enumerable.Range(0, 70).Select(depth => new string(' ', depth) + "key:")) + "\n" + new string(' ', 70) + "value: end";
        string many = string.Join('\n', Enumerable.Range(0, 4097).Select(index => "key" + index + ": value"));
        string longFolded = "value:\n" + string.Join('\n', Enumerable.Repeat("  " + new string('a', 128), 65));
        foreach (string body in new[] { deep, many, longFolded, "value:\n  \"unterminated", "value:\n  first\n  child: mismatched" })
            Check(!PlantUmlRenderer.TryRender("@startyaml\n" + body + "\n@endyaml", out _), "depth/node/scalar and malformed folded bounds decline atomically");
        var emptyItems = Render("@startyaml\nitems:\n  -\n  - next\n@endyaml");
        Check(emptyItems.Descendants(Ns + "g").Count(element => (string?)element.Attribute("class") == "data-table") == 2,
            "empty sequence entries do not absorb their following peers");
    }

    private static void PreprocessingBoundaries()
    {
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string source = ("\uFEFF@startyaml\n!custom: literal key\nbody:\n  !!str 42\n  !local value\nnext: retained\n@endyaml").ReplaceLineEndings(newline);
            var svg = Render(source);
            Check(svg.Value.Contains("!custom") && svg.Value.Contains("!!str 42 !local value"), "unknown bang-prefixed YAML keys and tag text remain literal");
            var field = svg.Descendants().Single(element => (string?)element.Attribute("data-edit-kind") == "yaml-folded-text");
            int start = source.IndexOf("!!str", StringComparison.Ordinal);
            Check((int)field.Attribute("data-edit-start")! == start && field.Attribute("data-source-generated") == null,
                "literal-only YAML bypasses TIM without shifting physical token offsets");

            string evaluated = ("@startyaml\nbody:\n  !custom field=literal\n  trailing\n@endyaml").ReplaceLineEndings(newline);
            var identity = Render(evaluated);
            var identityField = identity.Descendants().Single(element => (string?)element.Attribute("data-edit-kind") == "yaml-folded-text");
            Check(identityField.Value == "!custom field=literal trailing" &&
                  (int)identityField.Attribute("data-edit-start")! == evaluated.IndexOf("!custom", StringComparison.Ordinal) &&
                  identityField.Attribute("data-source-generated") == null,
                "proven identity expansion restores exact original source bytes");
        }
        var processed = Render("@startyaml\n!$value = \"Native\"\nbody:\n  !!str 42\n  !custom literal\nvalue: $value\n@endyaml");
        Check(processed.Value.Contains("!!str 42 !custom literal") && processed.Value.Contains("Native") &&
              !processed.Descendants().Any(element => element.Attribute("data-edit-start") != null),
            "real TIM expansion retains literal YAML tags and removes obsolete token offsets");
        var branch = Render("@startyaml\n!if 1\nvalue: visible\n!else\nvalue: hidden\n!endif\n@endyaml");
        Check(branch.Value.Contains("visible") && !branch.Value.Contains("hidden"), "known conditional directives still execute before YAML parsing");
        foreach (string directive in new[] { "!assert 0", "!include missing.yml", "!if 1", "!endif", "!while 1\nvalue: looping\n!endwhile" })
            Check(!PlantUmlRenderer.TryRender("@startyaml\n" + directive + "\nvalue: retained\n@endyaml", out _),
                "known or malformed TIM directives do not become literal YAML " + directive);
        Check(!PlantUmlRenderer.TryRender("@startuml\n!unknown literal\nA -> B: kept\n@enduml", out _) &&
              !PlantUmlPreprocessor.TryProcess("@startuml\n!unknown literal\n@enduml", out _),
            "non-YAML unknown directives retain their existing failure contract");
        Check(PlantUmlPreprocessor.TryProcess("@startyaml\r\n!custom: literal\r\nbody:\r\n  !!str 42\r\n@endyaml", out var publicResult) &&
              publicResult.Source == "@startyaml\n!custom: literal\nbody:\n  !!str 42\n@endyaml" &&
              publicResult.SourceMap.Select(location => location.SourceLine).SequenceEqual(Enumerable.Range(0, 5)) &&
              publicResult.SourceMap.All(location => !location.Generated && location.File == null),
            "public preprocessing keeps its normalized-output and source-map contract");
        var messages = new List<string>();
        Check(PlantUmlPreprocessor.TryProcess("@startyaml\n!log tag pass\n!option svg_title \"Data\"\nvalue: !!str 42\n@endyaml", out var options,
                  new PlantUmlRenderOptions { Log = messages.Add }) && messages.SequenceEqual(new[] { "tag pass" }) && options.Options["svg_title"] == "Data",
            "public preprocessing still invokes diagnostics and returns rendering options");
        messages.Clear();
        Check(PlantUmlRenderer.TryRender("@startyaml\n!log: literal\nnext: retained\n@endyaml", out var logKey,
                  new PlantUmlRenderOptions { Log = messages.Add }) && messages.SequenceEqual(new[] { ": literal" }) &&
              !XElement.Parse(logKey.Svg).Value.Contains("literal", StringComparison.Ordinal),
            "directive word boundaries match the JVM even for colon-looking YAML keys");
        foreach (string directiveKey in new[] { "!include: literal", "!if: literal", "!theme: literal" })
            Check(!PlantUmlRenderer.TryRender("@startyaml\n" + directiveKey + "\nnext: retained\n@endyaml", out _),
                "known directive-looking keys keep their TIM semantics " + directiveKey);
        foreach (string key in new[] { "!include1", "!if_other", "!logΩ", "!!str", "!unknown" })
            Check(Render("@startyaml\n" + key + ": literal\nnext: retained\n@endyaml").Value.Contains(key, StringComparison.Ordinal),
                "a longer YAML key does not match a TIM keyword prefix " + key);
        var assigned = Render("@startyaml\nbody:\n  !custom = literal\nnext: retained\n@endyaml");
        Check(!assigned.Value.Contains("!custom", StringComparison.Ordinal) && assigned.Value.Contains("retained", StringComparison.Ordinal),
            "valid assignments in YAML remain assignments");
    }

    private static IEnumerable<string> Text(XElement svg) => svg.Descendants(Ns + "text").Select(element => element.Value);
    private static IEnumerable<string> ExpectedText(JsonElement root)
    {
        yield return root.ValueKind == JsonValueKind.Array ? "[]" : "{}";
        var rows = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().Select((value, index) => (Key: index.ToString(System.Globalization.CultureInfo.InvariantCulture), Value: value))
            : root.EnumerateObject().Select(property => (Key: property.Name, Value: property.Value));
        foreach (var (key, value) in rows)
        {
            yield return key.Length == 0 ? "\"\"" : key;
            yield return value.ValueKind switch
            {
                JsonValueKind.Array => "[]",
                JsonValueKind.Object => "{}",
                _ => value.GetString() is { Length: > 0 } text ? text : "\"\""
            };
        }
        foreach (var (_, value) in rows)
            if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                foreach (string text in ExpectedText(value)) yield return text;
    }
    private static int CountLines(ReadOnlySpan<char> text) => text.Count('\n') + text.Count('\r') - text.Count("\r\n");
    private static XElement Render(string source)
    {
        if (!PlantUmlRenderer.TryRender(source, out var diagram)) throw new Exception("YAML compatibility declined " + source);
        return XElement.Parse(diagram.Svg);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("YAML compatibility " + message);
    }
}
