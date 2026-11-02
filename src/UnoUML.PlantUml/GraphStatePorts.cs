using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    // Graphviz attaches inputs to the preceding rank side and outputs to the succeeding side.
    // Keep glyph rectangles separate from caption bounds: they are also used as edge ports.
    private static void AttachStatePorts(Node owner, bool horizontal)
    {
        var sides = owner.Children.Where(c => c.IsStatePort).GroupBy(c => c.IsInputPort)
            .Select(g => (Input: g.Key, Ports: g.ToArray())).ToArray();
        foreach (var (_, ports) in sides)
        {
            if (horizontal) owner.Height = Math.Max(owner.Height, 38 + (ports.Length + 1) * 35);
            else owner.Width = Math.Max(owner.Width, 28 + ports.Sum(p => SvgText.Measure(p.Label) + 22));
        }
        owner.Height = Math.Max(owner.Height, 88);
        foreach (var (input, ports) in sides)
        {
            int total = horizontal ? 0 : ports.Sum(p => SvgText.Measure(p.Label) + 22);
            int offset = (owner.Width - total) / 2;
            for (int i = 0; i < ports.Length; i++)
            {
                var p = ports[i];
                int slot = SvgText.Measure(p.Label) + 22;
                int cx = horizontal ? (input ? 0 : owner.Width) : offset + slot / 2;
                int cy = horizontal ? 34 + (i + 1) * (owner.Height - 34) / (ports.Length + 1) : (input ? 0 : owner.Height);
                p.X = cx - 6;
                p.Y = cy - 6;
                p.CaptionBelow = horizontal || !input;
                offset += slot;
            }
        }
    }

    private static int PortCaptionY(Node n) => n.Y + 6 + (n.CaptionBelow ? 19 : -17);

    private static void DrawStatePort(StringBuilder s, Node n)
    {
        int cx = n.X + 6, cy = n.Y + 6;
        if (n.Stereotype is "inputPin" or "outputPin") Rect(s, n.X, n.Y, 12, 12, 0, n.Fill);
        else
        {
            s.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"6\" fill=\"{n.Fill}\" stroke=\"{Ink}\" stroke-width=\"1.5\"/>");
            if (n.Stereotype == "exitPoint") Path(s,$"M{cx-4} {cy-4}l8 8m-8 0l8 -8");
        }
        Text(s, cx, PortCaptionY(n), n.Label, 14);
    }
}
