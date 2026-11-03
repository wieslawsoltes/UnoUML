using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>A measured document title shared by graph and sequence layouts.</summary>
internal sealed record StyleTitle(StyleTitle.Row[] Rows, int FontSize, int Width, int Height, StyleSpacing Padding, StyleSpacing Margin)
{
    internal sealed record Row(string Text, int Width, int Height, int Ascent, int Side);

    internal static StyleTitle? Measure(string? text, ManagedSkin skin, int defaultSize)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int size = skin.Number("title", "fontsize", defaultSize);
        var padding = StyleSpacing.Parse(skin.Style("title", "padding", "0"));
        var margin = StyleSpacing.Parse(skin.Style("title", "margin", "0"));
        int maximum = skin.Number("title", "maximumwidth", 0);
        string fontStyle = skin.Style("title", "fontstyle", "bold");
        double allowance = fontStyle.Contains("bold", StringComparison.OrdinalIgnoreCase) ? 1.17 : 1.08;
        var rows = StyleText.Wrap(text, size, maximum == 0 ? 0 : Math.Max(size, (int)((maximum - padding.Horizontal) / allowance)))
            .Select(row =>
            {
                double largest = SvgText.IsPlain(row) ? (row.Length == 0 ? 0 : size) : RichText.Parse(row).Lines.SelectMany(line => line)
                    .Where(run => run.SpriteName == null && run.Text.Length != 0)
                    .Select(run => ((run.Style.Size ?? size) + run.Style.SizeOffset) * (run.Style.Script == 0 ? 1 : .75)).DefaultIfEmpty(0).Max();
                var ink = StyleText.InkPadding((int)Math.Ceiling(largest));
                int measured = StyleText.Measure(row, size, fontStyle), side = largest == 0 ? 0 : (int)Math.Ceiling(measured * .04);
                return new Row(row, measured + side * 2, SvgText.Height(row, size) + ink.Vertical, SvgText.Ascent(row, size) + ink.Top, side);
            }).ToArray();
        int width = Math.Max(skin.Number("title", "minimumwidth", 0), rows.Max(row => row.Width) + padding.Horizontal);
        int height = rows.Sum(row => row.Height) + (rows.Length - 1) * 4;
        return new(rows, size, width + margin.Horizontal, height + padding.Vertical + margin.Vertical, padding, margin);
    }

    internal void Append(StringBuilder svg, ManagedSkin skin, int center, int top, int sourceLine = -1)
    {
        int x = center - Width / 2 + Margin.Left, y = top + Margin.Top;
        int width = Width - Margin.Horizontal, height = Height - Margin.Vertical;
        svg.Append("<g class=\"title\" data-style-name=\"title\"");
        if (sourceLine >= 0) svg.Append(CultureInfo.InvariantCulture, $" data-source-line=\"{sourceLine}\"");
        svg.Append('>');
        string fill = skin.Style("title", "backgroundcolor", "none"), stroke = skin.Style("title", "linecolor", "none");
        if (fill != "none" || stroke != "none")
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
        string alignment = skin.Style("title", "horizontalalignment", "center");
        string fallback = alignment == "left" ? "start" : alignment == "right" ? "end" : "middle";
        string color = skin.Style("title", "fontcolor", skin.Get("defaultfontcolor", "#232333"));
        int rowTop = y + Padding.Top;
        foreach (var row in Rows)
        {
            string anchor = SvgText.Alignment(row.Text, fallback);
            int rowX = anchor == "start" ? x + Padding.Left + row.Side : anchor == "end" ? x + width - Padding.Right - row.Side : x + Padding.Left + (width - Padding.Horizontal) / 2;
            SvgText.Append(svg, rowX, rowTop + row.Ascent, row.Text, FontSize, anchor, bold: true, color: color);
            rowTop += row.Height + 4;
        }
        svg.Append("</g>");
    }
}
