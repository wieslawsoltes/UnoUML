using System.Text;

namespace UnoUML.PlantUml;

/// <summary>Measured rich labels shared by the table and control painters.</summary>
internal sealed record StyleLabel(string[] Rows, int Size, int Width, int Height, StyleSpacing Padding,
    StyleSpacing Margin, string Color, string FontStyle, string Alignment)
{
    internal const string Metrics = "fontsize padding margin maximumwidth minimumwidth width";

    internal static StyleLabel Measure(string text, string category, int size = 14, string padding = "0", string stereotype = "", string part = "")
    {
        var skin = StyleDocument.Current;
        size = skin.Number(category, "fontsize", size, stereotype, part);
        var inset = StyleSpacing.Parse(skin.Style(category, "padding", padding, stereotype, part));
        var margin = StyleSpacing.Parse(skin.Style(category, "margin", "0", stereotype, part));
        int maximum = skin.Number(category, "maximumwidth", 0, stereotype, part);
        string fontStyle = skin.Style(category, "fontstyle", "normal", stereotype, part);
        int allowance = fontStyle.Contains("bold", StringComparison.OrdinalIgnoreCase) ? 115 : 106;
        var rows = StyleText.Wrap(text, size, maximum == 0 ? 0 : Math.Max(size, (maximum - inset.Horizontal) * 100 / allowance));
        int contentWidth = rows.Select(row => StyleText.Measure(row, size, fontStyle)).DefaultIfEmpty(0).Max();
        var ink = StyleText.InkPadding(size);
        inset = new(inset.Top + ink.Top, inset.Right, inset.Bottom + ink.Bottom, inset.Left);
        int width = Math.Max(skin.Number(category, "minimumwidth", 0, stereotype, part), contentWidth + inset.Horizontal);
        width = Math.Max(width, skin.Number(category, "width", 0, stereotype, part));
        int height = StyleText.Height(rows, size) + inset.Vertical;
        return new(rows, size, width + margin.Horizontal, height + margin.Vertical, inset, margin,
            skin.Style(category, "fontcolor", "#232333", stereotype, part), fontStyle,
            skin.Style(category, "horizontalalignment", "left", stereotype, part));
    }

    internal void Append(StringBuilder svg, int x, int top, int width = 0, string? alignment = null)
    {
        width = Math.Max(width, Width);
        string fallback = (alignment ?? Alignment) switch { "center" => "middle", "right" => "end", _ => "start" };
        int y = top + Margin.Top + Padding.Top;
        int left = x + Margin.Left + Padding.Left, right = x + width - Margin.Right - Padding.Right;
        foreach (string row in Rows)
        {
            string anchor = SvgText.Alignment(row, fallback);
            int labelX = anchor switch { "middle" => (left + right) / 2, "end" => right, _ => left };
            SvgText.Append(svg, labelX, y + SvgText.Ascent(row, Size), row, Size, anchor,
                bold: FontStyle.Contains("bold", StringComparison.OrdinalIgnoreCase),
                italic: FontStyle.Contains("italic", StringComparison.OrdinalIgnoreCase), color: Color);
            y += SvgText.Height(row, Size) + 4;
        }
    }

    internal static void Scope(StringBuilder svg, string category, string stereotype = "")
    {
        svg.Append(" data-style-name=\"").Append(SvgText.Escape(category)).Append("\" data-style-metrics=\"").Append(Metrics).Append('"');
        if (stereotype.Length > 0) svg.Append(" data-style-stereotype=\"").Append(SvgText.Escape(stereotype)).Append('"');
    }
}
