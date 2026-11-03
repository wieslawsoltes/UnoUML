using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class SaltCases
{
    public static void Run()
    {
        var svg = Render("""
            @startsalt
            {#
              Sign in
              User | "Username      "
              [X] Save | () Guest
              [] News  | (X) Member
              ^Plan^ | [Submit]
            }
            @endsalt
            """);
        var all = svg.Descendants().Where(e => e.Attribute("data-source-line") != null).ToArray();
        Require(all.Length == 9, "source hits for every widget");
        XElement Cell(string text) => all.Single(e => e.Value == text);
        Require((int?)Cell("User").Attribute("data-source-line") == 3 && (int?)Cell("Username").Attribute("data-column-index") == 1, "row/column mapping");
        Require(Cell("Save").Descendants().Any(e => e.Name.LocalName == "path") && Cell("News").Descendants().All(e => e.Name.LocalName != "path"), "checkbox state");
        Require(Cell("Member").Descendants().Count(e => e.Name.LocalName == "circle") == 2 && Cell("Guest").Descendants().Count(e => e.Name.LocalName == "circle") == 1, "radio state");
        Require(Cell("Submit").Elements().Any(e => e.Attribute("rx") != null), "rounded button");
        Require(Cell("Plan").Elements().Any(e => e.Name.LocalName == "path"), "dropdown");
        Require((int?)Render("@startsalt\n{\n\"X        \"\n}\n@endsalt").Attribute("width") > (int?)Render("@startsalt\n{\n\"X\"\n}\n@endsalt").Attribute("width"), "text field padding");
        var linked = Render("@startsalt\n{\n[[https://example.com Open]]\n}\n@endsalt");
        Require(linked.Descendants().Any(e => e.Name.LocalName == "a" && (string?)e.Attribute("href") == "https://example.com"), "rich hyperlink label");
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string source = "\uFEFF@startsalt" + newline + "{" + newline + "[Same] | [X] Same | \"Same     \" | ^Same^" + newline + "}" + newline + "@endsalt";
            var cells = Render(source).Descendants().Where(e => (string?)e.Attribute("data-edit-kind") == "salt-text").ToArray();
            Require(cells.Length == 4 && cells.All(e => source.Substring((int)e.Attribute("data-edit-start")!, (int)e.Attribute("data-edit-length")!) == "Same"), "exact duplicate-label edit ranges and preserved field padding");
        }
        var styled = Render("@startsalt\n<style>\nsaltDiagram { button { FontSize 26; Padding 8; FontColor automatic; BackgroundColor #123456 } }\n</style>\n{\n[**Run** <math>x^2</math>] | <size:28>Account</size>\n}\n@endsalt");
        Require(styled.Descendants().Any(e => e.Name.LocalName == "text" && (int?)e.Attribute("font-size") == 26) &&
            (int)styled.Attribute("height")! > 50, "rich text and style metrics enlarge controls");
        Require(styled.Descendants().Any(e => e.Attribute("data-sprite")?.Value == "$math/x^2"), "inline formula within Salt button");
        foreach (var body in new[] { "{\n{\n[X]\n}", "{N=\n[North]\n}", "{\nA | *\n*\n}", "{\n!include secret.txt\n}" })
            Require(!PlantUmlRenderer.TryRender("@startsalt\n" + body + "\n@endsalt", out _), "unsupported syntax fallback");
        SaltLayoutCases.Run();
    }

    private static XElement Render(string text)
    {
        if (!PlantUmlRenderer.TryRender(text, out var d) || d.Kind.ToString() != "Salt") throw new Exception("Expected managed Salt: " + text);
        return XElement.Parse(d.Svg);
    }
    private static void Require(bool value, string context)
    {
        if (!value) throw new Exception("Salt test failed: " + context);
    }
}
