using System.Globalization;

namespace UnoUML.PlantUml;

internal static class PacketRenderer
{
    private readonly record struct Field(int First, int Last, string Name, int Line);

    internal static string? TryRender(string source)
    {
        var fields = new List<Field>();
        int columns = 32, stage = 0, line = -1;
        bool configured = false;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            line++;
            var s = raw.Trim();
            if (s.IsEmpty || s.StartsWith("'")) continue;
            if (stage == 0 && s.SequenceEqual("@startpacketdiag")) { stage = 1; continue; }
            if (stage == 1 && s.SequenceEqual("{")) { stage = 2; continue; }
            if (stage == 2 && s.SequenceEqual("}")) { stage = 3; continue; }
            if (stage == 3 && s.SequenceEqual("@endpacketdiag")) { stage = 4; continue; }
            if (stage != 2) return null;
            if (s.StartsWith("colwidth"))
            {
                var setting = s[8..].TrimStart();
                if (configured || fields.Count != 0 || setting.Length == 0 || setting[0] != '=' ||
                    !Number(setting[1..], out columns) || columns is < 1 or > 128) return null;
                configured = true;
                continue;
            }
            int colon = s.IndexOf(':');
            if (colon <= 0 || fields.Count >= 2048) return null;
            var range = s[..colon].Trim();
            int dash = range.IndexOf('-');
            if (!Number(dash >= 0 ? range[..dash] : range, out int first)) return null;
            int last = first;
            if (dash >= 0 && !Number(range[(dash+1)..], out last)) return null;
            if (last < first || last >= columns * 360 || fields.Count > 0 && first <= fields[^1].Last) return null;
            string label = s[(colon+1)..].Trim().ToString();
            if (label.Length is 0 or > 256 || !SvgText.IsPlain(label) || label.Contains('[')) return null;
            fields.Add(new(first, last, label, line));
        }
        if (stage != 4 || fields.Count == 0) return null;
        int cell = 23;
        // Every field label is legible even when it spans a single bit.
        foreach (var f in fields)
        {
            int visible = Math.Min(f.Last - f.First + 1, columns - f.First % columns);
            cell = Math.Max(cell, (SvgText.Measure(f.Name) + 12 + visible - 1) / visible);
        }
        int width = columns * cell + 32;
        if (width > 32768) return null;
        var svg = TreeDiagram.Canvas(width, (fields[^1].Last / columns + 1) * 48 + 38);
        for (int bit = 0; bit < columns; bit++)
            svg.Append($"<text x=\"{16+bit*cell+cell/2}\" y=\"19\" text-anchor=\"middle\" font-size=\"11\">{bit}</text>");
        foreach (var f in fields)
        {
            svg.Append($"<g data-source-line=\"{f.Line}\"><title>{SvgText.Escape(f.Name)}</title>");
            for (int start = f.First; start <= f.Last;)
            {
                int row = start / columns, stop = Math.Min(f.Last + 1, (row + 1) * columns);
                int x = 16 + start % columns * cell, y = 26 + row * 48, w = (stop - start) * cell;
                svg.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"48\" fill=\"#f5f5fa\" stroke=\"#68687d\"/>");
                if (start == f.First) svg.Append($"<text x=\"{x+w/2}\" y=\"{y+29}\" text-anchor=\"middle\">{SvgText.Escape(f.Name)}</text>");
                start = stop;
            }
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }

    private static bool Number(ReadOnlySpan<char> text, out int n) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n);
}
