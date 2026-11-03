using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private static void DrawArchimateGlyph(StringBuilder svg, Node node, string color)
    {
        if (node.Kind != "archimate") return;
        string glyph = TypeGlyph(node.Stereotype);
        if (glyph.Length == 0) return;
        svg.Append(CultureInfo.InvariantCulture, $"<path class=\"archimate-type-glyph\" transform=\"translate({node.X + node.Width - 21} {node.Y + 5})\" d=\"{glyph}\" fill=\"none\" stroke=\"{SvgText.Escape(color)}\" stroke-width=\"1.2\" stroke-linejoin=\"round\"/>");
    }
}
