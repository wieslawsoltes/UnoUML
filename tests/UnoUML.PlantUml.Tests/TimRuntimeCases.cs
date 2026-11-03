using System.Text.Json;
using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class TimRuntimeCases
{
    // Expected expansions were compared with the PlantUML 1.2026.9 preprocessor.
    private static readonly (string Name, string Source, string Expected)[] Cases =
    [
        ("scalar_member_number",
            """
            @startuml
            !$v=1
            !$y=$v.0
            A -> B: $y / %get_json_type($y)
            @enduml
            """,
            """
            @startuml
            A -> B: 1.0 / number
            @enduml
            """),
        ("scalar_index_variable",
            """
            @startuml
            !$v="word"
            !$i=2
            !$y=$v[$i]
            A -> B: $y
            @enduml
            """,
            """
            @startuml
            A -> B: word[2]
            @enduml
            """),
        ("scalar_unknown_suffix",
            """
            @startuml
            !$y=$unknown.member[0]
            A -> B: $y
            @enduml
            """,
            """
            @startuml
            A -> B: $unknown.member[0]
            @enduml
            """),
        ("scalar_suffix_snapshot",
            """
            @startuml
            !$v="word"
            !$i=1
            !function next()
            !global $v="changed"
            !global $i=2
            !return "prefix "
            !endfunction
            !$y=next()+$v[$i]
            A -> B: $y / $v / $i
            @enduml
            """,
            """
            @startuml
            A -> B: prefix word[1] / changed / 2
            @enduml
            """),
        ("json_bare_key",
            """
            @startuml
            !$v={"x":1}
            A -> B: $v[x]
            @enduml
            """,
            """
            @startuml
            A -> B: 1
            @enduml
            """),
        ("scalar_dot_expr",
            """
            @startuml
            !$v="word"
            !$y=$v.member
            A -> B: $y
            @enduml
            """,
            """
            @startuml
            A -> B: word.member
            @enduml
            """),
        ("scalar_index_expr",
            """
            @startuml
            !$v="word"
            !$y=$v[0]
            A -> B: $y
            @enduml
            """,
            """
            @startuml
            A -> B: word[0]
            @enduml
            """),
        ("legacy_directive",
            """
            @startuml
            !define SET !$x=1
            SET
            A -> B: $x
            @enduml
            """,
            """
            @startuml
            !$x=1
            A -> B: $x
            @enduml
            """),
        ("adjacent_group",
            """
            @startuml
            !$x=1+(2 word)
            A -> B: $x
            @enduml
            """,
            """
            @startuml
            A -> B: 2word
            @enduml
            """),
        ("adjacent_group_twice",
            """
            @startuml
            !$x=(1 2)+(3 4)
            A -> B: $x
            @enduml
            """,
            """
            @startuml
            A -> B: 7
            @enduml
            """),
        ("basic",
            """
            @startuml
            !$x=2
            A -> B : $x / $unknown
            @enduml
            """,
            """
            @startuml
            A -> B : 2 / $unknown
            @enduml
            """),
        ("bare_variables",
            """
            @startuml
            !answer=7
            A -> B : answer / unanswered / preanswer / answerSuffix
            @enduml
            """,
            """
            @startuml
            A -> B : 7 / unanswered / preanswer / answerSuffix
            @enduml
            """),
        ("defaults",
            """
            @startuml
            !function add(a,b=2) !return a+b
            A -> B : add(3) / add(3,4)
            @enduml
            """,
            """
            @startuml
            A -> B : 5 / 7
            @enduml
            """),
        ("default_capture",
            """
            @startuml
            !$base=2
            !function f(a=$base) !return a
            !$base=3
            A -> B : f()
            @enduml
            """,
            """
            @startuml
            A -> B : 2
            @enduml
            """),
        ("named",
            """
            @startuml
            !function f(a=1,b=2,c=3) !return a*100+b*10+c
            A -> B : f(c=9,a=7) / f(b=4,2,8) / f(a=3,a=4)
            @enduml
            """,
            """
            @startuml
            A -> B : 729 / 248 / 423
            @enduml
            """),
        ("overload",
            """
            @startuml
            !function f(a) !return a
            !function f(a,b) !return a+b
            A -> B : f(3) / f(3,4)
            @enduml
            """,
            """
            @startuml
            A -> B : 3 / 7
            @enduml
            """),
        ("redefinition",
            """
            @startuml
            !function f(a) !return a
            !function f(a) !return a+1
            A -> B : f(3)
            @enduml
            """,
            """
            @startuml
            A -> B : 4
            @enduml
            """),
        ("unquoted",
            """
            @startuml
            !unquoted function f(a,b="Default") !return a+":"+b
            A -> B : f(One, Two Words) / f(Three) / f("Four", b=Five)
            @enduml
            """,
            """
            @startuml
            A -> B : One:Two Words / Three:Default / Four:Five
            @enduml
            """),
        ("unquoted_variables",
            """
            @startuml
            !$x="Expand"
            !unquoted function f(a) !return a
            A -> B : f($x) / f(%upper($x)) / f(1+2)
            @enduml
            """,
            """
            @startuml
            A -> B : Expand / EXPAND / 1+2
            @enduml
            """),
        ("unquoted_parentheses",
            """
            @startuml
            !unquoted function f(a,b="B") !return a+":"+b
            A -> B : f(Map(Key), Value)
            @enduml
            """,
            """
            @startuml
            A -> B : Map(Key):Value
            @enduml
            """),
        ("multistatement",
            """
            @startuml
            !function f(a)
            !$sum=0
            !while a>0
            !$sum=$sum+a
            !a=a-1
            !endwhile
            !return $sum
            !endfunction
            A -> B : f(4)
            @enduml
            """,
            """
            @startuml
            A -> B : 10
            @enduml
            """),
        ("early_return",
            """
            @startuml
            !function f(a)
            !if a==1
            !return "one"
            !elseif a==2
            !return "two"
            !else
            !return "many"
            !endif
            !return "unreachable"
            !endfunction
            A -> B : f(1) / f(2) / f(3)
            @enduml
            """,
            """
            @startuml
            A -> B : one / two / many
            @enduml
            """),
        ("recursive",
            """
            @startuml
            !function fac(n)
            !if n<=1
            !return 1
            !endif
            !return n*fac(n-1)
            !endfunction
            A -> B : fac(6)
            @enduml
            """,
            """
            @startuml
            A -> B : 720
            @enduml
            """),
        ("global_updates",
            """
            @startuml
            !$count=1
            !procedure p()
            !$count=$count+1
            !$temp=10
            !endprocedure
            p()
            p()
            A -> B : $count / $temp
            @enduml
            """,
            """
            @startuml
            A -> B : 3 / $temp
            @enduml
            """),
        ("local_shadow",
            """
            @startuml
            !$x=1
            !procedure p()
            !local $x=2
            A -> B : $x
            !global $x=3
            A -> B : $x
            !endprocedure
            p()
            A -> B : $x
            @enduml
            """,
            """
            @startuml
            A -> B : 2
            A -> B : 2
            A -> B : 3
            @enduml
            """),
        ("global_vs_local",
            """
            @startuml
            !procedure p()
            !$x=1
            !global $x=2
            A -> B : $x
            !endprocedure
            p()
            A -> B : $x
            @enduml
            """,
            """
            @startuml
            A -> B : 2
            A -> B : 2
            @enduml
            """),
        ("optional",
            """
            @startuml
            !$x=1
            !procedure p($x)
            !global $x ?= 9
            !local $x ?= 8
            A -> B : $x
            !endprocedure
            p(2)
            A -> B : $x
            @enduml
            """,
            """
            @startuml
            A -> B : 2
            A -> B : 1
            @enduml
            """),
        ("undef",
            """
            @startuml
            !define FLAG
            !ifdef FLAG
            A -> B : before
            !endif
            !undef FLAG
            !ifndef FLAG
            A -> B : after
            !endif
            @enduml
            """,
            """
            @startuml
            A -> B : before
            A -> B : after
            @enduml
            """),
        ("ifdef_boolean",
            """
            @startuml
            !define X
            !ifdef X && (!Y || Z)
            A -> B : present
            !else
            A -> B : absent
            !endif
            @enduml
            """,
            """
            @startuml
            A -> B : present
            @enduml
            """),
        ("ifdef_function",
            """
            @startuml
            !function f() !return 1
            !ifdef f
            A -> B : function
            !endif
            !ifndef missing
            A -> B : missing
            !endif
            @enduml
            """,
            """
            @startuml
            A -> B : function
            A -> B : missing
            @enduml
            """),
        ("nested_conditional",
            """
            @startuml
            !if 1
            !ifdef missing
            A -> B : no
            !else
            A -> B : yes
            !endif
            !endif
            @enduml
            """,
            """
            @startuml
            A -> B : yes
            @enduml
            """),
        ("legacy_constant",
            """
            @startuml
            !define NAME Alice
            A -> B : NAME
            !define NAME Bob
            A -> B : NAME
            @enduml
            """,
            """
            @startuml
            A -> B : Alice
            A -> B : Bob
            @enduml
            """),
        ("legacy_function",
            """
            @startuml
            !define PAIR(a,b=Default) a / b
            A -> B : PAIR(One, Two) / PAIR(Three)
            @enduml
            """,
            """
            @startuml
            A -> B : One / Two / Three / Default
            @enduml
            """),
        ("legacy_long",
            """
            @startuml
            !definelong MSG(a)
            A -> B : a
            B -> A : a
            !enddefinelong
            MSG(Hello)
            @enduml
            """,
            """
            @startuml
            A -> B : Hello
            B -> A : Hello
            @enduml
            """),
        ("legacy_zero",
            """
            @startuml
            !definelong MSG
            A -> B : hello
            !enddefinelong
            MSG()
            @enduml
            """,
            """
            @startuml
            A -> B : hello
            @enduml
            """),
        ("concatenation",
            """
            @startuml
            !$x="Name"
            A -> B : prefix##$x / $x##Suffix / $x##$x
            @enduml
            """,
            """
            @startuml
            A -> B : prefixName / NameSuffix / NameName
            @enduml
            """),
        ("procedure_prefix",
            """
            @startuml
            !procedure p()
            hello
            world
            !endprocedure
            A -> B : p() suffix
            @enduml
            """,
            """
            @startuml
            A -> B : hello
            world suffix
            @enduml
            """),
        ("json_multiline",
            """
            @startuml
            !$x={
            "name":"Alice",
            "parts":[1,2]
            }
            A -> B : $x.name / $x.parts[1] / $x
            @enduml
            """,
            """
            @startuml
            A -> B : Alice / 2 / {"name":"Alice","parts":[1,2]}
            @enduml
            """),
        ("json_types",
            """
            @startuml
            !$x={"n":2,"s":"42","b":true,"z":null}
            A -> B : %get_json_type($x.n) / %get_json_type($x.s) / %get_json_type($x.b) / %get_json_type($x.z)
            A -> B : %string($x.n+1)
            @enduml
            """,
            """
            @startuml
            A -> B : number / number / boolean / json
            A -> B : 21
            @enduml
            """),
        ("json_dynamic",
            """
            @startuml
            !$x={"parts":[{"name":"Alice"},{"name":"Bob"}]}
            !$index=1
            !$key="name"
            A -> B : $x.parts[$index][$key]
            @enduml
            """,
            """
            @startuml
            A -> B : Bob
            @enduml
            """),
        ("foreach_array",
            """
            @startuml
            !foreach $v in ["A","B"]
            A -> B : $v
            !endfor
            A -> B : $v
            @enduml
            """,
            """
            @startuml
            A -> B : A
            A -> B : B
            A -> B : B
            @enduml
            """),
        ("foreach_object",
            """
            @startuml
            !foreach $v in {"One":1,"Two":2}
            A -> B : $v
            !endfor
            @enduml
            """,
            """
            @startuml
            A -> B : One
            A -> B : Two
            @enduml
            """),
        ("foreach_scope",
            """
            @startuml
            !procedure p()
            !foreach $v in [1,2]
            !endfor
            !endprocedure
            p()
            A -> B : $v
            @enduml
            """,
            """
            @startuml
            A -> B : 2
            @enduml
            """),
        ("foreach_scalar",
            """
            @startuml
            !foreach $v in "foo"
            A -> B : ignored
            !endfor
            A -> B : done
            @enduml
            """,
            """
            @startuml
            A -> B : done
            @enduml
            """),
        ("comments",
            """
            @startuml
            /' start
            removed
            '/
            !$x=2 /' inline '/
            A -> B : $x
            ' full comment
            @enduml
            """,
            """
            @startuml
            A -> B : 2
            @enduml
            """),
        ("continuation",
            """
            @startuml
            !$x=1 + \
            2
            A -> B : $x
            @enduml
            """,
            """
            @startuml
            A -> B : 3
            @enduml
            """),
        ("string_arithmetic",
            """
            @startuml
            A -> B : %string("a"-"b") / %string("a"*2) / %string(2/"b")
            @enduml
            """,
            """
            @startuml
            A -> B : ab / a*2 / 2/b
            @enduml
            """),
        ("mixed_comparison",
            """
            @startuml
            A -> B : %string(2=="2") / %string(2<"3") / %string("b">"a")
            @enduml
            """,
            """
            @startuml
            A -> B : 1 / 1 / 1
            @enduml
            """),
        ("overflow",
            """
            @startuml
            !$x=2147483647+1
            !$y=-2147483648/-1
            A -> B : $x / $y
            @enduml
            """,
            """
            @startuml
            A -> B : -2147483648 / -2147483648
            @enduml
            """),
        ("function_plain",
            """
            @startuml
            !function f()
            A -> B : suppressed
            !return 4
            !endfunction
            A -> B : f()
            @enduml
            """,
            """
            @startuml
            A -> B : 4
            @enduml
            """),
        ("assert",
            """
            @startuml
            !assert 2+2==4 : "math"
            A -> B : yes
            @enduml
            """,
            """
            @startuml
            A -> B : yes
            @enduml
            """),
        ("subparts",
            """
            @startuml
            !startsub one
            A -> B : first
            !endsub
            !includesub one
            @enduml
            """,
            """
            @startuml
            A -> B : first
            A -> B : first
            @enduml
            """),
        ("dynamic_function",
            """
            @startuml
            !function f(a) !return a+1
            A -> B : %call_user_func("f",3)
            @enduml
            """,
            """
            @startuml
            A -> B : 4
            @enduml
            """),
        ("dynamic_procedure",
            """
            @startuml
            !procedure p(a)
            A -> B : a
            !endprocedure
            %invoke_procedure("p","Hi")
            @enduml
            """,
            """
            @startuml
            A -> B : Hi
            @enduml
            """),
        ("spaced_routine_endings",
            """
            @startuml
            !function f(a)
            !return a+1
            !end function
            !procedure p(a)
            A -> B : f(a)
            !end procedure
            p(3)
            @enduml
            """,
            """
            @startuml
            A -> B : 4
            @enduml
            """),
        ("quoted_comment_markers",
            """
            @startuml
            !$one="text /' remains"
            !$two='//'
            A -> B : $one $two
            /' outside comment '/
            A -> B : after
            @enduml
            """,
            """
            @startuml
            A -> B : text /' remains //
            A -> B : after
            @enduml
            """),
        ("legacy_decimal_operands",
            """
            @startuml
            !$scale=0.5
            !$text=foo.bar
            !$last=first second
            !function f(a=0.5) !return a
            A -> B : $scale / $text / $last / f() / %string(0.5+2) / %string(1+2 trailing)
            @enduml
            """,
            """
            @startuml
            A -> B : .5 / foo.bar / second / .5 / .52 / 2trailing
            @enduml
            """),
        ("legacy_dollar_prefix",
            """
            @startuml
            !unquoted procedure sprite(e_sprite)
            A -> B : <$e_sprite>
            !endprocedure
            sprite(Tiger)
            @enduml
            """,
            """
            @startuml
            A -> B : <$Tiger>
            @enduml
            """),
        ("function_plain_sideeffects",
            """
            @startuml
            !$count=0
            !function setCount()
            %set_variable_value("$count",2)
            !return 1
            !endfunction
            A -> B : setCount() / $count
            @enduml
            """,
            """
            @startuml
            A -> B : 1 / 2
            @enduml
            """),
        ("procedure_in_function",
            """
            @startuml
            !procedure p()
            C -> D : emitted
            !endprocedure
            !function f()
            p()
            !return 1
            !endfunction
            A -> B : f()
            @enduml
            """,
            """
            @startuml
            C -> D : emitted
            A -> B : 1
            @enduml
            """),
        ("mixed_routine_overloads",
            """
            @startuml
            !$count=0
            !function next()
            !$count=$count+1
            !return $count
            !endfunction
            !function f(a) !return a
            !procedure f(a,b)
            A -> B : a b
            !endprocedure
            A -> B : f(next()) / $count
            f(next(), next())
            A -> B : $count
            @enduml
            """,
            """
            @startuml
            A -> B : 1 / 1
            A -> B : 2 3
            A -> B : 3
            @enduml
            """),
        ("procedure_suffix_literals",
            """
            @startuml
            !$name="expanded"
            !procedure p()
            first
            last
            !endprocedure
            A -> B : $name p() $name
            @enduml
            """,
            """
            @startuml
            A -> B : expanded first
            last $name
            @enduml
            """),
        ("expression_spaced_calls",
            """
            @startuml
            !function f(a) !return a+1
            !$x=f (3)
            A -> B : $x
            @enduml
            """,
            """
            @startuml
            A -> B : 4
            @enduml
            """),
        ("unquoted_expression",
            """
            @startuml
            !unquoted function f(a) !return a
            !$x=f(1+2)
            A -> B : $x / f(1+2) / %string(f(1+2))
            @enduml
            """,
            """
            @startuml
            A -> B : 3 / 1+2 / 3
            @enduml
            """),
        ("unquoted_quoted_variable",
            """
            @startuml
            !$x="expanded"
            !unquoted function f(a) !return a
            !$y=f("$x")
            A -> B : $y / f("$x")
            @enduml
            """,
            """
            @startuml
            A -> B : $x / expanded
            @enduml
            """),
        ("tab_return",
            """
            @startuml
            !function f()
            !return	3
            !endfunction
            A -> B : f()
            @enduml
            """,
            """
            @startuml
            A -> B : 3
            @enduml
            """),
        ("expression_variable_binding",
            """
            @startuml
            !$count=0
            !function next()
            !$count=$count+1
            !return $count
            !endfunction
            !$x=next()+$count
            A -> B : $x / $count
            @enduml
            """,
            """
            @startuml
            A -> B : 1 / 1
            @enduml
            """),
        ("argument_variable_binding",
            """
            @startuml
            !$count=0
            !function next()
            !$count=$count+1
            !return $count
            !endfunction
            !function f(a,b) !return a+":"+b
            A -> B : f(next(),$count)
            !$x=f(next(),$count)
            A -> B : $x / $count
            @enduml
            """,
            """
            @startuml
            A -> B : 1:1
            A -> B : 2:1 / 2
            @enduml
            """),
        ("local_subparts",
            """
            @startuml
            !startsub a
            A -> B : one
            !endsub
            !startsub a
            A -> B : two
            !endsub
            !includesub a
            @enduml
            """,
            """
            @startuml
            A -> B : one
            A -> B : two
            A -> B : two
            @enduml
            """),
        ("undef_function",
            """
            @startuml
            !function f() !return 1
            !undef f
            A -> B : %function_exists("f") / f()
            @enduml
            """,
            """
            @startuml
            A -> B : 1 / 1
            @enduml
            """),
    ];

    internal static void Run()
    {
        foreach (var test in Cases)
        {
            if (!PlantUmlPreprocessor.TryProcess(test.Source, out var result))
                throw new Exception("TIM runtime rejected " + test.Name);
            Check(Normalize(result.Source) == Normalize(test.Expected), test.Name + " expansion");
            Check(result.SourceMap.Count == result.Source.Split('\n').Length, test.Name + " source map length");
        }
        Resources();
        AssetResources();
        Metadata();
        Limits();
    }

    private static void Resources()
    {
        var files = new Dictionary<string, string>
        {
            ["models/diagrams.puml"] = "@startuml(id=first)\nclass First\n@enduml\n@startuml(id=second)\nclass Second\n@enduml",
            ["models/sections.puml"] = "@startuml\n!startsub Selected\nclass One\n!endsub\nclass Omitted\n!startsub Selected\nclass Two\n!endsub\n@enduml",
            ["models/parts/shared.iuml"] = "class Shared",
            ["models/parts/wrapper.iuml"] = "!include ./shared.iuml\nclass Wrapper",
            ["<fixtures/constants>"] = "!function $name() !return \"Library\"",
            ["https://example.invalid/parts/base.iuml"] = "!include child.iuml\nclass Remote",
            ["https://example.invalid/parts/child.iuml"] = "class RemoteChild",
            ["/shared/absolute.iuml"] = "class Absolute",
            ["C:/shared/windows.iuml"] = "class Windows",
            ["<theme/custom>"] = "---\nname: Custom\nauthor: Test\n---\nskinparam BackgroundColor white",
            ["models/themes/puml-theme-local.puml"] = "skinparam ArrowColor blue"
        };
        var options = new PlantUmlRenderOptions { SourceFileName = "models/main.puml", ResolveInclude = path => files.GetValueOrDefault(path) };
        const string source = """
            @startuml
            !include diagrams.puml!first
            !include diagrams.puml!1
            !include diagrams.puml!first
            !includesub sections.puml!Selected
            !include parts/wrapper.iuml
            !include <fixtures/constants>
            class $name()
            !include https://example.invalid/parts/base.iuml
            !include /shared/absolute.iuml
            !include C:\shared\windows.iuml
            @enduml
            """;
        Check(PlantUmlPreprocessor.TryProcess(source, out var result, options), "resource selection and normalization");
        Check(Normalize(result!.Source) == "@startuml\nclass First\nclass Second\nclass One\nclass Two\nclass Shared\nclass Wrapper\nclass Library\nclass RemoteChild\nclass Remote\nclass Absolute\nclass Windows\n@enduml", "included output");
        var outputLines = result.Source.Split('\n');
        var second = result.SourceMap[Array.IndexOf(outputLines, "class Second")];
        Check(second.SourceLine == 2 && second.File == "models/diagrams.puml" && second.FileLine == 4 && second.Generated, "selected diagram provenance");
        var shared = result.SourceMap[Array.IndexOf(outputLines, "class Shared")];
        Check(shared.SourceLine == 5 && shared.File == "models/parts/shared.iuml" && shared.FileLine == 0, "nested include provenance");
        Check(!PlantUmlPreprocessor.TryProcess("@startuml\n!include_once diagrams.puml!0\n!include_once diagrams.puml!0\n@enduml", out _, options), "include_once duplicate");
        Check(PlantUmlPreprocessor.TryProcess("@startuml\n!include_many diagrams.puml!0\n!include_many diagrams.puml!0\n@enduml", out var repeated, options) &&
            repeated.Source.Split("class First").Length == 3, "include_many repetitions");
        Check(PlantUmlPreprocessor.TryProcess("@startuml\n!theme custom\n!theme local from themes\nA -> B : styled\n@enduml", out var themed, options) &&
            themed.Source.Contains("skinparam BackgroundColor white") && themed.Source.Contains("skinparam ArrowColor blue") && !themed.Source.Contains("author:"), "theme resources and frontmatter");
        files["models/themes/puml-theme-relative.puml"] = "!include theme-common.iuml\nskinparam ArrowColor green";
        files["models/theme-common.iuml"] = "class ThemeDependency";
        files["models/puml-theme-nearby.puml"] = "skinparam BackgroundColor ivory";
        files["<c4/themes/puml-theme-fixture.puml>"] = "skinparam ArrowColor purple";
        Check(PlantUmlPreprocessor.TryProcess("@startuml\n!theme relative FROM themes\n!theme nearby\n!theme fixture from <C4/themes>\n@enduml", out var themeVariants, options) &&
            themeVariants.Source.Contains("class ThemeDependency") && themeVariants.Source.Contains("BackgroundColor ivory") &&
            themeVariants.Source.Contains("ArrowColor purple"), "theme locations and caller-relative dependencies");
        files["<theme/metadata>"] = "---\nname: first\nversion: \"1.0\"\n  nested: value\nname: last\n---\nA -> B : %get_current_theme()";
        Check(PlantUmlPreprocessor.TryProcess("@startuml\n!theme custom\n!theme metadata\n%get_current_theme()\n@enduml", out var themeMetadata, options), "theme metadata lifecycle");
        Check(themeMetadata!.Source.Contains("A -> B : {\"name\":\"Custom\",\"author\":\"Test\"}") &&
            themeMetadata.Source.Contains("{\"name\":\"last\",\"version\":\"\\\"1.0\\\"\",\"nested\":\"value\"}"), "reference theme metadata values and publication timing");
        Check(PlantUmlPreprocessor.TryProcess("@startuml\n!theme _none_\n%get_current_theme()\n@enduml", out var noTheme) &&
            noTheme.Source.Contains("\"name\":\"_none_\""), "empty theme metadata");

        var archive = new PlantUmlResourceLibrary(new Dictionary<string, string>
        {
            ["widgets/model.iuml"] = "!include ../common.iuml\nclass Widget",
            ["common.iuml"] = "class ArchiveCommon"
        });
        var importedOptions = options with { ImportLibrary = path => path == "models/widgets.zip" ? archive : null };
        const string importedSource = "@startuml\n!import widgets.zip\n!include widgets/model.iuml\n@enduml";
        Check(PlantUmlPreprocessor.TryProcess(importedSource, out var imported, importedOptions) &&
            imported.Source.Contains("class ArchiveCommon") && imported.Source.Contains("class Widget"), "archive import relative includes");
        Check(!PlantUmlPreprocessor.TryProcess("@startuml\n!include widgets/model.iuml\n@enduml", out _, importedOptions), "imports remain render-local");
        Parallel.For(0, 16, iteration => Check(PlantUmlPreprocessor.TryProcess(importedSource, out _, importedOptions), "concurrent immutable archive import"));

        var diagnostics = new List<string>();
        Check(PlantUmlPreprocessor.TryProcess("@startuml\n!$x=3\n!log value $x\n!dump_memory state\nA -> B : done\n@enduml", out _, new() { Log = diagnostics.Add }), "diagnostic directives");
        Check(diagnostics.Count == 2 && diagnostics[0] == "value 3" && diagnostics[1].Contains("$x = 3"), "diagnostic callback");

        var definitions = new Dictionary<string, string>
        {
            ["Named"] = "class Defined\n!function $defined() !return 42",
            ["Nested"] = "!include shared.iuml\nclass NestedDefinition",
            ["Cycle"] = "!includedef Cycle"
        };
        files["models/parts/with-definition.iuml"] = "!includedef Nested";
        var definedOptions = options with { Definitions = definitions };
        definitions["Named"] = "class ChangedAfterOptions";
        Check(PlantUmlPreprocessor.TryProcess("@startuml\n!includedef Named\n!include parts/with-definition.iuml\nA -> B : $defined()\n@enduml", out var defined, definedOptions), "host source definitions");
        Check(defined!.Source.Contains("class Defined") && !defined.Source.Contains("ChangedAfterOptions") &&
            defined.Source.Contains("class Shared") && defined.Source.Contains("class NestedDefinition") && defined.Source.Contains("A -> B : 42"), "definition snapshot, relative resources and routines");
        Check(defined.SourceMap.Any(location => location is { File: "<definition/Named>", FileLine: 0, SourceLine: 1, Generated: true }), "definition source provenance");
        Check(!PlantUmlPreprocessor.TryProcess("!includedef Missing", out _, definedOptions), "missing source definition");
        Check(!PlantUmlPreprocessor.TryProcess("!includedef Cycle", out _, definedOptions), "recursive source definition bound");
    }

    private static void AssetResources()
    {
        const string art = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"><path d=\"M0 0h8v8H0z\"/></svg>";
        var archive = new PlantUmlResourceLibrary(new Dictionary<string, string> { ["icons/mark.svg"] = art });
        var requests = new List<string>();
        var options = new PlantUmlRenderOptions
        {
            SourceFileName = "models/main.puml",
            ImportLibrary = path => { requests.Add(path); return path == "models/artwork.zip" ? archive : null; },
            ResolveInclude = path => path switch
            {
                "models/parts/art.iuml" => "sprite $first ../artwork.zip~icons/mark.svg\nsprite $second ../artwork.zip~icons/mark.svg",
                "jar:host-library!/icons/mark.svg" or "jar:host-library!/icons/mark~alternate.svg" or
                    "jar:host~library!/icons/mark.svg" => art,
                _ => null
            }
        };
        Check(PlantUmlRenderer.TryRender("@startuml\n!include parts/art.iuml\nrectangle \"<$first> <$second>\" as A\n@enduml", out var rendered, options), "archive asset from included source");
        Check(requests.SequenceEqual(new[] { "models/artwork.zip" }) && XElement.Parse(rendered!.Svg).Descendants().Count(element => element.Name.LocalName == "svg") == 2,
            "asset archive cache and declaration-relative source");
        Check(PlantUmlRenderer.TryRender("@startuml\nsprite $mark jar:host-library!/icons/mark.svg\nrectangle \"<$mark>\" as A\n@enduml", out _, options), "opaque host JAR resource");
        foreach (string path in new[] { "jar:host-library!/icons/mark~alternate.svg", "jar:host~library!/icons/mark.svg" })
            Check(PlantUmlRenderer.TryRender("@startuml\nsprite $mark " + path + "\nrectangle \"<$mark>\" as A\n@enduml", out _, options),
                "opaque JAR identifier retains tilde " + path);
        Check(requests.SequenceEqual(new[] { "models/artwork.zip" }), "JAR identifiers bypass archive entry resolution");
        Check(!PlantUmlRenderer.TryRender("@startuml\nsprite $mark artwork.zip~../escape.svg\nrectangle \"<$mark>\" as A\n@enduml", out _, options), "invalid archive entry fails through renderer");
    }

    private static void Metadata()
    {
        const string source = """
            @startuml
            !option SVG_TITLE "Diagram & <title>"
            !option svg_desc "A description with <markup> & values"
            !option LANGUAGE "fr"
            !option UseDescriptiveNames true
            !option handwritten false
            !option debug false
            A -> B : message
            @enduml
            """;
        Check(PlantUmlPreprocessor.TryProcess(source, out var expanded), "preprocessor option metadata");
        Check(expanded!.Options.Count == 6 && expanded.Options["svg_title"] == "Diagram & <title>" &&
            expanded.Options["language"] == "fr" && expanded.Options["use_descriptive_names"] == "true", "normalized option keys and values");
        Check(!expanded.Source.Contains("!option") && expanded.Source.Contains("skinparam handwritten false"), "option text emission");
        Check(PlantUmlRenderer.TryRender(source, out var diagram), "SVG option integration");
        var svg = XElement.Parse(diagram!.Svg);
        Check(svg.Elements().Any(element => element.Name.LocalName == "title" && element.Value == "Diagram & <title>"), "escaped SVG title option");
        Check(svg.Elements().Any(element => element.Name.LocalName == "desc" && element.Value == "A description with <markup> & values"), "escaped SVG description option");

        Check(PlantUmlPreprocessor.TryProcess("@startuml\nclass \"Combined \\\nname\" as C\nclass D /' inline '/\n@enduml", out var continued), "continued and filtered source");
        var lines = continued!.Source.Split('\n');
        Check(continued.SourceMap[Array.FindIndex(lines, line => line.Contains("Combined name"))] is { SourceLine: 1, Generated: true }, "continued source routed to original statement");
        Check(continued.SourceMap[Array.FindIndex(lines, line => line.Contains("class D"))] is { SourceLine: 3, Generated: true }, "comment-filtered source provenance");

        Check(PlantUmlRenderer.TryRender("@startjson\n!$value={\"items\":[1,2]}\n$value\n@endjson", out var data), "preprocessed JSON rendering");
        var jsonSvg = XElement.Parse(data!.Svg);
        Check(!jsonSvg.DescendantsAndSelf().Attributes().Any(attribute => attribute.Name.LocalName is "data-json-start" or "data-json-length" or "data-json-kind"), "expanded JSON offsets removed");
        Check(jsonSvg.Descendants().Any(element => (string?)element.Attribute("data-source-generated") == "true"), "expanded JSON routes to generated source");

        Check(PlantUmlPreprocessor.TryProcess("%get_all_theme()", out var themeList), "embedded theme enumeration");
        using var themes = JsonDocument.Parse(themeList!.Source);
        foreach (var theme in themes.RootElement.EnumerateArray())
        {
            string name = theme.GetString()!;
            Check(PlantUmlPreprocessor.TryProcess("@startuml\n!theme " + name + "\nA -> B : themed\n@enduml", out _), "complete embedded theme expansion " + name);
        }
    }

    private static void Limits()
    {
        foreach (string body in new[]
        {
            "!final function f(a) !return a\n!function f(a) !return a+1",
            "!assert 0 : \"failed\"", "!return 3", "!function f()\n!endfunction", "!function f(a,a) !return a",
            "!procedure p()\n!return 1\n!endprocedure", "!procedure p()\np()\n!endprocedure\np()",
            "!while 1\n!$n=1\n!endwhile", "!function f() !return f()\nA -> B : f()",
            "!function f(a) !return a\nA -> B : f(a=1,other=2)", "!$json={\"missing\":", "!include ../escape.iuml"
        })
            Check(!PlantUmlPreprocessor.TryProcess("@startuml\n" + body + "\n@enduml", out _), "bounded rejection " + body);

        const string changing = "@startuml\n!$count=0\n!function next()\n!$count=$count+1\n!return $count\n!endfunction\nA -> B : next()\nA -> B : $count\n@enduml";
        Check(PlantUmlPreprocessor.TryProcess(changing, out var result) && Normalize(result.Source) == "@startuml\nA -> B : 1\nA -> B : 1\n@enduml", "function evaluated once per invocation");
        foreach (string snippet in new[]
        {
            "!unquoted final function f($a=\"default\",$b=2)\n!if $b>0\n!return $a\n!endif\n!return \"\"\n!endfunction\nA -> B : f($b=1)",
            "!$data={\"items\":[{\"x\":1},{\"x\":2}]}\n!foreach $v in $data.items\nA -> B : $v.x\n!endfor"
        })
            for (int length = 0; length < snippet.Length; length++)
                PlantUmlPreprocessor.TryProcess("@startuml\n" + snippet[..length] + "\n@enduml", out _);
    }

    private static string Normalize(string source) => string.Join("\n", source.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("TIM runtime: " + name); }
}
