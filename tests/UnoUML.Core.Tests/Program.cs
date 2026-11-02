using System.Text.Json;
using UnoUML.Core;

if (args.Contains("--catalog"))
{
    // A portable fixture list for renderer compatibility runs, including every compatible template/tool combination.
    var fixtures = new List<object>();
    foreach (var template in StudioCatalog.Templates)
    {
        fixtures.Add(new { template.Name, template.Family, template.Source });
        foreach (var tool in StudioCatalog.ForPage(new StudioDocument(template.Source).Page))
        {
            var example = new StudioDocument(template.Source);
            example.Add(tool);
            fixtures.Add(new { Name = template.Name + " / " + tool.Name, template.Family, example.Source });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(fixtures));
    return;
}

int assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    assertions++;
}

const string source = "\uFEFF' header\r\n@startuml first\r\nclass \"Shopping cart\" as Cart #red {\r\n  +id : int\r\n}\r\nclass Order\r\nCart --> Order : create\r\n' untouched   \r\n@enduml\r\n\r\n@startmindmap\n* Roadmap\n** Alpha\n@endmindmap\n";
var doc = new StudioDocument(source);
Check(doc.Pages.Count == 2 && doc.Page.Title == "first" && doc.Page.Family == "Class", "pages");
var cart = doc.Find("Cart")!;
Check(cart.Line == 2 && cart.EndLine == 4 && cart.Label == "Shopping cart", "declaration range");
Check(doc.Find(null, 3)?.Kind == "Source", "innermost source line");
Check(doc.Rename(cart, "Basket"), "rename");
Check(doc.Source == source.Replace("Shopping cart", "Basket"), "verbatim rename");
Check(!doc.Remove(cart), "stale span");
Check(doc.Undo() && doc.Source == source && doc.Redo(), "history");
Check(doc.Rename(doc.Find("Order")!, "New order"), "implicit alias rename");
Check(doc.Source.Contains("class \"New order\" as Order\r\nCart --> Order"), "preserve refs");
Check(doc.Duplicate(doc.Find("Cart")!) && doc.Find("CartCopy")?.EndLine == 7, "duplicate block");
Check(doc.Remove(doc.Find("Cart")!) && !doc.Source.Contains("Cart --> Order") && doc.Source.Contains("class \"Basket\" as CartCopy"), "remove incident refs");
Check(doc.Undo(), "undo compound remove");
Check(doc.MoveBefore(doc.Find("Order")!, doc.Find("Cart")!) && doc.Objects.First(x => x.IsConnectable).Alias == "Order", "move");
Check(doc.Connect(doc.Find("Cart")!, doc.Find("Order")!, "-->", "purchase"), "connect");
Check(doc.Source.Contains("Cart --> Order : purchase\r\n@enduml"), "insert within page");
Check(doc.Rename(doc.Objects.Last(o => o.Kind == "Relationship"), "buy") && doc.Source.Contains(" : buy\r\n@enduml"), "edit relation");
doc.ActivePage = 1;
Check(doc.Page.Family == "Mind map" && doc.Objects.Count == 2, "second page");
Check(doc.Rename(doc.Objects.Last(), "Beta") && doc.Source.EndsWith("* Roadmap\n** Beta\n@endmindmap\n"), "edit mixed endings");
Check(doc.Undo() && doc.ActivePage == 1, "undo page selection");

var comments = new StudioDocument("@startuml\n/'\nclass Hidden\n@enduml\n'/\nnote right\nclass AlsoHidden\nend note\nclass Visible\n@enduml");
Check(comments.Pages.Count == 1 && comments.Find("Hidden") == null && comments.Find("AlsoHidden") == null && comments.Find("Visible") != null, "opaque constructs");
var duplicates = new StudioDocument("@startuml\nclass A\nclass A\n@enduml");
Check(duplicates.Find("A") == null && !duplicates.Objects.First().CanRename, "ambiguous aliases");
var nested = new StudioDocument("@startuml\npackage Nest {\n  class A\n}\nclass B\n@enduml");
Check(!nested.MoveBefore(nested.Find("B")!, nested.Find("A")!) && !nested.Duplicate(nested.Find("Nest")!), "scope safety");
var editing = new StudioDocument("");
editing.SetSource("a"); editing.SetSource("ab"); editing.SetSource("abc");
Check(editing.Undo() && editing.Source.Length == 0 && editing.Redo() && editing.Source == "abc", "typing coalesces");
editing.Undo(); editing.SetSource("new");
Check(!editing.CanRedo, "new edit branches history");
var symbols = new StudioDocument("@startuml\nparticipant Alice\nparticipant Bob\nAlice -> Bob\n@enduml");
Check(symbols.Rename(symbols.Objects.Last(), "Hi") && symbols.Source.Contains("Alice -> Bob : Hi"), "unlabeled message");
var escaped = new StudioDocument("@startuml\nclass \"Thing\" as Object #blue ' display\n@enduml");
Check(escaped.Rename(escaped.Find("Object")!, "quote \"A\"") && escaped.Find("Object")!.Label == "quote \"A\"", "quoted label");
foreach (var template in StudioCatalog.Templates)
{
    var sample = new StudioDocument(template.Source);
    Check(sample.Page.Family == template.Family, template.Name + ": detected " + sample.Page.Family);
    foreach (var tool in StudioCatalog.ForPage(sample.Page))
    {
        Check(sample.Add(tool) && sample.Undo() && sample.Source == template.Source, tool.Name + " insert/undo");
    }
}
Console.WriteLine($"PASS {assertions} assertions, {StudioCatalog.Templates.Count} templates, {StudioCatalog.Tools.Count} tools.");
