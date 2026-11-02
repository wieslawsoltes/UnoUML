using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>Time-proportional tracks with sparse transitions and source-addressable wave segments.</summary>
internal static partial class TimingRenderer
{
    private const string Decimal = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)";
    private const string Identifier = @"[\p{L}_][\w.]*";
    private const string Name = """(?:"[^"\\]+"|[\p{L}_][\w.]*)""";
    private const RegexOptions Grammar = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    [GeneratedRegex(@"^(robust|concise|binary|analog|clock)\s+(" + Name + @")(?:\s+as\s+(" + Name + @"))?(?:\s+with\s+period\s+(" + Decimal + @")(?:\s+pulse\s+(" + Decimal + @"))?(?:\s+offset\s+(" + Decimal + @"))?)?$", Grammar)]
    private static partial Regex Declaration();
    [GeneratedRegex(@"^(" + Identifier + """)\s+is\s+("[^"\\]*"|[\p{L}\p{N}_.+\-]+)(?:\s*:\s*(.+))?$""", Grammar)]
    private static partial Regex Transition();
    [GeneratedRegex(@"^(" + Identifier + @")\s*->\s*(" + Identifier + @")(?:@(" + Decimal + @"))?\s*:\s*(.+)$", Grammar)]
    private static partial Regex Message();

    private sealed record Change(double Time, string State, double Value, string Annotation, int Line);
    private sealed record Link(string From, string To, double Start, double End, string Label, int Line);

    private sealed class Track(string kind, string id, string label, int line)
    {
        internal readonly string Kind = kind, Id = id, Label = label;
        internal readonly int Line = line;
        internal readonly List<Change> Changes = [];
        internal readonly Dictionary<string, int> States = new(StringComparer.Ordinal);
        internal int Top, Height;
        internal double Low, High, Period, Pulse, Offset;
        internal bool ChangedAt(double time) => Changes.Count != 0 && Changes[^1].Time == time;
    }

    private static bool Numeric(string text, out double value) => double.TryParse(text,
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && Math.Abs(value) <= 1e12;
    private static string Unquote(string token) => token[0] == '"' ? token[1..^1] : token;

    internal static string? TryRender(string source)
    {
        if (source.Length > 512_000) return null;
        var tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        var ticks = new SortedDictionary<double, int>();
        var links = new List<Link>();
        bool opened = false, closed = false, showAxis = true;
        int sourceLine = -1, titleLine = -1;
        string title = "";
        double now = 0;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            sourceLine++;
            string line = raw.Trim().ToString();
            if (line.Length > 4096) return null;
            if (line.Length == 0 || line[0] == '\'') continue;
            if (!opened) { if (line != "@startuml") return null; opened = true; continue; }
            if (closed) return null;
            if (line == "@enduml") { closed = true; continue; }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                if (titleLine >= 0 || !SvgText.IsPlain(title = line[6..].Trim())) return null;
                titleLine = sourceLine; continue;
            }
            if (line.Equals("hide time-axis", StringComparison.OrdinalIgnoreCase)) { showAxis = false; continue; }
            if (line[0] == '@')
            {
                var token = line[1..].Trim();
                if (!Numeric(token, out double value)) return null;
                now = token.StartsWith('+') ? now + value : value;
                if (Math.Abs(now) > 1e12 || ticks.Count > 4096) return null;
                ticks.TryAdd(now, sourceLine); continue;
            }

            var change = Transition().Match(line);
            if (change.Success)
            {
                if (!tracks.TryGetValue(change.Groups[1].Value, out var track) || track.Kind == "clock" || track.Changes.Count >= 4096 || track.ChangedAt(now)) return null;
                string state = Unquote(change.Groups[2].Value), annotation = change.Groups[3].Value;
                if (state.Length == 0 || !SvgText.IsPlain(state) || !SvgText.IsPlain(annotation)) return null;
                double value = 0;
                if (track.Kind == "analog" && !Numeric(state, out value)) return null;
                if (track.Kind == "binary")
                {
                    value = state.ToLowerInvariant() switch { "high" or "1" => 1, "low" or "0" => 0, _ => -1 };
                    if (value < 0) return null;
                }
                track.Changes.Add(new(now, state, value, annotation, sourceLine));
                if (track.Kind == "robust")
                {
                    track.States.TryAdd(state, track.States.Count);
                    if (track.States.Count > 256) return null;
                }
                ticks.TryAdd(now, sourceLine); continue;
            }
            var declaration = Declaration().Match(line);
            if (declaration.Success)
            {
                string kind = declaration.Groups[1].Value.ToLowerInvariant(), name = declaration.Groups[2].Value, alias = declaration.Groups[3].Value;
                if (alias.StartsWith('"') && !name.StartsWith('"')) (alias, name) = (name, alias);
                var track = new Track(kind, alias.Length == 0 ? Unquote(name) : Unquote(alias), Unquote(name), sourceLine);
                if (!SvgText.IsPlain(track.Label) || tracks.Count >= 128 || !tracks.TryAdd(track.Id, track)) return null;
                if (kind == "clock")
                {
                    if (!Numeric(declaration.Groups[4].Value, out track.Period) || track.Period <= 0) return null;
                    track.Pulse = track.Period / 2;
                    if (declaration.Groups[5].Success && (!Numeric(declaration.Groups[5].Value, out track.Pulse) || track.Pulse <= 0 || track.Pulse >= track.Period)) return null;
                    if (declaration.Groups[6].Success && !Numeric(declaration.Groups[6].Value, out track.Offset)) return null;
                }
                else if (declaration.Groups[4].Success) return null;
                continue;
            }
            var message = Message().Match(line);
            if (!message.Success || !tracks.ContainsKey(message.Groups[1].Value) || !tracks.ContainsKey(message.Groups[2].Value) || !SvgText.IsPlain(message.Groups[4].Value)) return null;
            var end = now;
            if (message.Groups[3].Success)
            {
                var value = message.Groups[3].Value;
                if (!Numeric(value, out end)) return null;
                if (value.StartsWith('+')) end += now;
            }
            links.Add(new(message.Groups[1].Value, message.Groups[2].Value, now, end, message.Groups[4].Value, sourceLine));
            ticks.TryAdd(now, sourceLine); ticks.TryAdd(end, sourceLine);
        }
        if (!closed || tracks.Count == 0) return null;
        ticks.TryAdd(0, -1);
        return Render(tracks, ticks, links, title, titleLine, showAxis);
    }

    private static string? Render(Dictionary<string, Track> tracks, SortedDictionary<double, int> ticks, List<Link> links,
        string title, int titleLine, bool showAxis)
    {
        int top = title.Length == 0 ? 12 : 42, height = top, gutter = 48;
        double first = ticks.First().Key, last = ticks.Last().Key;
        if (last == first) last += tracks.Values.Where(t => t.Kind == "clock").Select(t => t.Period * 2).DefaultIfEmpty(1).Max();
        int plotWidth = Math.Max(300, Math.Min(16000, ticks.Count * 68));
        foreach (var t in tracks.Values)
        {
            t.Changes.Sort((a, b) => a.Time.CompareTo(b.Time));
            for (int i = 1; i < t.Changes.Count; i++) if (t.Changes[i - 1].Time == t.Changes[i].Time) return null;
            if (t.Kind == "clock" && (last - first) / t.Period > 2048) return null;
            if (t.Kind == "analog" && t.Changes.Count > 0)
            {
                t.Low = t.Changes.Min(c => c.Value); t.High = t.Changes.Max(c => c.Value);
            }
            t.Top = height;
            int spaceForLabels = t.States.Keys.Select(label => SvgText.Measure(label, 12)).DefaultIfEmpty(0).Max();
            gutter = Math.Max(gutter, Math.Max(SvgText.Measure(t.Label) + 24, spaceForLabels + 28));
            t.Height = t.Kind switch { "robust" => Math.Max(68, 39 + t.States.Count * 18), "analog" => 86, _ => 60 };
            height += t.Height;
        }
        int width = Math.Max(gutter + plotWidth + 45, SvgText.Measure(title, 18) + 28);
        if (width > 32768 || height > 32700) return null;
        double X(double time) => gutter + (time - first) / (last - first) * plotWidth;
        var s = new StringBuilder(512 + ticks.Count * 70 + tracks.Count * 200);
        height += showAxis ? 34 : 12;
        s.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\">");
        if (titleLine >= 0)
        {
            Group(s, "title", titleLine); Text(s, width / 2, 25, title, "middle", 18); s.Append("</g>");
        }
        int axisY = height - (showAxis ? 31 : 10);
        foreach (var (time, row) in ticks)
        {
            double x = X(time);
            s.Append($"<line x1=\"{F(x)}\" x2=\"{F(x)}\" y1=\"{top}\" y2=\"{axisY}\" stroke=\"#B8B8B8\" stroke-dasharray=\"2 3\"/>");
        }
        foreach (var t in tracks.Values)
        {
            Group(s, "timing-track", t.Line, t.Id);
            s.Append($"<line x1=\"8\" x2=\"{width-10}\" y1=\"{t.Top}\" y2=\"{t.Top}\" stroke=\"#999\"/>");
            Text(s, 10, t.Top + 16, t.Label);
            foreach (var (state, _) in t.States) Text(s, gutter - 9, Level(t, state, 0) + 4, state, "end", 12);
            if (t.Kind == "clock") DrawClock(s, t, first, last, X);
            s.Append("</g>");
            if (t.Kind == "clock") continue;
            if (t.Kind == "binary" && (t.Changes.Count == 0 || t.Changes[0].Time > first))
            {
                Group(s, "timing-state", t.Line);
                Stroke(s, X(first), Level(t, "", 0), t.Changes.Count == 0 ? X(last) + 16 : X(t.Changes[0].Time), Level(t, "", 0));
                s.Append("</g>");
            }
            for (int i = 0; i < t.Changes.Count; i++)
            {
                var c = t.Changes[i]; var next = i + 1 < t.Changes.Count ? t.Changes[i + 1] : null;
                double x = X(c.Time), endX = next == null ? X(last) + 16 : X(next.Time), y = Level(t, c.State, c.Value);
                Group(s, "timing-state", c.Line);
                if (t.Kind == "concise")
                {
                    double notch = Math.Min(4, (endX - x) / 3);
                    s.Append($"<path d=\"M{F(x)} {F(y)}l{F(notch)} -9H{F(endX-notch)}L{F(endX)} {F(y)}l-{F(notch)} 9H{F(x+notch)}z\" fill=\"#EEEEEE\" stroke=\"#333\"/>");
                    Text(s, (x + endX) / 2, y + 4, c.State, "middle", 12);
                }
                else
                {
                    double nextY = next == null ? y : Level(t, next.State, next.Value);
                    Stroke(s, x, y, endX, t.Kind == "analog" ? nextY : y);
                    if (t.Kind != "analog" && next != null) Stroke(s, endX, y, endX, nextY);
                }
                if (c.Annotation.Length > 0) Text(s, x + 3, t.Top + 16, c.Annotation, size: 12);
                s.Append("</g>");
            }
        }
        foreach (var link in links)
        {
            var a = tracks[link.From]; var b = tracks[link.To];
            var x1 = X(link.Start); var x2 = X(link.End);
            double y1 = SignalY(a, link.Start), y2 = SignalY(b, link.End);
            double angle = Math.Atan2(y2-y1, x2-x1);
            Group(s, "timing-message", link.Line);
            Stroke(s, x1,y1,x2,y2);
            s.Append($"<path d=\"M{F(x2)} {F(y2)}l{F(-8*Math.Cos(angle-.45))} {F(-8*Math.Sin(angle-.45))}L{F(x2-8*Math.Cos(angle+.45))} {F(y2-8*Math.Sin(angle+.45))}z\" fill=\"#333\"/>");
            Text(s,(x1+x2)/2+5,(y1+y2)/2,link.Label,size:12);
            s.Append("</g>");
        }
        if (showAxis)
        {
            Stroke(s, gutter, axisY, X(last) + 16, axisY);
            double lastRight = double.NegativeInfinity;
            foreach (var (time, row) in ticks)
            {
                string label = time.ToString("G12", CultureInfo.InvariantCulture);
                double x = X(time), halfWidth = SvgText.Measure(label, 12) / 2d;
                if (row >= 0) Group(s, "time", row);
                Stroke(s, x, axisY, x, axisY + 4);
                if (x - halfWidth > lastRight) { Text(s, x, axisY + 19, label, "middle", 12); lastRight = x + halfWidth + 4; }
                if (row >= 0) s.Append("</g>");
            }
        }
        return s.Append("</svg>").ToString();
    }

    private static double SignalY(Track t, double time)
    {
        if (t.Kind == "clock") return Level(t, "", Phase(time, t) < t.Pulse ? 1 : 0);
        var events = t.Changes;
        int low = 0, high = events.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (events[mid].Time <= time) low = mid + 1; else high = mid;
        }
        if (low == 0) return t.Top + t.Height - 12;
        var last = events[low - 1];
        double value = last.Value;
        if (t.Kind == "analog" && low < events.Count)
        {
            var next = events[low];
            value += (next.Value - value) * (time - last.Time) / (next.Time - last.Time);
        }
        return Level(t, last.State, value);
    }

    private static double Level(Track t, string state, double value) => t.Kind switch
    {
        "robust" => t.Top + t.Height - 12 - 18 * t.States[state],
        "concise" => t.Top + 36,
        "analog" => t.Top + (t.High > t.Low ? 72 - 45 * (value - t.Low) / (t.High - t.Low) : 46),
        _ => t.Top + 46 - value * 20
    };

    private static double Phase(double time, Track t) => ((time - t.Offset) % t.Period + t.Period) % t.Period;
    private static void DrawClock(StringBuilder s, Track t, double first, double last, Func<double, double> xAt)
    {
        double phase = Phase(first, t);
        bool high = phase < t.Pulse;
        double y = Level(t, "", high ? 1 : 0);
        s.Append($"<path d=\"M{F(xAt(first))} {F(y)}");
        double edge = first + (high ? t.Pulse - phase : t.Period - phase);
        for (int n = 0; edge <= last && n <= 4098; n++)
        {
            high = !high;
            s.Append($"H{F(xAt(edge))}V{F(Level(t, "", high ? 1 : 0))}");
            edge += high ? t.Pulse : t.Period - t.Pulse;
        }
        s.Append($"H{F(xAt(last) + 16)}\" fill=\"none\" stroke=\"#333\" stroke-width=\"1.5\"/>");
    }

    private static string F(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
    private static void Group(StringBuilder s, string kind, int line, string? alias = null)
    {
        s.Append($"<g class=\"{kind}\" data-source-line=\"{line}\"");
        if (alias != null) s.Append($" data-qualified-name=\"{SvgText.Escape(alias)}\"");
        s.Append('>');
    }
    private static void Stroke(StringBuilder s, double x, double y, double endX, double endY) =>
        s.Append($"<line x1=\"{F(x)}\" y1=\"{F(y)}\" x2=\"{F(endX)}\" y2=\"{F(endY)}\" stroke=\"#333\" stroke-width=\"1.5\"/>");
    private static void Text(StringBuilder s, double x, double y, string text, string anchor = "start", int size = 14) =>
        s.Append($"<text x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\" font-size=\"{size}\">{SvgText.Escape(text)}</text>");
}
