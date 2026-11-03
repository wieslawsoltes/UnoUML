using System.Diagnostics.CodeAnalysis;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private sealed record EdgePaint(string? Stroke, string? Thickness, string? Dashes)
    {
        internal static bool TryParse(string input, [NotNullWhen(true)] out EdgePaint? paint)
        {
            paint = null;
            string? color = null, thickness = null, pattern = null;
            foreach (var modifier in input.Split(',',StringSplitOptions.TrimEntries))
            {
                if (modifier.StartsWith('#'))
                {
                    if (color != null || (color = ManagedSkin.Color(modifier)) == null) return false;
                }
                else if (modifier.StartsWith("thickness=",StringComparison.OrdinalIgnoreCase))
                {
                    if (thickness != null || !ManagedSkin.Thickness(modifier[10..],out thickness)) return false;
                }
                else if (modifier.Equals("bold",StringComparison.OrdinalIgnoreCase))
                {
                    if (thickness != null) return false;
                    thickness = "2";
                }
                else
                {
                    if (pattern != null) return false;
                    pattern = modifier.ToLowerInvariant() switch { "solid" => "", "dotted" => "1,3", "dashed" => "7,7", _ => null };
                    if (pattern == null) return false;
                }
            }
            paint = new(color,thickness,pattern);
            return true;
        }
    }
}
