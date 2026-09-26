using Discord;

/// <summary>클래스 아이콘·이모지 배지가 배포 에셋으로 복사되고, 클래스 ID로만 찾아지며, 봇 이모지 동기화가 없는 것만 올리는지 검사한다.</summary>
internal static class ClassIconTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static async Task RunAsync()
    {
        Assert(ClassIcons.ClassIds.Count == 21 && ClassIcons.ClassIds.Distinct(StringComparer.Ordinal).Count() == 21, "클래스 아이콘 ID 21개·중복 없음");
        foreach (var id in ClassIcons.ClassIds)
        {
            var path = ClassIcons.GetPath(id);
            Assert(path is not null && File.ReadAllBytes(path).AsSpan().StartsWith(PngSignature), "클래스 아이콘 PNG 존재: " + id);
        }
        Assert(ClassIcons.AttachmentUrl("warrior") == "attachment://warrior.png", "클래스 아이콘 첨부 주소");
        Assert(ClassIcons.GetPath("unknown") is null && ClassIcons.GetPath("../hollymolly") is null && ClassIcons.GetPath("Warrior") is null
            && ClassIcons.CreateAttachment("unknown") is null, "목록 밖 클래스 ID는 아이콘 없음");
        using (var attachment = ClassIcons.CreateAttachment("healer") ?? throw new Exception("힐러 아이콘 첨부 생성 실패"))
            Assert(attachment.FileName == "healer.png", "클래스 아이콘 첨부 파일 이름");

        foreach (var id in ClassIcons.ClassIds)
        {
            var path = Path.Combine(ClassEmojis.DirectoryPath, id + ".png");
            Assert(File.Exists(path) && File.ReadAllBytes(path).AsSpan().StartsWith(PngSignature) && new FileInfo(path).Length < 256 * 1024,
                "클래스 이모지 배지 PNG 존재·256KB 미만: " + id);
            Assert(System.Text.RegularExpressions.Regex.IsMatch(ClassEmojis.EmojiName(id), "^[A-Za-z0-9_]{2,32}$"), "클래스 이모지 이름 규칙: " + id);
        }

        Assert(ClassEmojis.Label("bard", "음유시인") == "음유시인", "이모지 등록 전에는 클래스 이름만 표기");
        var created = new List<string>();
        var logs = new List<string>();
        await ClassEmojis.SyncAsync(
            () => Task.FromResult<IReadOnlyCollection<Emote>>([new Emote(1, "class_bard", false), new Emote(2, "other_emoji", false)]),
            (name, _) =>
            {
                created.Add(name);
                if (name == "class_thief") throw new InvalidOperationException("업로드 실패");
                return Task.FromResult(new Emote((ulong)(100 + created.Count), name, false));
            },
            logs.Add);
        Assert(!created.Contains("class_bard") && created.Count == ClassIcons.ClassIds.Count - 1, "이미 등록된 클래스 이모지는 다시 올리지 않고 나머지만 등록");
        Assert(ClassEmojis.Label("bard", "음유시인") == "<:class_bard:1>음유시인", "기존 봇 이모지를 클래스 이름 앞에 표기");
        Assert(ClassEmojis.Label("warrior", "전사").StartsWith("<:class_warrior:") && ClassEmojis.Label("warrior", "전사").EndsWith(">전사"), "새로 등록한 봇 이모지를 클래스 이름 앞에 표기");
        Assert(ClassEmojis.Label("thief", "도적") == "도적" && ClassEmojis.Count == ClassIcons.ClassIds.Count - 1 && logs.Any(x => x.Contains("class_thief 등록 실패")),
            "한 클래스 이모지 등록 실패는 이름만 표기하고 다른 클래스는 계속 등록");
        Assert(ClassEmojis.Label("unknown", "미지") == "미지", "모르는 클래스는 이름만 표기");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
