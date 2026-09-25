namespace Molly.Market;

/// <summary>시세 모니터링 공용 정각 수집 일정. KST는 UTC와 정시 단위로 어긋나지 않아 UTC 정각이 곧 KST 정각입니다.</summary>
public static class MarketSchedule
{
    public static DateTimeOffset NextHourUtc(DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
    }

    /// <summary>
    /// 시작 시 즉시 수집이 필요한지 판단합니다. 저장된 시세가 없거나, 마지막 수집이 가장 최근 정각보다 이전이면
    /// (예: 13:03 시작, 마지막 수집 12:57 → 13:00 수집을 놓침) 즉시 수집합니다.
    /// </summary>
    public static bool NeedsCatchUp(DateTimeOffset? lastCollectedUtc, DateTimeOffset nowUtc) =>
        lastCollectedUtc is null || lastCollectedUtc.Value < NextHourUtc(nowUtc).AddHours(-1);
}
