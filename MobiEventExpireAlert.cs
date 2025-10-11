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

            await ((IMessageChannel)ch).SendMessageAsync(embed: eb.Build());
        }
    }
}
