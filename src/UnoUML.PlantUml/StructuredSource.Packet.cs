using System.Text.RegularExpressions;

namespace UnoUML.Syntax;

internal sealed record PacketSourceField(int First, int Last, string Label, SourceRow Row, SourceToken Range,
    SourceToken LabelToken, SourceToken LengthToken, bool Automatic, int Height, int Rotation)
{
    internal int Width => Last - First + 1;
}

internal sealed class PacketSource
{
    internal int Columns = 32, NodeHeight = 48, ScaleInterval = 1, InsertAt;
    internal bool RightToLeft, SameHeight = true;
    internal readonly List<PacketSourceField> Fields = [];
    private static readonly Regex FieldPattern = new(@"^(?<range>\*|\d{1,7}(?:\s*-\s*\d{1,7})?)\s*:?[ \t]+(?<label>.*?)(?:\s*\[(?<attributes>[^\[\]]*)\])?\s*;?$", RegexOptions.CultureInvariant);
    private static readonly Regex SettingPattern = new(@"^(colwidth|node_height|scale_interval|scale_direction|same_height)\s*=\s*([a-zA-Z0-9]+)\s*;?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AttributePattern = new(@"\G\s*(?<key>len|height|rotate)\s*=\s*(?<value>\d+)(?:\s*,\s*|\s+|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static PacketSource? Parse(string source)
    {
        var rows = StructuredText.Rows(source);
        if (rows == null) return null;
        var packet = new PacketSource();
        int stage = 0;
        bool brace = false;
        var settings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string line = row.Trimmed;
            if (line.Length == 0) continue;
            if (stage == 0) { if (!StructuredText.Header(line, "packetdiag")) return null; stage = 1; continue; }
            if (stage == 3) return null;
            if (line.Equals("@endpacketdiag", StringComparison.OrdinalIgnoreCase))
            { if (brace && stage != 2) return null; if (stage == 1) packet.InsertAt = row.Start; stage = 3; continue; }
            if (stage != 1) return null;
            if (line is "{" or "packetdiag {" or "packetdiag") { if (brace || packet.Fields.Count != 0) return null; brace = true; continue; }
            if (line == "}") { if (!brace) return null; packet.InsertAt = row.Start; stage = 2; continue; }
            var setting = SettingPattern.Match(line);
            if (setting.Success)
            {
                string key = setting.Groups[1].Value.ToLowerInvariant(), value = setting.Groups[2].Value;
                if (!settings.Add(key) || packet.Fields.Count > 0) return null;
                if (key == "scale_direction") { if (value is not ("ltr" or "rtl")) return null; packet.RightToLeft = value == "rtl"; }
                else if (key == "same_height") { if (!bool.TryParse(value, out packet.SameHeight)) return null; }
                else
                {
                    if (!StructuredText.Integer(value, out int number)) return null;
                    switch (key)
                    {
                        case "colwidth": if (number is < 1 or > 128) return null; packet.Columns = number; break;
                        case "node_height": if (number is < 16 or > 512) return null; packet.NodeHeight = number; break;
                        case "scale_interval": if (number is < 1 or > 128) return null; packet.ScaleInterval = number; break;
                    }
                }
                continue;
            }
            var field = FieldPattern.Match(line);
            if (!field.Success || packet.Fields.Count >= 2048) return null;
            int len = 1, height = 1, rotate = 0;
            SourceToken lenToken = default;
            var attrs = field.Groups["attributes"];
            if (attrs.Success)
            {
                int cursor = 0; var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (cursor < attrs.Length)
                {
                    if (string.IsNullOrWhiteSpace(attrs.Value[cursor..])) break;
                    var attr = AttributePattern.Match(attrs.Value, cursor);
                    if (!attr.Success || attr.Index != cursor || !keys.Add(attr.Groups["key"].Value) || !StructuredText.Integer(attr.Groups["value"].Value, out int number)) return null;
                    switch (attr.Groups["key"].Value.ToLowerInvariant())
                    {
                        case "len": if (number is < 1 or > 46080) return null; len = number; lenToken = new(row.ContentStart + attrs.Index + attr.Groups["value"].Index, attr.Groups["value"].Length); break;
                        case "height": if (number is < 1 or > 32) return null; height = number; break;
                        case "rotate": if (number is not (0 or 90 or 180 or 270)) return null; rotate = number; break;
                    }
                    cursor += attr.Length;
                }
            }
            string range = field.Groups["range"].Value;
            if (source.Substring(row.ContentStart + field.Groups["range"].Index, range.Length) != range) return null;
            bool automatic = range == "*";
            int first, last;
            if (automatic) { first = packet.Fields.Count == 0 ? 0 : packet.Fields[^1].Last + 1; last = first + len - 1; }
            else
            {
                string[] ends = range.Split('-');
                if (!StructuredText.Integer(ends[0].Trim(), out first)) return null;
                last = first + len - 1;
                if (ends.Length == 2 && !StructuredText.Integer(ends[1].Trim(), out last)) return null;
            }
            var label = field.Groups["label"];
            string labelValue = label.Value.TrimEnd();
            if (!StructuredText.PacketLabel(labelValue) || source.Substring(row.ContentStart + label.Index, labelValue.Length) != labelValue || first < 0 || last < first || last >= packet.Columns * 360 || packet.Fields.Count > 0 && first <= packet.Fields[^1].Last) return null;
            packet.Fields.Add(new(first, last, labelValue, row, StructuredText.Token(field.Groups["range"], row.ContentStart),
                new(row.ContentStart + label.Index, labelValue.Length), lenToken, automatic, height, rotate));
        }
        return stage == 3 ? packet : null;
    }
}
