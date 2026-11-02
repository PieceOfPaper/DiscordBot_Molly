using Discord;
using Discord.Interactions;
using Molly.Shop;
using Molly.Unpack;

namespace DiscordBot_Molly.Commands;

public class ShopCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("상점검색", "아이템을 파는 NPC 상점과 물물교환을 게임 데이터에서 검색합니다.")]
    public async Task Command_Shop([Summary("아이템", "찾을 아이템 이름")] string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            await RespondAsync("아이템 이름을 입력해주세요.", ephemeral: true);
            return;
        }

        var snapshot = Program.instance.Shops.Current;
        if (snapshot is null)
        {
            await RespondAsync(UnpackDataMessages.NoData(Program.instance.Shops.Name), ephemeral: true);
            return;
        }

        await DeferAsync();
        var result = ShopSearch.Find(snapshot.Data, Program.instance.World.Data, itemName, DateTimeOffset.Now);
        if (result.Items.Count == 0)
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = ShopMessages.NoResult(itemName);
                m.AllowedMentions = AllowedMentions.None;
            });
            return;
        }

        var footer = snapshot.PatchDisplay.Length > 0 ? $"몰리 • 게임 데이터 패치 {snapshot.PatchDisplay}" : "몰리 • 게임 데이터 기준";
        var pages = ShopMessages.BuildPages(result, footer);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = ShopMessages.Header(itemName, result);
            m.Embeds = pages[0].ToArray();
            m.AllowedMentions = AllowedMentions.None;
        });
        foreach (var page in pages.Skip(1))
            await FollowupAsync(embeds: page.ToArray(), allowedMentions: AllowedMentions.None);
    }
}
