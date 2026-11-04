using System.Text.Json;
using System.Xml.Linq;
using UnoUML.PlantUml;
using UnoUML.PlantUml.Tests;

if (args is ["--catalog", var path])
{
    using var inputs = JsonDocument.Parse(File.ReadAllText(path));
    var counts = new Dictionary<string, int>();
    int supported = 0, total = 0;
    var unsupported = new List<string>();
    foreach (var input in inputs.RootElement.EnumerateArray())
    {
        total++;
        if (!PlantUmlRenderer.TryRender(input.GetProperty("Source").GetString()!, out var result))
        { unsupported.Add(input.GetProperty("Name").GetString()!); continue; }
        if (XDocument.Parse(result.Svg).Root?.Name.LocalName != "svg") throw new Exception("Invalid SVG for " + input.GetProperty("Name").GetString());
        var family = input.GetProperty("Family").GetString()!;
        counts[family] = counts.GetValueOrDefault(family) + 1;
        supported++;
    }
    Console.WriteLine($"Managed catalog coverage: {supported}/{total}. " + string.Join(", ", counts.Select(p => $"{p.Key} {p.Value}")));
    if (unsupported.Count > 0) throw new Exception("Managed catalog cases declined: " + string.Join(", ", unsupported));
    return;
}

PreprocessorCases.Run();
TimRuntimeCases.Run();
BuiltinCases.Run();
SpriteCases.Run();
CreoleCases.Run();
ModernStyleCases.Run();
RichTextCases.Run();
MathCases.Run();
ChenCases.Run();
DitaaCases.Run();
JsonTokenCases.Run();
DataStyleCases.Run();
SequenceCases.Run();
GraphCases.Run();
DotCases.Run();
StateCases.Run();
ActivityCases.Run();
ChartCases.Run();
Console.WriteLine($"PASS {StructuredRendererCases.Run()} structured renderer assertions.");
GanttCases.Run();
NetworkCases.Run();
SaltCases.Run();
RailroadCases.Run();
C4Cases.Run();
ArchimateCases.Run();
TimingCases.Run();
var ns = XNamespace.Get("http://www.w3.org/2000/svg");
XElement Render(string source, DiagramKind kind)
{
    if (!PlantUmlRenderer.TryRender(source, out var result) || result.Kind != kind) throw new Exception("Expected " + kind + " renderer for " + source);
    return XElement.Parse(result.Svg);
}
void Reject(string source)
{
    if (PlantUmlRenderer.TryRender(source, out _)) throw new Exception("Expected reference fallback: " + source);
}
foreach (var (kind, tag) in new[] { (DiagramKind.MindMap,"mindmap"), (DiagramKind.WorkBreakdown,"wbs") })
{
    var document = Render($"\r\n@start{tag}\r\n* Root\r\n** First\r\n*** Target & marker\r\n** Duplicate\r\n** Duplicate\r\n@end{tag}\r\n", kind);
    var identities = document.Descendants(ns+"g").Attributes("data-source-line").Select(a => (int)a).ToArray();
    if (!identities.SequenceEqual(new[] {2,3,4,5,6}) || !document.Value.Contains("Target & marker")) throw new Exception("Outline lost source mapping.");
    Reject($"@start{tag}\n* Root\n*** Missing parent\n@end{tag}");
    var linked = Render($"@start{tag}\n* Root\n** [[https://example.org/ External]]\n@end{tag}", kind);
    if (!linked.Descendants(ns + "a").Any(link => (string?)link.Attribute("href") == "https://example.org/")) throw new Exception("Tree hyperlink missing.");
}
var dot = Render("@startdot\ndigraph Test {rankdir=LR; A -> B -> C [label=\"data\"]; B [shape=box,label=\"Middle\"]; }\n@enddot", DiagramKind.Dot);
if (!dot.Value.Contains("Middle") || dot.Descendants(ns+"g").Count(e => (string?)e.Attribute("class") == "link") != 2) throw new Exception("DOT lost a chained edge or an explicit label.");
var coloredDot = Render("@startdot\ndigraph { a -> b [color=red] }\n@enddot", DiagramKind.Dot);
if (!coloredDot.Descendants().Attributes("stroke").Any(a => a.Value is "red" or "#ff0000")) throw new Exception("DOT edge color missing.");
var creole = Render("@startcreole\n= Notes\n* **One** and //two//\n@endcreole", DiagramKind.Creole);
if (!creole.Descendants(ns+"tspan").Any(x => (string?)x.Attribute("font-style") == "italic" && x.Value == "two")) throw new Exception("Creole did not preserve inline formatting.");
var coloredCreole = Render("@startcreole\n<color:red>Advanced</color>\n@endcreole", DiagramKind.Creole);
if (!coloredCreole.Descendants(ns + "tspan").Any(t => (string?)t.Attribute("fill") == "red" && t.Value == "Advanced")) throw new Exception("Creole rich text lost its inline color.");
var packet = Render("@startpacketdiag\n{\ncolwidth = 8\n0-3: Type\n4-10: Data\n}\n@endpacketdiag", DiagramKind.Packet);
if (packet.Descendants(ns+"rect").Count() != 3) throw new Exception("Packet field crosses row boundary.");
Reject("@startpacketdiag\n{\n0-7: First\n7-8: Overlap\n}\n@endpacketdiag");
var files = Render("@startfiles\n/src/A.cs\n/tests/Tests.cs\n/src/B.cs\n@endfiles", DiagramKind.Files);
if (string.Join(" ", files.Descendants(ns+"text").Select(t=>t.Value)) != "src A.cs B.cs tests Tests.cs") throw new Exception("Directory grouping lost order.");
var json = Render("@startjson\n{\n \"Cost\": 12.5,\n \"Libraries\": [\"Uno\", true],\n \"描画\": null\n}\n@endjson", DiagramKind.Json);
if (!json.Value.Contains("12.5") || !json.Value.Contains("描画") || !json.Descendants().Any(e => (string?)e.Attribute("data-source-line") == "4")) throw new Exception("JSON values or source spans missing.");
Reject("@startmindmap\n*[#notAColour] Root\n@endmindmap");
Reject("@startyaml\na:\n\u00A0\u00A0b: c\n@endyaml");
var yaml = Render("@startyaml\nname: Studio\nformats:\n  - UML\n  - JSON\n@endyaml", DiagramKind.Yaml);
if (!yaml.Value.Contains("JSON") || !yaml.Value.Contains("formats")) throw new Exception("YAML table lost a child.");
var yamlIndicators = Render("@startyaml\nbase: &base editor\ncopy: *base\n@endyaml", DiagramKind.Yaml);
if (!yamlIndicators.Value.Contains("&base editor") || !yamlIndicators.Value.Contains("*base")) throw new Exception("YAML indicator-like scalars lost upstream literal behavior.");
Reject("@startjson\n{broken}\n@endjson");
if (!PlantUmlRenderer.TryRender("@startuml\n!include <C4/C4_Context>\n!$name = %getenv(\"USER\")\nPerson(a,$name)\n@enduml", out var environmentDiagram,
    new PlantUmlRenderOptions { ResolveEnvironment = name => name == "USER" ? "Port user" : null }) || !environmentDiagram.Svg.Contains("Port user", StringComparison.Ordinal))
    throw new Exception("Host environment callback did not reach the native C4 renderer.");
Reject("@startmindmap\n* root\n@endmindmap\n@startuml\nclass A\n@enduml");
Reject("@startuml\nA -> B: bad\u0001\n@enduml");
int partialInputs = EditingInputs.Run();
Console.WriteLine($"Managed renderer regression cases and {partialInputs} partial-input checks passed.");
