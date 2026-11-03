using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace UnoUML.PlantUml;

internal static class SvgText
{
    internal static string Escape(string text) => WebUtility.HtmlEncode(text);

    internal static bool IsSupported(string text) => RichText.IsSupported(text);

    internal static string Alignment(string text, string fallback = "middle")
    {
        if (IsPlain(text)) return fallback;
        var document = RichText.Parse(text);
        string alignment = document.Alignments is { Length: > 0 } rows && rows[0].Length > 0 ? rows[0] : document.Alignment;
        return alignment.Length == 0 ? fallback : alignment;
    }

    internal static int Height(string text, int fontSize = 14)
    {
        if (IsPlain(text)) return fontSize;
        var lines = Layout(text, fontSize);
        return Ceiling(lines.Sum(line => line.Ascent + line.Descent) + Math.Max(0, lines.Length - 1) * Gap(fontSize));
    }

    internal static int Ascent(string text, int fontSize = 14) => IsPlain(text) ? Ceiling(fontSize * .8) : Ceiling(Layout(text, fontSize)[0].Ascent);

    // Relative advances in a generic sans-serif font. SVG text uses the same font stack.
    // Wide scripts and emoji get one em, combining marks are attached to the preceding rune.
    internal static int Measure(string text, int fontSize = 14)
    {
        if (IsPlain(text)) return Ceiling(Advance(text, fontSize));
        return Ceiling(Layout(text, fontSize).Max(line => line.Runs.Sum(run => run.Width)));
    }

    internal static int MeasurePlain(string text, int fontSize = 14) => Ceiling(Advance(text, fontSize));

    private static double Advance(string text, double fontSize, bool monospaced = false)
    {
        long units = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int n = rune.Value;
            if (n > 127 && Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            if (monospaced) { units += n == '\t' ? 240 : 60; continue; }
            units += n switch
            {
                'i' or 'j' or 'l' or 'I' or '!' or '|' or '.' or ',' or ':' or ';' or '\'' => 31,
                ' ' or '(' or ')' or '[' or ']' => 35,
                '\t' => 140,
                'm' or 'w' or 'M' or 'W' or '@' => 88,
                >= 'A' and <= 'Z' => 69,
                >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff or >= 0x1f000 => 100,
                _ => 58
            };
        }
        return units * fontSize / 100;
    }

    private sealed record PlacedRun(RichText.Run Source, double Width, double FontSize, double Shift, RichText.Sprite? Sprite, double SpriteScale, double Inset = 0);
    private sealed record Line(PlacedRun[] Runs, double Ascent, double Descent, string Alignment = "");
    private static readonly ConditionalWeakTable<string, ConcurrentDictionary<int, Line[]>> Metrics = new();

    private static Line[] Layout(string text, int size)
    {
        var document = RichText.Parse(text);
        if (!document.Supported) return [new([new(new(text, new()), Advance(text, size), size, 0, null, 1)], size * .8, size * .2)];
        // Resource dimensions belong to the current render. Pure text metrics are
        // immutable and shared by measurement, wrapping and drawing within a frame.
        Line[] Arrange() => document.Lines.Select((line, index) => Place(line, size,
            document.Alignments?[index] is { Length: > 0 } alignment ? alignment : document.Alignment)).ToArray();
        if (document.Lines.Any(line => line.Any(run => run.SpriteName != null))) return Arrange();
        return Metrics.GetValue(text, static _ => new()).GetOrAdd(size, _ => Arrange());
    }

    private static Line Place(IEnumerable<RichText.Run> runs, int size, string alignment = "")
    {
        double ascent = size * .8, descent = size * .2;
        var placed = new List<PlacedRun>();
        foreach (var run in runs)
        {
            double nominal = (run.Style.Size ?? size) + run.Style.SizeOffset;
            double fontSize = run.Style.Script == 0 ? nominal : nominal * .75;
            double shift = run.Style.Script switch { > 0 => -nominal * .4, < 0 => nominal * .25, _ => 0 };
            var sprite = run.SpriteName is { } name ? RichText.Resolve(name) : null;
            bool icon = run.SpriteName?.StartsWith("$openiconic/", StringComparison.Ordinal) == true;
            bool emoji = run.SpriteName?.StartsWith("$emoji/", StringComparison.Ordinal) == true;
            double scale = run.SpriteScale * (run.Image ? 1 : nominal / (icon ? 12 : emoji ? 24 : 13));
            double inset = icon ? 1 : 0;
            double width = sprite == null ? Advance(run.Text, fontSize, IsMonospaced(run.Style.Font)) * (run.Style.Bold == true ? 1.06 : 1) : sprite.Width * scale + inset * 2;
            double top = sprite == null ? fontSize * .8 - shift : sprite.Height * scale;
            double bottom = sprite == null ? fontSize * .2 + shift : 0;
            ascent = Math.Max(ascent, top);
            descent = Math.Max(descent, bottom);
            placed.Add(new(run, width, fontSize, shift, sprite, scale, inset));
        }
        return new(placed.ToArray(), ascent, descent, alignment);
    }

    internal static void Append(StringBuilder svg, int x, int y, string text, int size, string anchor = "middle",
        bool bold = false, bool italic = false, bool underline = false, string color = "#232333", string part = "")
    {
        if (text.Length == 0) return;
        if (IsPlain(text))
        {
            TextStart(svg, x, y, size, anchor, bold, italic, underline, color, part);
            svg.Append(Escape(text)).Append("</text>");
            return;
        }
        var lines = Layout(text, size);
        double baseline = y;
        double blockWidth = lines.Max(line => line.Runs.Sum(run => run.Width));
        double blockLeft = anchor switch { "middle" => x - blockWidth / 2, "end" => x - blockWidth, _ => x };
        for (int row = 0; row < lines.Length; row++)
        {
            var line = lines[row];
            double width = line.Runs.Sum(run => run.Width);
            string alignment = line.Alignment.Length == 0 ? anchor : line.Alignment;
            double start = blockLeft + (alignment switch { "middle" => (blockWidth - width) / 2, "end" => blockWidth - width, _ => 0 });
            double cursor = start;
            foreach (var run in line.Runs)
            {
                if (run.Source.Style.Background is { } background)
                    svg.Append(CultureInfo.InvariantCulture, $"<rect data-rich-paint=\"true\" x=\"{cursor:0.###}\" y=\"{baseline - line.Ascent:0.###}\" width=\"{run.Width:0.###}\" height=\"{line.Ascent + line.Descent:0.###}\" fill=\"{Escape(background)}\"/>");
                cursor += run.Width;
            }
            cursor = start;
            bool textOpen = false;
            foreach (var run in line.Runs)
            {
                if (run.Sprite is { } sprite)
                {
                    if (textOpen) { svg.Append("</text>"); textOpen = false; }
                    var style = run.Source.Style;
                    LinkStart(svg, style.Link);
                    string tint = run.Source.SpriteColor is null or "currentColor" ? style.Color ?? color : run.Source.SpriteColor;
                    svg.Append("<g data-rich-paint=\"true\"");
                    if (run.Source.Image) svg.Append(" data-inline-image=\"true\"");
                    else svg.Append(" data-sprite=\"").Append(Escape(run.Source.SpriteName!)).Append('"');
                    svg.Append(CultureInfo.InvariantCulture, $" transform=\"translate({cursor + run.Inset:0.###} {baseline - sprite.Height * run.SpriteScale:0.###}) scale({run.SpriteScale:0.######})\" fill=\"{Escape(tint)}\" style=\"color:{Escape(tint)}\">");
                    string? forcedTint = run.Source.SpriteName?.StartsWith("$emoji/", StringComparison.Ordinal) == true && run.Source.SpriteColor != null ? tint : null;
                    svg.Append(SpriteCatalog.Instantiate(sprite.Svg, forcedTint)).Append("</g>");
                    if (style.Link != null) svg.Append("</a>");
                }
                else
                {
                    if (!textOpen)
                    {
                        TextStart(svg, start, baseline, size, "start", bold, italic, underline, color, part);
                        textOpen = true;
                    }
                    Span(svg, run, cursor, baseline);
                }
                cursor += run.Width;
            }
            if (textOpen) svg.Append("</text>");
            cursor = start;
            foreach (var run in line.Runs)
            {
                Decoration(svg, run, cursor, baseline, color);
                cursor += run.Width;
            }
            if (row + 1 < lines.Length) baseline += line.Descent + Gap(size) + lines[row + 1].Ascent;
        }
    }

    private static void TextStart(StringBuilder svg, double x, double y, int size, string anchor, bool bold, bool italic, bool underline, string color, string part)
    {
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x:0.###}\" y=\"{y:0.###}\" text-anchor=\"{Escape(anchor)}\" font-size=\"{size}\" fill=\"{Escape(color)}\"");
        if (bold) svg.Append(" font-weight=\"bold\"");
        if (italic) svg.Append(" font-style=\"italic\"");
        if (underline) svg.Append(" text-decoration=\"underline\"");
        if (part.Length > 0) svg.Append(" data-style-part=\"").Append(Escape(part)).Append('"');
        svg.Append(" xml:space=\"preserve\">");
    }

    private static void Span(StringBuilder svg, PlacedRun run, double x, double y)
    {
        var style = run.Source.Style;
        LinkStart(svg, style.Link);
        svg.Append(CultureInfo.InvariantCulture, $"<tspan x=\"{x:0.###}\" y=\"{y + run.Shift:0.###}\"");
        if (style.Size != null || style.Script != 0 || style.SizeOffset != 0) svg.Append(CultureInfo.InvariantCulture, $" font-size=\"{run.FontSize:0.###}\"");
        if (style.Bold is { } bold) svg.Append(" font-weight=\"").Append(bold ? "bold" : "normal").Append('"');
        if (style.Italic is { } italic) svg.Append(" font-style=\"").Append(italic ? "italic" : "normal").Append('"');
        if (style.Color is { } color)
        {
            svg.Append(" fill=\"").Append(Escape(color)).Append('"');
            if (!style.LinkColorDefault) svg.Append(" data-rich-color=\"explicit\"");
        }
        if (style.Font is { } font)
        {
            svg.Append(" font-family=\"").Append(Escape(font)).Append('"');
            svg.Append(style.Monospaced ? " data-rich-monospaced=\"true\"" : " data-rich-font=\"explicit\"");
        }
        if (style.Link != null) svg.Append(" data-style-part=\"hyperlink\"");
        else if (style.Monospaced) svg.Append(" data-style-part=\"monospaced\"");
        if (style.DecorationColor == null && (style.Underline != null || style.Strike != null))
        {
            string decoration = style.Underline == true ? "underline" : "";
            if (style.Strike == true) decoration += (decoration.Length == 0 ? "" : " ") + "line-through";
            svg.Append(" text-decoration=\"").Append(decoration.Length == 0 ? "none" : decoration).Append('"');
        }
        svg.Append('>').Append(Escape(run.Source.Text)).Append("</tspan>");
        if (style.Link != null) svg.Append("</a>");
    }

    private static void LinkStart(StringBuilder svg, RichText.Link? link)
    {
        if (link == null) return;
        svg.Append("<a");
        if (link.Href.Length > 0) svg.Append(" href=\"").Append(Escape(link.Href)).Append('"');
        svg.Append('>');
        if (link.Tooltip is { Length: > 0 } tooltip) svg.Append("<title>").Append(Escape(tooltip)).Append("</title>");
    }

    private static void Decoration(StringBuilder svg, PlacedRun run, double x, double baseline, string defaultColor)
    {
        var style = run.Source.Style;
        if (run.Width <= 0 || run.Sprite != null) return;
        string color = Escape(style.DecorationColor ?? style.Color ?? defaultColor);
        double y = baseline + run.Shift, stroke = Math.Max(1, run.FontSize / 14);
        if (style.DecorationColor != null)
        {
            if (style.Underline == true) Rule(y + run.FontSize * .12);
            if (style.Strike == true) Rule(y - run.FontSize * .3);
        }
        if (style.Wave == true)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<path data-rich-paint=\"true\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{stroke:0.###}\" d=\"M{x:0.###} {y + 2:0.###}");
            int segments = Math.Min(8192, (int)Math.Ceiling(run.Width / 2));
            for (int i = 1; i <= segments; i++)
                svg.Append(CultureInfo.InvariantCulture, $"L{x + Math.Min(run.Width, i * 2):0.###} {y + 2 + (i % 2 == 0 ? 0 : 2):0.###}");
            svg.Append("\"/>");
        }
        void Rule(double ordinate) => svg.Append(CultureInfo.InvariantCulture, $"<line data-rich-paint=\"true\" x1=\"{x:0.###}\" y1=\"{ordinate:0.###}\" x2=\"{x + run.Width:0.###}\" y2=\"{ordinate:0.###}\" stroke=\"{color}\" stroke-width=\"{stroke:0.###}\"/>");
    }

    internal static string[] Wrap(string text, int size, int maximumWidth)
    {
        if (maximumWidth <= 0 && IsPlain(text)) return [text];
        var document = RichText.Parse(text);
        if (!document.Supported) return text.Split('\n');
        var result = new List<string>();
        for (int index = 0; index < document.Lines.Length; index++)
        {
            var line = document.Lines[index];
            string alignment = document.Alignments?[index] is { Length: > 0 } explicitAlignment ? explicitAlignment : document.Alignment;
            var row = new List<RichText.Run>();
            var pendingSpaces = new List<RichText.Run>();
            double width = 0;
            foreach (var word in Words(line))
            {
                if (word.All(run => run.SpriteName == null && run.Text.All(char.IsWhiteSpace)))
                {
                    pendingSpaces.AddRange(word);
                    continue;
                }
                double wordWidth = Place(word, size).Runs.Sum(run => run.Width);
                double spaceWidth = Place(pendingSpaces, size).Runs.Sum(run => run.Width);
                if (maximumWidth > 0 && row.Count > 0 && width + spaceWidth + wordWidth > maximumWidth)
                {
                    result.Add(Serialize(row, size, alignment));
                    row.Clear();
                    pendingSpaces.Clear();
                    width = 0;
                }
                else
                {
                    row.AddRange(pendingSpaces);
                    width += spaceWidth;
                    pendingSpaces.Clear();
                }
                row.AddRange(word);
                width += wordWidth;
            }
            row.AddRange(pendingSpaces);
            result.Add(Serialize(row, size, alignment));
        }
        return result.ToArray();
    }

    private static IEnumerable<List<RichText.Run>> Words(IEnumerable<RichText.Run> runs)
    {
        var word = new List<RichText.Run>();
        bool? spaces = null;
        foreach (var run in runs)
        {
            if (run.SpriteName != null)
            {
                if (word.Count > 0) { yield return word; word = []; }
                yield return [run];
                spaces = null;
                continue;
            }
            int start = 0;
            while (start < run.Text.Length)
            {
                bool whitespace = char.IsWhiteSpace(run.Text[start]);
                int end = start + 1;
                while (end < run.Text.Length && char.IsWhiteSpace(run.Text[end]) == whitespace) end++;
                if (spaces != null && spaces != whitespace) { yield return word; word = []; }
                spaces = whitespace;
                word.Add(run with { Text = run.Text[start..end] });
                start = end;
            }
        }
        if (word.Count > 0) yield return word;
    }

    private static string Serialize(IEnumerable<RichText.Run> runs, int fontSize, string alignment)
    {
        var text = new StringBuilder();
        text.Append(alignment switch { "start" => "<left>", "end" => "<right>", "middle" => "<center>", _ => "" });
        foreach (var run in Coalesce(runs))
        {
            var style = run.Style;
            var closing = new Stack<string>();
            if (style.Link is { } link)
            {
                text.Append("[[\"").Append(link.Href).Append('"');
                if (link.Tooltip != null) text.Append('{').Append(link.Tooltip).Append('}');
                text.Append(' ');
                closing.Push("]]");
            }
            if (style.Bold == false || style.Italic == false || style.Underline == false || style.Strike == false || style.Wave == false) Tag("plain");
            if (style.Bold == true) Tag("b");
            if (style.Italic == true) Tag("i");
            if (style.Color is { } color && !style.LinkColorDefault) Tag("color", color);
            if (style.Background is { } background) Tag("back", background);
            if (style.Size != null || style.SizeOffset != 0) Tag("size", ((style.Size ?? fontSize) + style.SizeOffset).ToString(CultureInfo.InvariantCulture));
            if (style.Font is { } font)
            {
                if (style.Monospaced) { text.Append("\"\""); closing.Push("\"\""); }
                else Tag("font", font);
            }
            if (style.Underline == true) Tag("u", style.DecorationColor);
            if (style.Strike == true) Tag("s", style.DecorationColor);
            if (style.Wave == true) Tag("w", style.DecorationColor);
            if (style.Script != 0) Tag(style.Script > 0 ? "sup" : "sub");
            if (run.SpriteName is { } name)
            {
                if (name.StartsWith("$math/", StringComparison.Ordinal) || name.StartsWith("$latex/", StringComparison.Ordinal))
                {
                    int separator = name.IndexOf('/');
                    string tag = name[1..separator];
                    text.Append('<').Append(tag).Append('>').Append(name[(separator + 1)..]).Append("</").Append(tag).Append('>');
                    while (closing.Count > 0) text.Append(closing.Pop());
                    continue;
                }
                if (run.Image) text.Append("<img:").Append(name);
                else if (name.StartsWith("$openiconic/", StringComparison.Ordinal)) text.Append("<&").Append(name["$openiconic/".Length..]);
                else if (name.StartsWith("$emoji/", StringComparison.Ordinal)) text.Append("<:").Append(name[7..]).Append(':');
                else text.Append("<$").Append(name);
                if (run.SpriteScale != 1)
                {
                    text.Append(run.Image ? "{scale=" : "*").Append(run.SpriteScale.ToString("0.######", CultureInfo.InvariantCulture));
                    if (run.Image) text.Append('}');
                }
                if (!run.Image && run.SpriteColor is { } tint) text.Append(",color=").Append(tint == "currentColor" ? "#0" : tint);
                text.Append('>');
            }
            else
            {
                foreach (char c in run.Text)
                {
                    if ("<>\\&*/_~-\"[]".Contains(c)) text.Append("<U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('>');
                    else text.Append(c);
                }
            }
            foreach (string close in closing) text.Append(close);
            void Tag(string name, string? argument = null)
            {
                text.Append('<').Append(name);
                if (argument != null) text.Append(':').Append(argument);
                text.Append('>');
                closing.Push("</" + name + ">");
            }
        }
        return text.ToString();
    }

    private static IEnumerable<RichText.Run> Coalesce(IEnumerable<RichText.Run> runs)
    {
        RichText.Run? current = null;
        var content = new StringBuilder();
        foreach (var run in runs)
        {
            if (current != null && (run.SpriteName != null || current.SpriteName != null || current.Style != run.Style))
            {
                yield return current with { Text = content.ToString() };
                current = null;
                content.Clear();
            }
            current ??= run;
            content.Append(run.Text);
        }
        if (current != null) yield return current with { Text = content.ToString() };
    }

    private static bool IsMonospaced(string? font) => font != null && (font.Contains("mono", StringComparison.OrdinalIgnoreCase) || font.Contains("courier", StringComparison.OrdinalIgnoreCase) || font.Equals("Consolas", StringComparison.OrdinalIgnoreCase));
    private static double Gap(int size) => Math.Max(2, size * .25);
    private static int Ceiling(double value) => (int)Math.Min(1_000_000, Math.Ceiling(value));

    internal static bool IsPlain(string value)
    {
        if (value.AsSpan().IndexOfAny(PossibleMarkup) < 0) return true;
        if (value.AsSpan().IndexOfAny(Boundaries) >= 0 || value.Length > 1 && value[0] == '=') return false;
        int entity = value.IndexOf('&');
        if (entity >= 0 && value.IndexOf(';', entity + 1) >= 0 && WebUtility.HtmlDecode(value) != value) return false;
        foreach (var marker in Markup)
            if (value.Contains(marker, StringComparison.Ordinal)) return false;
        return true;
    }

    private static readonly SearchValues<char> PossibleMarkup = SearchValues.Create("<>\\\n\r*/_~-[]\"%$&=");
    private static readonly SearchValues<char> Boundaries = SearchValues.Create("<>\\\n\r");
    private static readonly string[] Markup = ["**", "//", "__", "~~", "--", "[[", "]]", "\"\"", "%", "$", "&lt;", "&gt;"];
}
