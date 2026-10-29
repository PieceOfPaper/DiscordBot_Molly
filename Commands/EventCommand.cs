using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class EventCommand : InteractionModuleBase<SocketInteractionContext>
{
    private const int EVENT_TIMEOUT_MS = 60_000;
    [SlashCommand("진행중인이벤트", "현재 진행중인 이벤트를 보자.")]
    public async Task Command_CurrentEvents(
        [Summary("마감미정", "마감일 미정(별도 안내 시 까지) 이벤트를 포함할지 여부 (기본 포함)")] bool includePerma = true)
    {
        // 1) 3초 내 ACK (동시 요청은 서비스가 한 번의 수집으로 합쳐 처리)
        await DeferAsync(ephemeral: true);

        // (선택) 간헐적 시계오차 이슈 대응
        // DiscordSocketConfig.UseInteractionSnowflakeDate = false 로도 완화 가능 (부트스트랩시 적용)

        // 2) 진행중 메시지 갱신
        var timeoutSeconds = EVENT_TIMEOUT_MS / 1000;
        await ModifyOriginalResponseAsync(m => m.Content = $"🔎 검색을 시작했어요... (최대 {timeoutSeconds}초)");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(EVENT_TIMEOUT_MS));
        try
        {
            var query = await MobiEventService.Shared.GetCurrentEventsAsync(cts.Token);
            var results = query?.Events.ToList();
            if (query == null || results == null || results.Count == 0)
            {
                await ModifyOriginalResponseAsync(m => m.Content =
                    $"진행중인 이벤트를 찾는데 실패했어요.");
                return;
            }

            await ModifyOriginalResponseAsync(m => m.Content = 
                $"🔎 진행중인 이벤트를 찾았습니다!");

            var dateTimeNow = MobiTime.now;
            var strBuilder = new System.Text.StringBuilder();
            if (query.IsStale)
                strBuilder.Append($"> ⚠️ 최신 목록을 가져오지 못해 {query.FetchedAtKst:yyyy-MM-dd HH:mm:ss}에 수집한 목록을 보여드려요.");
            else
                strBuilder.Append($"> {dateTimeNow:yyyy-MM-dd HH:mm:ss} 기준 진행중인 이벤트 입니다.");
            var appendedCount = 0;
            results.Sort((a, b) => a.end.CompareTo(b.end));
            foreach (var result in results)
            {
                if (result.isPerma)
                {
                    if (includePerma == false) continue;
                    strBuilder.Append('\n');
                    strBuilder.Append($"- **[별도 안내 시 까지]** [{result.eventName}]({result.url})");
                    appendedCount++;
                }
                else
                {
                    if (result.end < dateTimeNow) continue; //지나간 것은 잊어라.
                    var remainTimespan = result.end.Date - dateTimeNow.Date;
                    var remainDay = (int)Math.Floor(remainTimespan.TotalDays);
                    strBuilder.Append('\n');
                    strBuilder.Append($"- **[D-{remainDay}]** [{result.eventName}]({result.url})");
                    appendedCount++;
                }
            }
            if (appendedCount == 0)
            {
                await ModifyOriginalResponseAsync(m => m.Content = "조건에 맞는 진행중 이벤트가 없어요.");
                return;
            }
            var texts = SplitIntoDiscordChunks(strBuilder.ToString());
            foreach (var text in texts)
                await FollowupAsync(text, ephemeral: false, flags: MessageFlags.SuppressEmbeds);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await ModifyOriginalResponseAsync(m => m.Content = $"⏱️ 작업이 제한 시간({timeoutSeconds}초)을 초과했어요.");
        }
    }
    
    private const string ExpireActionSummary = "등록: 이 채널로 받기 · 확인: 현재 설정 · 해제: 끄기 · 테스트: 알림 하나 보내기 (기본 확인)";

    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("이벤트마감알림", "진행 중인 이벤트의 마감 임박 알림을 등록·확인·해제·테스트합니다.")]
    public async Task Command_EventExpireAlert(
        [Summary("작업", ExpireActionSummary)]
        [Choice(OfficialNewsCommand.Register, OfficialNewsCommand.Register)]
        [Choice(OfficialNewsCommand.Check, OfficialNewsCommand.Check)]
        [Choice(OfficialNewsCommand.Unregister, OfficialNewsCommand.Unregister)]
        [Choice(OfficialNewsCommand.Test, OfficialNewsCommand.Test)]
        string action = OfficialNewsCommand.Check,
        [Summary("시간", "등록할 때 마감 몇 시간 전에 알릴지 (1~240, 기본 24)")] int? hours = null)
    {
        var guildId = Context.Interaction.GuildId;
        if (guildId is null)
        {
            await RespondAsync("DM에서는 사용할 수 없어요.", ephemeral: true);
            return;
        }
        if (!AlertCommandAccess.IsManager(Context.User))
        {
            await RespondAsync(AlertCommandAccess.DeniedMessage, ephemeral: true);
            return;
        }

        switch (action)
        {
            case OfficialNewsCommand.Register:
                await RegistEventExpireAlertAsync(guildId.Value, hours);
                break;
            case OfficialNewsCommand.Unregister:
                await MobiEventExpireAlert.RegistEventExpireAlert(guildId.Value, new ());
                await RespondAsync("이벤트 마감 알림을 비활성화했어요.", ephemeral: false);
                break;
            case OfficialNewsCommand.Test:
                await TestEventExpireAlertAsync(guildId.Value);
                break;
            default:
                await CheckEventExpireAlertAsync(guildId.Value);
                break;
        }
    }

    private async Task RegistEventExpireAlertAsync(ulong guildId, int? hours)
    {
        var channelId = Context.Interaction.ChannelId;
        if (channelId is null)
        {
            await RespondAsync("채널 정보를 불러오지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
            return;
        }

        var hoursBefore = (hours is >= 1 and <= 240) ? hours.Value : 24;

        var setting = new EventExpireAlertSetting
        {
            Enabled = true,
            ChannelId = channelId.Value,
            HoursBefore = hoursBefore
        };
        await MobiEventExpireAlert.RegistEventExpireAlert(guildId, setting);
        await RespondAsync(
            $"이 채널(<#{channelId}>)에 **{hoursBefore}시간 전** 알림을 등록했어요.",
            ephemeral: false);
    }

    private async Task CheckEventExpireAlertAsync(ulong guildId)
    {
        var setting = await MobiEventExpireAlert.LoadSetting(guildId);
        if (setting == null || !setting.Enabled)
        {
            await RespondAsync("현재 이벤트 마감 알림 비활성화 상태입니다.", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"<#{setting.ChannelId}> 채널에 **{setting.HoursBefore}시간 전** 알림 등록되어있어요.",
            ephemeral: false);
    }

    private async Task TestEventExpireAlertAsync(ulong guildId)
    {
        var setting = await MobiEventExpireAlert.LoadSetting(guildId);
        if (setting == null || !setting.Enabled)
        {
            await RespondAsync("이벤트 마감 알림이 등록돼 있지 않아요. 먼저 `/이벤트마감알림 작업:등록`을 실행해주세요.", ephemeral: true);
            return;
        }

        await MobiEventExpireAlert.TestSendEventExpireAlerts(guildId);
        await RespondAsync($"<#{setting.ChannelId}> 채널로 마감 알림 하나를 테스트로 보냈어요. 알릴 진행 중 이벤트가 없으면 오지 않아요.", ephemeral: true);
    }

    private static IEnumerable<string> SplitIntoDiscordChunks(string text, int limit = 2000)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            // 한 줄 자체가 limit를 넘으면 잘라서 보냄
            if (line.Length > limit)
            {
                int idx = 0;
                while (idx < line.Length)
                {
                    int take = Math.Min(limit, line.Length - idx);
                    if (sb.Length > 0)
                    {
                        yield return sb.ToString();
                        sb.Clear();
                    }
                    yield return line.Substring(idx, take);
                    idx += take;
                }
                continue;
            }

            if (sb.Length + line.Length + 1 > limit)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }
}
