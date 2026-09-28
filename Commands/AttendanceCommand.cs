using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Molly.Attendance;
using Molly.Currency;

namespace DiscordBot_Molly.Commands;

public sealed class AttendanceCommand : InteractionModuleBase<SocketInteractionContext>
{
    // 서버 관리 권한이 있는 운영자에게만 명령이 보인다. 서버 설정의 연동(Integrations)에서 권한을 바꿀 수 있어 실행 시에도 다시 확인한다.
    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("출석부등록", "이 채널에 매일 오전 6시에 초기화되는 출석부를 만듭니다.")]
    public async Task RegisterAsync()
    {
        if (!await CheckManagerAsync("출석부는")) return;
        if (Context.Channel is not SocketTextChannel channel || channel is SocketThreadChannel)
        {
            await RespondAsync("출석부는 일반 텍스트 채널에만 만들 수 있어요.", ephemeral: true);
            return;
        }
        var permissions = Context.Guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks)
        {
            await RespondAsync("이 채널에서 몰리가 메시지를 보내거나 임베드를 쓸 권한이 없어요. 채널 권한을 확인해주세요.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        try
        {
            var result = await Program.instance.Attendance.RegisterAsync(Context.Guild.Id, channel.Id);
            await FollowupAsync(result switch
            {
                AttendanceRegisterResult.Moved => "출석부를 이 채널로 옮겼어요. 이전 출석부의 버튼은 꺼졌고, 오늘 출석 기록은 그대로 이어져요.",
                AttendanceRegisterResult.Reposted => "출석부를 새로 올렸어요. 이전 출석부의 버튼은 꺼졌고, 오늘 출석 기록은 그대로 이어져요.",
                _ => $"이 채널에 출석부를 만들었어요. 매일 오전 {AttendanceClock.ResetHourKst}시에 같은 메시지가 새 날짜로 바뀌어요.",
            }, ephemeral: true);
        }
        catch (AttendanceChannelUnavailableException)
        {
            await FollowupAsync("이 채널에 출석부를 보내지 못했어요. 채널 권한을 확인해주세요.", ephemeral: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[출석] 출석부 등록 실패: " + ex.GetType().Name + ": " + ex.Message);
            await FollowupAsync("출석부를 만들지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
        }
    }

    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("출석부해제", "이 서버의 출석부를 끕니다. 출석 기록과 받은 증표는 그대로 남습니다.")]
    public async Task UnregisterAsync()
    {
        if (!await CheckManagerAsync("출석부는")) return;
        await DeferAsync(ephemeral: true);
        try
        {
            var removed = await Program.instance.Attendance.UnregisterAsync(Context.Guild.Id);
            await FollowupAsync(removed
                ? $"출석부를 해제했어요. 출석부 메시지의 버튼은 꺼졌고, 받은 {MollyToken.Label}는 그대로 남아요."
                : "이 서버에는 사용 중인 출석부가 없어요.", ephemeral: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[출석] 출석부 해제 실패: " + ex.GetType().Name + ": " + ex.Message);
            await FollowupAsync("출석부를 해제하지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
        }
    }

    [ComponentInteraction(AttendanceBoardRenderer.CheckInButtonId)]
    public async Task CheckInAsync()
    {
        if (Context.Guild is null || Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("출석은 서버의 출석부에서만 할 수 있어요.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        try
        {
            var displayName = (Context.User as SocketGuildUser)?.DisplayName ?? Context.User.GlobalName ?? Context.User.Username;
            var attendance = Program.instance.Attendance;
            var result = await attendance.CheckInAsync(Context.Guild.Id, component.Message.Id, Context.User.Id, displayName);
            await FollowupAsync(Message(result), ephemeral: true);
            if (result.Status == AttendanceClickStatus.CheckedIn) _ = attendance.RequestRefresh(Context.Guild.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[출석] 출석 처리 실패: " + ex.GetType().Name + ": " + ex.Message);
            await FollowupAsync("출석을 처리하지 못했어요. 잠시 후 다시 눌러주세요.", ephemeral: true);
        }
    }

    /// <summary>출석 버튼 응답. 테스트에서 문구를 검사할 수 있도록 공개한다.</summary>
    public static string Message(AttendanceClickResult result) => result.Status switch
    {
        AttendanceClickStatus.CheckedIn => $"출석했습니다! {MollyToken.Named(result.RewardAmount)}를 받았습니다.\n-# 가방: {MollyToken.Amount(result.Balance)}",
        AttendanceClickStatus.AlreadyCheckedIn => $"오늘은 이미 출석했습니다. 다음 출석은 오전 {AttendanceClock.ResetHourKst}시부터 가능합니다.",
        _ => "지금 사용하지 않는 출석부예요. 이 서버의 새 출석부에서 출석해주세요.",
    };

    private async Task<bool> CheckManagerAsync(string subject)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("DM에서는 사용할 수 없어요.", ephemeral: true);
            return false;
        }
        if (Context.User is not SocketGuildUser { GuildPermissions.ManageGuild: true })
        {
            await RespondAsync(subject + " 서버 관리 권한이 있는 운영자만 관리할 수 있어요.", ephemeral: true);
            return false;
        }
        return true;
    }
}
