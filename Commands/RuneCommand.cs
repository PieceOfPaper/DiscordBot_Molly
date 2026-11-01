using System.Text;
using Discord;
using Discord.Interactions;
using Molly.Runes;
using Molly.Unpack;

namespace DiscordBot_Molly.Commands;

public class RuneCommand : InteractionModuleBase<SocketInteractionContext>
{
    public const int DefaultSeason = 2;

    [SlashCommand("룬", "이름이나 효과에 검색어가 들어간 룬을 찾습니다. AND·OR·괄호로 검색어를 조합할 수 있어요.")]
    public async Task Search(
        [Summary("검색어", "룬 이름·효과에서 찾을 말. AND·OR·괄호 사용 가능 (예: (공격력 OR 치명타) AND 보호막)"), MaxLength(RuneQuery.MaxLength)] string keyword,
        [Summary("시즌", "검색할 룬 시즌 (기본 2)"), MinValue(0)] int season = DefaultSeason,
        [Summary("부위", "검색할 룬 부위 (비우면 전체 부위)")]
        [Choice("무기", "무기")]
        [Choice("방어구", "방어구")]
        [Choice("엠블럼", "엠블럼")]
        [Choice("장신구", "장신구")] string? part = null)
    {
        // 잘못된 검색식은 일반 검색으로 바꾸지 않고 원인과 예시를 안내합니다.
        var query = RuneQuery.Parse(keyword, out var error);
        if (query is null)
        {
            await RespondAsync($"⚠️ 검색식을 해석하지 못했어요: {error}\n예: `/룬 검색어:분노`, `/룬 검색어:공격 속도`, `/룬 검색어:{RuneQuery.Example}`",
                ephemeral: true);
            return;
        }
        RuneCategory? category = null;
        if (part is not null && (category = RuneNames.ParseCategory(part)) is null)
        {
            await RespondAsync("부위는 무기·방어구·엠블럼·장신구 중에서 골라주세요.", ephemeral: true);
            return;
        }

        var snapshot = Program.instance.Runes.Current;
        if (snapshot is null)
        {
            await RespondAsync(UnpackDataMessages.NoData(Program.instance.Runes.Name), ephemeral: true);
            return;
        }
        var seasons = snapshot.Data.Items.Select(x => x.Season).Distinct().Order().ToArray();
        if (!seasons.Contains(season))
        {
            await RespondAsync($"시즌 {season} 룬은 없어요. 검색할 수 있는 시즌: {string.Join("·", seasons)}", ephemeral: true);
            return;
        }

        await DeferAsync();
        var conditions = RuneSearch.Conditions(season, category);
        var results = RuneSearch.Find(snapshot.Data, query, season, category);
        if (results.Count == 0)
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = RuneMessages.NoResult(query.Text, conditions);
                m.AllowedMentions = AllowedMentions.None;
            });
            return;
        }

        var footer = snapshot.PatchDisplay.Length > 0 ? $"몰리 • 게임 데이터 패치 {snapshot.PatchDisplay}" : "몰리 • 게임 데이터 기준";
        var header = RuneMessages.Header(query.Text, conditions, results.Count);
        var pages = RuneMessages.BuildPages(results, RankingCommand.ClassLabel, footer);
        // 메시지가 너무 많아지는 결과는 이름 목록과 전체 결과 파일로 누락 없이 제공합니다.
        if (pages is null)
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = header;
                m.Embed = RuneMessages.BuildSummary(query.Text, results, footer);
                m.AllowedMentions = AllowedMentions.None;
            });
            var text = RuneSearch.Format(results, $"검색어: {query.Text} · {conditions}");
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
