using Discord;

namespace DiscordBot_Molly.Commands;

/// <summary>명령을 실행한 일반 텍스트 채널에서 짧은 게임용 공개 스레드를 만듭니다.</summary>
internal static class GameThreads
{
    public static async Task<IThreadChannel> CreateAsync(IMessageChannel channel, string name, CancellationToken ct = default)
    {
        if (channel is IThreadChannel)
            throw new ArgumentException("게임은 스레드 안이 아닌 서버의 일반 텍스트 채널에서 시작해주세요.");
        if (channel is not ITextChannel parent)
            throw new ArgumentException("게임은 서버의 일반 텍스트 채널에서만 시작할 수 있어요.");

        return await parent.CreateThreadAsync(name, ThreadType.PublicThread, ThreadArchiveDuration.OneDay,
            options: new RequestOptions { CancelToken = ct });
    }

}
