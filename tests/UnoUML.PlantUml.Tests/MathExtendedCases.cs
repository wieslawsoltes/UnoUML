using System.Globalization;
using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class MathExtendedCases
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    internal static void Run()
    {
        foreach (string formula in new[]
        {
            "cancel(x)+overset(n)(=)+underset(i)(sum)",
            "color(red)(x^2)+color(#1266aa)(sqrt y)",
            "obrace(a+b)^n+ubrace(c+d)_m+overarc(xy)",
            "cc(ABC)+fr(hello)+bbb(R)",
            "|x+1|+|__x__|+|~y~|+a|><b+x><|y+a|><|b",
            "a+b newline c+d",
            "{: x if x gt= 0, -x if x lt 0 :}"
        }) RenderFormula(formula, false);
        foreach (string formula in new[]
        {
            @"\color{blue} x+\textcolor{red}{\frac{a}{b}}+z",
            @"{a+b\over c+d}+{n\choose k}+{x\atop y}",
            @"\overbrace{a+b}^{n}+\underbrace{c+d}_{m}",
            @"\overset{!}{=}+\underset{x\to0}{\lim}\cancel{x}+\bcancel{y}+\xcancel{z}",
            @"\mathcal{ABCDEFGHIJKLMNOPQRSTUVWXYZ}+\mathfrak{abcdefghijklmnopqrstuvwxyz}+\boldsymbol{\alpha}",
            @"\begin{array}{l|cr}a&b&c\\long&y&z\end{array}",
            @"\begin{aligned}x&=y+z\\a+b&=c\end{aligned}",
            @"\begin{alignedat}{2}a&=b&c&=d\\e&=f&g&=h\end{alignedat}",
            @"\begin{smallmatrix}1&2\\3&4\end{smallmatrix}",
            @"\begin{gathered}a=b\\c=d\end{gathered}",
            @"\boxed{\displaystyle\frac{x}{y}}+\scriptstyle a_i",
            @"\bigl(\frac{x}{y}\bigr)+\Bigl[\sqrt{x}\Bigr]",
            @"\overleftarrow{AB}+\overleftrightarrow{CD}+\underleftrightarrow{EF}+\ddddot{x}",
            @"\nsubseteq+\twoheadrightarrow+\bigoplus+\oiint+\varnothing"
        }) RenderFormula(formula, true);

        var color = RenderFormula(@"\color{blue}x+{\color{red}y}+z", true);
        var colored = color.Descendants(Ns + "g").Where(e => (string?)e.Attribute("class") == "math-color").ToArray();
        Check(colored.Length == 2 && colored[0].Attribute("fill")!.Value == "blue" && colored[1].Attribute("fill")!.Value == "red", "nested color scope");
        var cancelled = RenderFormula(@"\xcancel{a}", true).Descendants(Ns + "g").Single(e => (string?)e.Attribute("class") == "math-cancel");
        Check(cancelled.Elements(Ns + "path").Count() == 2, "both cancellation strokes");
        var phantom = RenderFormula(@"a\phantom{WWWW}b", true);
        Check(!phantom.Descendants(Ns + "text").Any(e => e.Value.Contains('W')) && (int)phantom.Attribute("width")! > 50, "phantom retains space without ink");
        var aligned = RenderFormula(@"\begin{aligned}a&=b\\long&=c\end{aligned}", true);
        var matrix = aligned.Descendants(Ns + "g").Single(e => (string?)e.Attribute("class") == "math-matrix");
        var cells = matrix.Elements(Ns + "g").ToArray();
        Check(X(cells[0]) > X(cells[2]) && X(cells[1]) == X(cells[3]), "aligned environment right and left alignment");

        foreach (string source in new[]
        {
            "@startuml\nrectangle \"Energy <math>E=mc^2</math>\" as A\n@enduml",
            "@startuml\nA -> B : <latex>\\frac{a}{b}</latex>\n@enduml",
            "@startmindmap\n* Root\n** <math>sqrt(x^2+y^2)</math>\n@endmindmap",
            "@startcreole\n**Formula** <latex>\\sum_{i=0}^n x_i</latex>\n@endcreole",
            "@startuml\n<style>\nclassDiagram { class { MaximumWidth 90 } }\n</style>\nclass \"word word <latex>\\frac{a}{b}</latex> word word\" as A\n@enduml"
        })
        {
            var svg = Render(source);
            Check(svg.Descendants(Ns + "g").Any(e => ((string?)e.Attribute("data-sprite"))?.StartsWith("$math/", StringComparison.Ordinal) == true ||
                ((string?)e.Attribute("data-sprite"))?.StartsWith("$latex/", StringComparison.Ordinal) == true), "inline formula survives painting and wrapping");
            Check(!svg.Descendants().Any(e => e.Attribute("data-math-column") != null), "inline formula has no misleading document offsets");
        }
        foreach (string formula in new[] { @"\textcolor{url(javascript:x)}{a}", @"\begin{array}{lc}a&b&c\end{array}", @"\begin{alignedat}{200}a\end{alignedat}", @"\overset{x}", @"\input{local}" })
            Check(!PlantUmlRenderer.TryRender("@startlatex\n" + formula + "\n@endlatex", out _), "malformed or unsupported extended formula declines");
        Check(!PlantUmlRenderer.TryRender("@startuml\nA -> B : <latex>\\input{file}</latex>\n@enduml", out _), "invalid inline math declines with its containing diagram");
    }

    private static double X(XElement element) => double.Parse(element.Attribute("transform")!.Value[10..^1].Split(' ')[0], CultureInfo.InvariantCulture);
    private static XElement RenderFormula(string formula, bool latex) => Render("@start" + (latex ? "latex" : "math") + "\n" + formula + "\n@end" + (latex ? "latex" : "math"));
    private static XElement Render(string source)
    {
        if (!PlantUmlRenderer.TryRender(source, out var diagram)) throw new Exception("Extended math declined " + source);
        return XElement.Parse(diagram.Svg);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("Extended math " + description);
    }
}
