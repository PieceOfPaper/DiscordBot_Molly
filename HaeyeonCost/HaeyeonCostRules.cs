using Molly.MagicResistance;

namespace Molly.HaeyeonCost;

/// <summary>해연 장비 슬롯이자 평균 단가 그룹. 순서는 출력 순서와 계산의 동률 처리 순서입니다.</summary>
public enum HaeyeonSlot { Weapon, Helmet, Top, Bottom, Gloves, Shoes, Necklace, Ring }

/// <summary>
/// /해연비용계산의 계산 가정과 부위 매핑. 자세한 내용은 docs/haeyeon-cost.md.
/// 장비 부위별 최초·최대 수치는 게임 데이터(EquipmentArcaneResistance)에서 받아 <see cref="HaeyeonGrowth"/>로 계산하고,
/// 게임 데이터에 없는 값(룬 저항, 감정마다 오르는 양)만 여기 둡니다. 근거는 2026-10-19 기능 요청서의 장비 성장표(공식 공지 원문은 미확인).
/// - 10성 룬 10개 저항 합계 3,000
/// - 무기는 감정마다 +28, 나머지 부위는 +14. 성능주입은 높은 수치로 덮어씀
/// - 반지 두 슬롯은 감정 진행도를 공유하고, 새로 얻은 반지를 낮은 쪽에 적용함
/// </summary>
public static class HaeyeonCostRules
{
    // 모든 저항 변화는 이 단위의 배수입니다(무기 +28 = 2단위).
    public const int Step = 14;
    public const int RuneResistance = 3_000;

    /// <summary>감정(새 장비 적용) 한 번에 오르는 양.</summary>
    public static int Increment(HaeyeonSlot slot) => slot == HaeyeonSlot.Weapon ? 28 : Step;

    /// <summary>게임 데이터의 마도저항 부위 묶음.</summary>
    public static ArcaneEquipmentPart PartOf(HaeyeonSlot slot) => slot switch
    {
        HaeyeonSlot.Weapon => ArcaneEquipmentPart.Weapon,
        HaeyeonSlot.Necklace => ArcaneEquipmentPart.Necklace,
        HaeyeonSlot.Ring => ArcaneEquipmentPart.Ring,
        _ => ArcaneEquipmentPart.Armor,
    };

    public static string Name(HaeyeonSlot slot) => slot switch
    {
        HaeyeonSlot.Weapon => "무기",
        HaeyeonSlot.Helmet => "투구",
        HaeyeonSlot.Top => "상의",
        HaeyeonSlot.Bottom => "하의",
        HaeyeonSlot.Gloves => "장갑",
        HaeyeonSlot.Shoes => "신발",
        HaeyeonSlot.Necklace => "목걸이",
        _ => "반지",
    };

    /// <summary>
    /// 게임 데이터 장비 부위(Equipment.EquipmentType, 제작 레시피의 Slot) → 해연 슬롯. 장비가 아니거나 다른 부위(엠블럼 등)면 null입니다.
    /// 무기는 종류와 상관없이 하나의 그룹이고, 방어구는 재질(중갑·경갑·천옷)과 상관없이 부위별 그룹입니다.
    /// </summary>
    public static HaeyeonSlot? SlotOf(string equipmentType) => equipmentType switch
    {
        "Weapon" => HaeyeonSlot.Weapon,
        "Hat" => HaeyeonSlot.Helmet,
        "Top" => HaeyeonSlot.Top,
        "Bottom" => HaeyeonSlot.Bottom,
        "Gloves" => HaeyeonSlot.Gloves,
        "Shoes" => HaeyeonSlot.Shoes,
        "Accessory" => HaeyeonSlot.Necklace,
        "Ring" => HaeyeonSlot.Ring,
        _ => null,
    };
}

/// <summary>
/// 게임 데이터의 해연 장비 마도저항 범위로 만든 성장 규칙. 범위가 감정 단위와 맞지 않으면 InvalidDataException을 던집니다.
/// - 초기 총 저항 = 룬 + 슬롯별 최초 수치(반지는 두 개), 최대 총 저항 = 룬 + 슬롯별 최대 수치
/// - 무기·방어구 부위·목걸이의 최대 추가 수 = (최대 − 최초) / 감정 단위. 반지는 두 슬롯이 공유하므로 그보다 1개 많음
/// </summary>
public sealed class HaeyeonGrowth
{
    private readonly IReadOnlyDictionary<HaeyeonSlot, EquipmentResistanceRange> m_Ranges;
    private readonly IReadOnlyDictionary<HaeyeonSlot, int> m_Steps;

    public HaeyeonGrowth(IReadOnlyDictionary<ArcaneEquipmentPart, EquipmentResistanceRange> equipment)
    {
        var ranges = new Dictionary<HaeyeonSlot, EquipmentResistanceRange>();
        var steps = new Dictionary<HaeyeonSlot, int>();
        foreach (var slot in Enum.GetValues<HaeyeonSlot>())
        {
            if (!equipment.TryGetValue(HaeyeonCostRules.PartOf(slot), out var range))
                throw new InvalidDataException($"{HaeyeonCostRules.Name(slot)}의 해연 마도저항 범위가 없습니다.");
            var increment = HaeyeonCostRules.Increment(slot);
            if (range.Initial % HaeyeonCostRules.Step != 0 || (range.Max - range.Initial) % increment != 0 || range.Max < range.Initial)
                throw new InvalidDataException($"{HaeyeonCostRules.Name(slot)}의 해연 마도저항 범위({range.Initial}~{range.Max})가 감정 단위 {increment}와 맞지 않습니다.");
            ranges[slot] = range;
            steps[slot] = (range.Max - range.Initial) / increment;
        }
        m_Ranges = ranges;
        m_Steps = steps;
        InitialResistance = HaeyeonCostRules.RuneResistance + Enum.GetValues<HaeyeonSlot>().Sum(InitialEquipment);
        MaxResistance = HaeyeonCostRules.RuneResistance + Enum.GetValues<HaeyeonSlot>().Sum(x => InitialEquipment(x) + Gain(x, MaxCount(x)));
    }

    public static HaeyeonGrowth From(MagicResistanceData data) => new(data.HaeyeonEquipment);

    public int InitialResistance { get; }
    public int MaxResistance { get; }

    // 지원하는 입력 범위(현재·목표 모두)
    public int MinInput => InitialResistance;
    public int MaxInput => MaxResistance;

    /// <summary>초기~최대 사이의 계산 단위 수.</summary>
    public int MaxUnits => (MaxResistance - InitialResistance) / HaeyeonCostRules.Step;

    /// <summary>슬롯별 최초 장비 저항(반지는 두 개 합계).</summary>
    public int InitialEquipment(HaeyeonSlot slot) => slot == HaeyeonSlot.Ring ? m_Ranges[slot].Initial * 2 : m_Ranges[slot].Initial;

    /// <summary>초기 이후 최대 추가 구매·감정 수.</summary>
    public int MaxCount(HaeyeonSlot slot) => slot == HaeyeonSlot.Ring ? m_Steps[slot] + 1 : m_Steps[slot];

    /// <summary>초기 상태에서 추가 구매·감정 count개를 적용했을 때의 저항 상승량.</summary>
    public int Gain(HaeyeonSlot slot, int count)
    {
        if (count < 0 || count > MaxCount(slot)) throw new ArgumentOutOfRangeException(nameof(count));
        var increment = HaeyeonCostRules.Increment(slot);
        var steps = m_Steps[slot];
        return slot switch
        {
            // q번째 새 반지는 최초+단위×q이고 낮은 쪽을 덮어쓰므로, 두 반지는 (최초+단위(q−1)) / (최초+단위q)가 됩니다(각각 최대 수치 상한).
            HaeyeonSlot.Ring => increment * (Math.Min(count, steps) + Math.Min(Math.Max(count - 1, 0), steps)),
            _ => increment * count,
        };
    }
}
