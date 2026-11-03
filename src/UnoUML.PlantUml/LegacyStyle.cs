namespace UnoUML.PlantUml;

internal static class LegacyStyle
{
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.Ordinal)
    {
        ["classheader"] = "class header",
        ["classattribute"] = "class body",
        ["classmember"] = "class body",
        ["classstereotype"] = "class stereotype",
        ["objectattribute"] = "object body",
        ["objectmember"] = "object body",
        ["objectstereotype"] = "object stereotype",
        ["stateattribute"] = "state statebody",
        ["usestereotype"] = "usecase stereotype",
        ["usecasestereotype"] = "usecase stereotype",
        ["circledcharacter"] = "spot",
        ["stereotypea"] = "spot spotabstractclass",
        ["stereotypec"] = "spot spotclass",
        ["stereotypee"] = "spot spotenum",
        ["stereotypei"] = "spot spotinterface",
        ["stereotypen"] = "spot spotannotation",
        ["stereotyper"] = "spot spotrecord",
        ["stereotyped"] = "spot spotdataclass",
        ["iconprivate"] = "visibilityicon private",
        ["iconprotected"] = "visibilityicon protected",
        ["iconpublic"] = "visibilityicon public",
        ["iconpackage"] = "visibilityicon package",
        ["statediamond"] = "statediagram diamond",
        ["activitydiamond"] = "diamond",
        ["swimlanetitle"] = "swimlane",
        ["divider"] = "separator",
        ["partition"] = "composite",
        ["default"] = "root",
        ["hyperlink"] = "root"
    };
    private static readonly (string Suffix, string Property)[] Suffixes =
    [
        ("backgroundcolor", "backgroundcolor"), ("borderroundcorner", "roundcorner"), ("borderthickness", "linethickness"),
        ("bordercolor", "linecolor"), ("borderstyle", "linestyle"), ("fontcolor", "fontcolor"), ("fontsize", "fontsize"),
        ("fontname", "fontname"), ("fontstyle", "fontstyle"), ("fontweight", "fontweight"), ("roundcorner", "roundcorner"),
        ("diagonalcorner", "diagonalcorner"), ("shadowing", "shadowing"), ("maximumwidth", "maximumwidth"),
        ("minimumwidth", "minimumwidth"), ("linestyle", "linestyle"), ("thickness", "linethickness"),
        ("headcolor", "headcolor"), ("padding", "padding"), ("margin", "margin"), ("textalignment", "horizontalalignment"),
        ("color", "linecolor")
    ];

    internal static bool TryMap(string key, out string names, out string property)
    {
        names = property = "";
        key = key.ToLowerInvariant();
        if (key.StartsWith("sequence", StringComparison.Ordinal))
        {
            key = key[8..];
            if (key == "groupbackgroundcolor") { names = "groupheader"; property = "backgroundcolor"; return true; }
            if (key == "lifelinebackgroundcolor") { names = "activationbox"; property = "backgroundcolor"; return true; }
            if (key is "fontname" or "fontsize" or "fontcolor" or "fontstyle")
            { names = "sequencediagram"; property = key; return true; }
        }
        switch (key)
        {
            case "swimlanewraptitlewidth": names = "swimlane"; property = "maximumwidth"; return true;
            case "packagetitlealignment": names = "package"; property = "horizontalalignment"; return true;
            case "referencealignment": names = "reference"; property = "horizontalalignment"; return true;
            case "fontcolor": names = "root"; property = "fontcolor"; return true;
            case "stateendcolor": names = "statediagram circle end"; property = "linecolor"; return true;
            case "statestartcolor": names = "statediagram circle start"; property = "backgroundcolor"; return true;
            case "backgroundcolor": names = "document"; property = "backgroundcolor"; return true;
            case "roundcorner": names = "root"; property = "roundcorner"; return true;
            case "shadowing": names = "root"; property = "shadowing"; return true;
            case "defaulttextalignment": names = "root"; property = "horizontalalignment"; return true;
            case "maxmessagesize": names = "arrow"; property = "maximumwidth"; return true;
            case "wrapwidth": names = "element"; property = "maximumwidth"; return true;
            case "minclasswidth": names = "class"; property = "minimumwidth"; return true;
            case "activitystartcolor": names = "circle start"; property = "backgroundcolor"; return true;
            case "activityendcolor": names = "circle end"; property = "linecolor"; return true;
            case "activitystopcolor": names = "circle stop"; property = "linecolor"; return true;
            case "activitybarcolor": names = "activitybar"; property = "backgroundcolor"; return true;
            case "hyperlinkcolor": names = "root"; property = "hyperlinkcolor"; return true;
            case "hyperlinkunderline": names = "element"; property = "hyperlinkunderlinethickness"; return true;
            case "stereotypealignment": names = "stereotype"; property = "horizontalalignment"; return true;
            case "messagealign":
            case "messagealignment": names = "arrow"; property = "horizontalalignment"; return true;
        }
        foreach (var (suffix, mapped) in Suffixes)
        {
            if (!key.EndsWith(suffix, StringComparison.Ordinal)) continue;
            string prefix = key[..^suffix.Length];
            property = mapped;
            names = Prefixes.GetValueOrDefault(prefix, prefix);
            if (names == prefix && prefix.Length > 10 && prefix.EndsWith("stereotype", StringComparison.Ordinal)) names = prefix[..^10] + " stereotype";
            if (prefix.EndsWith("arrow", StringComparison.Ordinal) && prefix.Length > 5 && StyleSheet.KnownName(prefix[..^5]))
                names = prefix[..^5] switch
                {
                    "class" => "classdiagram arrow",
                    "object" => "objectdiagram arrow",
                    "activity" => "activitydiagram arrow",
                    "state" => "statediagram arrow",
                    "usecase" => "componentdiagram arrow",
                    _ => prefix[..^5] + " arrow"
                };
            if (prefix == "groupbody") names = "group";
            if (names.Length == 0) return false;
            if (names == "class" && property.StartsWith("font", StringComparison.Ordinal)) names = "class header";
            if (names == "root" && key == "defaultfontsize") names = "element";
            if (names is "header" or "footer" or "caption" or "title") names = "document " + names;
            return names.Split(' ').All(StyleSheet.KnownName);
        }
        return false;
    }

    internal static string? AdditionalNames(string key) => key switch
    {
        "sequencereferencebordercolor" or "sequencereferencefontname" or "sequencereferencefontsize" or
        "sequencereferencefontstyle" or "sequencereferencefontcolor" => "referenceheader",
        "sequencegroupbordercolor" => "groupheader",
        "packagebackgroundcolor" or "packagebordercolor" => "group",
        _ => null
    };
}
