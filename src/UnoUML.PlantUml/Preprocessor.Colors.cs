using System.Globalization;

namespace UnoUML.PlantUml;

internal sealed partial class Preprocessor
{
    private static Value ColorBuiltin(string name, IReadOnlyList<Value> args)
    {
        if (name == "%hsl_color")
            return Value.String(Hsl(args[0].Int, args[1].Int, args[2].Int, args.Count == 4 ? args[3].Int : 100));
        string input = args[0].Scalar;
        int rgb = 0;
        if (!input.Equals("transparent", StringComparison.OrdinalIgnoreCase) &&
            (input.TrimStart('#').Length == 4 && input.TrimStart('#').All(char.IsAsciiHexDigit) || !ManagedSkin.TryRgb(input, out rgb))) throw Unsupported();
        int red = rgb >> 16, green = rgb >> 8 & 255, blue = rgb & 255;
        if (name is "%is_dark" or "%is_light")
        {
            bool dark = (red * 299 + green * 587 + blue * 114) / 1000 < 128;
            return Bool(name == "%is_dark" ? dark : !dark);
        }
        if (name == "%reverse_color") return Value.String("#" + (rgb ^ 0xffffff).ToString("X6", CultureInfo.InvariantCulture));
        if (name == "%reverse_hsluv_color") return Value.String("#" + Hsluv.Reverse(rgb).ToString("X6", CultureInfo.InvariantCulture));
        float r = red / 255f, g = green / 255f, b = blue / 255f;
        float minimum = MathF.Min(r, MathF.Min(g, b)), maximum = MathF.Max(r, MathF.Max(g, b));
        float delta = maximum - minimum, luminance = (maximum + minimum) / 2;
        float saturation = delta == 0 ? 0 : delta / (luminance <= .5f ? maximum + minimum : 2 - maximum - minimum);
        float hue = delta == 0 ? 0 : maximum == r ? (60 * (g - b) / delta + 360) % 360 : maximum == g ? 60 * (b - r) / delta + 120 : 60 * (r - g) / delta + 240;
        float ratio = args[1].Int / 100f;
        return Value.String(Hsl(hue, saturation * 100, luminance * 100 * (name == "%lighten" ? 1 + ratio : 1 - ratio), 100));
    }

    private static string Hsl(float hue, float saturation, float luminance, float alpha)
    {
        hue = hue % 360 / 360;
        saturation = Math.Clamp(saturation, 0, 100) / 100;
        luminance = Math.Clamp(luminance, 0, 100) / 100;
        int opacity = (int)(Math.Clamp(alpha, 0, 100) / 100 * 255 + .5f);
        if (opacity == 0) return "transparent";
        float maximum = luminance < .5f ? luminance * (1 + saturation) : luminance + saturation - saturation * luminance;
        float minimum = 2 * luminance - maximum;
        int Channel(float position)
        {
            if (position < 0) position++;
            if (position > 1) position--;
            float value = position < 1f / 6 ? minimum + (maximum - minimum) * 6 * position : position < .5f ? maximum :
                position < 2f / 3 ? minimum + (maximum - minimum) * (2f / 3 - position) * 6 : minimum;
            return (int)(Math.Clamp(value, 0, 1) * 255 + .5f);
        }
        int rgb = Channel(hue + 1f / 3) << 16 | Channel(hue) << 8 | Channel(hue - 1f / 3);
        return opacity == 255 ? "#" + rgb.ToString("X6", CultureInfo.InvariantCulture) : "#" +
            ((uint)opacity << 24 | (uint)rgb).ToString("x8", CultureInfo.InvariantCulture);
    }
}
