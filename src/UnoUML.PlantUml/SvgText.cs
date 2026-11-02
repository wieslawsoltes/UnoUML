using System.Globalization;
using System.Net;
using System.Text;

namespace UnoUML.PlantUml;

internal static class SvgText
{
    internal static string Escape(string text) => WebUtility.HtmlEncode(text);

    // Relative advances in a generic sans-serif font. SVG text uses the same font stack.
    // Wide scripts and emoji get one em, combining marks are attached to the preceding rune.
    internal static int Measure(string text, int fontSize = 14)
    {
        int units = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int n = rune.Value;
            if (n > 127 && Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            units += n switch
            {
                'i' or 'j' or 'l' or 'I' or '!' or '|' or '.' or ',' or ':' or ';' or '\'' => 31,
                ' ' or '(' or ')' or '[' or ']' => 35,
                'm' or 'w' or 'M' or 'W' or '@' => 88,
                >= 'A' and <= 'Z' => 69,
                >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff or >= 0x1f000 => 100,
                _ => 58
            };
        }
        return (units * fontSize + 99) / 100;
    }

    internal static bool IsPlain(string value)
    {
        if (value.AsSpan().IndexOfAny('<', '>', '\\') >= 0) return false;
        foreach (var marker in Markup)
            if (value.Contains(marker, StringComparison.Ordinal)) return false;
        return true;
    }

    private static readonly string[] Markup = ["**", "//", "__", "~~", "--", "[[", "]]", "\"\"", "%", "$", "&lt;", "&gt;"];
}
