using Molly.Battle;

/// <summary>
/// 피해 적중/빗나감 분기를 검사하려고 상대에게 거는 테스트 전용 회피 상태(경갑 제작과 같은 회피확률 효과유형). 실제 시트에는 없다.
/// 전투시작 패시브로 영구 상태를 걸어 모든 타격마다 회피를 판정한다.
/// </summary>
internal static class TestEvasion
{
    public const string PassiveId = "test_evasion";

    public static BattleStatus Status(double chance) => new("test_evasion", "테스트 회피", "회피확률", chance, "테스트 전용 회피 확률");

    public static readonly BattlePassive Passive = new(PassiveId, true,
        [new BattleEffect("test_evasion_01", 1, "상태효과", "자신", 0, 1, 1, 0, "test_evasion", 0, null, null, null, null, null, null, null, null, Trigger: "전투시작")]);

    /// <summary>시트 데이터에 테스트 회피 패시브·상태를 더한다. 패시브는 클래스의 PassiveIds에 <see cref="PassiveId"/>를 넣은 쪽만 받는다.</summary>
    public static (IReadOnlyDictionary<string, BattlePassive> Passives, IReadOnlyDictionary<string, BattleStatus> Statuses) Add(BattleDataSnapshot data, double chance = 1)
        => (data.Passives.Append(new(PassiveId, Passive)).ToDictionary(), data.Statuses.Append(new("test_evasion", Status(chance))).ToDictionary());
}
