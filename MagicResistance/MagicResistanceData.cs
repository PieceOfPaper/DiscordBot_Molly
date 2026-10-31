namespace Molly.MagicResistance;

/// <summary>마도저항을 확인할 콘텐츠 한 단계. 필요 마도저항은 입장 조건, 마도 압력은 피해 증감 계산 기준이다.</summary>
public sealed record MagicPressureContent(string Name, int RequiredResistance, int Pressure);

/// <summary>게임 데이터 EquipmentArcaneResistance의 부위 묶음(무기·방어구·목걸이·반지).</summary>
public enum ArcaneEquipmentPart { Weapon, Armor, Necklace, Ring }

/// <summary>장비 하나의 마도저항 범위. Initial은 최초 수치, Max는 감정·성능주입으로 오를 수 있는 최대 수치다.</summary>
public sealed record EquipmentResistanceRange(int Initial, int Max);

/// <summary>
/// 게임 데이터 저장소에서 만든 마도저항 기준값(<see cref="UnpackMagicResistanceDataset"/>).
/// </summary>
/// <param name="AbyssSeason">지옥 난이도에 마도저항 기준이 있는 가장 최근 어비스 시즌 번호(Abyss_Season_03 → 3).</param>
/// <param name="AbyssHell">그 시즌의 지옥 난이도별 기준값. 지옥1부터 순서대로다.</param>
/// <param name="HaeyeonEquipment">해연 장비 부위별 마도저항 범위.</param>
public sealed record MagicResistanceData(
    int AbyssSeason,
    IReadOnlyList<MagicPressureContent> AbyssHell,
    IReadOnlyDictionary<ArcaneEquipmentPart, EquipmentResistanceRange> HaeyeonEquipment)
{
    /// <summary>어비스 시즌의 표시 이름. 게임 데이터에 시즌 이름이 없어 알려진 시즌만 이름을 붙인다.</summary>
    public string AbyssName => AbyssSeason switch
    {
        3 => "룬다 어비스",
        _ => $"어비스 시즌 {AbyssSeason}",
    };
}
