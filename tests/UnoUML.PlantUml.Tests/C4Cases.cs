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
        CheckScopedComponents();
        CheckDeployment();
        foreach (string extra in new[] { "!include <C4/C4_Container>", "!define MY_COLOR red", "UpdateElementStyle(svc, $bgColor=\"red\")", "Container(db, \"DB\")", "System(svc, \"Name\", \"Desc\", $tags=\"red\")", "Rel(unknown,svc,\"Uses\")", "System(bad, \"<$icon>\")", "System_Boundary(b, \"Boundary\") {" })
            Require(!PlantUmlRenderer.TryRender("@startuml\n!include <C4/C4_Context>\nSystem(svc, \"App\")\n" + extra + "\n@enduml", out _), "whole-document compatibility fallback: " + extra);
    }
    private static void CheckScopedComponents()
    {
        const string input = """
            @startuml
            !include <C4/C4_Component>
            Person(user, "Visitor")
            System_Boundary(site, "Booking") {
              Container_Boundary(api, "Orders API") {
                Component(ctrl, "Controller", "ASP.NET Core", "Accepts bookings")
                ComponentDb_Ext(data, "Archive", "SQL")
                ComponentQueue(bus, "Events", "AMQP")
              }
              ContainerDb(records, "Records", "PostgreSQL")
              ContainerQueue_Ext(outbox, "Legacy bus", "Kafka")
            }
            Rel(user, ctrl, "Opens", "HTTPS")
            BiRel_R(ctrl, data, "Reads", "SQL")
            Rel_D(ctrl, bus, "Publishes")
            Rel(ctrl, records, "Persists")
            @enduml
            """;
        var svg = Render(input); XNamespace ns = svg.Name.Namespace;
        XElement Item(string id) => svg.Descendants(ns+"g").Single(g=>(string?)g.Attribute("data-qualified-name")==id);
        var site=Item("site"); var api=Item("site.api"); var ctrl=Item("site.api.ctrl"); var archive=Item("site.api.data");
        Require((string?)site.Attribute("data-c4-alias")=="site" && (string?)api.Attribute("data-c4-alias")=="api" &&
            (string?)ctrl.Attribute("data-c4-alias")=="ctrl" && (string?)archive.Attribute("data-c4-alias")=="data", "source aliases remain available alongside qualified visual identities");
        Require(Contains(site,api) && Contains(api,ctrl), "nested boundary containment");
        Require((int?)api.Attribute("data-source-line")==4 && (int?)api.Attribute("data-source-end-line")==8 && (int?)ctrl.Attribute("data-source-line")==5,"unexpanded source mapping");
        Require(site.Element(ns+"rect")?.Attribute("stroke-dasharray")!=null && api.Value.Contains("[container]"),"pinned boundary style");
        Require((string?)ctrl.Element(ns+"rect")?.Attribute("fill")=="#85BBF0" && ctrl.Elements(ns+"text").Any(t=>t.Value=="[ASP.NET Core]" && (string?)t.Attribute("fill")=="#000000" && (string?)t.Attribute("font-style")=="italic"),"component technology and color");
        Require((string?)archive.Element(ns+"path")?.Attribute("fill")=="#CCCCCC" && archive.Value.Contains("«external_component»"),"external component database");
        Require((string?)Item("site.records").Element(ns+"path")?.Attribute("fill")=="#438DD5" && (string?)Item("site.outbox").Element(ns+"path")?.Attribute("stroke")=="#A6A6A6","container database, queue and external palette");
        var link=svg.Descendants(ns+"g").Single(g=>(string?)g.Attribute("class")=="link" && (int?)g.Attribute("data-source-line")==13);
        Require((string?)link.Attribute("data-entity-1")=="site.api.ctrl" && (string?)link.Attribute("data-entity-2")=="site.api.data" && link.Elements(ns+"polygon").Count()==2,"bidirectional cross-card relation");
        Require((int?)ctrl.Element(ns+"rect")?.Attribute("x") < XStartOfPath(archive.Element(ns+"path")!),"right-directed relation");
        foreach(var invalid in new[]{input.Replace("ASP.NET Core","<i>"),input.Replace("ComponentQueue(bus, \"Events\", \"AMQP\")","ComponentQueue(bus, \"Events\", \"AMQP\", \"\", \"icon\")"),input.Replace("Container_Boundary(api, \"Orders API\")","Container_Boundary(api, \"Orders API\", \"customTag\")"), input.Replace("  }","  " )})
            Require(!PlantUmlRenderer.TryRender(invalid,out _),"unsupported customization or malformed scope");
        var containers=Render("@startuml\n!include <C4/C4_Container>\nSystem_Boundary(s,\"Service\") {\n Container(w,\"Worker\",\".NET\")\n}\n@enduml");
        Require(containers.Descendants().Any(x=>(string?)x.Attribute("data-qualified-name")=="s.w"),"Container library entry point");
    }

    private static int XStartOfPath(XElement path)
    {
        string route=(string)path.Attribute("d")!;
        return int.Parse(route[1..route.IndexOf(' ')],System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool Contains(XElement parent, XElement child)
    {
        var p=parent.Element(parent.Name.Namespace+"rect")!; var c=child.Element(child.Name.Namespace+"rect")!;
        int V(XElement e,string a)=>(int)e.Attribute(a)!;
        return V(c,"x")>V(p,"x") && V(c,"y")>V(p,"y") && V(c,"x")+V(c,"width")<V(p,"x")+V(p,"width")
            && V(c,"y")+V(c,"height")<V(p,"y")+V(p,"height");
    }

    private static void CheckDeployment()
    {
        var svg=Render("""
            @startuml
            !include <C4/C4_Deployment>
            Deployment_Node(dc, "Region", "Datacenter", "West region") {
              Node(host, "Worker", "Linux") {
                Container(svc, "API", "Go")
              }
            }
            Node(other, "Standalone", "VM")
            Rel(svc, other, "Connects", "TLS")
            @enduml
            """);
        XNamespace ns=svg.Name.Namespace;
        var dc=svg.Descendants(ns+"g").Single(g=>(string?)g.Attribute("data-qualified-name")=="dc");
        var host=svg.Descendants(ns+"g").Single(g=>(string?)g.Attribute("data-qualified-name")=="dc.host");
        var svc=svg.Descendants(ns+"g").Single(g=>(string?)g.Attribute("data-qualified-name")=="dc.host.svc");
        Require(Contains(dc,host) && Contains(host,svc),"nested deployment frames");
        Require(dc.Value.Contains("«node»") && dc.Value.Contains("[Datacenter]") && dc.Value.Contains("West region") && dc.Element(ns+"rect")?.Attribute("stroke-dasharray")==null,"deployment frame headers and solid border");
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
