namespace Molly.MagicResistance;

/// <summary>마도저항을 확인할 콘텐츠 한 단계. 필요 마도저항은 입장 조건, 마도 압력은 피해 증감 계산 기준이다.</summary>
public sealed record MagicPressureContent(string Name, int RequiredResistance, int Pressure);

/// <summary>
/// `/마도저항확인`이 비교하는 콘텐츠별 기준값. 필요 마도저항·마도 압력이 바뀌면 이 목록만 고친다.
/// 출처(2026-10-19 확인, 기획서 docs/magic-resistance.md):
/// - 지옥1: 공식 공지에서 수치를 찾지 못해 나무위키 '마비노기 모바일/어비스/룬다' 표 기준(커뮤니티)
/// - 지옥2: 9/17 업데이트 노트(News/Update/3545062)에서 6,500/7,200 → 6,000/6,600 조정, 2026-09-17 적용
/// - 지옥3: 9/17 업데이트 노트(News/Update/3545062), 2026-09-21 개방
/// - 지옥4: 10/15 업데이트 노트(News/Update/3563175), 2026-10-19 개방
/// </summary>
public static class MagicResistanceCatalog
{
    public const string AppliedOn = "2026-10-19";

    public static readonly IReadOnlyList<MagicPressureContent> RundaAbyss =
    [
        new("지옥1", 3_500, 4_400),
        new("지옥2", 6_000, 6_600),
        new("지옥3", 7_200, 7_800),
        new("지옥4", 8_500, 9_000),
    ];
}
