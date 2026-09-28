using Discord;

namespace Molly.Currency;

/// <summary>
/// 마물 퇴치 증표의 이름·수량 표기와 전용 봇 이모지(Application Emoji).
/// 몰리의 문장에서는 <see cref="Named"/>(풀네임), 이름을 생략하는 버튼·배율표·간단 잔액에서는 <see cref="Amount"/>(이모지+수량)를 쓴다.
/// 이모지 그림은 assets/currency_emojis/molly_token.png이며, 파일이 없거나 등록 전이면 풀네임 표기로 대신한다.
/// 그림을 바꿀 때는 개발자 포털의 봇 앱 Emojis에서 기존 molly_token을 지운 뒤 봇을 다시 시작한다.
/// </summary>
public static class MollyToken
{
    public const string Name = "마물 퇴치 증표";
    public const string EmojiName = "molly_token";

    private static Emote? s_Emote;

    public static string EmojiPath => Path.Combine(AppContext.BaseDirectory, "assets", "currency_emojis", EmojiName + ".png");

    /// <summary>등록된 이모지 표기(<c>&lt;:molly_token:ID&gt;</c>). 없으면 null.</summary>
    public static string? Emoji => Volatile.Read(ref s_Emote)?.ToString();

    /// <summary>문장용 풀네임 표기. 예: <c>마물 퇴치 증표 10개</c>.</summary>
    public static string Named(long amount) => $"{Name} {Count(amount)}";

    /// <summary>이름을 생략한 수량 표기. 예: <c>&lt;:molly_token:ID&gt;10개</c>. 이모지가 없으면 <see cref="Named"/>와 같다.</summary>
    public static string Amount(long amount) => Emoji is { } emoji ? emoji + Count(amount) : Named(amount);

    private static string Count(long amount) => amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "개";

    public static Task SyncEmojiAsync(IDiscordClient client, Action<string>? log = null)
        => SyncEmojiAsync(() => client.GetApplicationEmotesAsync(), (name, image) => client.CreateApplicationEmoteAsync(name, image), EmojiPath, log);

    /// <summary>
    /// 봇 이모지 목록에 molly_token이 있으면 그대로 쓰고, 없으면 그림 파일이 있을 때만 올린다.
    /// 테스트에서 Discord 호출을 대신할 수 있도록 목록 조회·생성을 주입받는다.
    /// </summary>
    public static async Task SyncEmojiAsync(
        Func<Task<IReadOnlyCollection<Emote>>> listEmotes,
        Func<string, Image, Task<Emote>> createEmote,
        string imagePath,
        Action<string>? log = null)
    {
        void Log(string message) => (log ?? Console.WriteLine).Invoke("[증표 이모지] " + message);

        if ((await listEmotes()).FirstOrDefault(x => x.Name == EmojiName) is { } found)
        {
            Volatile.Write(ref s_Emote, found);
            Log("등록된 이모지 사용");
            return;
        }
        if (!File.Exists(imagePath))
        {
            Volatile.Write(ref s_Emote, null);
            Log("이모지 그림이 없어 풀네임으로 표기합니다.");
            return;
        }
        try
        {
            using var image = new Image(imagePath);
            Volatile.Write(ref s_Emote, await createEmote(EmojiName, image));
            Log("새로 등록");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref s_Emote, null);
            Log($"등록 실패, 풀네임으로 표기합니다: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
