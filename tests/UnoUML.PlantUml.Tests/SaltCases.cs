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
        Require(Cell("Save").Elements().Any(e => e.Name.LocalName == "path") && Cell("News").Elements().All(e => e.Name.LocalName != "path"), "checkbox state");
        Require(Cell("Member").Elements().Count(e => e.Name.LocalName == "circle") == 2 && Cell("Guest").Elements().Count(e => e.Name.LocalName == "circle") == 1, "radio state");
        Require(Cell("Submit").Elements().Any(e => e.Attribute("rx") != null), "rounded button");
        Require(Cell("Plan").Elements().Any(e => e.Name.LocalName == "path"), "dropdown");
        Require((int?)Render("@startsalt\n{\n\"X        \"\n}\n@endsalt").Attribute("width") > (int?)Render("@startsalt\n{\n\"X\"\n}\n@endsalt").Attribute("width"), "text field padding");
        foreach (var body in new[] { "{\n{\n[X]\n}\n}", "{T\n +Tree\n}", "{\n^Open^ option1^ option2^\n}", "{\nA | *\n}", "{\n!include secret.txt\n}", "{\n[[http://example.com]]\n}" })
            Require(!PlantUmlRenderer.TryRender("@startsalt\n" + body + "\n@endsalt", out _), "unsupported syntax fallback");
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
