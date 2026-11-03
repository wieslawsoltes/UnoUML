using System.Globalization;
using System.Xml.Linq;

namespace UnoUML.PlantUml.Tests;

internal static class MathCases
{
    public static void Run()
    {
        var mass=Render("E = mc^2",false);
        HasGlyph(mass,"E");HasGlyph(mass,"m");
        var script=Class(mass,"scripts").Single();
        if (string.Concat(Class(script,"glyph").Select(x=>x.Value))!="c2") throw new Exception("Exponent must bind only to c");
        var frac=Render(@"\frac{a}{b}",true);
        var fraction=Class(frac,"fraction").Single();
        var parts=fraction.Elements().Where(x=>x.Name.LocalName=="g").ToArray();
        if(parts.Length!=2 || Y(parts[0])>=0 || Y(parts[1])<=0 || !fraction.Elements().Any(x=>x.Name.LocalName=="path"))
            throw new Exception("Fraction baselines or rule lost");

        // These exercise the standalone and inline math examples in PlantUML's public grammar.
        Render(@"\sum_{i=0}^{n-1} (a_i + b_i^2)",true);
        Render(@"f(t)=(a_0)/2 + sum_(n=1)^ooa_ncos((npit)/L)+sum_(n=1)^oo b_n\ sin((npit)/L)",false);
        var derivative=Render("d/dxf(x)=lim_(h->0)(f(x+h)-f(x))/h",false);
        if(!Class(derivative,"fraction").First().Value.EndsWith("dx",StringComparison.Ordinal))throw new Exception("Differential denominator");
        Render("P(y|bb\"x\") or f(bb\"x\")+epsilon",false);
        Render(@"\dfrac{d}{dx} f(x)=\lim\limits_{h\to0}\dfrac{f(x+h)-f(x)}{h}",true);
        foreach(var root in new[]{Render("x=(-b+-sqrt(b^2-4ac))/(2a)",false),Render(@"\sqrt[3]{x^2+y_1}",true)})
            if(!Class(root,"radical").Any() || !Class(root,"scripts").Any())throw new Exception("Radical and nested script");
        var matrix=Render(@"\left(\begin{matrix}1&2\\3&4\end{matrix}\right)",true);
        var ascii=Render("[[1,2],[3,4]]",false);
        foreach(var diagram in new[]{matrix,ascii})
        {
            var grid=Class(diagram,"matrix").Single();
            var cells=grid.Elements().Where(e=>e.Name.LocalName=="g").ToArray();
            if(cells.Length!=4 || Y(cells[0])!=Y(cells[1]) || Y(cells[2])<=Y(cells[0]) || X(cells[1])<=X(cells[0]))
                throw new Exception("Matrix rows and columns");
        }
        Render(@"\begin{cases}x&x>0\\0&x\le0\end{cases}",true);
        Render(@"\mathbb{R}^n\to\mathbb{C}\quad\text{a < b & c}",true);
        var plain=Render("E=mc^2",false);
        var scaled=Render("scale 2\ncolor #336699\nbackgroundcolor white\n  E=mc^2",false);
        if((int)scaled.Attribute("width")! != 2*(int)plain.Attribute("width")! ||
            (string?)Class(scaled,"expression").Single().Attribute("data-source-line")!="4" ||
            (string?)Class(scaled,"glyph").First().Attribute("data-math-column")!="2") throw new Exception("Scale or formula source location");
        var old=CultureInfo.CurrentCulture;
        try {CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");if(!XNode.DeepEquals(frac,Render(@"\frac{a}{b}",true)))throw new Exception("Non-invariant geometry");}
        finally {CultureInfo.CurrentCulture=old;}
        foreach(var bad in new[]{@"\frac{a}",@"\sqrt{", @"\href{https://example.com}{x}",@"\input{a}", @"\begin{matrix}1&2\\3\end{matrix}", @"\left(x", "@startuml",new string('{',80)+"x"+new string('}',80)})
            Reject(bad,true);
        foreach(var bad in new[]{"color url(javascript:bad)\nx","!include <x>", "sqrt(", "x^", "[[1,2],[3]]","cancel x","scale NaN\nx","x\ny"})Reject(bad,false);
    }
    private static double X(XElement group)=>Coordinate(group,0);
    private static double Y(XElement group)=>Coordinate(group,1);
    private static double Coordinate(XElement group,int component)=>double.Parse(group.Attribute("transform")!.Value[10..^1].Split(' ')[component],CultureInfo.InvariantCulture);
    private static IEnumerable<XElement> Class(XElement doc,string name)=>doc.Descendants().Where(e=>(string?)e.Attribute("class")=="math-"+name);
    private static void HasGlyph(XElement e,string text)
    {
        if(!Class(e,"glyph").Any(g=>g.Value==text))throw new Exception("Missing math glyph "+text);
    }
    private static XElement Render(string body,bool tex)
    {
        var tag=tex?"latex":"math";
        if(!PlantUmlRenderer.TryRender("@start"+tag+"\n"+body+"\n@end"+tag,out var diagram))throw new Exception("Math rejected "+body);
        return XElement.Parse(diagram.Svg);
    }
    private static void Reject(string body,bool tex)
    {
        var tag=tex?"latex":"math";
        if(PlantUmlRenderer.TryRender("@start"+tag+"\n"+body+"\n@end"+tag,out _))throw new Exception("Expected fallback "+body);
    }
}
