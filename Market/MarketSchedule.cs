namespace Molly.Market;

/// <summary>시세 모니터링 공용 정각 수집 일정. KST는 UTC와 정시 단위로 어긋나지 않아 UTC 정각이 곧 KST 정각입니다.</summary>
public static class MarketSchedule
{
    public static DateTimeOffset NextHourUtc(DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
    }

    // 수집 시각이 속한 정각 회차. 10분 간격 재시도(예: 10:10)나 늦은 시작 직후 수집(13:03)도 해당 정각(10:00·13:00) 회차로 봅니다.
    public static DateTimeOffset HourOf(DateTimeOffset utc)
    {
        utc = utc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// 시작 시 즉시 수집이 필요한지 판단합니다. 저장된 시세가 없거나, 마지막 수집이 가장 최근 정각보다 이전이면
    /// (예: 13:03 시작, 마지막 수집 12:57 → 13:00 수집을 놓침) 즉시 수집합니다.
    /// </summary>
    public static bool NeedsCatchUp(DateTimeOffset? lastCollectedUtc, DateTimeOffset nowUtc) =>
        lastCollectedUtc is null || lastCollectedUtc.Value < NextHourUtc(nowUtc).AddHours(-1);

    // 회차 수집이 실패하면 이 간격으로 최대 MaxRetries번 다시 시도합니다. 다음 정각에 닿으면 정각 수집에 맡깁니다.
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);
    public const int MaxRetries = 3;

    /// <summary>
    /// 한 회차를 수집하고, 실패하면 RetryDelay마다 최대 MaxRetries번 다시 시도합니다.
    /// 다음 재시도 시각이 다음 정각 이후면 더 시도하지 않습니다. 마지막 시도의 성공 여부를 돌려줍니다.
    /// </summary>
    public static async Task<bool> CollectWithRetryAsync(
        Func<CancellationToken, Task<bool>> collect,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<string> log,
        CancellationToken ct)
    {
        var nextHour = NextHourUtc(utcNow());
        if (await collect(ct).ConfigureAwait(false)) return true;
        for (var retry = 1; retry <= MaxRetries; retry++)
        {
            if (utcNow() + RetryDelay >= nextHour)
            {
                log("다음 정각이 가까워 재시도하지 않고 정각 수집을 기다립니다.");
                return false;
            }
            log($"수집 실패로 {RetryDelay.TotalMinutes:0}분 뒤 다시 시도합니다({retry}/{MaxRetries}).");
            await delay(RetryDelay, ct).ConfigureAwait(false);
            if (await collect(ct).ConfigureAwait(false)) return true;
        }
        log($"재시도 {MaxRetries}번이 모두 실패해 다음 정각 수집을 기다립니다.");
        return false;
    }
}
