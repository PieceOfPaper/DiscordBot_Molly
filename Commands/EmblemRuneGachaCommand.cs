using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class EmblemRuneGachaCommand :  InteractionModuleBase<SocketInteractionContext>
{
    private readonly Random m_Random = new Random();

    private static readonly string[] m_EmblemRuneNames = new string[]
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
        await RespondAsync($"뽑기결과: {m_EmblemRuneNames[m_Random.Next(0, m_EmblemRuneNames.Length)]}");
    }
}
