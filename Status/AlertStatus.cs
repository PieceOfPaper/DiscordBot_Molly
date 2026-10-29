using Discord;

namespace Molly.Status;

/// <summary>서버별 전송 상태를 구분하는 알림 종류. 공식 소식은 발송 작업 표에서 읽으므로 여기 값으로 저장하지 않는다.</summary>
public static class StatusAlerts
{
    public const string EventExpire = "event_expire";
    public static string Market(string monitorId) => "market:" + monitorId;
}

public enum ChannelCheckKind { NotRegistered, Ok, ChannelUnavailable, NotMessageChannel, MissingPermissions, CheckFailed }

/// <summary>등록 채널·봇 권한 확인 결과. Ok여도 실제 전송 성공을 뜻하지 않는다(설정 정상).</summary>
public sealed record ChannelCheck(ChannelCheckKind Kind, IReadOnlyList<string> MissingPermissions)
{
    public static readonly ChannelCheck NotRegistered = new(ChannelCheckKind.NotRegistered, []);
    public static readonly ChannelCheck Ok = new(ChannelCheckKind.Ok, []);
    public static readonly ChannelCheck ChannelUnavailable = new(ChannelCheckKind.ChannelUnavailable, []);
    public static readonly ChannelCheck NotMessageChannel = new(ChannelCheckKind.NotMessageChannel, []);
    public static readonly ChannelCheck CheckFailed = new(ChannelCheckKind.CheckFailed, []);

    public bool IsProblem => Kind is ChannelCheckKind.ChannelUnavailable or ChannelCheckKind.NotMessageChannel or ChannelCheckKind.MissingPermissions or ChannelCheckKind.CheckFailed;
}

/// <summary>알림 종류별로 실제 전송 방식에 필요한 봇 권한. 공식 소식은 썸네일이 없으면 기본 이미지를 파일로 첨부한다.</summary>
public static class AlertChannelRequirements
{
    /// <summary>부족한 권한의 표시 이름. 스레드에서는 메시지 보내기 대신 스레드에서 메시지 보내기 권한을 본다.</summary>
    public static IReadOnlyList<string> Missing(ChannelPermissions permissions, bool needsAttachFiles, bool isThread)
    {
        var missing = new List<string>();
        if (!permissions.ViewChannel) missing.Add("채널 보기");
        if (isThread ? !permissions.SendMessagesInThreads : !permissions.SendMessages) missing.Add(isThread ? "스레드에서 메시지 보내기" : "메시지 보내기");
        if (!permissions.EmbedLinks) missing.Add("링크 첨부");
        if (needsAttachFiles && !permissions.AttachFiles) missing.Add("파일 첨부");
        return missing;
    }

    public static ChannelCheck Check(ChannelPermissions permissions, bool needsAttachFiles, bool isThread)
    {
        var missing = Missing(permissions, needsAttachFiles, isThread);
        return missing.Count == 0 ? ChannelCheck.Ok : new ChannelCheck(ChannelCheckKind.MissingPermissions, missing);
    }
}

/// <summary>
/// 현재 등록 채널 기준의 전송 상태. UnresolvedFailureAt은 마지막 성공보다 늦은 실패만 남는다(이후 정상 전송으로 복구).
/// 다른 채널로의 과거 전송과 현재 설정 등록 전의 실패는 현재 설정의 결과로 보지 않는다. 채널·권한 확인 결과로는 해제하지 않는다.
/// </summary>
public sealed record DeliveryView(DateTimeOffset? LastSuccessAt, DateTimeOffset? UnresolvedFailureAt, string? ErrorKind)
{
    public static readonly DeliveryView None = new(null, null, null);

    public bool HasUnresolvedFailure => UnresolvedFailureAt is not null;

    /// <param name="settingSince">현재 설정을 등록·변경한 시각. 이보다 앞선 실패는 이전 설정의 결과로 보고 표시하지 않는다.</param>
    public static DeliveryView For(DateTimeOffset? lastSuccessAt, DateTimeOffset? lastFailureAt, string? errorKind, DateTimeOffset? settingSince = null) =>
        lastFailureAt is { } failure && (lastSuccessAt is not { } success || failure > success) && (settingSince is not { } since || failure >= since)
            ? new DeliveryView(lastSuccessAt, failure, errorKind ?? StatusErrorKinds.Unknown)
            : new DeliveryView(lastSuccessAt, null, null);

    /// <summary>저장된 서버·알림 종류의 전송 결과에서 현재 등록 채널의 것만 본다.</summary>
    public static DeliveryView For(AlertDeliveryRecord? record, ulong currentChannelId, DateTimeOffset? settingSince = null)
    {
        if (record is null) return None;
        var success = record.LastSuccessChannelId == currentChannelId ? record.LastSuccessAt : null;
        var failure = record.LastFailureChannelId == currentChannelId ? record.LastFailureAt : null;
        return For(success, failure, record.LastErrorKind, settingSince);
    }
}

/// <summary>
/// /몰리상태에 보여줄 이 서버의 알림 하나. Detail은 마감 시간·등락률처럼 생략하면 안 되는 개별 설정이다.
/// QueryFailed면 설정·기록을 읽지 못한 것이고 나머지 값은 의미가 없다.
/// </summary>
public sealed record AlertEntry(string Name, ulong? ChannelId, string? Detail, ChannelCheck Check, DeliveryView? Delivery, bool QueryFailed = false)
{
    public bool Registered => ChannelId is not null;

    public static AlertEntry Failed(string name) => new(name, null, null, ChannelCheck.NotRegistered, null, QueryFailed: true);
}
