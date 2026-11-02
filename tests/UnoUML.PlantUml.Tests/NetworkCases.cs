using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class NetworkCases
{
    public static void Run()
    {
        const string office = """
            nwdiag {
              network office {
                address = "192.0.2.0/24";
                client [address = "192.0.2.10"];
                server [address = "192.0.2.20"];
              }
            }
            """;
        var first = Render(office);
        var labels = first.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToHashSet();
        if (!labels.SetEquals(new[] { "office", "192.0.2.0/24", "192.0.2.10", "192.0.2.20", "client", "server" }))
            throw new InvalidOperationException("Network addresses differ from PlantUML output.");
        var network = first.Descendants().Single(e => (string?)e.Attribute("class") == "network");
        if ((int?)network.Attribute("data-source-line") != 2) throw new InvalidOperationException("Network declaration line mismatch.");
        var two = Render("nwdiag {\nnetwork dmz {\ngw [address=\"a,b\",description=\"Gateway\"];\n}\nnetwork lan {\ngw [address=\"c\"];\ndb [address=\"d\",shape=database];\n}\n}");
        if (two.Descendants().Count(e => (string?)e.Attribute("data-qualified-name") == "gw") != 1 ||
            two.Descendants().Count(e => (string?)e.Attribute("class") == "interface") != 3 ||
            two.Descendants().Count(e => e.Name.LocalName == "text" && e.Value == "Gateway") != 1)
            throw new InvalidOperationException("A shared network node must have one shape and one interface on each bus.");
        foreach (var bad in new[]{ "nwdiag {\ngroup {\na\n}\n}", "nwdiag {\nnetwork X {\na [unknown=42]\n}\n}", "nwdiag {\nnetwork X {\na [description=\"<$sprite>\"]\n}\n}",
            "nwdiag {\nnetwork X {\na\n}", "nwdiag {\nnetwork X {\na [color=\"url(foo)\"]\n}\n}" })
            if (PlantUmlRenderer.TryRender("@startnwdiag\n" + bad + "\n@endnwdiag", out _)) throw new InvalidOperationException("Unsupported NWDiag was accepted.");
    }
    private static XElement Render(string body)
    {
        if (!PlantUmlRenderer.TryRender("@startnwdiag\n" + body + "\n@endnwdiag", out var diagram)) throw new InvalidOperationException("Network render failed: " + body);
        return XElement.Parse(diagram.Svg);
    }
}
