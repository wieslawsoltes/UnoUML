using System.Text.Json;
using UnoUML.Core;

if (args.Contains("--catalog"))
{
    // A portable fixture list for renderer compatibility runs, including every compatible template/tool combination.
    var fixtures = new List<object>();
    foreach (var template in StudioCatalog.Templates)
    {
        fixtures.Add(new { template.Name, template.Family, template.RequiresServer, template.RequiresReferenceServer, template.Source });
        foreach (var tool in StudioCatalog.ForPage(new StudioDocument(template.Source).Page))
        {
            var example = new StudioDocument(template.Source);
            example.Add(tool);
            fixtures.Add(new { Name = template.Name + " / " + tool.Name, template.Family, template.RequiresServer, template.RequiresReferenceServer, example.Source });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(fixtures));
    return;
}

// Match the reflection-free JSON contract of the trimmed browser build.
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
int assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    assertions++;
}
Check(!JsonSerializer.IsReflectionEnabledByDefault, "JSON reflection is disabled for editing regressions");

const string source = "\uFEFF' header\r\n@startuml first\r\nclass \"Shopping cart\" as Cart #red {\r\n  +id : int\r\n}\r\nclass Order\r\nCart --> Order : create\r\n' untouched   \r\n@enduml\r\n\r\n@startmindmap\n* Roadmap\n** Alpha\n@endmindmap\n";
var doc = new StudioDocument(source);
Check(doc.Pages.Count == 2 && doc.Page.Title == "first" && doc.Page.Family == "Class", "pages");
var cart = doc.Find("Cart")!;
Check(cart.Line == 2 && cart.EndLine == 4 && cart.Label == "Shopping cart", "declaration range");
Check(doc.Find(null, 3)?.Kind == "Member", "innermost member line");
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
var styled = new StudioDocument("@startuml\nclass A #red {\n  +id\n}\nclass B ' keep this\nA \"1\" -- \"many\" B : dependencies\n@enduml");
Check(styled.SetFill(styled.Find("A")!, "#3271ff") && styled.Source.Contains("A #3271ff {"), "replace color");
Check(styled.SetFill(styled.Find("B")!, "#3271ff") && styled.Source.Contains("B #3271ff ' keep this"), "insert color");
Check(!styled.SetFill(styled.Find("A")!, "red"), "reject invalid color");
Check(styled.InsertMember(styled.Find("B")!, "+save()") && styled.Source.Contains("class B #3271ff { ' keep this\n  +save()\n}"), "add first member");
Check(styled.InsertMember(styled.Find("A")!, "+cancel()") && styled.Source.Contains("  +id\n  +cancel()\n}"), "append member");
Check(styled.SetArrow(styled.Objects.Last(), "*--"), "change arrow");
Check(styled.Retarget(styled.Objects.Last(), styled.Find("B")!, styled.Find("A")!) && styled.Source.Contains("B \"1\" *-- \"many\" A : dependencies"), "retarget with multiplicity");
var layout = new StudioDocument("@startuml\nclass A\nclass B\nclass C\nA -[hidden]-> C\n@enduml");
Check(layout.PlaceRelative(layout.Find("B")!, layout.Find("A")!, "left") && layout.Source.Contains("A -[hidden]left-> B"), "directional layout");
Check(layout.PlaceRelative(layout.Find("A")!, layout.Find("B")!, "down") && layout.Objects.Count(o => o.Kind == "Layout") == 1 && layout.Source.Contains("B -[hidden]down-> A"), "replace managed edge");
Check(layout.Remove(layout.Find("B")!) && !layout.Source.Contains("@unouml:layout") && layout.Source.Contains("A -[hidden]-> C"), "delete managed layout, retain unrelated handwritten constraint");
Check(layout.Undo() && layout.Source.Contains("@unouml:layout"), "atomic layout delete undo");
var rotated = new StudioDocument("@startuml\nleft to right direction\npackage P {\n  class A\n  class B\n}\n@enduml");
Check(rotated.PlaceRelative(rotated.Find("A")!, rotated.Find("B")!, "right") && rotated.Source.Contains("  B -[hidden]down-> A\n}"), "rank rotation and nested placement");
var library = new StudioDocument("@startuml\nclass A\n@enduml");
Check(library.Include("C4/C4_Context") && library.Source.StartsWith("@startuml\n!include <C4/C4_Context>\n") && !library.Include("C4/C4_Context") && !library.Include("../foo"), "safe include");
var multi = new StudioDocument("@startuml\r\nclass A\r\n' retained note\r\nclass B {\r\n  +field\r\n}\r\nclass ACopy\r\nclass Remote\r\nA --> B : internal\r\nB --> Remote : outbound\r\n@enduml");
string baseline = multi.Source;
DiagramObject[] Pair() => [multi.Find("A")!, multi.Find("B")!];
string fragment = multi.CopySelection(Pair())!;
Check(fragment.Contains("' retained note\r\n") && fragment.Contains("A --> B : internal") && !fragment.Contains("outbound"), "copy induced subgraph and trivia");
Check(multi.DuplicateMany(Pair()) && multi.Source.Contains("ACopy2 --> BCopy : internal") && multi.Source.Contains("B --> Remote : outbound"), "clone without alias collisions");
Check(multi.Undo() && multi.Source == baseline, "undo entire duplicate");
Check(multi.RemoveMany(Pair()) && !multi.Source.Contains("outbound") && multi.Source.Contains("class Remote") && multi.Source.Contains("' retained note"), "multi-delete with trivia and incident edge cleanup");
Check(multi.Undo() && multi.Source == baseline, "undo entire delete");
Check(multi.Group(Pair(), "Domain") && multi.Find("Domain") != null && multi.Find("Domain")!.Statement.Contains("A --> B : internal") && multi.Source.Contains("B --> Remote : outbound"), "group as package preserving edges");
Check(multi.Undo() && multi.Source == baseline, "undo group");
Check(multi.PasteSelection("class A\r\nA --> Remote.A") && multi.Source.Contains("ACopy2 --> Remote.A"), "external qualified names survive paste");
Check(!multi.PasteSelection("@startuml\nclass Z\n@enduml"), "no nested page paste");
Check(multi.Undo() && multi.Source == baseline, "undo paste");
Check(multi.CutSelection(Pair(), out string cut) && cut == fragment && !multi.Source.Contains("class B"), "atomic cut");
var nestedGroup = new StudioDocument("@startuml\npackage Outer {\n class A\n class B\n}\nclass C\nOuter.A --> C\n@enduml");
Check(nestedGroup.Group([nestedGroup.Find("A")!, nestedGroup.Find("B")!], "Domain") && nestedGroup.Source.Contains("Outer.Domain.A --> C"), "qualified external refs track package");
Check(nestedGroup.DuplicateMany([nestedGroup.Find("Outer")!]) && nestedGroup.Find("OuterCopy") != null && nestedGroup.Find("DomainCopy") != null, "duplicate nested declarations");
Check(!nestedGroup.Group([nestedGroup.Find("A")!, nestedGroup.Find("C")!], "Bad"), "reject grouping across scopes");
var ambiguous = new StudioDocument("@startuml\nclass A\n@enduml");
Check(!ambiguous.PasteSelection("class A\nUNKNOWN_MACRO(A)"), "refuse ambiguous macro rewriting");
var origin = new StudioDocument(source);
Check(origin.FindAtOffset(source.IndexOf("Alpha"), out int pIndex)?.Label == "Alpha" && pIndex == 1 && origin.ActivePage == 0, "offset lookup across diagrams");
var guarded = new StudioDocument("@startuml\n!if $enabled\n!if $nested\nclass Hidden\n!endif\nclass Hidden2\n!endif\nclass Visible\n@enduml");
Check(guarded.Find("Hidden") == null && guarded.Find("Hidden2") == null && guarded.Find("Visible") != null, "nested preprocessing is opaque");
var branches = new StudioDocument("@startuml\nstart\nif (OK?) then (yes)\n:Accept;\nelse (no)\n:Decline;\nendif\nstop\n@enduml");
Check(!branches.MoveBefore(branches.Objects.First(o => o.Label == "Decline"), branches.Objects.First(o => o.Label == "Accept")), "move cannot cross branches");
foreach (var template in StudioCatalog.Templates)
{
    var sample = new StudioDocument(template.Source);
    Check(sample.Page.Family == template.Family, template.Name + ": detected " + sample.Page.Family);
    foreach (var tool in StudioCatalog.ForPage(sample.Page))
    {
        Check(sample.Add(tool) && sample.Undo() && sample.Source == template.Source, tool.Name + " insert/undo");
    }
}
var inline = new StudioDocument("@startuml\r\nclass Cart {\r\n  +{static} total() : Money   ' shown at checkout\r\n}\r\nclass User\r\nUser --> Cart\r\n@enduml");
var member = inline.Find(null, 2)!;
Check(member.Kind == "Member" && member.EditableLabel == "total() : Money", "member field exposes a precise editable span");
string originalInline = inline.Source;
Check(inline.Rename(member, "sum() : Money") && inline.Source == originalInline.Replace("total()", "sum()"), "member editing preserves visibility, modifiers, comments and newline spelling");
Check(inline.Undo() && inline.Source == originalInline, "member edit undoes in one transaction");
Check(inline.Find(null, 5)!.EditableLabel == "", "unlabeled relationship opens an empty label field");
var controlFlow = new StudioDocument("@startuml\nstart\nif (Has capacity?) then (yes)\n  :Process;\nendif\nrepeat\n :Try;\nrepeat while (Retry?) is (yes) not (no)\nstop\n@enduml");
Check(controlFlow.Rename(controlFlow.Find(null,2)!, "Available?") && controlFlow.Source.Contains("if (Available?) then (yes)"), "inline decision edit preserves outgoing condition labels");
Check(controlFlow.Rename(controlFlow.Find(null,7)!, "Repeat?") && controlFlow.Source.Contains("repeat while (Repeat?) is (yes) not (no)"), "inline repeat label edits the closing condition span");
const string wireSource = "@startuml\r\nclass A\r\nclass B\r\nclass C\r\nB \"1\" <|-- \"many\" A : builds   ' keep\r\n@enduml";
var wire = new StudioDocument(wireSource);
Check(wire.UpdateConnection(wire.Find(null,4)!, null, wire.Find("C"), "<|--") && wire.Source == wireSource.Replace(" A : builds"," C : builds"), "endpoint drag preserves arrow, lexical roles, multiplicity, label and comment");
Check(wire.Undo() && wire.Source == wireSource, "endpoint drag undoes atomically");
const string compactJson = "@startjson\r\n{\"é🪴\":\"same\",\"b\":\"same\",\"amount\":123}\r\n@endjson";
var jsonEditor = new StudioDocument("@startuml\r\nA -> B\r\n@enduml\r\n"+compactJson);
jsonEditor.ActivePage = 1;
int second = compactJson.LastIndexOf("\"same\"",StringComparison.Ordinal);
var field = jsonEditor.OpenJsonField(second,6,"string")!;
var jsonBaseline = jsonEditor.Source;
Check(field.Text == "same" && jsonEditor.ApplyJsonField(field,"changed") && jsonEditor.Source == jsonBaseline.Replace("\"b\":\"same\"","\"b\":\"changed\""), "JSON same-line duplicate strings use UTF-16 token spans after Unicode text");
Check(!jsonEditor.ApplyJsonField(field,"stale"), "JSON stale token rejected");
Check(jsonEditor.Undo() && jsonEditor.Source == jsonBaseline, "JSON replacement preserves CRLF and undo");
var keyEdit = jsonEditor.OpenJsonField(compactJson.IndexOf("\"b\"",StringComparison.Ordinal),3,"key")!;
Check(jsonEditor.ApplyJsonField(keyEdit,"empty") && jsonEditor.Source == jsonBaseline.Replace("\"b\":","\"empty\":"), "inline JSON key");
Check(jsonEditor.Undo(), "undo JSON key rename");
int amount = compactJson.IndexOf("123",StringComparison.Ordinal);
var typed = jsonEditor.OpenJsonField(amount,3,"scalar")!;
Check(!jsonEditor.ApplyJsonField(typed,"4x") && !jsonEditor.ApplyJsonField(typed,"{}") && jsonEditor.Source==jsonBaseline, "invalid and composite JSON typed edits are atomic");
Check(jsonEditor.OpenJsonField(amount+1,1,"scalar")==null && jsonEditor.OpenJsonField(amount,3,"key")==null, "reject stale, partial and wrong-role JSON ranges");
Check(jsonEditor.ApplyJsonField(typed,"null") && jsonEditor.Source == jsonBaseline.Replace("123","null"), "JSON scalar type transition");
C4EditCases.Run(Check);
LegacyEditCases.Run(Check);
SequenceEditCases.Run(Check);
BlockEditCases.Run(Check);
assertions += ArchiveEditCases.Run();
assertions += SourceFieldCases.Run();
assertions += SaltEditCases.Run();
assertions += YamlFieldCases.Run();
assertions += CanvasLayoutCases.Run();
assertions += StructuredEditCases.Run();
Console.WriteLine($"PASS {assertions} assertions, {StudioCatalog.Templates.Count} templates, {StudioCatalog.Tools.Count} tools.");
