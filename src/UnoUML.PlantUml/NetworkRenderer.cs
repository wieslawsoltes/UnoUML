using System.Globalization;
using System.Text;
using UnoUML.Syntax;

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
        public SourceToken CaptionToken, AddressToken;
        public string CaptionKind = "text", AddressKind = "quoted";
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
        public SourceToken CaptionToken;
        public string CaptionKind = "text";
        public List<(int Network, string Address, int Line, SourceToken Token, string Kind)> Interfaces { get; } = [];
    }

    internal static string? TryRender(string source)
    {
        if (NetworkSource.Parse(source) is not { } syntax || !Plain(syntax.Title)) return null;
        var networks = new List<Network>();
        var hosts = new Dictionary<string, Host>(StringComparer.Ordinal);
        foreach (var bus in syntax.Networks)
        {
            var current = new Network(bus.Alias, bus.Line)
            {
                Caption = bus.Property("description") ?? bus.Alias,
                Address = bus.Property("address") ?? "",
                Color = bus.Property("color") is { } color ? ManagedSkin.Color(color) ?? "" : "#aab6ce",
                CaptionToken = bus.Properties.LastOrDefault(a => a.Name == "description")?.ValueToken ?? bus.AliasToken,
                AddressToken = bus.Properties.LastOrDefault(a => a.Name == "address")?.ValueToken ?? default
            };
            if (!Plain(current.Caption) || !Plain(current.Address) || current.Color.Length == 0) return null;
            current.CaptionKind = Kind(current.CaptionToken); current.AddressKind = Kind(current.AddressToken);
            var addressProperty = bus.Properties.LastOrDefault(a => a.Name == "address");
            if (addressProperty != null) current.AddressLine = syntax.Rows.Last(r => r.Start <= addressProperty.ValueToken.Start).Index;
            networks.Add(current);
            foreach (var connection in bus.Interfaces)
            {
                string alias = connection.Host.Alias;
                if (!hosts.TryGetValue(alias, out var host))
                {
                    var definition = connection.Host;
                    host = new Host(alias, networks.Count - 1, connection.Line)
                    {
                        Caption = definition.Property("description") ?? alias,
                        Color = definition.Property("color") is { } paint ? ManagedSkin.Color(paint) ?? "" : "#ebedf5",
                        Shape = definition.Property("shape") ?? "rectangle",
                        CaptionToken = definition.Interfaces.SelectMany(i => i.Attributes).LastOrDefault(a => a.Name == "description")?.ValueToken ?? connection.AliasToken
                    };
                    host.CaptionKind = Kind(host.CaptionToken);
                    if (!Plain(host.Caption) || host.Color.Length == 0 || host.Shape is not ("rectangle" or "node" or "database" or "folder" or "artifact")) return null;
                    hosts.Add(alias, host);
                }
                string address = connection.Address ?? "";
                if (!Plain(address)) return null;
                var token = connection.Attributes.FirstOrDefault(a => a.Name == "address")?.ValueToken ?? default;
                current.Hosts.Add(alias); host.Last = networks.Count - 1;
                host.Interfaces.Add((host.Last, address, connection.Line, token, Kind(token)));
            }
        }
        if (networks.Count == 0) return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"240\" height=\"64\" viewBox=\"0 0 240 64\"/>";
        return Draw(syntax.Title, networks, hosts.Values.ToList());
        string Kind(SourceToken token) => token.Length > 0 && source[token.Start] == '"' ? "quoted" : "text";
    }

    private static bool Plain(string text) => text.Length <= 450 && SvgText.IsPlain(text) && !text.Any(char.IsControl);

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
        void Text(int x, int y, string label, int size, string anchor = "start", bool bold = false, SourceToken token = default, string kind = "text")
        {
            if (token.Length > 0) s.Append(CultureInfo.InvariantCulture, $"<g data-edit-start=\"{token.Start}\" data-edit-length=\"{token.Length}\" data-edit-kind=\"{kind}\" data-edit-role=\"label\">");
            s.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" font-size=\"{size}\" fill=\"#282c3c\" text-anchor=\"{anchor}\"{(bold ? " font-weight=\"bold\"" : "")}>{SvgText.Escape(label)}</text>");
            if (token.Length > 0) s.Append("</g>");
        }
        if (title.Length > 0) Text(w / 2, 28, title, 19, "middle", true);
        for (int i = 0; i < networks.Count; i++)
        {
            var n = networks[i]; int y = top + i * pitch;
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"network\" data-canvas-id=\"{n.CanvasId}\" data-qualified-name=\"{SvgText.Escape(n.Id)}\" data-source-line=\"{n.Line}\"><title>{SvgText.Escape(n.Caption)}</title><rect x=\"{railStart}\" y=\"{y - 3}\" width=\"{railEnd - railStart}\" height=\"6\" rx=\"3\" fill=\"{n.Color}\" stroke=\"#4b5363\"/></g>");
            // The caption travels with its bus without enlarging the rail's docking boundary.
            s.Append(CultureInfo.InvariantCulture, $"<g class=\"network-caption\" data-canvas-decoration-for=\"{n.CanvasId}\" data-source-line=\"{n.Line}\">");
            Text(margin, y + 2, n.Caption, 14, bold: true, token: n.CaptionToken, kind: n.CaptionKind);
            if (n.Address.Length > 0)
            {
                s.Append(CultureInfo.InvariantCulture, $"<g data-source-line=\"{n.AddressLine}\">"); Text(margin, y + 19, n.Address, 12, token: n.AddressToken, kind: n.AddressKind); s.Append("</g>");
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
                s.Append(CultureInfo.InvariantCulture, $"<g class=\"interface\" data-canvas-fraction=\"0\" data-source-line=\"{p.Line}\"><title>{SvgText.Escape(host.Id)}</title><circle cx=\"{x}\" cy=\"{bus}\" r=\"3\" fill=\"#535f78\"/>");
                s.Append("</g>");
                if (p.Address.Length > 0)
                {
                    s.Append(CultureInfo.InvariantCulture, $"<g class=\"interface-address\" data-canvas-fraction=\"0\" data-source-line=\"{p.Line}\">");
                    Text(x, bus + (p.Network == host.First ? 22 : -9), p.Address, 12, "middle", token: p.Token, kind: p.Kind); s.Append("</g>");
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
            Text(x, y + 23, host.Caption, 14, "middle", token: host.CaptionToken, kind: host.CaptionKind); s.Append("</g>");
        }
        return s.Append("</svg>").ToString();
    }
}
