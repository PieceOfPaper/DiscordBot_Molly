using Discord;
using Discord.WebSocket;

namespace DiscordBot_Molly.Commands;

/// <summary>
/// 특정 채널로 알림을 받는 명령(공식 소식·이벤트 마감·시세 알림)은 등록·확인·해제·테스트 모두 서버 관리 권한이 있는 운영자만 쓴다.
/// 명령에는 DefaultMemberPermissions(ManageGuild)도 붙여 권한 없는 사용자에게는 기본으로 보이지 않게 하고, 서버가 명령 권한을 바꿔도 실행 시 다시 확인한다.
/// </summary>
internal static class AlertCommandAccess
{
    public const string DeniedMessage = "채널 알림은 서버 관리 권한이 있는 운영자만 등록·확인·해제·테스트할 수 있어요.";

    public static bool IsManager(IUser user) => user is SocketGuildUser { GuildPermissions.ManageGuild: true };
}
