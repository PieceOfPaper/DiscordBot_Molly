using System.Text;
using Discord;
using Discord.Interactions;
using Molly.Runes;
using Molly.Unpack;

namespace DiscordBot_Molly.Commands;

public class RuneCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("룬", "이름에 검색어가 포함된 룬을 모두 찾습니다.")]
    public async Task Search([Summary("이름", "검색할 룬 이름의 일부 (예: 분노)")] string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            await RespondAsync("검색할 룬 이름을 입력해주세요. 예: /룬 이름:분노", ephemeral: true);
            return;
        }

        var snapshot = Program.instance.Runes.Current;
        if (snapshot is null)
        {
            await RespondAsync(UnpackDataMessages.NoData(Program.instance.Runes.Name), ephemeral: true);
            return;
        }

        await DeferAsync();
        var results = RuneSearch.Find(snapshot.Data, keyword);
        if (results.Count == 0)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "🔍 이름에 해당 검색어가 포함된 룬을 찾지 못했어요.");
            return;
        }

        var footer = snapshot.PatchDisplay.Length > 0 ? $"몰리 • 게임 데이터 패치 {snapshot.PatchDisplay}" : "몰리 • 게임 데이터 기준";
        var header = RuneMessages.Header(keyword, results.Count);
        var pages = RuneMessages.BuildPages(results, RankingCommand.ClassLabel, footer);
        // 메시지가 너무 많아지는 결과는 이름 목록과 전체 결과 파일로 누락 없이 제공합니다.
        if (pages is null)
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = header;
                m.Embed = RuneMessages.BuildSummary(keyword, results, footer);
                m.AllowedMentions = AllowedMentions.None;
            });
            var text = RuneSearch.Format(results);
            using var file = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray());
            await FollowupWithFileAsync(file, "룬-검색결과.txt", allowedMentions: AllowedMentions.None);
            return;
        }

        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = header;
            m.Embeds = pages[0].ToArray();
            m.AllowedMentions = AllowedMentions.None;
        });
        foreach (var page in pages.Skip(1))
            await FollowupAsync(embeds: page.ToArray(), allowedMentions: AllowedMentions.None);
    }
}
