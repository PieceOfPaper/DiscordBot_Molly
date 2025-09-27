using System.Text.Json.Serialization;
using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public sealed class EventAlertSetting
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("channelId")]
    public ulong ChannelId { get; set; }

    [JsonPropertyName("hoursBefore")]
    public int HoursBefore { get; set; } = 24;
}

public class EventCommand : InteractionModuleBase<SocketInteractionContext>
{
    public static readonly LocalStorage<EventAlertSetting> s_AlertSettingStorage = new();
    
    [SlashCommand("진행중인이벤트", "현재 진행중인 이벤트를 보자.")]
    public async Task Command_CurrentEvents()
    {
        if (MobiEventBrowser.IsCachingRunning())
        {
            await DeferAsync(ephemeral: true);
            await ModifyOriginalResponseAsync(m => m.Content = "잠시 후에 다시 시도해주세요.");
            return;
        }

        // 1) 3초 내 ACK
        await DeferAsync(ephemeral: true);

        // (선택) 간헐적 시계오차 이슈 대응
        // DiscordSocketConfig.UseInteractionSnowflakeDate = false 로도 완화 가능 (부트스트랩시 적용)

        // 2) 진행중 메시지 갱신
        await ModifyOriginalResponseAsync(m => m.Content = "🔎 검색을 시작했어요... (최대 60초)");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var results = await MobiEventBrowser.GetCurrentEventsAsync(cts.Token);
            if (results == null || results.Any() == false)
            {
                await ModifyOriginalResponseAsync(m => m.Content =
                    $"진행중인 이벤트를 찾는데 실패했어요.");
                return;
            }

            await ModifyOriginalResponseAsync(m => m.Content = 
                $"🔎 진행중인 이벤트를 찾았습니다!");

            var dateTimeNow = MobiTime.now;
            var strBuilder = new System.Text.StringBuilder();
            strBuilder.Append($"> {dateTimeNow:yyyy-MM-dd HH:mm:ss} 기준 진행중인 이벤트 입니다.");
            results.Sort((a, b) => a.end.CompareTo(b.end));
            foreach (var result in results)
            {
                strBuilder.Append('\n');
                if (result.isPerma) strBuilder.Append($"- **[별도 안내 시 까지]** [{result.eventName}]({result.url})");
                else
                {
                    var remainTimespan = result.end.Date - dateTimeNow.Date;
                    var remainDay = (int)Math.Floor(remainTimespan.TotalDays);
                    strBuilder.Append($"- **[D-{remainDay}]** [{result.eventName}]({result.url})");
                }
            }
            await FollowupAsync(strBuilder.ToString(), ephemeral: false, flags: MessageFlags.SuppressEmbeds);
        }
        catch (TaskCanceledException)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "⏱️ 작업이 제한 시간(60초)을 초과했어요.");
        }
    }
    
    [SlashCommand("이벤트마감알림등록", "이 채널로 이벤트 마감 알림을 받도록 등록합니다.")]
    public async Task Command_RegistEventExpireAlert([Summary("시간", "마감 몇 시간 전에 알림할지 (기본 24)")] int? hours = null)
    {
        var guildId = Context.Interaction.GuildId;
        if (guildId is null)
        {
            await RespondAsync("DM에서는 사용할 수 없어요.", ephemeral: true);
            return;
        }
        
        var channelId = Context.Interaction.ChannelId;
        if (channelId is null)
        {
            await RespondAsync("채널 정보를 불러오지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
            return;
        }

        var hoursBefore = (hours is >= 1 and <= 240) ? hours.Value : 24;

        var settings = new EventAlertSetting
        {
            Enabled = true,
            ChannelId = channelId.Value,
            HoursBefore = hoursBefore
        };
        await s_AlertSettingStorage.SaveAsync(guildId.Value, settings);

        await RespondAsync(
            $"이 채널(<#{channelId}>)에 **{hoursBefore}시간 전** 알림을 등록했어요.",
            ephemeral: false);
    }

    [SlashCommand("이벤트마감알림해제", "이 길드의 이벤트 마감 알림을 비활성화합니다.")]
    public async Task Command_UnregistEventExpireAlert()
    {
        var guildId = Context.Interaction.GuildId;
        if (guildId is null)
        {
            await RespondAsync("DM에서는 사용할 수 없어요.", ephemeral: true);
            return;
        }
        
        var current = await s_AlertSettingStorage.LoadAsync(guildId.Value).ConfigureAwait(false) ?? new EventAlertSetting();
        current.Enabled = false;
        await s_AlertSettingStorage.SaveAsync(guildId.Value, current).ConfigureAwait(false);
        await RespondAsync("이벤트 마감 알림을 비활성화했어요.", ephemeral: false);
    }
}
