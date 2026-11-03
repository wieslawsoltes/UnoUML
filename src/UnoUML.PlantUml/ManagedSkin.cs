using System.Globalization;

namespace UnoUML.PlantUml;

/// <summary>Validated paint values shared by legacy skin parameters and modern style declarations.</summary>
internal sealed class ManagedSkin
{
    private static readonly string[] StructuralScopes = ["class", "object", "component", "rectangle", "package", "database", "note"];
    private readonly Dictionary<string,string> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,int> priorities = new(StringComparer.OrdinalIgnoreCase);
    internal bool HasModernStyles { get; set; }
    internal bool ModernParticipantStyle { get; set; }
    internal string Get(string key, string fallback)
    {
        if (key != "defaultfontcolor" && key.EndsWith("fontcolor", StringComparison.Ordinal) &&
            priorities.TryGetValue("defaultfontcolor", out int global) && global > priorities.GetValueOrDefault(key, -1))
            return values["defaultfontcolor"];
        return values.GetValueOrDefault(key, fallback);
    }
    internal int Size(string key, int fallback) => values.TryGetValue(key,out var value) ? int.Parse(value,CultureInfo.InvariantCulture) : fallback;
    internal string For(string category, string key, string stereotype, string fallback)
    {
        if (values.Count == 0) return fallback;
        string property = category + key;
        return stereotype.Length != 0 && values.TryGetValue(property + "<<" + stereotype + ">>",out var value) ? value : Get(property,fallback);
    }

    internal bool Read(string input, ref string? block, bool sequence, int sourceLine = int.MaxValue)
    {
        string setting;
        if (block != null)
        {
            if (input == "}") { block = null; return true; }
            setting = input;
        }
        else
        {
            setting = input[10..].Trim();
            if (setting.EndsWith('{'))
            {
                string scope = setting[..^1].Trim().ToLowerInvariant();
                if (!(sequence ? scope == "sequence" : StructuralScopes.Contains(scope))) return false;
                block = scope;
                return true;
            }
        }
        int split = setting.IndexOfAny([' ', '\t']);
        if (split <= 0 || values.Count >= 512) return false;
        string property = setting[..split], value = setting[(split+1)..].Trim(), qualifier = "";
        int tag = property.IndexOf("<<", StringComparison.Ordinal);
        if (tag >= 0)
        {
            if (sequence || !property.EndsWith(">>",StringComparison.Ordinal) || tag + 4 >= property.Length ||
                property[(tag+2)..^2].Any(c=>!(char.IsLetterOrDigit(c) || c is '-' or '_'))) return false;
            qualifier = property[tag..]; property = property[..tag];
        }
        string key = ((block ?? "") + property).ToLowerInvariant();
        if (key == "shadowing") return qualifier.Length == 0 && value.Equals("false",StringComparison.OrdinalIgnoreCase);
        if (sequence && key.StartsWith("sequence",StringComparison.Ordinal)) key=key[8..];
        bool global = key is "backgroundcolor" or "defaultfontname" or "defaultfontcolor" or "titlefontcolor"
            or "arrowcolor" or "arrowfontcolor" or "arrowthickness" or "arrowfontsize";
        bool allowed = global;
        if (!allowed && sequence)
        {
            allowed = key is "participantbordercolor" or "participantbackgroundcolor" or "participantfontcolor" or "participantfontsize" or "participantborderthickness"
                or "actorbordercolor" or "actorbackgroundcolor" or "actorfontcolor" or "actorfontsize"
                or "lifelinebordercolor" or "lifelinebackgroundcolor" or "notebackgroundcolor" or "notebordercolor" or "notefontcolor";
        }
        else if (!allowed)
        {
            foreach (string scope in StructuralScopes)
            {
                if (!key.StartsWith(scope,StringComparison.Ordinal)) continue;
                allowed = key[scope.Length..] is "backgroundcolor" or "bordercolor" or "fontcolor" or "borderthickness";
                break;
            }
        }
        if (!allowed || global && qualifier.Length != 0) return false;
        return SetStyle(key + qualifier, value, sourceLine == int.MaxValue ? int.MaxValue : sourceLine * 1024);
    }

    internal bool SetStyle(string property, string input, int priority)
    {
        string key = property.Split("<<", StringSplitOptions.None)[0], value = input;
        if (key.EndsWith("color", StringComparison.Ordinal))
        {
            if (Color(value) is not { } color) return false;
            value = color;
        }
        else if (key.EndsWith("thickness", StringComparison.Ordinal))
        {
            if (!Thickness(value, out value)) return false;
        }
        else if (key.EndsWith("fontsize", StringComparison.Ordinal))
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size is < 8 or > 48) return false;
            value = size.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (value.Length is 0 or > 80 || !char.IsLetter(value[0]) || value.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '-'))) return false;
        }
        if (priority >= priorities.GetValueOrDefault(property, -1))
        {
            values[property] = value;
            priorities[property] = priority;
        }
        return true;
    }

    internal static bool Thickness(string input, out string normalized)
    {
        normalized = "";
        if (!double.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n is <= 0 or > 16) return false;
        normalized = n.ToString("0.###",CultureInfo.InvariantCulture);
        return normalized != "0";
    }

    internal static string? Color(string text)
    {
        if (text.Equals("transparent",StringComparison.OrdinalIgnoreCase) || text.Equals("none",StringComparison.OrdinalIgnoreCase)) return "none";
        var name = text.AsSpan().TrimStart('#');
        if (text.Length > 0 && text[0] == '#' && name.Length + 1 == text.Length && name.Length is 3 or 6 && name.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0) return text;
        string color = name.ToString().ToLowerInvariant();
        return color is "black" or "white" or "red" or "blue" or "green" or "yellow" or "orange" or "pink" or "gray" or "grey" or "purple" or "brown" or "gold" or "silver" or "lightblue" or "lightgreen" or "darkgreen" or "darkblue" or "darkred" or "steelblue" or "teal" or "navy" or "lightyellow" ? color : null;
    }
}
