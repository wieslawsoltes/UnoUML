using System.Globalization;
using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class AdvancedStyleCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    internal static void Run()
    {
        CascadeAndGeometry();
        CaptionClearance();
        Trees();
        Sequence();
        TitlesAndLegacySequence();
        AutomaticColorsAndMarkers();
        Paint();
        LibraryForms();
        Themes();
    }

    private static void CascadeAndGeometry()
    {
        const string source = """
            @startuml
            <style>
            --surface #112233
            /* Nested selectors intersect semantic names. */
            classDiagram {
              class, object {
                BackgroundColor var(--surface)
                FontSize 25
                Padding 12 20
                MinimumWidth 170
                MaximumWidth 210
                LineThickness 2.5
                .Critical { FontColor #cc2244 }
                header { FontStyle bold }
              }
            }
            root { FontColor #223344 }
            </style>
            class "Long label spanning several words" as A <<Critical>> {
              +A member with several words
            }
            object Other as B
            A --> B
            @enduml
            """;
        var svg = Render(source, DiagramKind.Graph);
        var node = Entity(svg, "A");
        var rect = node.Element(Ns + "rect")!;
        Check((int)rect.Attribute("width")! >= 170 && (int)rect.Attribute("height")! > 100, "font, padding and wrap affect class geometry");
        Check((string?)rect.Attribute("fill") == "#112233" && (string?)rect.Attribute("stroke-width") == "2.5", "variables and fractional border width");
        Check(node.Descendants(Ns + "text").Where(t => (string?)t.Attribute("data-style-part") == "header").Count() > 1, "header wrapping");
        Check(node.Descendants(Ns + "text").Where(t => (string?)t.Attribute("data-style-part") == "header").All(t => (string?)t.Attribute("fill") == "#cc2244"), "stereotype specificity beats later root rule");
        Check((int)node.Attribute("data-source-line")! == Array.IndexOf(source.Split('\n'), "class \"Long label spanning several words\" as A <<Critical>> {"), "style masking preserves physical source lines");

        var qualified = Render("@startuml\nskinparam class<<Urgent>> {\nBackgroundColor #abc123\nFontSize 24\n}\nclass A <<Urgent>>\nclass B\n@enduml", DiagramKind.Graph);
        Check((string?)Entity(qualified, "A").Element(Ns + "rect")!.Attribute("fill") == "#abc123" &&
            (int?)Text(qualified, "A").Attribute("font-size") == 24 && (int?)Text(qualified, "B").Attribute("font-size") == 14, "stereotype-qualified skinparam blocks");

        var badge = Render("@startuml\nskinparam circledCharacterRadius 18\nskinparam circledCharacterFontSize 19\nskinparam stereotypeCBackgroundColor #009988\nskinparam classAttributeIconSize 12\nskinparam iconPublicColor #ff1100\nskinparam classHeaderBackgroundColor #ccddff\nclass A {\n+member\n}\n@enduml", DiagramKind.Graph);
        var spot = badge.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-style-name") == "spot spotclass");
        Check((int?)spot.Element(Ns + "circle")!.Attribute("r") == 18 && (string?)spot.Element(Ns + "circle")!.Attribute("fill") == "#009988", "spot radius and background");
        Check((int?)Text(spot, "C").Attribute("font-size") == 19, "spot typography");
        Check(badge.Descendants(Ns + "g").Any(g => (string?)g.Attribute("data-style-name") == "visibilityicon public" && g.Descendants().Any(e => (string?)e.Attribute("stroke") == "#ff1100")), "member visibility icon styling");
        Check(Entity(badge, "A").Elements(Ns + "path").Any(p => (string?)p.Attribute("fill") == "#ccddff"), "independent class header fill");
    }

    private static void Trees()
    {
        const string source = """
            @startmindmap
            <style>
            mindmapDiagram {
              node { Padding 10; MaximumWidth 130; LineThickness 2 }
              .Branch * { FontColor #bb1100 }
              .Local * { FontColor #007744 }
              depth(2) { BackgroundColor #ffeecc }
              leafNode { LineColor #0044aa }
            }
            </style>
            * Root
            ** Branch <<Branch>>
            *** Leaf with several words
            *** Nested <<Local>>
            **** Last
            @endmindmap
            """;
        var svg = Render(source, DiagramKind.MindMap);
        Check((string?)Text(svg, "Last").Attribute("fill") == "#007744", "nearest ancestor starred style");
        var leaf = svg.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-source-line") == "12");
        Check(leaf.Elements(Ns + "text").All(t => (string?)t.Attribute("fill") == "#bb1100"), "ancestor style propagates to wrapped descendants");
        Check((string?)leaf.Element(Ns + "rect")!.Attribute("fill") == "#ffeecc" && (string?)leaf.Element(Ns + "rect")!.Attribute("stroke") == "#0044aa", "depth and leaf selectors combine");
        var regular = Render("@startwbs\n* Root\n** Child\n@endwbs", DiagramKind.WorkBreakdown);
        var padded = Render("@startwbs\n<style>\nnode { Margin 15 20; FontSize 24; Padding 15 }\n</style>\n* Root\n** Child\n@endwbs", DiagramKind.WorkBreakdown);
        Check((int)padded.Attribute("height")! > (int)regular.Attribute("height")! && (int)padded.Attribute("width")! > (int)regular.Attribute("width")!, "tree margins and font sizes enlarge layout");
    }

    private static void CaptionClearance()
    {
        var svg = Render("@startuml\n<style>\nactor { FontSize 40 }\narrow { FontSize 20; MaximumWidth 130 }\n</style>\nactor Customer\nrectangle API\nCustomer --> API : <size:22>Long relationship caption</size>\n@enduml", DiagramKind.Graph);
        var actor = Entity(svg, "Customer");
        int headY = (int)actor.Element(Ns + "circle")!.Attribute("cy")!;
        int labelY = (int)Text(actor, "Customer").Attribute("y")!;
        Check(labelY - 32 >= headY + 44, "large actor font clears the symbol");
        var caption = svg.Descendants(Ns + "g").Single(g => (string?)g.Attribute("class") == "link").Element(Ns + "rect")!;
        Check((int)caption.Attribute("y")! >= labelY + 12, "wrapped relationship caption clears the actor label");

        var tagged = Render("@startuml\n<style>\nactor { FontSize 32; stereotype { FontSize 18 } }\n</style>\nactor Customer <<Human>>\nrectangle Service\n@enduml", DiagramKind.Graph);
        Check((int)Text(tagged, "Customer").Attribute("y")! - 26 > (int)Text(tagged, "«Human»").Attribute("y")! + 4, "actor stereotype reserves its own caption row");
    }

    private static void Sequence()
    {
        const string source = """
            @startuml
            <style>
            participant { FontSize 22; Padding 16 20; Margin 10 30; MaximumWidth 140 }
            groupHeader { FontSize 24; BackgroundColor #ddffee }
            referenceHeader { FontSize 23; FontColor #cc3300 }
            reference { FontSize 19; Padding 10 20 }
            separator { FontSize 21; BackgroundColor #abcdef }
            note { FontSize 20; Padding 14; MaximumWidth 130 }
            arrow { FontSize 18; MaximumWidth 120; HorizontalAlignment right }
            </style>
            participant "Client with a long name" as A
            participant Server as B
            A -> B : A message spanning several words
            alt approved
            ref over A,B : Review result
            note over A : A note with several words
            else other
            B -> A : Retry
            end
            == Checkpoint ==
            @enduml
            """;
        var svg = Render(source, DiagramKind.Sequence);
        Check((int?)Text(svg, "alt").Attribute("font-size") == 24 && (int?)Text(svg, "ref").Attribute("font-size") == 23, "fragment and reference header fonts");
        Check((string?)Text(svg, "ref").Attribute("fill") == "#cc3300" && (int?)Text(svg, "Checkpoint").Attribute("font-size") == 21, "reference and separator paint");
        var heads = svg.Descendants(Ns + "g").Where(g => (string?)g.Attribute("class") == "participant participant-head").ToArray();
        Check(heads.Length == 2 && heads[0].Elements(Ns + "text").Count() > 1, "participant wrapping");
        Check((int)heads[0].Element(Ns + "rect")!.Attribute("height")! > 60, "participant padding and multiline geometry");
        var note = svg.Descendants(Ns + "g").Single(g => (string?)g.Attribute("class") == "note");
        Check(note.Elements(Ns + "text").Count() > 1 && note.Elements(Ns + "text").All(t => (int?)t.Attribute("font-size") == 20), "note wrapping and metrics");
    }

    private static void Paint()
    {
        const string body = "<style>\nclass { BackgroundColor red-blue; LineColor #234567; LineStyle 4-2; RoundCorner 16; Shadowing 3 }\narrow { LineColor #00aa55; HeadColor #ee3311 }\n</style>\nclass A\nclass B\nA --|> B";
        var svg = Render("@startuml\n" + body + "\n@enduml", DiagramKind.Graph);
        var gradient = svg.Descendants(Ns + "linearGradient").Single();
        Check((string?)gradient.Attribute("x1") == "50%" && (string?)gradient.Attribute("y2") == "100%", "PlantUML vertical gradient direction");
        var rect = Entity(svg, "A").Element(Ns + "rect")!;
        Check(((string?)rect.Attribute("fill"))?.StartsWith("url(#", StringComparison.Ordinal) == true && (string?)rect.Attribute("rx") == "8" && (string?)rect.Attribute("stroke-dasharray") == "4,2", "gradient, corner and dash paints");
        Check(svg.Descendants(Ns + "feDropShadow").Any() && rect.Attribute("filter") != null, "native shadow filter");
        Check(svg.Descendants(Ns + "g").Any(g => (string?)g.Attribute("data-style-fill") == "explicit" && g.Elements(Ns + "path").Any(p => (string?)p.Attribute("fill") == "white")), "hollow inheritance arrow keeps its interior");

        string sketchSource = "@startuml\nskinparam handwritten true\nclass A\nclass B\nA --> B\n@enduml";
        var sketch = Render(sketchSource, DiagramKind.Graph);
        Check((string?)sketch.Attribute("data-handwritten") == "true" && sketch.ToString() == Render(sketchSource, DiagramKind.Graph).ToString(), "deterministic handwritten geometry");
        Check(Entity(sketch, "A").Elements(Ns + "path").Any(p => ((string?)p.Attribute("d"))?.Contains('C') == true), "handwritten box curves");

        Check(SequencePages.TryRender("@startuml\nskinparam dpi 192\nA -> B : First\nnewpage\nA -> B : Second\n@enduml", out var pages) && pages.Count == 2, "scaled sequence pages");
        foreach (var page in pages!)
        {
            var sheet = XElement.Parse(page.Svg);
            Check((int)sheet.Attribute("width")! == page.Width && (int)sheet.Attribute("height")! == page.Height && sheet.Elements(Ns + "g").Any(g => (string?)g.Attribute("transform") == "scale(2)"), "page export dimensions include dpi");
        }
        var activity = Render("@startuml\n<style>\nactivity { BackgroundColor #ffeebb; LineColor #225544; FontColor #123456 }\n</style>\nstart\n:Work;\nstop\n@enduml", DiagramKind.Activity);
        var action = activity.Descendants(Ns + "g").Single(g => (string?)g.Attribute("class") == "activity-Action");
        Check((string?)action.Element(Ns + "rect")!.Attribute("fill") == "#ffeebb" && (string?)Text(action, "Work").Attribute("fill") == "#123456", "activity paint bridge");

        const string jsonSource = "@startjson\r\nskinparam backgroundColor #ddffee\r\n{\"value\":\"ok\"}\r\n@endjson";
        var json = Render(jsonSource, DiagramKind.Json);
        Check((string?)json.Element(Ns + "rect")!.Attribute("fill") == "#ddffee", "CRLF JSON skinparam masking");
        Check(json.Descendants().Where(e => e.Attribute("data-json-start") != null)
            .Select(e => jsonSource.Substring((int)e.Attribute("data-json-start")!, (int)e.Attribute("data-json-length")!))
            .SequenceEqual(["\"value\"", "\"ok\""]), "styled JSON retains original CRLF token offsets");
    }

    private static void TitlesAndLegacySequence()
    {
        const string titleStyle = "<style>\ntitle { FontSize 24; Padding 8 12; Margin 5; MaximumWidth 180; BackgroundColor #112233; FontColor white; LineColor #334455; LineThickness 3; RoundCorner 12 }\n</style>\ntitle <size:32>A rich heading with several words</size>\n";
        foreach (var (body, kind) in new[] { ("class A", DiagramKind.Graph), ("A -> B : Message", DiagramKind.Sequence) })
        {
            var svg = Render("@startuml\n" + titleStyle + body + "\n@enduml", kind);
            var title = svg.Descendants(Ns + "g").Single(g => (string?)g.Attribute("class") == "title");
            var rect = title.Element(Ns + "rect")!;
            Check((string?)rect.Attribute("fill") == "#112233" && (string?)rect.Attribute("stroke") == "#334455" &&
                (string?)rect.Attribute("stroke-width") == "3" && (string?)rect.Attribute("rx") == "6", "title background, border and corners in " + kind);
            Check((int)rect.Attribute("height")! > 90 && title.Elements(Ns + "text").Count() > 2, "title wrapping and rich font geometry in " + kind);
        }
        Check(SequencePages.TryRender("@startuml\n" + titleStyle + "A -> B\nnewpage <size:32>Another long heading</size>\nB -> A\n@enduml", out var pages) &&
            pages.Count == 2 && pages.All(p => XElement.Parse(p.Svg).Descendants(Ns + "g").Any(g => (string?)g.Attribute("class") == "title" && g.Element(Ns + "rect") != null)), "styled titles are repeated and remeasured on sequence pages");

        var large = Render("@startuml\n<style>\ntitle { FontSize 40; Padding 0; BackgroundColor #eeddcc }\nreferenceHeader { FontSize 40; Padding 12 }\nreference { FontSize 24; Padding 8 }\n</style>\ntitle Header text\nA -> B\nref over A : Done\n@enduml", DiagramKind.Sequence);
        var largeTitle = large.Descendants(Ns + "g").Single(g => (string?)g.Attribute("class") == "title").Element(Ns + "rect")!;
        Check((int)largeTitle.Attribute("height")! >= 48 && (int)Text(large, "Header text").Attribute("y")! - (int)largeTitle.Attribute("y")! >= 38, "zero-padding title retains font ink bounds");
        var reference = large.Descendants(Ns + "g").Single(g => (string?)g.Attribute("class") == "reference").Element(Ns + "rect")!;
        Check((int)reference.Attribute("width")! >= 110 && (int)Text(large, "Done").Attribute("y")! - (int)Text(large, "ref").Attribute("y")! >= 50, "reference header padding enlarges frame and separates its body");

        var legacy = Render("""
            @startuml
            skinparam titleFontColor #112233
            skinparam sequenceTitleFontColor #778899
            skinparam sequenceBackgroundColor #aabbcc
            skinparam sequenceGroupBackgroundColor #123456
            skinparam sequenceGroupHeaderBackgroundColor #654321
            skinparam sequenceGroupFontColor #445566
            skinparam sequenceGroupHeaderFontColor #667788
            skinparam sequenceGroupFontSize 18
            skinparam sequenceGroupHeaderFontSize 24
            skinparam sequenceReferenceFontColor #119933
            skinparam sequenceReferenceHeaderFontColor #991133
            skinparam sequenceReferenceBorderColor #335511
            skinparam sequenceGroupBorderColor #337799
            title Heading
            A -> B : Test
            alt Cond
            ref over A,B : Body
            else Other
            B -> A : Bye
            end
            @enduml
            """, DiagramKind.Sequence);
        var fragment = legacy.Descendants(Ns + "g").Single(g => (string?)g.Attribute("class") == "fragment");
        Check((string?)fragment.Element(Ns + "path")!.Attribute("fill") == "#123456" &&
            (string?)fragment.Element(Ns + "path")!.Attribute("stroke") == "#337799", "legacy sequence fragment header mapping");
        Check(legacy.Descendants(Ns + "g").Where(g => (string?)g.Attribute("data-style-name") == "group").Elements(Ns + "rect").All(e => (string?)e.Attribute("fill") == "none"), "header background does not fill the fragment body");
        Check((string?)Text(legacy, "Cond").Attribute("fill") == "#445566" && (int?)Text(legacy, "Cond").Attribute("font-size") == 18 &&
            (string?)Text(legacy, "alt").Attribute("fill") == "#667788" && (int?)Text(legacy, "alt").Attribute("font-size") == 24, "fragment keyword and condition use independent styles and metrics");
        Check((string?)Text(legacy, "ref").Attribute("fill") == "#119933" && (string?)Text(legacy, "Body").Attribute("fill") == "#119933", "legacy reference font applies to header and body");
        Check(legacy.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-style-name") == "referenceheader").Elements(Ns + "path").All(p => (string?)p.Attribute("stroke") == "#335511"), "legacy reference border applies to header");
        Check((string?)Text(legacy, "Heading").Attribute("fill") == "#112233" && !legacy.Elements(Ns + "rect").Any(), "inactive legacy sequence keys preserve reference behavior");
    }

    private static void LibraryForms()
    {
        var spaced = Render("@startuml\nskinparam folderBackgroundColor<<MA ACCOUNT>> White\nskinparam rectangle<<FA USER>>FontColor #aabbcc\nfolder Account <<MA ACCOUNT>>\nrectangle User <<FA USER>>\n@enduml", DiagramKind.Graph);
        Check((string?)Entity(spaced, "Account").Element(Ns + "path")!.Attribute("fill") == "white" &&
            (string?)Text(spaced, "User").Attribute("fill") == "#aabbcc", "inline skinparam stereotypes containing spaces");
        var c4 = Render("""
            @startuml
            skinparam PackageTitleAlignment Left
            skinparam maxMessageSize 150
            skinparam rectangle<<boundary>> {
              BorderStyle dashed
            }
            skinparam rectangle<<service>>BackgroundColor #ddffee
            <style>
            .boundary { LineColor #224466 }
            .boundary { .service { FontColor #aa2255 } }
            </style>
            rectangle "== Group" <<boundary>><<service>> as Group {
              rectangle "== API\n//[C#]//" <<service>> as A
              database "== Store" <<service>> as B
            }
            A -->> B : **Uses**
            @enduml
            """, DiagramKind.Graph);
        Check((string?)Entity(c4, "Group").Element(Ns + "rect")!.Attribute("stroke") == "#224466", "expanded library stereotype blocks");
        Check(Entity(c4, "Group").Elements(Ns + "text").Where(t => t.Value == "Group").All(t => (string?)t.Attribute("fill") == "#aa2255"), "combined stereotype specificity");
        Check(Entity(c4, "Group").Element(Ns + "rect")!.Attribute("stroke-dasharray") != null, "library boundary border style");
        var archimate = Render("@startuml\nskinparam lineType curve\narchimate #BUSINESS \"User\" <<business-actor>> as a\narchimate #APPLICATION \"Service\" <<application-component>> as b\na --> b\n@enduml", DiagramKind.Graph);
        Check((string?)Entity(archimate, "a").Element(Ns + "rect")!.Attribute("fill") == "#ffffcc" && (string?)Entity(archimate, "b").Element(Ns + "rect")!.Attribute("fill") == "#c2f0ff", "named ArchiMate palette");
        Check(archimate.Descendants(Ns + "path").Count(p => (string?)p.Attribute("class") == "archimate-type-glyph") == 2, "expanded ArchiMate symbols");
        Check(archimate.Descendants(Ns + "g").Single(g => (string?)g.Attribute("class") == "link").Elements(Ns + "path").Any(), "curved routing emits a path");
    }

    private static void AutomaticColorsAndMarkers()
    {
        foreach (var (background, expected) in new[] { ("white", "#000000"), ("black", "#ffffff"), ("#7f7f7f", "#ffffff"), ("#808080", "#000000"), ("red-blue", "#ffffff") })
        {
            var svg = Render("@startuml\n<style>\nclass { BackgroundColor " + background + "; FontColor automatic }\n</style>\nclass A\n@enduml", DiagramKind.Graph);
            Check((string?)Text(svg, "A").Attribute("fill") == expected, "automatic text against " + background);
        }
        foreach (string body in new[]
        {
            "skinparam classBackgroundColor white\nskinparam classFontColor automatic\nclass A #black",
            "skinparam classBackgroundColor white\nskinparam classHeaderBackgroundColor black\nskinparam classFontColor automatic\nclass A",
            "skinparam backgroundColor black\nskinparam classBackgroundColor transparent\nskinparam classFontColor automatic\nclass A"
        })
            Check((string?)Text(Render("@startuml\n" + body + "\n@enduml", DiagramKind.Graph), "A").Attribute("fill") == "#ffffff", "automatic text respects explicit, header and transparent backgrounds");
        var sequence = Render("@startuml\nskinparam participantBackgroundColor black\nskinparam participantFontColor automatic\nA -> B\n@enduml", DiagramKind.Sequence);
        Check(sequence.Descendants(Ns + "text").Where(t => t.Value == "A").All(t => (string?)t.Attribute("fill") == "#ffffff"), "automatic participant text on both sequence headers");
        var tree = Render("@startmindmap\n<style>\nnode { FontColor automatic }\n</style>\n*[#black] Root\n**[#white] Child\n@endmindmap", DiagramKind.MindMap);
        Check((string?)Text(tree, "Root").Attribute("fill") == "#ffffff" && (string?)Text(tree, "Child").Attribute("fill") == "#000000", "automatic tree text uses each node fill");
        Check(!PlantUmlRenderer.TryRender("@startuml\n<style>\nactivity { FontColor automatic }\n</style>\nstart\n:Work;\nstop\n@enduml", out _), "automatic text delegates when painter background metadata is incomplete");

        string Chart(string shape, string extra = "") => "@startchart\n<style>\nscatter { MarkerShape " + shape + "; MarkerColor red; " + extra + " }\n</style>\nh-axis [a,b]\nv-axis 0 --> 10\nscatter \"Measured\" [2,4] #00aa00\nlegend bottom\n@endchart";
        foreach (var (shape, element) in new[] { ("circle", "circle"), ("square", "rect"), ("triangle", "polygon") })
        {
            var chart = Render(Chart(shape), DiagramKind.Chart);
            var markers = chart.Descendants().Where(e => e.Attribute("data-chart-marker") != null).ToArray();
            Check(markers.Length == 3 && markers.All(m => m.Name.LocalName == element && (string?)m.Attribute("fill") == "red" && (string?)m.Attribute("stroke") == "red"), "scatter " + shape + " shape and color include legend");
            Check(markers.Count(m => m.Attribute("data-point-index") != null) == 2, "styled markers retain point editing metadata");
        }
        foreach (string unsupported in new[] { "diamond", "cross", "none" })
            Check(!PlantUmlRenderer.TryRender(Chart(unsupported), out _), "unsupported marker " + unsupported + " delegates");
        Check(!PlantUmlRenderer.TryRender(Chart("square", "MarkerSize 12"), out _), "marker size retains measured-layout fallback");
    }

    private static void Themes()
    {
        string[] names = ["_none_", "amiga", "aws-orange", "black-knight", "bluegray", "blueprint", "carbon-gray", "cerulean-outline", "cerulean", "cloudscape-design", "crt-amber", "crt-green", "cyborg-outline", "cyborg", "hacker", "lightgray", "mars", "materia-outline", "materia", "metal", "mimeograph", "minty", "mono", "plain", "reddress-darkblue", "reddress-darkgreen", "reddress-darkorange", "reddress-darkred", "reddress-lightblue", "reddress-lightgreen", "reddress-lightorange", "reddress-lightred", "sandstone", "silver", "sketchy-outline", "sketchy", "spacelab-white", "spacelab", "sunlust", "superhero-outline", "superhero", "toy", "united", "vibrant"];
        foreach (string name in names)
        {
            Render("@startuml\n!theme " + name + "\nclass A <<Important>> {\n+member : String\n}\nclass B\nA --> B : relation\n@enduml", DiagramKind.Graph);
            Render("@startuml\n!theme " + name + "\nparticipant A\nactor B\nA -> B : message\n@enduml", DiagramKind.Sequence);
        }
    }

    private static XElement Render(string source, DiagramKind kind)
    {
        if (!PlantUmlRenderer.TryRender(source, out var result) || result.Kind != kind) throw new InvalidOperationException("Advanced style input was declined for " + kind + "\n" + source);
        return XElement.Parse(result.Svg);
    }

    private static XElement Entity(XElement root, string name) => root.Descendants(Ns + "g").Single(g => (string?)g.Attribute("data-qualified-name") == name);
    private static XElement Text(XElement root, string value) => root.Descendants(Ns + "text").First(t => t.Value == value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Advanced style regression failed for " + message); }
}
