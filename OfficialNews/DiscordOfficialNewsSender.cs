using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Molly.OfficialNews;

/// <summary>공식 소식 Embed를 Discord 채널로 보낸다. 403·404는 영구 실패, 나머지는 일시 실패로 분류한다. 429는 Discord.Net이 기다렸다 다시 보낸다.</summary>
public sealed class DiscordOfficialNewsSender(DiscordSocketClient client) : IOfficialNewsSender
{
    public bool IsReady => client.ConnectionState == ConnectionState.Connected && client.LoginState == LoginState.LoggedIn;

    public async Task<ulong> SendAsync(ulong channelId, NewsMessage news, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        IChannel? channel;
        try { channel = await client.GetChannelAsync(channelId, options).ConfigureAwait(false); }
        catch (HttpException ex) { throw Classify(ex); }
        if (channel is null) throw new NewsDeliveryException("channel_not_found", true, "채널을 찾을 수 없습니다(삭제되었거나 봇이 서버에 없음).");
        if (channel is not IMessageChannel messageChannel) throw new NewsDeliveryException("not_message_channel", true, "메시지를 보낼 수 없는 채널입니다.");
        try
        {
            // 공식 제목에 @everyone 같은 문구가 있어도 멘션이 울리지 않게 한다.
            var message = news.AttachmentPath is { } path
                ? await messageChannel.SendFileAsync(path, embed: news.Embed, allowedMentions: AllowedMentions.None, options: options).ConfigureAwait(false)
                : await messageChannel.SendMessageAsync(embed: news.Embed, allowedMentions: AllowedMentions.None, options: options).ConfigureAwait(false);
            return message.Id;
        }
        catch (HttpException ex) { throw Classify(ex); }
    }

    private static NewsDeliveryException Classify(HttpException ex)
    {
        var permanent = ex.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
        var code = $"discord_{(int)ex.HttpCode}" + (ex.DiscordCode is { } discordCode ? $"_{(int)discordCode}" : "");
        return new NewsDeliveryException(code, permanent, ex.Message);
    }
}
