using System.Globalization;
using System.Text;

namespace Molly.Status;

/// <summary>수집 상태 한 줄. View가 null이면 상태를 읽지 못한 것이다. UsedByThisServer는 이 서버에 이 수집에 기대는 알림이 등록돼 있는지다.</summary>
public sealed record CollectionLine(string Section, string Name, JobStatusView? View, bool UsedByThisServer);

public sealed record StatusReportInput(
    DateTimeOffset Now,
    DateTimeOffset ProcessStartedAt,
    IReadOnlyList<CollectionLine> Collections,
    IReadOnlyList<AlertEntry> Alerts,
    int RecordFailureCount = 0,
    DateTimeOffset? LastRecordFailureAt = null);

public enum StatusSummary { Ok, NeedsAttention, Preparing }

public sealed record StatusReport(StatusSummary Summary, string Title, IReadOnlyList<string> Reasons, IReadOnlyList<string> Pages);

/// <summary>
/// /몰리상태 응답 문구. 수집 상태(봇 공통)와 이 서버의 알림 설정·전송 상태를 나눠 보여준다.
/// 정상 항목은 한 줄로, 문제가 있는 항목은 마지막 시도·성공과 원인을 덧붙인다. 오류는 분류 문구만 쓰고 원문은 넣지 않는다.
/// </summary>
public static class MollyStatusReport
{
    // Embed 설명은 4096자 제한이라 여유를 둔다.
    public const int MaxPageLength = 3800;
    private const int MaxReasonsInSummary = 5;

    public static StatusReport Build(StatusReportInput input)
    {
        var reasons = new List<string>();
        var actions = new List<string>();
        var body = new List<string>();

        foreach (var section in input.Collections.Select(x => x.Section).Distinct())
        {
            body.Add("");
            body.Add($"**[{section}]**");
            body.AddRange(CollectionLines(input, input.Collections.Where(x => x.Section == section).ToList(), reasons, actions));
        }

        body.Add("");
        body.Add("**[이 서버의 알림 설정]**");
        body.AddRange(SettingLines(input.Alerts, reasons, actions));

        var registered = input.Alerts.Where(x => x.Registered && !x.QueryFailed).ToList();
        if (registered.Count > 0)
        {
            body.Add("");
            body.Add("**[이 서버의 전송 상태]**");
            body.AddRange(DeliveryLines(input.Now, registered, reasons, actions));
        }

        if (input.RecordFailureCount > 0)
        {
            reasons.Add("상태 기록 실패");
            actions.Add($"이번 실행에서 상태 기록에 {input.RecordFailureCount}번 실패했어요(마지막 {Time(input.LastRecordFailureAt, input.Now)}). 일부 상태가 실제보다 오래됐을 수 있어요.");
        }

        if (actions.Count > 0)
        {
            body.Add("");
            body.Add("**[확인할 내용]**");
            body.AddRange(actions.Select(x => "• " + x));
        }

        var preparing = input.Collections.Any(x => x.View?.State == CollectionState.Preparing);
        var summary = reasons.Count > 0 ? StatusSummary.NeedsAttention : preparing ? StatusSummary.Preparing : StatusSummary.Ok;
        var title = summary switch
        {
            StatusSummary.NeedsAttention => "🤖 몰리 상태 — 일부 확인 필요",
            StatusSummary.Preparing => "🤖 몰리 상태 — 준비 중",
            _ => "🤖 몰리 상태 — 정상",
        };

        var header = new List<string>
        {
            $"확인 시각: {Full(input.Now)} KST",
            $"이번 실행 시작: {Full(input.ProcessStartedAt)} KST",
        };
        if (reasons.Count > 0)
        {
            var distinct = reasons.Distinct().ToList();
            var shown = string.Join(", ", distinct.Take(MaxReasonsInSummary));
            header.Add($"원인: {shown}{(distinct.Count > MaxReasonsInSummary ? $" 외 {distinct.Count - MaxReasonsInSummary}건" : "")}");
        }
        else if (summary == StatusSummary.Preparing)
            header.Add("봇을 시작한 뒤 첫 확인을 기다리는 항목이 있어요.");

        return new StatusReport(summary, title, reasons.Distinct().ToList(), Paginate(header.Concat(body).ToList()));
    }

    private static IEnumerable<string> CollectionLines(StatusReportInput input, IReadOnlyList<CollectionLine> lines, List<string> reasons, List<string> actions)
    {
        var now = input.Now;
        // 정상·같은 사유의 사용 안 함처럼 한 줄로 끝나는 항목은 같은 문구끼리 이름을 묶는다(첫 항목 위치에 표시).
        var output = new List<(string? Text, string? Icon, string? Tail, List<string>? Names)>();
        void AddSimple(string name, string icon, string tail)
        {
            var existing = output.FindIndex(x => x.Icon == icon && x.Tail == tail);
            if (existing >= 0) output[existing].Names!.Add(name);
            else output.Add((null, icon, tail, [name]));
        }
        void Add(string text) => output.Add((text, null, null, null));

        foreach (var line in lines)
        {
            if (line.View is not { } view)
            {
                Add($"⚠️ {line.Name} · 상태를 불러오지 못함");
                reasons.Add($"{line.Name} 상태 조회 실패");
                continue;
            }
            var record = view.Record;
            switch (view.State)
            {
                case CollectionState.Ok:
                    AddSimple(line.Name, "🟢", $"마지막 성공 {Time(record?.LastSuccessAt, now)}");
                    break;
                case CollectionState.Disabled:
                    var disabled = view.Disabled!;
                    AddSimple(line.Name, "⚪", $"사용 안 함 — {disabled.Reason}{(disabled.MissingSetting ? " (필수 설정 누락)" : "")}");
                    if (disabled.MissingSetting && line.UsedByThisServer)
                    {
                        reasons.Add($"{line.Name} 필수 설정 누락");
                        actions.Add($"{line.Name}: {disabled.Reason}. 설정하기 전까지 수집하지 않아 관련 알림이 오지 않아요.");
                    }
                    break;
                case CollectionState.Preparing:
                    Add($"⏳ {line.Name} · 준비 중(이번 실행의 첫 확인 대기·진행 중)");
                    if (record?.LastSuccessAt is { } previous) Add($"└ 마지막 성공 {Time(previous, now)}");
                    break;
                case CollectionState.RecentFailure:
                    Add($"🟡 {line.Name} · 최근 확인 실패");
                    Add(Detail(record!, now));
                    reasons.Add($"{line.Name} 최근 확인 실패");
                    actions.Add($"{line.Name}: {StatusErrorKinds.Describe(record!.LastErrorKind)}. 이전 정상 데이터를 유지하고 다음 확인에서 다시 시도해요.");
                    break;
                case CollectionState.Unavailable:
                    Add($"🔴 {line.Name} · 수집 불가");
                    Add(Detail(record!, now));
                    reasons.Add($"{line.Name} 수집 불가");
                    actions.Add($"{line.Name}: {StatusErrorKinds.Describe(record!.LastErrorKind)}. 사용할 정상 데이터가 없어 관련 알림을 보낼 수 없어요. 다음 확인에서 다시 시도해요.");
                    break;
                case CollectionState.Delayed:
                    Add($"🟠 {line.Name} · 확인 지연");
                    Add(record is null ? "└ 이번 실행에서 확인한 기록 없음" : Detail(record, now));
                    reasons.Add($"{line.Name} 수집 지연");
                    actions.Add(view.Note == CollectionStatusJudge.RunTooLongNote
                        ? $"{line.Name}: 확인이 너무 오래 끝나지 않고 있어요. 계속되면 봇 로그를 확인해주세요."
                        : $"{line.Name}: 예정된 확인 시각이 지났는데 새 확인 기록이 없어요. 계속되면 봇 로그를 확인해주세요.");
                    break;
            }
        }
        return output.Select(x => x.Text ?? $"{x.Icon} {string.Join("·", x.Names!)} · {x.Tail}");
    }

    private static string Detail(JobStatusRecord record, DateTimeOffset now)
    {
        var text = $"└ 마지막 시도 {Time(record.LastAttemptAt, now)} / 마지막 성공 {Time(record.LastSuccessAt, now)}";
        if (!record.LastSucceeded && record.ConsecutiveFailures > 1) text += $" · 연속 실패 {record.ConsecutiveFailures}회";
        return text;
    }

    private static IEnumerable<string> SettingLines(IReadOnlyList<AlertEntry> alerts, List<string> reasons, List<string> actions)
    {
        var groups = new List<(string Tail, List<AlertEntry> Entries)>();
        foreach (var alert in alerts)
        {
            var tail = SettingTail(alert);
            var existing = groups.FindIndex(x => x.Tail == tail);
            if (existing >= 0) groups[existing].Entries.Add(alert);
            else groups.Add((tail, [alert]));
        }
        foreach (var (tail, entries) in groups)
        {
            var names = string.Join("·", entries.Select(x => x.Name));
            var first = entries[0];
            if (first.QueryFailed)
            {
                reasons.Add($"{names} 설정 조회 실패");
            }
            else if (first.Registered && first.Check.IsProblem)
            {
                var channel = $"<#{first.ChannelId}>";
                switch (first.Check.Kind)
                {
                    case ChannelCheckKind.ChannelUnavailable:
                        reasons.Add($"{names} 채널 접근 불가");
                        actions.Add($"{names}: 등록 채널을 찾을 수 없어요(삭제되었거나 몰리가 볼 수 없음). 보낼 채널에서 다시 등록해주세요.");
                        break;
                    case ChannelCheckKind.NotMessageChannel:
                        reasons.Add($"{names} 채널 종류 확인 필요");
                        actions.Add($"{names}: {channel}은 메시지를 보낼 수 없는 채널이에요. 일반 텍스트 채널에서 다시 등록해주세요.");
                        break;
                    case ChannelCheckKind.MissingPermissions:
                        reasons.Add($"{names} 채널 권한 부족");
                        actions.Add($"{names}: {channel}에서 몰리에게 {string.Join("·", first.Check.MissingPermissions)} 권한이 필요해요.");
                        break;
                    default:
                        reasons.Add($"{names} 채널 확인 실패");
                        break;
                }
            }
            yield return $"{names} → {tail}";
        }
    }

    private static string SettingTail(AlertEntry alert)
    {
        if (alert.QueryFailed) return "설정을 불러오지 못함";
        if (!alert.Registered) return "미등록";
        var parts = new List<string>();
        parts.Add(alert.Check.Kind == ChannelCheckKind.ChannelUnavailable ? "등록 채널을 확인할 수 없음" : $"<#{alert.ChannelId}>");
        if (alert.Detail is { } detail) parts.Add(detail);
        switch (alert.Check.Kind)
        {
            case ChannelCheckKind.Ok: parts.Add("설정 정상"); break;
            case ChannelCheckKind.NotMessageChannel: parts.Add("메시지를 보낼 수 없는 채널"); break;
            case ChannelCheckKind.MissingPermissions: parts.Add(string.Join("·", alert.Check.MissingPermissions) + " 권한 필요"); break;
            case ChannelCheckKind.CheckFailed: parts.Add("채널 확인 실패"); break;
        }
        return string.Join(" · ", parts);
    }

    private static IEnumerable<string> DeliveryLines(DateTimeOffset now, IReadOnlyList<AlertEntry> alerts, List<string> reasons, List<string> actions)
    {
        var groups = new List<(string Tail, List<string> Names, AlertEntry First)>();
        foreach (var alert in alerts)
        {
            var tail = alert.Delivery switch
            {
                null => "전송 기록을 불러오지 못함",
                { HasUnresolvedFailure: true } d => $"🔴 전송 실패 {Time(d.UnresolvedFailureAt, now)} ({StatusErrorKinds.Describe(d.ErrorKind)}) · 마지막 성공 {Time(d.LastSuccessAt, now)}",
                { LastSuccessAt: { } success } => $"마지막 전송 {Time(success, now)}",
                _ => "전송 기록 없음",
            };
            var existing = groups.FindIndex(x => x.Tail == tail);
            if (existing >= 0) groups[existing].Names.Add(alert.Name);
            else groups.Add((tail, [alert.Name], alert));
        }
        foreach (var (tail, names, first) in groups)
        {
            var joined = string.Join("·", names);
            if (first.Delivery is null) reasons.Add($"{joined} 전송 기록 조회 실패");
            else if (first.Delivery.HasUnresolvedFailure)
            {
                reasons.Add($"{joined} 전송 실패");
                actions.Add($"{joined}: {Time(first.Delivery.UnresolvedFailureAt, now)}에 보내지 못했어요({StatusErrorKinds.Describe(first.Delivery.ErrorKind)}). 같은 채널로 다음 전송이 성공하면 해제돼요.");
            }
            yield return $"{joined} · {tail}";
        }
    }

    private static List<string> Paginate(IReadOnlyList<string> lines)
    {
        var pages = new List<string>();
        var builder = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Length > MaxPageLength ? raw[..(MaxPageLength - 1)] + "…" : raw;
            if (builder.Length > 0 && builder.Length + line.Length + 1 > MaxPageLength)
            {
                pages.Add(builder.ToString().TrimEnd());
                builder.Clear();
            }
            builder.AppendLine(line);
        }
        if (builder.Length > 0) pages.Add(builder.ToString().TrimEnd());
        return pages;
    }

    public static string Full(DateTimeOffset utc) => Kst(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>확인 시각과 같은 날(KST)이면 시:분만, 다른 날이면 날짜도 붙인다. 기록이 없으면 "기록 없음".</summary>
    public static string Time(DateTimeOffset? utc, DateTimeOffset now)
    {
        if (utc is not { } value) return "기록 없음";
        var kst = Kst(value);
        var today = Kst(now);
        if (kst.Date == today.Date) return kst.ToString("HH:mm", CultureInfo.InvariantCulture);
        return kst.Year == today.Year
            ? kst.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : kst.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static DateTime Kst(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, MobiTime.timezone).DateTime;
}
