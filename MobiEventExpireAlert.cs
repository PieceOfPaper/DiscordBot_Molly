using System.Text.Json.Serialization;
using Discord;

public sealed class EventExpireAlertSetting
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("channelId")]
    public ulong ChannelId { get; set; }

    [JsonPropertyName("hoursBefore")]
    public int HoursBefore { get; set; } = 24;
}

public sealed class EventExpireAlertInfo
{
    public EventExpireAlertSetting setting;
    public List<MobiEventResult> results = new();
    public CancellationTokenSource cts;
}

public static class MobiEventExpireAlert
{
    private static readonly LocalStorage<EventExpireAlertSetting> s_ExpireAlertSettingStorage = new();
    private static readonly Dictionary<ulong, List<EventExpireAlertInfo>> s_EventExpireAlertInfos = new();
    
    private static Task s_UpdateTask = null;
    private static CancellationTokenSource s_UpdateTaskCancellationTokenSource = null;


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
            var token = s_UpdateTaskCancellationTokenSource.Token;
            while (!token.IsCancellationRequested)
            {
                var nowKst = MobiTime.now;
                var nextRunKst = GetNextUpdateTimeKst(nowKst);
                var delay = nextRunKst - nowKst;
                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;

                Console.WriteLine($"[MobiEventExpireAlert] Next update at {nextRunKst:yyyy-MM-dd HH:mm:ss} (KST)");

                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                    break;

                try
                {
                    await RegistEventExpireAlertAll().ConfigureAwait(false);
                    Console.WriteLine("[MobiEventExpireAlert] Update completed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MobiEventExpireAlert] Update failed: {ex.Message}");
                }
            }
        }, s_UpdateTaskCancellationTokenSource.Token);
    }

    private static DateTime GetNextUpdateTimeKst(DateTime nowKst)
    {
        var today = nowKst.Date;
        var nine = today.AddHours(9);
        var twentyOne = today.AddHours(21);

        if (nowKst < nine)
            return nine;
        if (nowKst < twentyOne)
            return twentyOne;
        return today.AddDays(1).AddHours(9);
    }

    public static async Task<EventExpireAlertSetting> LoadSetting(ulong guildId) => await s_ExpireAlertSettingStorage.LoadAsync(guildId).ConfigureAwait(false);
    
    public static async Task RegistEventExpireAlert(ulong guildId, EventExpireAlertSetting setting)
    {
        await s_ExpireAlertSettingStorage.SaveAsync(guildId, setting).ConfigureAwait(false);
        await ProcessRegistEventExpireAlert(guildId, setting);
    }

    public static async Task RegistEventExpireAlertAll()
    {
        var list = s_ExpireAlertSettingStorage.GetAllGuildIds();
        foreach (var guildId in list)
        {
            var setting = await s_ExpireAlertSettingStorage.LoadAsync(guildId);
            await ProcessRegistEventExpireAlert(guildId, setting);
        }
    }

    public static async Task TestSendEventExpireAlerts(ulong guildId)
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

        var eventList = await MobiEventBrowser.GetCurrentEventsAsync();
        if (eventList == null || eventList.Count == 0)
            return;
        
        var dateTimeNow = MobiTime.now;
        var groupedResults = new Dictionary<TimeSpan, List<MobiEventResult>>();
        foreach (var result in eventList)
        {
            if (result.isPerma) continue;
            if (result.end < dateTimeNow) continue; //지나간 것은 잊어라.
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
        
        var ch = await Program.instance.client.GetChannelAsync(info.setting.ChannelId).ConfigureAwait(false);
        if (ch is null)
        {
            Console.WriteLine($"[MobiEventExpireAlert] SendEventExpireAlertMessage - not found channel (guildId:{guildId}, channelId:{info.setting.ChannelId})");
            return;
        }
        
        if (ch is not IMessageChannel)
        {
            Console.WriteLine($"[MobiEventExpireAlert] SendEventExpireAlertMessage - is not message channel (guildId:{guildId}, channelId:{info.setting.ChannelId})");
            return;
        }

        foreach (var result in info.results)
        {
            var eb = new EmbedBuilder()
                .WithTitle($"⏰ 이벤트 마감 알림 - {result.eventName}")
                .WithDescription($"이벤트 종료까지 {info.setting.HoursBefore}시간 남았습니다.\n종료 시간: {result.end:yyyy-MM-dd HH:mm:ss}")
                .WithColor(Color.Orange)
                .WithFooter("몰리 • 이벤트 마감 알림")
                .WithCurrentTimestamp();

            if (!string.IsNullOrWhiteSpace(result.url))
                eb.WithUrl(result.url);
            if (!string.IsNullOrWhiteSpace(result.thumbnailUrl))
                eb.WithImageUrl(result.thumbnailUrl);

            await ((IMessageChannel)ch).SendMessageAsync(embed: eb.Build());
        }
    }
}
