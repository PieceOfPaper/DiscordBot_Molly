using Molly.Battle;

/// <summary>
/// 대검전사의 검 자세(앞·뒤)와 전투 템포, 어깨 치기↔발구르기, 대검술 템포별 파생(대지 휩쓸기·심장 찌르기·바람 가르기), 바람 가르기 추가 사용,
/// 필사의 일격, 불굴·보복·난투·뭉개진 상처 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-10-03 배틀 시트에서 대검전사 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 대검전사 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·배틀스킬AI·돌발 이벤트·생활스킬 시트는 석궁사수 사본을 그대로 쓴다(AI 가산 없이 같은 가중치면 마지막 후보가 뽑힌다).
/// </summary>
internal static class GreatswordWarriorBattleTests
{
    private static double Variance(double random) => .9 + random * .2;
    private static int Hit(double shownDamage, double outgoing = 1.05, double incoming = 1, double random = .9) => (int)Math.Round((shownDamage * .25 - 200 * .5) * outgoing * incoming * Variance(random));

    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow);
        GripTests(data);
        ShoulderBashTests(data);
        GreatswordArtTests(data);
        WindCleaveTests(data);
        RetaliationTests(data);
    }

    private static void GripTests(BattleDataSnapshot data)
    {
        // 전투는 검을 뒤로 쥔 자세로 시작한다. 가르기는 뒤로 쥔 자세에서 템포를 얻고 검을 앞으로 쥐므로, 바로 다시 쓰면 템포가 없다.
        var cleave = Turns(Duel(data, ["cleave"], maxActions: 4));
        Assert(cleave[0].Skill == "가르기" && cleave[0].Damage == Hit(7413) && cleave[0].Resources.Contains("전투 템포 +1 (현재 1)") && cleave[0].Statuses.Contains("상처"),
            "가르기는 검을 뒤로 쥔 자세에서 템포를 얻고(전투 숙련: 패기 +5%), 적중하면 뭉개진 상처를 남긴다");
        Assert(cleave[1].Skill == "가르기" && !cleave[1].Resources.Any(x => x.StartsWith("전투 템포", StringComparison.Ordinal)),
            "가르기 뒤에는 검을 앞으로 쥐어 다시 쓴 가르기는 템포를 얻지 못한다");

        // 회전 베기는 사용 후에도 검을 뒤로 쥐어 계속 템포를 얻고, 불굴로 다음 행동까지 방어력 +150%다.
        var spin = Duel(data, ["spinning_slash"], maxActions: 4);
        var spinTurns = Turns(spin);
        Assert(spinTurns[0].Skill == "회전 베기" && spinTurns[0].Damage == Hit(10589) && spinTurns[0].Resources.Contains("전투 템포 +1 (현재 1)") && spinTurns[0].Statuses.Contains("불굴")
            && spinTurns[1].Resources.Contains("전투 템포 +1 (현재 2)"),
            "회전 베기는 사용 후 검을 뒤로 쥐어 연달아 템포를 얻고 불굴을 얻는다");
        Assert(spin.Events.First(x => x.Type == "DamageDealt" && x.Actor == "B").Amount == (int)Math.Round((1500 - 200 * 2.5 * .5) * .9 * Variance(.9)),
            "불굴 중에는 방어력이 150% 늘어 상대 일반 공격 피해가 줄고, 전투 숙련: 패기로 받는 기본 공격 피해가 10% 줄어든다");

        var rising = Turns(Duel(data, ["rising_smash"], maxActions: 2, initial: [("gs_grip_back", 0), ("gs_grip_front", 1)]));
        Assert(rising[0].Skill == "라이징 스매시" && rising[0].Damage == Hit(7264) && rising[0].Resources.Contains("전투 템포 +1 (현재 1)") && rising[0].BreakGauge,
            "라이징 스매시는 검을 앞으로 쥔 자세에서 템포를 얻고 브레이크 피해를 준다");
        var risingBack = Turns(Duel(data, ["rising_smash"], maxActions: 2));
        Assert(risingBack[0].Skill == "라이징 스매시" && !risingBack[0].Resources.Any(x => x.StartsWith("전투 템포", StringComparison.Ordinal)),
            "검을 뒤로 쥔 자세의 라이징 스매시는 템포를 얻지 못한다");
    }

    private static void ShoulderBashTests(BattleDataSnapshot data)
    {
        var bash = Turns(Duel(data, ["shoulder_bash"], maxActions: 2));
        Assert(bash[0].Skill == "어깨 치기" && bash[0].Damage == Hit(8260) && !bash[0].Resources.Any(x => x.StartsWith("전투 템포", StringComparison.Ordinal)),
            "검을 뒤로 쥐고 있으면 어깨 치기가 나가고 템포는 없다");
        var stomp = Turns(Duel(data, ["shoulder_bash"], maxActions: 6, initial: [("gs_grip_back", 0), ("gs_grip_front", 1)]));
        Assert(stomp[0].Skill == "발구르기" && stomp[0].Hits == 1 && stomp[0].Damage == Hit(8260) && stomp[0].Resources.Contains("전투 템포 +1 (현재 1)"),
            "검을 앞으로 쥐고 있으면 어깨 치기가 발구르기로 바뀌어 템포를 얻는다(어깨 치기 피해는 나가지 않는다)");
        Assert(stomp[1].Skill == "(일반 공격)" && stomp[2].Skill == "어깨 치기",
            "발구르기 뒤에는 검을 뒤로 쥐어, 재사용 대기가 끝난 다음 버튼은 어깨 치기다");
    }

    private static void GreatswordArtTests(BattleDataSnapshot data)
    {
        var none = Turns(Duel(data, ["greatsword_art"], maxActions: 2));
        Assert(none[0].Skill == "(일반 공격)", "전투 템포가 없으면 대검술을 쓸 수 없다");
        var earth = Turns(Duel(data, ["greatsword_art"], maxActions: 2, initial: [("gs_tempo", 1)]));
        Assert(earth[0].Skill == "대지 휩쓸기" && earth[0].Damage == Hit(12602) && earth[0].Resources.Contains("전투 템포 -1 (현재 0)"),
            "템포 1의 대검술은 대지 휩쓸기로 템포를 모두 쓴다");
        var heart = Duel(data, ["greatsword_art"], maxActions: 3, initial: [("gs_tempo", 2)]);
        var heartTurns = Turns(heart);
        Assert(heartTurns[0].Skill == "심장 찌르기" && heartTurns[0].Damage == Hit(12602) && heartTurns[0].Statuses.Contains("파열") && heartTurns[0].Resources.Contains("전투 템포 -2 (현재 0)"),
            "템포 2의 대검술은 심장 찌르기로 파열을 남긴다");
        Assert(heart.Events.Any(x => x.Type == "StatusDamage" && x.Target == "B" && x.Detail == "파열" && x.Amount == (int)Math.Round((1260 * .25 - 100) * 1.1)),
            "파열은 상대 턴마다 지속 피해를 주고 [시너지] 받는 피해 +10%가 그 피해에도 붙는다");
        var wind = Turns(Duel(data, ["greatsword_art"], maxActions: 2, initial: [("gs_tempo", 3)]));
        Assert(wind[0].Skill == "바람 가르기" && wind[0].Damage == Hit(20756) && wind[0].Resources.Contains("전투 템포 -3 (현재 0)"),
            "템포 3의 대검술은 바람 가르기다");
    }

    private static void WindCleaveTests(BattleDataSnapshot data)
    {
        // 라이징 스매시로 브레이크(2턴)를 건 뒤 바람 가르기 → 무방비 대상이라 다음 행동이 추가 바람 가르기. 추가 바람 가르기는 상대가 아직 브레이크여도 다시 발동하지 않는다.
        var broken = Turns(Duel(data, ["greatsword_art", "rising_smash"], maxActions: 8, initial: [("gs_tempo", 2), ("gs_grip_back", 0), ("gs_grip_front", 1)],
            rules: [("break_gauge_maximum", "1"), ("break_duration_turns", "2")]));
        Assert(broken[0].Skill == "라이징 스매시" && broken[0].Broken && broken[1].Skill == "바람 가르기" && broken[1].Damage == Hit(20756, incoming: 1.2)
            && broken[2].Skill == "바람 가르기" && broken[2].Damage == Hit(20756, incoming: 1.2) && !broken[2].Resources.Any(x => x.StartsWith("전투 템포", StringComparison.Ordinal))
            && broken[3].Skill == "라이징 스매시",
            "브레이크된 적을 바람 가르기로 베면 템포 없이 바람 가르기를 한 번 더 쓰고, 그 바람 가르기는 다시 발동하지 않는다");

        // 심장 찌르기 → 필사의 일격(템포 최대·맹공·공격력 +50%) → 바람 가르기(파열 대상 35%) → 추가 바람 가르기. 난수 0.3이면 치명타·추가타·맹공은 없고 35%는 발동한다.
        var ruptured = Turns(Duel(data, ["greatsword_art", "desperate_strike"], maxActions: 10, random: .3, initial: [("gs_tempo", 2), ("ultimate_gauge", 300)]));
        Assert(ruptured[0].Skill == "심장 찌르기" && ruptured[1].Skill == "필사의 일격" && ruptured[1].Damage == Hit(32764, incoming: 1.1, random: .3)
            && ruptured[1].Resources.Contains("전투 템포 +3 (현재 3)") && ruptured[1].Statuses.Contains("맹공") && ruptured[1].Statuses.Contains("필사의 일격"),
            "필사의 일격은 큰 피해를 주고 템포를 최대로 채우며 맹공과 공격력 증가를 얻는다");
        Assert(ruptured[2].Skill == "바람 가르기" && ruptured[2].Damage == Hit(20756, 1.55, 1.1, .3) && ruptured[3].Skill == "바람 가르기" && ruptured[3].Damage == Hit(20756, 1.55, 1.1, .3)
            && ruptured[4].Skill == "(일반 공격)",
            "파열 상태인 적을 바람 가르기로 베면 35% 확률로 한 번 더 쓰고, 필사의 일격 공격력 +50%는 2턴 동안 붙는다");
    }

    private static void RetaliationTests(BattleDataSnapshot data)
    {
        // 최대 체력 8,000: 상대 일반 공격 1,361(받는 기본 공격 -10%)은 10%(800)를 넘어 보복 1중첩. 다음 가르기는 +5%, 난투가 중첩당 840 × 0.25를 회복한다.
        var result = Duel(data, ["cleave"], maxActions: 3, rules: [("base_max_hp", "8000")]);
        var turns = Turns(result);
        Assert(result.Events.Any(x => x.Type == "DamageDealt" && x.Actor == "B" && x.Amount == 1361) && result.Events.Any(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail == "보복 +1 (현재 1)"),
            "체력을 최대 체력의 10%만큼 잃으면 보복 1중첩을 얻는다");
        Assert(turns[1].Damage == Hit(7413, 1.10) && result.Events.Any(x => x.Type == "HealApplied" && x.Actor == "A" && x.Amount == 210),
            "보복 중첩당 주는 피해 +5%, 난투가 공격 적중 시 중첩당 체력을 회복한다");
        Assert(result.Events.Any(x => x.Type == "StatusDamage" && x.Target == "B" && x.Detail == "상처" && x.Amount == (int)Math.Round((6177 * .25 - 100) * (1 + 1.43 * turns[0].Damage / 8000d))),
            "뭉개진 상처는 상대가 잃은 체력에 비례해 지속 피해가 커진다");
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses, int Hits, bool Broken, bool BreakGauge);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, (string Id, int Value)[]? initial = null, (string Id, string Value)[]? rules = null)
    {
        var ruleMap = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", maxActions.ToString()) }.Concat(rules ?? [])) ruleMap[id] = ruleMap[id] with { Value = value };
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in initial ?? []) resources[id] = resources[id] with { InitialValue = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = ruleMap,
            Classes = new Dictionary<string, BattleClass>
            {
                ["greatsword_warrior"] = data.Classes["greatsword_warrior"] with { SkillIds = skills },
                ["idle"] = new("idle", "대상", Array.Empty<string>())
            },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, SkillAiRules = data.SkillAiRules, LoadedAt = data.LoadedAt
        };
        // 대검전사가 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "greatsword_warrior", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
    }

    /// <summary>A의 행동별로 사용 스킬·A가 준 피해 합계·A의 자원 변화·새로 걸린 상태·타격 수·브레이크 발동·브레이크 게이지 변화 여부를 모은다.</summary>
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
                    current.Any(x => x.Type == "BreakActivated"),
                    current.Any(x => x.Type == "BreakGaugeChanged" && x.Target == "B")));
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
greatsword_warrior,대검전사,전사,"대검을 휘둘러서 다수의 적을 단번에 휩쓸어버리는 클래스. 지치지 않는 공격으로 적들을 거칠게 몰아붙인 후 전력을 다해 분쇄한다. 두꺼운 갑옷을 선호하며, 폭발할 듯한 힘을 대검에 쏟아부어서 강력한 일격을 날린다.",spinning_slash,rising_smash,shoulder_bash,cleave,greatsword_art,desperate_strike,onslaught,indomitable,combat_mastery_might_greatsword,retaliation_greatsword,brawl,crushed_wound
"""",
        ["스킬"] = """"
ID,이름,스킬구분,부모스킬ID,설명,태그,기타
spinning_slash,회전 베기,일반,,"주위에 몰려든 적들을 제압하는 신속한 일격. 간결한 동작만으로 반경 안의 모든 적을 베어낸다. 검을 뒤로 쥘 때, 더 빠르게 공격 가능하며 전투 템포를 높인다.","강타, 보조",등급: 에픽; 강화 레벨: +24; 대미지: 25213; 재사용 대기 시간: 4초; 사거리: 3.2m; 범위: 4m
rising_smash,라이징 스매시,일반,,"다수의 적을 한꺼번에 띄우는 묵직한 베기 공격. 대검에 강력한 힘을 쏟아부어 올려치며 베고, 적들을 넘어뜨린다. 검을 앞으로 쥘 때, 더 빠르게 공격 가능한 동시에 전투 템포를 높인다.","강타, 방해",등급: 고급; 강화 레벨: +8; 대미지: 17296; 브레이크 대미지: 1칸; 재사용 대기 시간: 12초; 사거리: 3.2m; 범위: 4.5m
shoulder_bash,어깨 치기,일반,,"맹렬한 기세로 돌진해 적을 밀쳐내는 육탄 공격. 순식간에 타겟과의 거리를 좁히며 피해를 준다. 단, 검을 앞으로 쥐고 있는 동안은 발구르기 공격으로 전환된다.","강타, 이동",등급: 에픽; 강화 레벨: +24; 대미지: 19666; 재사용 대기 시간: 7.5초; 사거리: 15m
stomp,발구르기,파생,shoulder_bash,"땅을 흔들어 적의 움직임을 가로막는 제압 공격. 발로 지면을 힘껏 차 충격파를 생성해 주변 적들에게 범위 피해를 주고, 전투 템포를 높인다. 단, 검을 뒤로 쥐고 있는 동안은 어깨치기 공격으로 전환된다.","강타, 보조, 방해",등급: 에픽; 강화 레벨: +24; 대미지: 19666; 재사용 대기 시간: 7.5초; 사거리: 4m; 범위: 5m; 전환 조건: 검을 앞으로 쥔 상태
cleave,가르기,일반,,"육중한 대검으로 적들을 압도하는 베기 공격. 높이 들어 올린 대검에 온몸의 무게를 힘껏 실어 수직으로 내려 벤다. 검을 뒤로 쥘 때, 더 빠르게 공격 가능한 동시에 전투 템포를 높인다.","강타, 보조",등급: 에픽; 강화 레벨: +24; 대미지: 17649; 재사용 대기 시간: 3.5초; 사거리: 3.2m
greatsword_art,대검술,일반,,대검에 온 힘을 실어 날리는 필살의 일격. 전투 템포를 전부 소모하여 강력한 일격을 가한다. 차징할 수 있으며 차징 단계에 비례하여 피해량이 증가한다.,"강타, 보조","등급: 고급; 강화 레벨: +8; 전투 템포에 따라 대지 휩쓸기(1), 심장 찌르기(2), 바람 가르기(3) 사용 가능; 각 파생 스킬의 차지 단계별 수치 참고"
earth_sweep,대지 휩쓸기,파생,greatsword_art,우레와 같은 기세로 땅을 뒤엎는 무거운 일격. 지면을 뒤집어 다수의 적을 전방으로 밀쳐내는 동시에 튀어 오른 파편을 날려 범위 피해를 준다. 차징 단계에 비례하여 피해량이 증가한다. 전투 템포가 1에 이르면 사용할 수 있다.,"강타, 보조",등급: 고급; 강화 레벨: +8; 대미지: 15884 / 22944 / 30004; 단계별 차지 시간: 1.05초; 전투 템포 소모: 1; 사거리: 3.2m; 범위: 6m
heart_pierce,심장 찌르기,파생,greatsword_art,예리한 검기로 적의 급소를 꿰뚫는 신중한 일격. 검 끝으로 타겟을 찔러 타겟에게 지속 피해와 함께 받는 피해를 증가시키는 약화 효과: 파열을 남긴다. 차징 단계에 비례하여 지속 시간과 초기 피해량이 증가한다. 전투 템포가 2에 이르면 사용할 수 있다.,"강타, 보조",등급: 고급; 강화 레벨: +8; 대미지: 15884 / 22944 / 30004; 지속 대미지: 1초마다 3000; [시너지] 받는 대미지 증가: 10%; 단계별 차지 시간: 1.05초; 차지 단계별 지속 시간: 30초 / 60초 / 90초; 전투 템포 소모: 2; 사거리: 3.2m
wind_cleave,바람 가르기,파생,greatsword_art,"휘몰아치는 돌풍처럼 적들을 파괴하는 거센 검격. 검의 궤적에 맞물린 바람이 주변 적들을 휩쓸어 강한 범위 피해를 주며, 차징 단계에 비례하여 피해량이 증가한다. 전투 템포가 3에 이르면 사용할 수 있다. 바람 가르기로 무방비한 적을 공격하거나 심장 찌르기 효과를 보유한 적을 공격할 경우, 지정된 확률로 전투 템포와 무관하게 바람 가르기를 차지 없이 2단계 위력으로 한 번 사용할 수 있다. 이 효과로 사용한 바람 가르기는 동일한 효과를 다시 발생시키지 않는다.","강타, 보조",등급: 고급; 강화 레벨: +8; 대미지: 28239 / 38828 / 49418; 단계별 차지 시간: 1.05초; 전투 템포 소모: 3; 무방비 대상 공격 시: 100% 확률로 발동; 심장 찌르기 보유 대상 공격 시: 35% 확률로 발동; 사거리: 3.2m; 범위: 4m
desperate_strike,필사의 일격,궁극기,,"무기의 저력을 극한까지 추구한 대검의 경지. 거대한 대검의 관성을 발판 삼아 높이 도약 후, 폭발적인 힘을 실어 내려꽂아 지면을 산산조각 내고 주변 적들에게 큰 피해를 준다. 추가로 자신의 전투 템포를 최대치로 올리고 강화 효과: 맹공을 얻고, 공격력이 증가한다. 『대지의 방랑자: 대검 한 자루에 몸을 싣고』","궁극기, 강타, 보조",등급: 고급; 강화 레벨: +8; 엠블럼 장착 필요; 대미지: 78010; 궁극기 비용: 300; 사거리: 6m; 공격력 증가: 50%; 범위: 8m
"""",
        ["패시브스킬"] = """"
ID,이름,설명,기타
onslaught,맹공,"지칠 새 없이 거세게 공격을 퍼붓는 날선 의지. 공격 적중 시, 일정 확률로 스킬 사용 속도를 높이는 강화 효과: 맹공을 얻는다.",발동 확률: 20%; 캐스팅 속도 증가: 50%; 스킬 발동 속도 증가: 50%; 적용 횟수: 2
indomitable,불굴,어떠한 공격이라도 단단히 버텨내는 굳센 의지. 스킬을 준비 중이면 방어력 증가와 함께 적에게 브레이크 당하지 않는다.,방어력 증가: 150%
combat_mastery_might_greatsword,전투 숙련: 패기,"근거리에서 공격을 수행하는 숙련된 전투 기법. 기본 공격으로 받는 피해가 감소하고, 적에게 주는 강타 피해가 증가한다. 대검전사 클래스 레벨이 30에 도달하면 전투 시 클래스 특화 어시스트를 사용할 수 있다.",받는 기본 공격 대미지 감소: 10%; 강타 대미지 증가: 5%; 클래스 특화 어시스트 해금: 대검전사 클래스 레벨 30
retaliation_greatsword,보복,"몰아치는 고통속에서 피어나는 강렬한 의지. 체력을 일정량 소모할 때마다, 잠시 동안 공격력이 증가한다.",발동을 위한 소모 체력: 10%; 중첩당 공격력 증가: 5%; 최대 중첩 수: 100; 지속 시간: 15초
brawl,난투,"잠시 동안 고통에서 벗어나는 굳건한 의지. 공격 적중 시, 중첩된 보복 효과의 수에 비례하여 체력을 회복한다.",보복 중첩 당 체력 회복: 1999
crushed_wound,뭉개진 상처,대검의 무게를 이용하여 벌어진 상처를 더 크게 벌어지게 하는 기술. 공격 적중 시 지속 피해: 상처를 부여한다. 적의 체력이 낮을수록 피해량이 증가한다.,"태그: 보조, 방해; 해금 조건: 대검전사 Lv.60 이상; 최소 대미지: 29415; 최대 대미지: 58831; 최대 대미지 적용 체력: 30%"
"""",
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고,궁극기문구
spinning_slash,TRUE,공격,상대,1,0,60,궁극기 게이지,,150,FALSE,"검을 뒤로 쥔 자세에서 빠르게 베어 전투 템포를 얻는다. 사용 후 검을 뒤로 쥐고, 쓰는 동안 불굴로 버틴다.",{caster}가 대검을 크게 휘둘러 {target}을 베어냅니다!,"원본 재사용 대기 4초→1턴, 대미지 25,213. 검을 뒤로 쥔 자세면 템포 +1, 사용 후 검을 뒤로 쥔다(나무위키 자세 설명). 피해 원본 ×0.42. 주변 적은 1:1이라 제외, 빠른 시전은 턴제에 시전 시간이 없어 제외",
rising_smash,TRUE,공격·방해,상대,2,0,60,궁극기 게이지,,150,FALSE,검을 앞으로 쥔 자세에서 올려 베어 넘어뜨리고 전투 템포를 얻는다. 사용 후 검을 뒤로 쥔다.,{caster}가 대검에 힘을 쏟아부어 {target}을 올려칩니다!,"원본 재사용 대기 12초→2턴, 대미지 17,296, 브레이크 1칸(넘어뜨림). 검을 앞으로 쥔 자세면 템포 +1, 사용 후 검을 뒤로 쥔다. 피해 원본 ×0.42",
shoulder_bash,TRUE,공격,상대,2,0,55,궁극기 게이지,,150,FALSE,돌진해 어깨로 밀친다. 검을 앞으로 쥐고 있으면 발구르기로 바뀐다.,{caster}가 맹렬히 돌진해 {target}을 어깨로 들이받습니다!,"원본 재사용 대기 7.5초→2턴, 대미지 19,666. 검을 뒤로 쥔 자세에서 쓰며 템포·자세 변화 없음(원본에 템포 언급 없음, 자세 유지는 가정). 검을 앞으로 쥐고 있으면 즉시 파생 shoulder_bash_stomp로 발구르기. 피해 원본 ×0.42",
stomp,TRUE,공격·방해,상대,2,0,0,,,0,FALSE,검을 앞으로 쥔 채 땅을 굴러 충격파를 일으키고 전투 템포를 얻는다. 사용 후 검을 뒤로 쥔다.,{caster}가 발로 지면을 힘껏 차 {target}에게 충격파를 보냅니다!,"파생 전용. 검을 앞으로 쥐고 있을 때 어깨 치기 대신 실행. 대미지 19,666, 템포 +1, 사용 후 검을 뒤로 쥔다(딜 사이클 1-4-3-5 기준). 움직임 가로막기는 1:1 이동이 없어 제외. 피해 원본 ×0.42",
cleave,TRUE,공격,상대,1,0,60,궁극기 게이지,,150,FALSE,검을 뒤로 쥔 자세에서 대검을 내려 베어 전투 템포를 얻는다. 사용 후 검을 앞으로 쥔다.,{caster}가 높이 들어 올린 대검을 {target}에게 수직으로 내려칩니다!,"원본 재사용 대기 3.5초→1턴, 대미지 17,649. 검을 뒤로 쥔 자세면 템포 +1, 사용 후 검을 앞으로 쥔다. 피해 원본 ×0.42",
greatsword_art,TRUE,공격·파생,상대,1,0,60,궁극기 게이지,,150,FALSE,전투 템포를 모두 써서 템포에 따라 대지 휩쓸기·심장 찌르기·바람 가르기를 최대 차지로 날린다.,{caster}가 대검에 온 힘을 싣습니다!,자체 효과 없음. 템포 3·2·1에 따라 배틀스킬파생(대체)으로 바람 가르기·심장 찌르기·대지 휩쓸기 중 하나. 템포가 없으면 쓸 수 없다. 차지는 단계별 1.05초라 자동전투에서는 항상 최대 차지로 본다. 재사용 대기 시간이 원본에 없어 최소 1턴,
earth_sweep,TRUE,공격,상대,1,0,0,,,0,FALSE,템포 1로 땅을 뒤엎는 무거운 일격을 날린다.,{caster}가 대검으로 땅을 뒤엎어 {target}에게 파편을 날립니다!,"파생 전용(템포 1). 최대 차지 대미지 15,884/22,944/30,004 중 30,004, 템포 전부 소모. 밀쳐내기는 다른 클래스와 같이 생략. 피해 원본 ×0.42",
heart_pierce,TRUE,공격·방해,상대,1,0,0,,,0,FALSE,템포 2로 급소를 꿰뚫어 지속 피해와 받는 피해 증가를 주는 파열을 남긴다.,{caster}가 날카로운 검 끝으로 {target}의 급소를 꿰뚫습니다!,"파생 전용(템포 2). 최대 차지 대미지 30,004, 파열 1초마다 3,000을 턴당 1회로 보정, 최대 차지 지속 90초→15턴, [시너지] 받는 대미지 +10%. 템포 전부 소모. 피해 원본 ×0.42",
wind_cleave,TRUE,공격,상대,1,0,0,,,0,FALSE,템포 3으로 돌풍 같은 검격을 날린다. 브레이크된 적이나 파열 상태인 적을 베면 바람 가르기를 한 번 더 쓴다.,{caster}의 대검이 돌풍을 일으키며 {target}을 가릅니다!,"파생 전용(템포 3). 최대 차지 대미지 49,418, 템포 전부 소모. 무방비(브레이크) 대상 100%·심장 찌르기(파열) 대상 35%로 다음 행동이 템포 없는 바람 가르기(2단계 위력 = 최대 차지 수치)가 되고, 그 바람 가르기는 다시 발동하지 않는다(gs_wind_free). 추가 사용은 다음 행동을 대체하는 다음행동지정으로 단순화. 피해 원본 ×0.42",
desperate_strike,TRUE,궁극기,자신·상대,0,0,100,궁극기 게이지,300,,TRUE,"높이 도약해 대검을 내리꽂아 큰 피해를 주고, 전투 템포를 가득 채우며 맹공과 공격력 증가를 얻는다.",{caster}가 대검의 관성을 발판 삼아 높이 도약합니다!,"원본 대미지 78,010, 템포 최대(3), 맹공(스킬 사용 속도 +50%, 2회 → 2턴 동안 쿨다운 1 추가 감소), 공격력 +50%(지속 시간이 원본에 없어 2턴 가정). 피해 원본 ×0.42. 엠블럼 장착 필요는 사용 조건이 아니라 제외",『대지의 방랑자: 대검 한 자루에 몸을 싣고』
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
shoulder_bash_stomp,shoulder_bash,stomp,대체,1,1,자원보유,gs_grip_front>=1,FALSE,즉시,검을 앞으로 쥐고 있으면 어깨 치기가 발구르기로 바뀐다(인게임 버튼 전환),100
greatsword_art_wind,greatsword_art,wind_cleave,대체,1,1,자원보유,gs_tempo>=3,FALSE,즉시,전투 템포 3이면 바람 가르기,300
greatsword_art_heart,greatsword_art,heart_pierce,대체,1,1,자원보유,gs_tempo>=2,FALSE,즉시,전투 템포 2면 심장 찌르기,200
greatsword_art_earth,greatsword_art,earth_sweep,대체,1,1,자원보유,gs_tempo>=1,FALSE,즉시,전투 템포 1이면 대지 휩쓸기,100
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
gss_01,spinning_slash,1,피해,상대,,10589,1,1,0,,0,,"원본 25,213",,,,,,,,
gss_02,spinning_slash,2,자원증가,자신,,1,1,1,0,gs_tempo,0,,검을 뒤로 쥔 자세면 전투 템포 +1,자신,자원보유,gs_grip_back,>=,1,,,
gss_03,spinning_slash,3,자원설정,자신,,1,1,1,0,gs_grip_back,0,,사용 후 검을 뒤로 쥔다,,,,,,,,
grs_01,rising_smash,1,피해,상대,,7264,1,1,0,,0,,"원본 17,296",,,,,,,,
grs_02,rising_smash,2,브레이크피해,상대,,1,1,1,0,,0,,브레이크 대미지 1칸(넘어뜨림),,,,,,,,
grs_03,rising_smash,3,자원증가,자신,,1,1,1,0,gs_tempo,0,,검을 앞으로 쥔 자세면 전투 템포 +1,자신,자원보유,gs_grip_front,>=,1,,,
grs_04,rising_smash,4,자원설정,자신,,1,1,1,0,gs_grip_back,0,,사용 후 검을 뒤로 쥔다,,,,,,,,
gsb_01,shoulder_bash,1,피해,상대,,8260,1,1,0,,0,,"원본 19,666. 검을 앞으로 쥐었으면 발구르기로 대체되므로 실행하지 않는다",자신,자원보유,gs_grip_front,<=,0,,,
gst_01,stomp,1,피해,상대,,8260,1,1,0,,0,,"원본 19,666",,,,,,,,
gst_02,stomp,2,자원증가,자신,,1,1,1,0,gs_tempo,0,,발구르기는 전투 템포를 높인다(앞으로 쥔 자세에서만 나간다),,,,,,,,
gst_03,stomp,3,자원설정,자신,,1,1,1,0,gs_grip_back,0,,사용 후 검을 뒤로 쥔다(딜 사이클 1-4-3-5),,,,,,,,
gcl_01,cleave,1,피해,상대,,7413,1,1,0,,0,,"원본 17,649",,,,,,,,
gcl_02,cleave,2,자원증가,자신,,1,1,1,0,gs_tempo,0,,검을 뒤로 쥔 자세면 전투 템포 +1,자신,자원보유,gs_grip_back,>=,1,,,
gcl_03,cleave,3,자원설정,자신,,1,1,1,0,gs_grip_front,0,,사용 후 검을 앞으로 쥔다,,,,,,,,
ges_01,earth_sweep,1,피해,상대,,12602,1,1,0,,0,,"최대 차지 원본 30,004",,,,,,,,
ges_02,earth_sweep,2,자원소모,자신,,0,1,1,0,gs_tempo,0,,전투 템포를 전부 소모,,,,,,,전부,
ghp_01,heart_pierce,1,피해,상대,,12602,1,1,0,,0,,"최대 차지 원본 30,004",,,,,,,,
ghp_02,heart_pierce,2,지속피해,상대,,1260,1,1,15,gs_rupture,1,파열,"파열 1초마다 3,000을 턴당 1회로 보정, 최대 차지 지속 90초→15턴. [시너지] 받는 대미지 +10%는 상태",,,,,,,,
ghp_03,heart_pierce,3,자원소모,자신,,0,1,1,0,gs_tempo,0,,전투 템포를 전부 소모,,,,,,,전부,
gwc_01,wind_cleave,1,자원설정,자신,,1,1,1,0,gs_wind_gate,0,,무방비(브레이크) 대상 공격 시 100% 추가 바람 가르기 판정,상대,상태효과유형보유,브레이크,,,,,
gwc_02,wind_cleave,2,자원설정,자신,,1,1,0.35,0,gs_wind_gate,0,,심장 찌르기(파열) 보유 대상 공격 시 35%,상대,상태효과보유,gs_rupture,,,,,
gwc_03,wind_cleave,3,자원소모,자신,,0,1,1,0,gs_wind_gate,0,,추가로 쓴 바람 가르기는 같은 효과를 다시 일으키지 않는다,자신,자원보유,gs_wind_free,>=,1,,전부,
gwc_04,wind_cleave,4,자원소모,자신,,0,1,1,0,gs_wind_free,0,,추가 바람 가르기 표식 소모,자신,자원보유,gs_wind_free,>=,1,,전부,
gwc_05,wind_cleave,5,다음행동지정,자신,,0,1,1,0,wind_cleave,0,,템포와 무관하게 다음 행동으로 바람 가르기를 한 번 더 쓴다(차지 없이 2단계 위력),자신,자원보유,gs_wind_gate,>=,1,,,
gwc_06,wind_cleave,6,자원설정,자신,,1,1,1,0,gs_wind_free,0,,다음 바람 가르기가 추가 사용임을 표시,자신,자원보유,gs_wind_gate,>=,1,,,
gwc_07,wind_cleave,7,자원소모,자신,,0,1,1,0,gs_wind_gate,0,,판정 표식 정리,,,,,,,전부,
gwc_08,wind_cleave,8,피해,상대,,20756,1,1,0,,0,,"최대 차지(2단계) 원본 49,418",,,,,,,,
gwc_09,wind_cleave,9,자원소모,자신,,0,1,1,0,gs_tempo,0,,전투 템포를 전부 소모(추가 사용은 템포가 이미 0),,,,,,,전부,
gds_01,desperate_strike,1,피해,상대,,32764,1,1,0,,0,,"원본 78,010",,,,,,,,
gds_02,desperate_strike,2,자원설정,자신,,3,1,1,0,gs_tempo,0,,전투 템포를 최대치로,,,,,,,,
gds_03,desperate_strike,3,상태효과,자신,,0,1,1,2,gs_onslaught,1,,강화 효과: 맹공(적용 2회 → 2턴),,,,,,,,
gds_04,desperate_strike,4,상태효과,자신,,0,1,1,2,gs_desperate_power,1,,공격력 +50%. 지속 시간이 원본에 없어 맹공과 같은 2턴 가정,,,,,,,,
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
onslaught,TRUE,공격 적중 시(스킬 적중·기본 공격) 20%로 맹공. 캐스팅·스킬 발동 속도 +50% 적용 2회는 2턴 동안 쿨다운 1 추가 감소로 근사
indomitable,TRUE,"스킬을 쓰는 동안 방어력 +150%·브레이크 면역. 턴제에 시전 시간이 없어 불굴이 적용되는 스킬(회전 베기·대검술, 나무위키 기준)을 쓴 뒤 다음 행동까지(1턴) 유지로 근사"
combat_mastery_might_greatsword,TRUE,"받는 기본 공격 대미지 -10%, 강타 대미지 +5%(대검전사 스킬은 모두 강타라 주는 피해 +5%). 레벨 30 어시스트 해금은 제외"
retaliation_greatsword,TRUE,"체력을 최대 체력의 10%만큼 잃을 때마다(체력소모시) 보복 1중첩(중첩마다 15초→3턴, 최대 100), 중첩당 공격력 +5%를 주는 피해 +5%로"
brawl,TRUE,"공격 적중 시 보복 중첩당 1,999 회복(원본 × 0.42, 공격력 비례라 피해 보정과 같이 적용). 회복 후 5초 재사용 대기는 행동당 1회라 생략"
crushed_wound,TRUE,"공격 적중 시 상처. 최소 29,415(체력 가득)~최대 58,831(체력 30% 이하)을 2턴에 나눔(지속 시간 미기재라 2턴 가정), 잃은 체력 비례 증폭. 원본에 재부여 간격이 없어 4턴에 한 번(재발동대기턴 4)으로 제한. 원본 × 0.42"
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
gon_01,onslaught,1,상태효과,자신,0,1,0.2,2,gs_onslaught,0,,,,,,,,,스킬적중완료시,,,공격 적중 시 20%로 맹공(2턴),
gon_02,onslaught,2,상태효과,자신,0,1,0.2,2,gs_onslaught,0,,,,,,,,,기본공격적중시,,,기본 공격 적중 시 20%,
gin_01,indomitable,1,상태효과,자신,0,1,1,1,gs_indomitable,0,,,,,,,,,스킬사용완료시,spinning_slash,,불굴이 적용되는 스킬 사용 후 다음 행동까지 유지,
gin_02,indomitable,2,상태효과,자신,0,1,1,1,gs_indomitable,0,,,,,,,,,스킬사용완료시,earth_sweep,,불굴이 적용되는 스킬 사용 후 다음 행동까지 유지,
gin_03,indomitable,3,상태효과,자신,0,1,1,1,gs_indomitable,0,,,,,,,,,스킬사용완료시,heart_pierce,,불굴이 적용되는 스킬 사용 후 다음 행동까지 유지,
gin_04,indomitable,4,상태효과,자신,0,1,1,1,gs_indomitable,0,,,,,,,,,스킬사용완료시,wind_cleave,,불굴이 적용되는 스킬 사용 후 다음 행동까지 유지,
gcm_01,combat_mastery_might_greatsword,1,받는기본공격피해감소,자신,0,1,1,0,gs_mastery_guard,0,,,,,,,,,전투시작,,,받는 기본 공격 대미지 -10%,
gcm_02,combat_mastery_might_greatsword,2,주는피해증가,자신,0,1,1,0,gs_mastery_power,0,,,,,,,,,전투시작,,,강타 대미지 +5%,
grt_01,retaliation_greatsword,1,주는피해증가,자신,0,1,1,0,gs_retaliation_power,0,,,,,,,,,전투시작,,,"중첩자원ID=gs_retaliation, 중첩당 +5%",
grt_02,retaliation_greatsword,2,자원증가,자신,1,1,1,0,gs_retaliation,0,,,,,,,,,체력소모시,,,체력 10% 소모마다 1중첩,
gbr_01,brawl,1,회복,자신,840,1,1,0,,0,,,,,,,gs_retaliation,소모중첩배율,스킬적중완료시,,,"보복 중첩당 1,999 × 0.42",
gbr_02,brawl,2,회복,자신,840,1,1,0,,0,,,,,,,gs_retaliation,소모중첩배율,기본공격적중시,,,기본 공격 적중 시도 같다,
gcw_01,crushed_wound,1,지속피해,상대,6177,1,1,2,gs_wound,0,상처,,,,,,,,스킬적중완료시,,,"최소 29,415를 2턴에 나눔. 잃은체력비례지속피해증폭 1.43(체력 30%에서 ×2)",4
gcw_02,crushed_wound,2,지속피해,상대,6177,1,1,2,gs_wound,0,상처,,,,,,,,기본공격적중시,,,기본 공격 적중 시도 같다,4
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
gs_tempo,전투 템포,자원,3,0,0,가산,TRUE,"대검전사 전투 템포(최대 3). 올바른 자세에서 회전 베기·가르기(뒤)·라이징 스매시·발구르기(앞)를 쓰면 1씩 얻고, 대검술이 전부 소모한다. 필사의 일격은 최대치로 채운다.",
gs_grip_back,검을 뒤로 쥔 자세,검 자세,1,1,0,상호배타,TRUE,전투 시작 자세. 회전 베기·라이징 스매시·발구르기 사용 후 이 자세가 된다. 회전 베기·가르기가 템포를 얻는다.,TRUE
gs_grip_front,검을 앞으로 쥔 자세,검 자세,1,0,0,상호배타,TRUE,가르기 사용 후 이 자세가 된다. 라이징 스매시가 템포를 얻고 어깨 치기가 발구르기로 바뀐다.,TRUE
gs_wind_gate,바람 가르기 추가 판정,자원,1,0,0,교체,TRUE,바람 가르기 효과 행 안에서만 쓰는 판정 표식.,TRUE
gs_wind_free,추가 바람 가르기,자원,1,0,0,교체,TRUE,다음 바람 가르기가 무방비·파열 효과로 얻은 추가 사용임을 나타낸다. 그 바람 가르기는 같은 효과를 다시 일으키지 않고 표식을 소모한다.,TRUE
gs_retaliation,보복,중첩,100,0,3,개별,TRUE,"체력 10% 소모마다 1중첩(중첩마다 15초→3턴, 최대 100). 중첩당 주는 피해 +5%, 난투가 중첩당 회복한다.",
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
break_broken,브레이크,브레이크|받는피해증가,0.2,브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외),,,,,,,,
gs_rupture,파열,받는피해증가,0.1,심장 찌르기 파열. 지속 피해와 [시너지] 받는 대미지 +10%(최대 차지 90초→15턴).,,,받는피해증가,,,,,
gs_onslaught,맹공,쿨다운감소,1,"맹공: 캐스팅·스킬 발동 속도 +50%, 적용 2회 → 2턴 동안 쿨다운 1 추가 감소.",,,,,,,,
gs_desperate_power,필사의 일격,주는피해증가,0.5,필사의 일격 공격력 +50%. 지속 시간이 원본에 없어 2턴 가정.,,,,,,,,
gs_indomitable,불굴,브레이크면역|방어력증가,0,"불굴: 방어력 +150%, 브레이크 면역. 불굴이 적용되는 스킬(회전 베기·대검술)을 쓴 뒤 다음 행동까지 유지.",,,,,0|1.5,,,
gs_mastery_guard,전투 숙련: 패기,받는기본공격피해감소,0.1,상대 일반 공격으로 받는 피해 -10%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,,,
gs_mastery_power,전투 숙련: 패기,주는피해증가,0.05,강타 대미지 +5%. 대검전사 스킬은 모두 강타라 주는 피해 증가로 표현,,,,,,,,
gs_retaliation_power,보복,주는피해증가,0.05,보복 중첩당 공격력 +5%(중첩자원ID).,,gs_retaliation,,,,,,
gs_wound,상처,잃은체력비례지속피해증폭,1.43,"뭉개진 상처. 적의 잃은 체력에 비례해 커진다(체력 30%에서 최대 대미지 ×2, 그 아래로는 조금 더 커짐).",,,,,,,,
"""",
    };
}
