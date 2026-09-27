using Molly.Battle;

/// <summary>
/// 빙결술사의 크리스탈 엣지 연속 단계·글래시어 커터→아이시클 섀터(브레이크 익스텐드)·아이스 스파이크·앱솔루트 제로·
/// 베일 오브 윈터·아이시클 마크 동결·흩날리는 서리·북풍의 인도자 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-09-27 배틀 시트에서 빙결술사 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 빙결술사 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·배틀스킬AI·돌발 이벤트·생활스킬 시트는 석궁사수 사본을 그대로 쓴다.
/// </summary>
internal static class IceMageBattleTests
{
    // 난수 0.9의 피해 편차: 0.9 + 0.9 × (1.1 − 0.9).
    private const double Variance = .9 + .9 * .2;
    private static int Hit(double shownDamage, double outgoing = 1, double incoming = 1) => (int)Math.Round((shownDamage * .25 - 200 * .5) * Variance * outgoing * incoming);

    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow);
        BreakExtendTests(data);
        CrystalEdgeTests(data);
        FrostTests(data);
        AbsoluteZeroTests(data);
        PassiveTests(data);
    }

    private static void BreakExtendTests(BattleDataSnapshot data)
    {
        // 브레이크 게이지 1칸이면 글래시어 커터가 바로 브레이크를 건다. 상대가 행동을 잃은 뒤 커터를 다시 누르면 아이시클 섀터가 나가고,
        // 브레이크를 익스텐드해 상대가 행동을 한 번 더 잃는다. 섀터는 익스텐드 뒤에 맞아 무방비 대미지 130%를 받는다.
        var result = Duel(data, ["glacier_cutter"], maxActions: 7, rules: [("break_gauge_maximum", "1")]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "글래시어 커터" && turns[0].Damage == Hit(8466) && result.Events.Any(x => x.Type == "BreakActivated" && x.Detail == "무방비 대미지 120%"),
            "글래시어 커터는 피해와 브레이크 피해를 준다");
        Assert(turns[1].Skill == "아이시클 섀터" && turns[1].Damage == Hit(12699, incoming: 1.3)
            && result.Events.Count(x => x.Type == "BreakExtended" && x.Detail == "무방비 대미지 130%") == 1
            && result.Events.Count(x => x.Type == "BreakActionLost" && x.Target == "B") == 2,
            "상대가 브레이크되면 글래시어 커터가 아이시클 섀터로 바뀌어 브레이크를 익스텐드하고, 상대는 행동을 두 번 잃는다");
        Assert(turns[2].Skill != "아이시클 섀터" && result.Events.Any(x => x.Type == "StatusExpired" && x.Actor == "B" && x.Detail == "브레이크 익스텐드"),
            "익스텐드된 브레이크에는 아이시클 섀터가 다시 나오지 않고, 상대의 다음 행동 시작에 풀린다");
    }

    private static void CrystalEdgeTests(BattleDataSnapshot data)
    {
        // 쿨다운 2라 한 턴씩 일반 공격을 섞는다. 연속 단계는 3턴 동안 남아 2·3회차 피해가 ×1.2·×1.2×1.17로 늘고 서리도 30·38·46을 얻는다.
        var turns = Turns(Duel(data, ["crystal_edge"], maxActions: 14, withoutPassives: ["icicle_mark", "guide_of_north_wind"]));
        var edges = turns.Where(x => x.Skill == "크리스탈 엣지").ToArray();
        Assert(edges.Length >= 4 && edges[0].Damage == Hit(6047) && edges[1].Damage == Hit(6047 * 1.2) && edges[2].Damage == Hit(6047 * 1.2 * 1.17) && edges[3].Damage == Hit(6047)
            && edges[2].Resources.Contains("서리 +8 (현재 114)"),
            "크리스탈 엣지는 이어 쓸수록 피해와 서리가 늘고, 3회째 뒤에는 처음부터 다시 시작한다");
    }

    private static void FrostTests(BattleDataSnapshot data)
    {
        // 아이스 스파이크: 서리 300을 내고 보호막·공격력 +20%를 얻은 뒤 반격 피해를 준다. 서리 2칸 소모로 흩날리는 서리 2중첩.
        var result = Duel(data, ["ice_spikes"], maxActions: 2, initial: [("ice_frost", 300)]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "아이스 스파이크" && turns[0].Damage == Hit(7559, outgoing: 1.2) && turns[0].Statuses.Contains("아이스 스파이크")
            && turns[0].Resources.Contains("서리 -300 (현재 0)") && turns[0].Resources.Contains("아이스 스파이크 +9290 (현재 9290)") && turns[0].Resources.Contains("흩날리는 서리 +2 (현재 2)")
            && result.Events.Any(x => x.Type == "ShieldAbsorbed" && x.Target == "A" && x.Detail == "아이스 스파이크"),
            "아이스 스파이크는 서리 2칸으로 보호막·공격력 증가·반격을 얻고 흩날리는 서리를 쌓는다");
        Assert(turns[0].Statuses.Contains("마크 오브 아이시클") && turns[0].Statuses.Contains("빙결"), "스킬 적중 시 마크 오브 아이시클과 빙결 지속 피해를 건다");

        // 베일 오브 윈터: 서리 3칸(450) 이상에서 공격받으면 서리 1칸으로 보호막을 만들고, 서리를 소모했으므로 흩날리는 서리 1중첩.
        var veil = Duel(data, [], maxActions: 4, initial: [("ice_frost", 450)]).Events;
        Assert(veil.Any(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail == "베일 오브 윈터 +1997 (현재 1997)")
            && veil.Any(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail == "서리 -150 (현재 300)")
            && veil.Any(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail == "흩날리는 서리 +1 (현재 1)")
            && veil.Count(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail!.StartsWith("베일 오브 윈터 +", StringComparison.Ordinal)) == 1,
            "베일 오브 윈터는 서리 3칸 이상일 때만 피격 시 서리 1칸으로 보호막을 만든다");
    }

    private static void AbsoluteZeroTests(BattleDataSnapshot data)
    {
        var turns = Turns(Duel(data, ["absolute_zero"], maxActions: 2, initial: [("ultimate_gauge", 300)]));
        Assert(turns[0].Skill == "앱솔루트 제로" && turns[0].Damage == Hit(21166) && turns[0].Broken
            && new[] { "강화된 얼음 지대", "냉기 취약", "프리징 필드", "앱솔루트 제로" }.All(turns[0].Statuses.Contains)
            && turns[0].Resources.Contains("냉기 보호막 +9290 (현재 9290)") && turns[0].Resources.Contains("서리 +900 (현재 900)"),
            "앱솔루트 제로는 한 번에 브레이크를 걸고 강화된 얼음 지대·보호막·서리 최대 회복·받는 피해 감소를 얻는다");
    }

    private static void PassiveTests(BattleDataSnapshot data)
    {
        // 동결 20중첩에서 크리스탈 엣지가 적중하면 25중첩이 되어 동결 피해를 주고 초기화한다.
        var freeze = Turns(Duel(data, ["crystal_edge"], maxActions: 2, initial: [("ice_freeze_stack", 20)], withoutPassives: ["guide_of_north_wind"]));
        Assert(freeze[0].Damage == Hit(6047) + Hit(25197) && freeze[0].Resources.Contains("동결 -25 (현재 0)"), "아이시클 마크는 동결 25중첩에 동결 피해를 주고 초기화한다");

        // 북풍의 기호 4에서 크리스탈 엣지 적중(+2)으로 6이 되면 북풍의 인도자 6회를 얻고, 다음 일반 공격이 추가 냉기 피해와 서리 50을 준다.
        var north = Turns(Duel(data, ["crystal_edge"], maxActions: 4, initial: [("ice_north_sign", 4)], withoutPassives: ["icicle_mark"]));
        Assert(north[0].Resources.Contains("북풍의 인도자 +6 (현재 6)") && north[1].Skill == "(일반 공격)"
            && north[1].Resources.Contains("북풍의 인도자 -1 (현재 5)") && north[1].Resources.Contains("서리 +50 (현재 80)") && north[1].Hits == 2,
            "북풍의 인도자는 기호 6개로 얻고, 이후 일반 공격·크리스탈 엣지·글래시어 커터 적중마다 추가 냉기 피해와 서리를 준다");
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses, int Hits, bool Broken);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, (string Id, int Value)[]? initial = null, (string Id, string Value)[]? rules = null, string[]? withoutPassives = null)
    {
        var ruleMap = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", maxActions.ToString()) }.Concat(rules ?? [])) ruleMap[id] = ruleMap[id] with { Value = value };
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in initial ?? []) resources[id] = resources[id] with { InitialValue = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = ruleMap,
            Classes = new Dictionary<string, BattleClass> { ["ice_mage"] = data.Classes["ice_mage"] with { SkillIds = skills, PassiveIds = data.Classes["ice_mage"].PassiveIds.Except(withoutPassives ?? []).ToArray() }, ["idle"] = new("idle", "대상", Array.Empty<string>()) },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, LoadedAt = data.LoadedAt
        };
        // 빙결술사가 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "ice_mage", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
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
ice_mage,빙결술사,마법사,"차가운 오브를 들고 얼어붙은 검을 휘두르는 얼음 원소에 특화된 클래스. 대기 속 냉기를 응집하여 갖은 형상의 얼음 결정으로 빚어낸 후, 적에게 뼛속까지 시린 추위를 선사한다. 천으로 만든 옷을 선호하나, 겹겹이 두른 얼음 방어막은 여느 갑옷 못지 않게 단단하다.",crystal_edge,glacier_cutter,ice_spikes,freezing_field,frozen_orb,absolute_zero,veil_of_winter,icicle_mark,combat_mastery_protection_ice_mage,scattered_frost,piercing_cold,guide_of_north_wind
"""",
        ["스킬"] = """"
ID,이름,스킬구분,부모스킬ID,설명,태그,기타
crystal_edge,크리스탈 엣지,일반,,"냉기로 만든 검을 세차게 휘두르는 빙결 마법. 단번에 얼어붙은 바람이 전방의 적들을 강타한다. 일정 시간 내로 최대 3회까지 재사용할 수 있으며, 재사용할 때마다 스킬의 피해와 적중 시 서리 생성량이 늘어난다.","원소, 강타",등급: 에픽; 강화 레벨: +24; 대미지: 10078; 적중 시 서리 생성량: 30; 연속 사용 시 추가 서리 생성량: 8; 최대 서리 생성량: 80; 최대 사용 횟수: 3회; 재사용 대기 시간: 8초; 사거리: 3m; 범위: 3m
glacier_cutter,글래시어 커터,일반,,냉기로 만든 두 자루 검을 휘둘러 공격하는 빙결 마법. 냉기가 지나는 궤적 위의 모든 적을 도발하고 얼어붙게 만든다.,"강타, 방해",등급: 고급; 강화 레벨: +8; 대미지: 14110; 적중 시 서리 생성량: 75; 연속 사용 시 추가 서리 생성량: 15; 최대 서리 생성량: 150; 브레이크 대미지: 1칸; 도발 지속 시간: 6초; 재사용 대기 시간: 10초; 사거리: 15m; 범위: 16m
icicle_shatter,아이시클 섀터,파생,glacier_cutter,"자세가 무너진 적을 확실하게 제압하는 공격. 적을 얼음 가시로 꿰뚫는 것으로 봉쇄하여 브레이크 상태를 연장하며 피해를 입히고, 적의 브레이크 상태가 유지되는 동안 더 큰 피해를 입게 만든다. 브레이크 익스텐드는 브레이크 대상에게만 사용할 수 있으며, 플레이어마다 각 대상에게 한 번씩만 적용할 수 있다. 여러 명이 동시에 적용할 경우 효과가 점점 감소한다. 브레이크 익스텐드의 받는 피해 증가 효과는 중복되지 않는다.","원소, 강타, 방해",등급: 고급; 강화 레벨: +8; 대미지: 21165; 브레이크 익스텐드 적용; 재사용 대기 시간: 1초; 사거리: 15m
ice_spikes,아이스 스파이크,일반,,"가시 돋친 서리 보호막을 두르는 방어 마법. 일정량의 피해를 흡수하고, 피해를 준 적에게 얼어붙은 가시로 반격하는 강화 효과: 아이스 스파이크를 얻는다. 공격력이 증가하며, 효과가 끝나면 가시가 폭발해 범위 피해를 준다.","원소, 생존, 보조",등급: 에픽; 강화 레벨: +24; 흡수량: 9290; 공격력 증가: 20%; 폭발 대미지: 12598; 반격 대미지: 12598; 반격 시 서리 생성량: 15; 반격 범위: 5m; 서리 생성 재사용 대기 시간: 2초; 서리 소모량: 300; 지속 시간: 15초; 재사용 대기 시간: 20초; 최대 스택 수: 2
freezing_field,프리징 필드,일반,,"급속도로 지면을 얼려 얼음 지대를 생성하는 빙결 마법. 얼음 지대 안에서는 자신의 이동 속도가 증가하고, 받는 피해가 감소한다. 반면, 적들은 이동 속도가 감소하고 받는 피해가 증가하며, 주기적으로 냉기 피해를 입는다.","원소, 생존, 소환",등급: 에픽; 강화 레벨: +24; 주기 대미지: 2초마다 5039; 타격당 최대 서리 생성량: 10; 타격당 최소 서리 생성량: 2; 받는 대미지 감소: 10%; [시너지] 적이 받는 대미지 증가: 10%; 적 이동 속도 감소: 30%; 자신의 이동 속도 증가: 50%; 범위: 8m; 지속 시간: 24초; 재사용 대기 시간: 8초
frozen_orb,프로즌 오브,일반,,"거센 냉기 폭풍을 일으키는 결계 오브의 결합 마법. 휘몰아치는 얼음 칼날로 지속적인 범위 피해를 주며, 피격된 적으로부터 서리를 생성한다.","원소, 소환",등급: 고급; 강화 레벨: +8; 최소 대미지: 0.5초마다 2822; 최대 대미지: 0.5초마다 4233; 종료 대미지: 5644; 타격당 최소 서리 생성량: 5; 타격당 최대 서리 생성량: 10; 사거리: 3.5m; 범위: 2m; 시전 시간: 1.33초; 채널링 시간: 2.67초; 재사용 대기 시간: 24초
absolute_zero,앱솔루트 제로,궁극기,,"냉기를 모아 얼음 운석을 소환하는 혹한의 비술. 운석은 충돌 지점 주변의 적들에게 강력한 피해를 주고 도발하며 얼어붙게 만들고, 일대를 강화된 프리징 필드로 뒤덮는다. 이후 냉기로부터 만들어낸 보호막을 즉시 얻고, 서리를 최대로 회복한다. 또한 잠시 동안 받는 피해가 감소한다. 『잊힌 시대의 겨울을, 여기 다시 한번』","궁극기, 원소, 생존, 소환",등급: 고급; 강화 레벨: +8; 엠블럼 장착 필요; 최초 대미지: 35276; 브레이크 대미지: 3칸; 주기 대미지: 2초마다 7760; 서리 생성량: 10; 사거리: 15m; 범위: 5.5m; 강화된 프리징 필드 범위: 17.5m; 받는 대미지 감소: 50%; 대미지 감소 지속 시간: 10초; 전체 지속 시간: 15초; 궁극기 비용: 200
"""",
        ["패시브스킬"] = """"
ID,이름,설명,기타
veil_of_winter,베일 오브 윈터,"서리를 겹겹이 둘러 몸을 방어하는 마법 장막. 서리가 3개 이상일 때, 공격을 받으면 서리 1개를 써서 보호막을 만든다.",보호막 생성을 위한 최소 자원 수치: 450; 흡수량: 1997
icicle_mark,아이시클 마크,"혹독한 냉기와 마주한 적에게 새기는 얼음 표식. 기본 공격이나 스킬을 사용할 시, 타겟에게 약화 효과: 마크 오브 아이시클을 남겨 주기적인 피해를 준다. 적을 냉기 속성으로 직접 공격 시, 동결 효과를 부여한다. 동결 효과가 일정 횟수 이상 중첩될 경우, 강력한 피해를 입힌다. 임계 체력 이하의 적을 공격 시, 즉시 동결이 발동하며, 이 경우 방어력을 무시한 피해를 입힌다.",태그: 원소; 지속 대미지: 3초마다 2799; 동결 대미지: 41995; 동결 발동을 위한 중첩 수: 25; 동결 임계 체력: 20%
combat_mastery_protection_ice_mage,전투 숙련: 수호,"전방에서 아군을 보호하는 숙련된 전투 기법. 모든 공격으로부터 받는 피해가 감소하고, 적에게 주는 멀티히트 피해가 증가한다. 빙결술사 클래스 레벨이 30에 도달하면 전투 시 클래스 특화 어시스트를 사용할 수 있다.",받는 대미지 감소: 15%; 멀티히트 대미지 증가: 3%; 클래스 특화 어시스트 해금: 빙결술사 클래스 레벨 30
scattered_frost,흩날리는 서리,"흩뿌린 서리로 방어를 굳히는 빙결 마법의 경지. 받는 대미지가 감소하며, 서리를 소모한 만큼 자신의 방어력과 적에게 주는 피해가 증가한다.",받는 대미지 감소: 10%; 중첩당 방어력 증가: 10%; 중첩당 대미지 증가: 4%; 지속 시간: 10초
piercing_cold,사무치는 냉기,"적의 상처에 파고든 냉기를 제어하는 전투 방식. 빙결술사 스킬을 사용할 때마다 일정 확률로 적에게 지속 피해: 빙결을 준다. 또한, 강타 시 적에게 지속 피해: 빙결을 추가로 준다.","태그: 원소, 강타; 빙결 지속 대미지: 839; 단일 타격형 스킬 확률: 100%; 연타형 스킬 확률: 25%; 강타 시 빙결 지속 대미지: 1959"
guide_of_north_wind,북풍의 인도자,"혹한의 바람을 칼날에 휘감는 강화 마법. 크리스탈 엣지와 크리스탈 엣지가 변화한 스킬, 글래시어 커터와 글래시어 커터가 변화한 스킬 그리고 기본 공격을 적중시킬 때마다 자신에게 북풍의 기호를 얻는다. 북풍의 기호가 최대로 중첩되면 북풍의 인도자 효과를 얻어, 다음 일정 횟수의 크리스탈 엣지와 크리스탈 엣지가 변화한 스킬, 글래시어 커터와 글래시어 커터가 변화한 스킬, 기본 공격 사용 시, 전방에 냉기를 내뿜어 범위 피해를 주고 서리를 생성한다.","태그: 원소, 강타; 해금 조건: 빙결술사 Lv.60 이상; 대미지: 13998; 필요한 중첩 수: 6; 추가 공격 횟수: 6; 서리 생성량: 50; 범위: 5m; 지속 시간: 40초"
"""",
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고
crystal_edge,TRUE,공격,상대,2,0,65,궁극기 게이지,,150,FALSE,냉기의 검을 휘둘러 강타하고 서리를 만든다. 이어서 쓸수록(최대 3회) 피해와 서리가 늘어난다.,{caster}가 냉기로 만든 검을 {target}에게 세차게 휘두릅니다!,"원본 재사용 대기 8초→2턴, 표시 피해 10,078, 서리 30(+8씩, 최대 80). 최대 3회 재사용은 3턴 안에 다시 쓰면 단계가 오르는 연속 단계(ice_edge_stage)로 반영. 재사용할 때마다 늘어나는 피해는 원본 수치가 없어 2회차 ×1.2, 3회차 ×1.4로 가정"
glacier_cutter,TRUE,공격·방해,상대,2,0,65,궁극기 게이지,,150,FALSE,냉기의 두 검으로 베어 브레이크 피해를 주고 서리를 만든다. 상대가 브레이크되면 아이시클 섀터로 바뀐다.,{caster}가 냉기의 두 검으로 {target}을 베어 얼립니다!,"원본 재사용 대기 10초→2턴, 표시 피해 14,110, 서리 75, 브레이크 1칸. 도발은 1:1이라 생략. 상대가 익스텐드 전 브레이크 상태이면 재사용 시 파생 glacier_to_icicle_shatter로 아이시클 섀터가 나간다"
icicle_shatter,TRUE,공격·방해,상대,2,0,0,,,0,FALSE,얼음 가시로 브레이크된 적을 꿰뚫어 브레이크를 연장(브레이크 익스텐드)하고 피해를 준다.,{caster}가 무너진 {target}을 얼음 가시로 꿰뚫습니다!,"파생 전용. 상대가 브레이크(익스텐드 전)일 때 글래시어 커터를 다시 누르면 실행. 표시 피해 21,165. 브레이크 익스텐드: 브레이크를 익스텐드 상태(무방비 대미지 130%)로 바꾸고 행동 불가를 브레이크 지속만큼 더해 2배로 만든다. 브레이크 한 번에 한 번만. 기본쿨다운 2는 재사용 시 글래시어 커터의 쿨다운으로 적용(원본 1초)"
ice_spikes,TRUE,생존·보조,자신·상대,4,0,60,ice_frost,300,,FALSE,"서리 2칸으로 가시 돋친 서리 보호막을 두르고 공격력을 높이며, 가시로 반격한다.",{caster}가 가시 돋친 서리 보호막을 두릅니다!,"원본 재사용 대기 20초→4턴, 서리 소모 300(2칸). 흡수량 9,290(15초→3턴), 공격력 +20%(3턴). 피격 시 반격 12,598은 사용 시 1회 피해로 단순화(힐러 프로텍션과 같은 방식). 효과가 끝날 때의 가시 폭발은 종료 시점 트리거가 없어 생략. 최대 스택 2는 충전 개념이 없어 제외. 서리를 비용으로 쓰므로 궁극기 게이지는 효과 행으로 적립"
freezing_field,TRUE,생존·방해,자신·상대,2,0,60,궁극기 게이지,,150,FALSE,"얼음 지대를 만들어 4턴 동안 자신의 받는 피해를 줄이고, 상대의 받는 피해를 늘리며 냉기 지속 피해를 준다.",{caster}가 발밑을 급속도로 얼려 얼음 지대를 펼칩니다!,"원본 재사용 대기 8초→2턴, 지속 24초→4턴. 받는 대미지 감소 10%, [시너지] 적 받는 대미지 증가 10%. 주기 대미지 2초마다 5,039는 턴(6초)당 3회면 다른 스킬보다 수 배 커서 턴당 1회 5,039로 보정. 서리는 타격당 2~10을 사용 시 10으로. 이동 속도 증감은 좌표가 없어 생략"
frozen_orb,TRUE,공격,상대,4,0,60,궁극기 게이지,,150,FALSE,냉기 폭풍 오브로 연속 피해를 주고 마지막에 폭발시키며 서리를 만든다.,{caster}가 거센 냉기 폭풍의 오브를 {target}에게 날립니다!,"원본 재사용 대기 24초→4턴. 채널링 2.67초 동안 0.5초마다 2,822~4,233을 평균 3,528×5회, 종료 대미지 5,644. 서리는 타격당 5~10을 5회 평균 38"
absolute_zero,TRUE,궁극기·생존,자신·상대,0,0,100,궁극기 게이지,300,,TRUE,"얼음 운석으로 강력한 피해와 브레이크를 주고, 강화된 얼음 지대·보호막·서리 최대 회복·받는 피해 50% 감소를 얻는다.",{caster}가 냉기를 모아 거대한 얼음 운석을 떨어뜨립니다!,"원본 최초 대미지 35,276, 브레이크 3칸(한 번에 브레이크), 강화된 프리징 필드 2초마다 7,760×15초는 턴당 1회 7,760×3턴으로 보정. 보호막은 흡수량이 없어 아이스 스파이크와 같은 9,290(2턴)으로 가정. 받는 대미지 감소 50% 10초→2턴. 도발 생략. 엠블럼 장착 필요는 사용 조건이 아니라 제외"
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
crystal_edge_01,crystal_edge,1,조건부피해증가,상대,,20,1,1,0,,0,,"2회차 이상 ×1.2(원본 증가량 미기재, 가정)",자신,자원보유,ice_edge_stage,>=,1,,,
crystal_edge_02,crystal_edge,2,조건부피해증가,상대,,17,1,1,0,,0,,3회차는 ×1.2×1.17≈×1.4,자신,자원보유,ice_edge_stage,>=,2,,,
crystal_edge_03,crystal_edge,3,피해,상대,,6047,1,1,0,,0,얼어붙은 바람이 {target}을 강타해 {damage}의 피해!,"표시 피해 10,078",,,,,,,,
crystal_edge_04,crystal_edge,4,자원증가,자신,,30,1,1,0,ice_frost,0,,적중 시 서리 30,,,,,,,,
crystal_edge_05,crystal_edge,5,자원증가,자신,,8,1,1,0,ice_frost,0,,연속 사용 시 추가 서리 8,자신,자원보유,ice_edge_stage,>=,1,,,
crystal_edge_06,crystal_edge,6,자원증가,자신,,8,1,1,0,ice_frost,0,,3회차 추가 서리 8,자신,자원보유,ice_edge_stage,>=,2,,,
crystal_edge_07,crystal_edge,7,자원증가,자신,,1,1,1,0,ice_edge_stage,0,,연속 단계 +1(3턴 안에 다시 쓰면 유지),,,,,,,,
crystal_edge_08,crystal_edge,8,자원소모,자신,,0,1,1,0,ice_edge_stage,0,,3회 모두 쓰면 처음부터,자신,자원보유,ice_edge_stage,>=,3,,전부,
glacier_cutter_01,glacier_cutter,1,피해,상대,,8466,1,1,0,,0,냉기의 궤적이 {target}을 베어 {damage}의 피해!,"표시 피해 14,110",,,,,,,,
glacier_cutter_02,glacier_cutter,2,브레이크피해,상대,,1,1,1,0,,0,{target}이 얼어붙어 휘청입니다!,브레이크 대미지 1칸,,,,,,,,
glacier_cutter_03,glacier_cutter,3,자원증가,자신,,75,1,1,0,ice_frost,0,,적중 시 서리 75(연속 사용 추가 15는 생략),,,,,,,,
icicle_shatter_01,icicle_shatter,1,브레이크익스텐드,상대,,0,1,1,0,break_extended,0,,브레이크 익스텐드: 익스텐드 상태로 바꾸고 행동 불가를 브레이크 지속만큼 더함,,,,,,,,
icicle_shatter_02,icicle_shatter,2,피해,상대,,12699,1,1,0,,0,얼음 가시가 무너진 {target}을 꿰뚫어 {damage}의 피해!,"표시 피해 21,165. 익스텐드 뒤에 맞아 무방비 130%가 붙는다",,,,,,,,
ice_spikes_01,ice_spikes,1,자원증가,자신,,9290,1,1,0,ice_spike_shield,0,가시 돋친 서리 보호막이 {caster}를 감쌉니다.,"흡수량 9,290(15초→3턴)",,,,,,,,
ice_spikes_02,ice_spikes,2,상태효과,자신,,0,1,1,3,ice_spikes_power,1,,"공격력 +20%, 15초→3턴",,,,,,,,
ice_spikes_03,ice_spikes,3,피해,상대,,7559,1,1,0,,0,얼어붙은 가시가 {target}에게 반격해 {damage}의 피해!,"피격 시 반격 12,598을 사용 시 1회로 단순화",,,,,,,,
ice_spikes_04,ice_spikes,4,자원증가,자신,,15,1,1,0,ice_frost,0,,반격 시 서리 15,,,,,,,,
ice_spikes_05,ice_spikes,5,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,서리를 비용으로 쓰는 스킬이라 궁극기 게이지는 효과 행으로 적립(도적 오버 차지 스킬과 같은 방식),,,,,,,,
freezing_field_01,freezing_field,1,상태효과,자신,,0,1,1,4,ice_field_guard,1,얼음 지대 위에서 {caster}의 받는 피해가 감소합니다.,"받는 대미지 감소 10%, 24초→4턴",,,,,,,,
freezing_field_02,freezing_field,2,상태효과,상대,,0,1,1,4,ice_field_chill,1,,"[시너지] 적이 받는 대미지 증가 10%, 24초→4턴",,,,,,,,
freezing_field_03,freezing_field,3,지속피해,상대,,3023,1,1,4,ice_field_frost,1,얼음 지대의 냉기,"주기 대미지 5,039를 턴당 1회로 보정",,,,,,,,
freezing_field_04,freezing_field,4,자원증가,자신,,10,1,1,0,ice_frost,0,,타격당 서리 2~10을 사용 시 10으로,,,,,,,,
frozen_orb_01,frozen_orb,1,피해,상대,,2117,5,1,0,,0,휘몰아치는 얼음 칼날이 {target}에게 총 {damage}의 피해!,"0.5초마다 2,822~4,233, 채널링 2.67초 → 평균 3,528×5",,,,,,,,
frozen_orb_02,frozen_orb,2,피해,상대,,3386,1,1,0,,0,냉기 폭풍이 터지며 {target}에게 {damage}의 피해!,"종료 대미지 5,644",,,,,,,,
frozen_orb_03,frozen_orb,3,자원증가,자신,,38,1,1,0,ice_frost,0,,타격당 서리 5~10 × 5회 평균,,,,,,,,
absolute_zero_01,absolute_zero,1,피해,상대,,21166,1,1,0,,0,얼음 운석이 {target}에게 떨어져 {damage}의 피해!,"최초 대미지 35,276",,,,,,,,
absolute_zero_02,absolute_zero,2,브레이크피해,상대,,3,1,1,0,,0,{target}이 통째로 얼어붙습니다!,브레이크 대미지 3칸,,,,,,,,
absolute_zero_03,absolute_zero,3,지속피해,상대,,4656,1,1,3,ice_zero_field,1,강화된 얼음 지대의 냉기,"강화된 프리징 필드 주기 대미지 7,760을 턴당 1회×3턴(15초)",,,,,,,,
absolute_zero_04,absolute_zero,4,상태효과,상대,,0,1,1,3,ice_field_chill,1,,강화된 프리징 필드의 적 받는 대미지 증가,,,,,,,,
absolute_zero_05,absolute_zero,5,상태효과,자신,,0,1,1,3,ice_field_guard,1,,강화된 프리징 필드의 받는 대미지 감소,,,,,,,,
absolute_zero_06,absolute_zero,6,자원증가,자신,,9290,1,1,0,ice_zero_shield,0,냉기로 만든 보호막이 {caster}를 감쌉니다.,"흡수량 미기재, 아이스 스파이크 9,290으로 가정",,,,,,,,
absolute_zero_07,absolute_zero,7,자원설정,자신,,900,1,1,0,ice_frost,0,,서리를 최대로 회복,,,,,,,,
absolute_zero_08,absolute_zero,8,상태효과,자신,,0,1,1,2,ice_zero_guard,1,혹한의 기운이 {caster}의 받는 피해를 크게 줄입니다.,"받는 대미지 감소 50%, 10초→2턴",,,,,,,,
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
glacier_to_icicle_shatter,glacier_cutter,icicle_shatter,조건,0,1,상대상태효과보유,break_broken,FALSE,재사용 시,상대가 익스텐드 전 브레이크 상태이면 글래시어 커터가 아이시클 섀터로 바뀐다(인게임 버튼 전환). 익스텐드되면 break_extended로 바뀌어 다시 나오지 않는다,100
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
veil_of_winter,TRUE,"서리 450(3칸) 이상일 때 공격을 받으면 서리 150(1칸)으로 보호막 1,997."
icicle_mark,TRUE,"스킬·일반 공격 적중 시 마크 오브 아이시클 지속 피해, 동결 중첩 25에 동결 피해. 적 체력 20% 이하면 즉시 동결."
combat_mastery_protection_ice_mage,TRUE,"받는 대미지 감소 15%, 멀티히트 대미지 +3%."
scattered_frost,TRUE,"받는 대미지 감소 10%, 서리 소모 칸당 방어·피해 증가(2턴)."
piercing_cold,TRUE,스킬 적중 시 지속 피해: 빙결.
guide_of_north_wind,TRUE,"크리스탈 엣지·글래시어 커터(섀터)·일반 공격 적중 6회로 북풍의 인도자, 다음 6회에 냉기 추가 피해와 서리."
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
vw_01,veil_of_winter,1,자원증가,자신,1997,1,1,0,ice_veil_shield,0,겹겹이 두른 서리가 보호막이 됩니다.,자신,자원보유,ice_frost,>=,450,,,피격시,,,"보호막 생성 최소 서리 450(3칸), 흡수량 1,997",
vw_02,veil_of_winter,2,자원소모,자신,150,1,1,0,ice_frost,0,,자신,자원보유,ice_frost,>=,450,,,피격시,,,서리 1개(150) 소모,
im_01,icicle_mark,1,지속피해,상대,1679,1,1,2,ice_mark,0,마크 오브 아이시클,,,,,,,,스킬적중완료시,,,"지속 대미지 3초마다 2,799를 턴당 1회로 보정. 지속 시간 미기재라 2턴",
im_02,icicle_mark,2,지속피해,상대,1679,1,1,2,ice_mark,0,마크 오브 아이시클,,,,,,,,기본공격적중시,,,기본 공격도 표식을 남긴다,
im_03,icicle_mark,3,피해,상대,25197,1,1,0,,0,임계에 몰린 {target}이 그대로 동결되어 {damage}의 피해!,상대,HP비율,,<,0.2,,,스킬적중완료시,,,임계 체력 20% 이하 즉시 동결. 방어력 무시는 생략. 반복 발동을 막으려 재발동 대기 3턴,3
im_04,icicle_mark,4,자원증가,자신,5,1,1,0,ice_freeze_stack,0,,,,,,,,,스킬적중완료시,,,냉기 직접 공격마다 동결 1중첩을 스킬 적중당 5로 환산(인게임 다단·빠른 타격),
im_05,icicle_mark,5,자원증가,자신,5,1,1,0,ice_freeze_stack,0,,,,,,,,,기본공격적중시,,,일반 공격 적중도 동결 중첩,
im_06,icicle_mark,6,피해,상대,25197,1,1,0,,0,쌓인 냉기가 폭발해 {target}이 동결! {damage}의 피해!,,,,,,,,자원최대치도달시,,ice_freeze_stack,"동결 25중첩 대미지 41,995",
im_07,icicle_mark,7,자원소모,자신,0,1,1,0,ice_freeze_stack,0,,,,,,,,전부,자원최대치도달시,,ice_freeze_stack,동결 후 중첩 초기화,
cmpi_01,combat_mastery_protection_ice_mage,1,받는피해감소,자신,0,1,1,0,ice_mastery_guard,0,전투 숙련: 수호로 받는 피해가 감소합니다.,,,,,,,,전투시작,,,,
cmpi_02,combat_mastery_protection_ice_mage,2,멀티히트피해증가,자신,0,1,1,0,ice_mastery_multi,0,전투 숙련: 수호로 다단 공격 피해가 증가합니다.,,,,,,,,전투시작,,,,
sf_01,scattered_frost,1,받는피해감소,자신,0,1,1,0,ice_scattered_guard,0,흩날리는 서리가 받는 피해를 줄입니다.,,,,,,,,전투시작,,,,
sf_02,scattered_frost,2,주는피해증가,자신,0,1,1,0,ice_scattered_power,0,,,,,,,,,전투시작,,,중첩자원ID=ice_scattered_stack으로 중첩당 +4%,
sf_03,scattered_frost,3,받는피해감소,자신,0,1,1,0,ice_scattered_defense,0,,,,,,,,,전투시작,,,중첩당 방어력 +10%를 받는 피해 -2%로 근사,
sf_04,scattered_frost,4,자원증가,자신,2,1,1,0,ice_scattered_stack,0,흩뿌린 서리로 방어를 굳힙니다.,,,,,,,,스킬사용완료시,ice_spikes,,아이스 스파이크 서리 2칸 소모,
sf_05,scattered_frost,5,자원증가,자신,1,1,1,0,ice_scattered_stack,0,흩뿌린 서리로 방어를 굳힙니다.,,,,,,,,자원획득시,,ice_veil_shield,베일 오브 윈터 서리 1칸 소모,
pc_01,piercing_cold,1,지속피해,상대,503,1,1,2,ice_chill,0,빙결,,,,,,,,스킬적중완료시,,,"빙결 지속 대미지 839. 단일/연타 확률(100%/25%)과 강타 시 추가 빙결 1,959는 배틀에 강타·연타 구분이 없어 스킬 적중마다 839로 단순화. 지속 시간 미기재라 2턴",
nw_ce_hit,guide_of_north_wind,1,피해,상대,8399,1,1,0,,0,북풍의 냉기가 {target}을 휩쓸어 {damage}의 피해!,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,crystal_edge,,"북풍의 인도자 추가 공격 13,998",
nw_ce_frost,guide_of_north_wind,2,자원증가,자신,50,1,1,0,ice_frost,0,,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,crystal_edge,,서리 50,
nw_ce_use,guide_of_north_wind,3,자원소모,자신,1,1,1,0,ice_north_wind,0,,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,crystal_edge,,추가 공격 1회 소모,
nw_ce_sign,guide_of_north_wind,4,자원증가,자신,2,1,1,0,ice_north_sign,0,,,효과미발동,nw_ce_hit,,,,,스킬적중완료시,crystal_edge,,"북풍의 인도자가 아닐 때 기호 +2(인게임 빠른 타격을 적중당 2로 환산, 동결 중첩과 같은 방식)",
nw_gc_hit,guide_of_north_wind,5,피해,상대,8399,1,1,0,,0,북풍의 냉기가 {target}을 휩쓸어 {damage}의 피해!,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,glacier_cutter,,"북풍의 인도자 추가 공격 13,998",
nw_gc_frost,guide_of_north_wind,6,자원증가,자신,50,1,1,0,ice_frost,0,,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,glacier_cutter,,서리 50,
nw_gc_use,guide_of_north_wind,7,자원소모,자신,1,1,1,0,ice_north_wind,0,,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,glacier_cutter,,추가 공격 1회 소모,
nw_gc_sign,guide_of_north_wind,8,자원증가,자신,2,1,1,0,ice_north_sign,0,,,효과미발동,nw_gc_hit,,,,,스킬적중완료시,glacier_cutter,,"북풍의 인도자가 아닐 때 기호 +2(인게임 빠른 타격을 적중당 2로 환산, 동결 중첩과 같은 방식)",
nw_is_hit,guide_of_north_wind,9,피해,상대,8399,1,1,0,,0,북풍의 냉기가 {target}을 휩쓸어 {damage}의 피해!,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,icicle_shatter,,"북풍의 인도자 추가 공격 13,998",
nw_is_frost,guide_of_north_wind,10,자원증가,자신,50,1,1,0,ice_frost,0,,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,icicle_shatter,,서리 50,
nw_is_use,guide_of_north_wind,11,자원소모,자신,1,1,1,0,ice_north_wind,0,,자신,자원보유,ice_north_wind,>=,1,,,스킬적중완료시,icicle_shatter,,추가 공격 1회 소모,
nw_is_sign,guide_of_north_wind,12,자원증가,자신,2,1,1,0,ice_north_sign,0,,,효과미발동,nw_is_hit,,,,,스킬적중완료시,icicle_shatter,,"북풍의 인도자가 아닐 때 기호 +2(인게임 빠른 타격을 적중당 2로 환산, 동결 중첩과 같은 방식)",
nw_na_hit,guide_of_north_wind,13,피해,상대,8399,1,1,0,,0,북풍의 냉기가 {target}을 휩쓸어 {damage}의 피해!,자신,자원보유,ice_north_wind,>=,1,,,기본공격적중시,,,"북풍의 인도자 추가 공격 13,998",
nw_na_frost,guide_of_north_wind,14,자원증가,자신,50,1,1,0,ice_frost,0,,자신,자원보유,ice_north_wind,>=,1,,,기본공격적중시,,,서리 50,
nw_na_use,guide_of_north_wind,15,자원소모,자신,1,1,1,0,ice_north_wind,0,,자신,자원보유,ice_north_wind,>=,1,,,기본공격적중시,,,추가 공격 1회 소모,
nw_na_sign,guide_of_north_wind,16,자원증가,자신,2,1,1,0,ice_north_sign,0,,,효과미발동,nw_na_hit,,,,,기본공격적중시,,,"북풍의 인도자가 아닐 때 기호 +2(인게임 빠른 타격을 적중당 2로 환산, 동결 중첩과 같은 방식)",
nw_ready,guide_of_north_wind,17,자원설정,자신,6,1,1,0,ice_north_wind,0,북풍의 기호가 가득 차 혹한의 바람이 칼날에 휘감깁니다!,,,,,,,,자원최대치도달시,,ice_north_sign,"필요 중첩 6, 추가 공격 6회(40초→7턴)",
nw_reset,guide_of_north_wind,18,자원소모,자신,0,1,1,0,ice_north_sign,0,,,,,,,,전부,자원최대치도달시,,ice_north_sign,,
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
ice_frost,서리,자원,900,0,0,가산,TRUE,"빙결술사 서리(인게임 6칸, 칸당 150). 스킬 적중으로 쌓이고 아이스 스파이크(300)·베일 오브 윈터(150)가 소모한다. 앱솔루트 제로가 최대로 회복한다.",
ice_edge_stage,크리스탈 엣지 연속,자원,3,0,3,가산,TRUE,"크리스탈 엣지 연속 사용 단계. 사용할 때마다 +1, 3회째에 초기화. 3턴 안에 다시 쓰지 않으면 사라진다(원본 '일정 시간 내 최대 3회 재사용').",TRUE
ice_spike_shield,아이스 스파이크,보호막,0,0,3,교체,TRUE,"아이스 스파이크 보호막(화면 흡수량 9,290, 15초→3턴).",
ice_zero_shield,냉기 보호막,보호막,0,0,2,교체,TRUE,"앱솔루트 제로 보호막(흡수량 미기재, 9,290 가정, 2턴).",
ice_veil_shield,베일 오브 윈터,보호막,0,0,0,가산,TRUE,"베일 오브 윈터 보호막(피격 시 서리 150으로 1,997).",
ice_freeze_stack,동결,중첩,25,0,0,가산,TRUE,아이시클 마크 동결 중첩. 스킬·일반 공격 적중마다 +5(인게임 타격마다 1)이고 25에 도달하면 동결 피해 후 초기화.,
ice_scattered_stack,흩날리는 서리,중첩,6,0,2,개별,TRUE,서리를 소모한 칸 수만큼 쌓이는 중첩(10초→중첩마다 2턴).,
ice_north_sign,북풍의 기호,중첩,6,0,0,가산,TRUE,크리스탈 엣지·글래시어 커터·아이시클 섀터·일반 공격 적중마다 +2(인게임 타격마다 1). 6이면 북풍의 인도자.,
ice_north_wind,북풍의 인도자,자원,6,0,7,가산,TRUE,"북풍의 인도자 추가 공격 횟수(6회, 40초→7턴).",
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
break_broken,브레이크,브레이크|받는피해증가,0.2,브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외),,,,,,,,
break_extended,브레이크 익스텐드,브레이크|받는피해증가,0.3,브레이크 익스텐드(아이시클 섀터). 브레이크를 대신해 행동 불가를 브레이크 지속만큼 더 주고 무방비 대미지 130%(받는 피해 +30%). 모든 브레이크 타입 공통인 무방비만 반영,,,,,,,,
ice_spikes_power,아이스 스파이크,주는피해증가,0.2,아이스 스파이크의 공격력 증가 20%(3턴).,,,,,,,,
ice_field_guard,프리징 필드,받는피해감소,0.05,얼음 지대 안에서 받는 대미지 감소 10%를 밸런스 보정으로 5%.,,,,,,,,
ice_field_chill,냉기 취약,받는피해증가,0.1,얼음 지대 안의 적이 받는 [시너지] 대미지 증가 10%.,,,받는피해증가,,,,,
ice_field_frost,얼음 지대,없음,0,프리징 필드의 주기 냉기 피해.,,,,,,,,
ice_zero_field,강화된 얼음 지대,없음,0,앱솔루트 제로의 강화된 프리징 필드 주기 피해.,,,,,,,,
ice_zero_guard,앱솔루트 제로,받는피해감소,0.5,앱솔루트 제로 후 받는 대미지 감소 50%(10초→2턴).,,,,,,,,
ice_mark,마크 오브 아이시클,없음,0,아이시클 마크의 약화 효과. 주기 피해.,,,,,,,,
ice_chill,빙결,없음,0,사무치는 냉기의 지속 피해: 빙결.,,,,,,,,
ice_mastery_guard,전투 숙련: 수호,받는피해감소,0.1,모든 공격으로부터 받는 대미지 감소 15%를 밸런스 보정으로 10%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,,,
ice_mastery_multi,전투 숙련: 수호,멀티히트피해증가,0.03,적에게 주는 멀티히트(다단) 피해 +3%,,,,,,,,
ice_scattered_guard,흩날리는 서리,받는피해감소,0.05,받는 대미지 감소 10%(상시)를 밸런스 보정으로 5%.,,,,,,,,
ice_scattered_power,흩날리는 서리: 공격,주는피해증가,0.04,서리 소모 중첩당 주는 피해 +4%.,,ice_scattered_stack,,,,,,TRUE
ice_scattered_defense,흩날리는 서리: 방어,받는피해감소,0.02,서리 소모 중첩당 방어력 +10%. 방어력 비중이 작아 받는 피해 -2%로 근사.,,ice_scattered_stack,,,,,,TRUE
"""",
    };
}
