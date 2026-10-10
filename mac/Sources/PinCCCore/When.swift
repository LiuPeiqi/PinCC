import Foundation

/// Reminder grammar shared with When.cs; Foundation resolves local dates and DST.
public enum When {
    public static var nowMs: Int64 { toMs(Date()) }

    public static func toMs(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1_000).rounded(.down))
    }

    public static func fromMs(_ milliseconds: Int64) -> Date {
        Date(timeIntervalSince1970: Double(milliseconds) / 1_000)
    }

    public static func parse(_ text: String, now: Date = Date(), calendar: Calendar? = nil) -> Int64? {
        let calendar = calendar ?? localCalendar
        guard isValid(now, calendar: calendar) else { return nil }
        let text = text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            .replacingOccurrences(of: "：", with: ":")
            .replacingOccurrences(of: "／", with: "/")
            .replacingOccurrences(of: "星期", with: "周")
            .replacingOccurrences(of: "礼拜", with: "周")
        guard !text.isEmpty else { return nil }

        if let match = match(relativePattern, in: text) {
            guard let number = match[1].flatMap(Double.init), number.isFinite,
                  let unit = match[2] else { return nil }
            let seconds: Double
            switch unit {
            case "m", "min", "分", "分钟": seconds = number * 60
            case "h", "hr", "小时", "个小时", "钟头": seconds = number * 3_600
            case "d", "天": seconds = number * 86_400
            default: seconds = number * 604_800
            }
            // Reject enormous values before Foundation receives an unbounded date.
            guard seconds.isFinite, seconds <= 315_537_897_600 else { return nil }
            let wholeDays = Int(seconds / 86_400)
            guard let day = calendar.date(byAdding: .day, value: wholeDays, to: now) else { return nil }
            let at = day.addingTimeInterval(seconds - Double(wholeDays) * 86_400)
            return isValid(at, calendar: calendar) ? toMs(at) : nil
        }

        let today = calendar.startOfDay(for: now)
        var day: Date?
        var period: String?
        var weekdayRoll = false
        var rest = text
        if let match = match(datePattern, in: rest) {
            let yearText = match[1] ?? String(calendar.component(.year, from: now))
            guard let year = Int(yearText), let month = match[2].flatMap(Int.init),
                  let number = match[3].flatMap(Int.init),
                  let date = date(year: year, month: month, day: number, calendar: calendar) else { return nil }
            day = date
            if match[1] == nil && date < today {
                guard let next = calendar.date(byAdding: .year, value: 1, to: date) else { return nil }
                day = next
            }
            rest = match.removingMatch
        } else if let match = match(dayPattern, in: rest), let word = match[0] {
            let offset: Int
            switch word {
            case "今天", "today", "今早", "今晚", "tonight": offset = 0
            case "明天", "tomorrow", "明早", "明晚": offset = 1
            case "后天": offset = 2
            default: offset = 3
            }
            guard let next = calendar.date(byAdding: .day, value: offset, to: today) else { return nil }
            day = next
            switch word {
            case "今早", "明早": period = "早上"
            case "今晚", "明晚", "tonight": period = "晚上"
            default: break
            }
            rest = match.removingMatch
        } else if let match = match(weekdayPattern, in: rest), let word = match[2],
                  let target = weekdayNames.firstIndex(of: word == "天" ? "日" : word) {
            let weekday = calendar.component(.weekday, from: now) - 1
            let offset: Int
            if match[1] != nil {
                offset = 7 - (weekday + 6) % 7 + (target + 6) % 7
            } else {
                offset = (target - weekday + 7) % 7
                weekdayRoll = true
            }
            guard let next = calendar.date(byAdding: .day, value: offset, to: today) else { return nil }
            day = next
            rest = match.removingMatch
        }

        rest = rest.trimmingCharacters(in: .whitespacesAndNewlines)
        let time = parseTime(rest, period: period)
        if time.minutes == nil && (!rest.isEmpty || period != nil) { return nil }
        if let day {
            guard isValid(day, calendar: calendar),
                  var at = timeOnDay(day, minutes: time.minutes ?? 9 * 60, calendar: calendar) else { return nil }
            if weekdayRoll && at <= now {
                guard let next = calendar.date(byAdding: .day, value: 7, to: at) else { return nil }
                at = next
            } else if !time.exact && at <= now && day == today {
                let minutes = (calendar.component(.hour, from: now) + 1) * 60
                guard let next = timeOnDay(today, minutes: minutes, calendar: calendar) else { return nil }
                at = next
            }
            return isValid(at, calendar: calendar) ? toMs(at) : nil
        }

        guard let minutes = time.minutes,
              var candidateDay = calendar.date(byAdding: .day, value: -1, to: today),
              isValid(candidateDay, calendar: calendar) else { return nil }
        // A night can be 25:00: at 00:30, “晚上1点” still means the coming 01:00.
        for _ in 0..<3 {
            guard let at = timeOnDay(candidateDay, minutes: minutes, calendar: calendar) else { return nil }
            if at > now { return isValid(at, calendar: calendar) ? toMs(at) : nil }
            guard let next = calendar.date(byAdding: .day, value: 1, to: candidateDay) else { return nil }
            candidateDay = next
        }
        return nil
    }

    public static func format(_ milliseconds: Int64, now: Date = Date(), calendar: Calendar? = nil) -> String {
        let calendar = calendar ?? localCalendar
        let at = fromMs(milliseconds)
        guard isValid(at, calendar: calendar), isValid(now, calendar: calendar) else { return "" }
        let c = calendar.dateComponents([.year, .month, .day, .hour, .minute, .weekday], from: at)
        guard let year = c.year, let month = c.month, let day = c.day,
              let hour = c.hour, let minute = c.minute, let weekday = c.weekday else { return "" }
        let hm = "\(hour < 10 ? "0" : "")\(hour):\(minute < 10 ? "0" : "")\(minute)"
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: now), to: calendar.startOfDay(for: at)).day ?? 0
        switch days {
        case 0: return hm
        case 1: return "明天 " + hm
        case -1: return "昨天 " + hm
        case 2..<7: return "周" + weekdayNames[weekday - 1] + " " + hm
        default:
            return year == calendar.component(.year, from: now)
                ? "\(month)月\(day)日 \(hm)" : "\(year)/\(month)/\(day) \(hm)"
        }
    }

    public static func presets(now: Date = Date(), calendar: Calendar? = nil) -> [(label: String, at: Int64)] {
        let calendar = calendar ?? localCalendar
        guard isValid(now, calendar: calendar) else { return [] }
        let today = calendar.startOfDay(for: now)
        let hour = calendar.component(.hour, from: now)
        var result: [(label: String, at: Int64)] = []
        func append(_ label: String, day: Date?, minutes: Int) {
            guard let day, let at = timeOnDay(day, minutes: minutes, calendar: calendar),
                  isValid(at, calendar: calendar) else { return }
            result.append((label, toMs(at)))
        }
        if let future = calendar.date(byAdding: .hour, value: 1, to: now) {
            let c = calendar.dateComponents([.hour, .minute, .second, .nanosecond], from: future)
            let fraction = (c.second ?? 0) != 0 || (c.nanosecond ?? 0) != 0
            let minute = ((c.minute ?? 0) + (fraction ? 1 : 0) + 4) / 5 * 5
            append("1 小时后", day: calendar.startOfDay(for: future), minutes: (c.hour ?? 0) * 60 + minute)
        }
        if hour < 5 { append("今天上午", day: today, minutes: 9 * 60) }
        if hour < 13 { append("今天下午", day: today, minutes: 14 * 60) }
        else if hour < 19 { append("今晚", day: today, minutes: 20 * 60) }
        if hour >= 5 { append("明天上午", day: calendar.date(byAdding: .day, value: 1, to: today), minutes: 9 * 60) }
        let weekday = calendar.component(.weekday, from: now) - 1
        let toMonday = (1 - weekday + 7) % 7
        append("下周一", day: calendar.date(byAdding: .day, value: toMonday == 0 ? 7 : toMonday, to: today), minutes: 9 * 60)
        return result
    }

    private static var localCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        return calendar
    }

    private static func isValid(_ date: Date, calendar: Calendar) -> Bool {
        guard date.timeIntervalSince1970.isFinite else { return false }
        let c = calendar.dateComponents([.era, .year], from: date)
        return c.era == 1 && c.year.map { (1...9999).contains($0) } == true
    }

    private static func date(year: Int, month: Int, day: Int, calendar: Calendar) -> Date? {
        guard (1...9999).contains(year), (1...12).contains(month), (1...31).contains(day),
              let date = calendar.date(from: DateComponents(year: year, month: month, day: day)) else { return nil }
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return c.year == year && c.month == month && c.day == day ? date : nil
    }

    private static func timeOnDay(_ day: Date, minutes: Int, calendar: Calendar) -> Date? {
        guard let day = calendar.date(byAdding: .day, value: minutes / 1_440, to: day) else { return nil }
        return calendar.date(bySettingHour: minutes % 1_440 / 60, minute: minutes % 60, second: 0, of: day,
                             matchingPolicy: .nextTimePreservingSmallerComponents, repeatedTimePolicy: .last)
    }

    private static func parseTime(_ text: String, period: String?) -> (minutes: Int?, exact: Bool) {
        guard let match = match(timePattern, in: text) else { return (nil, false) }
        let period = match[1] ?? period
        guard let hourText = match[2] else {
            let defaults = ["早上": 8, "早晨": 8, "上午": 9, "中午": 12, "下午": 15, "傍晚": 18, "晚上": 20, "夜里": 22, "半夜": 24]
            return (period.flatMap { defaults[$0] }.map { $0 * 60 }, false)
        }
        guard var hour = Int(hourText), hour < 24 else { return (nil, true) }
        let minute: Int
        if let minuteText = match[3] {
            guard let parsed = Int(minuteText), parsed < 60 else { return (nil, true) }
            minute = parsed
        } else { minute = match[4] == nil ? 0 : 30 }
        switch period {
        case "下午", "傍晚": if hour < 12 { hour += 12 }
        case "中午": if hour < 11 { hour += 12 }
        case "晚上", "夜里", "半夜":
            if hour == 0 || hour == 12 { hour = 24 }
            else if hour <= 4 { hour += 24 }
            else if hour < 12 { hour += 12 }
        case "凌晨": if hour == 12 { hour = 0 }
        case nil:
            if match[5] == "pm" && hour < 12 { hour += 12 }
            else if match[5] == "am" && hour == 12 { hour = 0 }
        default: break
        }
        return (hour * 60 + minute, true)
    }

    private struct Match {
        let text: NSString
        let result: NSTextCheckingResult
        subscript(_ group: Int) -> String? {
            let range = result.range(at: group)
            return range.location == NSNotFound ? nil : text.substring(with: range)
        }
        var removingMatch: String { text.replacingCharacters(in: result.range, with: "") }
    }

    private static func match(_ pattern: String, in text: String) -> Match? {
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let result = regex.firstMatch(in: text, options: [], range: NSRange(text.startIndex..<text.endIndex, in: text)) else { return nil }
        return Match(text: text as NSString, result: result)
    }

    private static let weekdayNames = ["日", "一", "二", "三", "四", "五", "六"]
    private static let relativePattern = #"^\+?\s*(\d+(?:\.\d+)?)\s*(m|min|分钟|分|h|hr|个小时|小时|钟头|d|天|w|周)\s*(后|以后|later)?$"#
    private static let datePattern = #"(?:(\d{4})\s*[-/.年]\s*)?(\d{1,2})\s*[-/.月]\s*(\d{1,2})\s*[日号]?"#
    private static let dayPattern = #"大后天|今天|明天|后天|今早|今晚|明早|明晚|today|tomorrow|tonight"#
    private static let weekdayPattern = #"(下)?周([一二三四五六日天])"#
    private static let timePattern = #"^(凌晨|早上|早晨|上午|中午|下午|傍晚|晚上|夜里|半夜)?\s*(?:(\d{1,2})(?:\s*(?::|点|时)\s*(?:(\d{1,2})\s*分?|(半))?)?\s*(am|pm)?)?$"#
}
