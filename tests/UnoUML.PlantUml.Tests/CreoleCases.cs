using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class CreoleCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    internal static void Run()
    {
        var document = Render("""
            @startcreole
            = Resources
            <#lightblue,#red>|= Name |= Symbol |
            | <color:green>Service</color> | <&cloud> |
            |<#yellow> Escaped ~| separator | <:grinning:> |
            <code>
            var literal = "<tag> **text**";
            </code>
            # First
            ## Nested
            # Second
            ----
            <font size=24 color=blue>Finish</font>
            @endcreole
            """);
        var cells = document.Descendants(Ns + "g").Where(element => (string?)element.Attribute("class") == "creole-cell").ToArray();
        Check(cells.Length == 6, "six table cells");
        Check(cells[0].Element(Ns + "rect")!.Attribute("fill")!.Value == "lightblue" && cells[0].Element(Ns + "rect")!.Attribute("stroke")!.Value == "red", "row colors");
        Check(cells[4].Element(Ns + "rect")!.Attribute("fill")!.Value == "yellow" && cells[4].Value.Contains("Escaped | separator"), "cell color and escaped separator");
        Check(cells[0].Element(Ns + "rect")!.Attribute("width")!.Value == cells[4].Element(Ns + "rect")!.Attribute("width")!.Value, "column widths are shared");
        Check(document.Descendants(Ns + "text").Any(element => (string?)element.Attribute("font-family") == "monospace" && element.Value.Contains("<tag> **text**")), "code is literal");
        Check(document.Descendants(Ns + "text").Any(element => element.Value == "2."), "nested numbering retains parent counter");
        Check(document.Descendants(Ns + "tspan").Any(element => (string?)element.Attribute("font-size") == "24" && element.Value == "Finish"), "rich font size");
        Check(cells.All(cell => cell.Attribute("data-source-line") != null), "table source mapping");
        foreach (string body in new[] { "<code>\nunclosed", "| <script>active</script> |", "|<#notacolor>bad|", "| missing end" })
            Check(!PlantUmlRenderer.TryRender("@startcreole\n" + body + "\n@endcreole", out _), "invalid Creole declines");
    }

    private static XElement Render(string source)
    {
        if (!PlantUmlRenderer.TryRender(source, out var result) || result.Kind != DiagramKind.Creole) throw new Exception("Creole declined " + source);
        return XElement.Parse(result.Svg);
    }

    private static void Check(bool valid, string description)
    {
        if (!valid) throw new Exception("Creole regression " + description);
    }
}
