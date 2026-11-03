using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class RendererRetryCases
{
    private const string CustomLibrary = "!log library\n!procedure Custom($text)\nrectangle \"$text\" as A\n!endprocedure";

    internal static void Run()
    {
        PureRetry();
        ProviderBoundary();
        ImportedLibraryRetry();
        ResourceFailures();
        DirectC4();
        DiagnosticBudget();
        DiagnosticFailures();
        PlainDiagram();
    }

    private static void PureRetry()
    {
        const string source = "@startuml\n!include settings.iuml\n!include <C4/C4_Context>\n!$missing=%file_exists(\"absent.iuml\")\n!log root $caption $missing\nCustom($caption)\n@enduml";
        var reads = new Dictionary<string, int>();
        var logs = new List<string>();
        var options = new PlantUmlRenderOptions
        {
            ResolveInclude = path =>
            {
                reads[path] = reads.GetValueOrDefault(path) + 1;
                return path switch
                {
                    "settings.iuml" => "!$caption=\"snapshot" + reads[path] + "\"",
                    "<c4/c4_context>" => CustomLibrary,
                    _ => null
                };
            },
            Log = logs.Add
        };
        for (int invocation = 1; invocation <= 2; invocation++)
        {
            logs.Clear();
            Check(PlantUmlRenderer.TryRender(source, out var diagram, options) && diagram.Kind == DiagramKind.Graph &&
                XElement.Parse(diagram.Svg).Value.Contains("snapshot" + invocation), "pure custom C4 expanded retry");
            Check(reads.Count == 3 && reads.Values.All(count => count == invocation), "source and missing-resource snapshots are render-local");
            Check(logs.SequenceEqual(new[] { "library", "root snapshot" + invocation + " 0" }), "only selected attempt diagnostics are delivered");
        }
    }

    private static void ProviderBoundary()
    {
        foreach (string provider in new[] { "random", "clock", "environment" })
        {
            int randomCalls = 0, clockCalls = 0, environmentCalls = 0, archiveCalls = 0, libraryReads = 0;
            var random = new Random(1729);
            var expectedRandom = new Random(1729);
            var logs = new List<string>();
            var options = new PlantUmlRenderOptions
            {
                ResolveInclude = _ => { libraryReads++; return CustomLibrary; },
                ImportLibrary = _ => { archiveCalls++; return new PlantUmlResourceLibrary([]); },
                RandomInteger = (minimum, maximum) => { randomCalls++; return random.Next(minimum, maximum); },
                UtcNow = () => { clockCalls++; return DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + clockCalls); },
                ResolveEnvironment = _ => { environmentCalls++; return "environment" + environmentCalls; },
                Log = logs.Add
            };
            string expression = provider switch
            {
                "random" => "%random(100)",
                "clock" => "%now()",
                _ => "%getenv(\"DEMO\")"
            };
            string source = "@startuml\n!include <C4/C4_Context>\n!$caption=" + expression + "\n!log attempt\nCustom($caption)\n@enduml";
            Check(!PlantUmlRenderer.TryRender(source, out var diagram, options) && diagram == null, provider + " prevents effectful retry");
            Check(randomCalls == (provider == "random" ? 1 : 0) && clockCalls == (provider == "clock" ? 1 : 0) &&
                environmentCalls == (provider == "environment" ? 1 : 0) && archiveCalls == 0 && libraryReads == 0,
                provider + " provider runs once without replay");
            Check(logs.SequenceEqual(new[] { "attempt" }), provider + " diagnostics delivered once");
            if (provider == "random") expectedRandom.Next(0, 100);
            Check(random.Next(0, 100) == expectedRandom.Next(0, 100), provider + " preserves seeded random state");
        }
    }

    private static void ImportedLibraryRetry()
    {
        const string source = "@startuml\n!import extras.zip\n!include <C4/C4_Context>\n!log root\nCustom(\"archive\")\n@enduml";
        int imports = 0;
        var archive = new PlantUmlResourceLibrary(new Dictionary<string, string> { ["c4/c4_context.puml"] = CustomLibrary });
        var logs = new List<string>();
        var options = new PlantUmlRenderOptions
        {
            ImportLibrary = path => { imports++; Check(path == "extras.zip", "normalized archive identifier"); return archive; },
            Log = logs.Add
        };
        Check(PlantUmlRenderer.TryRender(source, out var diagram, options) && diagram.Kind == DiagramKind.Graph &&
            XElement.Parse(diagram.Svg).Value.Contains("archive"), "imported custom C4 retries natively");
        Check(imports == 1 && logs.SequenceEqual(new[] { "library", "root" }), "immutable archive snapshot and diagnostics reused across retry");
    }

    private static void DirectC4()
    {
        const string source = "@startuml\n!include <C4/C4_Context>\n!import extras.zip\n!$caption=%getenv(\"DEMO\")\n!$n=%random(100)\n!$time=%now()\n!log direct\nPerson(user,$caption)\nPerson(literal,\"Literal\")\n@enduml";
        int randomCalls = 0, clockCalls = 0, environmentCalls = 0, archiveCalls = 0, libraryReads = 0;
        var logs = new List<string>();
        var options = new PlantUmlRenderOptions
        {
            ResolveInclude = _ => { libraryReads++; return CustomLibrary; },
            ImportLibrary = _ => { archiveCalls++; return new PlantUmlResourceLibrary([]); },
            RandomInteger = (minimum, maximum) => { randomCalls++; return minimum; },
            UtcNow = () => { clockCalls++; return DateTimeOffset.UnixEpoch; },
            ResolveEnvironment = _ => { environmentCalls++; return "Person"; },
            Log = logs.Add
        };
        Check(PlantUmlRenderer.TryRender(source, out var diagram, options) && diagram.Kind == DiagramKind.C4, "direct C4 remains native with providers");
        Check(randomCalls == 1 && clockCalls == 1 && environmentCalls == 1 && archiveCalls == 1 && libraryReads == 0 &&
            logs.SequenceEqual(new[] { "direct" }), "direct C4 providers run once");
        var svg = XElement.Parse(diagram!.Svg);
        var generated = svg.Descendants().Single(element => (string?)element.Attribute("data-c4-alias") == "user");
        var literal = svg.Descendants().Single(element => (string?)element.Attribute("data-c4-alias") == "literal");
        Check((int?)generated.Attribute("data-source-line") == 7 && (string?)generated.Attribute("data-source-generated") == "true" &&
            (int?)literal.Attribute("data-source-line") == 8 && literal.Attribute("data-source-generated") == null,
            "direct C4 preserves original editable source identity");
    }

    private static void ResourceFailures()
    {
        foreach (bool archive in new[] { false, true })
        {
            int reads = 0;
            var logs = new List<string>();
            var options = new PlantUmlRenderOptions
            {
                ResolveInclude = _ => { reads++; throw new InvalidOperationException("source unavailable"); },
                ImportLibrary = _ => { reads++; throw new InvalidOperationException("archive unavailable"); },
                Log = logs.Add
            };
            string source = "@startuml\n!log attempt\n" + (archive ? "!import broken.zip\n!include <C4/C4_Context>" : "!include broken.iuml") + "\n@enduml";
            Check(!PlantUmlRenderer.TryRender(source, out _, options) && reads == 1 && logs.SequenceEqual(new[] { "attempt" }),
                "failed resource provider is not replayed");
        }
    }

    private static void DiagnosticBudget()
    {
        int messages = 0, libraryReads = 0;
        string source = "@startuml\n!include <C4/C4_Context>\n!$message=\"" + new string('x', 32768) +
            "\"\n!$index=0\n!while $index < 40\n!log $message\n!$index=$index+1\n!endwhile\nCustom(\"More\")\n@enduml";
        var options = new PlantUmlRenderOptions
        {
            ResolveInclude = _ => { libraryReads++; return CustomLibrary; },
            Log = message => { messages++; Check(message.Length == 32768, "bounded diagnostic content"); }
        };
        Check(!PlantUmlRenderer.TryRender(source, out _, options), "streaming diagnostic overflow disables replay");
        Check(messages == 40 && libraryReads == 0, "bounded diagnostics stream exactly once");
    }

    private static void DiagnosticFailures()
    {
        foreach (bool include in new[] { false, true })
        {
            int messages = 0;
            var options = new PlantUmlRenderOptions
            {
                ResolveInclude = _ => "",
                Log = _ => { messages++; throw new InvalidOperationException("diagnostic unavailable"); }
            };
            string source = "@startuml\n" + (include ? "!include empty.iuml\n" : "") + "!log first\n!log second\nA -> B\n@enduml";
            Check(!PlantUmlRenderer.TryRender(source, out var diagram, options) && diagram == null && messages == 1,
                "throwing diagnostic consumers retain the preprocessing failure boundary");
        }

        int streamedMessages = 0;
        string overflow = "@startuml\n!include <C4/C4_Context>\n!$message=\"" + new string('x', 32768) +
            "\"\n!$index=0\n!while $index < 40\n!log $message\n!$index=$index+1\n!endwhile\nCustom(\"More\")\n@enduml";
        var streaming = new PlantUmlRenderOptions
        {
            ResolveInclude = _ => CustomLibrary,
            Log = _ => { streamedMessages++; throw new InvalidOperationException("stream unavailable"); }
        };
        Check(!PlantUmlRenderer.TryRender(overflow, out var result, streaming) && result == null && streamedMessages == 1,
            "a failed overflow flush discards its remaining diagnostic queue");
    }

    private static void PlainDiagram()
    {
        var logs = new List<string>();
        var options = new PlantUmlRenderOptions
        {
            Log = logs.Add,
            ResolveEnvironment = _ => { Check(logs.SequenceEqual(new[] { "before" }), "plain diagram keeps direct diagnostic delivery"); return "value"; }
        };
        Check(PlantUmlRenderer.TryRender("@startuml\n!log before\nA -> B : %getenv(\"DEMO\")\n@enduml", out _, options),
            "plain diagram retains existing provider path");
    }

    private static void Check(bool valid, string label)
    {
        if (!valid) throw new Exception("Renderer retry: " + label);
    }
}
