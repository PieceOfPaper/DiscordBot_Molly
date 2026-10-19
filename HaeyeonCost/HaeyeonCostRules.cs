namespace Molly.HaeyeonCost;

/// <summary>해연 장비 슬롯이자 평균 단가 그룹. 순서는 출력 순서와 계산의 동률 처리 순서입니다.</summary>
public enum HaeyeonSlot { Weapon, Helmet, Top, Bottom, Gloves, Shoes, Necklace, Ring }

/// <summary>
/// /해연비용계산의 게임 수치와 계산 가정. 수치·가정이 바뀌면 이 파일만 고칩니다.
/// 근거: 2026-10-19 기능 요청서의 장비 성장표(공식 공지 원문은 미확인). 자세한 내용은 docs/haeyeon-cost.md.
/// - 10성 룬 10개 저항 합계 3,000, 해연 최초 수치 장비 9개(무기·방어구 5·목걸이·반지 2) 4,200 → 초기 7,200, 최대 10,000
/// - 무기는 감정마다 +28(840→1,400), 나머지 부위는 +14(420→700). 성능주입은 높은 수치로 덮어씀
/// - 반지 두 슬롯은 감정 진행도를 공유하고, 새로 얻은 반지를 낮은 쪽에 적용함
/// </summary>
public static class HaeyeonCostRules
{
    // 모든 저항 변화는 이 단위의 배수입니다(무기 +28 = 2단위).
    public const int Step = 14;
    public const int RuneResistance = 3_000;
    public const int InitialResistance = 7_200;
    public const int MaxResistance = 10_000;

    // 지원하는 입력 범위(현재·목표 모두)
    public const int MinInput = InitialResistance;
    public const int MaxInput = MaxResistance;

    // 슬롯별 최초 장비 저항(반지는 두 개 합계)과 초기 이후 최대 추가 구매·감정 수
    public static int InitialEquipment(HaeyeonSlot slot) => slot switch
    {
        HaeyeonSlot.Weapon => 840,
        HaeyeonSlot.Ring => 840,
        _ => 420,
    };

    public static int MaxCount(HaeyeonSlot slot) => slot == HaeyeonSlot.Ring ? 21 : 20;

    /// <summary>초기 상태에서 추가 구매·감정 count개를 적용했을 때의 저항 상승량.</summary>
    public static int Gain(HaeyeonSlot slot, int count)
    {
        if (count < 0 || count > MaxCount(slot)) throw new ArgumentOutOfRangeException(nameof(count));
        return slot switch
        {
            HaeyeonSlot.Weapon => 28 * count,
            // q번째 새 반지는 420+14q이고 낮은 쪽을 덮어쓰므로, 두 반지는 (420+14(q−1)) / (420+14q)가 됩니다(각각 700 상한).
            HaeyeonSlot.Ring => Step * (Math.Min(count, 20) + Math.Min(Math.Max(count - 1, 0), 20)),
            _ => Step * count,
        };
    }

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

    // 무기는 제작 시트 분류가 '무기'인 해연 완제품 전체. 방어구·장신구는 시트 분류만으로 부위를 알 수 없어 이름을 명시적으로 매핑합니다.
    // 시트에 새 방어구·장신구가 생기면 여기에 추가해야 평균에 들어갑니다(매핑 없는 품목은 평균에서 빠집니다).
    public const string WeaponCategory = "무기";

    public static readonly IReadOnlyDictionary<string, (string Category, HaeyeonSlot Slot)> ExplicitSlots =
        new Dictionary<string, (string, HaeyeonSlot)>(StringComparer.Ordinal)
        {
            ["해연의 비늘 갑옷 투구ZZ"] = ("방어구", HaeyeonSlot.Helmet),
            ["해연의 비늘 갑옷 상의ZZ"] = ("방어구", HaeyeonSlot.Top),
            ["해연의 비늘 갑옷 하의ZZ"] = ("방어구", HaeyeonSlot.Bottom),
            ["해연의 비늘 갑옷 장갑ZZ"] = ("방어구", HaeyeonSlot.Gloves),
            ["해연의 비늘 갑옷 신발ZZ"] = ("방어구", HaeyeonSlot.Shoes),
            ["해연의 가죽 갑옷 투구ZZ"] = ("방어구", HaeyeonSlot.Helmet),
            ["해연의 가죽 갑옷 상의ZZ"] = ("방어구", HaeyeonSlot.Top),
            ["해연의 가죽 갑옷 하의ZZ"] = ("방어구", HaeyeonSlot.Bottom),
            ["해연의 가죽 갑옷 장갑ZZ"] = ("방어구", HaeyeonSlot.Gloves),
            ["해연의 가죽 갑옷 신발ZZ"] = ("방어구", HaeyeonSlot.Shoes),
            ["해연의 전투복 투구ZZ"] = ("방어구", HaeyeonSlot.Helmet),
            ["해연의 전투복 상의ZZ"] = ("방어구", HaeyeonSlot.Top),
            ["해연의 전투복 하의ZZ"] = ("방어구", HaeyeonSlot.Bottom),
            ["해연의 전투복 장갑ZZ"] = ("방어구", HaeyeonSlot.Gloves),
            ["해연의 전투복 신발ZZ"] = ("방어구", HaeyeonSlot.Shoes),
            ["해연의 페리도트 네크리스ZZ"] = ("장신구", HaeyeonSlot.Necklace),
            ["해연의 페리도트 링ZZ"] = ("장신구", HaeyeonSlot.Ring),
        };
}
