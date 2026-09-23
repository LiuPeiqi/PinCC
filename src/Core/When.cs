using System.Globalization;
using System.Text.RegularExpressions;

namespace PinCC.Core;

/// <summary>Reminder times: quick presets, a forgiving parser (中文/English), and compact formatting.</summary>
public static partial class When
{
    public static long ToMs(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();
    public static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
    public static long NowMs => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public static List<(string Label, DateTime At)> Presets(DateTime now)
    {
        var today = now.Date;
        var list = new List<(string, DateTime)> { ("1 小时后", RoundUp(now.AddHours(1), 5)) };
        // Before dawn the coming morning is today's: "明天上午" would skip a whole day.
        if (now.Hour < 5) list.Add(("今天上午", today.AddHours(9)));
        if (now.Hour < 13) list.Add(("今天下午", today.AddHours(14)));
        else if (now.Hour < 19) list.Add(("今晚", today.AddHours(20)));
        if (now.Hour >= 5) list.Add(("明天上午", today.AddDays(1).AddHours(9)));
        int toMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        list.Add(("下周一", today.AddDays(toMonday == 0 ? 7 : toMonday).AddHours(9)));
        return list;
    }

    /// <summary>A reminder at or before now fires at once (callers may want to say so).</summary>
    public static bool IsPast(DateTime at, DateTime now) => at <= now;

    static DateTime RoundUp(DateTime t, int minutes)
    {
        var step = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTime((t.Ticks + step - 1) / step * step, t.Kind);
    }

    static DateTime NextHour(DateTime now) => now.Date.AddHours(now.Hour + 1);

    /// <summary>
    /// Parses "+30m", "2h", "90分钟", "1.5小时", "3天", "18:30", "18点半", "下午3点", "9pm", "晚上", "明天", "今晚",
    /// "明早 8点", "后天晚上", "周五", "下周一 10点", "9/25", "9月25日 14:00", "2026-09-25 08:00". Returns null if not
    /// understood (never throws). A bare time already past means its next occurrence; a date alone means 09:00 (a period
    /// alone its usual hour), or the next full hour when that has already passed today. Night hours belong to the night:
    /// "晚上12点" is 00:00 and "晚上1点" 01:00 of the next day.
    /// </summary>
    public static DateTime? Parse(string? text, DateTime now)
    {
        try { return ParseCore(text, now); }
        catch (Exception e) when (e is ArgumentException or OverflowException or FormatException) { return null; } // "99999999天", DateTime overflow
    }

    static DateTime? ParseCore(string? text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().ToLowerInvariant().Replace('：', ':').Replace('／', '/').Replace("星期", "周").Replace("礼拜", "周");

        var rel = RelRx().Match(s);
        if (rel.Success)
        {
            var n = double.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture);
            var u = rel.Groups[2].Value;
            var span = u switch
            {
                "m" or "min" or "分" or "分钟" => TimeSpan.FromMinutes(n),
                "h" or "hr" or "小时" or "个小时" or "钟头" => TimeSpan.FromHours(n),
                "d" or "天" => TimeSpan.FromDays(n),
                _ => TimeSpan.FromDays(7 * n), // w / 周
            };
            return now + span;
        }

        DateTime? day = null;
        string? period = null;
        bool weekdayRoll = false;
        var rest = s;
        var m = DateRx().Match(rest);
        if (m.Success)
        {
            int y = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : now.Year;
            int mo = int.Parse(m.Groups["mo"].Value), d = int.Parse(m.Groups["d"].Value);
            if (y is < 1 or > 9999 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
            day = new DateTime(y, mo, d);
            if (!m.Groups["y"].Success && day < now.Date) day = day.Value.AddYears(1);
            rest = rest.Remove(m.Index, m.Length);
        }
        else if ((m = DayWordRx().Match(rest)).Success)
        {
            day = now.Date.AddDays(m.Value switch
            {
                "今天" or "today" or "今早" or "今晚" or "tonight" => 0,
                "明天" or "tomorrow" or "明早" or "明晚" => 1,
                "后天" => 2,
                _ => 3,
            });
            period = m.Value switch { "今早" or "明早" => "早上", "今晚" or "明晚" or "tonight" => "晚上", _ => null };
            rest = rest.Remove(m.Index, m.Length);
        }
        else if ((m = WeekdayRx().Match(rest)).Success)
        {
            int target = "日一二三四五六天".IndexOf(m.Groups[2].Value[0]) % 7; // Sunday = 0
            if (m.Groups[1].Success)
            {
                // 下周X = that weekday in next (Monday-based) week
                var nextMonday = now.Date.AddDays(7 - ((int)now.DayOfWeek + 6) % 7);
                day = nextMonday.AddDays((target + 6) % 7);
            }
            else
            {
                day = now.Date.AddDays((target - (int)now.DayOfWeek + 7) % 7);
                weekdayRoll = true;
            }
            rest = rest.Remove(m.Index, m.Length);
        }

        var t = ParseTime(rest.Trim(), period, out bool given, out bool exact);
        if (t == null && given) return null;
        if (day == null)
        {
            if (t == null) return null;
            // The next occurrence; start from yesterday because "晚上1点" (25:00) said at 00:30 is still ahead tonight.
            var at = now.Date.AddDays(-1) + t.Value;
            while (IsPast(at, now)) at = at.AddDays(1);
            return at;
        }
        var result = day.Value + (t ?? TimeSpan.FromHours(9));
        if (weekdayRoll && IsPast(result, now)) result = result.AddDays(7);
        else if (!exact && IsPast(result, now) && day == now.Date) result = NextHour(now); // "今天" at 10:15 → 11:00, not 09:00
        return result;
    }

    /// <summary>
    /// Time of day from "18:30", "18点", "18点半", "下午3点15", "9am", "3:30pm", "晚上8点", or a period alone ("晚上" =
    /// 20:00, <paramref name="period"/> is implied by "今晚"/"明早"…). May exceed 24h for night hours ("晚上1点" = 25:00).
    /// given = there was something to parse; exact = an hour was stated.
    /// </summary>
    static TimeSpan? ParseTime(string s, string? period, out bool given, out bool exact)
    {
        given = s.Length > 0 || period != null;
        exact = false;
        var m = TimeRx().Match(s);
        if (!m.Success) return null;
        if (m.Groups["p"].Success) period = m.Groups["p"].Value;
        if (!m.Groups["h"].Success) return period == null ? null : DefaultHour(period) is int dh ? TimeSpan.FromHours(dh) : null;
        exact = true;
        int h = int.Parse(m.Groups["h"].Value);
        int min = m.Groups["min"].Success ? int.Parse(m.Groups["min"].Value) : m.Groups["half"].Success ? 30 : 0;
        if (h > 23 || min > 59) return null;
        h = period switch
        {
            "下午" or "傍晚" when h < 12 => h + 12,
            "中午" when h < 11 => h + 12,
            "晚上" or "夜里" or "半夜" => h switch { 0 or 12 => 24, <= 4 => h + 24, < 12 => h + 12, _ => h }, // 夜里2点 = 26:00
            "凌晨" when h == 12 => 0,
            null when m.Groups["ap"].Value == "pm" && h < 12 => h + 12,
            null when m.Groups["ap"].Value == "am" && h == 12 => 0,
            _ => h,
        };
        return new TimeSpan(h, min, 0);
    }

    /// <summary>The hour a period word alone means; 凌晨 needs an explicit hour.</summary>
    static int? DefaultHour(string period) => period switch
    {
        "早上" or "早晨" => 8, "上午" => 9, "中午" => 12, "下午" => 15, "傍晚" => 18, "晚上" => 20, "夜里" => 22, "半夜" => 24, _ => null,
    };

    /// <summary>"14:00" (today), "明天 09:00", "周四 09:00" (within a week), "9月25日 14:00", "2027/1/2 09:00".</summary>
    public static string Format(DateTime at, DateTime now)
    {
        var hm = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        int days = (at.Date - now.Date).Days;
        if (days == 0) return hm;
        if (days == 1) return "明天 " + hm;
        if (days == -1) return "昨天 " + hm;
        if (days is > 1 and < 7) return "周" + "日一二三四五六"[(int)at.DayOfWeek] + " " + hm;
        if (at.Year == now.Year) return $"{at.Month}月{at.Day}日 {hm}";
        return $"{at.Year}/{at.Month}/{at.Day} {hm}";
    }

    [GeneratedRegex(@"^\+?\s*(\d+(?:\.\d+)?)\s*(m|min|分钟|分|h|hr|个小时|小时|钟头|d|天|w|周)\s*(后|以后|later)?$")]
    private static partial Regex RelRx();

    [GeneratedRegex(@"(?:(?<y>\d{4})\s*[-/.年]\s*)?(?<mo>\d{1,2})\s*[-/.月]\s*(?<d>\d{1,2})\s*[日号]?")]
    private static partial Regex DateRx();

    [GeneratedRegex(@"大后天|今天|明天|后天|今早|今晚|明早|明晚|today|tomorrow|tonight")]
    private static partial Regex DayWordRx();

    [GeneratedRegex(@"(下)?周([一二三四五六日天])")]
    private static partial Regex WeekdayRx();

    [GeneratedRegex(@"^(?<p>凌晨|早上|早晨|上午|中午|下午|傍晚|晚上|夜里|半夜)?\s*(?:(?<h>\d{1,2})(?:\s*(?::|点|时)\s*(?:(?<min>\d{1,2})\s*分?|(?<half>半))?)?\s*(?<ap>am|pm)?)?$")]
    private static partial Regex TimeRx();
}
