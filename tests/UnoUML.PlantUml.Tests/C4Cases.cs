using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class C4Cases
{
    public static void Run()
    {
        var svg = Render("""
            @startuml
            !include <C4/C4_Context>
            title Live services
            Person(person, "Operator", "Runs the system")
            Person_Ext(customer, "Customer", "Buys, receives and approves")
            System(svc, "Internal", "Stores customer records")
            System_Ext(supplier, "Supplier", "Sales orders")
            Rel(person, svc, "Controls", "HTTPS")
            Rel_R(customer, supplier, "Buys")
            @enduml
            """);
        XNamespace ns = svg.Name.Namespace;
        XElement Node(string id) => svg.Descendants(ns + "g").Single(x => (string?)x.Attribute("data-qualified-name") == id);
        var p = Node("person"); var c = Node("customer"); var app = Node("svc"); var ext = Node("supplier");
        // These colors, stereotypes, label weights, icon presence and formatted technology match C4_Context 2.14.
        Require((string?)p.Element(ns + "rect")?.Attribute("fill") == "#08427B" && (string?)c.Element(ns + "rect")?.Attribute("fill") == "#686868", "person styles");
        Require((string?)app.Element(ns + "rect")?.Attribute("fill") == "#1168BD" && (string?)ext.Element(ns + "rect")?.Attribute("fill") == "#999999", "system styles");
        Require(p.Descendants(ns + "circle").Any() && !app.Descendants(ns + "circle").Any(), "person pictogram");
        Require(app.Elements(ns + "text").Any(t => t.Value == "Stores customer records") && c.Elements(ns + "text").Any(t => t.Value == "Buys, receives and approves"), "descriptions and quoted commas");
        Require(ext.Elements(ns + "text").Any(t => t.Value == "«external_system»" && (string?)t.Attribute("font-style") == "italic"), "external stereotype");
        Require(p.Elements(ns + "text").Any(t => t.Value == "Operator" && (string?)t.Attribute("font-weight") == "bold"), "card caption");
        var link = svg.Descendants(ns + "g").Single(g => (string?)g.Attribute("class") == "link" && (int?)g.Attribute("data-source-line") == 7);
        Require(link.Elements(ns + "text").Select(x => x.Value).SequenceEqual(["Controls", "[HTTPS]"]), "relationship technology");
        Require((int?)p.Attribute("data-source-line") == 3 && (int?)ext.Attribute("data-source-line") == 6, "physical macro lines");
        Require((int?)app.Element(ns+"rect")?.Attribute("y") > (int?)p.Element(ns+"rect")?.Attribute("y"), "relationship default rank");
        Require((int?)ext.Element(ns+"rect")?.Attribute("x") > (int?)c.Element(ns+"rect")?.Attribute("x"), "directional right relation");
        foreach (string extra in new[] { "!include <C4/C4_Container>", "!define MY_COLOR red", "UpdateElementStyle(svc, $bgColor=\"red\")", "Container(db, \"DB\")", "System(svc, \"Name\", \"Desc\", $tags=\"red\")", "Rel(unknown,svc,\"Uses\")", "System(bad, \"<$icon>\")", "System_Boundary(b, \"Boundary\") {" })
            Require(!PlantUmlRenderer.TryRender("@startuml\n!include <C4/C4_Context>\nSystem(svc, \"App\")\n" + extra + "\n@enduml", out _), "whole-document compatibility fallback: " + extra);
    }
    private static XElement Render(string text)
    {
        if (!PlantUmlRenderer.TryRender(text, out var d) || d.Kind.ToString() != "C4") throw new Exception("Expected managed C4 diagram: " + text);
        return XElement.Parse(d.Svg);
    }
    private static void Require(bool result, string label)
    {
        if (!result) throw new Exception("C4 test failed: " + label);
    }
}
