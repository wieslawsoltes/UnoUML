using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class SaltLayoutCases
{
    internal static void Run()
    {
        const string body = """
            {+
              {^"Account settings"
                Name | "Ada          "
                Access | { (X) public | ( ) private
                           [] active | [X] verified }
              }
              {# . | First | Second
                 Note | A spanning description | * }
              {S Message
                  .
                  "                    " }
              [Cancel] | [Apply]
            }
            """;
        var nested = Render(body);
        Require(nested.Descendants().Count(e => (string?)e.Attribute("class") == "salt-group") == 4, "nested forms");
        Require(nested.Value.Contains("Account settings") && nested.Value.Contains("A spanning description"), "group title and spanning cells");
        Require(nested.Descendants().Count(e => (string?)e.Attribute("class") == "salt-scrollbar") == 2, "both scrollbars");
        Require(nested.Descendants().Count(e => (string?)e.Attribute("data-edit-kind") == "salt-state") == 4, "independent state tokens");
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string source = ("\uFEFF@startsalt\n{+\nSame | { [Same] | [ ] Same\n \"Same   \" | ^Same^^Other^^Last^ }\n}\n@endsalt").ReplaceLineEndings(newline);
            var xml = RenderSource(source);
            var fields = xml.Descendants().Where(e => (string?)e.Attribute("data-edit-kind") == "salt-text").ToArray();
            Require(fields.Length == 7, "all nested labels and popup options have source metadata");
            foreach (var field in fields)
            {
                string literal = source.Substring((int)field.Attribute("data-edit-start")!, (int)field.Attribute("data-edit-length")!);
                Require(literal == field.Value, "nested physical token span " + literal);
            }
            var state = xml.Descendants().Single(e => (string?)e.Attribute("data-edit-kind") == "salt-state");
            Require(source.Substring((int)state.Attribute("data-edit-start")!, (int)state.Attribute("data-edit-length")!) == "[ ]", "state token includes only checkbox marker");
        }
        var escaped = Render("{\n[Set \\{A\\} \\| B] | \\{literal\\}\n}");
        Require(escaped.Value.Contains("Set {A} | B") && escaped.Value.Contains("{literal}"), "escaped structure stays inside labels");
        var open = Render("{\n^Plan^^Basic^^Professional^\nBelow\n}");
        var closed = Render("{\n^Plan^\nBelow\n}");
        int Baseline(XElement svg) => (int)svg.Descendants().Single(e => e.Name.LocalName == "text" && e.Value == "Below").Attribute("y")!;
        Require(Baseline(open) == Baseline(closed), "popup does not change grid row height");
        Require((int)open.Attribute("height")! > (int)closed.Attribute("height")!, "popup expands image extent");
        Require(open.Descendants().Count(e => e.Name.LocalName == "text" && e.Value == "Basic") == 1, "popup text painted once");
        Require(open.Elements().Last().Attribute("class")?.Value == "salt-popup", "popup paints above following widgets");
        var tree = Render("{T#\n+ Region | Count\n++ Europe | { [X] Enabled | [Edit] }\n+++ Poland | 12\n++ America | 24\n}");
        Require(tree.Value.Contains("Poland") && tree.Descendants().Count(e => (string?)e.Attribute("class") == "salt-widget salt-tree") == 4, "tree table preserves hierarchy and following nested cells");
        Require(tree.Descendants().Any(e => (string?)e.Attribute("stroke-dasharray") == "1,2"), "tree ancestry connectors");
        var tabs = Render("{+\n{/ <b>General | Advanced | Saving }\n[Apply]\n}");
        var verticalTabs = Render("{+\n{/ General\nAdvanced\nSaving } | [Apply]\n}");
        Require(tabs.Descendants().Count(e => (string?)e.Attribute("class") == "salt-widget salt-tab") == 3, "tab captions");
        Require((int)verticalTabs.Attribute("height")! > (int)tabs.Attribute("height")!, "vertical tab orientation");
        var menu = Render("{+\n{* File | Edit\nFile | New | Open | - | Close }\n[Document]\n}");
        Require(menu.Value.Contains("New") && menu.Value.Contains("Close") && menu.Descendants().Count(e => (string?)e.Attribute("class") == "salt-popup") == 1, "menu popup belongs to named entry");
        var emptyTitle = Render("{^\"\"\n[Confirm]\n}");
        var titleTarget = emptyTitle.Descendants().Single(e => (string?)e.Attribute("class") == "salt-group-title");
        Require((int)titleTarget.Attribute("data-edit-length")! == 0 && titleTarget.Elements().Any(e => e.Name.LocalName == "rect" && (string?)e.Attribute("fill") == "transparent"),
            "empty title retains a nonempty canvas hit target");
        Require(Render("{^\".\"\n[Confirm]\n}").Descendants().Any(e => (string?)e.Attribute("class") == "salt-group-title" && e.Value == "."), "dot title remains literal content");
        Require(Render("{\n^Plan^^.^\n}").Descendants().Any(e => (string?)e.Attribute("class") == "salt-widget salt-option" && e.Value == "."), "dot popup entry retains measured text");
        foreach (string literal in new[] { "*", "(Project)", "[Literal]" })
        {
            Require(Render("{^\"" + literal + "\"\n[Confirm]\n}").Descendants().Any(e => (string?)e.Attribute("class") == "salt-group-title" && e.Value == literal), "forced group title is literal: " + literal);
            Require(Render("{\n^Plan^^" + literal + "^\n}").Descendants().Any(e => (string?)e.Attribute("class") == "salt-widget salt-option" && e.Value == literal), "forced popup option is literal: " + literal);
        }
        Require(Render("{\n(Project)\n}").Descendants().Any(e => (string?)e.Attribute("class") == "salt-widget salt-label" && e.Value == "(Project)"), "parenthesized plain label is not a radio marker");
        var styled = RenderSource("@startsalt\n<style>\nsaltDiagram { button { FontSize 25; Padding 7 } }\n</style>\n{+\n{^\"Controls\"\n[**Execute** <math>x^2</math>]\n}\n}\n@endsalt");
        Require(styled.Descendants().Any(e => e.Attribute("data-sprite")?.Value == "$math/x^2") && (int)styled.Attribute("height")! > 60, "nested form uses rich style metrics");
        foreach (string invalid in new[]
        {
            "{\n* | A\n}", "{+\n[Missing]\n", "{T\n+ A | {\nB\n}\n", "{* File | File\nFile | New }",
            string.Concat(Enumerable.Repeat("{\n", 33)) + "Text\n" + string.Concat(Enumerable.Repeat("}\n", 33)),
            "{\n" + string.Join(" | ", Enumerable.Repeat("A", 129)) + "\n}",
            "{\n" + string.Concat(Enumerable.Repeat("A\n", 1024)) + "B }"
        }) Require(!PlantUmlRenderer.TryRender("@startsalt\n" + invalid + "\n@endsalt", out _), "bounded malformed form rejection");
        Console.WriteLine("Native Salt nested forms, spans, trees, tabs, menus, scrollbars, popup extents and exact control fields passed.");
    }

    private static XElement Render(string body) => RenderSource("@startsalt\n" + body + "\n@endsalt");
    private static XElement RenderSource(string source)
    {
        if (!PlantUmlRenderer.TryRender(source, out var result) || result.Kind != DiagramKind.Salt) throw new Exception("Native Salt declined " + source);
        return XElement.Parse(result.Svg);
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception("Salt nested layout " + message);
    }
}
