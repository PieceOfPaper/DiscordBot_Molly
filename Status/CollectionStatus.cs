using Molly.OfficialNews;

namespace Molly.Status;

public enum CollectionState { Ok, Preparing, RecentFailure, Delayed, Unavailable, Disabled }

/// <summary>
/// 수집 작업의 실제 일정. NextDue는 마지막 시도 시작 시각으로 다음 시도가 시작될 예정 시각을, AllowedLag는 그 뒤로 허용할 지연을 뜻한다.
/// MaxRunDuration을 넘겨 진행 중이면 멈춘 것으로 보고, FirstRunAllowance는 봇 시작 후 첫 시도를 기다리는 시간이다.
/// </summary>
public sealed record JobSchedule(Func<DateTimeOffset, DateTimeOffset> NextDue, TimeSpan AllowedLag, TimeSpan MaxRunDuration, TimeSpan FirstRunAllowance)
{
    public DateTimeOffset Deadline(DateTimeOffset lastAttemptAt) => NextDue(lastAttemptAt) + AllowedLag;
}

/// <summary>설정으로 수집하지 않는 이유. MissingSetting이면 필수 설정 누락, 아니면 의도적인 비활성화다.</summary>
public sealed record JobDisabled(bool MissingSetting, string Reason);

public sealed record JobStatusInput(JobStatusRecord? Record, DateTimeOffset? RunningSince, bool HasUsableData, JobDisabled? Disabled = null);

public sealed record JobStatusView(CollectionState State, JobStatusRecord? Record, JobDisabled? Disabled = null, string? Note = null);

/// <summary>/몰리상태가 다루는 수집 작업 ID와 각 작업의 일정. 주기 값은 각 기능의 기존 설정을 그대로 쓴다.</summary>
public static class StatusJobs
{
    public static string News(NewsBoard board) => "news:" + board.Id;
    public const string EventList = "event_list";
    public static string Market(string monitorId) => "market:" + monitorId;
    public const string HaeyeonMonitorId = "haeyeon";

    /// <summary>공식 소식: 확인을 마치고 Interval 뒤 다시 확인한다. 실패해도 다음 주기에 다시 확인하므로 한 주기 지연까지 허용한다.</summary>
    public static readonly JobSchedule NewsSchedule = new(
        last => last + OfficialNewsCollector.Interval, OfficialNewsCollector.Interval, OfficialNewsCollector.Interval, OfficialNewsCollector.Interval);

    /// <summary>이벤트 마감용 목록: 마감 알림 예약 갱신(UpdateInterval)마다 다시 읽는다. 시작할 때 한 번 바로 수집한다.</summary>
    public static readonly JobSchedule EventListSchedule = new(
        last => last + MobiEventExpireAlert.UpdateInterval, MobiEventExpireAlert.UpdateInterval, MobiEventExpireAlert.UpdateInterval, MobiEventExpireAlert.UpdateInterval);

    /// <summary>시세: 매 정각 수집하고, 실패하면 RetryDelay 간격으로 다시 시도한다. 정각 뒤 첫 재시도 시각까지 허용한다.</summary>
    public static readonly JobSchedule MarketHourlySchedule = new(
        Molly.Market.MarketSchedule.NextHourUtc, Molly.Market.MarketSchedule.RetryDelay, Molly.Market.MarketSchedule.RetryDelay, Molly.Market.MarketSchedule.RetryDelay);
}

/// <summary>
/// 수집 상태 판정. 겹칠 때의 우선순위: 사용 안 함 → 진행 시간 초과(확인 지연) → 준비 중 → 수집 불가 → 확인 지연 → 최근 확인 실패 → 정상.
/// 새 글·시세 변화가 없어도 수집이 성공했으면 정상이다. 마지막 시도가 성공하면 이전 실패는 문제로 남지 않는다.
/// </summary>
public static class CollectionStatusJudge
{
    public const string RunTooLongNote = "진행 시간 초과";

    public static JobStatusView Judge(JobStatusInput input, JobSchedule schedule, DateTimeOffset processStartedAt, DateTimeOffset now)
    {
        if (input.Disabled is { } disabled) return new(CollectionState.Disabled, input.Record, disabled);
        var record = input.Record;
        if (input.RunningSince is { } since && now - since > schedule.MaxRunDuration)
            return new(CollectionState.Delayed, record, Note: RunTooLongNote);

        var attemptedThisRun = record is not null && record.LastAttemptAt >= processStartedAt;
        var freshSuccess = record is { LastSucceeded: true } && now <= schedule.Deadline(record.LastAttemptAt);
        if (!attemptedThisRun && !freshSuccess && (input.RunningSince is not null || now <= processStartedAt + schedule.FirstRunAllowance))
            return new(CollectionState.Preparing, record);
        if (record is null) return new(CollectionState.Delayed, null, Note: "확인 기록 없음");
        if (!record.LastSucceeded && !input.HasUsableData) return new(CollectionState.Unavailable, record);
        if (input.RunningSince is null && now > schedule.Deadline(record.LastAttemptAt)) return new(CollectionState.Delayed, record);
        if (!record.LastSucceeded) return new(CollectionState.RecentFailure, record);
        return new(CollectionState.Ok, record);
    }

    /// <summary>전체 요약에 문제로 올릴 상태인지. 준비 중과 의도적인 비활성화는 문제가 아니다.</summary>
    public static bool IsProblem(JobStatusView view) => view.State switch
    {
        CollectionState.RecentFailure or CollectionState.Delayed or CollectionState.Unavailable => true,
        CollectionState.Disabled => view.Disabled?.MissingSetting == true,
        _ => false,
    };
}
