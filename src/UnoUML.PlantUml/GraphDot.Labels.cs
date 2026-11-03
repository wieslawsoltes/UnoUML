using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace UnoUML.PlantUml;

internal static partial class GraphRenderer
{
    private sealed record DotFont(int Size, string Family, string Color, bool Bold = false, bool Italic = false,
        bool Underline = false, bool Strike = false, bool Overline = false, int Script = 0)
    {
        internal int ActualSize => Script == 0 ? Size : Math.Max(1, (int)Math.Ceiling(Size * .75));
        internal double Shift => Script > 0 ? -Size * .4 : Script < 0 ? Size * .25 : 0;
    }
    private readonly record struct DotRect(int X, int Y, int Width, int Height);
    private sealed record DotRun(string Text, DotFont Font, int EditStart = -1, int EditLength = 0)
    {
        internal int Width
        {
            get
            {
                double width = Font.Family.Contains("Courier", StringComparison.OrdinalIgnoreCase) || Font.Family.Contains("monospace", StringComparison.OrdinalIgnoreCase)
                    ? Text.EnumerateRunes().Sum(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format ? 0 : rune.Value >= 0x2e80 ? Font.ActualSize : Font.ActualSize * .6)
                    : SvgText.MeasurePlain(Text, Font.ActualSize);
                return (int)Math.Ceiling(width * (Font.Bold ? 1.06 : 1));
            }
        }
    }
    private sealed class DotRow(List<DotRun> runs, string alignment)
    {
        internal readonly List<DotRun> Runs = runs;
        internal readonly string Alignment = alignment;
        internal int Width => Runs.Sum(run => run.Width);
        internal int Ascent => (int)Math.Ceiling(Runs.Select(run => run.Font.ActualSize * .82 - run.Font.Shift).DefaultIfEmpty(11).Max());
        internal int Descent => (int)Math.Ceiling(Runs.Select(run => run.Font.ActualSize * .25 + run.Font.Shift).DefaultIfEmpty(4).Max());
        internal int Height => Ascent + Descent;
    }

    private abstract class DotLabel
    {
        internal int Width, Height, X, Y, ActualWidth, ActualHeight;
        internal string Port = "";
        internal int EditStart = -1, EditLength;
        internal string EditKind = "";
        internal virtual void Place(int x, int y, int width, int height) { X = x; Y = y; ActualWidth = width; ActualHeight = height; }
        internal virtual void Ports(Dictionary<string, DotRect> ports)
        {
            if (Port.Length != 0) ports.TryAdd(Port, new(X, Y, ActualWidth, ActualHeight));
        }
        internal abstract void Draw(StringBuilder svg);
        internal abstract string Plain { get; }
    }

    private sealed class DotTextLabel(List<DotRow> rows) : DotLabel
    {
        internal readonly List<DotRow> Rows = rows;
        internal override string Plain => string.Join("\n", Rows.Select(row => string.Concat(row.Runs.Select(run => run.Text))));
        internal void Measure()
        {
            Width = Rows.Select(row => row.Width).DefaultIfEmpty(0).Max();
            Height = Rows.Sum(row => row.Height) + Math.Max(0, Rows.Count - 1) * 2;
        }
        internal override void Draw(StringBuilder svg)
        {
            if (EditStart >= 0) DotFieldStart(svg, EditStart, EditLength, EditKind);
            int y = Y + (ActualHeight - Height) / 2;
            foreach (var row in Rows)
            {
                int x = X + (row.Alignment == "left" ? 0 : row.Alignment == "right" ? ActualWidth - row.Width : (ActualWidth - row.Width) / 2);
                foreach (var run in row.Runs)
                {
                    var font = run.Font;
                    if (run.EditStart >= 0) DotFieldStart(svg, run.EditStart, run.EditLength, "dot-html-text");
                    svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y + row.Ascent + font.Shift:0.###}\" font-size=\"{font.ActualSize}\" font-family=\"{SvgText.Escape(font.Family)}\" fill=\"{SvgText.Escape(font.Color)}\" xml:space=\"preserve\"");
                    if (font.Bold) svg.Append(" font-weight=\"bold\"");
                    if (font.Italic) svg.Append(" font-style=\"italic\"");
                    if (font.Underline || font.Strike || font.Overline)
                        svg.Append(" text-decoration=\"").Append(string.Join(' ', new[] { font.Underline ? "underline" : "", font.Strike ? "line-through" : "", font.Overline ? "overline" : "" }.Where(part => part.Length != 0))).Append('"');
                    svg.Append('>').Append(SvgText.Escape(run.Text)).Append("</text>");
                    if (run.EditStart >= 0) svg.Append("</g>");
                    x += run.Width;
                }
                y += row.Height + 2;
            }
            if (EditStart >= 0) svg.Append("</g>");
        }
    }

    private sealed class DotRecordLabel(List<DotLabel> children, bool horizontal, string stroke, double thickness) : DotLabel
    {
        internal readonly List<DotLabel> Children = children;
        internal readonly bool Horizontal = horizontal;
        internal override string Plain => string.Join(" | ", Children.Select(child => child.Plain));
        internal void Measure()
        {
            Width = Horizontal ? Children.Sum(child => child.Width) : Children.Max(child => child.Width);
            Height = Horizontal ? Children.Max(child => child.Height) : Children.Sum(child => child.Height);
        }
        internal override void Place(int x, int y, int width, int height)
        {
            base.Place(x, y, width, height);
            int cursor = 0, extra = (Horizontal ? width - Width : height - Height) / Children.Count;
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                int length = i + 1 == Children.Count ? (Horizontal ? width : height) - cursor : (Horizontal ? child.Width : child.Height) + extra;
                child.Place(x + (Horizontal ? cursor : 0), y + (Horizontal ? 0 : cursor), Horizontal ? length : width, Horizontal ? height : length);
                cursor += length;
            }
        }
        internal override void Ports(Dictionary<string, DotRect> ports)
        {
            base.Ports(ports);
            foreach (var child in Children) child.Ports(ports);
        }
        internal override void Draw(StringBuilder svg)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (i != 0)
                {
                    int x = Horizontal ? child.X : X, y = Horizontal ? Y : child.Y;
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{x} {y}l{(Horizontal ? 0 : ActualWidth)} {(Horizontal ? ActualHeight : 0)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{thickness:0.###}\"/>");
                }
                child.Draw(svg);
            }
        }
    }

    private sealed class DotCellLabel(DotLabel content, int padding, string align, string valign) : DotLabel
    {
        internal readonly DotLabel Content = content;
        internal int Row, Column, RowSpan = 1, ColumnSpan = 1;
        internal int Border;
        internal string Fill = "none", Stroke = "black", Link = "", Target = "", Tooltip = "", Sides = "LTRB";
        internal bool Fixed;
        internal override string Plain => Content.Plain;
        internal void Measure(int minimumWidth, int minimumHeight)
        {
            Width = Math.Max(minimumWidth, Content.Width + 2 * padding);
            Height = Math.Max(minimumHeight, Content.Height + 2 * padding);
            if (Fixed) { Width = Math.Max(1, minimumWidth); Height = Math.Max(1, minimumHeight); }
        }
        internal override void Place(int x, int y, int width, int height)
        {
            base.Place(x, y, width, height);
            int contentWidth = Math.Min(Content.Width, Math.Max(0, width - padding * 2));
            int contentHeight = Math.Min(Content.Height, Math.Max(0, height - padding * 2));
            if (Content is DotImageLabel image)
            {
                var dimensions = image.Fit(Math.Max(1, width - padding * 2), Math.Max(1, height - padding * 2));
                contentWidth = align is "LEFT" or "RIGHT" ? Math.Min(Content.Width, dimensions.Width) : dimensions.Width;
                contentHeight = valign is "TOP" or "BOTTOM" ? Math.Min(Content.Height, dimensions.Height) : dimensions.Height;
            }
            else if (align == "TEXT" && Content is DotTextLabel) contentWidth = Math.Max(0, width - padding * 2);
            int left = align == "LEFT" ? padding : align == "RIGHT" ? width - contentWidth - padding : (width - contentWidth) / 2;
            int top = valign == "TOP" ? padding : valign == "BOTTOM" ? height - contentHeight - padding : (height - contentHeight) / 2;
            Content.Place(x + left, y + top, contentWidth, contentHeight);
        }
        internal override void Ports(Dictionary<string, DotRect> ports) { base.Ports(ports); Content.Ports(ports); }
        internal override void Draw(StringBuilder svg)
        {
            bool link = DotLinkStart(svg, Link, Target, Tooltip);
            if (Fill != "none") svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{X}\" y=\"{Y}\" width=\"{ActualWidth}\" height=\"{ActualHeight}\" fill=\"{Fill}\"/>");
            if (Border > 0) DotBoxSides(svg, X, Y, ActualWidth, ActualHeight, Sides, Stroke, Border);
            Content.Draw(svg);
            if (link) svg.Append("</a>");
        }
    }

    private sealed class DotTableLabel(List<DotCellLabel> cells, int[] columns, int[] rows, int spacing, int border) : DotLabel
    {
        internal readonly List<DotCellLabel> Cells = cells;
        internal string Fill = "none", Stroke = "black", Link = "", Target = "", Tooltip = "", Sides = "LTRB";
        internal bool Rounded;
        internal override string Plain => string.Join("\n", Cells.GroupBy(cell => cell.Row).Select(row => string.Join(" ", row.Select(cell => cell.Plain))));
        internal void Measure(int minimumWidth, int minimumHeight)
        {
            Width = Math.Max(minimumWidth, columns.Sum() + spacing * (columns.Length + 1) + border * 2);
            Height = Math.Max(minimumHeight, rows.Sum() + spacing * (rows.Length + 1) + border * 2);
        }
        internal override void Place(int x, int y, int width, int height)
        {
            base.Place(x, y, width, height);
            int extraX = Math.Max(0, width - columns.Sum() - spacing * (columns.Length + 1) - 2 * border);
            int extraY = Math.Max(0, height - rows.Sum() - spacing * (rows.Length + 1) - 2 * border);
            int[] widths = columns.Select((size, i) => size + extraX / columns.Length + (i < extraX % columns.Length ? 1 : 0)).ToArray();
            int[] heights = rows.Select((size, i) => size + extraY / rows.Length + (i < extraY % rows.Length ? 1 : 0)).ToArray();
            var xs = new int[widths.Length + 1]; var ys = new int[heights.Length + 1];
            xs[0] = x + spacing + border; ys[0] = y + spacing + border;
            for (int i = 0; i < widths.Length; i++) xs[i + 1] = xs[i] + widths[i] + spacing;
            for (int i = 0; i < heights.Length; i++) ys[i + 1] = ys[i] + heights[i] + spacing;
            foreach (var cell in Cells)
                cell.Place(xs[cell.Column], ys[cell.Row], xs[cell.Column + cell.ColumnSpan] - xs[cell.Column] - spacing,
                    ys[cell.Row + cell.RowSpan] - ys[cell.Row] - spacing);
        }
        internal override void Ports(Dictionary<string, DotRect> ports) { base.Ports(ports); foreach (var cell in Cells) cell.Ports(ports); }
        internal override void Draw(StringBuilder svg)
        {
            bool link = DotLinkStart(svg, Link, Target, Tooltip);
            if (Fill != "none") svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{X}\" y=\"{Y}\" width=\"{ActualWidth}\" height=\"{ActualHeight}\" rx=\"{(Rounded ? 8 : 0)}\" fill=\"{Fill}\"/>");
            if (border > 0 && Rounded)
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{X}\" y=\"{Y}\" width=\"{ActualWidth}\" height=\"{ActualHeight}\" rx=\"8\" fill=\"none\" stroke=\"{Stroke}\" stroke-width=\"{border}\"/>");
            else if (border > 0) DotBoxSides(svg, X, Y, ActualWidth, ActualHeight, Sides, Stroke, border);
            foreach (var cell in Cells) cell.Draw(svg);
            if (link) svg.Append("</a>");
        }
    }

    private sealed class DotImageLabel(RichText.Sprite image, string scale = "false") : DotLabel
    {
        internal override string Plain => "";
        internal (int Width, int Height) Fit(int width, int height)
        {
            string mode = scale.ToLowerInvariant();
            if (mode is not ("false" or "true" or "width" or "height" or "both")) throw InvalidDot();
            if (mode == "true")
            {
                double factor = Math.Min(width / image.Width, height / image.Height);
                return (Math.Max(1, (int)Math.Round(image.Width * factor)), Math.Max(1, (int)Math.Round(image.Height * factor)));
            }
            return (mode is "width" or "both" ? width : Math.Min(width, Width), mode is "height" or "both" ? height : Math.Min(height, Height));
        }
        internal override void Draw(StringBuilder svg)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({X} {Y}) scale({ActualWidth / image.Width:0.######} {ActualHeight / image.Height:0.######})\">");
            svg.Append(SpriteCatalog.Instantiate(image.Svg)).Append("</g>");
        }
    }

    private static DotLabel DotPlainLabel(string value, DotFont font)
    {
        var rows = new List<DotRow>();
        if (value.Length == 0) return new DotTextLabel(rows);
        var text = new StringBuilder();
        void Row(string align) { rows.Add(new([new(text.ToString(), font)], align)); text.Clear(); }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                c = value[++i];
                if (c is 'n' or 'l' or 'r') { Row(c == 'l' ? "left" : c == 'r' ? "right" : "center"); continue; }
            }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n') i++;
                Row("center");
                continue;
            }
            text.Append(c);
        }
        if (text.Length != 0 || rows.Count == 0) Row("center");
        var label = new DotTextLabel(rows);
        label.Measure();
        return label;
    }

    private static string DotSubstitute(string value, string graph, string node = "", string tail = "", string head = "", string edge = "")
    {
        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[++i];
                string? replacement = next switch { 'G' => graph, 'N' => node, 'T' => tail, 'H' => head, 'E' => edge, _ => null };
                if (replacement != null) text.Append(replacement.Replace("\\", "\\\\", StringComparison.Ordinal));
                else text.Append('\\').Append(next);
            }
            else text.Append(c);
        }
        return text.ToString();
    }

    private static DotLabel DotRecord(string value, DotToken source, DotFont font, bool horizontal, string stroke, double thickness)
    {
        int position = 0, fields = 0;
        int[]? offsets = value == source.Value ? DotCharacterOffsets(source) : null;
        DotLabel Parse(bool direction, int depth)
        {
            if (depth > 32) throw InvalidDot();
            var children = new List<DotLabel>();
            do
            {
                if (++fields > 1024) throw InvalidDot();
                if (position < value.Length && value[position] == '{')
                {
                    position++;
                    children.Add(Parse(!direction, depth + 1));
                    if (position == value.Length || value[position++] != '}') throw InvalidDot();
                }
                else
                {
                    var text = new StringBuilder();
                    string port = "";
                    int textStart = position;
                    while (position < value.Length && value[position] is not ('|' or '}'))
                    {
                        char c = value[position++];
                        if (c == '<' && text.ToString().Trim().Length == 0)
                        {
                            int close = value.IndexOf('>', position);
                            if (close < 0 || close - position > 256 || port.Length != 0) throw InvalidDot();
                            port = value[position..close].Trim(); position = close + 1;
                            text.Clear(); textStart = position;
                        }
                        else if (c == '\\' && position < value.Length) text.Append(c).Append(value[position++]);
                        else if (c is '{' or '<' or '>') throw InvalidDot();
                        else text.Append(c);
                    }
                    string raw = text.ToString();
                    int leading = raw.Length - raw.TrimStart().Length;
                    int trimmedLength = raw.Trim().Length;
                    var content = DotPlainLabel(raw.Trim(), font);
                    if (offsets != null && trimmedLength > 0)
                    {
                        content.EditStart = offsets[textStart + leading];
                        content.EditLength = offsets[textStart + leading + trimmedLength] - content.EditStart;
                        content.EditKind = "dot-record-text";
                    }
                    var leaf = new DotCellLabel(content, 4, "CENTER", "MIDDLE") { Port = port };
                    leaf.Measure(0, 0);
                    children.Add(leaf);
                }
                if (position == value.Length || value[position] != '|') break;
                position++;
            } while (true);
            var record = new DotRecordLabel(children, direction, stroke, thickness);
            record.Measure();
            return record;
        }
        var result = Parse(horizontal, 0);
        if (position != value.Length) throw InvalidDot();
        return result;
    }

    private static DotLabel DotHtml(string value, DotToken source, DotFont font)
    {
        var textSpans = new List<(int Start, int Length)>();
        if (source.Start >= 0 && value == source.Value)
        {
            for (int at = 0; at < value.Length;)
            {
                if (value[at] == '<')
                {
                    if (value.AsSpan(at).StartsWith("<!--"))
                    {
                        int end = value.IndexOf("-->", at + 4, StringComparison.Ordinal);
                        if (end < 0) throw InvalidDot();
                        at = end + 3;
                        continue;
                    }
                    char quote = '\0';
                    do
                    {
                        char c = value[at++];
                        if (quote != '\0') { if (c == quote) quote = '\0'; }
                        else if (c is '\'' or '"') quote = c;
                        else if (c == '>') break;
                    } while (at < value.Length);
                }
                else
                {
                    int start = at;
                    while (at < value.Length && value[at] != '<') at++;
                    textSpans.Add((source.Start + 1 + start, at - start));
                }
            }
        }
        // Graphviz accepts named HTML entities beyond XML's five built-ins. Decode
        // each entity to numeric XML references, without turning text into markup.
        value = Regex.Replace(value, @"&([A-Za-z][A-Za-z0-9]+);", match =>
        {
            string decoded = WebUtility.HtmlDecode(match.Value);
            if (decoded == match.Value) return match.Value;
            return string.Concat(decoded.EnumerateRunes().Select(rune => "&#" + rune.Value.ToString(CultureInfo.InvariantCulture) + ";"));
        }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 };
        using var reader = XmlReader.Create(new StringReader("<dot-label>" + value + "</dot-label>"), settings);
        var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        if (root.Descendants().Take(4097).Count() > 4096) throw InvalidDot();
        var textNodes = root.DescendantNodes().OfType<XText>().ToArray();
        var metadata = new Dictionary<XText, (int Start, int Length)>();
        if (textNodes.Length == textSpans.Count)
            for (int i = 0; i < textNodes.Length; i++) metadata.Add(textNodes[i], textSpans[i]);
        return HtmlContent(root.Nodes(), font, 0, metadata, source.Line);
    }

    private static DotLabel HtmlContent(IEnumerable<XNode> source, DotFont font, int depth, Dictionary<XText, (int Start, int Length)> metadata, int sourceLine, string defaultAlignment = "CENTER")
    {
        if (depth > 32) throw InvalidDot();
        var nodes = source.ToArray();
        var significant = nodes.Where(node => node is not XComment && (node is not XText text || !string.IsNullOrWhiteSpace(text.Value))).ToArray();
        if (significant.Length == 1 && significant[0] is XElement only)
        {
            string name = only.Name.LocalName.ToUpperInvariant();
            if (name == "TABLE") return HtmlTable(only, font, depth + 1, metadata, sourceLine);
            if (name is "FONT" or "B" or "I" or "U" or "O" or "S" or "SUB" or "SUP")
            {
                if (only.Descendants().Any(element => element.Name.LocalName.Equals("TABLE", StringComparison.OrdinalIgnoreCase)))
                    return HtmlContent(only.Nodes(), HtmlFont(only, font), depth + 1, metadata, sourceLine, defaultAlignment);
            }
            if (name == "IMG")
            {
                string uri = HtmlValue(only, "SRC");
                var image = SpriteCatalog.ResolveImage(uri, sourceLine);
                if (image == null) throw InvalidDot();
                return new DotImageLabel(image, HtmlValue(only, "SCALE", "false")) { Width = (int)Math.Ceiling(image.Width), Height = (int)Math.Ceiling(image.Height) };
            }
        }
        var rows = new List<DotRow>();
        var runs = new List<DotRun>();
        void Read(IEnumerable<XNode> children, DotFont current, int nesting)
        {
            if (nesting > 32) throw InvalidDot();
            foreach (var node in children)
            {
                if (node is XComment) continue;
                if (node is XText text)
                {
                    string plain = text.Value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Replace("\t", "", StringComparison.Ordinal);
                    if (plain.Length != 0)
                    {
                        bool hasSpan = metadata.TryGetValue(text, out var span);
                        runs.Add(new(plain, current, hasSpan ? span.Start : -1, hasSpan ? span.Length : 0));
                    }
                    continue;
                }
                if (node is not XElement element) throw InvalidDot();
                string name = element.Name.LocalName.ToUpperInvariant();
                if (name == "BR")
                {
                    string alignment = HtmlValue(element, "ALIGN", defaultAlignment).ToLowerInvariant();
                    if (alignment is not ("left" or "right" or "center")) throw InvalidDot();
                    rows.Add(new(runs.Count == 0 ? [new("", current)] : runs, alignment));
                    runs = [];
                }
                else if (name is "FONT" or "B" or "I" or "U" or "O" or "S" or "SUB" or "SUP") Read(element.Nodes(), HtmlFont(element, current), nesting + 1);
                else throw InvalidDot();
            }
        }
        Read(nodes, font, depth);
        if (runs.Count != 0 || rows.Count == 0) rows.Add(new(runs.Count == 0 ? [new("", font)] : runs, defaultAlignment.ToLowerInvariant()));
        var label = new DotTextLabel(rows);
        label.Measure();
        return label;
    }

    private static DotFont HtmlFont(XElement element, DotFont inherited)
    {
        return element.Name.LocalName.ToUpperInvariant() switch
        {
            "B" => inherited with { Bold = true },
            "I" => inherited with { Italic = true },
            "U" => inherited with { Underline = true },
            "O" => inherited with { Overline = true },
            "S" => inherited with { Strike = true },
            "SUB" => inherited with { Script = -1 },
            "SUP" => inherited with { Script = 1 },
            "FONT" => DotNamedFont(inherited with
            {
                Size = HtmlNumber(element, "POINT-SIZE", inherited.Size, 1, 256),
                Color = DotColor(HtmlValue(element, "COLOR", inherited.Color))
            }, HtmlValue(element, "FACE", inherited.Family)),
            _ => throw InvalidDot()
        };
    }

    private static DotLabel HtmlTable(XElement element, DotFont font, int depth, Dictionary<XText, (int Start, int Length)> metadata, int sourceLine)
    {
        if (DotBool(HtmlValue(element, "FIXEDSIZE")) || HtmlValue(element, "ROWS").Length != 0 || HtmlValue(element, "COLUMNS").Length != 0) throw InvalidDot();
        int border = HtmlNumber(element, "BORDER", 1, 0, 64), cellBorder = HtmlNumber(element, "CELLBORDER", border, 0, 64);
        int spacing = HtmlNumber(element, "CELLSPACING", 2, 0, 128), padding = HtmlNumber(element, "CELLPADDING", 2, 0, 128);
        string stroke = DotColor(HtmlValue(element, "COLOR", "black"));
        var cells = new List<DotCellLabel>();
        var occupied = new HashSet<(int Row, int Column)>();
        int row = 0, columnCount = 0, rowCount = 0;
        foreach (var rowElement in element.Elements())
        {
            if (rowElement.Name.LocalName.Equals("HR", StringComparison.OrdinalIgnoreCase)) throw InvalidDot();
            if (!rowElement.Name.LocalName.Equals("TR", StringComparison.OrdinalIgnoreCase) || row >= 128) throw InvalidDot();
            int column = 0;
            foreach (var cellElement in rowElement.Elements())
            {
                if (!cellElement.Name.LocalName.Equals("TD", StringComparison.OrdinalIgnoreCase)) throw InvalidDot();
                while (occupied.Contains((row, column))) column++;
                int columnSpan = HtmlNumber(cellElement, "COLSPAN", 1, 1, 128), rowSpan = HtmlNumber(cellElement, "ROWSPAN", 1, 1, 128);
                if (column + columnSpan > 128 || row + rowSpan > 128 || cells.Count >= 1024) throw InvalidDot();
                for (int r = row; r < row + rowSpan; r++)
                    for (int c = column; c < column + columnSpan; c++)
                        if (!occupied.Add((r, c))) throw InvalidDot();
                if (HtmlValue(cellElement, "CELLSPACING").Length != 0) throw InvalidDot();
                string alignment = HtmlValue(cellElement, "ALIGN", "CENTER").ToUpperInvariant(), verticalAlignment = HtmlValue(cellElement, "VALIGN", "MIDDLE").ToUpperInvariant();
                string lineAlignment = HtmlValue(cellElement, "BALIGN", "CENTER").ToUpperInvariant();
                if (alignment is not ("LEFT" or "RIGHT" or "CENTER" or "TEXT") || verticalAlignment is not ("TOP" or "BOTTOM" or "MIDDLE") || lineAlignment is not ("LEFT" or "RIGHT" or "CENTER")) throw InvalidDot();
                var content = HtmlContent(cellElement.Nodes(), font, depth + 1, metadata, sourceLine, lineAlignment);
                var cell = new DotCellLabel(content, HtmlNumber(cellElement, "CELLPADDING", padding, 0, 128),
                    alignment, verticalAlignment)
                {
                    Row = row, Column = column, RowSpan = rowSpan, ColumnSpan = columnSpan,
                    Border = HtmlNumber(cellElement, "BORDER", cellBorder, 0, 64),
                    Stroke = DotColor(HtmlValue(cellElement, "COLOR", stroke)), Fill = DotColor(HtmlValue(cellElement, "BGCOLOR", "none")),
                    Port = HtmlValue(cellElement, "PORT"), Link = HtmlValue(cellElement, "HREF"), Target = HtmlValue(cellElement, "TARGET"),
                    Tooltip = HtmlValue(cellElement, "TOOLTIP", HtmlValue(cellElement, "TITLE")), Sides = HtmlValue(cellElement, "SIDES", "LTRB").ToUpperInvariant(),
                    Fixed = DotBool(HtmlValue(cellElement, "FIXEDSIZE"))
                };
                cell.Measure(HtmlNumber(cellElement, "WIDTH", 0, 0, 16000), HtmlNumber(cellElement, "HEIGHT", 0, 0, 16000));
                cells.Add(cell);
                column += columnSpan;
                columnCount = Math.Max(columnCount, column);
                rowCount = Math.Max(rowCount, row + rowSpan);
            }
            row++;
        }
        if (row == 0 || cells.Count == 0 || rowCount > row) throw InvalidDot();
        var widths = new int[columnCount]; var heights = new int[rowCount];
        foreach (var cell in cells.OrderBy(cell => cell.ColumnSpan))
        {
            int deficit = cell.Width - spacing * (cell.ColumnSpan - 1);
            for (int c = cell.Column; c < cell.Column + cell.ColumnSpan; c++) deficit -= widths[c];
            for (int c = 0; c < cell.ColumnSpan && deficit > 0; c++) widths[cell.Column + c] += deficit / cell.ColumnSpan + (c < deficit % cell.ColumnSpan ? 1 : 0);
        }
        foreach (var cell in cells.OrderBy(cell => cell.RowSpan))
        {
            int deficit = cell.Height - spacing * (cell.RowSpan - 1);
            for (int r = cell.Row; r < cell.Row + cell.RowSpan; r++) deficit -= heights[r];
            for (int r = 0; r < cell.RowSpan && deficit > 0; r++) heights[cell.Row + r] += deficit / cell.RowSpan + (r < deficit % cell.RowSpan ? 1 : 0);
        }
        var table = new DotTableLabel(cells, widths, heights, spacing, border)
        {
            Stroke = stroke, Fill = DotColor(HtmlValue(element, "BGCOLOR", "none")), Port = HtmlValue(element, "PORT"),
            Link = HtmlValue(element, "HREF"), Target = HtmlValue(element, "TARGET"), Tooltip = HtmlValue(element, "TOOLTIP", HtmlValue(element, "TITLE")),
            Sides = HtmlValue(element, "SIDES", "LTRB").ToUpperInvariant(), Rounded = HtmlValue(element, "STYLE").Equals("ROUNDED", StringComparison.OrdinalIgnoreCase)
        };
        table.Measure(HtmlNumber(element, "WIDTH", 0, 0, 16000), HtmlNumber(element, "HEIGHT", 0, 0, 16000));
        return table;
    }

    private static string HtmlValue(XElement element, string name, string fallback = "") =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? fallback;

    private static void DotFieldStart(StringBuilder svg, int start, int length, string kind) =>
        svg.Append(CultureInfo.InvariantCulture, $"<g data-edit-start=\"{start}\" data-edit-length=\"{length}\" data-edit-kind=\"{kind}\" data-edit-role=\"label\">");

    private static void SuppressDotLabelEditing(DotLabel label)
    {
        label.EditStart = -1;
        if (label is DotTextLabel text)
            foreach (var row in text.Rows)
                for (int i = 0; i < row.Runs.Count; i++) row.Runs[i] = row.Runs[i] with { EditStart = -1 };
        if (label is DotCellLabel cell) SuppressDotLabelEditing(cell.Content);
        else if (label is DotRecordLabel record) foreach (var child in record.Children) SuppressDotLabelEditing(child);
        else if (label is DotTableLabel table) foreach (var child in table.Cells) SuppressDotLabelEditing(child);
    }

    private static int[]? DotCharacterOffsets(DotToken token)
    {
        if (token.Source == null || token.Composite) return null;
        var offsets = new int[token.Value.Length + 1];
        int at = token.Start + (token.Kind == DotTokenKind.Quoted ? 1 : 0), index = 0;
        int end = token.End - (token.Kind == DotTokenKind.Quoted ? 1 : 0);
        while (at < end)
        {
            int first = at;
            char c = token.Source[at++];
            if (token.Kind == DotTokenKind.Quoted && c == '\\' && at < end)
            {
                char next = token.Source[at];
                if (next is '\r' or '\n')
                {
                    at++;
                    if (next == '\r' && at < end && token.Source[at] == '\n') at++;
                    continue;
                }
                if (next == '"') at++;
                else if (next == '\\')
                {
                    if (index + 1 >= offsets.Length) return null;
                    offsets[index++] = first;
                    offsets[index++] = at++;
                    continue;
                }
            }
            if (index >= token.Value.Length) return null;
            offsets[index++] = first;
        }
        if (index != token.Value.Length) return null;
        offsets[index] = end;
        return offsets;
    }

    private static int HtmlNumber(XElement element, string name, int fallback, int minimum, int maximum)
    {
        string value = HtmlValue(element, name);
        if (value.Length == 0) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < minimum || number > maximum) throw InvalidDot();
        return (int)Math.Ceiling(number);
    }

    private static void DotBoxSides(StringBuilder svg, int x, int y, int width, int height, string sides, string stroke, int thickness)
    {
        var path = new StringBuilder();
        if (sides.Contains('L')) path.Append(CultureInfo.InvariantCulture, $"M{x} {y}v{height}");
        if (sides.Contains('T')) path.Append(CultureInfo.InvariantCulture, $"M{x} {y}h{width}");
        if (sides.Contains('R')) path.Append(CultureInfo.InvariantCulture, $"M{x + width} {y}v{height}");
        if (sides.Contains('B')) path.Append(CultureInfo.InvariantCulture, $"M{x} {y + height}h{width}");
        svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{path}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{thickness}\"/>");
    }
}
