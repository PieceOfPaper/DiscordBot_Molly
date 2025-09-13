using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class RankingCommand :  InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("전투력랭킹", "캐릭터의 전투력 랭킹을 가져옵니다.")]
    public async Task Command_CombatRank(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 이름 입력 (미입력시 모든 클래스)")] string? className = null)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            await RespondAsync($"캐릭터 이름은 필수로 입력해주세요.");
            return;
        }

        if (server == 0) server = MobiServer.칼릭스; //기본값 설정

        // 1) 3초 내 ACK
        await DeferAsync(ephemeral: true);

        // (선택) 간헐적 시계오차 이슈 대응
        // DiscordSocketConfig.UseInteractionSnowflakeDate = false 로도 완화 가능 (부트스트랩시 적용)

        // 2) 진행중 메시지 갱신
        await ModifyOriginalResponseAsync(m => m.Content = "🔎 검색을 시작했어요... (최대 60초)");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var result = await MobiRankBrowser.GetCombatRankBySearchAsync(nickname, server, className, cts.Token);
            if (result == null)
            {
                await ModifyOriginalResponseAsync(m => m.Content =
                    $"{server}서버에서 {nickname}의 랭킹을 찾는데 실패했어요.");
                return;
            }

            if (string.IsNullOrWhiteSpace(className) || className == "모든 클래스")
            {
                await ModifyOriginalResponseAsync(m => m.Content =
                    $"🏆 [{result.ServerName}] 전체 {result.Rank:n0}위\n👤 {nickname} ({result.ClassName})\n⚔️ 전투력：{result.Power:n0}");
            }
            else
            {
                await ModifyOriginalResponseAsync(m => m.Content =
                    $"🏆 [{result.ServerName}] {className} {result.Rank:n0}위\n👤 {nickname}\n⚔️ 전투력：{result.Power:n0}");
            }
        }
        catch (TaskCanceledException)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "⏱️ 작업이 제한 시간(60초)을 초과했어요.");
        }
    }

}
