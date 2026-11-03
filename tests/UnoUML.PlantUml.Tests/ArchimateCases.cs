using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class ArchimateCases
{
    public static void Run()
    {
        string[] kinds = ["Business_Actor", "Business_Role", "Business_Service", "Business_Process", "Application_Component", "Application_DataObject", "Technology_Node"];
        string[] relations = ["Assignment","Association","Composition","Aggregation","Specialization","Realization","Serving","Access","Triggering","Flow","Influence"];
        string[] lines = ["@startuml", "!include <archimate/Archimate>",
            .. kinds.Select((kind,i) => $"{kind}(n{i}, \"{kind}\")"),
            .. relations.Select((kind,i) => $"Rel_{kind}(n{i%6}, n{i%6+1}, \"{kind}\")"), "@enduml"];
        if (!PlantUmlRenderer.TryRender(string.Join('\n',lines),out var result) || result.Kind.ToString() != "ArchiMate")
            throw new Exception("Expected managed ArchiMate");
        var svg = XElement.Parse(result.Svg);
        XNamespace ns = svg.Name.Namespace;
        var nodes = svg.Descendants(ns+"g").Where(g => (string?)g.Attribute("class")=="entity archimate").ToArray();
        Require(nodes.Length == 7 && (int?)nodes[0].Attribute("data-source-line")==2 && (string?)nodes[^1].Attribute("data-qualified-name")=="n6","node map");
        Require((string?)nodes[0].Element(ns+"rect")?.Attribute("fill")=="#FFFFCC" && (string?)nodes[4].Element(ns+"rect")?.Attribute("fill")=="#C2F0FF" &&
            (string?)nodes[6].Element(ns+"rect")?.Attribute("fill")=="#C9FFC9","reference business, application and technology palette");
        Require((int?)nodes[2].Element(ns+"rect")?.Attribute("rx") > 0 && (int?)nodes[1].Element(ns+"rect")?.Attribute("rx") == 0,"business service rounded envelope");
        Require(nodes.Select(n=>n.Element(ns+"path")?.Attribute("d")?.Value).Distinct().Count()==7,"distinct type symbols");
        Require(nodes[3].Elements(ns+"text").Any(x=>x.Value=="«business-process»"),"process stereotype");
        XElement Link(string type) => svg.Descendants(ns+"g").Single(g=>(string?)g.Attribute("class")=="link archimate-"+type.ToLowerInvariant());
        var assignment = Link("Assignment");
        Require((int?)assignment.Attribute("data-source-line")==9 && assignment.Element(ns+"circle")!=null && assignment.Element(ns+"polygon")!=null,"assignment dot and arrow");
        Require(Link("Association").Elements(ns+"polygon").Count()==0, "unadorned association");
        Require((string?)Link("Composition").Element(ns+"polygon")?.Attribute("fill")=="#000000" && (string?)Link("Aggregation").Element(ns+"polygon")?.Attribute("fill")=="none","composition and aggregation diamonds");
        Require(Link("Realization").Elements(ns+"polyline").Any(p=>(string?)p.Attribute("stroke-dasharray")=="1,3") &&
            (string?)Link("Realization").Element(ns+"polygon")?.Attribute("class")=="hollow-triangle","dotted realization and hollow triangle");
        Require(Link("Flow").Elements(ns+"polyline").Any(p=>(string?)p.Attribute("stroke-dasharray")=="7,7"),"dashed flow");
        Require(PlantUmlRenderer.TryRender("@startuml\n!include <archimate/Archimate>\n!define CUSTOM Client\nBusiness_Actor(a, \"CUSTOM\")\n@enduml", out var defined) && defined.Svg.Contains(">Client</text>", StringComparison.Ordinal), "legacy macro substitution");
        foreach (string extra in new[]{"!theme archimate-alternate","!include <archimate/ArchimateSprites>","Business_Role(x, \"Order\", 1)","Business_Future(x, \"Unknown\")","Rel_Assignment(a, unknown)","Rel_Fancy(a, a)"})
            Require(!PlantUmlRenderer.TryRender("@startuml\n!include <archimate/Archimate>\nBusiness_Actor(a, \"Client\")\n" + extra + "\n@enduml",out _), "compatibility for " + extra);
    }

    private static void Require(bool result,string condition)
    {
        if (!result) throw new Exception("ArchiMate test failed: "+condition);
    }
}
