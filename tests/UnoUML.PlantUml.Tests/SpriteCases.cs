using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class SpriteCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=";

    internal static void Run()
    {
        var gray = Render("""
            @startuml
            sprite $gray {
            F0
            08
            }
            rectangle "<$gray>" as A
            @enduml
            """);
        Check(gray.Descendants(Ns + "path").Any(p => (string?)p.Attribute("fill-opacity") == "0.533333"), "sixteen-level opacity");
        Check(gray.Descendants(Ns + "g").Any(p => (string?)p.Attribute("data-source-line") == "5"), "declaration masking preserves source lines");
        Render("@startuml\nsprite $inline F0F\nrectangle \"<$inline>\" as A\n@enduml");
        var color = Render("@startuml\nsprite $rgb[2x1/color]{\n..zz\nend sprite\nrectangle \"<$rgb>\" as A\n@enduml");
        Check(color.Descendants(Ns + "path").Any(path => (string?)path.Attribute("fill") == "#000000") &&
            color.Descendants(Ns + "path").Any(path => (string?)path.Attribute("fill") == "#ffffff"), "4096-color sprite encoding");
        foreach (var (levels, height) in new[] { (4, 3), (8, 2) })
        {
            var packed = Render($"@startuml\nsprite $packed [2x{height}/{levels}] {{\n_0\n}}\nrectangle \"<$packed>\" as A\n@enduml");
            var raster = packed.Descendants(Ns + "svg").Single();
            Check((string?)raster.Attribute("viewBox") == $"0 0 2 {height}", "packed gray dimensions");
            Check(raster.Descendants(Ns + "path").Single().Attribute("d")!.Value.Count(c => c == 'M') == height, "vertical packed pixels");
        }
        foreach (int levels in new[] { 4, 8, 16 })
        {
            byte[] pixels = [0, (byte)(levels - 1), 1, 0, 0, (byte)(levels - 1)];
            string compressed = Compress(pixels);
            var image = Render($"@startuml\nsprite $zip [2x3/{levels}z] {compressed}\nrectangle \"<$zip>\" as A\n@enduml");
            Check(image.Descendants(Ns + "svg").Any(s => (string?)s.Attribute("viewBox") == "0 0 2 3"), "compressed gray dimensions");
            Check(image.Descendants(Ns + "path").Any(p => (string?)p.Attribute("fill-opacity") == "1" && p.Attribute("d")!.Value.Contains("M1 2h1")), "compressed pixels");
        }
        // Published PlantUML documentation fixture exercises trimmed base64 zeros.
        Render("@startuml\nsprite $printer [15x15/8z] NOtH3W0W208HxFz_kMAhj7lHWpa1XC716sz0Pq4MVPEWfBHIuxP3L6kbTcizR8tAhzaqFvXwvFfPEqm0\nrectangle \"<$printer>\" as A\n@enduml");

        const string art = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 12 8\"><defs><linearGradient id=\"paint\"><stop stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient><path id=\"shape\" d=\"M0 0h12v8H0z\"/></defs><use href=\"#shape\" fill=\"url(#paint)\"/></svg>";
        string twins = "@startuml\nsprite $art " + art + "\nrectangle \"<$art> <$art>\" as A\n@enduml";
        var vector = Render(twins);
        string[] ids = vector.Descendants().Attributes("id").Select(a => a.Value).ToArray();
        Check(ids.Length == ids.Distinct().Count(), "repeated SVG sprite IDs are unique");
        foreach (var use in vector.Descendants(Ns + "use"))
        {
            Check(ids.Contains(use.Attribute("href")!.Value[1..]), "use reference remapped");
            string fill = use.Attribute("fill")!.Value;
            Check(ids.Contains(fill[5..^1]), "gradient reference remapped");
        }
        Check(vector.ToString() == Render(twins).ToString(), "sprite IDs are deterministic per render");
        var isolatedMap = Render("@startuml\nsprite $art <svg viewBox=\"0 0 10 10\"><rect class=\"entity\" data-source-line=\"400\" data-qualified-name=\"Injected\" width=\"10\" height=\"10\"/></svg>\nrectangle \"<$art>\" as A\n@enduml");
        Check(!isolatedMap.Descendants().Attributes("data-qualified-name").Any(attribute => attribute.Value == "Injected") &&
            !isolatedMap.Descendants().Attributes("data-source-line").Any(attribute => attribute.Value == "400"), "embedded artwork cannot supply diagram edit identities");

        var resources = new PlantUmlResourceLibrary(new Dictionary<string, string>
        {
            ["model/art/icon.svg"] = art,
            ["model/art/pixel.png"] = "data:image/png;base64," + Png,
            ["model/parts/sprite.puml"] = "sprite $art ../art/icon.svg"
        });
        var options = new PlantUmlRenderOptions { Resources = resources, SourceFileName = "model/main.puml" };
        Render("@startuml\nsprite $art art/icon.svg\nrectangle \"<$art>\" as A\n@enduml", options);
        Render("@startuml\n!include parts/sprite.puml\nrectangle \"<$art>\" as A\n@enduml", options);
        var bitmap = Render("@startuml\nsprite $art art/pixel.png\nrectangle \"<$art>\" as A\n@enduml", options);
        Check(bitmap.Descendants(Ns + "image").Single().Attribute("href")!.Value == "data:image/png;base64," + Png, "host bitmap resource");
        Check(SequencePages.TryRender("@startuml\nsprite $art " + art + "\nA -> B: <$art>\nnewpage Next\nA -> B: <$art>\n@enduml", out var pages) &&
            pages.Count == 2 && pages.All(p => XDocument.Parse(p.Svg).Descendants(Ns + "use").Any()), "sprites on individual sequence sheets");

        foreach (string body in new[]
        {
            "sprite $bad [9000x9000/16z] ABCD", "sprite $bad [2x2/16z] not_compressed", "sprite $bad {\nZZ\n}",
            "sprite $bad <svg viewBox=\"0 0 5 5\"><script>alert(1)</script></svg>",
            "sprite $bad <svg viewBox=\"0 0 5 5\"><path onload=\"alert(1)\"/></svg>",
            "sprite $bad <svg viewBox=\"0 0 5 5\"><image href=\"https://example.org/icon.png\"/></svg>",
            "sprite $bad <svg viewBox=\"0 0 5 5\"><path style=\"fill:url(https://example.org/icon.svg)\"/></svg>",
            "sprite $bad data:image/png;base64,SGVsbG8=", "sprite $bad ../../missing.svg"
        })
            Check(!PlantUmlRenderer.TryRender("@startuml\n" + body + "\nrectangle \"<$bad>\" as A\n@enduml", out _, options), "malformed or active sprite declined");

        Parallel.For(1, 25, width =>
        {
            string source = "@startuml\nsprite $same {\n" + new string('F', width) + "\n}\nrectangle \"<$same>\" as A\n@enduml";
            Check(Render(source).Descendants(Ns + "svg").Single().Attribute("viewBox")!.Value == $"0 0 {width} 1", "concurrent resource isolation");
        });
        ArchiveResources();
    }

    private static void ArchiveResources()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("parts/icon.puml").Open(), Encoding.UTF8)) writer.Write("sprite $icon pixel.png");
            using (var file = zip.CreateEntry("parts/pixel.png").Open()) file.Write(Convert.FromBase64String(Png));
        }
        stream.Position = 0;
        var library = PlantUmlResourceLibrary.FromZip(stream);
        Check(stream.CanRead && library.Paths.Count == 2, "ZIP leaves input open");
        var options = new PlantUmlRenderOptions { Resources = library };
        Render("@startuml\n!include parts/icon.puml\nrectangle \"<$icon>\" as A\n@enduml", options);
        var cases = new[] { "../escape.puml", "/absolute.puml", "C:/file.puml" };
        foreach (string path in cases)
        {
            bool rejected = false;
            try { _ = new PlantUmlResourceLibrary(new Dictionary<string, string> { [path] = "class A" }); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "archive traversal declined");
        }
        var aliases = new PlantUmlResourceLibrary(new Dictionary<string, string> { ["Icons/Cloud.puml"] = "cloud" });
        Check(aliases.Resolve("<icons/cloud>") == "cloud" && aliases.Resolve("icons/cloud.puml") == null, "standard case folding and local case sensitivity");
    }

    private static string Compress(byte[] pixels)
    {
        using var stream = new MemoryStream();
        using (var deflater = new DeflateStream(stream, CompressionLevel.SmallestSize, leaveOpen: true)) deflater.Write(pixels);
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_";
        const string base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        return new string(Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Select(c => alphabet[base64.IndexOf(c)]).ToArray()).TrimEnd('0');
    }

    private static XElement Render(string source, PlantUmlRenderOptions? options = null)
    {
        if (!PlantUmlRenderer.TryRender(source, out var result, options ?? new())) throw new Exception("Sprite render declined " + source);
        return XElement.Parse(result.Svg);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Sprite case failed " + name);
    }
}
