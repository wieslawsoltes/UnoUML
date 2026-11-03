using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

/// <summary>Time-proportional tracks with sparse transitions and source-addressable wave segments.</summary>
internal static partial class TimingRenderer
{
    private const string Decimal = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)";
    private const string Identifier = @"[\p{L}_][\w.]*";
    private const string Quoted = "\"(?:\\\\.|[^\"\\\\])*\"";
    private const string Name = "(?:" + Quoted + "|" + Identifier + ")";
    private const RegexOptions Grammar = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    [GeneratedRegex(@"^(robust|concise|binary|analog|clock)\s+(" + Name + @")(?:\s+as\s+(" + Name + @"))?(?:\s+<<(?<stereotype>[^<>\r\n]+)>>)?(?:\s+with\s+period\s+(" + Decimal + @")(?:\s+pulse\s+(" + Decimal + @"))?(?:\s+offset\s+(" + Decimal + @"))?)?(?:\s+<<(?<stereotype>[^<>\r\n]+)>>)?$", Grammar)]
    private static partial Regex Declaration();
    [GeneratedRegex(@"^(" + Identifier + @")\s+is\s+(" + Quoted + @"|[\p{L}\p{N}_.+\-]+)(?:\s*:\s*(.+))?$", Grammar)]
    private static partial Regex Transition();
    [GeneratedRegex(@"^(" + Identifier + @")\s*->\s*(" + Identifier + @")(?:@(" + Decimal + @"))?\s*:\s*(.+)$", Grammar)]
    private static partial Regex Message();

    private readonly record struct TextToken(int Start, int Length, string Kind);
    private sealed record Change(double Time, string State, double Value, string Annotation, int Line, TextToken StateToken, TextToken AnnotationToken)
    {
        internal StyleLabel StateLabel = null!, AnnotationLabel = null!;
    }
    private sealed record Link(string From, string To, double Start, double End, string Label, int Line, TextToken Token)
    {
        internal StyleLabel Caption = null!;
        internal int CaptionTop;
        internal double CaptionLeft;
    }

    private sealed record Legend(bool Above, string Align, int Line)
    {
        internal readonly List<string> Rows = [];
        internal readonly List<TextToken> Tokens = [];
        internal StyleLabel[] Labels = [];
        internal int Width => Math.Max(48, Labels.Max(label => label.Width) + 18);
        internal int Height => Labels.Sum(label => label.Height + 4) + 10;
    }

    private static Legend? ParseLegend(string line, int index)
    {
        var tokens = line.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 1;
        bool above = i < tokens.Length && tokens[i] == "top";
        if (above || (i < tokens.Length && tokens[i] == "bottom")) i++;
        string align = i < tokens.Length ? tokens[i++] : "center";
        return i == tokens.Length && align is ("left" or "center" or "right") ? new(above, align, index) : null;
    }

    private sealed class Track(string kind, string id, string label, int line)
    {
        internal readonly string Kind = kind, Id = id, Label = label;
        internal readonly int Line = line;
        internal readonly List<Change> Changes = [];
        internal readonly List<Link> Messages = [];
        internal readonly Dictionary<string, int> States = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, StyleLabel> StateLabels = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, Change> StateSources = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, double> StateOffsets = new(StringComparer.Ordinal);
        internal StyleLabel Caption = null!, LowLabel = null!, HighLabel = null!;
        internal string Stereotype = "";
        internal TextToken? CaptionToken;
        internal int Top, Height, HeaderHeight, SignalTop, SignalHeight, RibbonHeight;
        internal double Low, High, Period, Pulse, Offset;
        internal bool ChangedAt(double time) => Changes.Count != 0 && Changes[^1].Time == time;
    }

    private static bool Numeric(string text, out double value) => double.TryParse(text,
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && Math.Abs(value) <= 1e12;
    private static string Unquote(string token) => token[0] == '"' ? token[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : token;

    internal static string? TryRender(string source)
    {
        if (source.Length > 512_000) return null;
        var tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        var ticks = new SortedDictionary<double, int>();
        var links = new List<Link>();
        Legend? legend = null;
        bool opened = false, closed = false, showAxis = true, inLegend = false;
        int sourceLine = -1, titleLine = -1, nextOffset = 0;
        string title = "";
        double now = 0;
        foreach (var raw in source.AsSpan().EnumerateLines())
        {
            sourceLine++;
            int lineOffset = nextOffset + raw.Length - raw.TrimStart().Length;
            nextOffset += raw.Length;
            if (nextOffset < source.Length && source[nextOffset] == '\r') nextOffset++;
            if (nextOffset < source.Length && source[nextOffset] == '\n') nextOffset++;
            string line = raw.Trim().ToString();
            if (sourceLine == 0 && line.StartsWith('\uFEFF')) line = line[1..].TrimStart();
            if (line.Length > 4096) return null;
            if (inLegend)
            {
                if (line.Equals("endlegend", StringComparison.OrdinalIgnoreCase))
                {
                    if (legend!.Rows.Count == 0) return null;
                    inLegend = false;
                }
                else
                {
                    if (legend!.Rows.Count >= 128 || !SvgText.IsSupported(line) ||
                        (line.Length != 0 && line[0] is '!' or '@' or '|')) return null;
                    legend.Rows.Add(line);
                    legend.Tokens.Add(new(lineOffset, line.Length, "text"));
                }
                continue;
            }
            if (line.Length == 0 || line[0] == '\'') continue;
            if (!opened) { if (line != "@startuml") return null; opened = true; continue; }
            if (closed) return null;
            if (line == "@enduml") { closed = true; continue; }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                if (titleLine >= 0 || !SvgText.IsSupported(title = line[6..].Trim())) return null;
                titleLine = sourceLine; continue;
            }
            if (line.Equals("legend", StringComparison.OrdinalIgnoreCase) || line.StartsWith("legend ", StringComparison.OrdinalIgnoreCase))
            {
                if (legend != null || (legend = ParseLegend(line, sourceLine)) == null) return null;
                inLegend = true;
                continue;
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
                if (state.Length == 0 || !SvgText.IsSupported(state) || !SvgText.IsSupported(annotation)) return null;
                double value = 0;
                if (track.Kind == "analog" && !Numeric(state, out value)) return null;
                if (track.Kind == "binary")
                {
                    value = state.ToLowerInvariant() switch { "high" or "1" => 1, "low" or "0" => 0, _ => -1 };
                    if (value < 0) return null;
                }
                track.Changes.Add(new(now, state, value, annotation, sourceLine,
                    Token(change.Groups[2], lineOffset, true, "timing-state"), Token(change.Groups[3], lineOffset)));
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
                if (kind == "analog" && alias.Length == 0) return null;
                int labelGroup = 2;
                if (alias.StartsWith('"') && !name.StartsWith('"')) { (alias, name) = (name, alias); labelGroup = 3; }
                var track = new Track(kind, alias.Length == 0 ? Unquote(name) : Unquote(alias), Unquote(name), sourceLine);
                track.Stereotype = string.Join(">><<", declaration.Groups["stereotype"].Captures.Select(capture => capture.Value));
                if (name.StartsWith('"')) track.CaptionToken = Token(declaration.Groups[labelGroup], lineOffset, true);
                if (!SvgText.IsSupported(track.Label) || tracks.Count >= 128 || !tracks.TryAdd(track.Id, track)) return null;
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
            if (!message.Success || !tracks.ContainsKey(message.Groups[1].Value) || !tracks.ContainsKey(message.Groups[2].Value) || !SvgText.IsSupported(message.Groups[4].Value)) return null;
            var end = now;
            if (message.Groups[3].Success)
            {
                var value = message.Groups[3].Value;
                if (!Numeric(value, out end)) return null;
                if (value.StartsWith('+')) end += now;
            }
            links.Add(new(message.Groups[1].Value, message.Groups[2].Value, now, end, message.Groups[4].Value, sourceLine, Token(message.Groups[4], lineOffset)));
            ticks.TryAdd(now, sourceLine); ticks.TryAdd(end, sourceLine);
        }
        if (!closed || inLegend || tracks.Count == 0) return null;
        ticks.TryAdd(0, -1);
        return Render(tracks, ticks, links, title, titleLine, showAxis, legend);
    }

    private static TextToken Token(Group group, int offset, bool quoted = false, string unquotedKind = "text") =>
        new(offset + group.Index, group.Length, quoted && group.Value.StartsWith('"') ? "quoted" : unquotedKind);

    private static string? Render(Dictionary<string, Track> tracks, SortedDictionary<double, int> ticks, List<Link> links,
        string title, int titleLine, bool showAxis, Legend? legend)
    {
        var heading = StyleTitle.Measure(title, StyleDocument.Current, 18);
        if (legend != null) legend.Labels = legend.Rows.Select(row => StyleLabel.Measure(row, "legend", 13)).ToArray();
        foreach (var link in links)
        {
            link.Caption = StyleLabel.Measure(link.Label, "arrow", 12);
            var from = tracks[link.From]; var to = tracks[link.To];
            (from.Line <= to.Line ? from : to).Messages.Add(link);
        }
        var axisLabels = ticks.ToDictionary(pair => pair.Key, pair => StyleLabel.Measure(pair.Key.ToString("G12", CultureInfo.InvariantCulture), "timeline", 12));
        int top = (heading == null ? 12 : heading.Height + 20) + (legend is { Above: true } ? legend.Height + 10 : 0);
        int height = top, gutter = 48;
        double first = ticks.First().Key, last = ticks.Last().Key;
        if (last == first) last += tracks.Values.Where(t => t.Kind == "clock").Select(t => t.Period * 2).DefaultIfEmpty(1).Max();
        double scale = Math.Max(300, Math.Min(16000, ticks.Count * 68)) / (last - first);
        int tailWidth = 16;
        foreach (var track in tracks.Values)
        {
            track.Changes.Sort((a, b) => a.Time.CompareTo(b.Time));
            for (int i = 1; i < track.Changes.Count; i++) if (track.Changes[i - 1].Time == track.Changes[i].Time) return null;
            if (track.Kind == "clock" && (last - first) / track.Period > 2048) return null;
            track.Caption = StyleLabel.Measure(track.Label, "timingdiagram", 14);
            track.HeaderHeight = track.Caption.Height + 4;
            foreach (var change in track.Changes)
            {
                change.StateLabel = StyleLabel.Measure(change.State, track.Kind, 12, stereotype: track.Stereotype);
                change.AnnotationLabel = StyleLabel.Measure(change.Annotation, track.Kind, 12, stereotype: track.Stereotype);
                if (change.Annotation.Length > 0) track.HeaderHeight = Math.Max(track.HeaderHeight, change.AnnotationLabel.Height + 4);
                if (track.Kind == "robust" && track.StateLabels.TryAdd(change.State, change.StateLabel)) track.StateSources.Add(change.State, change);
            }
            double stateOffset = 0;
            foreach (var state in track.States.Keys)
            {
                var label = track.StateLabels[state];
                int band = Math.Max(18, label.Height + 6);
                track.StateOffsets[state] = stateOffset + band / 2d;
                stateOffset += band;
            }
            if (track.Kind == "analog" && track.Changes.Count > 0)
            {
                track.Low = track.Changes.Min(c => c.Value); track.High = track.Changes.Max(c => c.Value);
            }
            track.LowLabel = StyleLabel.Measure(track.Low.ToString("0.0##########", CultureInfo.InvariantCulture), track.Kind, 12, stereotype: track.Stereotype);
            track.HighLabel = StyleLabel.Measure(track.High.ToString("0.0##########", CultureInfo.InvariantCulture), track.Kind, 12, stereotype: track.Stereotype);
            int stateWidth = track.StateLabels.Values.Select(label => label.Width).DefaultIfEmpty(0).Max();
            if (track.Kind == "analog") stateWidth = Math.Max(stateWidth, Math.Max(track.LowLabel.Width, track.HighLabel.Width));
            gutter = Math.Max(gutter, Math.Max(track.Caption.Width + 24, stateWidth + 28));
            track.RibbonHeight = Math.Max(22, track.Changes.Select(change => change.StateLabel.Height + 8).DefaultIfEmpty(22).Max());
            track.SignalHeight = track.Kind switch
            {
                "robust" => Math.Max(36, (int)Math.Ceiling(stateOffset)),
                "concise" => track.RibbonHeight,
                "analog" => Math.Max(45, Math.Max(track.LowLabel.Height, track.HighLabel.Height) * 2 + 10),
                _ => 20
            };
            track.Top = height;
            int signalPadding = track.Kind == "analog" ? (Math.Max(track.LowLabel.Height, track.HighLabel.Height) + 1) / 2 : 0;
            track.SignalTop = track.Top + track.HeaderHeight + signalPadding + 8;
            track.Height = track.HeaderHeight + track.SignalHeight + signalPadding * 2 + 20;
            foreach (var message in track.Messages)
            {
                message.CaptionTop = track.Top + track.Height;
                track.Height += message.Caption.Height + 12;
            }
            height += track.Height;
            for (int i = 0; i < track.Changes.Count; i++)
            {
                var change = track.Changes[i];
                double end = i + 1 < track.Changes.Count ? track.Changes[i + 1].Time : last;
                int required = track.Kind == "concise" ? change.StateLabel.Width + 24 : 0;
                if (change.Annotation.Length > 0) required = Math.Max(required, change.AnnotationLabel.Width + 8);
                if (end > change.Time && required > 0) scale = Math.Max(scale, required / (end - change.Time));
                if (i + 1 == track.Changes.Count) tailWidth = Math.Max(tailWidth, required);
            }
        }
        foreach (var link in links)
            if (link.Start != link.End) scale = Math.Max(scale, (link.Caption.Width + 12) / Math.Abs(link.End - link.Start));
        double plotWidth = (last - first) * scale;
        if (!double.IsFinite(plotWidth) || plotWidth > 16000) return null;
        if (showAxis)
        {
            gutter = Math.Max(gutter, axisLabels[first].Width / 2 + 8);
            tailWidth = Math.Max(tailWidth, axisLabels.Values.Max(label => label.Width) / 2 + 8);
        }
        double X(double time) => gutter + (time - first) * scale;
        int width = Math.Max((int)Math.Ceiling(gutter + plotWidth + tailWidth + 12), (heading?.Width ?? 0) + 28);
        if (legend != null) width = Math.Max(width, legend.Width + 16);
        foreach (var link in links)
        {
            double x1 = X(link.Start), x2 = X(link.End);
            if (link.From == link.To)
                link.CaptionLeft = link.Start == link.End ? x1 + 8 : (x1 + x2 - link.Caption.Width) / 2;
            else
            {
                var (y1, y2) = MessageLevels(tracks[link.From], tracks[link.To], link);
                double topFraction = Math.Clamp((link.CaptionTop - y1) / (y2 - y1), 0, 1);
                double bottomFraction = Math.Clamp((link.CaptionTop + link.Caption.Height - y1) / (y2 - y1), 0, 1);
                link.CaptionLeft = Math.Max(x1 + (x2 - x1) * topFraction, x1 + (x2 - x1) * bottomFraction) + 8;
            }
            width = Math.Max(width, (int)Math.Ceiling(link.CaptionLeft) + link.Caption.Width + 16);
        }
        int axisY = height + 2;
        height += showAxis ? axisLabels.Values.Max(label => label.Height) + 18 : 12;
        if (legend is { Above: false }) height += legend.Height + 10;
        if (width > 32768 || height > 32768) return null;
        var svg = new StringBuilder(512 + ticks.Count * 70 + tracks.Count * 200);
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\">");
        heading?.Append(svg, StyleDocument.Current, width / 2, 8, titleLine);
        if (legend != null)
        {
            int x = legend.Align switch { "left" => 8, "right" => width - legend.Width - 8, _ => (width - legend.Width) / 2 };
            int y = legend.Above ? top - legend.Height - 10 : height - legend.Height - 8;
            Group(svg, "legend", legend.Line, category: "legend");
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{legend.Width}\" height=\"{legend.Height}\" fill=\"#DDD\" stroke=\"#888\"/>");
            y += 5;
            for (int i = 0; i < legend.Labels.Length; i++)
            {
                var label = legend.Labels[i];
                Field(svg, legend.Tokens[i], () => label.Append(svg, x + 8, y));
                y += label.Height + 4;
            }
            svg.Append("</g>");
        }
        svg.Append("<g"); StyleLabel.Scope(svg, "timegrid"); svg.Append('>');
        foreach (var (time, _) in ticks)
        {
            double x = X(time);
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(x)}\" x2=\"{F(x)}\" y1=\"{top}\" y2=\"{axisY}\" stroke=\"#B8B8B8\" stroke-dasharray=\"2 3\"/>");
        }
        svg.Append("</g>");
        foreach (var track in tracks.Values)
        {
            Group(svg, "timing-track", track.Line, track.Id, track.Kind, track.Stereotype);
            svg.Append(CultureInfo.InvariantCulture, $"<line data-style-name=\"timingdiagram\" data-style-metrics=\"{StyleLabel.Metrics}\" x1=\"8\" x2=\"{width - 10}\" y1=\"{track.Top}\" y2=\"{track.Top}\" stroke=\"#999\"/>");
            svg.Append("<g"); StyleLabel.Scope(svg, "timingdiagram"); svg.Append('>');
            Field(svg, track.CaptionToken, () => track.Caption.Append(svg, 10, track.Top + 2));
            svg.Append("</g>");
            if (track.Kind == "analog" && track.Changes.Count > 0)
            {
                track.LowLabel.Append(svg, gutter - 9 - track.LowLabel.Width, (int)Math.Round(Level(track, "", track.Low) - track.LowLabel.Height / 2d));
                if (track.High != track.Low) track.HighLabel.Append(svg, gutter - 9 - track.HighLabel.Width, (int)Math.Round(Level(track, "", track.High) - track.HighLabel.Height / 2d));
            }
            foreach (var (state, label) in track.StateLabels)
            {
                var change = track.StateSources[state];
                Group(svg, "timing-state-label", change.Line, category: track.Kind, stereotype: track.Stereotype);
                Field(svg, change.StateToken, () => label.Append(svg, gutter - 9 - label.Width, (int)Math.Round(Level(track, state, 0) - label.Height / 2d)), "value");
                svg.Append("</g>");
            }
            if (track.Kind == "clock") DrawClock(svg, track, first, last, X);
            svg.Append("</g>");
            if (track.Kind == "clock") continue;
            if (track.Kind == "binary" && (track.Changes.Count == 0 || track.Changes[0].Time > first))
            {
                Group(svg, "timing-state", track.Line, category: track.Kind, stereotype: track.Stereotype);
                Stroke(svg, X(first), Level(track, "", 0), track.Changes.Count == 0 ? X(last) + tailWidth : X(track.Changes[0].Time), Level(track, "", 0));
                svg.Append("</g>");
            }
            for (int i = 0; i < track.Changes.Count; i++)
            {
                var change = track.Changes[i]; var next = i + 1 < track.Changes.Count ? track.Changes[i + 1] : null;
                double x = X(change.Time), endX = next == null ? X(last) + tailWidth : X(next.Time), y = Level(track, change.State, change.Value);
                Group(svg, "timing-state", change.Line, category: track.Kind, stereotype: track.Stereotype);
                if (track.Kind == "concise")
                {
                    double notch = Math.Min(4, (endX - x) / 3), half = track.RibbonHeight / 2d;
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(x)} {F(y)}l{F(notch)} -{F(half)}H{F(endX - notch)}L{F(endX)} {F(y)}l-{F(notch)} {F(half)}H{F(x + notch)}z\" fill=\"#EEEEEE\" stroke=\"#333\"/>");
                    Field(svg, change.StateToken, () => change.StateLabel.Append(svg, (int)Math.Ceiling(x + notch + 4), (int)Math.Round(y - change.StateLabel.Height / 2d), (int)Math.Floor(endX - x - 2 * notch - 8), "center"), "value");
                }
                else
                {
                    double nextY = next == null ? y : Level(track, next.State, next.Value);
                    Stroke(svg, x, y, endX, track.Kind == "analog" ? nextY : y);
                    if (track.Kind != "analog" && next != null) Stroke(svg, endX, y, endX, nextY);
                }
                if (change.Annotation.Length > 0)
                    Field(svg, change.AnnotationToken, () => change.AnnotationLabel.Append(svg, (int)Math.Ceiling(x + 3), track.Top + 2));
                svg.Append("</g>");
            }
        }
        foreach (var link in links)
        {
            var from = tracks[link.From]; var to = tracks[link.To];
            double x1 = X(link.Start), x2 = X(link.End);
            var (y1, y2) = MessageLevels(from, to, link);
            double angle = Math.Atan2(y2 - y1, x2 - x1);
            Group(svg, "timing-message", link.Line, category: "arrow");
            if (link.From == link.To)
            {
                double messageY = link.CaptionTop + link.Caption.Height + 4;
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(x1)} {F(y1)}V{F(messageY)}");
                if (x1 == x2)
                {
                    svg.Append(CultureInfo.InvariantCulture, $"H{F(link.CaptionLeft + link.Caption.Width + 8)}V{F(y2)}H{F(x2)}");
                    angle = Math.PI;
                }
                else
                {
                    svg.Append(CultureInfo.InvariantCulture, $"H{F(x2)}V{F(y2)}");
                    angle = -Math.PI / 2;
                }
                svg.Append("\" fill=\"none\" stroke=\"#333\" stroke-width=\"1.5\"/>");
            }
            else Stroke(svg, x1, y1, x2, y2);
            svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(x2)} {F(y2)}l{F(-8 * Math.Cos(angle - .45))} {F(-8 * Math.Sin(angle - .45))}L{F(x2 - 8 * Math.Cos(angle + .45))} {F(y2 - 8 * Math.Sin(angle + .45))}z\" fill=\"#333\"/>");
            Field(svg, link.Token, () => link.Caption.Append(svg, (int)Math.Round(link.CaptionLeft), link.CaptionTop));
            svg.Append("</g>");
        }
        if (showAxis)
        {
            svg.Append("<g"); StyleLabel.Scope(svg, "timeline"); svg.Append('>');
            Stroke(svg, gutter, axisY, X(last) + tailWidth, axisY);
            double lastRight = double.NegativeInfinity;
            foreach (var (time, row) in ticks)
            {
                var label = axisLabels[time];
                double x = X(time), halfWidth = label.Width / 2d;
                if (row >= 0) Group(svg, "time", row, category: "timeline");
                Stroke(svg, x, axisY, x, axisY + 4);
                if (x - halfWidth > lastRight)
                {
                    label.Append(svg, (int)Math.Round(x - halfWidth), axisY + 6, alignment: "center");
                    lastRight = x + halfWidth + 4;
                }
                if (row >= 0) svg.Append("</g>");
            }
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }

    private static void Field(StringBuilder svg, TextToken? token, Action draw, string role = "label")
    {
        if (token is { Length: > 0 } field)
            svg.Append(CultureInfo.InvariantCulture, $"<g data-edit-start=\"{field.Start}\" data-edit-length=\"{field.Length}\" data-edit-kind=\"{field.Kind}\" data-edit-role=\"{role}\">");
        draw();
        if (token is { Length: > 0 }) svg.Append("</g>");
    }

    private static (double From, double To) MessageLevels(Track from, Track to, Link link)
    {
        double y1 = SignalY(from, link.Start), y2 = SignalY(to, link.End);
        int direction = from == to ? 1 : Math.Sign(to.Top - from.Top);
        if (from.Kind == "concise") y1 += direction * from.RibbonHeight / 2d;
        if (to.Kind == "concise") y2 += (from == to ? 1 : -direction) * to.RibbonHeight / 2d;
        return (y1, y2);
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
        if (low == 0) return t.SignalTop + t.SignalHeight;
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
        "robust" => t.SignalTop + t.SignalHeight - t.StateOffsets[state],
        "concise" => t.SignalTop + t.RibbonHeight / 2d,
        "analog" => t.SignalTop + (t.High > t.Low ? t.SignalHeight * (1 - (value - t.Low) / (t.High - t.Low)) : t.SignalHeight / 2d),
        _ => t.SignalTop + t.SignalHeight * (1 - value)
    };

    private static double Phase(double time, Track t) => ((time - t.Offset) % t.Period + t.Period) % t.Period;
    private static void DrawClock(StringBuilder s, Track t, double first, double last, Func<double, double> xAt)
    {
        double phase = Phase(first, t);
        bool high = phase < t.Pulse;
        double y = Level(t, "", high ? 1 : 0);
        s.Append(CultureInfo.InvariantCulture, $"<path d=\"M{F(xAt(first))} {F(y)}");
        double edge = first + (high ? t.Pulse - phase : t.Period - phase);
        for (int n = 0; edge <= last && n <= 4098; n++)
        {
            high = !high;
            s.Append(CultureInfo.InvariantCulture, $"H{F(xAt(edge))}V{F(Level(t, "", high ? 1 : 0))}");
            edge += high ? t.Pulse : t.Period - t.Pulse;
        }
        s.Append(CultureInfo.InvariantCulture, $"H{F(xAt(last) + 16)}\" fill=\"none\" stroke=\"#333\" stroke-width=\"1.5\"/>");
    }

    private static string F(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
    private static void Group(StringBuilder s, string kind, int line, string? alias = null, string category = "timeline", string stereotype = "")
    {
        s.Append(CultureInfo.InvariantCulture, $"<g class=\"{kind}\" data-source-line=\"{line}\"");
        if (alias != null) s.Append(CultureInfo.InvariantCulture, $" data-qualified-name=\"{SvgText.Escape(alias)}\"");
        StyleLabel.Scope(s, category, stereotype);
        s.Append('>');
    }
    private static void Stroke(StringBuilder s, double x, double y, double endX, double endY) =>
        s.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(x)}\" y1=\"{F(y)}\" x2=\"{F(endX)}\" y2=\"{F(endY)}\" stroke=\"#333\" stroke-width=\"1.5\"/>");
}
