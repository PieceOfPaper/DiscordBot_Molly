using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class EmblemRuneGachaCommand :  InteractionModuleBase<SocketInteractionContext>
{
    //TODO - 데이터 테이블로 빼는게 좋을 것 같다.
    private const int LEGEND_COMBINE_RATE = 1000;
    private static readonly string[] EMBLEM_NAMES = new string[]
    {
        "엠블럼 룬: 굳건함 +",
        "엠블럼 룬: 날쌤 +",
        "엠블럼 룬: 강렬함 +",
        "엠블럼 룬: 광폭함 +",
        "엠블럼 룬: 현란함",
        "엠블럼 룬: 냉혹함",
        "엠블럼 룬: 여신의 권능",
        "엠블럼 룬: 여신의 가호",
    };
    
    [SlashCommand("엠블럼룬뽑기", "엠블럼 룬을 뽑아보자!")]
    public async Task Command_EmblemRuneGacha()
    {
        await RespondAsync(EMBLEM_NAMES[Random.Shared.Next(0, EMBLEM_NAMES.Length)]);
    }
    
    [SlashCommand("엠블럼룬합성", "엠블럼 룬을 합성해보자! (확률 10%)")]
    public async Task Command_EmblemRuneCombine()
    {
        var randRate = Random.Shared.Next(0, 10000);
        if (randRate < LEGEND_COMBINE_RATE)
        {
            await Command_EmblemRuneGacha();
            return;
        }

        await RespondAsync($"실패");
    }
}
