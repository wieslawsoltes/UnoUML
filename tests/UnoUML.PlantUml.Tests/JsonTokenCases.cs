using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class JsonTokenCases
{
    internal static void Run()
    {
        foreach (var nl in new[] {"\n","\r\n","\r"})
        {
            var source = "@startjson"+nl+"{ \"描画🪴\":\"same\", \"other\":\"same\", \"nested\":"+nl+"[true, null, 1e-2, \"\\u96ea\", \"\"]}"+nl+"@endjson";
            if (!PlantUmlRenderer.TryRender(source,out var result)) throw new Exception("JSON source spans require a render");
            var tokens = XElement.Parse(result.Svg).Descendants().Where(e=>e.Attribute("data-json-start")!=null).ToList();
            var raw = tokens.Select(e => source.Substring((int)e.Attribute("data-json-start")!, (int)e.Attribute("data-json-length")!)).ToArray();
            if (!raw.SequenceEqual(new[]{ "\"描画🪴\"", "\"same\"", "\"other\"", "\"same\"", "\"nested\"", "true", "null", "1e-2", "\"\\u96ea\"", "\"\"" }))
                throw new Exception("Unicode or CRLF JSON token offset mismatch");
            if (tokens.Take(5).Any(e=>(int)e.Attribute("data-source-line")! != 1) || tokens.Skip(5).Any(e=>(int)e.Attribute("data-source-line")! != 2))
                throw new Exception("JSON token line mismatch");
            if (tokens[0].Attribute("data-json-kind")!.Value != "key" || tokens[^1].Value.Length == 0) throw new Exception("Invalid JSON edit target");
        }
        foreach (string value in new[] { "@startjson\n{\"x\":\"\\uFFFF\"}\n@endjson", "@startyaml\nx: \"\\uFFFE\"\n@endyaml",
                   "@startjson\n{\"huge\":\""+new string('x',900000)+"\\n\"}\n@endjson" })
            if (PlantUmlRenderer.TryRender(value,out _)) throw new Exception("Invalid or oversized decoded JSON/YAML text must fall back");
    }
}
