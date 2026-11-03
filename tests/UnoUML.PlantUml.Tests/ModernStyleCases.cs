using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class ModernStyleCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    public static void Run()
    {
        AdvancedStyleCases.Run();
        string classes = """
            @startuml
            <style>
            root {
              FontName Verdana
              FontColor #1f2f3f
            }
            document { BackgroundColor #eeffee }
            classDiagram {
              class {
                BackgroundColor #abcdef
                LineColor #123456
                LineThickness 3
                FontColor #de137c
                header { FontColor #221177 }
              }
              arrow {
                LineColor #3300ab
                LineThickness 4
                FontColor #196633
                FontSize 20
              }
            }
            </style>
            title Class styles
            class A {
              +member : String
            }
            class B <<Hot>>
            A --> B : relation
            @enduml
            """;
        var graph = Render(classes, DiagramKind.Graph);
        Check((string?)graph.Attribute("font-family") == "Verdana", "root font family");
        Check(Color(graph.Element(Ns + "rect")!, "fill") == "#eeffee", "document background");
        var card = Entity(graph, "A");
        Check(Color(card.Element(Ns + "rect")!, "fill") == "#abcdef" && Color(card.Element(Ns + "rect")!, "stroke") == "#123456", "class appearance");
        Check((string?)card.Attribute("stroke-width") == "3", "class line thickness");
        Check(Color(Text(graph, "A"), "fill") == "#221177" && Color(Text(graph, "+member : String"), "fill") == "#de137c" &&
            Color(Text(graph, "«Hot»"), "fill") == "#de137c", "class body, header and stereotype scopes");
        Check(Color(Text(graph, "Class styles"), "fill") == "#1f2f3f", "document text is outside class scope");
        Check(Color(Text(graph, "relation"), "fill") == "#196633" && (int?)Text(graph, "relation").Attribute("font-size") == 20, "arrow caption style");
        var edge = graph.Descendants(Ns + "g").Single(e => (string?)e.Attribute("class") == "link");
        Check(Color(edge.Elements(Ns + "polyline").Last(), "stroke") == "#3300ab" && (string?)edge.Elements(Ns + "polyline").Last().Attribute("stroke-width") == "4", "arrow stroke style");
        Check((int?)card.Attribute("data-source-line") == Array.IndexOf(classes.Split('\n'), "class A {") &&
            !card.Attributes().Any(a => a.Name.LocalName == "data-source-generated"), "original physical source mapping");

        var sequence = Render("""
            @startuml
            <style>
            root { FontName Verdana; FontColor #123456 }
            sequenceDiagram {
              participant {
                BackgroundColor #aaccff
                LineColor #112233
                LineThickness 3
                FontColor #118844
                FontSize 17
              }
              actor { BackgroundColor #88aa00; LineColor #ff1166; FontColor #11aa22 }
              arrow { LineColor #aa33cc; FontColor #552211; LineThickness 4; FontSize 19 }
              lifeLine { LineColor #ddee11; BackgroundColor #ffeeee }
              note { BackgroundColor #ffffaa; LineColor #448844; FontColor #663366 }
            }
            </style>
            actor Alice
            participant Bob
            Alice -> Bob : Hello
            activate Bob
            newpage Follow up
            Bob --> Alice : Reply
            deactivate Bob
            note right of Bob: Note
            @enduml
            """, DiagramKind.Sequence);
        var sheets = sequence.Elements(Ns + "svg").ToArray();
        Check(sheets.Length == 2 && (string?)sheets[1].Attribute("font-family") == "Verdana", "sequence sheet style inheritance");
        Check(Color(Text(sequence, "Alice"), "fill") == "#11aa22" && Color(Text(sequence, "Bob"), "fill") == "#118844" &&
            (int?)Text(sequence, "Bob").Attribute("font-size") == 17, "participant selectors");
        Check(Color(Text(sequence, "Reply"), "fill") == "#552211" && (int?)Text(sequence, "Reply").Attribute("font-size") == 19, "sequence arrow caption");
        Check(Color(Text(sequence, "Note"), "fill") == "#663366", "sequence note");
        var activation = sheets[1].Descendants(Ns + "g").First(e => (string?)e.Attribute("class") == "activation").Element(Ns + "rect")!;
        Check(Color(activation, "fill") == "#ffeeee" && Color(activation, "stroke") == "#ddee11", "lifeline continuation paint");

        var order = Render("""
            @startuml
            skinparam classBackgroundColor #112233
            <style>
            classDiagram { class { BackgroundColor #aaaacc } }
            class { BackgroundColor: #abcdef; FontColor #334455 }
            classDiagram { .Hot { BackgroundColor #223344; FontColor #445566 } }
            root { FontColor #556677 }
            </style>
            class A
            class B <<hOt>>
            class C <<Hot>> #ffeeaa
            @enduml
            """, DiagramKind.Graph);
        Check(Color(Entity(order, "A").Element(Ns + "rect")!, "fill") == "#abcdef", "later generic style overrides earlier scoped and legacy rules");
        Check(Color(Entity(order, "B").Element(Ns + "rect")!, "fill") == "#223344" && Color(Text(order, "B"), "fill") == "#445566", "case-insensitive stereotype specificity");
        Check(Color(Entity(order, "C").Element(Ns + "rect")!, "fill") == "#ffeeaa", "explicit fill wins");
        Check(Color(Text(order, "A"), "fill") == "#556677", "later root font color");
        var compact = Render("@startuml\n<style>\nclass { FontColor red } root { FontColor blue }\n</style>\nclass A\n@enduml", DiagramKind.Graph);
        Check(Color(Text(compact, "A"), "fill") == "blue", "declaration order within one physical line");
        var laterLegacy = Render("@startuml\n<style>\nclass { BackgroundColor #abcdef }\n</style>\nskinparam classBackgroundColor #112233\nclass A\n@enduml", DiagramKind.Graph);
        Check(Color(Entity(laterLegacy, "A").Element(Ns + "rect")!, "fill") == "#112233", "later legacy rule wins");
        var groups = Render("@startuml\n<style>\nclass, object { LineColor #123456 }\n</style>\nclass A\nobject B\n@enduml", DiagramKind.Graph);
        Check(Color(Entity(groups, "A").Element(Ns + "rect")!, "stroke") == "#123456" && Color(Entity(groups, "B").Element(Ns + "rect")!, "stroke") == "#123456", "comma selector groups");

        Reject("A -> B: Hello\nnote right of B\n<style>\nroot { FontColor red }\n</style>\nend note");
        Reject("class A {\n<style>\nroot { FontColor red }\n</style>\n}");
        Reject("A -> B: Hello\nlegend\n<style>\nroot { FontColor red }\n</style>\nendlegend");
        Check(!PlantUmlRenderer.TryRender("@startuml\nclass A\n@enduml\n<style>\nroot { FontColor red }\n</style>", out _), "style outside document");
        Render("@startuml\n<style>\nclass { Padding 20 }\n</style>\nclass A\n@enduml", DiagramKind.Graph);
        Reject("<style>\nclass { LineThickness NaN }\n</style>\nclass A");
        Reject("<style>\nclass { FontColor url(http://example.invalid) }\n</style>\nclass A");
        Reject("<style>\nunknownSelector { FontColor red }\n</style>\nclass A");
        Reject("<style>\nclass { FontColor red\n</style>\nclass A");
        Reject("<style>\nclass { FontColor red }\nclass A");
        Render("@startuml\n<style>\nroot { BackgroundColor red }\n</style>\nclass A\n@enduml", DiagramKind.Graph);
        Render("@startuml\n<style>\nparticipant { FontColor red }\n</style>\ndatabase A\nA -> B\n@enduml", DiagramKind.Sequence);
        Render("@startuml\n<style>\nclassDiagram { arrow { LineColor red } }\n</style>\nrectangle A\nrectangle B\nA --> B\n@enduml", DiagramKind.Graph);
    }

    private static XElement Render(string source, DiagramKind expected)
    {
        if (!PlantUmlRenderer.TryRender(source, out var result) || result.Kind != expected) throw new Exception("Expected modern styled " + expected);
        return XElement.Parse(result.Svg);
    }

    private static XElement Entity(XElement root, string name) => root.Descendants(Ns + "g").Single(e => (string?)e.Attribute("data-qualified-name") == name);
    private static XElement Text(XElement root, string value) => root.Descendants(Ns + "text").First(e => e.Value == value);
    private static string? Color(XElement node, string attribute) => ((string?)node.Attribute(attribute))?.ToLowerInvariant();
    private static void Reject(string body) => Check(!PlantUmlRenderer.TryRender("@startuml\n" + body + "\n@enduml", out _), "unsupported rule falls back");
    private static void Check(bool valid, string state) { if (!valid) throw new Exception("Modern style test failed: " + state); }
}
