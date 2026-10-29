using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Molly.Status;

namespace DiscordBot_Molly.Commands;

/// <summary>
/// 알림이 오지 않을 때 원인을 나눠 보는 운영자용 점검. 각 작업이 남긴 상태와 이 서버의 설정만 읽고, 공식 홈페이지·시세 API는 새로 호출하지 않는다.
/// 설정 변경·테스트 전송·재수집은 하지 않는다.
/// </summary>
public sealed class MollyStatusCommand : InteractionModuleBase<SocketInteractionContext>
{
    public const string DeniedMessage = "몰리 상태는 서버 관리 권한이 있는 운영자만 확인할 수 있어요.";

    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("몰리상태", "공식 소식·이벤트 마감·시세 알림의 수집·전송 상태와 이 서버의 알림 설정을 점검합니다. (운영자)")]
    public async Task StatusAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("DM에서는 사용할 수 없어요. 상태를 확인할 서버 채널에서 실행해주세요.", ephemeral: true);
            return;
        }
        if (!AlertCommandAccess.IsManager(Context.User))
        {
            await RespondAsync(DeniedMessage, ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        StatusReport report;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            report = MollyStatusReport.Build(await Program.instance.Status.GatherAsync(Context.Guild.Id, CheckChannelAsync, cts.Token));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[몰리상태] 상태 조회 실패: {ex.GetType().Name}: {ex.Message}");
            await FollowupAsync("상태를 확인하지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
            return;
        }

        var color = report.Summary switch
        {
            StatusSummary.Ok => Color.Green,
            StatusSummary.Preparing => Color.Blue,
            _ => Color.Orange,
        };
        for (var i = 0; i < report.Pages.Count; i++)
        {
            var embed = new EmbedBuilder()
                .WithTitle(report.Pages.Count == 1 ? report.Title : $"{report.Title} ({i + 1}/{report.Pages.Count})")
                .WithDescription(report.Pages[i])
                .WithColor(color);
            if (i == report.Pages.Count - 1)
                embed.WithFooter("설정 정상은 채널·권한 확인 결과이며 실제 전송 성공을 뜻하지 않아요. 봇이 꺼져 있으면 이 명령도 응답하지 않아요.");
            await FollowupAsync(embed: embed.Build(), ephemeral: true, allowedMentions: AllowedMentions.None);
        }
    }

    // 캐시(Guilds 인텐트로 받은 채널 목록)를 먼저 보고, 캐시에 없는 채널(보관된 스레드 등)만 한 번 직접 확인한다.
    private async Task<ChannelCheck> CheckChannelAsync(ulong channelId, bool needsAttachFiles)
    {
        var guild = Context.Guild;
        IGuildChannel? channel = guild.GetChannel(channelId);
        if (channel is null)
        {
            try { channel = await Context.Client.Rest.GetChannelAsync(channelId) as IGuildChannel; }
            catch (HttpException) { channel = null; }
            if (channel is null || channel.GuildId != guild.Id) return ChannelCheck.ChannelUnavailable;
        }
        if (channel is not IMessageChannel) return ChannelCheck.NotMessageChannel;

        var me = guild.CurrentUser;
        if (channel is IThreadChannel)
        {
            // 스레드 권한은 부모 채널 권한을 따른다.
            var parentId = channel switch
            {
                SocketThreadChannel socket => socket.ParentChannel?.Id,
                Discord.Rest.RestThreadChannel rest => rest.ParentChannelId,
                _ => null,
            };
            if (parentId is not { } id || guild.GetChannel(id) is not { } parent) return ChannelCheck.ChannelUnavailable;
            return AlertChannelRequirements.Check(me.GetPermissions(parent), needsAttachFiles, isThread: true);
        }
        return AlertChannelRequirements.Check(me.GetPermissions(channel), needsAttachFiles, isThread: false);
    }
}
