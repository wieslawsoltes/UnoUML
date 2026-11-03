using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

// Parsing is independent of the render's resource scope. Cached labels can therefore
// resolve the same sprite name against different documents without leaking resources.
internal static partial class RichText
{
    internal sealed record Sprite(double Width, double Height, string Svg);
    internal sealed record Link(string Href, string? Tooltip);
    internal sealed record Style(bool? Bold = null, bool? Italic = null, bool? Underline = null,
        bool? Strike = null, bool? Wave = null, string? Color = null, string? Background = null,
        string? DecorationColor = null, int? Size = null, string? Font = null, int Script = 0, Link? Link = null,
        bool Monospaced = false, bool LinkColorDefault = false, int SizeOffset = 0);
    internal sealed record Run(string Text, Style Style, string? SpriteName = null, double SpriteScale = 1, string? SpriteColor = null, bool Image = false);
    internal sealed record Document(bool Supported, Run[][] Lines, string Alignment = "", string[]? Alignments = null);

    private static readonly ConditionalWeakTable<string, Document> Cache = new();
    private static readonly AsyncLocal<Func<string, Sprite?>?> Sprites = new();

    internal static IDisposable BeginSprites(Func<string, Sprite?> resolver)
    {
        var previous = Sprites.Value;
        Sprites.Value = resolver;
        return new SpriteScope(previous);
    }

    internal static Sprite? Resolve(string name) => name.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
        ? SpriteCatalog.TryImage(name, out var image) ? image : null
        : name.StartsWith("$math/", StringComparison.Ordinal) || name.StartsWith("$latex/", StringComparison.Ordinal)
        ? MathRenderer.Inline(name)
        : Sprites.Value?.Invoke(name);
    internal static Document Parse(string text) => Cache.GetValue(text, static value => new Parser(value, new Style()).Parse());

    internal static bool IsSupported(string text)
    {
        if (text.Length > 65536) return false;
        if (SvgText.IsPlain(text)) return ValidXml(text, rejectControls: true);
        var document = Parse(text);
        return document.Supported && document.Lines.All(line => line.All(run =>
            run.SpriteName is not { } name || Resolve(name) is { Width: > 0, Height: > 0 }));
    }

    private sealed class SpriteScope(Func<string, Sprite?>? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            Sprites.Value = previous;
            disposed = true;
        }
    }

    private sealed class Parser(string text, Style initial, int depth = 0)
    {
        private readonly List<List<Run>> lines = [new()];
        private readonly List<string> alignments = [""];
        private readonly List<(string Tag, Style Previous)> scopes = [];
        private readonly StringBuilder buffer = new();
        private Style current = initial;
        private bool supported = true;
        private int heading;
        private string naturalAlignment = "";

        internal Document Parse()
        {
            if (text.Length > 65536 || depth > 32 || !ValidXml(text)) return new(false, []);
            for (int at = 0; at < text.Length && supported;)
            {
                char c = text[at];
                if (c == '=' && heading == 0 && lines[^1].Count == 0 && buffer.Length == 0 && Heading(at, out int next)) at = next;
                else if (c is '\r' or '\n')
                {
                    NewLine();
                    at += c == '\r' && at + 1 < text.Length && text[at + 1] == '\n' ? 2 : 1;
                }
                else if (c == '\\')
                {
                    if (at + 1 >= text.Length) { supported = false; break; }
                    char escaped = text[at + 1];
                    if (escaped is 'n' or 'l' or 'r')
                    {
                        if (escaped != 'n') naturalAlignment = escaped == 'l' ? "start" : "end";
                        NewLine();
                    }
                    else if (escaped == 't') buffer.Append('\t');
                    else if (escaped is '\\' or '"') buffer.Append(escaped);
                    else { supported = false; break; }
                    at += 2;
                }
                else if (c == '~' && at + 1 < text.Length && "<>[]*/_-\"~".Contains(text[at + 1]) && text[at + 1] != '~')
                {
                    buffer.Append(text[at + 1]);
                    at += 2;
                }
                else if (c == '<') at = Tag(at);
                else if (c == '[' && at + 1 < text.Length && text[at + 1] == '[') at = Hyperlink(at);
                else if (c == '&' && Entity(at, out int consumed)) at += consumed;
                else if (at + 1 < text.Length && c == text[at + 1] && "*/_~-\"".Contains(c) &&
                    !(c == '/' && at > 0 && text[at - 1] == ':')) at = Creole(at);
                else if (char.IsControl(c) && c != '\t' || char.IsSurrogate(c) &&
                    (!char.IsHighSurrogate(c) || at + 1 >= text.Length || !char.IsLowSurrogate(text[at + 1]))) supported = false;
                else
                {
                    buffer.Append(c);
                    at++;
                    if (char.IsHighSurrogate(c)) buffer.Append(text[at++]);
                }
            }
            Flush();
            return new(supported, lines.Select(line => line.ToArray()).ToArray(), naturalAlignment, alignments.ToArray());
        }

        private void Flush()
        {
            if (buffer.Length == 0) return;
            Add(new(buffer.ToString(), current));
            buffer.Clear();
        }

        private void Add(Run run)
        {
            if (heading > 0)
                run = run with
                {
                    Style = run.Style with
                    {
                        SizeOffset = run.Style.Size == null ? heading switch { 1 => 4, 2 => 2, 3 => 1, _ => 0 } : 0,
                        Bold = run.Style.Bold ?? (heading <= 3 ? true : null),
                        Italic = run.Style.Italic ?? (heading > 3 ? true : null)
                    }
                };
            lines[^1].Add(run);
        }

        private void NewLine()
        {
            Flush();
            lines.Add([]);
            alignments.Add("");
            heading = 0;
        }

        private bool Heading(int at, out int next)
        {
            next = at;
            while (next < text.Length && text[next] == '=') next++;
            int level = next - at;
            while (next < text.Length && text[next] is ' ' or '\t') next++;
            if (next >= text.Length || text[next] is '\n' or '\r') return false;
            heading = level;
            return true;
        }

        private int Tag(int at)
        {
            int end = text.IndexOf('>', at + 1);
            if (end < 0) { supported = false; return text.Length; }
            string value = text[(at + 1)..end].Trim();
            if (value.Equals("math", StringComparison.OrdinalIgnoreCase) || value.Equals("latex", StringComparison.OrdinalIgnoreCase))
            {
                string mathTag = value.ToLowerInvariant();
                int close = text.IndexOf("</" + mathTag + ">", end + 1, StringComparison.OrdinalIgnoreCase);
                if (close < 0) { supported = false; return text.Length; }
                string formula = text[(end + 1)..close];
                if (formula.Length is 0 or > 16384) supported = false;
                else
                {
                    Flush();
                    Add(new("", current, "$" + mathTag + "/" + formula));
                }
                return close + mathTag.Length + 3;
            }
            string alignment = value.ToLowerInvariant() switch
            {
                "l" or "left" => "start",
                "c" or "center" => "middle",
                "r" or "right" => "end",
                _ => ""
            };
            if (alignment.Length > 0 && lines[^1].Count == 0 && buffer.Length == 0)
            {
                alignments[^1] = alignment;
                return end + 1;
            }
            if (value.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int point) ||
                    !Rune.IsValid(point) || point < 32 && point is not (9 or 10 or 13) || point is 0xfffe or 0xffff) supported = false;
                else if (point is 10 or 13) NewLine();
                else buffer.Append(new Rune(point));
                return end + 1;
            }
            if (value.Equals("br", StringComparison.OrdinalIgnoreCase) || value.Equals("br/", StringComparison.OrdinalIgnoreCase) || value.Equals("br /", StringComparison.OrdinalIgnoreCase))
            {
                NewLine();
                return end + 1;
            }
            if (value.StartsWith("img", StringComparison.OrdinalIgnoreCase) && value.Length > 3 && value[3] is ':' or ' ')
            {
                string uri = value[4..].Trim();
                double scale = 1;
                int options = uri.LastIndexOf("{scale=", StringComparison.OrdinalIgnoreCase);
                if (options >= 0)
                {
                    if (!uri.EndsWith('}') || !double.TryParse(uri.AsSpan(options + 7, uri.Length - options - 8), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out scale) || scale is <= 0 or > 100)
                        supported = false;
                    uri = uri[..options];
                }
                if (!uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) supported = false;
                if (supported)
                {
                    Flush();
                    Add(new("", current, uri, scale, Image: true));
                }
                return end + 1;
            }
            if (SpritePattern().Match(value) is { Success: true } sprite)
            {
                bool emoji = sprite.Groups["emoji"].Success;
                string name = sprite.Groups["icon"].Success ? "$openiconic/" + sprite.Groups["icon"].Value :
                    emoji ? "$emoji/" + sprite.Groups["emoji"].Value : sprite.Groups["name"].Value;
                double scale = 1;
                string scaleText = sprite.Groups["scale"].Value;
                string colorText = sprite.Groups["color"].Success ? "#" + sprite.Groups["color"].Value : sprite.Groups["tint"].Value;
                string? color = colorText.Length == 0 ? null : emoji && colorText.ToLowerInvariant() is "#0" or "#000" or "#black" ? "currentColor" : ManagedSkin.Color(colorText);
                if (scaleText.Length > 0 && (!double.TryParse(scaleText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out scale) || scale is <= 0 or > 100) ||
                    colorText.Length > 0 && color == null) supported = false;
                else
                {
                    Flush();
                    Add(new("", current, name, scale, color));
                }
                return end + 1;
            }
            if (value.StartsWith('/'))
            {
                string name = Name(value[1..]);
                if (scopes.Count == 0 || scopes[^1].Tag != name) supported = false;
                else
                {
                    Flush();
                    current = scopes[^1].Previous;
                    scopes.RemoveAt(scopes.Count - 1);
                }
                return end + 1;
            }
            int separator = value.IndexOfAny([':', ' ', '\t']);
            string tag = Name(separator < 0 ? value : value[..separator]);
            string argument = separator < 0 ? "" : value[(separator + 1)..].Trim();
            Style? changed = tag switch
            {
                "b" when argument.Length == 0 => current with { Bold = true },
                "i" when argument.Length == 0 => current with { Italic = true },
                "u" => Decoration(argument, current with { Underline = true }),
                "s" => Decoration(argument, current with { Strike = true }),
                "w" => Decoration(argument, current with { Wave = true }),
                "plain" when argument.Length == 0 => current with { Bold = false, Italic = false, Underline = false, Strike = false, Wave = false },
                "color" when ManagedSkin.Color(argument) is { } color => current with { Color = color, LinkColorDefault = false },
                "back" when ManagedSkin.Color(argument) is { } color => current with { Background = color },
                "size" when Size(argument) is { } size => current with { Size = size },
                "font" => Font(argument),
                "sup" when argument.Length == 0 => current with { Script = 1 },
                "sub" when argument.Length == 0 => current with { Script = -1 },
                _ => null
            };
            if (changed == null || scopes.Count >= 64) supported = false;
            else
            {
                Flush();
                scopes.Add((tag, current));
                current = changed;
            }
            return end + 1;
        }

        private int Creole(int at)
        {
            string tag = text.Substring(at, 2);
            if (scopes.Count > 0 && scopes[^1].Tag == tag)
            {
                Flush();
                current = scopes[^1].Previous;
                scopes.RemoveAt(scopes.Count - 1);
            }
            else if (scopes.Count < 64 && text.IndexOf(tag, at + 2, StringComparison.Ordinal) > at + 2)
            {
                Flush();
                scopes.Add((tag, current));
                current = tag switch
                {
                    "**" => current with { Bold = true },
                    "//" => current with { Italic = true },
                    "__" => current with { Underline = true },
                    "--" => current with { Strike = true },
                    "~~" => current with { Wave = true },
                    _ => current with { Font = "monospace", Monospaced = true }
                };
            }
            else buffer.Append(tag);
            return at + 2;
        }

        private int Hyperlink(int at)
        {
            int end = text.IndexOf("]]", at + 2, StringComparison.Ordinal);
            if (end < 0 || current.Link != null) { supported = false; return text.Length; }
            string value = text[(at + 2)..end].Trim();
            int position = 0;
            string href;
            if (value.StartsWith('"'))
            {
                int quote = value.IndexOf('"', 1);
                if (quote < 0) { supported = false; return end + 2; }
                href = value[1..quote];
                position = quote + 1;
            }
            else
            {
                while (position < value.Length && !char.IsWhiteSpace(value[position]) && value[position] != '{') position++;
                href = value[..position];
            }
            while (position < value.Length && char.IsWhiteSpace(value[position])) position++;
            string? tooltip = null;
            if (position < value.Length && value[position] == '{')
            {
                int close = value.IndexOf('}', position + 1);
                if (close < 0) { supported = false; return end + 2; }
                tooltip = value[(position + 1)..close];
                position = close + 1;
            }
            string label = value[position..].Trim();
            if (label.Length == 0) label = href.Length == 0 ? tooltip ?? "" : href;
            if (!SafeLink(href)) { supported = false; return end + 2; }
            var linked = new Parser(label, current with { Link = new(href, tooltip), Underline = true, Color = current.Color ?? "#0000ee", LinkColorDefault = current.Color == null }, depth + 1).Parse();
            if (!linked.Supported) { supported = false; return end + 2; }
            Flush();
            for (int line = 0; line < linked.Lines.Length; line++)
            {
                if (line > 0) NewLine();
                foreach (var run in linked.Lines[line]) Add(run);
            }
            return end + 2;
        }

        private bool Entity(int at, out int consumed)
        {
            consumed = 0;
            int end = text.IndexOf(';', at + 1);
            if (end < 0 || end - at > 12) return false;
            string value = text[at..(end + 1)];
            string decoded = WebUtility.HtmlDecode(value);
            if (value == decoded) return false;
            if (!ValidXml(decoded)) supported = false;
            else
            {
                foreach (char c in decoded)
                {
                    if (c is '\r' or '\n') NewLine();
                    else buffer.Append(c);
                }
            }
            consumed = end + 1 - at;
            return true;
        }

        private Style? Font(string argument)
        {
            if (!argument.Contains('='))
                return FontName(argument) is { } family ? current with { Font = family, Monospaced = false } : null;
            Style result = current;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int position = 0;
            foreach (Match match in FontAttributes().Matches(" " + argument))
            {
                if (match.Index != position || !seen.Add(match.Groups["name"].Value)) return null;
                string value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["single"].Success ? match.Groups["single"].Value : match.Groups["value"].Value;
                result = match.Groups["name"].Value.ToLowerInvariant() switch
                {
                    "size" when Size(value) is { } size => result with { Size = size },
                    "color" when ManagedSkin.Color(value) is { } color => result with { Color = color, LinkColorDefault = false },
                    "face" or "name" when FontName(value) is { } font => result with { Font = font, Monospaced = false },
                    _ => null!
                };
                if (result == null) return null;
                position += match.Length;
            }
            return position == argument.Length + 1 ? result : null;
        }

        private static Style? Decoration(string argument, Style style) => argument.Length == 0 ? style :
            ManagedSkin.Color(argument) is { } color ? style with { DecorationColor = color } : null;
        private static int? Size(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int size) && size is >= 1 and <= 512 ? size : null;
        private static string? FontName(string text) => text.Length is > 0 and <= 128 && text.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or ',') ?
            text.Equals("monospaced", StringComparison.OrdinalIgnoreCase) ? "monospace" : text : null;
        private static string Name(string text) => text.ToLowerInvariant() switch { "strong" => "b", "em" => "i", "strike" or "del" => "s", var name => name };
    }

    private static bool SafeLink(string href)
    {
        if (href.Any(char.IsControl) || href.Contains('"') || href.Contains('<') || href.Contains('>')) return false;
        if (href.Length == 0 || href.StartsWith('#')) return true;
        if (!Uri.TryCreate(href, UriKind.RelativeOrAbsolute, out var uri)) return false;
        return !uri.IsAbsoluteUri || uri.Scheme is "http" or "https" or "mailto";
    }

    private static bool ValidXml(string value, bool rejectControls = false)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(c) || c < 32 && c is not ('\t' or '\r' or '\n') || c is '\ufffe' or '\uffff' ||
                rejectControls && c is >= '\u007f' and <= '\u009f') return false;
        }
        return true;
    }

    [GeneratedRegex("^(?:#(?<color>\\w+))?(?:\\$(?<name>[-\\p{L}0-9_/]+)|&(?<icon>[-\\w]+)|:(?<emoji>[0-9a-z][0-9_a-z]*):)(?:[,{]?(?:(?:scale=|\\*)(?<scale>[0-9.]+))?(?:,?color[= :](?<tint>#[0-9a-fA-F]{1,8}|\\w+))?\\}?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SpritePattern();

    [GeneratedRegex("\\s+(?<name>size|color|face|name)\\s*=\\s*(?:\"(?<quoted>[^\"]+)\"|'(?<single>[^']+)'|(?<value>[^\\s]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FontAttributes();
}
