using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

/// <summary>A standalone sequence sheet with inclusive, zero-based lines in the original @startuml source.</summary>
public sealed record SequencePage(string? Title, int StartLine, int EndLine, int Width, int Height, string Svg);

/// <summary>Renders each sheet of a supported sequence diagram for navigation, printing or individual file export.</summary>
public static class SequencePages
{
    public static bool TryRender(string source, [NotNullWhen(true)] out IReadOnlyList<SequencePage>? pages) => TryRender(source, null, out pages);

    /// <summary>Renders sheets with the same bounded preprocessing and virtual local includes as the single-SVG renderer.</summary>
    public static bool TryRender(string source, Func<string, string?>? resolveInclude, [NotNullWhen(true)] out IReadOnlyList<SequencePage>? pages)
        => TryRender(source, out pages, new PlantUmlRenderOptions { ResolveInclude = resolveInclude });

    public static bool TryRender(string source, [NotNullWhen(true)] out IReadOnlyList<SequencePage>? pages, PlantUmlRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        pages = null;
        if (!PlantUmlRenderer.TryPrepare(source, options, out source, out var kind, out var preprocessor) || kind != DiagramKind.Sequence) return false;
        Preprocessor? resourceResolver = preprocessor;
        string? ResolveAsset(string path, int line) => (resourceResolver ??= new(options.Resolve, options)).ResolveAsset(path, line);
        if (!SpriteCatalog.TryExtract(source, ResolveAsset, out source, out var sprites)) return false;
        using var spriteScope = sprites.BeginScope();
        var rendered = SequenceRenderer.RenderIndividual(source);
        if (rendered == null) return false;
        pages = preprocessor == null ? rendered : rendered.Select(page => page with
        {
            StartLine = preprocessor.SourceLine(page.StartLine),
            EndLine = preprocessor.SourceLine(page.EndLine),
            Svg = preprocessor.Remap(page.Svg)
        }).ToArray();
        return true;
    }
}

internal static partial class SequenceRenderer
{
    private const int PageGutter = 20;
    private static int Scaled(Diagram diagram, int value) => (int)Math.Ceiling(value * diagram.Skin.Dpi / 96.0);
    // Repeated headers and lifelines can otherwise amplify small page-break-only source into gigantic exports.
    private const int MaximumRepeatedElements = 32_768;

    private sealed record Slice(Diagram Sheet, int Start, int End, int Offset);

    internal static string? TryRender(string source)
    {
        var d = Parse(source);
        if (d == null) return null;
        if (d.Breaks.Count == 0) return WriteSvg(d);
        var slices = SplitPages(d);
        if (slices == null) return null;
        int height = slices.Sum(p => Scaled(p.Sheet, p.Sheet.Height)) + PageGutter * (slices.Count - 1);
        if (height > MaximumExtent) return null;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Scaled(d, d.Width)}\" height=\"{height}\" viewBox=\"0 0 {Scaled(d, d.Width)} {height}\">");
        int y = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            var sheet = slices[i].Sheet;
            svg.Append(WriteSvg(sheet, PageAttributes(sheet, i, y)));
            y += Scaled(sheet, sheet.Height) + PageGutter;
        }
        return svg.Append("</svg>").ToString();
    }

    internal static IReadOnlyList<SequencePage>? RenderIndividual(string source)
    {
        var d = Parse(source);
        if (d == null) return null;
        if (d.Breaks.Count == 0) return [new(d.Title, d.FirstLine, d.LastLine, Scaled(d, d.Width), Scaled(d, d.Height), WriteSvg(d))];
        var slices = SplitPages(d);
        if (slices == null) return null;
        var result = new SequencePage[slices.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var sheet = slices[i].Sheet;
            result[i] = new(sheet.Title, sheet.FirstLine, sheet.LastLine, Scaled(sheet, sheet.Width), Scaled(sheet, sheet.Height), WriteSvg(sheet, PageAttributes(sheet, i, 0)));
        }
        return result;
    }

    private static string PageAttributes(Diagram sheet, int index, int y)
    {
        var title = sheet.Title == null ? "" : " data-page-title=\"" + SvgText.Escape(sheet.Title).Replace("\n", "&#10;", StringComparison.Ordinal) + "\"";
        return FormattableString.Invariant($" x=\"0\" y=\"{y}\" data-sequence-page=\"{index + 1}\" data-start-line=\"{sheet.FirstLine}\" data-end-line=\"{sheet.LastLine}\"{title}");
    }

    // Intersect the *already laid out* logical event stream with each page. Participants,
    // globally numbered messages and horizontal constraints are shared. An activation or
    // fragment spanning a boundary keeps its original lifetime and source identity on both sides.
    private static List<Slice>? SplitPages(Diagram d)
    {
        var result = new List<Slice>(d.Breaks.Count + 1);
        int cost = (d.Breaks.Count + 1) * (d.Lanes.Count + (d.Legend?.Length ?? 0));
        if (cost > MaximumRepeatedElements) return null;
        int firstStep = 0, start = d.HeadBottom, firstLine = d.FirstLine, titleLine = d.TitleLine;
        string? title = d.Title;
        for (int i = 0; i <= d.Breaks.Count; i++)
        {
            bool last = i == d.Breaks.Count;
            int limit = last ? d.Steps.Count : d.Breaks[i];
            Step? boundary = last ? null : d.Steps[limit];
            int end = boundary?.Top ?? d.LifelineEnd;
            var heading = StyleTitle.Measure(title, d.Skin, 18);
            int header = d.HeaderHeight + HeadingHeight(heading);
            int dy = header + (i == 0 ? 0 : 10) - start;
            int lifelineEnd = Math.Max(header + 20, end + dy + (last ? 0 : 6));
            int height = lifelineEnd + (d.Footbox ? d.HeaderHeight : 0) + 10;
            if (d.Legend != null) height += 26 + StyleText.Height(d.Legend, d.Skin.Size("legendfontsize", 13));
            if (height > MaximumExtent) return null;
            var sheet = new Diagram
            {
                Title = title, Heading = heading, TitleLine = titleLine, FirstLine = firstLine, LastLine = boundary?.Line - 1 ?? d.LastLine,
                Legend = d.Legend, LegendLine = d.LegendLine, LegendWidth = d.LegendWidth, LegendAlignment = d.LegendAlignment,
                Skin = d.Skin, Width = d.Width, Height = height, HeaderHeight = d.HeaderHeight, HeadBottom = header,
                LifelineEnd = lifelineEnd, Footbox = d.Footbox, IdPrefix = FormattableString.Invariant($"p{i+1}-")
            };
            foreach (var lane in d.Lanes)
            {
                var copy = lane.Copy();
                copy.ShowHead = lane.Creation == null || lane.Creation.Line >= sheet.FirstLine && lane.Creation.Line <= sheet.LastLine;
                if (copy.HeadTop != null) copy.HeadTop += dy;
                int born = lane.LifeStart ?? d.HeadBottom, died = lane.LifeEnd ?? d.LifelineEnd;
                if (born > end || died < start)
                {
                    copy.LifeStart = 1;
                    copy.LifeEnd = 0;
                }
                else
                {
                    copy.LifeStart = born < start ? header : born + dy;
                    copy.LifeEnd = died > end ? lifelineEnd : died + dy;
                }
                sheet.Lanes.Add(copy);
            }
            for (int j = firstStep; j < limit; j++) sheet.Steps.Add(d.Steps[j].Translate(dy));
            result.Add(new(sheet, start, end, dy));
            if (boundary == null) break;
            firstStep = limit + 1;
            start = end;
            firstLine = titleLine = boundary.Line;
            title = boundary.Text.Length == 0 ? null : boundary.Text;
        }

        // Binary search the first possible sheet for each interval. Work scales with
        // intervals actually painted, including long-lived bars spanning many sheets.
        foreach (var bar in d.Bars)
        {
            for (int i = FirstSlice(result, bar.Start); i < result.Count && result[i].Start <= bar.End; i++)
            {
                var slice = result[i];
                if (bar.Start == slice.End && bar.Line > slice.Sheet.LastLine) continue;
                if (++cost > MaximumRepeatedElements) return null;
                int barTop = bar.Start < slice.Start ? slice.Sheet.HeadBottom : bar.Start + slice.Offset;
                int barBottom = bar.End > slice.End ? slice.Sheet.LifelineEnd : bar.End + slice.Offset;
                slice.Sheet.Bars.Add(bar with { Start = barTop, End = barBottom });
            }
        }
        foreach (var frame in d.Frames)
        {
            for (int i = FirstSlice(result, frame.Start); i < result.Count && result[i].Start < frame.End; i++)
            {
                var slice = result[i];
                if (frame.Start >= slice.End) continue;
                if (++cost > MaximumRepeatedElements) return null;
                bool begins = frame.Start >= slice.Start, ends = frame.End <= slice.End;
                slice.Sheet.Frames.Add(new(frame.Nest,
                    begins ? frame.Start + slice.Offset : slice.Sheet.HeadBottom,
                    ends ? frame.End + slice.Offset : slice.Sheet.LifelineEnd,
                    begins, ends));
            }
        }
        return result;
    }

    private static int FirstSlice(List<Slice> slices, int y)
    {
        int low = 0, high = slices.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (slices[mid].End < y) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
