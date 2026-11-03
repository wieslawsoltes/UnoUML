using System.Globalization;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

// Managed implementation of the browser engine's ordered lifelines and event stream.
// Unknown commands delegate the whole document to the compatibility engine.
internal static partial class SequenceRenderer
{
    private const int MaximumExtent = 32768;
    private const string Token = """(?:"[^"\\]*"|[\p{L}\p{N}_.$]+)""";
    private const string Color = @"#[\w]+";
    private const RegexOptions Grammar = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^(participant|actor|boundary|control|entity|database|collections|queue)\s+(" + Token + @")(?:\s+as\s+(" + Token + @"))?(?:\s*<<(?<stereotype>[^<>]+)>>)?(?:\s+(" + Color + @"))?$", Grammar)]
    private static partial Regex ParticipantDeclaration();
    [GeneratedRegex(@"^(" + Token + @")\s*(-{1,2}(?:\[#[a-zA-Z0-9]+\]-?)?>{1,2}|<{1,2}(?:-?\[#[a-zA-Z0-9]+\])?-{1,2})\s*(" + Token + @")(?:\s*(--\+\+|\+\+--|\+\+|--|\*\*|!!))?(?:\s*(" + Color + @"))?\s*(?::\s*(.*))?$", Grammar)]
    private static partial Regex MessageStatement();
    [GeneratedRegex(@"^(activate|deactivate|destroy)\s+(" + Token + @")(?:\s+(" + Color + @"))?$", Grammar)]
    private static partial Regex Activation();
    [GeneratedRegex(@"^note\s+(over|left of|right of)\s+(" + Token + @")(?:\s*,\s*(" + Token + @"))?(?:\s+(" + Color + @"))?\s*(?::\s*(.*))?$", Grammar)]
    private static partial Regex Note();
    [GeneratedRegex(@"^(alt|opt|loop|par|break|critical|group)(?:\s+(.*))?$", Grammar)]
    private static partial Regex FragmentStart();
    [GeneratedRegex(@"^ref(?<color>#[\w]+)?\s+over\s+(?<participant>" + Token + @")(?:\s*,\s*(?<participant>" + Token + @"))*\s*(?::\s*(?<text>.*))?$", Grammar)]
    private static partial Regex Reference();

    private sealed class Lane(string id, string name, string kind, string? fill, int line, string stereotype = "")
    {
        internal readonly string Id = id, Name = name, Kind = kind, Stereotype = stereotype;
        internal int FontSize = 14, StereotypeSize = 12, HeaderHeight = 27;
        internal bool ShowStereotype;
        internal StyleSpacing Padding = new(6, 10, 6, 10), Margin;
        internal string[] Rows = [name];
        internal int TextHeight = 14;
        internal readonly string? Fill = fill;
        internal readonly int Line = line;
        internal int Width = Math.Max(38, SvgText.Measure(name) + 20);
        internal int X, LeftSpace, RightSpace;
        internal int ActiveDepth;
        internal Step? Creation, Destruction;
        internal int? HeadTop, LifeStart, LifeEnd;
        internal bool ShowHead = true, Used;
        internal readonly List<(int Left, int Width)> Incoming = [];

        internal Lane Copy() => (Lane)MemberwiseClone();
    }

    private enum Op { Send, Start, Finish, Destroy, Note, Reference, Open, Branch, Close, Divider, Delay, Space, Page }
    private sealed class Step(Op op, int line, int a = -1, int b = -1, string text = "", string variant = "", string? color = null)
    {
        internal readonly Op Op = op;
        internal readonly int Line = line, A = a, B = b;
        internal readonly string Text = text, Variant = variant;
        internal readonly string? Color = color;
        internal int Top, Bottom, Anchor, Number, Nest, End, TextWidth, FromDepth, ToDepth;
        internal int FontSize = 13;
        internal string[] Rows = [text];
        internal int TextHeight = 13, HeaderSize = 13, HeaderWidth;
        internal StyleSpacing Padding, HeaderPadding, Margin;
        internal bool Creates, Standalone;
        internal Step Translate(int dy)
        {
            var copy = (Step)MemberwiseClone();
            copy.Top += dy;
            copy.Bottom += dy;
            copy.Anchor += dy;
            return copy;
        }
    }

    private sealed record Bar(int Lane, int Depth, int Start, int End, int Line, string Fill);

    private sealed record Frame(int Nest, int Start, int End, bool DrawTop, bool DrawBottom);

    private sealed class Diagram
    {
        internal readonly List<Lane> Lanes = [];
        internal readonly List<Step> Steps = [];
        internal readonly List<Bar> Bars = [];
        internal readonly List<Frame> Frames = [];
        internal readonly List<int> Breaks = [];
        internal string IdPrefix = "";
        internal int FirstLine, LastLine;
        internal readonly Dictionary<string, int> Names = new(StringComparer.Ordinal);
        internal ManagedSkin Skin = new();
        internal string? Title;
        internal StyleTitle? Heading;
        internal string[]? Legend;
        internal string LegendAlignment = "right";
        internal int LegendLine, LegendWidth;
        internal int TitleLine, Width, Height, HeadBottom, LifelineEnd, HeaderHeight, MaxNest;
        internal bool Footbox = true;
        internal string Style(string name, string fallback) => Skin.Get(name, fallback);

        internal int Resolve(string token, int line)
        {
            var id = Unquote(token);
            if (Names.TryGetValue(id, out int existing)) return existing;
            return Declare(id, id, "participant", null, line);
        }

        internal int Declare(string id, string name, string kind, string? fill, int line, string stereotype = "")
        {
            if (Lanes.Count >= 1024 || name.Length == 0 || !Plain(name) || !Names.TryAdd(id, Lanes.Count)) return -1;
            Lanes.Add(new(id, name, kind, fill, line, stereotype));
            return Lanes.Count - 1;
        }
    }

    private static Diagram? Parse(string source)
    {
        if (source.Length > 512_000) return null;
        var diagram = new Diagram();
        if (!ModernStyle.TryExtract(source, sequence: true, diagram.Skin, out source)) return null;
        var lines = source.Split('\n');
        var life = new LifeState(diagram);
        var frames = new Stack<int>();
        bool started = false, ended = false, auto = false, ignorePages = false;
        string? skin = null;
        int number = 1, increment = 1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length > 8192) return null;
            if (line.Length == 0 || line[0] == '\'') continue;
            if (!started) { if (!line.Equals("@startuml", StringComparison.OrdinalIgnoreCase)) return null; started = true; diagram.FirstLine = i; continue; }
            if (ended) return null;
            if (skin != null || line.StartsWith("skinparam ", StringComparison.OrdinalIgnoreCase))
            {
                if (!diagram.Skin.Read(line, ref skin, sequence: true, sourceLine: i)) return null;
                continue;
            }

            if (line.Equals("@enduml", StringComparison.OrdinalIgnoreCase)) { ended = true; diagram.LastLine = i; continue; }
            if (line.Equals("ignore newpage", StringComparison.OrdinalIgnoreCase)) { ignorePages = true; continue; }
            var pageCommand = line.AsSpan(line[0] == '@' ? 1 : 0);
            if (pageCommand.StartsWith("newpage", StringComparison.OrdinalIgnoreCase) &&
                (pageCommand.Length == 7 || char.IsWhiteSpace(pageCommand[7]) || pageCommand[7] == ':'))
            {
                if (ignorePages) continue;
                var caption = pageCommand[7..].TrimStart();
                if (!caption.IsEmpty && caption[0] == ':') caption = caption[1..].TrimStart();
                string pageTitle = caption.ToString().Replace("\\n", "\n", StringComparison.Ordinal);
                if (!Plain(pageTitle) || diagram.Breaks.Count >= 255) return null;
                diagram.Breaks.Add(diagram.Steps.Count);
                diagram.Steps.Add(new(Op.Page, i, text: pageTitle));
                continue;
            }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                string title = line[6..].Replace("\\n", "\n", StringComparison.Ordinal);
                if (diagram.Title != null || !Plain(title)) return null;
                diagram.Title = title; diagram.TitleLine = i;
                continue;
            }
            if (line.Equals("legend", StringComparison.OrdinalIgnoreCase) || line.StartsWith("legend ", StringComparison.OrdinalIgnoreCase))
            {
                if (diagram.Legend != null) return null;
                var alignment = line.Length > 6 ? line[6..].Trim().ToLowerInvariant() : "center";
                if (alignment is not ("left" or "right" or "center")) return null;
                diagram.LegendAlignment = alignment; diagram.LegendLine = i;
                int first = i + 1;
                while (++i < lines.Length && !lines[i].Trim().Equals("endlegend", StringComparison.OrdinalIgnoreCase))
                    if (lines[i].TrimStart().StartsWith('!') || !Plain(lines[i])) return null;
                if (i >= lines.Length || i == first || i - first > 128) return null;
                diagram.Legend = Dedent(lines[first..i]);
                diagram.LegendWidth = Math.Max(50, diagram.Legend.Max(row => SvgText.Measure(row, 13)) + 20);
                continue;
            }
            if (line.Equals("hide footbox", StringComparison.OrdinalIgnoreCase)) { diagram.Footbox = false; continue; }
            if (line.Equals("autoactivate on", StringComparison.OrdinalIgnoreCase)) { life.Automatic = true; continue; }
            if (line.Equals("autoactivate off", StringComparison.OrdinalIgnoreCase)) { life.Automatic = false; continue; }
            if (line.Equals("autonumber stop", StringComparison.OrdinalIgnoreCase)) { auto = false; continue; }
            if (line.Equals("autonumber resume", StringComparison.OrdinalIgnoreCase)) { auto = true; continue; }
            if (line.StartsWith("autonumber", StringComparison.OrdinalIgnoreCase))
            {
                string[] options = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (!options[0].Equals("autonumber", StringComparison.OrdinalIgnoreCase) || options.Length > 3) return null;
                number = increment = 1;
                if (options.Length >= 2 && (!int.TryParse(options[1], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1)) return null;
                if (options.Length == 3 && (!int.TryParse(options[2], NumberStyles.None, CultureInfo.InvariantCulture, out increment) || increment < 1)) return null;
                auto = true; continue;
            }
            if (line.Equals("return", StringComparison.OrdinalIgnoreCase) || line.StartsWith("return ", StringComparison.OrdinalIgnoreCase))
            {
                string label = line.Length == 6 ? "" : line[7..].Trim();
                string? returnColor = null;
                if (label.StartsWith('#'))
                {
                    int split = label.IndexOf(' ');
                    if (split < 0 || (returnColor = ManagedSkin.Color(label[..split])) == null) return null;
                    label = label[(split + 1)..].TrimStart();
                }
                if (!Plain(label) || life.Reply(i, label, returnColor) is not {} reply || !NumberMessage(reply, auto, ref number, increment)) return null;
                continue;
            }
            if (line.Contains("->", StringComparison.Ordinal) || line.Contains("<-", StringComparison.Ordinal) || line.Contains("-[", StringComparison.Ordinal) || line.Contains("]-", StringComparison.Ordinal))
            {
                var m = MessageStatement().Match(line);
                if (m.Success)
                {
                    string arrow = m.Groups[2].Value, label = m.Groups[6].Value;
                    string? color = null;
                    int bracket = arrow.IndexOf('[');
                    if (bracket >= 0)
                    {
                        int close = arrow.IndexOf(']',bracket);
                        color = ManagedSkin.Color(arrow[(bracket+1)..close]);
                        if (color == null) return null;
                        arrow = arrow.Remove(bracket, close-bracket+1);
                    }
                    if (!Plain(label)) return null;
                    int a = diagram.Resolve(m.Groups[1].Value, i), b = diagram.Resolve(m.Groups[3].Value, i);
                    if (a < 0 || b < 0) return null;
                    if (arrow[0] == '<') (a, b) = (b, a);
                    var step = new Step(Op.Send, i, a, b, label, arrow, color);
                    string? lifeColor = null;
                    if (m.Groups[5].Success && (lifeColor = ManagedSkin.Color(m.Groups[5].Value)) == null) return null;
                    if (!NumberMessage(step, auto, ref number, increment) || !life.Send(step, m.Groups[4].Value, lifeColor)) return null;
                    continue;
                }
            }
            bool creates = line.StartsWith("create ", StringComparison.OrdinalIgnoreCase);
            string declarationText = creates ? line[7..].TrimStart() : line;
            var declaration = ParticipantDeclaration().Match(declarationText);
            if (creates && !declaration.Success)
                declaration = ParticipantDeclaration().Match("participant " + declarationText);
            if (declaration.Success)
            {
                var name = declaration.Groups[2].Value;
                var alias = declaration.Groups[3].Value;
                if (alias.StartsWith('"') && !name.StartsWith('"')) (alias, name) = (name, alias);
                var id = Unquote(alias.Length == 0 ? name : alias);
                string? color = null;
                if (declaration.Groups[4].Success && (color = ManagedSkin.Color(declaration.Groups[4].Value)) == null) return null;
                int lane = creates && declarationText == name && diagram.Names.TryGetValue(id, out int existing)
                    ? existing : diagram.Declare(id, Unquote(name), declaration.Groups[1].Value.ToLowerInvariant(), color, i, declaration.Groups["stereotype"].Value);
                if (lane < 0 || creates && !life.Create(lane)) return null;
                continue;
            }
            var activation = Activation().Match(line);
            if (activation.Success)
            {
                int lane = diagram.Resolve(activation.Groups[2].Value, i);
                if (lane < 0) return null;
                bool start = activation.Groups[1].Value.Equals("activate", StringComparison.OrdinalIgnoreCase);
                bool destroy = activation.Groups[1].Value.Equals("destroy", StringComparison.OrdinalIgnoreCase);
                string? color = null;
                if (activation.Groups[3].Success && (!start || (color = ManagedSkin.Color(activation.Groups[3].Value)) == null)) return null;
                if (!life.Change(start ? Op.Start : destroy ? Op.Destroy : Op.Finish, lane, i, color)) return null;
                continue;
            }
            var reference = Reference().Match(line);
            if (reference.Success)
            {
                int lineIndex = i, left = int.MaxValue, right = -1;
                foreach (Capture capture in reference.Groups["participant"].Captures)
                {
                    int lane = diagram.Resolve(capture.Value, i);
                    if (lane < 0) return null;
                    left = Math.Min(left, lane); right = Math.Max(right, lane);
                }
                string? color = null;
                if (reference.Groups["color"].Success && (color = ManagedSkin.Color(reference.Groups["color"].Value)) == null) return null;
                string body = reference.Groups["text"].Value.Replace("\\n", "\n", StringComparison.Ordinal);
                if (!reference.Groups["text"].Success)
                {
                    int first = i + 1;
                    while (++i < lines.Length && !IsReferenceEnd(lines[i].Trim()))
                        if (lines[i].TrimStart().StartsWith('!')) return null;
                    if (i >= lines.Length) return null;
                    body = string.Join('\n', Dedent(lines[first..i]));
                }
                if (!Plain(body)) return null;
                diagram.Steps.Add(new(Op.Reference, lineIndex, left, right, body, color: color)); continue;
            }
            var note = Note().Match(line);
            if (note.Success)
            {
                int lineIndex = i;
                string side = note.Groups[1].Value.ToLowerInvariant();
                int a = diagram.Resolve(note.Groups[2].Value, i);
                int b = note.Groups[3].Success ? diagram.Resolve(note.Groups[3].Value, i) : a;
                if (a < 0 || b < 0 || (a != b && side != "over")) return null;
                string? color = null;
                if (note.Groups[4].Success && (color = ManagedSkin.Color(note.Groups[4].Value)) == null) return null;
                string body = note.Groups[5].Value;
                if (!note.Groups[5].Success)
                {
                    int first = i + 1;
                    while (++i < lines.Length && !lines[i].Trim().Equals("end note", StringComparison.OrdinalIgnoreCase))
                        if (lines[i].TrimStart().StartsWith('!')) return null;
                    if (i >= lines.Length) return null;
                    body = string.Join('\n', Dedent(lines[first..i]));
                }
                if (!Plain(body)) return null;
                diagram.Steps.Add(new(Op.Note, lineIndex, a, b, body, side, color)); continue;
            }
            var open = FragmentStart().Match(line);
            if (open.Success)
            {
                if (frames.Count >= 32 || !Plain(open.Groups[2].Value)) return null;
                frames.Push(diagram.Steps.Count);
                diagram.MaxNest = Math.Max(diagram.MaxNest, frames.Count);
                diagram.Steps.Add(new(Op.Open, i, text: open.Groups[2].Value, variant: open.Groups[1].Value.ToLowerInvariant()) { Nest = frames.Count - 1 }); continue;
            }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (!frames.TryPop(out int index)) return null;
                diagram.Steps[index].End = diagram.Steps.Count;
                life.Barrier();
                diagram.Steps.Add(new(Op.Close, i)); continue;
            }
            if (line.Equals("else", StringComparison.OrdinalIgnoreCase) || line.StartsWith("else ", StringComparison.OrdinalIgnoreCase))
            {
                var label = line.Length == 4 ? "" : line[5..].Trim();
                if (!frames.TryPeek(out int index) || !Plain(label)) return null;
                diagram.Steps.Add(new(Op.Branch, i, text: label) { Nest = diagram.Steps[index].Nest }); continue;
            }
            if (line == "|||") { diagram.Steps.Add(new(Op.Space, i)); continue; }
            if (line.Length >= 4 && line.StartsWith("==") && line.EndsWith("=="))
            {
                var label = line[2..^2].Trim();
                if (!Plain(label)) return null;
                life.Barrier();
                diagram.Steps.Add(new(Op.Divider, i, text: label)); continue;
            }
            if (line == "..." || (line.Length >= 6 && line.StartsWith("...") && line.EndsWith("...")))
            {
                var label = line == "..." ? "" : line[3..^3].Trim();
                if (!Plain(label)) return null;
                life.AfterDelay = true;
                diagram.Steps.Add(new(Op.Delay, i, text: label)); continue;
            }
            return null;
        }

        if (!ended || skin != null || frames.Count != 0 || life.PendingCreate >= 0 || diagram.Lanes.Count == 0 || !Arrange(diagram)) return null;
        return diagram;
    }

    private static string[] Dedent(string[] rows)
    {
        int indent = rows.Where(r => r.Trim().Length > 0).Select(r => r.Length - r.TrimStart().Length).DefaultIfEmpty(0).Min();
        if (indent != 0)
            for (int i = 0; i < rows.Length; i++) rows[i] = rows[i].Length >= indent ? rows[i][indent..] : "";
        return rows;
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' ? value[1..^1] : value;
    private static bool Plain(string text) => text.Length <= 8192 && SvgText.IsSupported(text);
    private static bool IsReferenceEnd(string line) => line.Equals("end ref", StringComparison.OrdinalIgnoreCase) || line.Equals("endref", StringComparison.OrdinalIgnoreCase) || line.Equals("end", StringComparison.OrdinalIgnoreCase);
    private static bool NumberMessage(Step step, bool enabled, ref int number, int increment)
    {
        if (!enabled) return true;
        if (number > int.MaxValue - increment) return false;
        step.Number = number; number += increment;
        return true;
    }

}
