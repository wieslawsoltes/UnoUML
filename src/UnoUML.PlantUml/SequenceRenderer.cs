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

    [GeneratedRegex(@"^(participant|actor|boundary|control|entity|database|collections|queue)\s+(" + Token + @")(?:\s+as\s+(" + Token + @"))?(?:\s+(" + Color + @"))?$", Grammar)]
    private static partial Regex ParticipantDeclaration();
    [GeneratedRegex(@"^(" + Token + @")\s*(-{1,2}>{1,2}|<{1,2}-{1,2})\s*(" + Token + @")(?:\s+(\+\+|--))?\s*(?::\s*(.*))?$", Grammar)]
    private static partial Regex MessageStatement();
    [GeneratedRegex(@"^(activate|deactivate)\s+(" + Token + @")(?:\s+(" + Color + @"))?$", Grammar)]
    private static partial Regex Activation();
    [GeneratedRegex(@"^note\s+(over|left of|right of)\s+(" + Token + @")(?:\s*,\s*(" + Token + @"))?(?:\s+(" + Color + @"))?\s*(?::\s*(.*))?$", Grammar)]
    private static partial Regex Note();
    [GeneratedRegex(@"^(alt|opt|loop|par|break|critical|group)(?:\s+(.*))?$", Grammar)]
    private static partial Regex FragmentStart();

    private sealed class Lane(string id, string name, string kind, string? fill, int line)
    {
        internal readonly string Id = id, Name = name, Kind = kind;
        internal readonly string? Fill = fill;
        internal readonly int Line = line;
        internal readonly int Width = Math.Max(38, SvgText.Measure(name) + 20);
        internal int X, LeftSpace, RightSpace;
        internal readonly List<(int Left, int Width)> Incoming = [];
    }

    private enum Op { Send, Start, Finish, Note, Open, Branch, Close, Divider, Delay, Space }
    private sealed class Step(Op op, int line, int a = -1, int b = -1, string text = "", string variant = "", string? color = null)
    {
        internal readonly Op Op = op;
        internal readonly int Line = line, A = a, B = b;
        internal readonly string Text = text, Variant = variant;
        internal readonly string? Color = color;
        internal int Top, Bottom, Number, Nest, End, TextWidth, FromDepth, ToDepth;
    }

    private sealed record Bar(int Lane, int Depth, int Start, int End, int Line, string Fill);

    private sealed class Diagram
    {
        internal readonly List<Lane> Lanes = [];
        internal readonly List<Step> Steps = [];
        internal readonly List<Bar> Bars = [];
        internal readonly Dictionary<string, int> Names = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> Colors = new(StringComparer.OrdinalIgnoreCase);
        internal string? Title;
        internal int TitleLine, Width, Height, HeadBottom, LifelineEnd, HeaderHeight, MaxNest;
        internal bool Footbox = true;
        internal string Style(string name, string fallback) => Colors.GetValueOrDefault(name, fallback);

        internal int Resolve(string token, int line)
        {
            var id = Unquote(token);
            if (Names.TryGetValue(id, out int existing)) return existing;
            return Declare(id, id, "participant", null, line);
        }

        internal int Declare(string id, string name, string kind, string? fill, int line)
        {
            if (Lanes.Count >= 1024 || name.Length == 0 || !Plain(name) || !Names.TryAdd(id, Lanes.Count)) return -1;
            Lanes.Add(new(id, name, kind, fill, line));
            return Lanes.Count - 1;
        }
    }

    internal static string? TryRender(string source)
    {
        if (source.Length > 512_000) return null;
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var diagram = new Diagram();
        var frames = new Stack<int>();
        bool started = false, ended = false, skin = false, auto = false;
        int number = 1, increment = 1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length > 8192) return null;
            if (line.Length == 0 || line[0] == '\'') continue;
            if (!started) { if (!line.Equals("@startuml", StringComparison.OrdinalIgnoreCase)) return null; started = true; continue; }
            if (ended) return null;
            if (skin)
            {
                if (line == "}") skin = false;
                else if (!SetColor(diagram, line)) return null;
                continue;
            }

            if (line.Equals("@enduml", StringComparison.OrdinalIgnoreCase)) { ended = true; continue; }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
            {
                if (diagram.Title != null || !Plain(line[6..])) return null;
                diagram.Title = line[6..]; diagram.TitleLine = i;
                continue;
            }
            if (line.Equals("hide footbox", StringComparison.OrdinalIgnoreCase)) { diagram.Footbox = false; continue; }
            if (line.Equals("skinparam sequence {", StringComparison.OrdinalIgnoreCase)) { skin = true; continue; }
            if (line.Equals("skinparam shadowing false", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("skinparam ", StringComparison.OrdinalIgnoreCase))
            {
                var setting = line[10..].Trim();
                if (setting.StartsWith("sequence", StringComparison.OrdinalIgnoreCase)) setting = setting[8..];
                else if (!setting.StartsWith("backgroundcolor ", StringComparison.OrdinalIgnoreCase)) return null;
                if (!SetColor(diagram, setting)) return null;
                continue;
            }
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
            if (line.Contains("->", StringComparison.Ordinal) || line.Contains("<-", StringComparison.Ordinal) || line.Contains("<<-", StringComparison.Ordinal))
            {
                var m = MessageStatement().Match(line);
                if (m.Success)
                {
                    string arrow = m.Groups[2].Value, label = m.Groups[5].Value;
                    if (!Plain(label)) return null;
                    int a = diagram.Resolve(m.Groups[1].Value, i), b = diagram.Resolve(m.Groups[3].Value, i);
                    if (a < 0 || b < 0) return null;
                    if (arrow[0] == '<') (a, b) = (b, a);
                    var step = new Step(Op.Send, i, a, b, label, arrow);
                    if (auto)
                    {
                        if (number > int.MaxValue - increment) return null;
                        step.Number = number; number += increment;
                    }
                    diagram.Steps.Add(step);
                    if (m.Groups[4].Value == "++") diagram.Steps.Add(new(Op.Start, i, b));
                    if (m.Groups[4].Value == "--") diagram.Steps.Add(new(Op.Finish, i, a));
                    continue;
                }
            }
            var declaration = ParticipantDeclaration().Match(line);
            if (declaration.Success)
            {
                var name = declaration.Groups[2].Value;
                var alias = declaration.Groups[3].Value;
                if (alias.StartsWith('"') && !name.StartsWith('"')) (alias, name) = (name, alias);
                var id = Unquote(alias.Length == 0 ? name : alias);
                string? color = null;
                if (declaration.Groups[4].Success && (color = ParseColor(declaration.Groups[4].Value)) == null) return null;
                if (diagram.Declare(id, Unquote(name), declaration.Groups[1].Value.ToLowerInvariant(), color, i) < 0) return null;
                continue;
            }
            var activation = Activation().Match(line);
            if (activation.Success)
            {
                if (!diagram.Names.TryGetValue(Unquote(activation.Groups[2].Value), out int lane)) return null;
                bool start = activation.Groups[1].Value.Equals("activate", StringComparison.OrdinalIgnoreCase);
                string? color = null;
                if (activation.Groups[3].Success && (!start || (color = ParseColor(activation.Groups[3].Value)) == null)) return null;
                diagram.Steps.Add(new(start ? Op.Start : Op.Finish, i, lane, color: color)); continue;
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
                if (note.Groups[4].Success && (color = ParseColor(note.Groups[4].Value)) == null) return null;
                string body = note.Groups[5].Value;
                if (!note.Groups[5].Success)
                {
                    int first = i + 1;
                    while (++i < lines.Length && !lines[i].Trim().Equals("end note", StringComparison.OrdinalIgnoreCase))
                        if (lines[i].TrimStart().StartsWith('!')) return null;
                    if (i >= lines.Length) return null;
                    var rows = lines[first..i];
                    int indent = rows.Where(r => r.Trim().Length > 0).Select(r => r.Length - r.TrimStart().Length).DefaultIfEmpty(0).Min();
                    body = string.Join('\n', rows.Select(row => row.Length >= indent ? row[indent..] : ""));
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
                diagram.Steps.Add(new(Op.Divider, i, text: label)); continue;
            }
            if (line == "..." || (line.Length >= 6 && line.StartsWith("...") && line.EndsWith("...")))
            {
                var label = line == "..." ? "" : line[3..^3].Trim();
                if (!Plain(label)) return null;
                diagram.Steps.Add(new(Op.Delay, i, text: label)); continue;
            }
            return null;
        }

        if (!ended || skin || frames.Count != 0 || diagram.Lanes.Count == 0 || !Arrange(diagram)) return null;
        return WriteSvg(diagram);
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' ? value[1..^1] : value;
    private static bool Plain(string text) => text.Length <= 8192 && SvgText.IsPlain(text) && !text.Contains('%');

    private static string? ParseColor(string text)
    {
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "none";
        var name = text.AsSpan().TrimStart('#');
        if (text.StartsWith('#') && name.Length is 3 or 6 && name.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0) return text;
        return name.ToString().ToLowerInvariant() switch {
            "black" or "white" or "red" or "blue" or "green" or "yellow" or "orange" or "pink" or "gray" or "grey" or "purple" or "brown" or "gold" or "silver" or "lightblue" or "lightgreen" => name.ToString().ToLowerInvariant(), _ => null
        };
    }

    private static bool SetColor(Diagram diagram, string assignment)
    {
        int space = assignment.IndexOfAny([' ', '\t']);
        if (space < 1 || ParseColor(assignment[(space + 1)..].Trim()) is not { } color) return false;
        string property = assignment[..space].ToLowerInvariant();
        if (property is not ("backgroundcolor" or "arrowcolor" or "lifelinebordercolor" or "participantbordercolor" or "participantbackgroundcolor" or "participantfontcolor" or "actorbordercolor" or "actorbackgroundcolor" or "actorfontcolor")) return false;
        diagram.Colors[property] = color;
        return true;
    }
}
