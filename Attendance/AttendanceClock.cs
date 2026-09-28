namespace Molly.Attendance;

/// <summary>
/// 출석일 계산. 매일 오전 6시(KST)에 새 출석일이 시작되고, 논리 출석일은 (KST 현재 시각 - 6시간)의 날짜다.
/// 초기화 시각은 운영 중에 바뀌면 같은 날 두 번 출석할 수 있어 시트가 아닌 코드에 고정한다.
/// </summary>
public static class AttendanceClock
{
    public const int ResetHourKst = 6;

    // 한국은 일광 절약 시간을 쓰지 않아 +9시간 고정이다.
    private static readonly TimeSpan s_KstOffset = TimeSpan.FromHours(9);

    public static DateOnly LogicalDate(DateTimeOffset now)
        => DateOnly.FromDateTime(now.ToOffset(s_KstOffset).AddHours(-ResetHourKst).DateTime);

    /// <summary>다음 출석일이 시작되는 시각(오전 6시 KST).</summary>
    public static DateTimeOffset NextResetUtc(DateTimeOffset now)
    {
        var next = LogicalDate(now).AddDays(1);
        return new DateTimeOffset(next.Year, next.Month, next.Day, ResetHourKst, 0, 0, s_KstOffset).ToUniversalTime();
    }

    public static string DateKey(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
