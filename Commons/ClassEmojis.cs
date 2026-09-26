using Discord;

/// <summary>
/// 클래스 배지(assets/class_emojis/{클래스 ID}.png)를 봇 전용 이모지(Application Emoji)로 등록하고 텍스트 속 표기를 만든다.
/// 이모지 이름은 <c>class_{클래스 ID}</c>이다. 이미 같은 이름이 있으면 다시 올리지 않으므로 그림을 바꿀 때는
/// 개발자 포털의 봇 앱 Emojis에서 기존 이모지를 지운 뒤 봇을 다시 시작한다.
/// </summary>
public static class ClassEmojis
{
    public const string NamePrefix = "class_";

    private static IReadOnlyDictionary<string, Emote> s_Emotes = new Dictionary<string, Emote>(StringComparer.Ordinal);

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "assets", "class_emojis");

    public static string EmojiName(string classId) => NamePrefix + classId;

    /// <summary>등록된 이모지 수. 로그·테스트 확인용.</summary>
    public static int Count => Volatile.Read(ref s_Emotes).Count;

    /// <summary>
    /// 클래스 이름 앞에 이모지를 붙인다(예: <c>&lt;:class_bard:123&gt;음유시인</c>).
    /// 등록 전이거나 등록에 실패한 클래스는 이름만 돌려준다.
    /// </summary>
    public static string Label(string classId, string className)
        => Volatile.Read(ref s_Emotes).TryGetValue(classId, out var emote) ? emote + className : className;

    public static Task SyncAsync(IDiscordClient client, Action<string>? log = null)
        => SyncAsync(() => client.GetApplicationEmotesAsync(), (name, image) => client.CreateApplicationEmoteAsync(name, image), log);

    /// <summary>
    /// 앱 이모지 목록을 읽어 없는 클래스 배지만 올린다. 한 클래스의 등록 실패가 다른 클래스 등록을 막지 않는다.
    /// 테스트에서 Discord 호출을 대신할 수 있도록 목록 조회·생성을 주입받는다.
    /// </summary>
    public static async Task SyncAsync(
        Func<Task<IReadOnlyCollection<Emote>>> listEmotes,
        Func<string, Image, Task<Emote>> createEmote,
        Action<string>? log = null)
    {
        void Log(string message) => (log ?? Console.WriteLine).Invoke("[클래스 이모지] " + message);

        var existing = new Dictionary<string, Emote>(StringComparer.Ordinal);
        foreach (var emote in await listEmotes())
            if (emote.Name.StartsWith(NamePrefix, StringComparison.Ordinal))
                existing.TryAdd(emote.Name, emote);

        var emotes = new Dictionary<string, Emote>(StringComparer.Ordinal);
        var created = 0;
        foreach (var classId in ClassIcons.ClassIds)
        {
            var name = EmojiName(classId);
            if (existing.TryGetValue(name, out var found))
            {
                emotes[classId] = found;
                continue;
            }

            var path = Path.Combine(DirectoryPath, classId + ".png");
            if (!File.Exists(path))
            {
                Log($"{name} 배지 파일이 없어 등록하지 못했습니다.");
                continue;
            }

            try
            {
                using var image = new Image(path);
                emotes[classId] = await createEmote(name, image);
                created++;
            }
            catch (Exception ex)
            {
                Log($"{name} 등록 실패: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Volatile.Write(ref s_Emotes, emotes);
        Log($"{emotes.Count}/{ClassIcons.ClassIds.Count}개 사용 가능(새로 등록 {created}개)");
    }
}
