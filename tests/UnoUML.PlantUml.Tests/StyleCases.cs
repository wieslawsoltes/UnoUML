using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class StyleCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    public static void Graph()
    {
        var root = Render("""
            @startuml
            skinparam backgroundColor #eef1f3
            skinparam defaultFontColor #102030
            skinparam defaultFontName "Verdana"
            skinparam ArrowColor #123456
            skinparam ArrowFontColor #334455
            skinparam ArrowThickness 2.5
            skinparam class {
              BackgroundColor #bbddff
              BackgroundColor<<Hot>> #ffbbbb
              BorderColor #314159
              BorderColor<<Hot>> #abcdef
              FontColor #876543
              FontColor<<Hot>> #733331
              BorderThickness 3
            }
            class Ordinary {
              +property
            }
            class Special <<Hot>> #AAFFEE
            class Other <<Hot>>
            Ordinary --> Special : Check
            Ordinary -[#229944,dashed,thickness=4]-> Special : Link
            @enduml
            """, DiagramKind.Graph);
        Check((string?)root.Attribute("font-family") == "Verdana" && (string?)root.Element(Ns + "rect")?.Attribute("fill") == "#eef1f3", "global font and background");
        XElement Card(string alias) => root.Descendants(Ns+"g").Single(e => (string?)e.Attribute("data-qualified-name") == alias);
        XElement Shape(string alias) => Card(alias).Element(Ns+"rect")!;
        Check(Color(Shape("Ordinary"), "fill") == "#bbddff" && Color(Shape("Special"), "fill") == "#aaffee" &&
            Color(Shape("Other"),"fill") == "#ffbbbb", "class, stereotype and explicit fill precedence");
        Check(Color(Shape("Ordinary"),"stroke") == "#314159" && Color(Shape("Special"),"stroke") == "#abcdef" &&
            (string?)Card("Special").Attribute("stroke-width") == "3", "class border styles");
        Check(Color(Text(root,"Ordinary"),"fill") == "#876543" && Color(Text(root,"Special"),"fill") == "#733331" &&
            Color(Text(root,"+property"),"fill") == "#102030", "caption and member colors");
        var links = root.Descendants(Ns+"g").Where(e => (string?)e.Attribute("class") == "link").ToArray();
        var first = links[0].Elements(Ns+"polyline").Last(); var second = links[1].Elements(Ns+"polyline").Last();
        Check(Color(first,"stroke") == "#123456" && (string?)first.Attribute("stroke-width") == "2.5", "global link");
        Check(Color(second,"stroke") == "#229944" && (string?)second.Attribute("stroke-width") == "4" &&
            (string?)second.Attribute("stroke-dasharray") == "7,7" && Color(Text(root,"Link"),"fill") == "#334455", "local link style");
        Reject("class A\nclass B\nA -[#red,invisible]-> B");
        Reject("skinparam class {\n InvalidProperty red\n}\nclass A");
        Reject("skinparam ArrowThickness NaN\nclass A");
    }

    public static void Sequence()
    {
        var root = Render("""
            @startuml
            skinparam defaultFontName "Verdana"
            skinparam defaultFontColor #102030
            skinparam NoteBackgroundColor #eeddee
            skinparam NoteBorderColor #dd11ee
            skinparam NoteFontColor #11aa44
            skinparam sequence {
              ArrowColor #123456
              ArrowFontColor #334455
              ArrowThickness 2.5
              ArrowFontSize 18
              ParticipantFontSize 19
              ParticipantBorderThickness 3
              LifeLineBorderColor #e54444
              LifeLineBackgroundColor #bb8866
            }
            participant Alice
            participant Bob
            Alice -> Bob: Forward
            activate Bob
            newpage Continued
            Bob -[#229944]> Alice: Response
            note right of Bob: Note
            @enduml
            """, DiagramKind.Sequence);
        var pages = root.Elements(Ns+"svg").ToArray();
        Check(pages.Length == 2 && (string?)pages[1].Attribute("font-family") == "Verdana", "page style inheritance");
        XElement Group(XElement page,string kind) => page.Descendants(Ns+"g").First(g=>(string?)g.Attribute("class") == kind);
        Check((int?)Text(pages[0],"Alice").Attribute("font-size") == 19 && (string?)Group(pages[0],"participant participant-head").Element(Ns+"rect")?.Attribute("stroke-width") == "3", "participant font size and border");
        Check(Color(Text(root,"Forward"),"fill") == "#334455" && (int?)Text(root,"Forward").Attribute("font-size") == 18, "message font");
        Check(Color(Group(pages[0],"message").Element(Ns+"line")!,"stroke") == "#123456" &&
            Color(Group(pages[1],"message").Element(Ns+"line")!,"stroke") == "#229944" &&
            (string?)Group(pages[1],"message").Element(Ns+"line")!.Attribute("stroke-width") == "2.5", "sequence arrows and local override");
        Check(Color(Group(pages[1],"activation").Element(Ns+"rect")!,"fill") == "#bb8866" &&
            Color(Group(pages[1],"participant-lifeline").Element(Ns+"line")!,"stroke") == "#e54444", "lifeline and continued activation");
        Check(Color(Group(pages[1],"note").Element(Ns+"path")!,"fill") == "#eeddee" &&
            Color(Group(pages[1],"note").Element(Ns+"path")!,"stroke") == "#dd11ee" && Color(Text(root,"Note"),"fill") == "#11aa44", "note styles");
        Reject("skinparam sequenceArrowFontSize -8\nA -> B");
        Reject("skinparam defaultFontName url(http://sample)\nA -> B");
    }

    private static XElement Render(string source, DiagramKind expected)
    {
        if (!PlantUmlRenderer.TryRender(source, out var result) || result.Kind != expected) throw new Exception("Expected styled " + expected);
        return XElement.Parse(result.Svg);
    }
    private static XElement Text(XElement svg,string text) => svg.Descendants(Ns+"text").First(t=>t.Value == text);
    private static string? Color(XElement svg,string key) => ((string?)svg.Attribute(key))?.ToLowerInvariant();
    private static void Reject(string body) => Check(!PlantUmlRenderer.TryRender("@startuml\n"+body+"\n@enduml", out _), "unsupported style");
    private static void Check(bool result,string state) { if (!result) throw new Exception("Skin parameter test failed: "+state); }
}
