using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace UnoUML.PlantUml;

// Definitions stay compressed until a label actually measures them. Large standard
// libraries commonly declare hundreds of sprites and use only one or two.
internal sealed partial class SpriteCatalog(Func<string, int, string?>? resolveResource)
{
    private const int MaximumPixels = 1_048_576;
    private const int MaximumAssetLength = 4_194_304;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_";
    private const string ColorAlphabet = ".,$%&*+-:;<=>?@^_~GHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly AsyncLocal<SpriteCatalog?> Current = new();
    private static readonly ConditionalWeakTable<string, ImageAsset> Images = new();
    private readonly Dictionary<string, Definition> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RichText.Sprite?> decoded = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, int Line), RichText.Sprite?> imageResources = [];
    private int imageResourceCharacters;
    private int instance;

    private sealed record Definition(string Body, int Line, int Width = 0, int Height = 0, int Levels = 16, bool Compressed = false, bool Pixels = false);
    private sealed record ImageAsset(RichText.Sprite? Sprite);

    internal static bool TryExtract(string source, Func<string, int, string?>? resolver,
        out string cleaned, out SpriteCatalog catalog, bool declarations = true)
    {
        cleaned = source;
        catalog = new(resolver);
        if (!declarations || !source.Contains("sprite", StringComparison.OrdinalIgnoreCase)) return true;
        char[]? mask = null;
        string[] lines = SourceLines.WithLineFeeds(source).Split('\n');
        int offset = 0;
        for (int line = 0; line < lines.Length; line++)
        {
            string text = lines[line].Trim();
            if (!text.StartsWith("sprite ", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("sprite\t", StringComparison.OrdinalIgnoreCase))
            {
                offset += lines[line].Length + 1;
                continue;
            }
            var match = Declaration().Match(text);
            if (!match.Success || catalog.definitions.Count >= 4096) return false;
            string name = match.Groups["name"].Value, body = match.Groups["body"].Value;
            int startLine = line, endOffset = offset + lines[line].Length;
            Definition definition;
            if (body.StartsWith("<svg", StringComparison.Ordinal))
            {
                var content = new StringBuilder(body);
                while (!lines[line].TrimEnd().EndsWith("</svg>", StringComparison.Ordinal))
                {
                    if (++line >= lines.Length || content.Length > MaximumAssetLength) return false;
                    endOffset += lines[line].Length + 1;
                    content.Append('\n').Append(lines[line].Trim());
                }
                definition = new(content.ToString(), startLine);
            }
            else if (body.StartsWith('[') || body.StartsWith('{'))
            {
                int width = 0, height = 0, levels = 16;
                bool compressed = false;
                if (body.StartsWith('['))
                {
                    var size = Dimensions().Match(body);
                    if (!size.Success || !int.TryParse(size.Groups["w"].Value, out width) || !int.TryParse(size.Groups["h"].Value, out height) ||
                        !ValidSize(width, height)) return false;
                    levels = size.Groups["color"].Success ? 4096 : int.Parse(size.Groups["levels"].Value, CultureInfo.InvariantCulture);
                    compressed = size.Groups["z"].Success;
                    body = body[size.Length..].Trim();
                }
                if (body == "{")
                {
                    var pixels = new StringBuilder();
                    while (true)
                    {
                        if (++line >= lines.Length || pixels.Length > MaximumAssetLength) return false;
                        endOffset += lines[line].Length + 1;
                        string row = lines[line].Trim();
                        if (row == "}" || row.Equals("end sprite", StringComparison.OrdinalIgnoreCase) || row.Equals("endsprite", StringComparison.OrdinalIgnoreCase)) break;
                        if (row.Length == 0) continue;
                        if (pixels.Length > 0 && !compressed) pixels.Append('\n');
                        pixels.Append(row);
                    }
                    body = pixels.ToString();
                }
                else if ((!compressed && levels != 4096) || body.Length == 0) return false;
                definition = new(body, startLine, width, height, levels, compressed, true);
            }
            else if (body.All(c => char.IsAsciiHexDigit(c))) definition = new(body, startLine, Pixels: true);
            else definition = new(body, startLine);
            catalog.definitions[name] = definition;
            mask ??= source.ToCharArray();
            for (int index = offset; index < endOffset; index++)
                if (mask[index] is not ('\n' or '\r')) mask[index] = ' ';
            offset = endOffset + 1;
        }
        if (mask != null) cleaned = new(mask);
        return true;
    }

    internal IDisposable BeginScope()
    {
        var previous = Current.Value;
        Current.Value = this;
        return new Scope(previous, RichText.BeginSprites(Resolve));
    }

    internal static RichText.Sprite? ResolveImage(string path, int sourceLine)
    {
        if (path.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return TryImage(path, out var embedded) ? embedded : null;
        return Current.Value?.ResolveImageResource(path, sourceLine);
    }

    private RichText.Sprite? ResolveImageResource(string path, int sourceLine)
    {
        if (path.Length is 0 or > 65536) return null;
        var key = (path, sourceLine);
        if (imageResources.TryGetValue(key, out var cached)) return cached;
        if (imageResources.Count >= 512) return null;
        string? body = resolveResource?.Invoke(path, sourceLine)?.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        RichText.Sprite? image = null;
        if (body is { Length: > 0 and <= MaximumAssetLength } && imageResourceCharacters + (long)body.Length <= 16_777_216)
        {
            imageResourceCharacters += body.Length;
            image = body.StartsWith('<') ? DecodeSvg(body) : TryImage(body, out var raster) ? raster : null;
        }
        imageResources.Add(key, image);
        return image;
    }

    private sealed class Scope(SpriteCatalog? previous, IDisposable richScope) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            richScope.Dispose();
            Current.Value = previous;
            disposed = true;
        }
    }

    private RichText.Sprite? Resolve(string name)
    {
        if (decoded.TryGetValue(name, out var sprite)) return sprite;
        string? artwork = name.StartsWith("$openiconic/", StringComparison.Ordinal) ? BuiltinResources.Icon(name["$openiconic/".Length..]) :
            name.StartsWith("$emoji/", StringComparison.Ordinal) ? BuiltinResources.Emoji(name["$emoji/".Length..]) : null;
        if (artwork != null)
        {
            int size = name.StartsWith("$emoji/", StringComparison.Ordinal) ? 36 : 8;
            sprite = DecodeSvg(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 {size} {size}\" fill=\"currentColor\">{artwork}</svg>"));
            decoded[name] = sprite;
            return sprite;
        }
        if (!definitions.TryGetValue(name, out var definition)) return null;
        if (definition.Pixels) sprite = definition.Levels == 4096 ? DecodeColors(definition.Body) : DecodePixels(definition);
        else
        {
            string body = definition.Body;
            if (!body.StartsWith('<') && !body.StartsWith("data:", StringComparison.Ordinal))
                body = resolveResource?.Invoke(body.Trim('"'), definition.Line) ?? "";
            body = body.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            sprite = body.StartsWith('<') ? DecodeSvg(body) : TryImage(body, out var image) ? image : null;
        }
        decoded[name] = sprite;
        return sprite;
    }

    private static RichText.Sprite? DecodePixels(Definition definition)
    {
        int width = definition.Width, height = definition.Height;
        byte[] pixels;
        if (definition.Compressed)
        {
            if (!ValidSize(width, height)) return null;
            var compressed = new byte[(definition.Body.Length + 3) / 4 * 3 + 256];
            for (int at = 0, output = 0; at < definition.Body.Length; at += 4, output += 3)
            {
                int a = Digit(at), b = Digit(at + 1), c = Digit(at + 2), d = Digit(at + 3);
                if ((a | b | c | d) < 0) return null;
                compressed[output] = (byte)((a << 2) | (b >> 4));
                compressed[output + 1] = (byte)((b << 4) | (c >> 2));
                compressed[output + 2] = (byte)((c << 6) | d);
            }
            int Digit(int index) => index < definition.Body.Length ? Alphabet.IndexOf(definition.Body[index]) : 0;
            pixels = new byte[width * height];
            try
            {
                using var input = new MemoryStream(compressed, writable: false);
                using var inflater = new DeflateStream(input, CompressionMode.Decompress);
                inflater.ReadExactly(pixels);
                if (inflater.ReadByte() != -1) return null;
            }
            catch (InvalidDataException) { return null; }
            catch (EndOfStreamException) { return null; }
        }
        else
        {
            string[] rows = definition.Body.Split('\n');
            int perCharacter = definition.Levels == 4 ? 3 : definition.Levels == 8 ? 2 : 1;
            if (definition.Levels == 16) { width = rows[0].Length; height = rows.Length; }
            if (!ValidSize(width, height) || rows.Length > (height + perCharacter - 1) / perCharacter) return null;
            pixels = new byte[width * height];
            for (int row = 0; row < rows.Length; row++)
            {
                for (int x = 0; x < Math.Min(width, rows[row].Length); x++)
                {
                    int digit = definition.Levels == 16 ? "0123456789ABCDEF".IndexOf(char.ToUpperInvariant(rows[row][x])) : Alphabet.IndexOf(rows[row][x]);
                    if (digit < 0) return null;
                    for (int part = perCharacter - 1; part >= 0; part--)
                    {
                        int y = row * perCharacter + part;
                        if (y < height) pixels[y * width + x] = (byte)(digit % definition.Levels);
                        digit /= definition.Levels;
                    }
                }
            }
        }
        var paths = new StringBuilder?[definition.Levels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width;)
            {
                int value = pixels[y * width + x], start = x++;
                if (value >= definition.Levels) return null;
                if (value == 0) continue;
                while (x < width && pixels[y * width + x] == value) x++;
                var path = paths[value] ??= new();
                path.Append(CultureInfo.InvariantCulture, $"M{start} {y}h{x - start}v1h{start - x}z");
            }
        }
        var svg = new StringBuilder(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" fill=\"currentColor\">"));
        for (int value = 1; value < paths.Length; value++)
            if (paths[value] is { } path)
                svg.Append(CultureInfo.InvariantCulture, $"<path fill-opacity=\"{(double)value / (definition.Levels - 1):0.######}\" d=\"{path}\"/>");
        return new(width, height, svg.Append("</svg>").ToString());
    }

    private static bool ValidSize(int width, int height) => width is > 0 and <= 4096 && height is > 0 and <= 4096 && (long)width * height <= MaximumPixels;

    private static RichText.Sprite? DecodeColors(string body)
    {
        string[] rows = body.Split('\n');
        int width = rows[0].Length / 2, height = rows.Length;
        if (!ValidSize(width, height) || rows.Any(row => row.Length % 2 != 0)) return null;
        var paths = new Dictionary<int, StringBuilder>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x * 2 >= rows[y].Length) continue;
                int first = Digit(rows[y][x * 2]), second = Digit(rows[y][x * 2 + 1]);
                if (first < 0 || second < 0) return null;
                int code = first * 64 + second;
                int rgb = ((code >> 8) * 17) << 16 | ((code >> 4 & 15) * 17) << 8 | (code & 15) * 17;
                if (!paths.TryGetValue(rgb, out var path)) paths[rgb] = path = new();
                path.Append(CultureInfo.InvariantCulture, $"M{x} {y}h1v1h-1z");
            }
        }
        var svg = new StringBuilder(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">"));
        foreach (var (color, path) in paths)
            svg.Append(CultureInfo.InvariantCulture, $"<path fill=\"#{color:x6}\" d=\"{path}\"/>");
        return new(width, height, svg.Append("</svg>").ToString());

        static int Digit(char character) => ColorAlphabet.IndexOf(character switch { '!' => '.', '#' => ',', _ => character });
    }

    internal static bool TryImage(string uri, out RichText.Sprite? sprite)
    {
        sprite = Images.GetValue(uri, static value =>
        {
            TryDecodeImage(value, out var image);
            return new(image);
        }).Sprite;
        return sprite != null;
    }

    private static bool TryDecodeImage(string uri, out RichText.Sprite? sprite)
    {
        sprite = null;
        if (uri.Length > MaximumAssetLength || !uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return false;
        int comma = uri.IndexOf(',');
        if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;
        string mime = uri[5..(comma - 7)].ToLowerInvariant();
        byte[] data;
        try { data = Convert.FromBase64String(uri[(comma + 1)..]); }
        catch (FormatException) { return false; }
        int width = 0, height = 0;
        if (mime == "image/png" && data.Length >= 33 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.AsSpan(12, 4).SequenceEqual("IHDR"u8) && CompletePng(data))
        {
            width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        }
        else if (mime == "image/gif" && data.Length >= 13 && (data.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || data.AsSpan(0, 6).SequenceEqual("GIF89a"u8)))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2));
        }
        else if (mime is "image/jpeg" or "image/jpg" && data.Length >= 4 && data[0] == 255 && data[1] == 216)
        {
            int at = 2;
            while (at + 4 <= data.Length && data[at] == 255)
            {
                byte marker = data[at + 1];
                if (marker == 255) { at++; continue; }
                if (marker is 0xd9 or 0xda) break;
                if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7) { at += 2; continue; }
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2, 2));
                if (length < 2 || at + 2 + length > data.Length) break;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && length >= 8)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 5, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 7, 2));
                    break;
                }
                at += 2 + length;
            }
        }
        else if (mime == "image/webp" && data.Length >= 30 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            var chunk = data.AsSpan(12, 4);
            if (chunk.SequenceEqual("VP8X"u8))
            {
                width = 1 + (data[24] | data[25] << 8 | data[26] << 16);
                height = 1 + (data[27] | data[28] << 8 | data[29] << 16);
            }
            else if (chunk.SequenceEqual("VP8 "u8) && data.AsSpan(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26, 2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28, 2)) & 0x3fff;
            }
            else if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2f)
            {
                width = 1 + (data[21] | (data[22] & 0x3f) << 8);
                height = 1 + ((data[22] >> 6) | data[23] << 2 | (data[24] & 0x0f) << 10);
            }
        }
        if (!ValidSize(width, height)) return false;
        string normalized = "data:" + mime + ";base64," + Convert.ToBase64String(data);
        sprite = new(width, height, FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\"><image width=\"{width}\" height=\"{height}\" href=\"{normalized}\"/></svg>"));
        return true;
    }

    private static bool CompletePng(ReadOnlySpan<byte> data)
    {
        bool pixels = false;
        int at = 8;
        while (at <= data.Length - 12)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.Slice(at, 4));
            if (length < 0 || length > data.Length - at - 12) return false;
            var type = data.Slice(at + 4, 4);
            if (at == 8 && length != 13) return false;
            if (type.SequenceEqual("IDAT"u8)) pixels = true;
            if (type.SequenceEqual("IEND"u8)) return pixels && length == 0 && at + 12 == data.Length;
            at += length + 12;
        }
        return false;
    }

    private static readonly HashSet<string> SvgElements = new("svg g defs path rect circle ellipse line polyline polygon text tspan textPath linearGradient radialGradient stop pattern clipPath mask marker use image symbol title desc filter feBlend feColorMatrix feComponentTransfer feComposite feConvolveMatrix feDiffuseLighting feDisplacementMap feDistantLight feDropShadow feFlood feFuncA feFuncB feFuncG feFuncR feGaussianBlur feMerge feMergeNode feMorphology feOffset fePointLight feSpecularLighting feSpotLight feTile feTurbulence".Split(' '), StringComparer.Ordinal);
    private static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";

    private static RichText.Sprite? DecodeSvg(string body)
    {
        if (body.Length > MaximumAssetLength) return null;
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumAssetLength });
            root = XElement.Load(reader);
        }
        catch (XmlException) { return null; }
        if (root.Name.LocalName != "svg") return null;
        double width = Dimension((string?)root.Attribute("width")), height = Dimension((string?)root.Attribute("height"));
        string? viewBox = (string?)root.Attribute("viewBox");
        if (viewBox != null)
        {
            string[] values = viewBox.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 4 || values.Any(s => !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n))) return null;
            if (width <= 0) width = double.Parse(values[2], CultureInfo.InvariantCulture);
            if (height <= 0) height = double.Parse(values[3], CultureInfo.InvariantCulture);
        }
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096 || !double.IsFinite(width) || !double.IsFinite(height)) return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int elements = 0;
        foreach (var element in root.DescendantsAndSelf())
        {
            if (++elements > 32768 || !SvgElements.Contains(element.Name.LocalName) || element.Name.NamespaceName is not ("" or "http://www.w3.org/2000/svg")) return null;
            element.Name = SvgNamespace + element.Name.LocalName;
            foreach (var attribute in element.Attributes().ToArray())
            {
                if (attribute.IsNamespaceDeclaration) { attribute.Remove(); continue; }
                string name = attribute.Name.LocalName, value = attribute.Value;
                if (name == "class" || name.StartsWith("data-", StringComparison.OrdinalIgnoreCase)) { attribute.Remove(); continue; }
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || attribute.Name.NamespaceName is not ("" or "http://www.w3.org/1999/xlink" or "http://www.w3.org/XML/1998/namespace")) return null;
                if (name == "id" && (!SafeId(value) || !ids.Add(value))) return null;
                if (name == "href" && !(value.StartsWith('#') && SafeId(value[1..])) &&
                    !(element.Name.LocalName == "image" && TryImage(value, out _))) return null;
                if (!SafeValue(value)) return null;
            }
        }
        root.SetAttributeValue("width", width.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", height.ToString(CultureInfo.InvariantCulture));
        if (viewBox == null) root.SetAttributeValue("viewBox", FormattableString.Invariant($"0 0 {width} {height}"));
        return new(width, height, root.ToString(SaveOptions.DisableFormatting));
    }

    private static double Dimension(string? value)
    {
        if (value == null) return 0;
        value = value.Trim();
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)) value = value[..^2];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
    }

    private static bool SafeId(string value) => value.Length is > 0 and <= 256 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':');

    private static bool SafeValue(string value)
    {
        if (value.Contains('\\') || value.Contains('@') || value.Contains("/*", StringComparison.Ordinal) || value.Contains("expression", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
        int at = 0;
        while ((at = value.IndexOf("url", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int open = at + 3;
            while (open < value.Length && char.IsWhiteSpace(value[open])) open++;
            if (open >= value.Length || value[open] != '(') { at = open; continue; }
            int end = value.IndexOf(')', open + 1);
            if (end < 0) return false;
            string target = value[(open + 1)..end].Trim().Trim('"', '\'');
            if (!target.StartsWith('#') || !SafeId(target[1..])) return false;
            at = end + 1;
        }
        return true;
    }

    // Each painted occurrence gets its own definition IDs. Measurement and parsed
    // label caching never allocate IDs, so output remains stable across renders.
    internal static string Instantiate(string svg, string? tint = null)
    {
        if (tint == null && !svg.Contains(" id=", StringComparison.Ordinal)) return svg;
        var root = XElement.Parse(svg);
        if (tint != null && ManagedSkin.TryRgb(tint, out int rgb))
        {
            root.SetAttributeValue("fill", tint);
            var colors = new List<(XAttribute Attribute, int Gray)>();
            foreach (var element in root.Descendants())
                foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "fill" or "stroke" or "stop-color"))
                    if (attribute.Value is not ("none" or "transparent") && ManagedSkin.TryRgb(attribute.Value, out int color))
                        colors.Add((attribute, ((color >> 16) * 299 + (color >> 8 & 255) * 587 + (color & 255) * 114) / 1000));
            int minimum = colors.Count == 0 ? 0 : colors.Min(entry => entry.Gray);
            foreach (var (attribute, gray) in colors)
                attribute.Value = "#" + Hsluv.Tint(rgb, (gray - minimum) / 256.0).ToString("X6", CultureInfo.InvariantCulture);
        }
        string prefix = "sprite" + (++Current.Value!.instance).ToString(CultureInfo.InvariantCulture) + "-";
        var ids = root.DescendantsAndSelf().Attributes("id").ToDictionary(a => a.Value, a => prefix + a.Value, StringComparer.Ordinal);
        foreach (var attribute in root.DescendantsAndSelf().Attributes())
        {
            if (attribute.Name.LocalName == "id") attribute.Value = ids[attribute.Value];
            else if (attribute.Name.LocalName == "href" && attribute.Value.StartsWith('#') && ids.TryGetValue(attribute.Value[1..], out var target)) attribute.Value = "#" + target;
            else if (attribute.Value.Contains("url", StringComparison.OrdinalIgnoreCase))
                attribute.Value = LocalUrl().Replace(attribute.Value, match => ids.TryGetValue(match.Groups["id"].Value, out var mapped) ? "url(#" + mapped + ")" : match.Value);
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    [GeneratedRegex("^sprite\\s+\\$?(?<name>[-\\p{L}\\p{N}_./]+)(?:\\s+(?<body>.+)|(?<body>[\\[{].*))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();

    [GeneratedRegex("^\\[(?<w>[0-9]{1,5})x(?<h>[0-9]{1,5})/(?:(?<levels>4|8|16)(?<z>z)?|(?<color>color))\\]", RegexOptions.CultureInvariant)]
    private static partial Regex Dimensions();

    [GeneratedRegex("url\\(\\s*['\"]?#(?<id>[-\\p{L}\\p{N}_.:]+)['\"]?\\s*\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalUrl();
}
