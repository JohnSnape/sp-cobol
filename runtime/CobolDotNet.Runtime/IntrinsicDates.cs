// Date and time intrinsic functions (CURRENT-DATE, INTEGER-OF-DATE, FORMATTED-*, LOCALE-*, ...)
// and the ACCEPT/DISPLAY system names that go with them: ACCEPT FROM DATE/DAY/TIME,
// COMMAND-LINE, ARGUMENT-NUMBER/VALUE and ENVIRONMENT.
//
// Ported from libcob 3.2 (intrinsic.c, common.c) function by function, keeping its
// quirks so results match stock GnuCOBOL byte for byte:
//  - integer dates count from 1601-01-01 = day 1, valid up to 9999-12-31 = 3067671;
//  - an offset of -1..-59 minutes prints as "+00mm" (the hours carry the sign);
//  - SECONDS-PAST-MIDNIGHT and the default "current year" of YEAR-TO-YYYY & co. read the
//    real clock, never the COB_CURRENT_DATE override;
//  - COMBINED-DATETIME of a big day number overflows its 9-digit result.
// COB_CURRENT_DATE (fix "now" for tests: "2024/03/15 10:20:30", "@seconds-since-epoch")
// is read at startup like libcob's cob_init; SET ENVIRONMENT later does not change it
// (in libcob the re-check after a SET compares the wrong pointer, so it never fires).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    public const int EC_IMP_DISPLAY = 0x0602;
    public const int EC_IMP_UTC_UNKNOWN = 0x0603;

    // ================================================================ the clock

    /// <summary>libcob's struct cob_time.</summary>
    struct DtTime
    {
        public int year, month, day_of_month, day_of_week, day_of_year;
        public int hour, minute, second, nanosecond;
        public int is_dst, offset_known, utc_offset;
    }

    const int Dt_DTR_DATE = 0, Dt_DTR_TIME_NO_NANO = 1, Dt_DTR_FULL = 2;

    // COB_CURRENT_DATE as parsed by check_current_date (year == 0: not set)
    // (read with the other statics at startup, as cob_init does; parsed at first use)
    static readonly string dtCurrentDateEnv = Environment.GetEnvironmentVariable("COB_CURRENT_DATE");
    static DtTime dtConst;
    static bool dtConstCalculated, dtConstLoaded;

    static int DtDow(DayOfWeek d) => ((int)d + 6) % 7 + 1;      // 1 = Monday .. 7 = Sunday

    /// <summary>cob_get_current_date_and_time_from_os (Windows build: whole seconds, then milliseconds).</summary>
    static DtTime DtFromOs(int res)
    {
        var now = DateTimeOffset.Now;
        var l = now.DateTime;
        var t = new DtTime
        {
            year = l.Year, month = l.Month, day_of_month = l.Day,
            day_of_week = DtDow(l.DayOfWeek), day_of_year = l.DayOfYear,
            hour = l.Hour, minute = l.Minute, second = l.Second,
            is_dst = TimeZoneInfo.Local.IsDaylightSavingTime(l) ? 1 : 0,
            offset_known = 1, utc_offset = (int)now.Offset.TotalMinutes,
        };
        t.nanosecond = res <= Dt_DTR_TIME_NO_NANO ? 0 : l.Millisecond * 1000000;
        return t;
    }

    /// <summary>cob_get_current_datetime: the OS time, adjusted by COB_CURRENT_DATE.</summary>
    static DtTime DtNow(int res)
    {
        if (!dtConstLoaded) { dtConstLoaded = true; DtCheckCurrentDate(); }
        var t = DtFromOs(res);
        var c = dtConst;
        if (c.year != 0)
        {
            if (c.hour != -1) t.hour = c.hour;
            if (c.minute != -1) t.minute = c.minute;
            if (c.second != -1) t.second = c.second;
            if (c.nanosecond != -1) t.nanosecond = c.nanosecond;
            if (c.offset_known != 0) { t.offset_known = c.offset_known; t.utc_offset = c.utc_offset; }
            if (dtConstCalculated)
            {
                t.year = c.year; t.month = c.month; t.day_of_month = c.day_of_month;
                t.day_of_week = c.day_of_week; t.day_of_year = c.day_of_year;
                t.is_dst = c.is_dst;
            }
            else
            {
                bool calc = false;
                if (c.year != -1) { t.year = c.year; calc = true; }
                if (c.month != -1) { t.month = c.month; calc = true; }
                if (c.day_of_month != -1) { t.day_of_month = c.day_of_month; calc = true; }
                if (calc)
                {
                    var m = DtMktime(t.year, t.month - 1, t.day_of_month, t.hour, t.minute, t.second);
                    t.day_of_week = m.dow;
                    t.day_of_year = m.yday;
                }
            }
        }
        if (t.second >= 60) t.second = 59;
        return t;
    }

    /// <summary>
    /// mktime on a struct tm taken from localtime(now) with these fields replaced:
    /// normalizes out-of-range values (Feb 30 = Mar 1). The C runtime's mktime fails
    /// before 1970 / after 3000 and then changes nothing (weekday and day of year stay today's).
    /// </summary>
    static (int year, int mon0, int mday, int hour, int min, int sec, int dow, int yday) DtMktime(
        int year, int mon0, int mday, int hour, int min, int sec)
    {
        var today = DateTime.Now;
        try
        {
            var d = new DateTime(year, 1, 1).AddMonths(mon0).AddDays(mday - 1)
                    .AddHours(hour).AddMinutes(min).AddSeconds(sec);
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeZoneInfo.Local);
            if (utc >= DateTime.UnixEpoch && utc < new DateTime(3001, 1, 1))
                return (d.Year, d.Month - 1, d.Day, d.Hour, d.Minute, d.Second, DtDow(d.DayOfWeek), d.DayOfYear);
        }
        catch (ArgumentException) { }
        return (year, mon0, mday, hour, min, sec, DtDow(today.DayOfWeek), today.DayOfYear);
    }

    static bool DtIsDigit(byte c) => c >= (byte)'0' && c <= (byte)'9';
    static bool DtIsSpace(byte c) => c == ' ' || (c >= 9 && c <= 13);

    /// <summary>check_current_date: parse COB_CURRENT_DATE into the time constant.</summary>
    static void DtCheckCurrentDate()
    {
        string env = dtCurrentDateEnv;
        if (string.IsNullOrEmpty(env)) return;
        var s = new byte[env.Length + 8];          // NUL-terminated, with room to look ahead
        for (int k = 0; k < env.Length; k++) s[k] = (byte)env[k];
        int p = 0;
        void Warn() => DtWarning($"COB_CURRENT_DATE '{env}' is invalid");

        while (s[p] == '\'' || s[p] == '"' || DtIsSpace(s[p])) p++;

        if (s[p] == '@')
        {
            p++;
            long seconds = 0;
            while (DtIsDigit(s[p])) seconds = seconds * 10 + (s[p++] - '0');
            if (s[p] != 0 || seconds > 253402300799L) { Warn(); return; }
            // localtime (0), then seconds/minutes/hours/days put into it and normalized
            var e = DateTimeOffset.FromUnixTimeSeconds(0).ToLocalTime().DateTime;
            int sec = (int)(seconds % 60); seconds /= 60;
            int min = (int)(seconds % 60); seconds /= 60;
            int hour = (int)(seconds % 24); seconds /= 24;
            var m = DtMktime(e.Year, e.Month - 1, (int)seconds, hour, min, sec);
            dtConst = new DtTime
            {
                year = m.year, month = m.mon0 + 1, day_of_month = m.mday,
                hour = m.hour, minute = m.min, second = m.sec, nanosecond = -1,
                day_of_week = m.dow, day_of_year = m.yday,
            };
            return;
        }

        int yr = -1, mm = -1, dd = -1, hh = -1, mi = -1, ss = -1, ns = -1;
        int i, ret = 0, offset = 9999;
        bool haveTz = false;

        // one numeric part of exactly `want` digits (4 also allows 2 for the year),
        // or a template letter run (YYYY, MM, ...) meaning "keep the system's value"
        int Part(int want, byte letter, bool yearPart)
        {
            int v = 0;
            for (i = 0; s[p] != 0; p++)
            {
                if (!DtIsDigit(s[p])) break;
                v = v * 10 + (s[p] - '0');
                if (++i == want) { p++; break; }
            }
            bool ok = yearPart ? (i == 2 || i == 4) : i == want;
            if (!ok)
            {
                if (s[p] == letter) { while (s[p] == letter) p++; }
                else ret = 1;
                return -1;
            }
            return v;
        }

        if (s[p] != 0)
        {
            yr = Part(4, (byte)'Y', true);
            if (yr != -1 && yr < 100) yr += 2000;
            if (s[p] == '/' || s[p] == '-') p++;
        }
        if (s[p] != 0)
        {
            mm = Part(2, (byte)'M', false);
            if (mm != -1 && (mm < 1 || mm > 12)) ret = 1;
            if (s[p] == '/' || s[p] == '-') p++;
        }
        if (s[p] != 0)
        {
            dd = Part(2, (byte)'D', false);
            if (dd != -1 && (dd < 1 || dd > 31)) ret = 1;
        }
        if (s[p] != 0)
        {
            while (DtIsSpace(s[p])) p++;
            hh = Part(2, (byte)'H', false);
            if (hh > 23) ret = 1;
            if (s[p] == ':' || s[p] == '-') p++;
        }
        if (s[p] != 0)
        {
            mi = Part(2, (byte)'M', false);
            if (mi > 59) ret = 1;
            if (s[p] == ':' || s[p] == '-') p++;
        }
        if (s[p] != 0 && s[p] != 'Z' && s[p] != '+' && s[p] != '-')
        {
            ss = Part(2, (byte)'S', false);
            if (ss > 60) ret = 1;
        }
        if (s[p] != 0 && s[p] != 'Z' && s[p] != '+' && s[p] != '-')
        {
            ns = 0;
            if (s[p] == '.' || s[p] == ':') p++;
            for (i = 0; s[p] != 0; p++)
            {
                if (!DtIsDigit(s[p])) break;
                ns = ns * 10 + (s[p] - '0');
                if (++i == 9) { p++; break; }
            }
        }
        if (s[p] == 'Z')
        {
            offset = 0;
            haveTz = true;
        }
        else if (s[p] == '+' || s[p] == '-')
        {
            int len = 0;
            while (s[p + len] != 0) len++;
            var tz = new byte[7];
            for (int k = 0; k < 6 && k < len; k++) tz[k] = s[p + k];
            if (len == 3) { tz[3] = (byte)'0'; tz[4] = (byte)'0'; tz[5] = 0; }
            else if (len >= 5 && tz[3] == ':')
            {
                tz[3] = s[p + 4]; tz[4] = s[p + 4] == 0 ? (byte)0 : s[p + 5]; tz[5] = 0;
                len--;
            }
            if (len > 5) ret = 1;
            for (i = 1; i < 5 && tz[i] != 0; i++) if (!DtIsDigit(tz[i])) break;
            i--;
            if (i == 4)
            {
                offset = (tz[1] - '0') * 600 + (tz[2] - '0') * 60 + (tz[3] - '0') * 10 + (tz[4] - '0');
                if (tz[0] == '-') offset = -offset;
                haveTz = true;
            }
            else ret = 1;
        }

        if (ret != 0) { Warn(); return; }

        var now = DateTime.Now;
        var n = DtMktime(yr != -1 ? yr : now.Year, mm != -1 ? mm - 1 : now.Month - 1,
                         dd != -1 ? dd : now.Day, hh != -1 ? hh : now.Hour,
                         mi != -1 ? mi : now.Minute, ss != -1 ? ss : now.Second);
        var c = new DtTime
        {
            year = yr != -1 ? n.year : -1,
            month = mm != -1 ? n.mon0 + 1 : -1,
            day_of_month = dd != -1 ? n.mday : -1,
            hour = hh != -1 ? n.hour : -1,
            minute = mi != -1 ? n.min : -1,
            second = ss != -1 ? n.sec : -1,
            nanosecond = ns,
        };
        if (yr != -1 && mm != -1 && dd != -1)
        {
            dtConstCalculated = true;
            c.day_of_week = n.dow;
            c.day_of_year = n.yday;
            c.is_dst = 0;
        }
        else
        {
            c.day_of_week = c.day_of_year = c.is_dst = -1;
        }
        if (haveTz) { c.offset_known = 1; c.utc_offset = offset; }
        // a constant with no date part at all still counts as set (year -1, not 0)
        dtConst = c;
    }

    static void DtWarning(string msg)
    {
        if (Environment.GetEnvironmentVariable("COB_DISABLE_WARNINGS") is string w
            && (w == "1" || w.Equals("true", StringComparison.OrdinalIgnoreCase) || w.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            return;
        Err.Write(Latin1Bytes($"libcob: warning: {msg}\n"));
        Err.Flush();
    }

    // ================================================================ calendar arithmetic

    static readonly int[] DtNormalDays = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365 };
    static readonly int[] DtLeapDays = { 0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366 };
    static readonly int[] DtNormalMonthDays = { 0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    static readonly int[] DtLeapMonthDays = { 0, 31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

    const int DtDateStrMax = 10, DtTimeStrMax = 25, DtDateTimeStrMax = 36, DtTimeDecMax = 9;

    static int DtLeapYear(int y) => (y % 4 == 0 && y % 100 != 0) || y % 400 == 0 ? 1 : 0;
    static int DtDaysInYear(int y) => 365 + DtLeapYear(y);
    static bool DtInRange(int min, int max, int v) => min <= v && v <= max;
    static bool DtValidIntegerDate(int d) => DtInRange(1, 3067671, d);
    static bool DtValidYear(int y) => DtInRange(1601, 9999, y);
    static bool DtValidMonth(int m) => DtInRange(1, 12, m);
    static bool DtValidDayOfYear(int y, int d) => DtInRange(1, DtDaysInYear(y), d);
    static bool DtValidDayOfMonth(int y, int m, int d)
        => DtInRange(1, (DtLeapYear(y) != 0 ? DtLeapMonthDays : DtNormalMonthDays)[m], d);
    static bool DtValidTime(int s) => DtInRange(0, 86400, s);
    static bool DtValidOffsetTime(int o) => Math.Abs(o) < 1440;

    /// <summary>date_of_integer: year, month, day from days since 1600-12-31.</summary>
    static void DtDateOfInteger(int dayNum, out int year, out int month, out int day)
    {
        int days = dayNum, baseyear = 1601, leapyear = 365, i;
        while (days > leapyear)
        {
            days -= leapyear;
            ++baseyear;
            leapyear = DtDaysInYear(baseyear);
        }
        var table = DtLeapYear(baseyear) != 0 ? DtLeapDays : DtNormalDays;
        for (i = 0; i < 13; ++i)
        {
            if (i != 0 && days <= table[i]) { days -= table[i - 1]; break; }
        }
        year = baseyear; month = i; day = days;
    }

    /// <summary>day_of_integer: year and day of year.</summary>
    static void DtDayOfInteger(int dayNum, out int year, out int day)
    {
        int leapyear = 365, days = dayNum;
        year = 1601;
        while (days > leapyear)
        {
            days -= leapyear;
            ++year;
            leapyear = DtDaysInYear(year);
        }
        day = days;
    }

    static uint DtDaysUpToYear(int year)
    {
        uint total = 0;
        for (int y = 1601; y != year; y++) total += (uint)DtDaysInYear(y);
        return total;
    }

    static uint DtIntegerOfDate(int year, int month, int days)
        => DtDaysUpToYear(year) + (uint)(DtLeapYear(year) != 0 ? DtLeapDays : DtNormalDays)[month - 1] + (uint)days;

    static uint DtIntegerOfDay(int year, int days) => DtDaysUpToYear(year) + (uint)days;

    static int DtDayOfWeek(int dayNum) => (dayNum - 1) % 7;             // 0 = Monday

    static int DtIsoWeekOne(int dayNum, int dayOfYear)
    {
        int jan4 = dayNum - dayOfYear + 4;
        return jan4 - DtDayOfWeek(jan4);
    }

    static void DtIsoWeek(int dayNum, out int year, out int week)
    {
        DtDayOfInteger(dayNum, out year, out int dayOfYear);
        int dec29 = dayNum - dayOfYear + DtDaysInYear(year) - 2;
        int weekOne;
        if (dayNum >= dec29)
        {
            weekOne = DtIsoWeekOne(dayNum + DtDaysInYear(year), dayOfYear);
            if (dayNum < weekOne) weekOne = DtIsoWeekOne(dayNum, dayOfYear);
            else ++year;
        }
        else
        {
            weekOne = DtIsoWeekOne(dayNum, dayOfYear);
            if (dayNum < weekOne)
            {
                --year;
                weekOne = DtIsoWeekOne(dayNum - dayOfYear, DtDaysInYear(year));
            }
        }
        week = (dayNum - weekOne) / 7 + 1;
    }

    static int DtMaxWeek(int year)
    {
        int first = (int)DtIntegerOfDate(year, 1, 1);
        DtIsoWeek(first + DtDaysInYear(year) - 1, out _, out int week);
        return week;
    }

    // ================================================================ format strings

    const int Dt_DAYS_MMDD = 0, Dt_DAYS_DDD = 1, Dt_DAYS_WWWD = 2;
    const int Dt_EXTRA_NONE = 0, Dt_EXTRA_Z = 1, Dt_EXTRA_OFFSET_TIME = 2;

    struct DtDateFmt { public int days; public bool hyphens; }
    struct DtTimeFmt { public bool colons; public int decimals; public int extra; }

    /// <summary>A char of a C string: NUL past the end.</summary>
    static char DtAt(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <summary>A C string from a buffer: up to the first NUL.</summary>
    static string DtCStr(string s) { int n = s.IndexOf('\0'); return n < 0 ? s : s.Substring(0, n); }

    /// <summary>copy_data_to_null_terminated_str: the data up to the first space (or NUL), at most max chars.</summary>
    static string DtCopyNts(CobField f, int max)
    {
        var d = f.Span;
        int n = 0;
        while (n < d.Length && !DtIsSpace(d[n])) n++;
        if (n > max) n = max;
        return DtCStr(Latin1(d.Slice(0, n)));
    }

    static bool DtValidDateFormat(string f)
        => f is "YYYYMMDD" or "YYYY-MM-DD" or "YYYYDDD" or "YYYY-DDD" or "YYYYWwwD" or "YYYY-Www-D";

    static int DtSecondsDecimals(string s, int pointPos)
    {
        int n = 0;
        while (DtAt(s, ++pointPos) == 's') n++;
        return n;
    }

    static bool DtValidTimeFormat(string f, char decimalPoint)
    {
        bool colons;
        int pos;
        if (f.StartsWith("hhmmss", StringComparison.Ordinal)) { colons = false; pos = 6; }
        else if (f.StartsWith("hh:mm:ss", StringComparison.Ordinal)) { colons = true; pos = 8; }
        else return false;
        if (DtAt(f, pos) == decimalPoint)
        {
            int dp = DtSecondsDecimals(f, pos);
            pos += dp + 1;
            if (dp == 0 || dp > DtTimeDecMax) return false;
        }
        if (f.Length > pos)
        {
            string rest = f.Substring(pos);
            if (rest != "Z" && rest != (colons ? "+hh:mm" : "+hhmm")) return false;
        }
        return true;
    }

    /// <summary>split_around_t: the parts before and after the first 'T'; nonzero if a part was too long.</summary>
    static int DtSplitAroundT(string str, out string first, ref string second)
    {
        int i = 0, ret = 0;
        while (i < str.Length && str[i] != 'T') i++;
        int firstLen = i;
        if (i > DtDateStrMax) { firstLen = DtDateStrMax; ret = DtDateStrMax + 1; }
        first = str.Substring(0, firstLen);
        if (i < str.Length)
        {
            string rest = str.Substring(i + 1);
            if (rest.Length > DtTimeStrMax) { rest = rest.Substring(0, DtTimeStrMax); ret = DtTimeStrMax + 1 + i; }
            second = rest;
        }
        return ret;
    }

    static bool DtValidDateTimeFormat(string f, char decimalPoint)
    {
        string time = "";
        if (DtSplitAroundT(f, out string date, ref time) != 0) return false;
        if (!DtValidDateFormat(date) || !DtValidTimeFormat(time, decimalPoint)) return false;
        return DtParseDateFmt(date).hyphens == DtParseTimeFmt(time).colons;
    }

    static DtDateFmt DtParseDateFmt(string f) => new DtDateFmt
    {
        days = f is "YYYYMMDD" or "YYYY-MM-DD" ? Dt_DAYS_MMDD : f is "YYYYDDD" or "YYYY-DDD" ? Dt_DAYS_DDD : Dt_DAYS_WWWD,
        hyphens = DtAt(f, 4) == '-',
    };

    static DtTimeFmt DtParseTimeFmt(string f)
    {
        var t = new DtTimeFmt();
        int pos;
        if (f.StartsWith("hhmmss", StringComparison.Ordinal)) { t.colons = false; pos = 6; }
        else { t.colons = true; pos = 8; }
        if (DtAt(f, pos) == '.' || DtAt(f, pos) == ',')
        {
            t.decimals = DtSecondsDecimals(f, pos);
            pos += t.decimals + 1;
        }
        t.extra = f.Length > pos ? (f.Substring(pos) == "Z" ? Dt_EXTRA_Z : Dt_EXTRA_OFFSET_TIME) : Dt_EXTRA_NONE;
        return t;
    }

    // ================================================================ formatting

    static string DtD2(int v) => v.ToString("00", CultureInfo.InvariantCulture);
    static string DtD3(int v) => v.ToString("000", CultureInfo.InvariantCulture);
    static string DtD4(int v) => v.ToString("0000", CultureInfo.InvariantCulture);

    static string DtFormatDate(DtDateFmt f, int days)
    {
        string h = f.hyphens ? "-" : "";
        if (f.days == Dt_DAYS_MMDD)
        {
            DtDateOfInteger(days, out int y, out int m, out int d);
            return DtD4(y) + h + DtD2(m) + h + DtD2(d);
        }
        if (f.days == Dt_DAYS_DDD)
        {
            DtDayOfInteger(days, out int y, out int d);
            return DtD4(y) + h + DtD3(d);
        }
        DtIsoWeek(days, out int year, out int week);
        return DtD4(year) + h + "W" + DtD2(week) + h + (DtDayOfWeek(days) + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>add_decimal_digits: the decimal point and the fraction's digits (32-bit, as libcob on Windows).</summary>
    static void DtAddDecimals(StringBuilder b, int places, BigInteger fracValue, int fracScale)
    {
        b.Append(DecimalPoint);
        uint fraction = (uint)(BigInteger.Abs(fracValue) & uint.MaxValue);
        int scale = fracScale;
        while (scale != 0 && places != 0)
        {
            --scale;
            int pow = 1;
            for (int k = 0; k < scale; k++) pow = unchecked(pow * 10);
            b.Append((char)('0' + fraction / (uint)pow));
            fraction %= (uint)pow;
            --places;
        }
        b.Append('0', places);
    }

    /// <summary>add_offset_time: +hhmm or +hh:mm; "00000" when the offset is unknown.</summary>
    static string DtOffsetText(bool colon, int? offset)
    {
        if (offset is not int o) return "00000";
        short hours = (short)(o / 60);
        int minutes = Math.Abs(o) % 60;
        string hs = (hours >= 0 ? "+" : "") + hours.ToString("00", CultureInfo.InvariantCulture);
        string s = hs + (colon ? ":" : "") + ((ushort)minutes).ToString("00", CultureInfo.InvariantCulture);
        return s.Length > 6 ? s.Substring(0, 6) : s;
    }

    /// <summary>format_time: returns the day overflow (-1, 0, 1) from converting to UTC for 'Z'.</summary>
    static int DtFormatTime(DtTimeFmt f, int time, BigInteger fracValue, int fracScale, int? offset, out string text)
    {
        int hours = time / 3600;
        time %= 3600;
        int minutes = time / 60, seconds = time % 60, overflow = 0;
        if (f.extra == Dt_EXTRA_Z)
        {
            if (offset is not int o)
            {
                SetException(EC_IMP_UTC_UNKNOWN);
                text = "";
                return 0;
            }
            hours -= o / 60;
            minutes -= o % 60;
            if (minutes >= 60) { minutes -= 60; ++hours; }
            else if (minutes < 0) { minutes += 60; --hours; }
            if (hours >= 24) { hours -= 24; overflow = 1; }
            else if (hours < 0) { hours += 24; overflow = -1; }
        }
        var b = new StringBuilder();
        string c = f.colons ? ":" : "";
        b.Append(DtD2(hours)).Append(c).Append(DtD2(minutes)).Append(c).Append(DtD2(seconds));
        if (f.decimals != 0) DtAddDecimals(b, f.decimals, fracValue, fracScale);
        if (f.extra == Dt_EXTRA_Z) b.Append('Z');
        else if (f.extra == Dt_EXTRA_OFFSET_TIME) b.Append(DtOffsetText(f.colons, offset));
        text = b.ToString();
        return overflow;
    }

    static string DtFormatDateTime(DtDateFmt df, DtTimeFmt tf, int days, int seconds,
                                   BigInteger fracValue, int fracScale, int? offset)
    {
        int overflow = DtFormatTime(tf, seconds, fracValue, fracScale, offset, out string time);
        return DtFormatDate(df, days + overflow) + "T" + time;
    }

    /// <summary>get_fractional_seconds: the time's value minus its whole seconds.</summary>
    static (BigInteger value, int scale) DtFraction(CobField time)
    {
        int whole = cob_get_int(time);
        var (v, s) = Num.Get(time);
        return (v - (BigInteger)(uint)whole * Num.Pow10(s), s);
    }

    /// <summary>The system's UTC offset (get_system_offset_time_ptr).</summary>
    static int? DtSystemOffset()
    {
        var t = DtNow(Dt_DTR_FULL);
        return t.offset_known != 0 ? t.utc_offset : null;
    }

    /// <summary>A result of this length: the text's bytes, zero-filled past its end (C's memcpy from a zeroed buffer).</summary>
    static CobField DtResult(int length, string text)
    {
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, length);
        var d = f.data.Span(length);
        for (int i = 0; i < length && i < text.Length; i++) d[i] = (byte)text[i];
        return f;
    }

    static CobField DtSpaces(int length)
    {
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, length);
        f.data.Span(length).Fill((byte)' ');
        return f;
    }

    static CobField DtRefMod(CobField f, int offset, int length) => offset > 0 ? IntrRefMod(f, offset, length) : f;

    static CobField DtNumDisplay(int digits, string text)
    {
        var f = IntrField(TYPE_NUMERIC_DISPLAY, digits, 0, 0, digits);
        var d = f.data.Span(digits);
        for (int i = 0; i < digits; i++) d[i] = (byte)text[i];
        return f;
    }

    // ================================================================ validation of formatted values

    // each returns 0 when fine, else the 1-based position of the bad character
    static int DtTestCond(bool cond, ref int off) { if (cond) { ++off; return 0; } return off + 1; }
    static int DtTestChar(char want, string s, ref int off) => DtTestCond(DtAt(s, off) == want, ref off);
    static int DtTestRange(char min, char max, char c, ref int off) => DtTestCond(min <= c && c <= max, ref off);
    static int DtTestDigit(char c, ref int off) => DtTestRange('0', '9', c, ref off);

    static int DtTestYear(string d, ref int off, out int state)
    {
        int r;
        state = 0;
        if ((r = DtTestRange('1', '9', DtAt(d, off), ref off)) != 0) return r;
        state = DtAt(d, off - 1) - '0';
        if ((r = state != 1 ? DtTestDigit(DtAt(d, off), ref off) : DtTestRange('6', '9', DtAt(d, off), ref off)) != 0) return r;
        state = state * 10 + (DtAt(d, off - 1) - '0');
        if ((r = DtTestDigit(DtAt(d, off), ref off)) != 0) return r;
        state = state * 10 + (DtAt(d, off - 1) - '0');
        if ((r = state != 160 ? DtTestDigit(DtAt(d, off), ref off) : DtTestRange('1', '9', DtAt(d, off), ref off)) != 0) return r;
        state = state * 10 + (DtAt(d, off - 1) - '0');
        return 0;
    }

    static int DtTestHyphen(bool with, string d, ref int off) => with ? DtTestChar('-', d, ref off) : 0;

    static int DtTestMonth(string d, ref int off, out int month)
    {
        int r;
        month = 0;
        if ((r = DtTestCond(DtAt(d, off) == '0' || DtAt(d, off) == '1', ref off)) != 0) return r;
        int first = DtAt(d, off - 1) - '0';
        if ((r = first == 0 ? DtTestRange('1', '9', DtAt(d, off), ref off) : DtTestRange('0', '2', DtAt(d, off), ref off)) != 0) return r;
        month = first * 10 + (DtAt(d, off - 1) - '0');
        return 0;
    }

    static int DtTestDayOfMonth(string d, int year, int month, ref int off)
    {
        int r;
        int dim = (DtLeapYear(year) != 0 ? DtLeapMonthDays : DtNormalMonthDays)[month];
        char max1 = (char)('0' + dim / 10), max2 = (char)('0' + dim % 10);
        if ((r = DtTestRange('0', max1, DtAt(d, off), ref off)) != 0) return r;
        char first = DtAt(d, off - 1);
        if (first == '0') r = DtTestRange('1', '9', DtAt(d, off), ref off);
        else if (first != max1) r = DtTestDigit(DtAt(d, off), ref off);
        else r = DtTestRange('0', max2, DtAt(d, off), ref off);
        return r;
    }

    static int DtTestDayOfYear(string d, int year, ref int off)
    {
        int r;
        if ((r = DtTestRange('0', '3', DtAt(d, off), ref off)) != 0) return r;
        int state = DtAt(d, off - 1) - '0';
        if ((r = state != 3 ? DtTestDigit(DtAt(d, off), ref off) : DtTestRange('0', '6', DtAt(d, off), ref off)) != 0) return r;
        state = state * 10 + (DtAt(d, off - 1) - '0');
        if (state == 0) r = DtTestRange('1', '9', DtAt(d, off), ref off);
        else if (state != 36) r = DtTestDigit(DtAt(d, off), ref off);
        else r = DtTestRange('0', DtLeapYear(year) != 0 ? '6' : '5', DtAt(d, off), ref off);
        return r;
    }

    static int DtTestWeek(string d, int year, ref int off)
    {
        int r;
        if ((r = DtTestRange('0', '5', DtAt(d, off), ref off)) != 0) return r;
        int first = DtAt(d, off - 1) - '0';
        if (first == 0) r = DtTestRange('1', '9', DtAt(d, off), ref off);
        else if (first != 5) r = DtTestDigit(DtAt(d, off), ref off);
        else r = DtTestRange('0', DtMaxWeek(year) == 53 ? '3' : '2', DtAt(d, off), ref off);
        return r;
    }

    static int DtTestNoTrailingJunk(string s, int off, bool endOfString)
    {
        if (endOfString)
        {
            for (; DtAt(s, off) != '\0'; off++) if (DtAt(s, off) != ' ') return off + 1;
            return 0;
        }
        return DtAt(s, off) == '\0' ? 0 : off + 1;
    }

    static int DtTestFormattedDate(DtDateFmt f, string d, bool endOfString)
    {
        int off = 0, r;
        if ((r = DtTestYear(d, ref off, out int year)) != 0) return r;
        if ((r = DtTestHyphen(f.hyphens, d, ref off)) != 0) return r;
        if (f.days == Dt_DAYS_MMDD)
        {
            if ((r = DtTestMonth(d, ref off, out int month)) != 0) return r;
            if ((r = DtTestHyphen(f.hyphens, d, ref off)) != 0) return r;
            if ((r = DtTestDayOfMonth(d, year, month, ref off)) != 0) return r;
        }
        else if (f.days == Dt_DAYS_DDD)
        {
            if ((r = DtTestDayOfYear(d, year, ref off)) != 0) return r;
        }
        else
        {
            if ((r = DtTestChar('W', d, ref off)) != 0) return r;
            if ((r = DtTestWeek(d, year, ref off)) != 0) return r;
            if ((r = DtTestHyphen(f.hyphens, d, ref off)) != 0) return r;
            if ((r = DtTestRange('1', '7', DtAt(d, off), ref off)) != 0) return r;
        }
        return DtTestNoTrailingJunk(d, off, endOfString);
    }

    static int DtTestLess60(string t, ref int off)
    {
        int r;
        if ((r = DtTestRange('0', '5', DtAt(t, off), ref off)) != 0) return r;
        return DtTestDigit(DtAt(t, off), ref off);
    }

    static int DtTestHour(string t, ref int off)
    {
        int r;
        if ((r = DtTestRange('0', '2', DtAt(t, off), ref off)) != 0) return r;
        int first = DtAt(t, off - 1) - '0';
        return first != 2 ? DtTestDigit(DtAt(t, off), ref off) : DtTestRange('0', '3', DtAt(t, off), ref off);
    }

    static int DtTestColon(bool with, string t, ref int off) => with ? DtTestChar(':', t, ref off) : 0;

    static int DtTestFormattedTime(DtTimeFmt f, string t, char decimalPoint)
    {
        int off = 0, r;
        if ((r = DtTestHour(t, ref off)) != 0) return r;
        if ((r = DtTestColon(f.colons, t, ref off)) != 0) return r;
        if ((r = DtTestLess60(t, ref off)) != 0) return r;
        if ((r = DtTestColon(f.colons, t, ref off)) != 0) return r;
        if ((r = DtTestLess60(t, ref off)) != 0) return r;
        if (f.decimals != 0)
        {
            if ((r = DtTestChar(decimalPoint, t, ref off)) != 0) return r;
            for (int i = 0; i < f.decimals; ++i)
                if ((r = DtTestDigit(DtAt(t, off), ref off)) != 0) return r;
        }
        if (f.extra == Dt_EXTRA_Z)
        {
            if ((r = DtTestChar('Z', t, ref off)) != 0) return r;
        }
        else if (f.extra == Dt_EXTRA_OFFSET_TIME)
        {
            char c = DtAt(t, off);
            if (c == '+' || c == '-')
            {
                ++off;
                if ((r = DtTestHour(t, ref off)) != 0) return r;
                if ((r = DtTestColon(f.colons, t, ref off)) != 0) return r;
                if ((r = DtTestLess60(t, ref off)) != 0) return r;
            }
            else if (c == '0')
            {
                ++off;
                if ((r = DtTestChar('0', t, ref off)) != 0) return r;
                if ((r = DtTestChar('0', t, ref off)) != 0) return r;
                if ((r = DtTestColon(f.colons, t, ref off)) != 0) return r;
                if ((r = DtTestChar('0', t, ref off)) != 0) return r;
                if ((r = DtTestChar('0', t, ref off)) != 0) return r;
            }
            else return off + 1;
        }
        return DtTestNoTrailingJunk(t, off, true);
    }

    /// <summary>sscanf "%Nd": up to N digits at pos (the values here were validated first).</summary>
    static int DtScan(string s, int pos, int width)
    {
        int v = 0;
        for (int i = 0; i < width && char.IsAsciiDigit(DtAt(s, pos + i)); i++) v = v * 10 + (s[pos + i] - '0');
        return v;
    }

    static uint DtIntegerOfFormattedDate(DtDateFmt f, string d)
    {
        int year = DtScan(d, 0, 4);
        int start = 4 + (f.hyphens ? 1 : 0);
        if (f.days == Dt_DAYS_MMDD)
            return DtIntegerOfDate(year, DtScan(d, start, 2), DtScan(d, start + 2 + (f.hyphens ? 1 : 0), 2));
        if (f.days == Dt_DAYS_DDD)
            return DtIntegerOfDay(year, DtScan(d, start, 3));
        int monday = DtIsoWeekOne((int)DtDaysUpToYear(year) + 1, 1);
        int week = DtScan(d, start + 1, 2);
        int dow = DtScan(d, start + 3 + (f.hyphens ? 1 : 0), 1);
        return (uint)(monday + (week - 1) * 7 + dow - 1);
    }

    // ================================================================ the functions

    public static CobField cob_intr_current_date(int offset, int length)
    {
        var t = offset == 1 && length <= 14 ? DtNow(Dt_DTR_TIME_NO_NANO) : DtNow(Dt_DTR_FULL);
        string s = DtD4(t.year) + DtD2(t.month) + DtD2(t.day_of_month) + DtD2(t.hour) + DtD2(t.minute)
                 + DtD2(t.second) + DtD2(t.nanosecond / 10000000) + DtOffsetText(false, t.utc_offset);
        var f = DtResult(21, s);
        return offset != 0 ? IntrRefMod(f, offset, length) : f;
    }

    public static CobField cob_intr_combined_datetime(CobField srcdays, CobField srctime)
    {
        Global.cob_exception_code = 0;
        int days = cob_get_int(srcdays);
        if (DtValidIntegerDate(days))
        {
            var (tv, ts) = Num.Get(srctime);
            if (tv * Num.Pow10(0) <= new BigInteger(86400) * Num.Pow10(ts))
            {
                var time = new CobDecimal { value = tv, scale = ts };
                cob_decimal_div(time, new CobDecimal { value = 100000, scale = 0 });
                var sum = new CobDecimal { value = days, scale = 0 };
                cob_decimal_add(sum, time);
                return IntrDecimal(sum);
            }
        }
        SetException(EC_ARGUMENT_FUNCTION);
        return IntrUint(0);
    }

    public static CobField cob_intr_date_of_integer(CobField srcdays)
    {
        Global.cob_exception_code = 0;
        int days = cob_get_int(srcdays);
        if (!DtValidIntegerDate(days))
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return DtNumDisplay(8, "00000000");
        }
        DtDateOfInteger(days, out int y, out int m, out int d);
        return DtNumDisplay(8, DtD4(y) + DtD2(m) + DtD2(d));
    }

    public static CobField cob_intr_day_of_integer(CobField srcdays)
    {
        Global.cob_exception_code = 0;
        int days = cob_get_int(srcdays);
        if (!DtValidIntegerDate(days))
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return DtNumDisplay(7, "0000000");
        }
        DtDayOfInteger(days, out int y, out int d);
        return DtNumDisplay(7, DtD4((ushort)y) + DtD3((ushort)d));
    }

    public static CobField cob_intr_integer_of_date(CobField srcfield)
    {
        Global.cob_exception_code = 0;
        int indate = cob_get_int(srcfield);
        int year = indate / 10000;
        indate %= 10000;
        int month = indate / 100, days = indate % 100;
        if (!DtValidYear(year) || !DtValidMonth(month) || !DtValidDayOfMonth(year, month, days))
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return IntrUint(0);
        }
        return IntrUint(DtIntegerOfDate(year, month, days));
    }

    public static CobField cob_intr_integer_of_day(CobField srcfield)
    {
        Global.cob_exception_code = 0;
        int indate = cob_get_int(srcfield);
        int year = indate / 1000, days = indate % 1000;
        if (!DtValidYear(year) || !DtValidDayOfYear(year, days))
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return IntrUint(0);
        }
        return IntrUint(DtIntegerOfDay(year, days));
    }

    public static CobField cob_intr_test_date_yyyymmdd(CobField srcfield)
    {
        int indate = cob_get_int(srcfield);
        int year = indate / 10000;
        if (!DtValidYear(year)) return IntrUint(1);
        indate %= 10000;
        int month = indate / 100;
        if (!DtValidMonth(month)) return IntrUint(2);
        if (!DtValidDayOfMonth(year, month, indate % 100)) return IntrUint(3);
        return IntrUint(0);
    }

    public static CobField cob_intr_test_day_yyyyddd(CobField srcfield)
    {
        int indate = cob_get_int(srcfield);
        int year = indate / 1000;
        if (!DtValidYear(year)) return IntrUint(1);
        if (!DtValidDayOfYear(year, indate % 1000)) return IntrUint(2);
        return IntrUint(0);
    }

    /// <summary>The optional interval (default 50) and current year (default: this year, from the real clock).</summary>
    static void DtIntervalAndYear(int count, CobField[] p, out int interval, out int currentYear)
    {
        interval = count > 1 ? cob_get_int(p[1]) : 50;
        currentYear = count > 2 ? cob_get_int(p[2]) : DateTime.Now.Year;
    }

    static int DtWindow(int year, int maxyear)
        => maxyear % 100 >= year ? year + 100 * (maxyear / 100) : year + 100 * (maxyear / 100 - 1);

    public static CobField cob_intr_year_to_yyyy(int count, params CobField[] p)
    {
        Global.cob_exception_code = 0;
        int year = cob_get_int(p[0]);
        DtIntervalAndYear(count, p, out int interval, out int currentYear);
        int maxyear = currentYear + interval;
        if (year < 0 || year > 99 || !DtValidYear(currentYear) || maxyear < 1700 || maxyear > 9999)
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return IntrUint(0);
        }
        return IntrInt(DtWindow(year, maxyear));
    }

    public static CobField cob_intr_date_to_yyyymmdd(int count, params CobField[] p)
    {
        Global.cob_exception_code = 0;
        int year = cob_get_int(p[0]);
        int mmdd = year % 10000;
        year /= 10000;
        DtIntervalAndYear(count, p, out int interval, out int currentYear);
        int maxyear = currentYear + interval;
        // the unusual year checks are as specified in the standard (libcob's comment)
        if (year < 0 || year > 999999 || !DtValidYear(currentYear) || maxyear < 1700 || maxyear > 9999)
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return IntrUint(0);
        }
        return IntrInt(DtWindow(year, maxyear) * 10000 + mmdd);
    }

    public static CobField cob_intr_day_to_yyyyddd(int count, params CobField[] p)
    {
        Global.cob_exception_code = 0;
        int year = cob_get_int(p[0]);
        int days = year % 1000;
        year /= 1000;
        DtIntervalAndYear(count, p, out int interval, out int currentYear);
        int maxyear = currentYear + interval;
        if (year < 0 || year > 999999 || !DtValidYear(currentYear) || maxyear < 1700 || maxyear > 9999)
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return IntrUint(0);
        }
        return IntrInt(DtWindow(year, maxyear) * 1000 + days);
    }

    /// <summary>Seconds since local midnight from the real clock (libcob ignores COB_CURRENT_DATE here).</summary>
    public static CobField cob_intr_seconds_past_midnight()
    {
        var n = DateTime.Now;
        return IntrInt(n.Hour * 3600 + n.Minute * 60 + Math.Min(n.Second, 59));
    }

    public static CobField cob_intr_seconds_from_formatted_time(CobField formatField, CobField timeField)
    {
        char dp = DecimalPoint;
        var fd = formatField.Span;
        int strLength = 0;
        while (strLength < fd.Length && !DtIsSpace(fd[strLength])) strLength++;
        string format = DtCStr(Latin1(fd.Slice(0, Math.Min(strLength, 2 * (DtDateTimeStrMax + 1) - 1))));
        Global.cob_exception_code = 0;

        bool isDatetime = false;
        if (DtValidDateTimeFormat(format, dp)) isDatetime = true;
        else if (!DtValidTimeFormat(format, dp)) goto invalid;

        string timeFormat = format, timeStr = "";
        if (isDatetime)
        {
            DtSplitAroundT(format, out _, ref timeFormat);
            DtSplitAroundT(DtCStr(IntrText(timeField)), out _, ref timeStr);
        }
        else
        {
            var td = timeField.Span;
            timeStr = DtCStr(Latin1(td.Slice(0, (int)Math.Min(strLength, td.Length))));
        }

        var tf = DtParseTimeFmt(timeFormat);
        if (DtTestFormattedTime(tf, timeStr, dp) != 0) goto invalid;

        int total = DtScan(timeStr, 0, 2) * 3600 + DtScan(timeStr, tf.colons ? 3 : 2, 2) * 60
                  + DtScan(timeStr, tf.colons ? 6 : 4, 2);
        var sec = new CobDecimal { value = total, scale = 0 };
        if (tf.decimals != 0)
        {
            int frac = 0;
            for (int o = tf.colons ? 9 : 7, end = o + tf.decimals; o != end; ++o) frac = frac * 10 + (timeStr[o] - '0');
            cob_decimal_add(sec, new CobDecimal { value = frac, scale = tf.decimals });
        }
        return IntrDecimal(sec);

    invalid:
        SetException(EC_ARGUMENT_FUNCTION);
        return IntrUint(0);
    }

    public static CobField cob_intr_formatted_date(int offset, int length, CobField formatField, CobField daysField)
    {
        string format = DtCopyNts(formatField, DtDateStrMax);
        Global.cob_exception_code = 0;
        int days = cob_get_int(daysField);
        CobField f;
        if (!DtValidIntegerDate(days) || !DtValidDateFormat(format))
        {
            SetException(EC_ARGUMENT_FUNCTION);
            f = DtSpaces(format.Length);
        }
        else f = DtResult(format.Length, DtFormatDate(DtParseDateFmt(format), days));
        return DtRefMod(f, offset, length);
    }

    // FORMATTED-TIME (format, seconds [, offset]): the trailing int is 1 for SYSTEM-OFFSET
    public static CobField cob_intr_formatted_time(int offset, int length, int count,
                                                   CobField formatField, CobField timeField, int useSystemOffset)
        => DtFormattedTime(offset, length, count, formatField, timeField, null, useSystemOffset);

    public static CobField cob_intr_formatted_time(int offset, int length, int count,
                                                   CobField formatField, CobField timeField, CobField offsetField, int useSystemOffset)
        => DtFormattedTime(offset, length, count, formatField, timeField, offsetField, useSystemOffset);

    static CobField DtFormattedTime(int offset, int length, int count, CobField formatField, CobField timeField,
                                    CobField offsetField, int useSystemOffset)
    {
        if (count != 3 && count != 4)
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return DtRefMod(DtSpaces(0), offset, length);
        }
        string format = DtCopyNts(formatField, DtTimeStrMax);
        Global.cob_exception_code = 0;
        CobField f;
        int whole = cob_get_int(timeField);
        if (!DtValidTime(whole) || !DtValidTimeFormat(format, DecimalPoint)) goto invalid;
        var (fv, fs) = DtFraction(timeField);
        var tf = DtParseTimeFmt(format);
        int? off;
        if (useSystemOffset != 0) off = DtSystemOffset();
        else if (!DtTryOffset(count == 4 ? offsetField : null, out off)) goto invalid;
        DtFormatTime(tf, whole, fv, fs, off, out string text);
        f = DtResult(format.Length, text);
        return DtRefMod(f, offset, length);

    invalid:
        SetException(EC_ARGUMENT_FUNCTION);
        return DtRefMod(DtSpaces(format.Length), offset, length);
    }

    /// <summary>try_get_valid_offset_time: no field = offset 0.</summary>
    static bool DtTryOffset(CobField f, out int? offset)
    {
        if (f == null) { offset = 0; return true; }
        int o = cob_get_int(f);
        offset = o;
        return DtValidOffsetTime(o);
    }

    // FORMATTED-DATETIME (format, days, seconds [, offset])
    public static CobField cob_intr_formatted_datetime(int offset, int length, int count,
                                                       CobField fmtField, CobField daysField, CobField timeField, int useSystemOffset)
        => DtFormattedDateTime(offset, length, count, fmtField, daysField, timeField, null, useSystemOffset);

    public static CobField cob_intr_formatted_datetime(int offset, int length, int count,
                                                       CobField fmtField, CobField daysField, CobField timeField,
                                                       CobField offsetField, int useSystemOffset)
        => DtFormattedDateTime(offset, length, count, fmtField, daysField, timeField, offsetField, useSystemOffset);

    static CobField DtFormattedDateTime(int offset, int length, int count, CobField fmtField, CobField daysField,
                                        CobField timeField, CobField offsetField, int useSystemOffset)
    {
        if (count != 4 && count != 5)
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return DtRefMod(DtSpaces(0), offset, length);
        }
        string fmt = DtCopyNts(fmtField, DtDateTimeStrMax);
        Global.cob_exception_code = 0;
        if (!DtValidDateTimeFormat(fmt, DecimalPoint)) goto invalid;
        int days = cob_get_int(daysField);
        int whole = cob_get_int(timeField);
        if (!DtValidIntegerDate(days) || !DtValidTime(whole)) goto invalid;
        string timeFmt = "";
        if (DtSplitAroundT(fmt, out string dateFmt, ref timeFmt) != 0) goto invalid;
        var tf = DtParseTimeFmt(timeFmt);
        int? off;
        if (useSystemOffset != 0) off = DtSystemOffset();
        else if (!DtTryOffset(count == 5 ? offsetField : null, out off)) goto invalid;
        var df = DtParseDateFmt(dateFmt);
        var (fv, fs) = DtFraction(timeField);
        string text = DtFormatDateTime(df, tf, days, whole, fv, fs, off);
        return DtRefMod(DtResult(fmt.Length, text), offset, length);

    invalid:
        SetException(EC_ARGUMENT_FUNCTION);
        return DtRefMod(DtSpaces(fmt.Length), offset, length);
    }

    public static CobField cob_intr_test_formatted_datetime(CobField formatField, CobField datetimeField)
    {
        Global.cob_exception_code = 0;
        char dp = DecimalPoint;
        string fmt = DtCopyNts(formatField, DtDateTimeStrMax);
        string value = DtCopyNts(datetimeField, DtDateTimeStrMax);
        bool datePresent, timePresent;
        if (DtValidDateFormat(fmt)) { datePresent = true; timePresent = false; }
        else if (DtValidTimeFormat(fmt, dp)) { datePresent = false; timePresent = true; }
        else if (DtValidDateTimeFormat(fmt, dp)) { datePresent = timePresent = true; }
        else
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return IntrUint(0);
        }

        string dateFmt = "", timeFmt = "", date = "", time = "";
        if (datePresent && timePresent)
        {
            DtSplitAroundT(fmt, out dateFmt, ref timeFmt);
            DtSplitAroundT(value, out date, ref time);
        }
        else if (datePresent) { dateFmt = fmt; date = value; }
        else { timeFmt = fmt; time = value; }

        int timeOffset = datePresent ? date.Length + 1 : 0;
        if (datePresent)
        {
            int e = DtTestFormattedDate(DtParseDateFmt(dateFmt), date, !timePresent);
            if (e != 0) return IntrUint((uint)e);
        }
        if (datePresent && timePresent && DtAt(value, date.Length) != 'T')
            return IntrUint((uint)date.Length + 1);
        if (timePresent)
        {
            int e = DtTestFormattedTime(DtParseTimeFmt(timeFmt), time, dp);
            if (e != 0) return IntrUint((uint)(timeOffset + e));
        }
        return IntrUint(0);
    }

    public static CobField cob_intr_integer_of_formatted_date(CobField formatField, CobField dateField)
    {
        Global.cob_exception_code = 0;
        string fmt = DtCopyNts(formatField, DtDateTimeStrMax);
        string value = DtCopyNts(dateField, DtDateTimeStrMax);
        string dateFmt, date;
        string ignored = "";
        bool isDate = DtValidDateFormat(fmt);
        if (isDate) dateFmt = fmt;
        else if (DtValidDateTimeFormat(fmt, DecimalPoint)) DtSplitAroundT(fmt, out dateFmt, ref ignored);
        else goto invalid;
        var df = DtParseDateFmt(dateFmt);
        if (isDate) date = value;
        else DtSplitAroundT(value, out date, ref ignored);
        if (DtTestFormattedDate(df, date, true) != 0) goto invalid;
        return IntrUint(DtIntegerOfFormattedDate(df, date));

    invalid:
        SetException(EC_ARGUMENT_FUNCTION);
        return IntrUint(0);
    }

    public static CobField cob_intr_formatted_current_date(int offset, int length, CobField formatField)
    {
        string fmt = DtCopyNts(formatField, DtDateTimeStrMax);
        Global.cob_exception_code = 0;
        CobField f;
        if (!DtValidDateTimeFormat(fmt, DecimalPoint))
        {
            SetException(EC_ARGUMENT_FUNCTION);
            f = DtSpaces(fmt.Length);
        }
        else
        {
            string timeFmt = "";
            DtSplitAroundT(fmt, out string dateFmt, ref timeFmt);
            var t = DtNow(Dt_DTR_FULL);
            int days = (int)DtIntegerOfDate(t.year, t.month, t.day_of_month);
            int secs = t.hour * 3600 + t.minute * 60 + t.second;
            string text = DtFormatDateTime(DtParseDateFmt(dateFmt), DtParseTimeFmt(timeFmt), days, secs,
                                           (uint)t.nanosecond, 9, t.offset_known != 0 ? t.utc_offset : null);
            f = DtResult(fmt.Length, text);
        }
        return DtRefMod(f, offset, length);
    }

    // ---------------------------------------------------------------- locale

    static Dictionary<string, int> dtWinLocales;

    /// <summary>libcob's table of locale names (de_DE, ja_JP, ...) to Windows locale IDs.</summary>
    static int? DtWinLocale(string name)
    {
        if (dtWinLocales == null)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in DtWinTable.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = e.IndexOf('=');
                d.TryAdd(e.Substring(0, eq), int.Parse(e.Substring(eq + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            dtWinLocales = d;
        }
        return dtWinLocales.TryGetValue(name, out int id) ? id : null;
    }

    const string DtWinTable =
        "af_ZA=0436 am_ET=045e ar_AE=3801 ar_BH=3c01 ar_DZ=1401 ar_EG=0c01 ar_IQ=0801 ar_JO=2c01 " +
        "ar_KW=3401 ar_LB=3001 ar_LY=1001 ar_MA=1801 ar_OM=2001 ar_QA=4001 ar_SA=0401 ar_SY=2801 " +
        "ar_TN=1c01 ar_YE=2401 arn_CL=047a as_IN=044d az_Cyrl_AZ=082c az_Latn_AZ=042c ba_RU=046d " +
        "be_BY=0423 bg_BG=0402 bn_IN=0445 bo_BT=0851 bo_CN=0451 br_FR=047e bs_Cyrl_BA=201a " +
        "bs_Latn_BA=141a ca_ES=0403 cs_CZ=0405 cy_GB=0452 da_DK=0406 de_AT=0c07 de_CH=0807 de_DE=0407 " +
        "de_LI=1407 de_LU=1007 dsb_DE=082e dv_MV=0465 el_GR=0408 en_029=2409 en_AU=0c09 en_BZ=2809 " +
        "en_CA=1009 en_GB=0809 en_IE=1809 en_IN=4009 en_JM=2009 en_MY=4409 en_NZ=1409 en_PH=3409 " +
        "en_SG=4809 en_TT=2c09 en_US=0409 en_ZA=1c09 en_ZW=3009 es_AR=2c0a es_BO=400a es_CL=340a " +
        "es_CO=240a es_CR=140a es_DO=1c0a es_EC=300a es_ES=040a es_GT=100a es_HN=480a es_MX=080a " +
        "es_NI=4c0a es_PA=180a es_PE=280a es_PR=500a es_PY=3c0a es_SV=440a es_US=540a es_UY=380a " +
        "es_VE=200a et_EE=0425 eu_ES=042d fa_IR=0429 fi_FI=040b fil_PH=0464 fo_FO=0438 fr_BE=080c " +
        "fr_CA=0c0c fr_CH=100c fr_FR=040c fr_LU=140c fr_MC=180c fy_NL=0462 ga_IE=083c gbz_AF=048c " +
        "gd=043c gl_ES=0456 gsw_FR=0484 gu_IN=0447 ha_Latn_NG=0468 he_IL=040d hi_IN=0439 hr_BA=101a " +
        "hr_HR=041a hu_HU=040e hy_AM=042b id_ID=0421 ig_NG=0470 ii_CN=0478 is_IS=040f it_CH=0810 " +
        "it_IT=0410 iu_Cans_CA=045d iu_Latn_CA=085d ja_JP=0411 ka_GE=0437 kh_KH=0453 kk_KZ=043f " +
        "kl_GL=046f kn_IN=044b ko_KR=0412 kok_IN=0457 ky_KG=0440 lb_LU=046e lo_LA=0454 lt_LT=0427 " +
        "lv_LV=0426 mi_NZ=0481 mk_MK=042f ml_IN=044c mn_Cyrl_MN=0450 mn_Mong_CN=0850 moh_CA=047c " +
        "mr_IN=044e ms_BN=083e ms_MY=043e mt_MT=043a nb_NO=0414 ne_NP=0461 nl_BE=0813 nl_NL=0413 " +
        "nn_NO=0814 ns_ZA=046c oc_FR=0482 or_IN=0448 pa_IN=0446 pl_PL=0415 ps_AF=0463 pt_BR=0416 " +
        "pt_PT=0816 qut_GT=0486 quz_BO=046b quz_EC=086b quz_PE=0c6b rm_CH=0417 ro_MO=0818 ro_RO=0418 " +
        "ru_MO=0819 ru_RU=0419 rw_RW=0487 sa_IN=044f sah_RU=0485 se_FI=0c3b se_NO=043b se_SE=083b " +
        "si_LK=045b sk_SK=041b sl_SI=0424 sma_NO=183b sma_SE=1c3b smj_NO=103b smj_SE=143b smn_FI=243b " +
        "sms_FI=203b sq_AL=041c sr_Cyrl_BA=1c1a sr_Cyrl_CS=0c1a sr_Latn_BA=181a sr_Latn_CS=081a st=0430 " +
        "sv_FI=081d sv_SE=041d sw_KE=0441 syr_SY=045a ta_IN=0449 te_IN=044a tg_Cyrl_TJ=0428 th_TH=041e " +
        "tk_TM=0442 tmz_Latn_DZ=085f tn_ZA=0432 tr_IN=0820 tr_TR=041f ts=0431 tt_RU=0444 ug_CN=0480 " +
        "uk_UA=0422 ur_PK=0420 uz_Cyrl_UZ=0843 uz_Latn_UZ=0443 vi_VN=042a wen_DE=042e wo_SN=0488 " +
        "xh_ZA=0434 yi=043d yo_NG=046a zh_CN=0804 zh_HK=0c04 zh_MO=1404 zh_SG=1004 zh_TW=0404 zu_ZA=0435 ";

    [StructLayout(LayoutKind.Sequential)]
    struct DtSystemTime
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDateFormatA")]
    static extern int DtGetDateFormat(uint locale, uint flags, ref DtSystemTime date, IntPtr format, byte[] buf, int len);

    [DllImport("kernel32.dll", EntryPoint = "GetTimeFormatA")]
    static extern int DtGetTimeFormat(uint locale, uint flags, ref DtSystemTime time, IntPtr format, byte[] buf, int len);

    const uint DtLocaleUserDefault = 0x0400, DtNoUserOverride = 0x80000000, DtShortDate = 1;

    /// <summary>
    /// The locale ID for an optional locale argument (cob_field_to_string, then the table).
    /// Null for an empty or unknown name: the function then fails.
    /// </summary>
    static bool DtLocaleId(CobField localeField, out uint id)
    {
        id = DtLocaleUserDefault;
        if (localeField == null) return true;
        string name = DtFieldToString(localeField, 255);
        if (string.IsNullOrEmpty(name)) return false;
        if (DtWinLocale(name) is not int lcid) return false;
        id = (uint)lcid;
        return true;
    }

    /// <summary>GetDateFormat / GetTimeFormat output as bytes (ANSI code page, as libcob's A calls); null on failure.</summary>
    static byte[] DtWinFormat(bool date, uint locale, DtSystemTime st)
    {
        if (OperatingSystem.IsWindows())
        {
            var buf = new byte[128];
            int n = date ? DtGetDateFormat(locale, DtShortDate, ref st, IntPtr.Zero, buf, buf.Length)
                         : DtGetTimeFormat(locale, DtNoUserOverride, ref st, IntPtr.Zero, buf, buf.Length);
            if (n == 0) return null;
            int len = Array.IndexOf(buf, (byte)0);
            return buf.AsSpan(0, len < 0 ? buf.Length : len).ToArray();
        }
        // elsewhere: .NET's culture data (short date / long time pattern)
        try
        {
            var ci = locale == DtLocaleUserDefault ? CultureInfo.CurrentCulture : new CultureInfo((int)locale);
            string s = date
                ? new DateTime(st.wYear, st.wMonth, st.wDay).ToString("d", ci)
                : new DateTime(2000, 1, 1, st.wHour, st.wMinute, st.wSecond).ToString("T", ci);
            return Encoding.Latin1.GetBytes(s);
        }
        catch (ArgumentException) { return null; }
    }

    static CobField DtLocaleResult(byte[] text, int offset, int length)
    {
        if (text == null)
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return DtSpaces(10);
        }
        var f = IntrAlnum(text);
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    /// <summary>The number in the first n characters, which must all be digits; -1 if not.</summary>
    static int DtLeadingDigits(CobField f, int n)
    {
        if (f.size < n) return -1;
        var d = f.Span;
        int v = 0;
        for (int i = 0; i < n; i++)
        {
            if (!DtIsDigit(d[i])) return -1;
            v = v * 10 + (d[i] - '0');
        }
        return v;
    }

    public static CobField cob_intr_locale_date(int offset, int length, CobField srcfield, CobField localeField)
    {
        Global.cob_exception_code = 0;
        int indate = IsNumeric(srcfield.Type) ? cob_get_int(srcfield) : DtLeadingDigits(srcfield, 8);
        byte[] text = null;
        if (indate >= 0 || IsNumeric(srcfield.Type))
        {
            int year = indate / 10000;
            indate %= 10000;
            int month = indate / 100, days = indate % 100;
            if (DtValidYear(year) && DtValidMonth(month) && DtValidDayOfMonth(year, month, days)
                && DtLocaleId(localeField, out uint id))
                text = DtWinFormat(true, id, new DtSystemTime { wYear = (ushort)year, wMonth = (ushort)month, wDay = (ushort)days });
        }
        return DtLocaleResult(text, offset, length);
    }

    public static CobField cob_intr_locale_date(int offset, int length, CobField srcfield, int noLocale)
        => cob_intr_locale_date(offset, length, srcfield, (CobField)null);

    static CobField DtLocaleTime(int offset, int length, int hours, int minutes, int seconds, CobField localeField)
    {
        byte[] text = null;
        if (DtLocaleId(localeField, out uint id))
            text = DtWinFormat(false, id, new DtSystemTime { wHour = (ushort)hours, wMinute = (ushort)minutes, wSecond = (ushort)seconds });
        return DtLocaleResult(text, offset, length);
    }

    public static CobField cob_intr_locale_time(int offset, int length, CobField srcfield, CobField localeField)
    {
        Global.cob_exception_code = 0;
        bool numeric = IsNumeric(srcfield.Type);
        int indate = numeric ? cob_get_int(srcfield) : DtLeadingDigits(srcfield, 6);
        if (numeric || indate >= 0)
        {
            int hours = indate / 10000;
            indate %= 10000;
            int minutes = indate / 100, seconds = indate % 100;
            if (hours >= 0 && hours <= 24 && minutes >= 0 && minutes <= 59 && seconds >= 0 && seconds <= 59)
                return DtLocaleTime(offset, length, hours, minutes, seconds, localeField);
        }
        return DtLocaleResult(null, offset, length);
    }

    public static CobField cob_intr_locale_time(int offset, int length, CobField srcfield, int noLocale)
        => cob_intr_locale_time(offset, length, srcfield, (CobField)null);

    public static CobField cob_intr_lcl_time_from_secs(int offset, int length, CobField srcfield, CobField localeField)
    {
        Global.cob_exception_code = 0;
        if (IsNumeric(srcfield.Type))
        {
            int secs = cob_get_int(srcfield);
            if (DtValidTime(secs))
                return DtLocaleTime(offset, length, secs / 3600, secs % 3600 / 60, secs % 60, localeField);
        }
        return DtLocaleResult(null, offset, length);
    }

    public static CobField cob_intr_lcl_time_from_secs(int offset, int length, CobField srcfield, int noLocale)
        => cob_intr_lcl_time_from_secs(offset, length, srcfield, (CobField)null);

    // ================================================================ ACCEPT FROM DATE / DAY / TIME

    /// <summary>Move an unsigned binary value of these digits to the target (a group gets it as text).</summary>
    static void DtAcceptNumber(CobField f, ulong val, int digits)
    {
        var src = IntrField(TYPE_NUMERIC_BINARY, digits, 0, 0, val > uint.MaxValue || digits > 9 ? 8 : 4);
        if (src.size == 8) src.data.U64 = val; else src.data.U32 = (uint)val;
        if (f.Type != TYPE_GROUP) cob_move(src, f);
        else cob_move(src, new CobField(f.size, f.data,
                 new CobFieldAttr(TYPE_ALPHANUMERIC, f.attr.digits, f.attr.scale, f.attr.flags, f.attr.pic)));
    }

    /// <summary>cob_move_intermediate: move text as if from an alphanumeric item.</summary>
    static void DtMoveText(CobField f, byte[] text)
        => cob_move(new CobField(text.Length, new CobPtr(text, 0), new CobFieldAttr(TYPE_ALPHANUMERIC, 0, 0, 0, null)), f);

    static void DtMoveText(CobField f, string text) => DtMoveText(f, Latin1Bytes(text));

    /// <summary>ACCEPT FROM DATE: YYMMDD.</summary>
    public static void cob_accept_date(CobField f)
    {
        var t = DtNow(Dt_DTR_DATE);
        DtAcceptNumber(f, (ulong)(t.day_of_month + t.month * 100 + t.year % 100 * 10000), 6);
    }

    /// <summary>ACCEPT FROM DATE YYYYMMDD.</summary>
    public static void cob_accept_date_yyyymmdd(CobField f)
    {
        var t = DtNow(Dt_DTR_DATE);
        DtAcceptNumber(f, (ulong)(t.day_of_month + t.month * 100 + t.year * 10000), 8);
    }

    /// <summary>ACCEPT FROM DAY: YYDDD.</summary>
    public static void cob_accept_day(CobField f)
    {
        var t = DtNow(Dt_DTR_DATE);
        DtAcceptNumber(f, (ulong)(t.day_of_year + t.year % 100 * 1000), 5);
    }

    /// <summary>ACCEPT FROM DAY YYYYDDD.</summary>
    public static void cob_accept_day_yyyyddd(CobField f)
    {
        var t = DtNow(Dt_DTR_DATE);
        DtAcceptNumber(f, (ulong)(t.day_of_year + t.year * 1000), 7);
    }

    /// <summary>ACCEPT FROM DAY-OF-WEEK: 1 (Monday) to 7 (Sunday).</summary>
    public static void cob_accept_day_of_week(CobField f)
        => DtMoveText(f, new[] { (byte)('0' + DtNow(Dt_DTR_DATE).day_of_week) });

    /// <summary>ACCEPT FROM TIME: HHMMSShh (hundredths only for a target longer than 6).</summary>
    public static void cob_accept_time(CobField f)
    {
        var t = f.size > 6 ? DtNow(Dt_DTR_FULL) : DtNow(Dt_DTR_TIME_NO_NANO);
        DtAcceptNumber(f, (ulong)(t.nanosecond / 10000000 + t.second * 100 + t.minute * 10000 + t.hour * 1000000), 8);
    }

    /// <summary>ACCEPT FROM MICROSECOND-TIME: HHMMSSuuuuuu.</summary>
    public static void cob_accept_microsecond_time(CobField f)
    {
        var t = DtNow(Dt_DTR_FULL);
        DtAcceptNumber(f, (ulong)(t.nanosecond / 1000) + (ulong)t.second * 1000000
                          + (ulong)t.minute * 100000000 + (ulong)t.hour * 10000000000, 12);
    }

    // ================================================================ command line and arguments

    static byte[] dtCommandLine;     // set by DISPLAY ... UPON COMMAND-LINE
    static int dtCurrentArg = 1;     // the next ACCEPT FROM ARGUMENT-VALUE

    static int DtArgc => Args.Length + 1;
    static string DtArgv(int i) => i == 0 ? (Global.cob_main_argv0 ?? "") : Args[i - 1];

    public static void cob_display_command_line(CobField f) => dtCommandLine = f.Span.ToArray();

    public static void cob_accept_command_line(CobField f)
    {
        if (dtCommandLine != null && dtCommandLine.Length > 0) { DtMoveText(f, dtCommandLine); return; }
        if (DtArgc <= 1) { DtMoveText(f, " "); return; }
        var b = new StringBuilder();
        for (int i = 1; i < DtArgc; ++i)
        {
            b.Append(DtArgv(i));
            if (i != DtArgc - 1) b.Append(' ');
            if (b.Length > f.size) break;
        }
        DtMoveText(f, b.ToString());
    }

    /// <summary>DISPLAY ... UPON ARGUMENT-NUMBER: which argument the next ACCEPT FROM ARGUMENT-VALUE reads.</summary>
    public static void cob_display_arg_number(CobField f)
    {
        var n = IntrField(TYPE_NUMERIC_BINARY, 9, 0, 0, 4);
        cob_move(f, n);
        int v = n.data.S32;
        if (v < 0 || v >= DtArgc) { SetException(EC_IMP_DISPLAY); return; }
        dtCurrentArg = v;
    }

    public static void cob_accept_arg_number(CobField f) => DtAcceptNumber(f, (ulong)(DtArgc - 1), 9);

    public static void cob_accept_arg_value(CobField f)
    {
        if (dtCurrentArg >= DtArgc) { SetException(EC_IMP_ACCEPT); return; }
        DtMoveText(f, DtArgv(dtCurrentArg));
        dtCurrentArg++;
    }

    // ================================================================ environment

    static string dtEnvName;         // set by DISPLAY ... UPON ENVIRONMENT-NAME

    /// <summary>cob_field_to_string: the data without trailing spaces/NULs, as a C string; null if longer than max.</summary>
    static string DtFieldToString(CobField f, int max)
    {
        if (f.size == 0 || f.data.IsNull) return null;
        var d = f.Span;
        int end = d.Length - 1;
        while (end > 0 && (d[end] == ' ' || d[end] == 0)) end--;
        if (d[end] == ' ' || d[end] == 0) return "";
        if (end > max) return null;
        return DtCStr(Latin1(d.Slice(0, end + 1)));
    }

    static string DtGetEnv(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('=')) return null;
        return Environment.GetEnvironmentVariable(name);
    }

    static bool DtEnvMangle()
    {
        string v = Environment.GetEnvironmentVariable("COB_ENV_MANGLE");
        return v != null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                             || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    static string DtMangle(string s)
    {
        if (!DtEnvMangle()) return s;
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++) if (!(c[i] < 128 && char.IsAsciiLetterOrDigit(c[i]))) c[i] = '_';
        return new string(c);
    }

    /// <summary>DISPLAY ... UPON ENVIRONMENT-NAME.</summary>
    public static void cob_display_environment(CobField f)
    {
        string s = DtFieldToString(f, (int)f.size);
        dtEnvName = s == null ? "" : DtMangle(s);
    }

    /// <summary>DISPLAY ... UPON ENVIRONMENT-VALUE: set the variable named before.</summary>
    public static void cob_display_env_value(CobField f)
    {
        if (string.IsNullOrEmpty(dtEnvName)) { SetException(EC_IMP_DISPLAY); return; }
        string value = DtFieldToString(f, 8191);
        if (value == null || dtEnvName.Contains('=')) { SetException(EC_IMP_DISPLAY); return; }
        Environment.SetEnvironmentVariable(dtEnvName, value);
    }

    /// <summary>SET ENVIRONMENT name TO value.</summary>
    public static void cob_set_environment(CobField f1, CobField f2)
    {
        cob_display_environment(f1);
        cob_display_env_value(f2);
    }

    /// <summary>ACCEPT x FROM ENVIRONMENT name.</summary>
    public static void cob_get_environment(CobField envname, CobField envval)
    {
        if (envname.size == 0 || envval.size == 0) { SetException(EC_IMP_ACCEPT); return; }
        string name = DtFieldToString(envname, 8191);
        if (string.IsNullOrEmpty(name)) { SetException(EC_IMP_ACCEPT); return; }
        string v = DtGetEnv(DtMangle(name));
        if (v != null) DtMoveText(f: envval, text: v);
        else
        {
            SetException(EC_IMP_ACCEPT);
            DtMoveText(envval, " ");
        }
    }

    /// <summary>ACCEPT x FROM ENVIRONMENT-VALUE (of the name DISPLAYed UPON ENVIRONMENT-NAME).</summary>
    public static void cob_accept_environment(CobField f)
    {
        string v = dtEnvName != null ? DtGetEnv(dtEnvName) : null;
        if (v != null) DtMoveText(f, v);
        else
        {
            SetException(EC_IMP_ACCEPT);
            DtMoveText(f, " ");
        }
    }
}
