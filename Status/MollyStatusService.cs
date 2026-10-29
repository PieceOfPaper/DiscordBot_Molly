using Molly.HaeyeonMarket;
using Molly.KeywordMarket;
using Molly.MobiLife;
using Molly.OfficialNews;

namespace Molly.Status;

/// <summary>
/// /몰리상태 응답에 필요한 상태를 모은다. 공식 홈페이지·모비라이프를 새로 호출하지 않고, 각 작업이 남긴 상태(DB·메모리)와 서버 설정만 읽는다.
/// 한 항목을 읽지 못해도 나머지 항목은 계속 모은다. 채널·권한 확인은 Discord 캐시를 쓰는 호출자가 넘긴다.
/// </summary>
public sealed class MollyStatusService(
    MollyStatusTracker tracker,
    OfficialNewsService news,
    MobiEventService events,
    EventExpireAlertSettingStore eventSettings,
    MobiLifeApiClient mobiLife,
    HaeyeonMarketMonitor haeyeon,
    KeywordMarketMonitor box,
    KeywordMarketMonitor package,
    Func<DateTimeOffset>? utcNow = null,
    Action<string>? log = null)
{
    public const string NewsSection = "공홈 수집";
    public const string MarketSection = "시세 수집";

    private readonly Func<DateTimeOffset> m_UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Action<string> m_Log = log ?? (msg => Console.WriteLine($"[몰리상태] {msg}"));

    public MollyStatusTracker Tracker => tracker;

    /// <param name="checkChannel">등록 채널 ID와 파일 첨부 필요 여부로 채널·봇 권한을 확인한다.</param>
    public async Task<StatusReportInput> GatherAsync(ulong guildId, Func<ulong, bool, Task<ChannelCheck>> checkChannel, CancellationToken ct = default)
    {
        var now = m_UtcNow();
        var alerts = new List<AlertEntry>();
        var collections = new List<CollectionLine>();

        foreach (var board in NewsBoards.Subscribable)
            alerts.Add(await SafeAlertAsync(board.Name, () => NewsAlertAsync(guildId, board, checkChannel, ct)).ConfigureAwait(false));
        alerts.Add(await SafeAlertAsync("이벤트 마감", () => EventExpireAlertAsync(guildId, checkChannel, ct)).ConfigureAwait(false));
        alerts.Add(await SafeAlertAsync("해연 시세", () => HaeyeonAlertAsync(guildId, checkChannel, ct)).ConfigureAwait(false));
        alerts.Add(await SafeAlertAsync($"{box.Definition.DisplayName} 시세", () => KeywordAlertAsync(box, guildId, checkChannel, ct)).ConfigureAwait(false));
        alerts.Add(await SafeAlertAsync($"{package.Definition.DisplayName} 시세", () => KeywordAlertAsync(package, guildId, checkChannel, ct)).ConfigureAwait(false));
        bool Uses(string alertName) => alerts.Any(x => x.Name == alertName && x.Registered);

        // 점검 알림은 공지 목록 수집을 함께 쓰므로 수집 상태를 따로 두지 않고 공지 줄에 합친다.
        foreach (var board in NewsBoards.All)
            collections.Add(await SafeCollectionAsync(NewsSection, board.Name,
                NewsBoards.Subscribable.Any(alert => alert.SourceBoard == board.Id && Uses(alert.Name)), StatusJobs.NewsSchedule, now,
                () => NewsInputAsync(board, ct)).ConfigureAwait(false));
        collections.Add(await SafeCollectionAsync(NewsSection, "이벤트 마감용 목록", Uses("이벤트 마감"), StatusJobs.EventListSchedule, now,
            () => EventListInputAsync(ct)).ConfigureAwait(false));
        collections.Add(await SafeCollectionAsync(MarketSection, "해연", Uses("해연 시세"), StatusJobs.MarketHourlySchedule, now,
            () => MarketInputAsync(StatusJobs.HaeyeonMonitorId, haeyeon.Store.GetLatestCollectedAtAsync, ct)).ConfigureAwait(false));
        foreach (var monitor in new[] { box, package })
            collections.Add(await SafeCollectionAsync(MarketSection, monitor.Definition.DisplayName, Uses($"{monitor.Definition.DisplayName} 시세"),
                StatusJobs.MarketHourlySchedule, now, () => MarketInputAsync(monitor.Definition.Id, monitor.Store.GetLatestCollectedAtAsync, ct)).ConfigureAwait(false));

        var (failures, lastFailure) = tracker.RecordFailures;
        return new StatusReportInput(now, tracker.ProcessStartedAt, collections, alerts, failures, lastFailure);
    }

    // ── 수집 상태 ─────────────────────────────────────────

    private async Task<CollectionLine> SafeCollectionAsync(string section, string name, bool used, JobSchedule schedule, DateTimeOffset now,
        Func<Task<JobStatusInput>> read)
    {
        try { return new CollectionLine(section, name, CollectionStatusJudge.Judge(await read().ConfigureAwait(false), schedule, tracker.ProcessStartedAt, now), used); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            m_Log($"{name} 수집 상태 조회 실패: {ex.GetType().Name}: {ex.Message}");
            return new CollectionLine(section, name, null, used);
        }
    }

    private async Task<JobStatusInput> NewsInputAsync(NewsBoard board, CancellationToken ct)
    {
        var jobId = StatusJobs.News(board);
        var state = await news.Store.GetBoardStateAsync(board.Id, ct).ConfigureAwait(false);
        var record = JobStatusRecord.Merge(jobId, await tracker.Store.GetJobAsync(jobId, ct).ConfigureAwait(false), state?.LastSuccessAt);
        var disabled = news.IsCollecting ? null : new JobDisabled(false, "설정(OfficialNews:Enabled=false)으로 꺼 둠");
        // 첫 기준을 잡았으면 실패해도 다음 확인에서 놓친 글을 다시 찾는다.
        return new JobStatusInput(record, tracker.RunningSince(jobId), state is not null, disabled);
    }

    private async Task<JobStatusInput> EventListInputAsync(CancellationToken ct)
    {
        var snapshot = events.LastSnapshot;
        DateTimeOffset? fetchedAt = snapshot is null
            ? null
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(snapshot.FetchedAtKst, DateTimeKind.Unspecified), MobiTime.timezone), TimeSpan.Zero);
        var record = JobStatusRecord.Merge(StatusJobs.EventList, await tracker.Store.GetJobAsync(StatusJobs.EventList, ct).ConfigureAwait(false), fetchedAt);
        // 마감 알림을 켠 서버가 없으면 목록을 주기적으로 다시 읽지 않는다(/진행중인이벤트 조회 때만 갱신).
        var disabled = await eventSettings.AnyEnabledAsync(ct).ConfigureAwait(false)
            ? null
            : new JobDisabled(false, "마감 알림을 등록한 서버가 없어 주기적으로 확인하지 않음");
        // 목록은 메모리에만 있어 재시작 뒤 첫 수집 전에는 쓸 데이터가 없다.
        return new JobStatusInput(record, tracker.RunningSince(StatusJobs.EventList), snapshot is not null, disabled);
    }

    private async Task<JobStatusInput> MarketInputAsync(string monitorId, Func<CancellationToken, Task<DateTimeOffset?>> latestCollectedAt, CancellationToken ct)
    {
        var jobId = StatusJobs.Market(monitorId);
        var latest = await latestCollectedAt(ct).ConfigureAwait(false);
        var record = JobStatusRecord.Merge(jobId, await tracker.Store.GetJobAsync(jobId, ct).ConfigureAwait(false), latest);
        var disabled = !mobiLife.IsEnabled ? new JobDisabled(false, "설정(MobiLife:Enabled=false)으로 꺼 둠")
            : !mobiLife.HasApiKey ? new JobDisabled(true, "모비라이프 API 키가 설정되지 않음")
            : null;
        return new JobStatusInput(record, tracker.RunningSince(jobId), latest is not null, disabled);
    }

    // ── 이 서버의 알림 ────────────────────────────────────

    private async Task<AlertEntry> SafeAlertAsync(string name, Func<Task<AlertEntry>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            m_Log($"{name} 알림 설정 조회 실패: {ex.GetType().Name}: {ex.Message}");
            return AlertEntry.Failed(name);
        }
    }

    private async Task<AlertEntry> NewsAlertAsync(ulong guildId, NewsBoard board, Func<ulong, bool, Task<ChannelCheck>> checkChannel, CancellationToken ct)
    {
        var subscription = await news.Store.GetSubscriptionAsync(guildId, board.Id, ct).ConfigureAwait(false);
        if (subscription is not { Enabled: true }) return new AlertEntry(board.Name, null, null, ChannelCheck.NotRegistered, null);
        var channelId = subscription.ChannelId;
        DeliveryView? delivery = null;
        try
        {
            var (sent, failed, code) = await news.Store.GetDeliveryHealthAsync(guildId, board.Id, channelId, ct).ConfigureAwait(false);
            delivery = DeliveryView.For(sent, failed, StatusErrorKinds.FromNewsDeliveryCode(code), subscription.UpdatedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { m_Log($"{board.Name} 전송 기록 조회 실패: {ex.GetType().Name}: {ex.Message}"); }
        // 썸네일이 없는 글은 기본 이미지를 파일로 첨부해 보낸다.
        return new AlertEntry(board.Name, channelId, null, await SafeCheckAsync(checkChannel, channelId, true).ConfigureAwait(false), delivery);
    }

    private async Task<AlertEntry> EventExpireAlertAsync(ulong guildId, Func<ulong, bool, Task<ChannelCheck>> checkChannel, CancellationToken ct)
    {
        const string name = "이벤트 마감";
        var setting = await eventSettings.LoadAsync(guildId, ct).ConfigureAwait(false);
        if (setting is not { Enabled: true } || setting.ChannelId == 0) return new AlertEntry(name, null, null, ChannelCheck.NotRegistered, null);
        return new AlertEntry(name, setting.ChannelId, $"{setting.HoursBefore}시간 전",
            await SafeCheckAsync(checkChannel, setting.ChannelId, false).ConfigureAwait(false),
            // 마감 알림 설정에는 등록 시각이 없어 채널로만 현재 설정과 연결한다.
            await DeliveryAsync(guildId, StatusAlerts.EventExpire, setting.ChannelId, null, ct).ConfigureAwait(false));
    }

    private async Task<AlertEntry> HaeyeonAlertAsync(ulong guildId, Func<ulong, bool, Task<ChannelCheck>> checkChannel, CancellationToken ct)
    {
        const string name = "해연 시세";
        var channel = await haeyeon.Store.GetChannelAsync(guildId, ct).ConfigureAwait(false);
        if (channel is null) return new AlertEntry(name, null, null, ChannelCheck.NotRegistered, null);
        var thresholds = channel.Thresholds;
        return new AlertEntry(name, channel.ChannelId, $"장비 {Percent(thresholds.Product)} / 재료 {Percent(thresholds.Material)}",
            await SafeCheckAsync(checkChannel, channel.ChannelId, false).ConfigureAwait(false),
            await DeliveryAsync(guildId, StatusAlerts.Market(StatusJobs.HaeyeonMonitorId), channel.ChannelId, channel.RegisteredAtUtc, ct).ConfigureAwait(false));
    }

    private async Task<AlertEntry> KeywordAlertAsync(KeywordMarketMonitor monitor, ulong guildId, Func<ulong, bool, Task<ChannelCheck>> checkChannel, CancellationToken ct)
    {
        var name = $"{monitor.Definition.DisplayName} 시세";
        var channel = await monitor.Store.GetChannelAsync(guildId, ct).ConfigureAwait(false);
        if (channel is null) return new AlertEntry(name, null, null, ChannelCheck.NotRegistered, null);
        return new AlertEntry(name, channel.ChannelId, $"등락률 {Percent(channel.Threshold)}",
            await SafeCheckAsync(checkChannel, channel.ChannelId, false).ConfigureAwait(false),
            await DeliveryAsync(guildId, StatusAlerts.Market(monitor.Definition.Id), channel.ChannelId, channel.RegisteredAtUtc, ct).ConfigureAwait(false));
    }

    private async Task<DeliveryView?> DeliveryAsync(ulong guildId, string alertKind, ulong channelId, DateTimeOffset? settingSince, CancellationToken ct)
    {
        try { return DeliveryView.For(await tracker.Store.GetDeliveryAsync(guildId, alertKind, ct).ConfigureAwait(false), channelId, settingSince); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            m_Log($"{alertKind} 전송 기록 조회 실패: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<ChannelCheck> SafeCheckAsync(Func<ulong, bool, Task<ChannelCheck>> checkChannel, ulong channelId, bool needsAttachFiles)
    {
        try { return await checkChannel(channelId, needsAttachFiles).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            m_Log($"채널 확인 실패(channel:{channelId}): {ex.GetType().Name}: {ex.Message}");
            return ChannelCheck.CheckFailed;
        }
    }

    private static string Percent(decimal rate) => (rate * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
