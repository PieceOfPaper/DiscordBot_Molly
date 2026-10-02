using Molly.Battle;

/// <summary>
/// 궁수의 순풍·바람결(질주하는 바람)·추진력·호크 샷 약화·다발 사격·작열의 궤적(재사용 대기 초기화·불화살) 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-10-02 배틀 시트에서 궁수 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 궁수 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·배틀스킬AI·돌발 이벤트·생활스킬 시트는 석궁사수 사본을 그대로 쓴다. 궁수는 파생이 없어 배틀스킬파생에는 성립하지 않는 자리표시 행만 둔다.
/// </summary>
internal static class ArcherBattleTests
{
    private static double Variance(double random) => .9 + random * .2;
    private static int Hit(double shownDamage, double random = .9, double outgoing = 1, double incoming = 1, double critical = 1) => (int)Math.Round((shownDamage * .25 - 200 * .5) * outgoing * incoming * Variance(random) * critical);
    // 약점 관통 기대값 +9%. 다단 피해에는 전투 숙련: 쾌속 연타 +5%와 순풍 중첩당 연타 +6%가 더해진다.
    private static double Outgoing(int tailwind = 0, bool multiHit = false, double extra = 0) => 1 + .09 + (multiHit ? .05 + .06 * tailwind : 0) + extra;

    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow);
        TailwindTests(data);
        WindTests(data);
        HawkShotTests(data);
        MultiShotTests(data);
        BlazingTrailTests(data);
    }

    private static void TailwindTests(BattleDataSnapshot data)
    {
        var result = Duel(data, ["arrow_revolver"], maxActions: 3);
        var turns = Turns(result);
        Assert(turns[0].Skill == "애로우 리볼버" && turns[0].Hits == 6 && turns[0].Damage == 6 * Hit(2357, outgoing: Outgoing(1, true)) && turns[0].Resources.Contains("순풍 +1 (현재 1)"),
            "애로우 리볼버는 여섯 발을 쏘고, 순풍은 턴 시작마다 1중첩씩 쌓여 연타 피해를 늘린다");
        Assert(turns[1].Skill == "애로우 리볼버" && turns[1].Damage == 6 * Hit(2357, outgoing: Outgoing(2, true)),
            "애로우 리볼버는 쿨다운 1턴이라 매 행동 다시 쓰고, 순풍 2중첩이면 연타 피해가 더 오른다");

        // 난수 0.1: 상대의 일반 공격이 치명타(22% - 순풍 급소 회피 8%)로 적중해 순풍을 모두 잃는다.
        var critical = Duel(data, ["arrow_revolver"], maxActions: 2, random: .1);
        Assert(critical.Events.Any(x => x.Type == "CriticalHit" && x.Actor == "B") && critical.Events.Any(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail == "순풍 -1 (현재 0)"),
            "치명타를 맞으면(급소 회피 실패) 순풍 중첩을 모두 잃는다");
    }

    private static void WindTests(BattleDataSnapshot data)
    {
        // 바람 25에서 이스케이프 스텝(바람 75)으로 가득 차면 질주하는 바람을 얻고 바람을 모두 쓴다. 다음 사이드 스텝에 추진력 +12.5%가 붙는다.
        var result = Duel(data, ["side_step", "escape_step"], maxActions: 3, initial: [("archer_wind", 25)]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "이스케이프 스텝" && turns[0].Damage == Hit(5584, outgoing: Outgoing(), critical: 1.5)
            && turns[0].Resources.Contains("바람 +75 (현재 100)") && turns[0].Resources.Contains("바람 -100 (현재 0)") && turns[0].Statuses.Contains("질주하는 바람") && turns[0].Statuses.Contains("둔화"),
            "이스케이프 스텝은 반드시 치명타로 적중하고 상대를 둔화시키며, 바람이 가득 차면 질주하는 바람을 얻는다");
        Assert(turns[1].Skill == "사이드 스텝" && turns[1].Hits == 3 && turns[1].Damage == 3 * Hit(3989, outgoing: Outgoing(2, true, .125)) && turns[1].Resources.Contains("바람 +25 (현재 25)"),
            "질주하는 바람 동안에는 추진력으로 주는 피해가 12.5% 늘고, 사이드 스텝은 바람 25를 모은다");
    }

    private static void HawkShotTests(BattleDataSnapshot data)
    {
        // 호크 샷의 약화(받는 피해 +21%)는 상대 턴으로 세어 2턴이라, 궁수의 다음 행동(일반 공격)까지 남는다.
        var weakened = Duel(data, ["hawk_shot"], maxActions: 3);
        var turns = Turns(weakened);
        Assert(turns[0].Skill == "호크 샷" && turns[0].Damage == 2 * Hit(5620, outgoing: Outgoing(1, true)) && turns[0].Statuses.Contains("약화"),
            "호크 샷은 두 번 적중하고 상대를 약화시킨다");
        Assert(turns[1].Skill == "(일반 공격)" && turns[1].Damage == (int)Math.Round(1400 * Outgoing() * 1.21 * Variance(.9)),
            "약화된 상대는 궁수의 다음 공격에 약점으로 맞아 30%(기대값 9% + 21%) 더 받는다");

        // 매그넘 샷으로 브레이크를 건 뒤 호크 샷은 총합 대미지가 두 배(+100%)이고 무방비 120%가 붙는다.
        var broken = Duel(data, ["hawk_shot", "magnum_shot"], maxActions: 3, rules: [("break_gauge_maximum", "1")]);
        var brokenTurns = Turns(broken);
        Assert(brokenTurns[0].Skill == "매그넘 샷" && brokenTurns[0].Damage == Hit(10154, outgoing: Outgoing()) && brokenTurns[0].Broken,
            "매그넘 샷은 브레이크 피해를 준다");
        Assert(brokenTurns[1].Skill == "호크 샷" && brokenTurns[1].Damage == 2 * Hit(5620 * 2, outgoing: Outgoing(2, true), incoming: 1.2),
            "브레이크된 상대에게 호크 샷은 두 배 피해를 준다");
    }

    private static void MultiShotTests(BattleDataSnapshot data)
    {
        // 타격 수 30에서 스킬이 적중하면(+10) 40이 되어 다발 사격을 쏘고 초기화한다. 다발 사격은 항상 약점에 적중한다(원본 × 1.3/1.09로 반영).
        var result = Duel(data, ["magnum_shot"], maxActions: 1, initial: [("archer_multi_count", 30)]);
        var turns = Turns(result);
        Assert(turns[0].Statuses.Contains("다발 사격") && turns[0].Hits == 6
            && turns[0].Damage == Hit(10154, outgoing: Outgoing()) + Hit(8737, outgoing: Outgoing()) + 4 * Hit(4368, outgoing: Outgoing(1, true))
            && result.Events.Any(x => x.Type == "StatusExpired" && x.Actor == "A" && x.Detail == "다발 사격"),
            "타격 40회가 되면 스킬 적중 뒤 다발 사격으로 화살 다섯 발을 더 쏜다");
    }

    private static void BlazingTrailTests(BattleDataSnapshot data)
    {
        // 매그넘 샷으로 게이지 300 → 작열의 궤적이 매그넘 샷 쿨다운을 초기화하고 불화살 3턴 → 바로 매그넘 샷에 불화살 추가 피해 254.
        var result = Duel(data, ["magnum_shot", "blazing_trail"], maxActions: 5, initial: [("ultimate_gauge", 150)]);
        var turns = Turns(result);
        Assert(turns[1].Skill == "작열의 궤적" && turns[1].Damage == Hit(10154, outgoing: Outgoing()) && turns[1].Statuses.Contains("불화살")
            && result.Events.Any(x => x.Type == "CooldownReduced" && x.Actor == "A" && x.Detail == "99"),
            "작열의 궤적은 피해를 주고 모든 스킬의 재사용 대기 시간을 초기화하며 불화살을 얻는다");
        Assert(turns[2].Skill == "매그넘 샷" && result.Events.SkipWhile(x => x.Detail != "작열의 궤적").Any(x => x.Type == "AdditionalDamage" && x.Actor == "A" && x.Amount == 254),
            "불화살 동안에는 재사용 대기가 초기화된 매그넘 샷에 화살마다 추가 피해가 붙는다");
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses, int Hits, bool Broken);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, (string Id, int Value)[]? initial = null, (string Id, string Value)[]? rules = null)
    {
        var ruleMap = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", maxActions.ToString()) }.Concat(rules ?? [])) ruleMap[id] = ruleMap[id] with { Value = value };
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in initial ?? []) resources[id] = resources[id] with { InitialValue = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = ruleMap,
            Classes = new Dictionary<string, BattleClass> { ["archer"] = data.Classes["archer"] with { SkillIds = skills }, ["idle"] = new("idle", "대상", Array.Empty<string>()) },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, LoadedAt = data.LoadedAt
        };
        // 궁수가 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "archer", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
    }

    /// <summary>A의 행동별로 사용 스킬·A가 준 피해 합계·A의 자원 변화·새로 걸린 상태·타격 수·브레이크 발동 여부를 모은다.</summary>
    private static List<Turn> Turns(BattleResult result)
    {
        var turns = new List<Turn>();
        List<BattleEvent>? current = null;
        foreach (var e in result.Events.Append(new BattleEvent("TurnStarted", "")))
        {
            if (e.Type != "TurnStarted") { current?.Add(e); continue; }
            if (current is not null)
                turns.Add(new(current.FirstOrDefault(x => x.Type is "SkillUsed" or "NormalAttackUsed") is { } used ? used.Detail ?? "(일반 공격)" : "(행동 없음)",
                    current.Where(x => x.Type is "DamageDealt" && x.Actor == "A").Sum(x => x.Amount ?? 0),
                    current.Where(x => x.Type == "ResourceChanged" && x.Actor == "A").Select(x => x.Detail ?? "").ToArray(),
                    current.Where(x => x.Type == "StatusApplied").Select(x => x.Detail ?? "").ToArray(),
                    current.Count(x => x.Type == "DamageDealt" && x.Actor == "A"),
                    current.Any(x => x.Type == "BreakActivated")));
            current = e.Actor == "A" ? [] : null;
        }
        return turns;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class FirstThenConstantRandom(double value) : IBattleRandom
    {
        private bool first = true;
        public double NextDouble() { if (!first) return value; first = false; return 0; }
    }

    public static readonly IReadOnlyDictionary<string, string> Sheets = new Dictionary<string, string>(CrossbowBattleTests.Sheets.Where(x => x.Key is "배틀규칙" or "배틀스킬AI" or "배틀돌발이벤트" or "배틀돌발이벤트효과" or "생활스킬" or "배틀생활스킬" or "배틀생활스킬효과"))
    {
        ["클래스"] = """"
ID,이름,계열,설명,스킬1,스킬2,스킬3,스킬4,스킬5,궁극기,패시브1,패시브2,패시브3,패시브4,패시브5,패시브6
archer,궁수,궁수,"활 시위를 빠르게 당겨 원거리의 적을 날렵하게 공격하는 클래스. 공중의 매와 같은 날카로운 조준과 정교하고 경쾌한 움직임으로, 쉴 틈 없이 약점을 노린다. 가벼운 갑옷을 선호하며, 쫓기 힘들 만큼 재빠른 몸놀림으로 적을 공략한다.",arrow_revolver,magnum_shot,side_step,hawk_shot,escape_step,blazing_trail,wind_flow,weak_point_piercing,combat_mastery_swift_archer,multi_shot,propulsion,tailwind
"""",
        ["스킬"] = """"
ID,이름,스킬구분,부모스킬ID,설명,태그,기타
arrow_revolver,애로우 리볼버,일반,,"능숙한 속사로 상대를 압박하는 연발 사격. 여러 발의 화살을 잇달아 쏘아 타겟을 궁지로 몰아넣는다. 질주하는 바람 발동 시, 타겟과 가까운 적에게도 피해를 준다. 기본 공격과 스킬 적중 시, 재사용 대기 시간이 감소한다.","연타, 방해",등급: 에픽; 강화 레벨: +24; 대미지: 3367 × 6; 도탄 화살 대미지: 1발당 2693; 재사용 대기 시간: 7초; 적중 시 재사용 대기 시간 감소: 0.5초; 사거리: 12m
magnum_shot,매그넘 샷,일반,,"다수의 적을 일거에 제압하는 묵직한 일발 사격. 활시위를 힘껏 당겨 전방의 적들을 단숨에 무너뜨리는 화살을 발사한다. 사용 도중, 강화 효과: 질주하는 바람으로 인한 바람 소모 및 바람 소모 감소 시간이 정지한다. 질주하는 바람 발동 시, 캐스팅 시간이 감소한다.","강타, 방해",등급: 고급; 강화 레벨: +8; 대미지: 14505; 브레이크 대미지: 1칸; 캐스팅 시간: 1.2초; 질주하는 바람 시 캐스팅 시간: 0.8초; 재사용 대기 시간: 12초; 사거리: 12m; 범위: 15m
side_step,사이드 스텝,일반,,"적의 공세를 재빨리 피해 화살을 쏘는 회피 사격. 기민한 몸놀림으로 자리를 벗어나며 타겟에게 화살을 연사한다. 질주하는 바람 발동 시, 타겟과 가까운 적에게도 피해를 준다.","연타, 이동",등급: 에픽; 강화 레벨: +24; 대미지: 5698 × 3; 도탄 화살 대미지: 1발당 4558; 바람 획득: 25%; 재사용 대기 시간: 9초; 최대 스택 수: 3; 사거리: 15m
hawk_shot,호크 샷,일반,,"공중으로 도약해 적의 빈틈을 파고드는 저격. 타겟과 주변 적 하나를 약화시키며 브레이크 시, 추가 피해를 준다. 약화된 적은 일정 시간 동안 자신으로부터 모든 공격이 약점으로 적중한다. 질주하는 바람 발동 시, 타겟과 가까운 적에게도 피해를 준다.","강타, 이동, 방해",등급: 에픽; 강화 레벨: +24; 총합 대미지: 8029 × 2; 브레이크 대상 총합 대미지: 16059 × 2; 도탄 화살 대미지: 1발당 6423; 지속 시간: 5초; 바람 획득: 25%; 재사용 대기 시간: 11초; 사거리: 12m
escape_step,이스케이프 스텝,일반,,"신속히 거리를 벌리며 적을 노리는 회피 사격. 빠르게 뒤로 물러나며 화살을 발사해 타겟의 움직임을 방해하고, 치명적인 피해를 준다. 질주하는 바람 발동 시, 타겟과 가까운 적에게도 피해를 준다.","이동, 생존",등급: 고급; 강화 레벨: +8; 대미지: 7977; 초탄 치명타 확률: 100%; 도탄 화살 대미지: 6382; 바람 획득: 75%; 지속 시간: 5초; 이동 속도 감소: 50%; 재사용 대기 시간: 9초; 사거리: 15m
blazing_trail,작열의 궤적,궁극기,,"기회를 노려 점화된 특수 화살로 교체하는 비기. 불화살을 쏘아 타겟에게 피해를 주고, 모든 스킬의 재사용 대기 시간을 초기화한다. 일정 시간 모든 공격이 불화살로 대체되어 추가 피해를 준다. 『바람이 그린 궤적 끝, 불꽃이 피어나리라』","궁극기, 연타, 보조",등급: 고급; 강화 레벨: +8; 엠블럼 장착 필요; 대미지: 14505; 지속 시간: 15초; 불화살 추가 대미지: 1450; 궁극기 비용: 300; 사거리: 12m
"""",
        ["패시브스킬"] = """"
ID,이름,설명,기타
wind_flow,바람결,"강렬한 움직임으로 일으킨 바람을 힘으로 바꾸는 발재간. 직접 이동하거나 이동 스킬 사용 시 바람을 생성한다. 바람이 최대치에 이르면 강화 효과: 질주하는 바람을 얻어, 바람이 완전히 소진될 때까지 이동 속도가 증가하고 모든 공격 스킬이 도탄되며 주변의 적 한 명을 추가로 공격한다. 강화 효과: 질주하는 바람이 활성화되면 일정 시간 동안 바람 소모량이 감소한다.",질주하는 바람 시 바람 소모: 초당 35%; 이동 속도 증가: 25%; 바람 소모 감소 시간: 5초; 바람 소모 감소량: 50%; 애로우 리볼버 도탄 횟수: 1발당 1회; 매그넘 샷 캐스팅 시간: 0.8초; 사이드 스텝 도탄 횟수: 1발당 1회; 호크 샷 도탄 횟수: 1발당 1회; 이스케이프 스텝 도탄 횟수: 1발당 1회
weak_point_piercing,약점 관통,재빠른 조준으로 적의 약점을 노리는 공격. 일정 확률로 약점을 사격하여 더 큰 피해를 입힌다.,발동 확률: 30%; 약점 공격 시 최종 대미지 증가: 30%
combat_mastery_swift_archer,전투 숙련: 쾌속,원거리에서 공격을 수행하는 숙련된 전투 기법. 적에게 주는 연타 피해가 증가한다. 궁수 클래스 레벨이 30에 도달하면 전투 시 클래스 특화 어시스트를 사용할 수 있다.,연타 대미지 증가: 5%; 클래스 특화 어시스트 해금: 궁수 클래스 레벨 30
multi_shot,다발사격,"시위 하나에 다수의 화살을 날려 보내는 테크닉. 기본 공격을 강화하여 부채꼴 범위 내의 적들에게 5발의 화살을 발사한다. 일정 횟수 이상의 공격을 적중시킬 경우, 활성화되며 활성화 이후에 적중시킨 공격 횟수에 따라서 대미지가 증가한다. 다발 사격은 항상 약점에 적중하며, 같은 대상에게 여러 발이 적중할 경우 화살의 피해가 크게 감소한다.","태그: 강타, 연타; 대미지: 10465; 다발 사격 발동을 위한 타격 횟수: 40; 추가 적중 시 대미지: 5232; 공격 적중에 따른 피해 증가: 2%; 최대 피해 증가: 200%"
propulsion,추진력,빠르고 탄력적인 몸놀림을 적극 활용하는 사격 방식. 이동 속도 증가 수치에 비례하여 적에게 주는 피해가 증가한다.,이동 속도 증가 1%당 대미지 증가: 0.5%
tailwind,순풍,"급소를 피하고, 더 정확하게 사격하는 날렵한 몸놀림. 전투 중, 일정 시간마다 급소 회피 확률과 연타 피해가 증가하는 강화 효과: 순풍을 얻는다. 피격 시 급소 회피가 발동하지 않을 경우, 모든 순풍 중첩을 잃는다.",급소 회피 확률 증가: 중첩당 8%; 연타 대미지 증가: 중첩당 6%; 중첩 획득 간격: 5초; 최대 중첩 수: 5회
"""",
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고,궁극기문구
arrow_revolver,TRUE,공격·방해,상대,1,0,65,궁극기 게이지,,150,FALSE,여러 발의 화살을 잇달아 쏘아 상대를 궁지로 몰아넣는다.,{caster}가 능숙한 속사로 {target}에게 화살을 잇달아 쏩니다!,"원본 대미지 3,367×6, 재사용 대기 7초. 기본 공격·스킬 적중 시 재사용 대기 0.5초 감소를 반영해 1턴. 피해 원본 ×0.7. 도탄 화살(질주하는 바람 시 주변 적)은 1:1이라 제외",
magnum_shot,TRUE,공격·방해,상대,2,0,60,궁극기 게이지,,150,FALSE,활시위를 힘껏 당겨 묵직한 화살로 상대를 무너뜨린다.,{caster}가 활시위를 힘껏 당겨 {target}에게 묵직한 화살을 날립니다!,"원본 대미지 14,505, 브레이크 1칸, 재사용 대기 12초→2턴. 피해 원본 ×0.7. 캐스팅 시간·바람 소모 정지·질주하는 바람 시 캐스팅 단축은 턴제에 시전 시간이 없어 제외",
side_step,TRUE,공격,자신·상대,2,0,60,궁극기 게이지,,150,FALSE,재빨리 옆으로 피하며 화살을 연사하고 바람을 모은다.,{caster}가 기민하게 몸을 피하며 {target}에게 화살을 연사합니다!,"원본 대미지 5,698×3, 바람 25%, 재사용 대기 9초→2턴. 피해 원본 ×0.7. 최대 스택 3은 충전 개념이 없어 제외. 도탄 화살은 1:1이라 제외",
hawk_shot,TRUE,공격·방해,자신·상대,2,0,60,궁극기 게이지,,150,FALSE,공중으로 도약해 상대를 약화시키는 저격. 브레이크된 상대에게 더 큰 피해를 준다.,{caster}가 공중으로 도약해 {target}의 빈틈을 저격합니다!,"원본 총합 대미지 8,029×2(브레이크 대상 16,059×2 → +100%), 약화 5초(상대 턴 기준 2턴), 바람 25%, 재사용 대기 11초→2턴. 피해 원본 ×0.7. 주변 적 약화·도탄은 1:1이라 제외",
escape_step,TRUE,공격·방해,자신·상대,2,0,60,궁극기 게이지,,150,FALSE,뒤로 물러나며 반드시 치명타로 적중하는 화살을 쏘고 상대를 둔화시킨다. 바람을 크게 모은다.,{caster}가 신속히 거리를 벌리며 {target}에게 화살을 날립니다!,"원본 대미지 7,977, 초탄 치명타 확률 100%, 바람 75%, 이동 속도 감소 50% 5초→1턴(쿨다운증가), 재사용 대기 9초→2턴. 피해 원본 ×0.7. 도탄 화살은 1:1이라 제외",
blazing_trail,TRUE,궁극기·보조,자신·상대,0,0,100,궁극기 게이지,300,,TRUE,점화된 불화살을 쏘아 피해를 주고 모든 스킬의 재사용 대기를 초기화한다. 잠시 모든 공격이 불화살로 바뀌어 추가 피해를 준다.,{caster}가 점화된 특수 화살로 바꿔 {target}에게 불화살을 쏘아 보냅니다!,"원본 대미지 14,505, 모든 스킬 재사용 대기 초기화, 불화살 15초→3턴(화살마다 추가 대미지 1,450). 피해 원본 ×0.7. 엠블럼 장착 필요는 사용 조건이 아니라 제외","『바람이 그린 궤적 끝, 불꽃이 피어나리라』"
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
fixture_placeholder,arrow_revolver,magnum_shot,대체,1,1,자원보유,archer_wind=999,FALSE,즉시,테스트 자리표시,0
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
arv_01,arrow_revolver,1,피해,상대,,2357,6,1,0,,0,,"원본 3,367×6",,,,,,,,
arv_02,arrow_revolver,2,추가피해,상대,,1524,1,1,0,,0,,"불화살 추가 대미지 원본 1,450×6발 × 보정 × 0.25(배틀 HP 직접)",자신,상태효과보유,archer_fire_arrow,,,,,
mgs_01,magnum_shot,1,피해,상대,,10154,1,1,0,,0,,"원본 14,505",,,,,,,,
mgs_02,magnum_shot,2,브레이크피해,상대,,1,1,1,0,,0,,브레이크 대미지 1칸,,,,,,,,
mgs_03,magnum_shot,3,추가피해,상대,,254,1,1,0,,0,,"불화살 추가 대미지 원본 1,450×1발 × 보정 × 0.25(배틀 HP 직접)",자신,상태효과보유,archer_fire_arrow,,,,,
sds_01,side_step,1,피해,상대,,3989,3,1,0,,0,,"원본 5,698×3",,,,,,,,
sds_02,side_step,2,추가피해,상대,,762,1,1,0,,0,,"불화살 추가 대미지 원본 1,450×3발 × 보정 × 0.25(배틀 HP 직접)",자신,상태효과보유,archer_fire_arrow,,,,,
sds_03,side_step,3,자원증가,자신,,25,1,1,0,archer_wind,0,,바람 획득 25%,,,,,,,,
hks_01,hawk_shot,1,조건부피해증가,상대,,100,1,1,0,,0,,"브레이크 대상 총합 대미지 16,059×2 = +100%",상대,상태효과유형보유,브레이크,,,,,
hks_02,hawk_shot,2,피해,상대,,5620,2,1,0,,0,,"원본 총합 대미지 8,029×2",,,,,,,,
hks_03,hawk_shot,3,추가피해,상대,,508,1,1,0,,0,,"불화살 추가 대미지 원본 1,450×2발 × 보정 × 0.25(배틀 HP 직접)",자신,상태효과보유,archer_fire_arrow,,,,,
hks_04,hawk_shot,4,상태효과,상대,,0,1,1,2,archer_weakened,1,,약화 5초. 상대 턴으로 세므로 궁수의 다음 행동까지 남도록 2턴,,,,,,,,
hks_05,hawk_shot,5,자원증가,자신,,25,1,1,0,archer_wind,0,,바람 획득 25%,,,,,,,,
esc_01,escape_step,1,상태효과,자신,,0,1,1,1,archer_escape_crit,1,,초탄 치명타 확률 100%(이번 스킬에만),,,,,,,,
esc_02,escape_step,2,피해,상대,,5584,1,1,0,,0,,"원본 7,977",,,,,,,,
esc_03,escape_step,3,추가피해,상대,,254,1,1,0,,0,,"불화살 추가 대미지 원본 1,450×1발 × 보정 × 0.25(배틀 HP 직접)",자신,상태효과보유,archer_fire_arrow,,,,,
esc_04,escape_step,4,상태효과,상대,,0,1,1,1,archer_slowed,1,,이동 속도 감소 50% 5초→1턴,,,,,,,,
esc_05,escape_step,5,자원증가,자신,,75,1,1,0,archer_wind,0,,바람 획득 75%,,,,,,,,
blt_01,blazing_trail,1,피해,상대,,10154,1,1,0,,0,,"원본 14,505",,,,,,,,
blt_02,blazing_trail,2,쿨다운감소,자신,,99,1,1,0,,0,모든 스킬의 재사용 대기 시간이 초기화됩니다!,모든 스킬의 재사용 대기 시간 초기화(큰 값으로 근사),,,,,,,,
blt_03,blazing_trail,3,상태효과,자신,,0,1,1,3,archer_fire_arrow,1,,불화살 15초→3턴,,,,,,,,
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
wind_flow,TRUE,"바람이 가득 차면(100%) 질주하는 바람 1턴(바람 소모 초당 35%·첫 5초 50% 감소 → 약 5.4초). 바람을 모두 소모하고, 질주 중 얻은 바람은 다음 질주로 이월(총 질주 시간 동일). 이동 속도 25%는 추진력으로 피해 증가. 도탄·직접 이동 바람 생성·매그넘 샷 캐스팅 단축은 제외"
weak_point_piercing,TRUE,30% 확률 약점 적중(최종 대미지 +30%)을 기대값 주는 피해 +9%로 단순화. 호크 샷 약화·다발 사격은 약점 확정(+30%)
combat_mastery_swift_archer,TRUE,연타 대미지 +5%(멀티히트피해증가). 레벨 30 어시스트 해금은 제외
multi_shot,TRUE,"타격 40회마다 다발 사격(10,465 + 추가 적중 5,232×4, 항상 약점). 스킬이 적중한 턴마다 10회(스킬 화살 + 사이사이 기본 공격, 도적 기본 공격 해석과 같음)로 세어 4턴마다 발동. 발동 후 적중 횟수 비례 피해 증가(2%, 최대 200%)는 전투가 짧아 생략"
propulsion,TRUE,이동 속도 증가 1%당 대미지 0.5%. 이동 속도 증가원은 질주하는 바람(+25%)뿐이라 질주 중 주는 피해 +12.5%
tailwind,TRUE,"5초마다 순풍 1중첩(턴당 1, 최대 5). 중첩당 급소 회피 +8%·연타 대미지 +6%. 치명타를 맞으면(급소 회피 실패) 모두 잃는다(발동시점 치명타피격시)"
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
wfl_01,wind_flow,1,상태효과,자신,0,1,1,1,archer_gale,0,,,,,,,,,자원최대치도달시,,archer_wind,바람 100% → 질주하는 바람 1턴,
wfl_02,wind_flow,2,자원소모,자신,0,1,1,0,archer_wind,0,,,,,,,,전부,자원최대치도달시,,archer_wind,바람을 모두 소모(질주하는 바람 동안 소모),
wpp_01,weak_point_piercing,1,주는피해증가,자신,0,1,1,0,archer_weak_point,0,,,,,,,,,전투시작,,,약점 적중 기대값 +9%,
cmsa_01,combat_mastery_swift_archer,1,멀티히트피해증가,자신,0,1,1,0,combat_mastery_swift_multi,0,,,,,,,,,전투시작,,,연타 대미지 +5%,
mts_01,multi_shot,1,자원증가,자신,10,1,1,0,archer_multi_count,0,,,,,,,,,스킬적중완료시,,,스킬이 적중한 턴의 타격 수 10(스킬 화살 + 사이사이 기본 공격),
mts_02,multi_shot,2,상태효과,자신,0,1,1,1,archer_multi_shot,0,,,,,,,,,자원최대치도달시,,archer_multi_count,다발 사격 로그 표시용,
mts_03,multi_shot,3,피해,상대,8737,1,1,0,,0,,,,,,,,,자원최대치도달시,,archer_multi_count,"원본 10,465 × 보정 × 항상 약점(1.3/1.09)",
mts_04,multi_shot,4,피해,상대,4368,4,1,0,,0,,,,,,,,,자원최대치도달시,,archer_multi_count,"추가 적중 원본 5,232×4 × 보정 × 항상 약점",
mts_05,multi_shot,5,상태해제,자신,0,1,1,0,archer_multi_shot,0,,,,,,,,,자원최대치도달시,,archer_multi_count,표시용 상태 해제,
mts_06,multi_shot,6,자원소모,자신,0,1,1,0,archer_multi_count,0,,,,,,,,전부,자원최대치도달시,,archer_multi_count,타격 수 초기화,
prp_01,propulsion,1,상태효과,자신,0,1,1,1,archer_propulsion,0,,,,,,,,,자원최대치도달시,,archer_wind,질주하는 바람(이동 속도 +25%) 동안 주는 피해 +12.5%,
twd_01,tailwind,1,턴당자원증가,자신,0,1,1,0,archer_tailwind_gain,0,,,,,,,,,전투시작,,,턴 시작마다 순풍 +1,
twd_02,tailwind,2,받는치명타확률감소,자신,0,1,1,0,archer_tailwind_evasion,0,,,,,,,,,전투시작,,,중첩자원ID=archer_tailwind로 중첩당 급소 회피 +8%,
twd_03,tailwind,3,멀티히트피해증가,자신,0,1,1,0,archer_tailwind_multi,0,,,,,,,,,전투시작,,,중첩자원ID=archer_tailwind로 중첩당 연타 대미지 +6%,
twd_04,tailwind,4,자원소모,자신,0,1,1,0,archer_tailwind,0,,자신,자원보유,archer_tailwind,>=,1,,전부,치명타피격시,,,치명타를 맞으면(급소 회피 실패) 순풍을 모두 잃는다,
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
archer_wind,바람,자원,100,0,0,가산,TRUE,"궁수 바람(최대 100%). 사이드 스텝·호크 샷 25, 이스케이프 스텝 75를 얻고, 가득 차면 질주하는 바람을 얻으며 모두 소모한다(패시브 바람결).",
archer_tailwind,순풍,중첩,5,0,0,가산,TRUE,궁수 순풍(최대 5). 턴 시작마다 +1(5초마다 → 턴당 1). 중첩당 급소 회피 +8%·연타 대미지 +6%. 치명타를 맞으면 모두 잃는다.,
archer_multi_count,다발 사격 타격 수,자원,40,0,0,가산,TRUE,다발 사격 발동을 위한 타격 횟수(40). 스킬이 적중한 턴마다 10(스킬 화살과 사이사이의 기본 공격). 40이 되면 다발 사격을 쏘고 초기화한다.,TRUE
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
break_broken,브레이크,브레이크|받는피해증가,0.2,브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외),,,,,,,,
combat_mastery_swift_multi,전투 숙련: 쾌속,멀티히트피해증가,0.05,적에게 주는 연타(다단) 피해 +5%,,,,,,,,
archer_gale,질주하는 바람,없음,0,바람이 가득 차 얻는 강화 효과(1턴). 이동 속도 +25%(패시브 추진력으로 주는 피해 +12.5%). 도탄은 1:1이라 주변 적이 없어 제외,,,,,,,,
archer_propulsion,추진력,주는피해증가,0.125,질주하는 바람의 이동 속도 +25% × 1%당 대미지 0.5% = 주는 피해 +12.5%(1턴).,,,,,,,,TRUE
archer_weak_point,약점 관통,주는피해증가,0.09,30% 확률로 약점 적중(최종 대미지 +30%)을 기대값 +9%로 단순화한 영구 상태.,,,,,,,,
archer_weakened,약화,받는피해증가,0.21,호크 샷 약화(5초). 궁수의 모든 공격이 약점으로 적중해 약점 관통 기대값 +9%에 +21%를 더해 +30%. 상대 턴으로 세므로 궁수의 다음 행동까지 남도록 2턴.,,,,,,,,
archer_escape_crit,이스케이프 스텝: 치명타,다음스킬치명타확률증가|다음스킬소모,1,이스케이프 스텝 초탄 치명타 확률 100%. 이번 스킬에만 적용하고 소모한다.,,,,,,,,TRUE
archer_slowed,둔화,쿨다운증가,1,이스케이프 스텝 이동 속도 감소 50%(5초→1턴). 지속 중 상대의 쿨다운이 줄지 않는다(석궁사수 둔화와 같은 단순화).,,,,,,,,
archer_fire_arrow,불화살,없음,0,"작열의 궤적(15초→3턴). 모든 공격이 불화살로 바뀌어 화살마다 추가 피해(원본 1,450)를 준다.",,,,,,,,
archer_tailwind_gain,순풍: 획득,턴당자원증가,1,전투 시작에 거는 영구 상태. 궁수의 턴 시작마다 순풍 +1(5초마다 → 턴당 1).,,,,,,archer_tailwind,,TRUE
archer_tailwind_evasion,순풍: 급소 회피,받는치명타확률감소,0.08,순풍 중첩당 급소 회피 +8%(받는 치명타 확률 -8%p).,,archer_tailwind,,,,,,TRUE
archer_tailwind_multi,순풍: 연타,멀티히트피해증가,0.06,순풍 중첩당 연타(다단) 대미지 +6%.,,archer_tailwind,,,,,,TRUE
archer_multi_shot,다발 사격,없음,0,타격 40회마다 기본 공격이 다발 사격으로 강화되어 화살 5발을 쏜다. 로그 표시용으로 걸었다가 바로 해제한다.,,,,,,,,
"""",
    };
}
