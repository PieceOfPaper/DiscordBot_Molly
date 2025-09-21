using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class EventCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("진행중인이벤트", "현재 진행중인 이벤트를 보자.")]
    public async Task Command_CurrentEvents()
    {
        if (MobiEventBrowser.IsFullRunning())
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
            strBuilder.AppendLine($"{dateTimeNow:yyyy-MM-dd HH:mm:ss} 기준 진행중인 이벤트 입니다.");
            results.Sort((a, b) => a.end.CompareTo(b.end));
            foreach (var result in results)
            {
                strBuilder.Append('\n');
                strBuilder.Append($"- [D-{(int)Math.Floor((result.end - dateTimeNow).TotalDays)}] {result.eventName} ({result.url})");
            }
            await FollowupAsync(strBuilder.ToString(), ephemeral: false);
        }
        catch (TaskCanceledException)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "⏱️ 작업이 제한 시간(60초)을 초과했어요.");
        }
    }
}
