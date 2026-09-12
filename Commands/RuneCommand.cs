using System.Text;
using Discord;
using Discord.Interactions;
using Molly.Runes;

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

        await Program.instance.Runes.EnsureFreshAsync(TimeSpan.FromMinutes(10));
        var table = Program.instance.Runes.Current;
        if (table.Items.Count == 0)
        {
            await RespondAsync("룬 데이터를 아직 준비하지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
            return;
        }

        await DeferAsync();
        var results = RuneSearch.Find(table, keyword);
        if (results.Count == 0)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "이름에 해당 검색어가 포함된 룬을 찾지 못했어요.");
            return;
        }

        var text = RuneSearch.Format(results);
        var chunks = RuneSearch.SplitMessages(text);
        // 아주 많은 결과도 누락하지 않고 한 파일에 제공합니다.
        if (chunks.Count > 5)
        {
            await ModifyOriginalResponseAsync(m => m.Content = $"룬 {results.Count}개를 찾았어요. 전체 결과는 첨부 파일에서 확인해주세요.");
            using var file = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray());
            await FollowupWithFileAsync(file, "룬-검색결과.txt", allowedMentions: AllowedMentions.None);
            return;
        }

        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = chunks[0];
            m.AllowedMentions = AllowedMentions.None;
        });
        foreach (var chunk in chunks.Skip(1))
            await FollowupAsync(chunk, allowedMentions: AllowedMentions.None);
    }
}
