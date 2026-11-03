using System.Xml.Linq;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class DitaaCases
{
    public static void Run()
    {
        var architecture=Render("+--------+     +-------+\n| Client +---->| Store |\n+--------+     +-------+");
        var shapes=Find(architecture,"entity").ToArray();
        Verify((int?)architecture.Attribute("width")==280 && (int?)architecture.Attribute("height")==98,"source grid dimensions match pinned JVM bitmap");
        Verify(shapes.Length==2 && Find(architecture,"link").Count()==1 && Find(architecture,"arrowhead").Count()==1,"two independent faces and connecting arrow");
        Verify(shapes.All(e=>(int?)e.Attribute("data-source-line")==1),"outline source row");
        var labels=Find(architecture,"ditaa-label").ToArray();
        Verify(labels.Select(e=>e.Elements().Last().Value).SequenceEqual(new[]{"Client","Store"}) &&
            labels.All(e=>(int?)e.Attribute("data-source-line")==2),"original label lines");
        var adjacent=Render("+------+------+\n|cRED  |cGRE  |\n+------+------+");
        Verify(Find(adjacent,"entity").Count()==2 && adjacent.Descendants().Any(e=>(string?)e.Attribute("fill")=="#ee3322") &&
            adjacent.Descendants().Any(e=>(string?)e.Attribute("fill")=="#99dd99"),"shared wall and named fills");
        var nested=Render("+-------------+\n| cBLU        |\n|  +------+   |\n|  |cABC  |   |\n|  +------+   |\n+-------------+");
        Verify(Find(nested,"entity").Count()==2 && nested.Descendants().Any(e=>(string?)e.Attribute("fill")=="#AABBCC"),"nested shape owning its color");
        foreach(string tag in new[]{"d","s","io","mo","c","o","tr"})
        {
            var icon=Render("+------=---+\n| {"+tag+"}"+new string(' ',7-tag.Length)+"|\n+----------+");
            Verify(Find(icon,"entity").Single().Attribute("data-ditaa-shape")?.Value==tag && icon.Descendants().Any(e=>e.Attribute("stroke-dasharray")!=null),"tag and dashed contour "+tag);
        }
        var arc=Render("/------\\\n| ok   |\n\\------/");
        Verify(Find(arc,"entity").Single().Elements().Any(e=>e.Attribute("d")?.Value.Contains('Q')==true),"rounded corner geometry");
        var open=Render("    ^\n    |\n<---*====>");
        Verify(Find(open,"arrowhead").Count()==3 && open.Descendants().Any(e=>e.Name.LocalName=="circle"),"wire branches and marker");
        var safe=Render("+-------------+\n| <A & B>     |\n+-------------+");
        Verify(Find(safe,"ditaa-label").Any(e=>e.Elements().Last().Value=="<A & B>"),"safe text");
        foreach(string unsupported in new[]{"!include foo", "+------+\n|{icon}|\n+------+"})
            Verify(!PlantUmlRenderer.TryRender("@startditaa\n"+unsupported+"\n@endditaa",out _),"unsupported Ditaa source");
    }

    private static IEnumerable<XElement> Find(XElement root,string kind)=>root.Descendants().Where(e=>(string?)e.Attribute("class")==kind);
    private static XElement Render(string body)
    {
        if(!PlantUmlRenderer.TryRender("@startditaa\n"+body+"\n@endditaa",out var result))throw new InvalidOperationException("Ditaa render failed:\n"+body);
        return XElement.Parse(result.Svg);
    }
    private static void Verify(bool value,string label)
    {
        if(!value)throw new InvalidOperationException("Ditaa check failed: "+label);
    }
}
