using System.Globalization;

namespace UnoUML.PlantUml;

/// <summary>Render-local, validated style cascade shared by native diagram painters.</summary>
internal sealed class ManagedSkin
{
    private static readonly string[] Scopes = ("class object component rectangle package database note actor participant boundary control entity collections queue lifeline state activity arrow title legend node rootnode leafnode file folder task milestone timeline network chenentity chenattribute chenrelationship").Split(' ');
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> priorities = new(StringComparer.OrdinalIgnoreCase);
    internal StyleSheet Sheet { get; set; } = new();
    internal string Diagram { get; set; } = "classdiagram";
    internal bool Handwritten { get; private set; }
    internal int Dpi { get; private set; } = 96;
    internal int CircledCharacterRadius { get; private set; } = 10;
    internal int VisibilityIconSize { get; private set; }
    internal string MonospacedFont { get; private set; } = "monospace";
    internal string ActorStyle { get; private set; } = "stickman";
    internal string LineType { get; private set; } = "polyline";
    internal bool HasModernStyles { get; set; }
    internal bool ModernParticipantStyle { get; set; }

    internal string Get(string key, string fallback)
    {
        if (key == "defaultfontname" && Handwritten && fallback is "sans-serif" or "Arial, sans-serif") fallback = "Comic Sans MS, cursive";
        string category = "", property = key, part = "";
        if (key == "backgroundcolor") category = "document";
        else if (key.StartsWith("default", StringComparison.Ordinal)) property = key[7..];
        else if (key == "arrowcolor") { category = "arrow"; property = "linecolor"; }
        else
            foreach (string scope in Scopes)
                if (key.StartsWith(scope, StringComparison.Ordinal)) { category = scope; property = key[scope.Length..]; break; }
        if (property.StartsWith("member", StringComparison.Ordinal)) { property = property[6..]; part = "body"; }
        if (property.StartsWith("stereotype", StringComparison.Ordinal)) { property = property[10..]; part = "stereotype"; }
        property = property switch { "bordercolor" => "linecolor", "borderthickness" or "thickness" => "linethickness", _ => property };
        return Resolve(category, property, "", fallback, part, key);
    }

    internal int Size(string key, int fallback) => double.TryParse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0 && value <= int.MaxValue ? (int)Math.Ceiling(value) : fallback;
    internal string For(string category, string key, string stereotype, string fallback)
    {
        string property = key, part = "";
        if (property.StartsWith("member", StringComparison.Ordinal)) { property = property[6..]; part = "body"; }
        if (property.StartsWith("stereotype", StringComparison.Ordinal)) { property = property[10..]; part = "stereotype"; }
        property = property switch { "bordercolor" => "linecolor", "borderthickness" => "linethickness", _ => property };
        return Resolve(category, property, stereotype, fallback, part, category + key);
    }

    internal string Style(string category, string property, string fallback, string stereotype = "", string part = "", int depth = -1) =>
        Resolve(category, property, stereotype, fallback, part, null, depth);

    internal int Number(string category, string property, int fallback, string stereotype = "", string part = "", int depth = -1) =>
        (int)Math.Ceiling(double.Parse(Style(category, property, fallback.ToString(CultureInfo.InvariantCulture), stereotype, part, depth), CultureInfo.InvariantCulture));

    internal Dictionary<string, StyleSheet.Value> Query(string category, string stereotype = "", string part = "", int depth = -1, bool ancestorsOnly = false)
    {
        string tags = "root " + (category is "document" or "title" or "legend" or "caption" or "footer" ? "document " : "element ") + Diagram + " " + category + " " + part;
        return Sheet.Resolve(tags, stereotype, depth, ancestorsOnly);
    }

    private string Resolve(string category, string property, string stereotype, string fallback, string part, string? legacyKey, int depth = -1)
    {
        if (values.Count == 0 && Sheet.IsEmpty) return fallback;
        var best = new StyleSheet.Value(fallback, -1, -1);
        void Legacy(string key, int specificity)
        {
            if (!values.TryGetValue(key, out var value)) return;
            var candidate = new StyleSheet.Value(value, specificity, priorities.GetValueOrDefault(key));
            if (candidate.Wins(best)) best = candidate;
        }
        if (property.StartsWith("font", StringComparison.Ordinal)) Legacy("default" + property, 0);
        string suffix = property switch { "linecolor" => category == "arrow" ? "color" : "bordercolor", "linethickness" => category == "arrow" ? "thickness" : "borderthickness", _ => property };
        string key = legacyKey ?? category + (part == "body" ? "member" : part == "stereotype" ? "stereotype" : "") + suffix;
        Legacy(key, 0);
        if (stereotype.Length != 0) Legacy(key + "<<" + stereotype + ">>", 1);
        var modern = Query(category, stereotype, part, depth);
        if (modern.TryGetValue(property, out var styled) && styled.Wins(best)) best = styled;
        // A class header has its own semantic tag, and inherits the class body declarations.
        if (category is "class" or "object" && part.Length == 0 && property.StartsWith("font", StringComparison.Ordinal) &&
            Query(category, stereotype, "header", depth).TryGetValue(property, out styled) && styled.Wins(best)) best = styled;
        return best.Text;
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
                int mark = scope.IndexOf("<<", StringComparison.Ordinal);
                string plain = mark < 0 ? scope : scope[..mark].TrimEnd();
                if (plain.Length > 80 || !plain.All(char.IsAsciiLetter) || mark >= 0 &&
                    (!scope.EndsWith(">>", StringComparison.Ordinal) || scope.Length - mark > 164 || scope.AsSpan(mark + 2, scope.Length - mark - 4).IndexOfAny('<', '>') >= 0)) return false;
                block = scope; return true;
            }
        }
        int split = setting.IndexOfAny([' ', '\t']);
        int qualifierStart = setting.IndexOf("<<", StringComparison.Ordinal);
        if (qualifierStart >= 0 && qualifierStart < split)
        {
            int qualifierEnd = setting.IndexOf(">>", qualifierStart + 2, StringComparison.Ordinal);
            if (qualifierEnd <= qualifierStart + 2 || qualifierEnd - qualifierStart > 162) return false;
            int separator = setting.AsSpan(qualifierEnd + 2).IndexOfAny(' ', '\t');
            split = separator < 0 ? -1 : qualifierEnd + 2 + separator;
        }
        if (split <= 0 || values.Count >= 1024) return false;
        string property = setting[..split], value = setting[(split + 1)..].Trim(), qualifier = "";
        int tag = property.IndexOf("<<", StringComparison.Ordinal);
        if (tag >= 0)
        {
            int end = property.IndexOf(">>", tag + 2, StringComparison.Ordinal);
            if (end <= tag + 2 || property.IndexOf("<<", tag + 2, StringComparison.Ordinal) >= 0) return false;
            qualifier = property[tag..(end + 2)]; property = property[..tag] + property[(end + 2)..];
        }
        string scopeName = block ?? "";
        int scopeTag = scopeName.IndexOf("<<", StringComparison.Ordinal);
        if (scopeTag >= 0)
        {
            if (qualifier.Length != 0) return false;
            qualifier = scopeName[scopeTag..];
            scopeName = scopeName[..scopeTag].TrimEnd();
        }
        string key = (scopeName + property).ToLowerInvariant();
        if (key == "handwritten")
        {
            if (!bool.TryParse(value, out bool handwritten)) return false;
            Handwritten = handwritten; return true;
        }
        if (key == "dpi")
        {
            if (!int.TryParse(value, out int dpi) || dpi is < 36 or > 600) return false;
            Dpi = dpi; return true;
        }
        // useBetaStyle is a retired renderer switch. Modern PlantUML always uses its style engine.
        if (key == "usebetastyle") return bool.TryParse(value, out _);
        if (key == "lifelinestrategy")
        {
            if (value is not ("solid" or "nosolid")) return false;
            Sheet.Rules.Add(new(["lifeline"], [], -1, false, "linestyle", value == "solid" ? "" : "3,3", Priority(sourceLine), false));
            return true;
        }
        // These historic or misspelled keys have no mapping in PlantUML's style engine.
        if (key is "sequencebordercolor" or "sequencestartcolor" or "sequenceendcolor" or "participantparticipantborderthickness")
            return Normalize(key.EndsWith("color", StringComparison.Ordinal) ? "linecolor" : "linethickness", value, out _);
        string? inactive = key switch
        {
            "sequencebackgroundcolor" or "sequencegroupheaderbackgroundcolor" => "backgroundcolor",
            "sequencetitlefontcolor" or "sequencereferenceheaderfontcolor" => "fontcolor",
            "sequencetitlefontname" or "sequencereferenceheaderfontname" => "fontname",
            "sequencetitlefontsize" or "sequencereferenceheaderfontsize" => "fontsize",
            "sequencetitlefontstyle" or "sequencereferenceheaderfontstyle" => "fontstyle",
            _ => null
        };
        if (inactive != null) return Normalize(inactive, value, out _);
        if (key == "linetype")
        {
            value = value.ToLowerInvariant();
            if (value is not ("polyline" or "ortho" or "curve")) return false;
            LineType = value; return true;
        }
        if (key == "actorstyle")
        {
            value = value.ToLowerInvariant();
            if (value is not ("stickman" or "awesome" or "hollow")) return false;
            ActorStyle = value; return true;
        }
        if (key is "circledcharacterradius" or "classattributeiconsize")
        {
            if (!int.TryParse(value, out int size) || size is < 0 or > 64) return false;
            if (key == "circledcharacterradius") CircledCharacterRadius = size;
            else VisibilityIconSize = size;
            return true;
        }
        if (key == "defaultmonospacedfontname")
        {
            if (!Normalize("fontname", value, out value)) return false;
            MonospacedFont = value; return true;
        }
        if (key == "arrowlollipopcolor")
        {
            string format = key.EndsWith("color", StringComparison.Ordinal) ? "linecolor" : key.EndsWith("fontname", StringComparison.Ordinal) ? "fontname" : "roundcorner";
            return Normalize(format, value, out _);
        }
        if (!LegacyStyle.TryMap(key, out string names, out string mapped) || !Normalize(mapped, value, out value)) return false;
        string[] tags = names.Split(' ');
        string[] stereotypes = qualifier.Length == 0 ? [] : qualifier[2..^2].Split(">><<", StringSplitOptions.RemoveEmptyEntries);
        Sheet.Rules.Add(new(tags, stereotypes, -1, false, mapped, value, Priority(sourceLine), false));
        if (LegacyStyle.AdditionalNames(key) is { } additional)
            Sheet.Rules.Add(new(additional.Split(' '), stereotypes, -1, false, mapped, value, Priority(sourceLine), false));
        // Preserve the established key accessors used by sequence lifetime and graph layout code.
        if (key == "sequencegroupbackgroundcolor") key = "sequencegroupheaderbackgroundcolor";
        if (key.StartsWith("sequence", StringComparison.Ordinal)) key = key[8..];
        values[key + qualifier] = value;
        priorities[key + qualifier] = Priority(sourceLine);
        return true;
    }

    private static long Priority(int line) => line == int.MaxValue ? long.MaxValue : line * 4096L;

    internal bool SetStyle(string property, string input, int priority)
    {
        string key = property.Split("<<", StringSplitOptions.None)[0];
        string type = key.EndsWith("color", StringComparison.Ordinal) ? "fontcolor" :
            key.EndsWith("thickness", StringComparison.Ordinal) ? "linethickness" :
            key.EndsWith("fontsize", StringComparison.Ordinal) ? "fontsize" :
            key.EndsWith("fontstyle", StringComparison.Ordinal) ? "fontstyle" :
            key.EndsWith("fontweight", StringComparison.Ordinal) ? "fontweight" :
            key.EndsWith("fontname", StringComparison.Ordinal) ? "fontname" :
            Scopes.FirstOrDefault(key.StartsWith) is { } scope ? key[scope.Length..] : key;
        if (!Normalize(type, input, out string value)) return false;
        if (priority >= priorities.GetValueOrDefault(property, -1)) { values[property] = value; priorities[property] = priority; }
        return true;
    }

    internal static bool Normalize(string property, string input, out string value)
    {
        value = input.Trim();
        if (property.EndsWith("color", StringComparison.Ordinal))
        {
            if (IsAutomaticColor(value)) { value = "automatic"; return property == "fontcolor"; }
            if (Color(value) is { } color) { value = color; return true; }
            // PlantUML's style and skinparam color conversion both fall back to white.
            if (value.Length is 0 or > 160 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '#' or '$' or '_' or '-' or ' ' or '\t')) return false;
            value = "white"; return true;
        }
        if (property == "fontname")
        {
            if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            return value.Length is > 0 and <= 160 && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or ',');
        }
        if (property == "fontstyle") { value = value.ToLowerInvariant(); return value is "plain" or "normal" or "bold" or "italic" or "bolditalic" or "bold italic" or "italic bold"; }
        if (property == "fontweight") return value is "normal" or "bold" or "lighter" or "bolder" || int.TryParse(value, out int weight) && weight is >= 100 and <= 900;
        if (property == "horizontalalignment") { value = value.ToLowerInvariant(); return value is "left" or "center" or "right" or "direction" or "reversedirection"; }
        if (property == "showstereotype") { value = value.ToLowerInvariant(); return value is "true" or "false"; }
        if (property == "linestyle")
        {
            value = value.ToLowerInvariant() switch { "solid" or "0" => "", "dashed" => "7,7", "dotted" => "1,3", _ => value.Replace('-', ',') };
            if (value.Length == 0) return true;
            string[] pattern = value.Split(',', StringSplitOptions.TrimEntries);
            return pattern.Length <= 8 && pattern.All(p => double.TryParse(p, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n) && n > 0 && n <= 128);
        }
        if (property is "padding" or "margin")
        {
            var dimensions = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (dimensions.Length is not (1 or 2 or 4)) return false;
            return dimensions.All(p => double.TryParse(p, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n) && n is >= 0 and <= 256);
        }
        if (property == "width" && value.Equals("auto", StringComparison.OrdinalIgnoreCase)) { value = "auto"; return true; }
        if (property == "shadowing" && bool.TryParse(value, out bool shadow)) value = shadow ? "4" : "0";
        if (property is "linethickness" or "hyperlinkunderlinethickness" or "fontsize" or "roundcorner" or "diagonalcorner" or "shadowing" or "maximumwidth" or "minimumwidth" or "markersize" or "barwidth" or "width")
        {
            if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < 0 || number > 4096) return false;
            if (property == "fontsize" && number is < 1 or > 128 || property == "linethickness" && number > 32 || property == "shadowing" && number > 32) return false;
            value = number.ToString("0.###", CultureInfo.InvariantCulture); return true;
        }
        if (property == "hyperlinkunderlinestyle") return value is "0" or "1" or "2" or "solid" or "dashed" or "dotted";
        if (property == "imageposition") return value.ToLowerInvariant() is "top" or "bottom" or "left" or "right";
        if (property == "markershape") { value = value.ToLowerInvariant(); return value is "circle" or "square" or "diamond" or "triangle" or "cross" or "none"; }
        if (property == "image") return value.Equals("none", StringComparison.OrdinalIgnoreCase);
        return property == "exportedname" && value.Length <= 256 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
    }

    internal static bool Thickness(string input, out string normalized) => Normalize("linethickness", input, out normalized) && normalized != "0";

    internal static bool IsAutomaticColor(string value) => value.AsSpan().TrimStart('#').Equals("automatic", StringComparison.OrdinalIgnoreCase);

    internal static string StereotypeLabel(string text) => "«" + text.Replace(">><<", ", ", StringComparison.Ordinal) + "»";

    private static int Palette(string name) => name.ToLowerInvariant() switch
    {
        "business" => 0xffffcc, "application" => 0xc2f0ff, "motivation" => 0xccccff,
        "strategy" => 0xf8e7c0, "technology" => 0xc9ffc9, "implementation" => 0xffe0e0, _ => -1
    };

    internal static bool TryRgb(string value, out int rgb)
    {
        rgb = 0;
        string name = value.TrimStart('#');
        if (name.Length is 1 or 3 or 4 or 6 or 8 && name.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
        {
            if (name.Length == 1) name = new string(name[0], 6);
            if (name.Length is 3 or 4) name = string.Concat(name.Take(3).Select(c => new string(c, 2)));
            return int.TryParse(name.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
        }
        if (Palette(name) is >= 0 and var custom) { rgb = custom; return true; }
        if (name.Equals("rebeccapurple", StringComparison.OrdinalIgnoreCase)) { rgb = 0x663399; return true; }
        var color = System.Drawing.Color.FromName(name);
        if (!color.IsKnownColor || color.IsSystemColor || color.A == 0) return false;
        rgb = color.ToArgb() & 0xffffff;
        return true;
    }

    internal static bool TryGradient(string text, out string first, out string second, out char direction)
    {
        first = second = ""; direction = '\0';
        int at = text.IndexOfAny(['-', '|', '/', '\\']);
        if (at <= 0 || at == text.Length - 1 || text.AsSpan(at + 1).IndexOfAny("-|/\\") >= 0) return false;
        if (Color(text[..at]) is not { } left || Color(text[(at + 1)..]) is not { } right) return false;
        first = left; second = right; direction = text[at]; return true;
    }

    internal static string? Color(string text)
    {
        if (TryGradient(text, out string first, out string second, out char direction)) return first + direction + second;
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
        var name = text.AsSpan().TrimStart('#');
        if (name.Length == 1 && name.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0) return "#" + new string(name[0], 6);
        if (name.Length is 3 or 4 or 6 or 8 && name.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0) return "#" + name.ToString();
        string color = name.ToString().ToLowerInvariant();
        if (Palette(color) is >= 0 and var custom) return "#" + custom.ToString("x6", CultureInfo.InvariantCulture);
        if (color == "rebeccapurple") return "#663399";
        var known = System.Drawing.Color.FromName(color);
        return known.IsKnownColor && !known.IsSystemColor ? color : null;
    }
}
