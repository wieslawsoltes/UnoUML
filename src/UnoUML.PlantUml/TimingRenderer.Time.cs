using System.Globalization;
using System.Text.RegularExpressions;

namespace UnoUML.PlantUml;

internal static partial class TimingRenderer
{
    private enum TimeFormat { Decimal, Hour, Date }
    private readonly record struct TimeMark(decimal Value, TimeFormat Format = TimeFormat.Decimal, string? Pattern = null);

    [GeneratedRegex(@"^:(?<name>[\p{L}\p{N}_.]+)(?<offset>[+-](?:\d+(?:\.\d*)?|\.\d+))?$", Grammar)]
    private static partial Regex AnchorTime();
    [GeneratedRegex(@"^(?<clock>" + Identifier + @")\*(?<count>\d+)$", Grammar)]
    private static partial Regex ClockTime();

    // Keep input arithmetic decimal. Repeated relative ticks must resolve to the same
    // instant as their absolute equivalent before geometry is converted to doubles.
    private static bool TimeNumber(string text, out decimal value) => decimal.TryParse(text,
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) &&
        Math.Abs(value) <= 1_000_000_000_000;

    private static bool ReadTime(TimingDocument document, string text, TimeMark? basis, out TimeMark time)
    {
        time = default;
        if (text.Length == 0)
        {
            time = basis ?? default;
            return basis != null;
        }
        if (text[0] == ':')
        {
            var anchor = AnchorTime().Match(text);
            if (!anchor.Success || !document.Anchors.TryGetValue(anchor.Groups["name"].Value, out time)) return false;
            if (anchor.Groups["offset"].Success)
            {
                if (!TimeNumber(anchor.Groups["offset"].Value, out decimal delta)) return false;
                time = new(time.Value + delta);
            }
            return Math.Abs(time.Value) <= 1_000_000_000_000;
        }
        if (text.Contains('*'))
        {
            var clock = ClockTime().Match(text);
            if (!clock.Success || !document.Tracks.TryGetValue(clock.Groups["clock"].Value, out var track) ||
                track.Kind != "clock" || !TimeNumber(clock.Groups["count"].Value, out decimal count)) return false;
            // Clock coordinates use period * count. Pulse and offset affect the waveform.
            time = new(track.Period * count);
            return Math.Abs(time.Value) <= 1_000_000_000_000;
        }
        if (text.Contains('/'))
        {
            string[] pieces = text.Split('/');
            if (pieces.Length != 3 || !int.TryParse(pieces[0], out int year) || !int.TryParse(pieces[1], out int month) ||
                !int.TryParse(pieces[2], out int day) || year is < 1583 or > 9999 || month is < 1 or > 12 ||
                day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
            time = new(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                TimeFormat.Date, document.DatePattern);
            return true;
        }
        if (text.Contains(':'))
        {
            string[] pieces = text.Split(':');
            if (pieces.Length != 3 || !TimeNumber(pieces[0], out decimal hours) || !TimeNumber(pieces[1], out decimal minutes) ||
                !TimeNumber(pieces[2], out decimal seconds) || hours < 0 || minutes < 0 || seconds < 0) return false;
            time = new(hours * 3600 + minutes * 60 + seconds, TimeFormat.Hour);
            return time.Value <= 1_000_000_000_000;
        }
        bool relative = text[0] == '+';
        if (!TimeNumber(relative ? text[1..] : text, out decimal number)) return false;
        time = new(number + (relative ? basis?.Value ?? 0 : 0));
        return Math.Abs(time.Value) <= 1_000_000_000_000;
    }

    private static string? FormatTime(decimal value, TimeMark format)
    {
        if (format.Format == TimeFormat.Decimal) return value.ToString("0.############################", CultureInfo.InvariantCulture);
        // Date/clock formatters upstream require whole seconds.
        if (value != decimal.Truncate(value)) return null;
        if (format.Format == TimeFormat.Hour)
        {
            long seconds = (long)value;
            return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + ":" +
                ClockPart(seconds / 60 % 60) + ":" + ClockPart(seconds % 60);
        }
        if (value < -12212553600 || value > 253402300799) return null;
        var instant = DateTimeOffset.FromUnixTimeSeconds((long)value);
        return format.Pattern == null ? instant.ToString("MM/dd", CultureInfo.InvariantCulture) :
            PreprocessorDate.Format(instant, format.Pattern, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-US"));
    }

    // Java's %02d width includes a negative sign, unlike the CLR's "00" format.
    private static string ClockPart(long value) => value.ToString(value < 0 ? "0" : "00", CultureInfo.InvariantCulture);

    private static SortedDictionary<decimal, int>? AxisTicks(TimingDocument document, decimal first, decimal last)
    {
        if (document.ScaleTicks == 0 || document.ManualAxis) return document.Ticks;
        decimal count = decimal.Ceiling((last - first) / document.ScaleTicks);
        if (count > 2048) return null;
        var ticks = new SortedDictionary<decimal, int>();
        for (int index = 0; index <= count; index++)
        {
            decimal time = first + index * document.ScaleTicks;
            ticks.Add(time, document.Ticks.GetValueOrDefault(time, -1));
        }
        return ticks;
    }
}
