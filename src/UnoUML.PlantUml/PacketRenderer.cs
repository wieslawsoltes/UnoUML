using System.Globalization;
using UnoUML.Syntax;

namespace UnoUML.PlantUml;

internal static class PacketRenderer
{
    internal static string? TryRender(string source)
    {
        var packet = PacketSource.Parse(source);
        if (packet == null || packet.Fields.Count == 0) return null;
        var fields = packet.Fields;
        int columns = packet.Columns, cell = 23;
        int[] heights = Enumerable.Repeat(packet.NodeHeight, fields[^1].Last / columns + 1).ToArray();
        foreach (var field in fields)
        {
            int visible = Math.Min(field.Width, columns - field.First % columns);
            string[] labels = field.Label.Split("\\n", StringSplitOptions.None);
            if (labels.Any(label => !SvgText.IsPlain(label))) return null;
            int labelWidth = labels.Max(label => SvgText.Measure(label)), labelHeight = labels.Length * 18;
            bool rotated = field.Rotation is 90 or 270;
            cell = Math.Max(cell, ((rotated ? labelHeight : labelWidth) + 12 + visible - 1) / visible);
            int required = Math.Max(packet.NodeHeight * field.Height, (rotated ? labelWidth : labelHeight) + 12);
            for (int row = field.First / columns; row <= field.Last / columns; row++) heights[row] = Math.Max(heights[row], required);
        }
        if (packet.SameHeight) Array.Fill(heights, heights.Max());
        int[] tops = new int[heights.Length + 1]; tops[0] = 26;
        for (int row = 0; row < heights.Length; row++) tops[row + 1] = tops[row] + heights[row];
        int width = columns * cell + 32, height = tops[^1] + 12;
        if (width > 32768 || height > 32768) return null;
        var svg = TreeDiagram.Canvas(width, height);
        for (int bit = 0; bit < columns; bit += packet.ScaleInterval)
        {
            int column = packet.RightToLeft ? columns - bit - 1 : bit;
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{16 + column * cell + cell / 2}\" y=\"19\" text-anchor=\"middle\" font-size=\"11\">{bit}</text>");
        }
        for (int index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            svg.Append(CultureInfo.InvariantCulture, $"<g class=\"packet-field\" data-packet-field-index=\"{index}\" data-packet-first=\"{field.First}\" data-packet-last=\"{field.Last}\" data-source-line=\"{field.Row.Index}\" data-edit-start=\"{field.LabelToken.Start}\" data-edit-length=\"{field.LabelToken.Length}\" data-edit-kind=\"packet-label\" data-edit-role=\"label\"><title>{SvgText.Escape(field.Label)}</title>");
            for (int start = field.First; start <= field.Last;)
            {
                int row = start / columns, stop = Math.Min(field.Last + 1, (row + 1) * columns), span = stop - start;
                int column = packet.RightToLeft ? columns - start % columns - span : start % columns;
                int x = 16 + column * cell, y = tops[row], w = span * cell, h = heights[row];
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"#f5f5fa\" stroke=\"#68687d\"/>");
                if (start == field.First)
                {
                    int cx = x + w / 2, cy = y + h / 2;
                    if (field.Rotation != 0) svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"rotate({field.Rotation} {cx} {cy})\">");
                    string[] labels = field.Label.Split("\\n", StringSplitOptions.None);
                    for (int line = 0; line < labels.Length; line++)
                        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{cx}\" y=\"{cy + 5 + (line * 2 - labels.Length + 1) * 9}\" text-anchor=\"middle\">{SvgText.Escape(labels[line])}</text>");
                    if (field.Rotation != 0) svg.Append("</g>");
                }
                start = stop;
            }
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }
}
