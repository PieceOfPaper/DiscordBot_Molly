using System.Globalization;
using Discord.Interactions;
using Molly.Currency;

namespace DiscordBot_Molly.Commands;

public sealed class BagCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("가방", "이 서버에서 모은 마물 퇴치 증표 등 내 가방을 봅니다.")]
    public async Task ShowAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("가방은 서버마다 따로 있어서 DM에서는 볼 수 없어요.", ephemeral: true);
            return;
        }
        try
        {
            var balance = await Program.instance.Tokens.GetBalanceAsync(Context.Guild.Id, Context.User.Id);
            await RespondAsync(Format(Context.Guild.Name, balance, MollyToken.Emoji), ephemeral: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[가방] 조회 실패: " + ex.GetType().Name);
            await RespondAsync("가방을 열지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
        }
    }

    /// <summary>
    /// 가방 메시지. 새로 보관할 것이 생기면 증표 줄 아래에 한 줄씩 더한다. 테스트에서 문구를 검사할 수 있도록 공개한다.
    /// 이름을 함께 쓰므로 이모지는 이름 앞 아이콘으로만 붙인다.
    /// </summary>
    public static string Format(string guildName, long tokenBalance, string? tokenEmoji)
        => "🎒 **내 가방** (" + guildName + ")\n"
            + tokenEmoji + "**" + MollyToken.Name + "** " + tokenBalance.ToString("N0", CultureInfo.InvariantCulture) + "개\n"
            + "-# " + MollyToken.Name + "는 몰리 전용 놀이 재화예요. 실제 마비노기 모바일 재화와는 관계없고, 서버마다 따로 모여요.";
}
