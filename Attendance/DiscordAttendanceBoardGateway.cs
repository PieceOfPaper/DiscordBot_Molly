using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Molly.Attendance;

public sealed class DiscordAttendanceBoardGateway(DiscordSocketClient client) : IAttendanceBoardGateway
{
    public async Task<ulong> SendAsync(ulong channelId, AttendanceBoard board, CancellationToken ct)
    {
        var channel = await GetChannelAsync(channelId, ct).ConfigureAwait(false);
        try
        {
            var message = await channel.SendMessageAsync(embed: AttendanceBoardRenderer.BuildEmbed(board),
                components: AttendanceBoardRenderer.BuildComponents(board), options: Options(ct)).ConfigureAwait(false);
            return message.Id;
        }
        catch (HttpException ex) when (ex.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new AttendanceChannelUnavailableException($"메시지를 보낼 수 없습니다({(int)ex.HttpCode}).");
        }
    }

    public async Task<bool> EditAsync(ulong channelId, ulong messageId, AttendanceBoard board, CancellationToken ct)
    {
        var channel = await GetChannelAsync(channelId, ct).ConfigureAwait(false);
        IMessage? found;
        try { found = await channel.GetMessageAsync(messageId, options: Options(ct)).ConfigureAwait(false); }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound) { return false; }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden)
        {
            throw new AttendanceChannelUnavailableException("출석부 메시지를 읽을 수 없습니다(403).");
        }
        if (found is not IUserMessage message) return false;
        try
        {
            await message.ModifyAsync(m =>
            {
                m.Content = "";
                m.Embed = AttendanceBoardRenderer.BuildEmbed(board);
                m.Components = AttendanceBoardRenderer.BuildComponents(board);
            }, Options(ct)).ConfigureAwait(false);
            return true;
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound) { return false; }
    }

    private async Task<IMessageChannel> GetChannelAsync(ulong channelId, CancellationToken ct)
    {
        IChannel? channel;
        try { channel = await client.GetChannelAsync(channelId, Options(ct)).ConfigureAwait(false); }
        catch (HttpException ex) when (ex.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { channel = null; }
        return channel as IMessageChannel
            ?? throw new AttendanceChannelUnavailableException(channel is null ? "채널을 찾을 수 없습니다(삭제되었거나 권한 없음)." : "메시지를 보낼 수 없는 채널입니다.");
    }

    private static RequestOptions Options(CancellationToken ct) => new() { CancelToken = ct };
}
