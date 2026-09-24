using Discord;
using Discord.WebSocket;

namespace Molly.HaeyeonMarket;

public sealed class DiscordHaeyeonAlertSender(DiscordSocketClient client) : IHaeyeonAlertSender
{
    public async Task SendAsync(HaeyeonMonitorChannel channel, IReadOnlyList<Embed> embeds, CancellationToken ct)
    {
        var found = await client.GetChannelAsync(channel.ChannelId, new RequestOptions { CancelToken = ct }).ConfigureAwait(false);
        if (found is not IMessageChannel messageChannel)
            throw new InvalidOperationException(found is null ? "채널을 찾을 수 없습니다(삭제되었거나 권한 없음)." : "메시지를 보낼 수 없는 채널입니다.");
        // 한 메시지의 Embed 합계 글자 수 제한이 있어 Embed마다 따로 보냅니다.
        foreach (var embed in embeds)
            await messageChannel.SendMessageAsync(embed: embed, options: new RequestOptions { CancelToken = ct }).ConfigureAwait(false);
    }
}
