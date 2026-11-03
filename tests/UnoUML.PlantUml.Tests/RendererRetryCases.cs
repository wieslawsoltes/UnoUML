using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class RendererRetryCases
{
    private const string CustomLibrary = "!log library\n!procedure Custom($text)\nrectangle \"$text\" as A\n!endprocedure";

    internal static void Run()
    {
        CustomLibraryExecution();
        ProviderExecution();
        ProviderOrdering();
        CustomStockProcedure();
        ImportedLibraryExecution();
        ResourceFailures();
        DirectC4();
        NamedDirectCalls();
        LibraryConfiguration();
        PureStockDependencies();
        LargeStockLibrary();
        DiagnosticBudget();
        DiagnosticFailures();
        PlainDiagram();
    }

    private static void CustomLibraryExecution()
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
                XElement.Parse(diagram.Svg).Value.Contains("snapshot" + invocation), "custom C4 single execution");
            Check(reads.Count == 3 && reads.Values.All(count => count == invocation), "source providers are called once per render");
            Check(logs.SequenceEqual(new[] { "library", "root snapshot" + invocation + " 0" }), "library and root diagnostics retain execution order");
        }
    }

    private static void ProviderExecution()
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
            Check(PlantUmlRenderer.TryRender(source, out var diagram, options) && diagram.Kind == DiagramKind.Graph, provider + " custom library renders natively");
            Check(randomCalls == (provider == "random" ? 1 : 0) && clockCalls == (provider == "clock" ? 1 : 0) &&
                environmentCalls == (provider == "environment" ? 1 : 0) && archiveCalls == 0 && libraryReads == 1,
                provider + " provider runs once without replay");
            Check(logs.SequenceEqual(new[] { "library", "attempt" }), provider + " diagnostics delivered once");
            if (provider == "random") expectedRandom.Next(0, 100);
            Check(random.Next(0, 100) == expectedRandom.Next(0, 100), provider + " preserves seeded random state");
        }
    }

    private static void ImportedLibraryExecution()
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
            XElement.Parse(diagram.Svg).Value.Contains("archive"), "imported custom C4 executes natively");
        Check(imports == 1 && logs.SequenceEqual(new[] { "library", "root" }), "archive import and diagnostics execute once");
    }

    private static void ProviderOrdering()
    {
        const string library = """
            !log library
            !$libraryEnvironment=%getenv("VALUE")
            !procedure Custom($text,$declared=%random(100))
            !log procedure
            rectangle "$text / $declared / $libraryEnvironment" as A
            !endprocedure
            """;
        const string source = """
            @startuml
            !log root
            !include <C4/C4_Context>
            !function next()
            !log next
            !return %random(100)
            !endfunction
            Custom(%string(next()) + " / " + %string(next()) + " / " + %now() + " / " + %now() + " / " + %getenv("VALUE") + " / " + %getenv("VALUE"))
            @enduml
            """;
        var events = new List<string>();
        var random = new Random(1729);
        var expected = new Random(1729);
        int declared = expected.Next(100), first = expected.Next(100), second = expected.Next(100);
        int clocks = 0, environments = 0;
        var options = new PlantUmlRenderOptions
        {
            ResolveInclude = path => { events.Add("include " + path); return library; },
            RandomInteger = (minimum, maximum) => { events.Add("random"); return random.Next(minimum, maximum); },
            UtcNow = () => { events.Add("clock"); return DateTimeOffset.FromUnixTimeSeconds(++clocks); },
            ResolveEnvironment = _ => { events.Add("environment"); return "env" + ++environments; },
            Log = message => events.Add("log " + message)
        };
        Check(PlantUmlRenderer.TryRender(source, out var diagram, options) && diagram.Kind == DiagramKind.Graph,
            "stateful library expressions render natively");
        Check(events.SequenceEqual(new[] { "log root", "include <c4/c4_context>", "log library", "environment", "random", "log next", "random",
            "log next", "random", "clock", "clock", "environment", "environment", "log procedure" }), "provider calls retain TIM execution order");
        Check(XElement.Parse(diagram!.Svg).Value.Contains($"{first} / {second} / 1 / 2 / env2 / env3 / {declared} / env1"),
            "identical provider signatures retain separate values");
        Check(random.Next(100) == expected.Next(100), "single execution consumes exactly three seeded draws");
    }

    private static void CustomStockProcedure()
    {
        const string library = "!unquoted procedure Person($alias,$label)\n!log custom $alias\nrectangle \"Custom $label\" as $alias\n!endprocedure";
        var messages = new List<string>();
        Check(PlantUmlRenderer.TryRender("@startuml\n!include <C4/C4_Context>\nPerson(user,\"User\")\n@enduml", out var diagram,
            new() { ResolveInclude = _ => library, Log = messages.Add }) && diagram.Kind == DiagramKind.Graph &&
            XElement.Parse(diagram.Svg).Value.Contains("Custom User") && messages.SequenceEqual(new[] { "custom user" }),
            "host-provided definitions replace stock procedure semantics");
    }

    private static void NamedDirectCalls()
    {
        const string source = "@startuml\r\n!include <C4/C4_Context>\r\nPerson(\\\r\n $label=\"Customer\",\\\r\n $alias=user)\r\nSystem(app,\"Portal\")\r\nRel($label=\"Visits\",$to=app,$from=user)\r\n@enduml";
        Check(PlantUmlRenderer.TryRender(source, out var diagram) && diagram.Kind == DiagramKind.C4, "continued named C4 calls use direct layout");
        var nodes = XElement.Parse(diagram!.Svg).Descendants();
        var person = nodes.Single(element => (string?)element.Attribute("data-c4-alias") == "user");
        var system = nodes.Single(element => (string?)element.Attribute("data-c4-alias") == "app");
        Check((int?)person.Attribute("data-source-line") == 2 && person.Attribute("data-source-generated") == null && (int?)system.Attribute("data-source-line") == 5 &&
            system.Attribute("data-source-generated") == null, "continued C4 calls preserve following literal source identity");
        Check(nodes.Any(element => (string?)element.Attribute("class") == "link" && (int?)element.Attribute("data-source-line") == 6 &&
            element.Attribute("data-source-generated") == null), "named relationship literals remain editable");
    }

    private static void LibraryConfiguration()
    {
        const string source = "@startuml\n!$PERSON_BG_COLOR=\"#FF0000\"\n!include <C4/C4_Context>\nPerson(user,\"User\")\n@enduml";
        Check(PlantUmlRenderer.TryRender(source, out var diagram) && diagram.Kind == DiagramKind.Graph &&
            XElement.Parse(diagram.Svg).Descendants().Any(element => (string?)element.Attribute("fill") == "#FF0000"),
            "library configuration is honored by expanded layout");
        Check(PlantUmlPreprocessor.TryProcess("@startuml\n!include <C4/C4_Context>\nA -> B : C4Version()\n@enduml", out var expanded) &&
            expanded.Source.Contains("A -> B : 2.14.0"), "embedded native library routines are available to the public preprocessor");
    }

    private static void DirectC4()
    {
        const string source = "@startuml\n!include <C4/C4_Context>\n!import extras.zip\n!$caption=%getenv(\"DEMO\")\n!$n=%random(100)\n!$time=%now()\n!log direct\nPerson(user,$caption)\nPerson(literal,\"Literal\")\n@enduml";
        int randomCalls = 0, clockCalls = 0, environmentCalls = 0, archiveCalls = 0, libraryReads = 0;
        var logs = new List<string>();
        var options = new PlantUmlRenderOptions
        {
            ResolveInclude = _ => { libraryReads++; return null; },
            ImportLibrary = _ => { archiveCalls++; return new PlantUmlResourceLibrary([]); },
            RandomInteger = (minimum, maximum) => { randomCalls++; return minimum; },
            UtcNow = () => { clockCalls++; return DateTimeOffset.UnixEpoch; },
            ResolveEnvironment = _ => { environmentCalls++; return "Person"; },
            Log = logs.Add
        };
        Check(PlantUmlRenderer.TryRender(source, out var diagram, options) && diagram.Kind == DiagramKind.C4, "direct C4 remains native with providers");
        Check(randomCalls == 1 && clockCalls == 1 && environmentCalls == 1 && archiveCalls == 1 && libraryReads == 2 &&
            logs.SequenceEqual(new[] { "direct" }), "direct C4 providers run once");
        var svg = XElement.Parse(diagram!.Svg);
        var generated = svg.Descendants().Single(element => (string?)element.Attribute("data-c4-alias") == "user");
        var literal = svg.Descendants().Single(element => (string?)element.Attribute("data-c4-alias") == "literal");
        Check((int?)generated.Attribute("data-source-line") == 7 && (string?)generated.Attribute("data-source-generated") == "true" &&
            (int?)literal.Attribute("data-source-line") == 8 && literal.Attribute("data-source-generated") == null,
            "direct C4 preserves original editable source identity");
    }

    private static void LargeStockLibrary()
    {
        string source = "@startuml\n!include <C4/C4_Context>\n" +
            string.Join('\n', Enumerable.Range(0, 1000).Select(index => $"System(n{index},\"Node {index}\")")) + "\n@enduml";
        Check(PlantUmlRenderer.TryRender(source, out var diagram) && diagram.Kind == DiagramKind.C4 &&
            XElement.Parse(diagram.Svg).Descendants().Count(element => element.Attribute("data-c4-alias") != null) == 1000,
            "trusted library execution retains the direct renderer's thousand-node range");
        Check(PlantUmlPreprocessor.TryProcess(source, out var expanded) && expanded.Source.Contains("Node 999", StringComparison.Ordinal),
            "full trusted library expansion supports a thousand-node source");
        Check(!PlantUmlPreprocessor.TryProcess("@startuml\n!include <C4/C4_Context>\n!while 1\n!$n=1\n!endwhile\n@enduml", out _),
            "trusted library availability retains the bounded user-code budget");
    }

    private static void PureStockDependencies()
    {
        foreach (var (module, statement, dependencies) in new[]
        {
            ("C4/C4_Context", "Person(a,\"A\")", new[] { "<c4/c4_context>", "<c4/c4>" }),
            ("C4/C4_Container", "Container(a,\"A\")", new[] { "<c4/c4_container>", "<c4/c4_context>", "<c4/c4>" }),
            ("C4/C4_Component", "Component(a,\"A\")", new[] { "<c4/c4_component>", "<c4/c4_container>", "<c4/c4_context>", "<c4/c4>" }),
            ("C4/C4_Deployment", "Deployment_Node(a,\"A\")", new[] { "<c4/c4_deployment>", "<c4/c4_container>", "<c4/c4_context>", "<c4/c4>" }),
            ("archimate/Archimate", "Business_Actor(a,\"A\")", new[] { "<archimate/archimate>", "<archimate/themes/shared_style>" })
        })
        {
            var reads = new List<string>();
            var logs = new List<string>();
            var options = new PlantUmlRenderOptions
            {
                ResolveInclude = path => { reads.Add(path); return null; },
                Log = logs.Add,
                UtcNow = () => throw new InvalidOperationException("Pure stock code read the clock."),
                RandomInteger = (_, _) => throw new InvalidOperationException("Pure stock code consumed randomness."),
                ResolveEnvironment = _ => throw new InvalidOperationException("Pure stock code read the environment.")
            };
            string literal = "@startuml\n!include <" + module + ">\n" + statement + "\n@enduml";
            Check(PlantUmlRenderer.TryRender(literal, out var diagram, options) &&
                diagram.Kind is DiagramKind.C4 or DiagramKind.ArchiMate && reads.SequenceEqual(dependencies) && logs.Count == 0,
                "literal specialization validates the ordered pinned dependencies for " + module);
            reads.Clear();
            Check(PlantUmlPreprocessor.TryProcess(literal, out var expanded, options) && expanded.Options.Count == 0 &&
                reads.SequenceEqual(dependencies) && logs.Count == 0,
                "the full pinned library contract has the same observable effects for " + module);
        }

        const string source = "@startuml\n!include <C4/C4_Context>\nPerson(a,\"A\")\n@enduml";
        var events = new List<string>();
        var custom = new PlantUmlRenderOptions
        {
            ResolveInclude = path =>
            {
                events.Add("include " + path);
                return path == "<c4/c4>" ? "!log custom dependency\n!assert 0 : stop" : null;
            },
            Log = text => events.Add("log " + text)
        };
        Check(!PlantUmlRenderer.TryRender(source, out _, custom) && events.SequenceEqual(new[]
            { "include <c4/c4_context>", "include <c4/c4>", "log custom dependency" }),
            "overridden nested dependencies enter TIM without repeating provider reads");
        int failures = 0;
        Check(!PlantUmlRenderer.TryRender(source, out _, new() { ResolveInclude = _ => { failures++; throw new InvalidOperationException("unavailable"); } }) &&
            failures == 1, "pure-library provider failures retain the ordinary failure boundary");
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
            ResolveInclude = _ => { libraryReads++; return CustomLibrary.Replace("!log library\n", ""); },
            Log = message => { messages++; Check(message.Length == 32768, "bounded diagnostic content"); }
        };
        Check(PlantUmlRenderer.TryRender(source, out _, options), "streaming diagnostics preserve native library execution");
        Check(messages == 40 && libraryReads == 1, "bounded diagnostics stream exactly once");
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
            "a diagnostic failure stops execution immediately");
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
        if (!valid) throw new Exception("Library execution: " + label);
    }
}
