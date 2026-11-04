using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>Network buses, shared server nodes and per-network interface addresses.</summary>
internal static partial class NetworkRenderer
{
    private sealed class Network(string id, int line)
    {
        public string Id { get; } = id;
        public string CanvasId { get; } = CanvasIdentity.Create("network-bus", id);
        public int Line { get; } = line;
        public string Caption = id, Address = "", Color = "#aab6ce";
        public int AddressLine = line;
        public HashSet<string> Hosts { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Host(string id, int first, int line)
    {
        public string Id { get; } = id;
        public string CanvasId { get; } = CanvasIdentity.Create("network-host", id);
        public int First { get; } = first;
        public int Line { get; } = line;
        public int Last = first, Slot, Center, Width;
        public string Caption = id, Color = "#ebedf5", Shape = "rectangle";
        public List<(int Network, string Address, int Line)> Interfaces { get; } = [];
    }

    private const string Id = @"[\p{L}\p{N}_][\p{L}\p{N}_.-]*";
    [GeneratedRegex("^network\\s+(" + Id + @")\s*\{$", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkHeader();
    [GeneratedRegex("^(" + Id + @")(?:\s*\[([^\]]*)\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostHeader();
    [GeneratedRegex("""\G\s*(address|description|shape|color)\s*=\s*(?:"([^"\\]*)"|([\w#.-]+))\s*(,|$)""", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    internal static string? TryRender(string source)
    {
        if (source.Length > 512000) return null;
        var networks = new List<Network>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new Dictionary<string, Host>(StringComparer.Ordinal);
        Network? current = null;
        int stage = 0, line = -1, ports = 0;
        string title = "";
        foreach (var row in source.AsSpan().EnumerateLines())
        {
            line++;
            var span = row.Trim();
            if (span.IsEmpty || span[0] == '\'' || span.StartsWith("//", StringComparison.Ordinal)) continue;
            if (span.Length > 2500) return null;
            if (stage == 0)
            {
                if (!span.SequenceEqual("@startnwdiag")) return null;
                stage = 1; continue;
            }
            if (stage == 1)
            {
                if (span.StartsWith("title ", StringComparison.Ordinal) && title.Length == 0)
                {
                    title = span[6..].ToString(); if (!Plain(title)) return null; continue;
                }
                if (!span.SequenceEqual("nwdiag {")) return null;
                stage = 2; continue;
            }
            if (stage == 3 && span.SequenceEqual("@endnwdiag")) { stage = 4; continue; }
            if (stage != 2) return null;
            if (span.EndsWith(";")) span = span[..^1].TrimEnd();
            if (span.SequenceEqual("}"))
            {
                if (current == null) stage = 3; else current = null;
                continue;
            }
            string text = span.ToString();
            if (current == null)
            {
                var start = NetworkHeader().Match(text);
                if (!start.Success || networks.Count >= 220 || !names.Add(start.Groups[1].Value)) return null;
                current = new Network(start.Groups[1].Value, line); networks.Add(current);
                continue;
            }
            var hostMatch = HostHeader().Match(text);
            if (!hostMatch.Success)
            {
                var props = Attributes(text);
                if (props is not { Count: 1 }) return null;
                var (key, val) = props.Single();
                switch (key)
                {
                    case "address": current.Address = val; current.AddressLine = line; break;
                    case "description": current.Caption = val; break;
                    case "color" when IsColor(val): current.Color = val; break;
                    default: return null;
                }
                continue;
            }
            string id = hostMatch.Groups[1].Value;
            if (!current.Hosts.Add(id) || ++ports > 4096 || id is "network" or "group") return null;
            if (!hosts.TryGetValue(id, out var host))
            {
                if (hosts.Count >= 1024) return null;
                hosts[id] = host = new Host(id, networks.Count - 1, line);
            }
            string address = "";
            if (hostMatch.Groups[2].Success)
            {
                var props = Attributes(hostMatch.Groups[2].Value);
                if (props == null) return null;
                foreach (var (key, val) in props)
                    switch (key)
                    {
                        case "address": address = val; break;
                        case "description": host.Caption = val; break;
                        case "color" when IsColor(val): host.Color = val; break;
                        case "shape" when val is "rectangle" or "node" or "database" or "folder" or "artifact": host.Shape = val; break;
                        default: return null;
                    }
            }
            host.Last = networks.Count - 1;
            host.Interfaces.Add((host.Last, address, line));
        }
        if (stage != 4 || networks.Count == 0) return null;
        return Draw(title, networks, hosts.Values.ToList());
    }

    private static Dictionary<string, string>? Attributes(string source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int pos = 0;
        while (pos < source.Length)
        {
            var match = Attribute().Match(source, pos);
            if (!match.Success || match.Index != pos) return null;
            string value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (!Plain(value) || !result.TryAdd(match.Groups[1].Value, value)) return null;
            pos += match.Length;
            if (match.Groups[4].Value == "," && pos >= source.Length) return null;
        }
        return result;
    }

    private static bool Plain(string text) => text.Length <= 450 && SvgText.IsPlain(text) && !text.Any(char.IsControl);
    private static bool IsColor(string color) => color.Length is 4 or 7 && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static string? Draw(string title, List<Network> networks, List<Host> hosts)
    {
        const int pitch = 134, margin = 18;
        int railStart = margin + Math.Max(60, networks.Max(n => Math.Max(SvgText.Measure(n.Caption), SvgText.Measure(n.Address, 12)))) + 18;
        var active = new PriorityQueue<int, (int Last, int Slot)>();
        var available = new PriorityQueue<int, int>();
        var widths = new List<int>();
        // A server retains its column across every bus it spans. Interval partitioning reuses
        // columns after the last attachment and minimizes the number of required columns.
        foreach (var host in hosts)
        {
            while (active.TryPeek(out int expired, out var key) && key.Last < host.First)
            {
                active.Dequeue(); available.Enqueue(expired, expired);
            }
            if (!available.TryDequeue(out int slot, out _)) { slot = widths.Count; widths.Add(0); }
            host.Slot = slot; active.Enqueue(slot, (host.Last, slot));
            host.Width = Math.Max(56, SvgText.Measure(host.Caption) + 24);
            int addresses = host.Interfaces.Max(p => SvgText.Measure(p.Address, 12));
            widths[slot] = Math.Max(widths[slot], Math.Max(host.Width, addresses + 18) + 28);
        }
        var centers = new int[widths.Count];
        int railEnd = railStart;
        for (int i = 0; i < widths.Count; i++)
        {
            centers[i] = railEnd + widths[i] / 2; railEnd += widths[i];
        }
        railEnd = Math.Max(railEnd, railStart + 70);
        foreach (var h in hosts) h.Center = centers[h.Slot];
        int top = title.Length == 0 ? 26 : 64, w = Math.Max(railEnd + margin, SvgText.Measure(title, 19) + margin * 2), height = top + (networks.Count - 1) * pitch + 112;
        if (w > 32768 || height > 32768) return null;
        var s = new StringBuilder(hosts.Count * 350 + networks.Count * 220);
        s.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{height}\" viewBox=\"0 0 {w} {height}\" font-family=\"Arial, sans-serif\">");
        void Text(int x, int y, string label, int size, string anchor = "start", bool bold = false) =>
            s.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" font-size=\"{size}\" fill=\"#282c3c\" text-anchor=\"{anchor}\"{(bold ? " font-weight=\"bold\"" : "")}>{SvgText.Escape(label)}</text>");
        if (title.Length > 0) Text(w / 2, 28, title, 19, "middle", true);
        for (int i = 0; i < networks.Count; i++)
        {
            var n = networks[i]; int y = top + i * pitch;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"network\" data-canvas-id=\"{n.CanvasId}\" data-qualified-name=\"{SvgText.Escape(n.Id)}\" data-source-line=\"{n.Line}\"><title>{SvgText.Escape(n.Caption)}</title><rect x=\"{railStart}\" y=\"{y - 3}\" width=\"{railEnd - railStart}\" height=\"6\" rx=\"3\" fill=\"{n.Color}\" stroke=\"#4b5363\"/></g>");
            // The caption travels with its bus without enlarging the rail's docking boundary.
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"network-caption\" data-canvas-decoration-for=\"{n.CanvasId}\" data-source-line=\"{n.Line}\">");
            Text(margin, y + 2, n.Caption, 14, bold: true);
            if (n.Address.Length > 0)
            {
                s.Append(CultureInfo.InvariantCulture, $"<g data-source-line=\"{n.AddressLine}\">"); Text(margin, y + 19, n.Address, 12); s.Append("</g>");
            }
            s.Append("</g>");
        }
        foreach (var host in hosts)
        {
            int x = host.Center, first = top + host.First * pitch, y = first + 52;
            foreach (var p in host.Interfaces)
            {
                int bus = top + p.Network * pitch;
                var network = networks[p.Network];
                string edgeId = CanvasIdentity.Create("network-interface", network.CanvasId, host.CanvasId);
                // Each interface is a complete bus-to-host connection. Shared lower segments
                // retain the original visible spine, while geometry edits can route them separately.
                int from = bus + (p.Network == host.First ? 3 : -3), to = p.Network == host.First ? y : y + 36;
                s.Append(CultureInfo.InvariantCulture, $"<g class=\"link\" data-style-name=\"arrow\" data-style-metrics=\"fontsize\" data-canvas-id=\"{edgeId}\" data-entity-1=\"{network.CanvasId}\" data-entity-2=\"{host.CanvasId}\"><path d=\"M{x} {from}V{to}\" fill=\"none\" stroke=\"#586177\"/>");
                s.Append(CultureInfo.InvariantCulture, $"<g class=\"interface\" data-source-line=\"{p.Line}\"><title>{SvgText.Escape(host.Id)}</title><circle cx=\"{x}\" cy=\"{bus}\" r=\"3\" fill=\"#535f78\"/>");
                s.Append("</g>");
                if (p.Address.Length > 0)
                {
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"interface-address\" data-source-line=\"{p.Line}\">");
                    Text(x, bus + (p.Network == host.First ? 22 : -9), p.Address, 12, "middle"); s.Append("</g>");
                }
                s.Append("</g>");
            }
            int left = x - host.Width / 2, right = left + host.Width, h = 36;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"entity\" data-canvas-id=\"{host.CanvasId}\" data-qualified-name=\"{SvgText.Escape(host.Id)}\" data-source-line=\"{host.Line}\"><title>{SvgText.Escape(host.Caption)}</title>");
            string? shape = host.Shape switch
            {
                "database" => FormattableString.Invariant($"M{left} {y + 7}C{left} {y - 3} {right} {y - 3} {right} {y + 7}V{y + h - 6}C{right} {y + h + 3} {left} {y + h + 3} {left} {y + h - 6}ZM{left} {y + 7}C{left} {y + 16} {right} {y + 16} {right} {y + 7}"),
                "node" => FormattableString.Invariant($"M{left} {y + 6}l7 -6H{right}V{y + h - 6}l-7 6H{left}ZM{left} {y + 6}H{right - 7}l7 -6m-7 6V{y + h}"),
                "artifact" => FormattableString.Invariant($"M{left} {y}H{right - 9}l9 9V{y + h}H{left}ZM{right - 9} {y}v9h9"),
                "folder" => FormattableString.Invariant($"M{left} {y}h18l6 5H{right}V{y + h}H{left}Z"),
                _ => null
            };
            if (shape != null) s.Append(CultureInfo.InvariantCulture, $"<path d=\"{shape}\" fill=\"{host.Color}\" stroke=\"#414857\"/>");
            else s.Append(CultureInfo.InvariantCulture, $"<rect x=\"{left}\" y=\"{y}\" width=\"{host.Width}\" height=\"{h}\" rx=\"2\" fill=\"{host.Color}\" stroke=\"#414857\"/>");
            Text(x, y + 23, host.Caption, 14, "middle"); s.Append("</g>");
        }
        return s.Append("</svg>").ToString();
    }
}
