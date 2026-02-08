using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class RankingCommand :  InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("전투력랭킹", "캐릭터의 전투력 랭킹을 가져옵니다.")]
    public async Task Command_Rank1(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 이름 입력 (미입력시 모든 클래스)")] string? className = null)
        => await ProcessCommand(1, nickname, server, className);
    
    [SlashCommand("매력랭킹", "캐릭터의 매력 랭킹을 가져옵니다.")]
    public async Task Command_Rank2(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 이름 입력 (미입력시 모든 클래스)")] string? className = null)
        => await ProcessCommand(2, nickname, server, className);
    
    [SlashCommand("생활력랭킹", "캐릭터의 생활력 랭킹을 가져옵니다.")]
    public async Task Command_Rank3(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 이름 입력 (미입력시 모든 클래스)")] string? className = null)
        => await ProcessCommand(3, nickname, server, className);

    [SlashCommand("종합랭킹", "캐릭터의 종합 랭킹을 가져옵니다.")]
    public async Task Command_Rank4(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 이름 입력 (미입력시 전체)")] string? className = null)
        => await ProcessCommand(4, nickname, server, className);

    private async Task ProcessCommand(int rankingIndex, string nickname, MobiServer server, string? className = null)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            await RespondAsync($"캐릭터 이름은 필수로 입력해주세요.");
            return;
        }

        if (MobiRankBrowser.IsFullRunning(rankingIndex))
        {
            await DeferAsync(ephemeral: true);
            await ModifyOriginalResponseAsync(m => m.Content = "잠시 후에 다시 시도해주세요.");
            return;
        }

        var keyword = "전투력";
        var keywordEmoji = "⚔️";
        switch (rankingIndex)
        {
            case 1:
                keyword = "전투력";
                keywordEmoji = "⚔️";
                break;
            case 2:
                keyword = "매력";
                keywordEmoji = "💕";
                break;
            case 3:
                keyword = "생활력";
                keywordEmoji = "🌱️";
                break;
            case 4:
                keyword = "종합";
                keywordEmoji = "👑";
                break;
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
            var result = await MobiRankBrowser.GetRankBySearchAsync(rankingIndex, nickname, server, className, cts.Token);
            if (result == null)
            {
                await ModifyOriginalResponseAsync(m => m.Content =
                    $"{server}서버 {nickname}의 {keyword} 랭킹을 찾는데 실패했어요.");
                return;
            }

            await ModifyOriginalResponseAsync(m => m.Content = 
                $"🔎 {server}서버 {nickname}의 {keyword} 랭킹을 찾았습니다!");
            
            if (rankingIndex == 4)
            {
                var totalScore = result.TotalScore ?? result.Power;
                var combatText = result.Combat?.ToString("n0") ?? "?";
                var charmText = result.Charm?.ToString("n0") ?? "?";
                var lifeText = result.Life?.ToString("n0") ?? "?";

                var rankScope = (string.IsNullOrWhiteSpace(className) || className == "전체 클래스")
                    ? "전체"
                    : className;

                await FollowupAsync(
                    $"🏆 [{result.ServerName}] {rankScope} {result.Rank:n0}위\n" +
                    $"👤 {nickname}\n" +
                    $"👑 점수: {totalScore:n0}점 = ⚔{combatText} + 🌱{charmText} + 💕{lifeText}",
                    ephemeral: false);
            }
            else if (string.IsNullOrWhiteSpace(className) || className == "전체 클래스")
            {
                await FollowupAsync($"🏆 [{result.ServerName}] 전체 {result.Rank:n0}위\n👤 {nickname} ({result.ClassName})\n{keywordEmoji}️ {keyword}：{result.Power:n0}", ephemeral: false);
            }
            else
            {
                await FollowupAsync($"🏆 [{result.ServerName}] {className} {result.Rank:n0}위\n👤 {nickname}\n{keywordEmoji}️ {keyword}：{result.Power:n0}", ephemeral: false);
            }
        }
        catch (TaskCanceledException)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "⏱️ 작업이 제한 시간(60초)을 초과했어요.");
        }
    }
}
