using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class ChenCases
{
    public static void Run()
    {
        const string source="""
            @startchen
            left to right direction
            entity "Customer" as CUSTOMER {
              Id : INTEGER <<key>>
              Name {
                First
                Last
              }
              Age <<derived>>
              Aliases <<multi>>
            }
            entity ORDER <<weak>> {
              Number <<key>>
            }
            relationship PLACES <<identifying>> {
              Date : DATE
            }
            PLACES -(0,N)- CUSTOMER
            PLACES =1= ORDER
            @endchen
            """;
        var xml=Render(source);
        var groups=xml.Descendants().Where(e=>e.Name.LocalName=="g").ToArray();
        XElement Item(string id)=>groups.Single(e=>(string?)e.Attribute("data-qualified-name")==id);
        bool Has(XElement e,string tag,int count)=>e.Elements().Count(c=>c.Name.LocalName==tag)==count;
        Verify(Has(Item("PLACES"),"path",2) && Has(Item("ORDER"),"rect",2),"identifying relationship and weak entity double contours");
        Verify(Has(Item("CUSTOMER/Aliases"),"ellipse",2),"multivalued attribute");
        Verify(Item("CUSTOMER/Id : INTEGER").Elements().Any(e=>e.Value=="Id : INTEGER" && (string?)e.Attribute("text-decoration")=="underline"),"key and type label");
        Verify(Item("CUSTOMER/Age").Elements().Any(e=>e.Attribute("stroke-dasharray")!=null),"derived attribute");
        Verify((int?)Item("CUSTOMER/Name/First").Attribute("data-source-line")==5,"nested attribute source mapping");
        var total=groups.Single(e=>(string?)e.Attribute("data-entity-1")=="PLACES" && (string?)e.Attribute("data-entity-2")=="ORDER");
        Verify(total.Elements().Any(e=>(string?)e.Attribute("stroke-width")=="2") && total.Elements().Any(e=>e.Value=="1"),"total participation");
        Verify(xml.Descendants().Any(e=>e.Name.LocalName=="text" && e.Value=="(0,N)"),"cardinality range");
        var sub=Render("@startchen\nentity A {\n}\nentity B {\n}\nentity C {\n}\nA =>= d { B, C }\nB -<- A\n@endchen");
        Verify(sub.Descendants().Any(e=>e.Name.LocalName=="text" && e.Value=="d"),"disjoint specialization");
        var unlabelled=Render("@startchen\nentity A {\n  \"Visible name\" as Field:INTEGER <<key>>\n}\nentity B {\n}\nrelationship R {\n}\nA--R\nR-BOTH-B\n@endchen");
        Verify(unlabelled.Descendants().Any(e=>e.Name.LocalName=="text" && e.Value=="Visible name") &&
            unlabelled.Descendants().Any(e=>(string?)e.Attribute("data-entity-1")=="A"),"unlabelled association and explicit attribute caption");
        foreach(var invalid in new[]{"entity A {}", "entity A {\n a <<unknown>>\n}", "entity A {\n a\n", "entity A {\n}\nA -1- Missing", "!include bad", "<style>\n</style>"})
            Verify(!PlantUmlRenderer.TryRender("@startchen\n"+invalid+"\n@endchen",out _),"unsupported syntax");
    }

    private static XElement Render(string source)
    {
        if(!PlantUmlRenderer.TryRender(source,out var result))throw new InvalidOperationException("Chen render failed:\n"+source);
        return XElement.Parse(result.Svg);
    }
    private static void Verify(bool value,string label)
    {
        if(!value)throw new InvalidOperationException("Chen check failed: "+label);
    }
}
