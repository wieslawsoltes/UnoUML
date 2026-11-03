namespace UnoUML.PlantUml;

// D65 CIELUV and the sRGB gamut boundary. The six linear inequalities give
// the maximum chroma on a hue ray without an iterative gamut search.
internal static class Hsluv
{
    private const double ReferenceU = .19783000664283681, ReferenceV = .468319994938791;
    private const double Epsilon = .008856451679035631, Kappa = 903.2962962962963;
    private static readonly double[,] Matrix =
    {
        { 3.240969941904521, -1.537383177570093, -.498610760293 },
        { -.96924363628087, 1.87596750150772, .041555057407175 },
        { .055630079696993, -.20397695888897, 1.056971514242878 }
    };

    internal static int Reverse(int color)
    {
        var (hue, saturation, lightness) = ToHsluv(color);
        // PlantUML rotates perceptual luminance and compresses it towards 50.
        lightness = (lightness + 50) % 100;
        lightness += .25 * (50 - lightness);
        return FromHsluv(hue, saturation, lightness);
    }

    internal static int Tint(int color, double amount)
    {
        var (hue, saturation, lightness) = ToHsluv(color);
        return FromHsluv(hue, saturation, lightness + (100 - lightness) * amount);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsluv(int color)
    {
        double red = Linear((color >> 16) / 256.0), green = Linear((color >> 8 & 255) / 256.0), blue = Linear((color & 255) / 256.0);
        double x = .4123907992659595 * red + .357584339383878 * green + .1804807884018343 * blue;
        double y = .2126390058715104 * red + .715168678767756 * green + .07219231536073371 * blue;
        double z = .01933081871559185 * red + .119194779794626 * green + .9505321522496607 * blue;
        double lightness = y > Epsilon ? 116 * Math.Cbrt(y) - 16 : Kappa * y;
        double denominator = x + 15 * y + 3 * z;
        double u = denominator == 0 ? 0 : 13 * lightness * (4 * x / denominator - ReferenceU);
        double v = denominator == 0 ? 0 : 13 * lightness * (9 * y / denominator - ReferenceV);
        double chroma = Math.Sqrt(u * u + v * v);
        double hue = chroma < .00000001 ? 0 : Math.Atan2(v, u);
        double saturation = lightness < .00000001 || lightness > 99.9999999 ? 0 : chroma / MaximumChroma(lightness, hue);

        return (hue, saturation, lightness);
    }

    private static int FromHsluv(double hue, double saturation, double lightness)
    {
        if (lightness < .00000001) return 0;
        if (lightness > 99.9999999) return 0xffffff;
        double chroma = saturation * MaximumChroma(lightness, hue);
        double u = Math.Cos(hue) * chroma;
        double v = Math.Sin(hue) * chroma;
        double primeU = u / (13 * lightness) + ReferenceU, primeV = v / (13 * lightness) + ReferenceV;
        double y = lightness > 8 ? Math.Pow((lightness + 16) / 116, 3) : lightness / Kappa;
        double x = 9 * y * primeU / (4 * primeV);
        double z = y * (12 - 3 * primeU - 20 * primeV) / (4 * primeV);
        int Channel(int row)
        {
            double value = Matrix[row, 0] * x + Matrix[row, 1] * y + Matrix[row, 2] * z;
            double gamma = value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
            return Math.Clamp((int)(gamma * 255), 0, 255);
        }
        return Channel(0) << 16 | Channel(1) << 8 | Channel(2);
    }

    private static double Linear(double value) => value > .04045 ? Math.Pow((value + .055) / 1.055, 2.4) : value / 12.92;

    private static double MaximumChroma(double lightness, double hue)
    {
        double sub = Math.Pow(lightness + 16, 3) / 1560896;
        double scaled = sub > Epsilon ? sub : lightness / Kappa;
        double shortest = double.PositiveInfinity;
        double sine = Math.Sin(hue), cosine = Math.Cos(hue);
        for (int component = 0; component < 3; component++)
        {
            double m1 = Matrix[component, 0], m2 = Matrix[component, 1], m3 = Matrix[component, 2];
            for (int boundary = 0; boundary < 2; boundary++)
            {
                double denominator = (632260 * m3 - 126452 * m2) * scaled + 126452 * boundary;
                double slope = (284517 * m1 - 94839 * m3) * scaled / denominator;
                double intercept = ((838422 * m3 + 769860 * m2 + 731718 * m1) * lightness * scaled - 769860 * boundary * lightness) / denominator;
                double length = intercept / (sine - slope * cosine);
                if (length >= 0) shortest = Math.Min(shortest, length);
            }
        }
        return shortest;
    }
}
