using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class RankingCommand :  InteractionModuleBase<SocketInteractionContext>
{
    private const int RANKING_TIMEOUT_MS = 180_000;
    [SlashCommand("전투력랭킹", "캐릭터의 전투력 랭킹을 가져옵니다.")]
    public async Task Command_Rank1(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 선택 (미입력시 전체 클래스)"), Autocomplete(typeof(RankClassAutocomplete))] string? className = null)
        => await ProcessCommand(1, nickname, server, className);
    
    [SlashCommand("매력랭킹", "캐릭터의 매력 랭킹을 가져옵니다.")]
    public async Task Command_Rank2(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 선택 (미입력시 전체 클래스)"), Autocomplete(typeof(RankClassAutocomplete))] string? className = null)
        => await ProcessCommand(2, nickname, server, className);
    
    [SlashCommand("생활력랭킹", "캐릭터의 생활력 랭킹을 가져옵니다.")]
    public async Task Command_Rank3(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 선택 (미입력시 전체 클래스)"), Autocomplete(typeof(RankClassAutocomplete))] string? className = null)
        => await ProcessCommand(3, nickname, server, className);

    [SlashCommand("종합랭킹", "캐릭터의 종합 랭킹을 가져옵니다.")]
    public async Task Command_Rank4(
        [Summary("캐릭터이름", "캐릭터 이름 입력")] string nickname,
        [Summary("서버", "서버 선택 (기본값은 칼릭스. 이유는 개발자가 칼릭서 서버)")] MobiServer server = 0,
        [Summary("클래스이름", "클래스 선택 (미입력시 전체 클래스)"), Autocomplete(typeof(RankClassAutocomplete))] string? className = null)
        => await ProcessCommand(4, nickname, server, className);

    private async Task ProcessCommand(int rankingIndex, string nickname, MobiServer server, string? className = null)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            await RespondAsync($"캐릭터 이름은 필수로 입력해주세요.");
            return;
        }

        // 자동완성을 쓰지 않고 직접 입력한 값도 받을 수 있어 알려진 클래스 이름인지 확인한다.
        className = MobiRankBrowser.NormalizeClassName(className, out var validClass);
        if (!validClass)
        {
            await RespondAsync("클래스 이름을 목록에서 골라주세요. (예: " + string.Join(", ", MobiRankBrowser.ClassNames.Take(5)) + " …)", ephemeral: true);
            return;
        }

        if (MobiRankBrowser.IsWarmingUp)
        {
            await RespondAsync(MobiRankBrowser.WarmingUpMessage, ephemeral: true);
            return;
        }

        var guildId = Context.Guild?.Id ?? 0;
        if (MobiRankBrowser.IsFullRunning(guildId))
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
        var timeoutSeconds = RANKING_TIMEOUT_MS / 1000;
        await ModifyOriginalResponseAsync(m => m.Content = $"🔎 검색을 시작했어요... (최대 {timeoutSeconds}초)");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(RANKING_TIMEOUT_MS));
        try
        {
            var result = await MobiRankBrowser.GetRankBySearchAsync(rankingIndex, nickname, server, className, cts.Token, guildId: guildId);
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

                var rankScope = className is null ? "전체" : ClassLabel(className);

                await FollowupAsync(
                    $"🏆 [{result.ServerName}] {rankScope} {result.Rank:n0}위\n" +
                    $"👤 {nickname}\n" +
                    $"👑 점수: {totalScore:n0}점 = ⚔{combatText} + 🌱{lifeText} + 💕{charmText}",
                    ephemeral: false);
            }
            else if (className is null)
            {
                await FollowupAsync($"🏆 [{result.ServerName}] 전체 {result.Rank:n0}위\n👤 {nickname} ({ClassLabel(result.ClassName)})\n{keywordEmoji}️ {keyword}：{result.Power:n0}", ephemeral: false);
            }
            else
            {
                await FollowupAsync($"🏆 [{result.ServerName}] {ClassLabel(className)} {result.Rank:n0}위\n👤 {nickname}\n{keywordEmoji}️ {keyword}：{result.Power:n0}", ephemeral: false);
            }
        }
        catch (TaskCanceledException)
        {
            await ModifyOriginalResponseAsync(m => m.Content = $"⏱️ 작업이 제한 시간({timeoutSeconds}초)을 초과했어요.");
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"[랭킹] 페이지 준비 시간 초과: {ex.Message}");
            await ModifyOriginalResponseAsync(m => m.Content =
                $"랭킹 페이지 처리 시간이 초과되었어요. ({ex.Message})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[랭킹] 조회 실패: {ex}");
            await ModifyOriginalResponseAsync(m => m.Content =
                "랭킹을 조회하는 중 오류가 발생했어요. 잠시 후 다시 시도해주세요.");
        }
    }

    /// <summary>랭킹 클래스 이름 앞에 배틀 명령과 같은 클래스 이모지를 붙인다. 배틀 시트에 없는 클래스(견습 등)는 이름만 쓴다.</summary>
    public static string ClassLabel(string className)
        => Program.instance?.Battles?.Current.Classes.Values.FirstOrDefault(x => x.Name == className) is { } battleClass
            ? ClassEmojis.Label(battleClass.Id, className)
            : className;
}

/// <summary>랭킹 클래스 옵션 자동완성. 공식 랭킹의 클래스 목록에서 입력한 글자가 이름에 들어간 것을 보여준다.</summary>
public sealed class RankClassAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var typed = autocompleteInteraction.Data.Current.Value?.ToString()?.Trim() ?? "";
        var suggestions = Suggest(typed).Select(x => new AutocompleteResult(x, x));
        return Task.FromResult(AutocompletionResult.FromSuccess(suggestions));
    }

    /// <summary>Discord 자동완성은 25개까지라 입력이 없으면 목록 앞쪽 25개만 보인다. 견습 클래스는 "견습"을 입력하면 모두 나온다.</summary>
    public static IEnumerable<string> Suggest(string typed)
        => MobiRankBrowser.ClassNames.Where(x => typed.Length == 0 || x.Contains(typed, StringComparison.Ordinal)).Take(25);
}
