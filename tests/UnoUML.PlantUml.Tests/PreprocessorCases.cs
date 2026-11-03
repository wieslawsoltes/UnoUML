using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class PreprocessorCases
{
    internal static void Run()
    {
        const string program = """
            @startuml
            !$flag = 2
            !$name ?= 'Billing'
            !$name ?= 'Other'
            !function $double($n) !return $n * 2
            !procedure $send($msg)
            A -> B : $msg
            !endprocedure
            participant A
            participant B
            !if ($flag > 1) && %true()
            $send(%upper($name))
            !elseif $flag == 1
            A -> B : wrong branch
            !else
            A -> B : also wrong
            !endif
            !$i = 0
            !while $i < $double(2)
            A -> B : Step $i
            !$i = $i + 1
            !endwhile
            B --> A : Literal
            @enduml
            """;
        var svg = Render(program);
        var messages = svg.Descendants().Where(e => (string?)e.Attribute("class") == "message").ToList();
        Check(messages.Count == 6 && messages[0].Value.Contains("BILLING") && messages[^1].Value.Contains("Literal"), "procedures, branches and loops");
        Check((int?)messages[0].Attribute("data-source-line") == 11 && (string?)messages[0].Attribute("data-source-generated") == "true", "macro invocation source");
        Check((int?)messages[^1].Attribute("data-source-line") == 22 && messages[^1].Attribute("data-source-generated") == null,"literal remains editable");
        var entries = Render("""
            @startuml services.svg
            !$config = {"services":[{"id":"Billing"},{"id":"Orders"}]}
            !foreach $entry in $config.services
            class $entry.id
            !endfor
            @enduml
            """);
        Check(entries.Value.Contains("Billing") && entries.Value.Contains("Orders"),"JSON member lookup and foreach");
        var loops = Render("@startuml\n!$item = 0\n!foreach $item in [\"first\",\"last\"]\nA -> B : $item\n!endfor\nA -> B : retained $item / %variable_exists(\"$item\")\n!$sizeObject={\"a\":1}\nA -> B : sizes %size(42) / %size(\"42\") / %size($sizeObject)\n@enduml");
        Check(loops.Value.Contains("retained last / 1") && loops.Value.Contains("sizes 0 / 2 / 1"), "reference-compatible foreach binding and size types");
        const string includes = "@startuml\n!include parts/model.iuml\nclass Checkout\nInvoice --> Checkout\n@enduml";
        var files = new Dictionary<string,string> {
            ["parts/model.iuml"] = "!include ./shared.iuml\nclass Invoice",
            ["parts/shared.iuml"] = "class Receipt"
        };
        var imported = Render(includes,path=>files.GetValueOrDefault(path));
        var receipt = imported.Descendants().First(e=>(string?)e.Attribute("data-qualified-name")=="Receipt");
        Check((int?)receipt.Attribute("data-source-line")==1 && (string?)receipt.Attribute("data-source-file")=="parts/shared.iuml" &&
            (int?)receipt.Attribute("data-file-line")==0, "nested virtual include origin");
        foreach (var body in new[] {"!include missing.iuml", "!include ../outside", "!include https://example.org/x", "!include cycle", "!if 1\nclass A",
            "!$x = 1/0", "!while 1\nclass A\n!endwhile", "!procedure $again()\n$again()\n!endprocedure\n$again()",
            "!$x = %size({\"a\":1})", "!$x = %unknown(1)", "!function $bad(\n!endfunction"})
            Check(!PlantUmlRenderer.TryRender("@startuml\n"+body+"\n@enduml", key => key == "cycle" ? "!include cycle" : null, out _),"fallback for "+body);
        var truth = Render("@startuml\n!if \"\"\nA -> B : wrong\n!else\nA -> B : empty false\n!endif\nA -> B : %not(\"\") / %not(\"0\")\n@enduml");
        Check(truth.Value.Contains("empty false") && truth.Value.Contains("1 / 0") && !truth.Value.Contains("wrong"), "empty-string TIM truth");
        var builtin = Render("@startuml\nA -> B : %upper(\"hello\")\n@enduml");
        Check(builtin.Value.Contains("HELLO") && !builtin.Value.Contains("%upper"), "builtins without preprocessor directives");
        var optional = Render("@startuml\n!$a=\"global\"\n!procedure $p($x)\n!local $a ?= \"local\"\n!global $x ?= \"newglobal\"\nA -> B : $a / $x\n!endprocedure\n$p(\"argument\")\nA -> B : %variable_exists(\"$x\")\n@enduml");
        Check(optional.Value.Contains("global / argument") && optional.Value.Contains("0"), "optional assignment preserves visible local and global bindings");
        var multiline = Render("@startuml\n!$a = {\"text\":\"class A\\nclass B\"}\n$a.text\nclass C\n@enduml");
        var generated = multiline.Descendants().Where(e => (string?)e.Attribute("data-qualified-name") is "A" or "B").ToArray();
        Check(generated.Length == 2 && generated.All(e => (int?)e.Attribute("data-source-line") == 2 && (string?)e.Attribute("data-source-generated") == "true"), "multiline JSON expansion source maps");
        Check((int?)multiline.Descendants().Single(e => (string?)e.Attribute("data-qualified-name") == "C").Attribute("data-source-line") == 3, "literal after multiline expansion source map");
        Check(!PlantUmlRenderer.TryRender("@startuml\n!global %bad = 1\nclass A\n@enduml", out _), "invalid assignment name");
        var literalC4 = Render("@startuml\n!include <C4/C4_Context>\n!$name=\"Reviewer\"\nPerson(user,$name)\n@enduml");
        Check(literalC4.Value.Contains("Reviewer"), "variables with pinned C4 library");
        var spacedInclude = Render("@startuml\n!include    <C4/C4_Context>\nPerson(user,\"User\")\n@enduml");
        Check(spacedInclude.Value.Contains("User"), "standard include whitespace normalization");
        var boundary = Render("@startuml\n!include <C4/C4_Context>\n!$name=\"Alice, Bob\"\nSystem_Boundary(group,$name) {\nPerson(user,\"User\")\n}\n@enduml");
        Check(boundary.Value.Contains("Alice, Bob"), "typed library arguments on opening boundaries");
        var boundaryGroup = boundary.Descendants().Single(element => (string?)element.Attribute("data-c4-alias") == "group");
        Check((int?)boundaryGroup.Attribute("data-source-line") == 3 && (int?)boundaryGroup.Attribute("data-source-end-line") == 5,
            "C4 boundary original line range after preprocessing");
        Check(!PlantUmlRenderer.TryRender("""
            @startuml
            !include <C4/C4_Context>
            !$config={"label":"\"Wrong\",\"\",\"\""}
            System_Boundary(group,$config.label) {
            Person(user,"User")
            }
            @enduml
            """, out _), "boundary arguments retain typed value boundaries");
        RendererRetryCases.Run();
    }

    private static XElement Render(string source, Func<string,string?>? resolver = null)
    {
        if (!PlantUmlRenderer.TryRender(source,resolver,out var result)) throw new Exception("Managed preprocessing rejected: " + source);
        return XElement.Parse(result.Svg);
    }
    private static void Check(bool valid,string label) { if (!valid) throw new Exception("Preprocessor: "+label); }
}
