
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
    
    public static DateTime CalculateRewardNextDateTime(int requireCount, int hasCount, bool isReceivedWeekendReward, int weekReward, int weekendReward)
    {
        var dateTimeNow = now;
        
        // 이미 충분하면 지금
        int remaining = requireCount - hasCount;
        if (remaining <= 0)
            return dateTimeNow;

        // 루프를 돌며 이벤트(주말15, 월요일315)를 시간 순으로 적용
        var t = dateTimeNow;
        bool claimedWeekendThisWeek = isReceivedWeekendReward;

        // 안전장치(최대 3년치 반복)
        for (int guard = 0; guard < 3 * 52 * 3; guard++)
        {
            var nextMon6 = t.ToEarliestMobiResetTimeWeekStart();

            // 다음 "주말 보상" 가능한 시각 계산
            DateTime nextWeekendEvent;
            if (t.IsMobiResetTimeWeekend())
            {
                // 주말 구간
                if (!claimedWeekendThisWeek)
                {
                    // 아직 이번 주 주말보상 안 받음 → 지금 바로 받을 수 있음
                    nextWeekendEvent = t;
                }
                else
                {
                    // 이미 이번 주는 받았음 → 다음 주 토 06:00
                    var thisWeekendStart = t.ToEarliestMobiResetTimeWeekendStart();
                    nextWeekendEvent = thisWeekendStart.AddDays(7); // 다음 토 06:00
                }
            }
            else
            {
                // 주말 구간 밖 → 다음 토 06:00
                nextWeekendEvent = t.ToNextMobiResetTimeWeekendStart();
                // (주중에 hasClaimedWeekendThisWeek=true 라는 상태는 의미가 없으므로 그대로 두되,
                // 월요일 06:00을 지나면 자동으로 새 주로 간주하고 false로 리셋됨)
            }

            // 다음 이벤트 결정(가장 이른 시각)
            DateTime nextEvent;
            int gain;
            bool isWeekendEvent;

            if (nextWeekendEvent <= nextMon6)
            {
                nextEvent = nextWeekendEvent;
                gain = weekendReward;
                isWeekendEvent = true;
            }
            else
            {
                nextEvent = nextMon6;
                gain = weekReward;
                isWeekendEvent = false;
            }

            remaining -= gain;

            // 목표 달성 시 반환 로직
            if (remaining <= 0)
            {
                if (isWeekendEvent)
                {
                    // 마지막 보상이 주말 보상 → GetEarliestWeekendKst 규칙으로 반환
                    // nextEvent(KST)를 UTC로 변환해서 그 시점 기준으로 계산
                    var eventUtc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(nextEvent, DateTimeKind.Unspecified), MobiTime.timezone);

                    return eventUtc.ToEarliestMobiResetTimeWeekend();
                }
                else
                {
                    // 월요일 06:00 보상으로 달성 → 그 시각 반환
                    return nextEvent;
                }
            }

            // 다음 반복을 위한 상태 업데이트
            if (isWeekendEvent)
            {
                // 이번 주 주말 보상은 받았다고 표시
                claimedWeekendThisWeek = true;
            }
            else
            {
                // 월요일 06:00을 지났으니 "새 주" 시작 → 주말 보상 수령 여부 리셋
                claimedWeekendThisWeek = false;
            }

            // 같은 타임스탬프 재사용 방지 (다음 이벤트 탐색을 위해 +1초)
            t = nextEvent.AddSeconds(1);
        }

        throw new InvalidOperationException("예상치 못한 반복 과다. 입력을 확인하세요.");
    }
}
