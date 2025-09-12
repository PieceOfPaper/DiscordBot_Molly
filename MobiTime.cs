
public static class MobiTime
{
    public const int RESET_HOUR = 6;
    public readonly static TimeZoneInfo timezone;

    public static DateTime now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone);

    static MobiTime()
    {
        try { timezone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"); }
        catch { timezone = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"); }
    }
    
    /// <summary>
    /// 리셋시간 기준 다음 주 시작일을 구하는 함수
    /// </summary>
    /// <param name="dateTime"></param>
    /// <param name="week"></param>
    /// <returns></returns>
    public static DateTime CalculateNextMobiResetTimeWeekStartDateTime(DateTime dateTime, int week)
    {
        DateTime GetUpcomingMonday6(DateTime nowLocal)
        {
            int daysUntilMon = ((int)DayOfWeek.Monday - (int)nowLocal.DayOfWeek + 7) % 7;
            var nextMondayDate = nowLocal.Date.AddDays(daysUntilMon);

            var candidate = new DateTime(nextMondayDate.Year, nextMondayDate.Month, nextMondayDate.Day, RESET_HOUR, 0, 0);
            if (daysUntilMon == 0 && nowLocal.TimeOfDay >= TimeSpan.FromHours(RESET_HOUR))
                candidate = candidate.AddDays(7);

            return candidate;
        }

        return GetUpcomingMonday6(dateTime).AddDays(7 * (week - 1)); // week-1 주 더하기
    }
    
    /// <summary>
    /// 리셋시간 기준 주말인지 체크
    /// </summary>
    /// <param name="dateTime"></param>
    /// <returns></returns>
    public static bool IsMobiResetTimeWeekend(this DateTime dateTime)
    {
        int daysSinceSaturday = ((int)dateTime.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        var start = now.Date.AddDays(-daysSinceSaturday).AddHours(RESET_HOUR);
        return (dateTime >= start && dateTime < start.AddDays(2));
    }
    
    public static DateTime ToEarliestMobiResetTimeWeekend(this DateTime dateTime)
    {
        // "이번 주" 토요일 날짜 (지난 토요일 포함)
        int daysSinceSaturday = ((int)dateTime.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        var satThisWeek = dateTime.Date.AddDays(-daysSinceSaturday);

        var weekendStart = satThisWeek.AddHours(6); // 토요일 06:00 (KST)
        var weekendEnd   = weekendStart.AddDays(2); // 월요일 06:00 (KST)

        if (dateTime < weekendStart)
            return weekendStart;              // 토 06:00 이전 → 이번 주 토 06:00
        if (dateTime < weekendEnd)
            return dateTime;                    // 주말 구간 안 → 현재 시각
        return weekendStart.AddDays(7);       // 월 06:00 이후 → 다음 주 토 06:00
    }
    
    public static DateTime ToEarliestMobiResetTimeWeekendStart(this DateTime dateTime)
    {
        int daysSinceSat = ((int)dateTime.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        return dateTime.Date.AddDays(-daysSinceSat).AddHours(RESET_HOUR);
    }

    public static DateTime ToEarliestMobiResetTimeWeekStart(this DateTime dateTime)
    {
        int daysUntilMon = ((int)DayOfWeek.Monday - (int)dateTime.DayOfWeek + 7) % 7;
        var mon = dateTime.Date.AddDays(daysUntilMon).AddHours(RESET_HOUR);
        return (dateTime <= mon) ? mon : mon.AddDays(7);
    }

    public static DateTime ToNextMobiResetTimeWeekendStart(this DateTime dateTime)
    {
        int daysUntilSat = ((int)DayOfWeek.Saturday - (int)dateTime.DayOfWeek + 7) % 7;
        var sat = dateTime.Date.AddDays(daysUntilSat).AddHours(RESET_HOUR);
        return (dateTime <= sat) ? sat : sat.AddDays(7);
    }
}
