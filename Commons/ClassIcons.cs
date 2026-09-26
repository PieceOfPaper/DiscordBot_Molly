using Discord;

/// <summary>
/// 마비노기 모바일 클래스 아이콘(assets/class_icons/{클래스 ID}.png)을 찾는다.
/// 파일명은 배틀 `클래스` 시트의 ID와 같다. 원본은 나무위키 클래스 문서의 흰색·투명 배경 아이콘(321×321, 암흑술사만 410×410)이라
/// 어두운 배경(임베드 썸네일, 작성자 아이콘 등)에 쓰는 것을 전제로 한다.
/// </summary>
public static class ClassIcons
{
    /// <summary>아이콘 파일이 준비된 클래스 ID. 시트에 클래스가 추가되면 파일과 함께 여기에도 추가한다.</summary>
    public static IReadOnlyList<string> ClassIds { get; } =
    [
        "warrior", "greatsword_warrior", "swordsman", "knight",
        "archer", "crossbowman", "longbowman",
        "mage", "fire_mage", "ice_mage", "lightning_mage",
        "healer", "priest", "monk", "dark_mage",
        "bard", "dancer", "musician",
        "thief", "fighter", "dual_blade",
    ];

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "assets", "class_icons");

    /// <summary>첨부 파일 이름. 임베드에서는 <see cref="AttachmentUrl"/>로 참조한다.</summary>
    public static string FileName(string classId) => classId + ".png";

    /// <summary>임베드 이미지·썸네일·작성자 아이콘 URL로 쓰는 attachment:// 주소.</summary>
    public static string AttachmentUrl(string classId) => "attachment://" + FileName(classId);

    /// <summary>알려진 클래스 ID이고 파일이 있으면 절대 경로, 아니면 null. 목록 밖 ID는 경로 조작을 막기 위해 거부한다.</summary>
    public static string? GetPath(string classId)
    {
        if (!ClassIds.Contains(classId, StringComparer.Ordinal)) return null;
        var path = Path.Combine(DirectoryPath, FileName(classId));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 응답에 붙일 첨부 파일을 만든다. 아이콘이 없으면 null이므로 아이콘 없이 응답하면 된다.
    /// 예: <c>embed.WithThumbnailUrl(ClassIcons.AttachmentUrl(id))</c> 후 <c>FollowupWithFilesAsync([attachment], embed: ...)</c>.
    /// 반환값은 파일 스트림을 열므로 전송 후 Dispose한다.
    /// </summary>
    public static FileAttachment? CreateAttachment(string classId)
        => GetPath(classId) is { } path ? new FileAttachment(path, FileName(classId)) : null;
}
