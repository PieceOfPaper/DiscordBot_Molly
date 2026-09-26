/// <summary>클래스 아이콘이 배포 에셋으로 복사되고, 클래스 ID로만 찾아지는지 검사한다.</summary>
internal static class ClassIconTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void Run()
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
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
