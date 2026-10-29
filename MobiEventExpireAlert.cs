using System.Text.Json.Serialization;
using Discord;
using Molly.Status;

public sealed class EventExpireAlertSetting
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("channelId")]
    public ulong ChannelId { get; set; }

    [JsonPropertyName("hoursBefore")]
    public int HoursBefore { get; set; } = 24;

    [JsonPropertyName("lastAlertAtKst")]
    public DateTime? LastAlertAtKst { get; set; }
}

public sealed class EventExpireAlertInfo
{
    public EventExpireAlertSetting setting = null!;
    public List<MobiEventResult> results = new();
    public CancellationTokenSource cts = null!;
}

public static class MobiEventExpireAlert
{
    private static readonly EventExpireAlertSettingStore s_ExpireAlertSettingStorage = new();
    private static readonly Dictionary<ulong, List<EventExpireAlertInfo>> s_EventExpireAlertInfos = new();
    
    private static Task? s_UpdateTask;
    private static CancellationTokenSource? s_UpdateTaskCancellationTokenSource;
    // 주기 갱신과 등록·해제·테스트 명령이 같은 서버의 알림 예약을 동시에 바꾸지 않게 한다.
    private static readonly SemaphoreSlim s_ScheduleGate = new(1, 1);

    /// <summary>진행 중 이벤트 목록을 다시 읽어 서버별 마감 알림 예약을 고치는 주기. 목록은 MobiEventService가 1분간 캐시한다.</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMinutes(10);

    public static EventExpireAlertSettingStore Storage => s_ExpireAlertSettingStorage;

    /// <summary>/몰리상태에 서버별 전송 결과를 남길 곳. 시작할 때 한 번 지정하며, 없으면 기록하지 않는다.</summary>
    public static MollyStatusTracker? Status { get; set; }

    public static Task InitializeStorageAsync(CancellationToken ct = default)
        => s_ExpireAlertSettingStorage.InitializeAsync(ct);


    public static void RunUpdateTask(CancellationToken appToken = default)
    {
        if (s_UpdateTaskCancellationTokenSource != null)
        {
            s_UpdateTaskCancellationTokenSource.Cancel();
            s_UpdateTaskCancellationTokenSource = null;
        }

        s_UpdateTaskCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        s_UpdateTask = Task.Run(async () =>
        {
            var token = s_UpdateTaskCancellationTokenSource!.Token;
            Console.WriteLine($"[MobiEventExpireAlert] {UpdateInterval.TotalMinutes:0}분마다 이벤트 목록을 다시 확인해 마감 알림 예약을 갱신합니다.");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(UpdateInterval, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                try
                {
                    await RegistEventExpireAlertAll().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MobiEventExpireAlert] Update failed: {ex.Message}");
                }
            }
        }, s_UpdateTaskCancellationTokenSource.Token);
    }

    public static async Task<EventExpireAlertSetting?> LoadSetting(ulong guildId)
        => await s_ExpireAlertSettingStorage.LoadAsync(guildId).ConfigureAwait(false);
    
    public static async Task RegistEventExpireAlert(ulong guildId, EventExpireAlertSetting setting)
    {
        await s_ScheduleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await s_ExpireAlertSettingStorage.SaveAsync(guildId, setting).ConfigureAwait(false);
            await ProcessRegistEventExpireAlert(guildId, setting);
        }
        finally
        {
            s_ScheduleGate.Release();
        }
    }

    public static async Task RegistEventExpireAlertAll()
    {
        var list = await s_ExpireAlertSettingStorage.GetAllGuildIdsAsync().ConfigureAwait(false);
        foreach (var guildId in list)
        {
            await s_ScheduleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var setting = await s_ExpireAlertSettingStorage.LoadAsync(guildId);
                if (setting != null)
                    await ProcessRegistEventExpireAlert(guildId, setting);
            }
            finally
            {
                s_ScheduleGate.Release();
            }
        }
    }

    public static async Task TestSendEventExpireAlerts(ulong guildId)
    {
        await s_ScheduleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TestSendEventExpireAlertsCore(guildId);
        }
        finally
        {
            s_ScheduleGate.Release();
        }
    }

    private static async Task TestSendEventExpireAlertsCore(ulong guildId)
    {
        if (s_EventExpireAlertInfos.ContainsKey(guildId) == false)
            return;

        var infoList = s_EventExpireAlertInfos[guildId];
        foreach (var info in infoList)
            await info.cts.CancelAsync();
        foreach (var info in infoList)
        {
            _ = SendEventExpireAlertMessage(guildId, TimeSpan.Zero, info);
            break; //하나만 보내서 테스트하자.
        }
        infoList.Clear();
        s_EventExpireAlertInfos.Remove(guildId);

        var setting = await s_ExpireAlertSettingStorage.LoadAsync(guildId).ConfigureAwait(false);
        if (setting != null)
            await ProcessRegistEventExpireAlert(guildId, setting);
    }
    
    private static async Task ProcessRegistEventExpireAlert(ulong guildId, EventExpireAlertSetting setting)
    {
        if (s_EventExpireAlertInfos.ContainsKey(guildId))
        {
            var infoList = s_EventExpireAlertInfos[guildId];
            foreach (var info in infoList)
                await info.cts.CancelAsync();
            infoList.Clear();
        }
        else
        {
            s_EventExpireAlertInfos.Add(guildId, new());
        }

        if (setting.Enabled == false)
            return;

        var eventList = (await MobiEventService.Shared.GetCurrentEventsAsync())?.Events;
        if (eventList == null || eventList.Count == 0)
            return;
        
        var dateTimeNow = MobiTime.now;
        var groupedResults = new Dictionary<TimeSpan, List<MobiEventResult>>();
        foreach (var result in eventList)
        {
            if (result.isPerma) continue;
            if (result.end < dateTimeNow) continue; //지나간 것은 잊어라.
            var alertTimeKst = result.end - TimeSpan.FromHours(setting.HoursBefore);
            if (setting.LastAlertAtKst.HasValue && setting.LastAlertAtKst.Value >= alertTimeKst)
                continue; //이미 알림을 보냈던 시간대면 스킵.
            var timeSpan = result.end - dateTimeNow;
            if (groupedResults.ContainsKey(timeSpan) == false)
                groupedResults.Add(timeSpan, new());
            groupedResults[timeSpan].Add(result);
        }
        foreach (var kayPair in groupedResults)
        {
            var timeSpan = kayPair.Key - TimeSpan.FromHours(setting.HoursBefore);
            if (timeSpan < TimeSpan.Zero)
                timeSpan = TimeSpan.Zero;
            var cts = new CancellationTokenSource();
            var info = new EventExpireAlertInfo()
            {
                setting = setting,
                results = new(kayPair.Value),
                cts = cts,
            };
            s_EventExpireAlertInfos[guildId].Add(info);
            _ = SendEventExpireAlertMessage(guildId, timeSpan, info, cts.Token);
        }
    }

    private static async Task SendEventExpireAlertMessage(ulong guildId, TimeSpan timeSpan, EventExpireAlertInfo info, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[MobiEventExpireAlert] SendEventExpireAlertMessage - guildId:{guildId}, timeSpan:{timeSpan}");
        if (timeSpan > TimeSpan.Zero)
            await Task.Delay(timeSpan, cancellationToken);
        
        // 발송·기록 중에는 주기 갱신이 예약을 다시 만들지 못하게 한다. 갱신이 먼저 이 예약을 취소했다면 새 예약이 보낸다(중복 발송 방지).
        await s_ScheduleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            IChannel? ch;
            try { ch = await Program.instance.client.GetChannelAsync(info.setting.ChannelId).ConfigureAwait(false); }
            catch (Exception ex)
            {
                await RecordDeliveryAsync(guildId, info.setting.ChannelId, StatusErrorKinds.ClassifyDelivery(ex)).ConfigureAwait(false);
                throw;
            }
            if (ch is null)
            {
                Console.WriteLine($"[MobiEventExpireAlert] SendEventExpireAlertMessage - not found channel (guildId:{guildId}, channelId:{info.setting.ChannelId})");
                await RecordDeliveryAsync(guildId, info.setting.ChannelId, StatusErrorKinds.ChannelMissing).ConfigureAwait(false);
                return;
            }
        
            if (ch is not IMessageChannel)
            {
                Console.WriteLine($"[MobiEventExpireAlert] SendEventExpireAlertMessage - is not message channel (guildId:{guildId}, channelId:{info.setting.ChannelId})");
                await RecordDeliveryAsync(guildId, info.setting.ChannelId, StatusErrorKinds.NotMessageChannel).ConfigureAwait(false);
                return;
            }

            foreach (var result in info.results)
            {
                var remainingText = FormatRemaining(result.end - MobiTime.now);
                var eb = new EmbedBuilder()
                    .WithTitle($"⏰ 이벤트 마감 알림 - {result.eventName}")
                    .WithDescription($"이벤트 종료까지 {remainingText} 남았습니다.\n종료 시간: {result.end:yyyy-MM-dd HH:mm:ss}")
                    .WithColor(Color.Orange)
                    .WithFooter("몰리 • 이벤트 마감 알림")
                    .WithCurrentTimestamp();

                if (!string.IsNullOrWhiteSpace(result.url))
                    eb.WithUrl(result.url);
                if (!string.IsNullOrWhiteSpace(result.thumbnailUrl))
                    eb.WithImageUrl(result.thumbnailUrl);

                try { await ((IMessageChannel)ch).SendMessageAsync(embed: eb.Build()); }
                catch (Exception ex)
                {
                    await RecordDeliveryAsync(guildId, info.setting.ChannelId, StatusErrorKinds.ClassifyDelivery(ex)).ConfigureAwait(false);
                    throw;
                }
            }
            if (Status is { } status)
                await status.RecordDeliveryAsync(guildId, StatusAlerts.EventExpire, info.setting.ChannelId, true).ConfigureAwait(false);

            info.setting.LastAlertAtKst = MobiTime.now;
            await s_ExpireAlertSettingStorage.SaveAsync(guildId, info.setting).ConfigureAwait(false);
        }
        finally
        {
            s_ScheduleGate.Release();
        }
    }

    private static Task RecordDeliveryAsync(ulong guildId, ulong channelId, string errorKind) =>
        Status?.RecordDeliveryAsync(guildId, StatusAlerts.EventExpire, channelId, false, errorKind) ?? Task.CompletedTask;

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        var days = remaining.Days;
        var hours = remaining.Hours;
        var minutes = remaining.Minutes;
        var seconds = remaining.Seconds;

        var parts = new List<string>();
        if (days > 0)
            parts.Add($"{days}일");
        if (hours > 0)
            parts.Add($"{hours}시간");
        if (minutes > 0)
            parts.Add($"{minutes}분");
        if (days == 0 && hours == 0 && minutes == 0)
            parts.Add($"{seconds}초");

        return parts.Count == 0 ? "0초" : string.Join(" ", parts);
    }
}
