using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class EditingInputs
{
    // An interactive editor routinely sees half-typed tokens. A rejected edit must
    // return false; a successful edit must produce a complete, addressable SVG.
    internal static int Run()
    {
        var seeds = new[]
        {
            "@startuml\nparticipant A\nA -> B : Hello\n@enduml",
            "@startuml\nclass A {\n +value : int\n}\nclass B\nA --> B\n@enduml",
            "@startuml\nstart\nif (Approved?) then (yes)\n:Ship;\nendif\nstop\n@enduml",
            "@startjson\n{\"items\":[true,\"name\",25]}\n@endjson",
            "@startdot\ndigraph G { a -> b [label=\"route\"]; }\n@enddot",
            "@startwbs\n* Root\n** Ship\n*** QA\n@endwbs",
            "@startebnf\nitem = 'a', [item];\n@endebnf",
            "@startregex\n(A|BC)+[0-9]?\n@endregex",
            "@startgantt\nProject starts 2026-01-05\n[A] lasts 2 days\n@endgantt",
            "@startchart\nh-axis [A,B]\nv-axis 0 --> 5\nbar \"S\" [1,2] #123456\n@endchart",
            "@startuml\n!$n = 0\n!procedure $send($i)\nA -> B : $i\n!endprocedure\n!while $n < 2\n$send($n)\n!$n = $n + 1\n!endwhile\n@enduml",
            "@startuml\n<style>\nclassDiagram { class { BackgroundColor #abcdef } }\n</style>\nclass A\nA --> B\n@enduml",
            "@startuml\ncreate participant B\nA -> B ++ : Start\nnewpage Result\nref over A, B : Work\nreturn Ready\ndestroy B\n@enduml",
            "@startchen\nentity CUSTOMER {\n Id <<key>>\n Name {\n First\n }\n}\nrelationship OWNS {\n}\nOWNS -N- CUSTOMER\n@endchen",
            "@startditaa\n+-------+    +-------+\n| Start +--->| Done  |\n+-------+    +-------+\n@endditaa",
            "@startmath\nsum_(i=1)^n x_i + sqrt(x^2 + 1)\n@endmath",
            "@startlatex\n\\frac{x^2}{\\sqrt{y}}+\\sum_{i=1}^{n}i\n@endlatex",
            "@startuml\n!include <C4/C4_Deployment>\nDeployment_Node(host,\"Server\",\"Linux\") {\nContainer(api,\"API\",\"C#\")\n}\n@enduml"
        };
        var random = new Random(5701);
        const string alphabet = "[]{}:<>!%#\\_\n0123= '";
        foreach (string seed in seeds)
        {
            int start = seed.IndexOf('\n') + 1, end = seed.LastIndexOf("\n@end", StringComparison.Ordinal);
            for (int i = 0; i < 60; i++)
            {
                int at = random.Next(start, end);
                string mutated = i % 2 == 0 ? seed.Remove(at, random.Next(1, Math.Min(6, end-at)+1))
                    : seed.Insert(at, alphabet[random.Next(alphabet.Length)].ToString());
                if (!PlantUmlRenderer.TryRender(mutated, out var rendered)) continue;
                var xml = XDocument.Parse(rendered.Svg);
                int last = mutated.Count(c => c == '\n');
                foreach (var n in xml.Descendants().Attributes("data-source-line"))
                    if ((int)n < 0 || (int)n > last) throw new Exception("Invalid source metadata for " + mutated);
            }
        }
        return seeds.Length * 60;
    }
}
