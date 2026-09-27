using System.Text;
using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public sealed class BattleRecordCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("전적", "등록한 캐릭터의 현재 정보와 클래스별 배틀 전적을 봅니다.")]
    public async Task ShowAsync([Summary("대상", "전적을 볼 사용자(비우면 나)")] IUser? target = null)
    {
        var user = target ?? Context.User;
        if (user.IsBot) { await RespondAsync("봇은 배틀 전적이 없어요.", ephemeral: true); return; }
        try
        {
            var character = await Program.instance.RegisteredCharacters.LoadAsync(user.Id);
            var records = await Program.instance.BattleRecords.LoadAsync(user.Id);
            if (character is null && records.Count == 0)
            {
                await RespondAsync(user.Mention + "님은 아직 `/캐릭터등록`으로 캐릭터를 등록하지 않았어요.", allowedMentions: AllowedMentions.None, ephemeral: target is null);
                return;
            }
            await RespondAsync(Format(user.Mention, character, records, ClassLabel), allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[전적] 조회 실패: " + ex.GetType().Name);
            await RespondAsync("전적을 불러오지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
        }
    }

    private static string ClassLabel(string classId)
        => ClassEmojis.Label(classId, Program.instance.Battles.Current.Classes.TryGetValue(classId, out var battleClass) ? battleClass.Name : classId);

    /// <summary>전적 메시지. 맨 위에 현재 캐릭터의 클래스와 전투력·생활력·매력, 아래에 클래스별 전적과 합계를 둔다. 테스트에서 문구를 검사할 수 있도록 공개한다.</summary>
    public static string Format(string ownerLabel, RegisteredCharacter? character, IReadOnlyList<BattleClassRecord> records, Func<string, string> classLabel)
    {
        var text = new StringBuilder("📜 **" + ownerLabel + "의 배틀 전적**\n");
        if (character is not null)
        {
            text.Append("[" + character.Server + "] " + character.CharacterName + " · " + (character.ClassId is { } classId ? classLabel(classId) : "클래스 확인 전") + "\n");
            // 능력치 이모지는 랭킹 명령(RankingCommand)과 같게 쓴다.
            text.Append("⚔️ 전투력 " + Stat(character.CombatPower) + " · 🌱 생활력 " + Stat(character.LifePower) + " · 💕 매력 " + Stat(character.CharmPower));
            // 능력치는 등록·배틀 때 조회한 랭킹 값이라 기준 시각을 함께 보여준다.
            if (character.LastSyncedAtUtc is { } syncedAt) text.Append(" (랭킹 기준 <t:" + syncedAt.ToUnixTimeSeconds() + ":R>)");
            text.Append('\n');
        }
        text.Append('\n');
        if (records.Count == 0) return text.Append("아직 배틀 전적이 없어요.").ToString();
        text.Append("📊 **전적**\n");
        foreach (var record in records) text.Append(classLabel(record.ClassId) + " " + Summary(record.Wins, record.Losses, record.Draws) + "\n");
        if (records.Count > 1) text.Append("**합계** " + Summary(records.Sum(x => x.Wins), records.Sum(x => x.Losses), records.Sum(x => x.Draws)) + "\n");
        return text.ToString().TrimEnd();
    }

    private static string Stat(int? value) => value?.ToString("N0") ?? "-";

    /// <summary>승률은 무승부를 포함한 전체 판수 기준이다.</summary>
    private static string Summary(int wins, int losses, int draws)
    {
        var total = wins + losses + draws;
        return wins + "승 " + losses + "패" + (draws > 0 ? " " + draws + "무" : "") + " · 승률 " + (wins * 100d / total).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
}
