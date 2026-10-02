using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace DiscordBot_Molly.Commands;

public sealed class CharacterRegistrationStatusCommand : InteractionModuleBase<SocketInteractionContext>
{
    private const int MessageLimit = 2000;

    // 서버 관리 권한이 있는 운영자에게만 명령이 보인다. 디스코드 서버 설정의 연동(Integrations)에서 권한을 바꿀 수 있어 실행 시에도 다시 확인한다.
    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("캐릭터등록현황", "이 디스코드 서버 멤버들의 캐릭터 등록 현황을 봅니다.")]
    public async Task ShowAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("DM에서는 사용할 수 없어요.", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser { GuildPermissions.ManageGuild: true })
        {
            await RespondAsync("캐릭터 등록 현황은 서버 관리 권한이 있는 운영자만 볼 수 있어요.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        try
        {
            // 등록은 사용자별 전역이고 봇은 멤버 목록 인텐트를 쓰지 않으므로, 등록한 사용자마다 이 서버 멤버인지 REST로 확인한다.
            var all = await Program.instance.RegisteredCharacters.LoadAllAsync();
            var members = new List<(string Label, RegisteredCharacter Character)>();
            foreach (var character in all)
            {
                var member = await Context.Client.Rest.GetGuildUserAsync(Context.Guild.Id, character.DiscordUserId);
                if (member is not null) members.Add(("<@" + character.DiscordUserId + ">", character));
            }

            var pages = Format(Context.Guild.Name, members, ClassLabel);
            await ModifyOriginalResponseAsync(message => { message.Content = pages[0]; message.AllowedMentions = AllowedMentions.None; });
            foreach (var page in pages.Skip(1))
                await FollowupAsync(page, allowedMentions: AllowedMentions.None, ephemeral: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[캐릭터등록현황] 조회 실패: " + ex.GetType().Name);
            await ModifyOriginalResponseAsync(message => message.Content = "캐릭터 등록 현황을 불러오지 못했어요. 잠시 후 다시 시도해주세요.");
        }
    }

    private static string ClassLabel(string classId)
        => ClassEmojis.Label(classId, Program.instance.Battles.Current.Classes.TryGetValue(classId, out var battleClass) ? battleClass.Name : classId);

    /// <summary>
    /// 등록 현황 메시지. 첫 줄에 인원, 다음 줄에 클래스별 인원, 아래에 전투력 높은 순 목록을 두고 Discord 글자 수 제한에 맞춰 나눈다.
    /// 테스트에서 문구를 검사할 수 있도록 공개한다.
    /// </summary>
    public static IReadOnlyList<string> Format(string guildName, IReadOnlyList<(string Label, RegisteredCharacter Character)> members, Func<string, string> classLabel)
    {
        var header = new StringBuilder("📋 **" + guildName + " 캐릭터 등록 현황** · " + members.Count + "명\n");
        if (members.Count == 0) return [header.Append("아직 `/캐릭터등록`으로 캐릭터를 등록한 멤버가 없어요.").ToString()];

        var classes = members
            .GroupBy(x => x.Character.ClassId)
            .OrderByDescending(x => x.Count()).ThenBy(x => x.Key is null).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => (x.Key is { } classId ? classLabel(classId) : "클래스 확인 전") + " " + x.Count());
        header.Append(string.Join(" · ", classes)).Append('\n');

        var lines = members
            .OrderByDescending(x => x.Character.CombatPower ?? -1).ThenBy(x => x.Character.CharacterName, StringComparer.Ordinal)
            .Select((x, i) => (i + 1) + ". " + x.Label + " — [" + x.Character.Server + "] " + x.Character.CharacterName
                + " · " + (x.Character.ClassId is { } classId ? classLabel(classId) : "클래스 확인 전")
                + " · ⚔️ " + (x.Character.CombatPower?.ToString("N0") ?? "-")
                + " · 등록 <t:" + x.Character.RegisteredAtUtc.ToUnixTimeSeconds() + ":d>");

        var pages = new List<string>();
        var page = new StringBuilder(header.ToString()).Append('\n');
        foreach (var line in lines)
        {
            if (page.Length + line.Length + 1 > MessageLimit)
            {
                pages.Add(page.ToString().TrimEnd());
                page.Clear();
            }
            page.Append(line).Append('\n');
        }
        pages.Add(page.ToString().TrimEnd());
        return pages;
    }
}
