using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace DiscordBot_Molly.Commands;

public sealed class DefaultServerCommand : InteractionModuleBase<SocketInteractionContext>
{
    // 서버 관리 권한이 있는 운영자에게만 명령이 보인다. 디스코드 서버 설정의 연동(Integrations)에서 권한을 바꿀 수 있어 실행 시에도 다시 확인한다.
    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("기본서버지정", "이 디스코드 서버에서 랭킹·캐릭터등록에 기본으로 쓸 마비노기 모바일 서버를 지정합니다.")]
    public async Task SetAsync([Summary("서버", "기본으로 쓸 마비노기 모바일 서버")] MobiServer server)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("DM에서는 사용할 수 없어요.", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser { GuildPermissions.ManageGuild: true })
        {
            await RespondAsync("기본 서버는 서버 관리 권한이 있는 운영자만 지정할 수 있어요.", ephemeral: true);
            return;
        }

        if (!Enum.IsDefined(server))
        {
            await RespondAsync("서버를 목록에서 골라주세요.", ephemeral: true);
            return;
        }

        await Program.instance.DefaultServers.SaveAsync(Context.Guild.Id, server);
        await RespondAsync($"이 디스코드 서버의 기본 서버를 **{server}**(으)로 지정했어요. 랭킹·캐릭터등록에서 서버를 고르지 않으면 {server} 서버로 찾아요.");
    }
}
