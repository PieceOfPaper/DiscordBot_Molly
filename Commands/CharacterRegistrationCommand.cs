using Discord.Interactions;
using Molly.Battle;

namespace DiscordBot_Molly.Commands;

public sealed class CharacterRegistrationCommand : InteractionModuleBase<SocketInteractionContext>
{
    private const int RankingTimeoutMilliseconds = 60_000;

    [SlashCommand("캐릭터등록", "종합 랭킹에 있는 내 캐릭터를 등록합니다.")]
    public async Task RegisterAsync(
        [Summary("서버", "캐릭터가 있는 서버")] MobiServer server,
        [Summary("캐릭터이름", "등록할 캐릭터 이름")] string characterName)
    {
        var normalizedName = characterName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            await RespondAsync("캐릭터 이름은 필수로 입력해주세요.", ephemeral: true);
            return;
        }

        if (normalizedName.Length > 12)
        {
            await RespondAsync("캐릭터 이름은 12자 이하로 입력해주세요.", ephemeral: true);
            return;
        }

        if (MobiRankBrowser.IsFullRunning(4))
        {
            await RespondAsync("종합 랭킹 검색이 진행 중이에요. 잠시 후 다시 시도해주세요.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ModifyOriginalResponseAsync(message => message.Content = "🔎 종합 랭킹에서 캐릭터를 찾고 있어요...");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(RankingTimeoutMilliseconds));
        try
        {
            var rank = await MobiRankBrowser.GetRankBySearchAsync(4, normalizedName, server, null, cts.Token);
            if (rank is null)
            {
                await ModifyOriginalResponseAsync(message => message.Content =
                    $"[{server}] 서버의 `{normalizedName}`은(는) 종합 랭킹에 없는 캐릭터입니다.");
                return;
            }

            var battleClass = Program.instance.Battles.Current.Classes.Values
                .SingleOrDefault(x => string.Equals(x.Name, rank.ClassName, StringComparison.Ordinal));
            await Program.instance.RegisteredCharacters.SaveAsync(new RegisteredCharacter(
                Context.User.Id, server, normalizedName, DateTimeOffset.UtcNow,
                battleClass?.Id, rank.Combat ?? rank.Power, rank.Life, rank.Charm, DateTimeOffset.UtcNow), cts.Token);

            await ModifyOriginalResponseAsync(message => message.Content =
                $"✅ [{rank.ServerName}] 서버의 `{normalizedName}` 캐릭터를 등록했어요.");
        }
        catch (TaskCanceledException)
        {
            await ModifyOriginalResponseAsync(message => message.Content = "종합 랭킹 검색 시간이 초과되었어요. 잠시 후 다시 시도해주세요.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[캐릭터등록] 종합 랭킹 조회 또는 저장 실패: {ex}");
            await ModifyOriginalResponseAsync(message => message.Content = "캐릭터 등록 중 오류가 발생했어요. 잠시 후 다시 시도해주세요.");
        }
    }
}
