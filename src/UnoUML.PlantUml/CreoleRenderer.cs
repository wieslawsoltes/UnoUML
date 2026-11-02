using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static class CreoleRenderer
{
    [Flags]
    private enum Face { Normal = 0, Bold = 1, Italic = 2, Underline = 4, Strike = 8, Mono = 16 }

    internal static string? TryRender(string source)
    {
        bool opened = false, closed = false;
        int line = -1, y = 10, width = 0, count = 0;
        int[] numbers = new int[16];
        var content = new StringBuilder();
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var text = raw.TrimEnd();
            if (!opened)
            {
                if (text.TrimStart().IsEmpty) continue;
                if (!text.TrimStart().SequenceEqual("@startcreole")) return null;
                opened = true; continue;
            }
            if (closed) { if (!text.Trim().IsEmpty) return null; continue; }
            if (text.Trim().SequenceEqual("@endcreole")) { closed = true; continue; }
            if (text.IsEmpty) { y += 12; continue; }
            if (++count > 1400 || text.Length > 2048 || y > 32600 || text[0] is '!' or '@' or '|' or '{' or '<' or ':' or ';') return null;
            int level = 0, indent = 0, size = 14;
            bool heading = false;
            string marker = "";
            char prefix = text[0];
            if (prefix is '*' or '#' or '=')
            {
                while (level < text.Length && text[level] == prefix) level++;
                if (level < text.Length && char.IsWhiteSpace(text[level]))
                {
                    if (level > 15 || (prefix == '=' && level > 6)) return null;
                    text = text[level..].TrimStart();
                    if (prefix == '=') { heading = true; size = Math.Max(16, 28 - level * 2); y += 4; }
                    else
                    {
                        indent = 19 * level;
                        if (prefix == '#') { numbers[level]++; marker = numbers[level].ToString(CultureInfo.InvariantCulture) + "."; }
                        else { numbers[level] = 0; marker = "•"; }
                        Array.Clear(numbers, level + 1, numbers.Length - level - 1);
                    }
                }
                else if (prefix != '*' || !text.StartsWith("**", StringComparison.Ordinal)) return null;
            }
            if (marker == "") Array.Clear(numbers);
            if (text.IsEmpty) return null;
            int x = 12 + indent, baseline = y + size;
            content.Append(CultureInfo.InvariantCulture, $"<g data-source-line=\"{line}\">");
            if (marker.Length > 0) content.Append(CultureInfo.InvariantCulture, $"<text x=\"{x-6}\" y=\"{baseline}\" text-anchor=\"end\">{marker}</text>");
            content.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{baseline}\" font-size=\"{size}\">");
            int measured = WriteInline(content, text, size, heading);
            if (measured < 0) return null;
            content.Append("</text></g>");
            width = Math.Max(width, x + measured + 14);
            y += size + (heading ? 9 : 6);
        }
        if (!closed || count == 0 || width > 32768 || y > 32758) return null;
        return TreeDiagram.Canvas(width, y+10).Append(content).Append("</svg>").ToString();
    }

    private static int WriteInline(StringBuilder svg, ReadOnlySpan<char> text, int size, bool heading)
    {
        Face style = Face.Normal;
        int width = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            bool end = i == text.Length;
            Face toggle = end || i + 1 == text.Length ? Face.Normal : text.Slice(i, 2) switch {
                "**" => Face.Bold, "//" => Face.Italic, "__" => Face.Underline, "--" => Face.Strike, "\"\"" => Face.Mono, _ => Face.Normal
            };
            if (!end && toggle == Face.Normal) continue;
            if (i > start)
            {
                string part = text[start..i].ToString();
                if (!SvgText.IsPlain(part) || part.Contains('~') || part.Contains("{{", StringComparison.Ordinal) || part.Contains("##", StringComparison.Ordinal)) return -1;
                svg.Append("<tspan");
                if (heading || style.HasFlag(Face.Bold)) svg.Append(" font-weight=\"bold\"");
                if (style.HasFlag(Face.Italic)) svg.Append(" font-style=\"italic\"");
                if (style.HasFlag(Face.Mono)) svg.Append(" font-family=\"monospace\"");
                bool under = style.HasFlag(Face.Underline), strike = style.HasFlag(Face.Strike);
                if (under || strike) svg.Append(" text-decoration=\"").Append(under && strike ? "underline line-through" : under ? "underline" : "line-through").Append('"');
                svg.Append('>').Append(SvgText.Escape(part)).Append("</tspan>");
                width += (SvgText.Measure(part, size) * (heading || style.HasFlag(Face.Bold) ? 110 : 100) + 99) / 100;
            }
            if (!end) { style ^= toggle; i++; start = i + 1; }
        }
        return style == Face.Normal && width > 0 ? width : -1;
    }
}
