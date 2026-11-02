using System.Text.Json;
using System.Xml.Linq;
using UnoUML.PlantUml;
using UnoUML.PlantUml.Tests;

if (args is ["--catalog", var path])
{
    using var inputs = JsonDocument.Parse(File.ReadAllText(path));
    var counts = new Dictionary<string, int>();
    int supported = 0, total = 0;
    foreach (var input in inputs.RootElement.EnumerateArray())
    {
        total++;
        if (!PlantUmlRenderer.TryRender(input.GetProperty("Source").GetString()!, out var result)) continue;
        if (XDocument.Parse(result.Svg).Root?.Name.LocalName != "svg") throw new Exception("Invalid SVG for " + input.GetProperty("Name").GetString());
        var family = input.GetProperty("Family").GetString()!;
        counts[family] = counts.GetValueOrDefault(family) + 1;
        supported++;
    }
    Console.WriteLine($"Managed catalog coverage: {supported}/{total}. " + string.Join(", ", counts.Select(p => $"{p.Key} {p.Value}")));
    return;
}

SequenceCases.Run();
GraphCases.Run();
StateCases.Run();
ActivityCases.Run();
ChartCases.Run();
GanttCases.Run();
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
    Reject($"@start{tag}\n* Root\n** [[external]]\n@end{tag}");
}
var packet = Render("@startpacketdiag\n{\ncolwidth = 8\n0-3: Type\n4-10: Data\n}\n@endpacketdiag", DiagramKind.Packet);
if (packet.Descendants(ns+"rect").Count() != 3) throw new Exception("Packet field crosses row boundary.");
Reject("@startpacketdiag\n{\n0-7: First\n7-8: Overlap\n}\n@endpacketdiag");
var files = Render("@startfiles\n/src/A.cs\n/tests/Tests.cs\n/src/B.cs\n@endfiles", DiagramKind.Files);
if (string.Join(" ", files.Descendants(ns+"text").Select(t=>t.Value)) != "src A.cs B.cs tests Tests.cs") throw new Exception("Directory grouping lost order.");
var json = Render("@startjson\n{\n \"Cost\": 12.5,\n \"Libraries\": [\"Uno\", true],\n \"描画\": null\n}\n@endjson", DiagramKind.Json);
if (!json.Value.Contains("12.5") || !json.Value.Contains("描画") || !json.Descendants().Any(e => (string?)e.Attribute("data-source-line") == "4")) throw new Exception("JSON values or source spans missing.");
var yaml = Render("@startyaml\nname: Studio\nformats:\n  - UML\n  - JSON\n@endyaml", DiagramKind.Yaml);
if (!yaml.Value.Contains("JSON") || !yaml.Value.Contains("formats")) throw new Exception("YAML table lost a child.");
Reject("@startyaml\nbase: &base editor\ncopy: *base\n@endyaml");
Reject("@startjson\n{broken}\n@endjson");
Reject("@startuml\n!include <C4/C4_Context>\nPerson(a,\"A\")\n@enduml");
Reject("@startmindmap\n* root\n@endmindmap\n@startuml\nclass A\n@enduml");
Reject("@startuml\nA -> B: bad\u0001\n@enduml");
Console.WriteLine("Managed renderer regression cases passed.");
