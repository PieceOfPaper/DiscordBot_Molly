using Molly.Battle;

/// <summary>
/// 듀얼블레이드의 질풍의 기운과 강화 스킬 전환(스플릿 다운·라이징 크레센트·스크류 러시·하울링 템페스트·라이트닝 퓨리), 방어력 무시 중첩,
/// 브레이크 대상 크레센트 2배 피해, 하울링 게일 받는 피해 감소, 글라이딩 퓨리 치명타 추가 타격, 파이널 히트와 패시브 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-10-03 배틀 시트에서 듀얼블레이드 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 듀얼블레이드 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·배틀스킬AI·돌발 이벤트·배틀생활스킬 시트는 석궁사수 사본을 그대로 쓴다.
/// </summary>
internal static class DualBladeBattleTests
{
    private static double Variance(double random) => .9 + random * .2;
    // 듀얼블레이드 스킬은 모두 다단이라 활기의 연타 피해 +25%가 기본으로 붙는다. 방어력 100(200 × 0.5)은 방어력 무시만큼 줄어든다.
    private static int Hit(double shownDamage, double outgoing = 1.25, double incoming = 1, double random = .9, double defense = 100) => (int)Math.Round((shownDamage * .25 - defense) * outgoing * incoming * Variance(random));

    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow, BattleTestGame.Data);
        SplitTests(data);
        CrescentTests(data);
        RushTests(data);
        HowlingTests(data);
        FuryTests(data);
        FinalHitTests(data);
    }

    private static void SplitTests(BattleDataSnapshot data)
    {
        // 스플릿 슬래시: 4회 피해 뒤 방어력 무시 1중첩, 교차하는 검격 균열은 방어력 10%를 무시한다. 궁극기가 아닌 스킬이라 질풍의 기운 +1.
        var split = Turns(Duel(data, ["DualBlades_OuterSlash"], maxActions: 2));
        Assert(split[0].Skill == "스플릿 슬래시" && split[0].Hits == 5 && split[0].Damage == 4 * Hit(2033) + Hit(6023, 1, defense: 90)
            && split[0].Resources.SequenceEqual(new[] { "방어력 무시 +1 (현재 1)", "궁극기 게이지 +150 (현재 150)", "질풍의 기운 +1 (현재 1)" }),
            "스플릿 슬래시는 방어력 무시 1중첩과 질풍의 기운 1을 얻고, 균열은 방어력 무시가 적용된다");

        // 질풍의 기운 4개: 스플릿 다운으로 바뀌어 질풍을 모두 쓰고(강화 스킬도 질풍 +1), 방어력 무시 3중첩. 활기로 강화 스킬 피해 +30%.
        var down = Turns(Duel(data, ["DualBlades_OuterSlash"], maxActions: 2, initial: [("dual_gale", 4)]));
        Assert(down[0].Skill == "스플릿 다운" && down[0].Hits == 5 && down[0].Damage == 4 * Hit(4065, 1.55) + Hit(6023, 1, defense: 70)
            && down[0].Resources.SequenceEqual(new[] { "방어력 무시 +3 (현재 3)", "질풍의 기운 -4 (현재 0)", "질풍의 기운 +1 (현재 1)", "궁극기 게이지 +150 (현재 150)" }),
            "질풍의 기운이 4개면 스플릿 슬래시가 스플릿 다운으로 바뀌어 질풍을 모두 쓰고 방어력 무시 3중첩을 얻는다(부모 피해는 나가지 않는다)");
    }

    private static void CrescentTests(BattleDataSnapshot data)
    {
        // 라이징 크레센트(질풍 4) 브레이크 2칸 → 브레이크 4턴 동안 재사용 대기(3턴)가 끝난 더블 크레센트는 피해 2배·무방비 120%·전투 숙련: 파멸 무방비 +5%.
        var result = Duel(data, ["DualBlades_DoubleCrescent"], maxActions: 8, initial: [("dual_gale", 4)], rules: [("break_gauge_maximum", "2"), ("break_duration_turns", "4")]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "라이징 크레센트" && turns[0].Hits == 6 && turns[0].Damage == 6 * Hit(2049, 1.55) && turns[0].Broken,
            "라이징 크레센트는 6회 피해 뒤 브레이크 2칸을 준다");
        Assert(turns[3].Skill == "더블 크레센트" && turns[3].Hits == 2 && turns[3].Damage == 2 * Hit(3225 * 2, 1.30, 1.2) && !turns[3].BreakGauge,
            "이미 브레이크된 적에게 더블 크레센트는 피해가 2배이고, 브레이크 중이라 게이지는 오르지 않는다");
        var normal = Turns(Duel(data, ["DualBlades_DoubleCrescent"], maxActions: 2));
        Assert(normal[0].Skill == "더블 크레센트" && normal[0].Damage == 2 * Hit(3225) && normal[0].BreakGauge, "브레이크되지 않은 적에게는 원래 피해와 브레이크 1칸");
    }

    private static void RushTests(BattleDataSnapshot data)
    {
        var spin = Turns(Duel(data, ["DualBlades_HurricaneDance"], maxActions: 2));
        Assert(spin[0].Skill == "스핀 러시" && spin[0].Hits == 7 && spin[0].Damage == 7 * Hit(1544), "스핀 러시는 7회 벤다");
        var screw = Duel(data, ["DualBlades_HurricaneDance"], maxActions: 2, initial: [("dual_gale", 4)]);
        var screwTurns = Turns(screw);
        Assert(screwTurns[0].Skill == "스크류 러시" && screwTurns[0].Damage == 7 * Hit(2060, 1.55) && screwTurns[0].Statuses.Contains("상처")
            && screw.Events.Any(x => x.Type == "StatusDamage" && x.Target == "B" && x.Detail == "상처" && x.Amount == (int)Math.Round(1626 * .25 - 100)),
            "스크류 러시는 강화 피해와 상처 지속 피해를 남긴다");

        // 난수 0.1: 스킬 적중 턴의 바람 칼날(40%)이 추가 피해와 질풍 +1, 피어오르는 열망 +1과 25% 추가 +1로 한 번에 질풍 3. 재충전(13%)이 쿨다운을 줄인다.
        var lucky = Duel(data, ["DualBlades_HurricaneDance"], maxActions: 2, random: .1);
        var luckyTurns = Turns(lucky);
        Assert(luckyTurns[0].Resources.Count(x => x.StartsWith("질풍의 기운 +1", StringComparison.Ordinal)) == 3 && luckyTurns[0].Resources.Contains("질풍의 기운 +1 (현재 3)")
            && lucky.Events.Any(x => x.Type == "CooldownReduced" && x.Actor == "A"),
            "바람 칼날·피어오르는 열망 확률 발동으로 질풍의 기운을 더 얻고, 재충전이 재사용 대기를 줄인다");
    }

    private static void HowlingTests(BattleDataSnapshot data)
    {
        var gale = Duel(data, ["DualBlades_HowlingGale"], maxActions: 2);
        var galeTurns = Turns(gale);
        Assert(galeTurns[0].Skill == "하울링 게일" && galeTurns[0].Hits == 20 && galeTurns[0].Damage == 17 * Hit(813) + 2 * Hit(3388) + Hit(6023, 1) && galeTurns[0].Statuses.Contains("하울링 게일"),
            "하울링 게일은 17회 연타·2회 마무리와 균열을 주고 받는 피해 감소를 얻는다");
        Assert(gale.Events.First(x => x.Type == "DamageDealt" && x.Actor == "B").Amount == (int)Math.Round((1500 - 100) * .4 * Variance(.9)),
            "하울링 게일 뒤 상대 행동의 일반 공격은 받는 피해 -50%와 전투 숙련: 파멸 받는 기본 공격 -10%로 줄어든다");
        var tempest = Turns(Duel(data, ["DualBlades_HowlingGale"], maxActions: 2, initial: [("dual_gale", 4)]));
        Assert(tempest[0].Skill == "하울링 템페스트" && tempest[0].Damage == 17 * Hit(1084, 1.55) + 2 * Hit(5204, 1.55) + Hit(6023, 1) && tempest[0].Statuses.Contains("하울링 템페스트"),
            "하울링 템페스트는 강화 피해와 추가타·급소 회피 상태를 얻는다");
    }

    private static void FuryTests(BattleDataSnapshot data)
    {
        var fury = Turns(Duel(data, ["DualBlades_GlidingFury"], maxActions: 2));
        Assert(fury[0].Skill == "글라이딩 퓨리" && fury[0].Damage == 2 * Hit(4648), "글라이딩 퓨리는 2회 돌진 피해");
        // 난수 0.1이면 두 타격이 모두 치명타지만 추가 타격은 사용당 한 번이다.
        var critical = Duel(data, ["DualBlades_GlidingFury"], maxActions: 2, random: .1);
        Assert(critical.Events.Count(x => x.Type == "AdditionalDamage" && x.Actor == "A" && x.Amount == 1162) == 1,
            "글라이딩 퓨리 돌진이 치명타면 치명타·추가타 없는 추가 타격이 한 번 들어간다");
        var lightning = Duel(data, ["DualBlades_GlidingFury"], maxActions: 2, random: .1, initial: [("dual_gale", 4)]);
        Assert(Turns(lightning)[0].Skill == "라이트닝 퓨리" && lightning.Events.Count(x => x.Type == "AdditionalDamage" && x.Actor == "A" && x.Amount == 2324) == 1,
            "라이트닝 퓨리도 치명타 시 강화된 추가 타격이 한 번 들어간다");
    }

    private static void FinalHitTests(BattleDataSnapshot data)
    {
        var final = Turns(Duel(data, ["DualBlades_FinalHit"], maxActions: 2, initial: [("ultimate_gauge", 300)]));
        Assert(final[0].Skill == "파이널 히트" && final[0].Hits == 19 && final[0].Damage == 16 * Hit(1342, 1.55) + 2 * Hit(5370, 1.55) + Hit(6023, 1)
            && !final[0].Resources.Any(x => x.StartsWith("질풍의 기운", StringComparison.Ordinal)),
            "파이널 히트는 활기 +30%가 붙은 연격과 균열을 주고, 궁극기라 질풍의 기운을 얻지 않는다");
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
                ["dual_blade"] = data.Classes["dual_blade"] with { SkillIds = skills },
                ["idle"] = new("idle", "대상", Array.Empty<string>())
            },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, SkillAiRules = data.SkillAiRules, LoadedAt = data.LoadedAt
        };
        // 듀얼블레이드가 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "dual_blade", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
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

    public static readonly IReadOnlyDictionary<string, string> Sheets = new Dictionary<string, string>(CrossbowBattleTests.Sheets.Where(x => x.Key is "배틀규칙" or "배틀이름교체" or "배틀스킬AI" or "배틀돌발이벤트" or "배틀돌발이벤트효과" or "배틀생활스킬" or "배틀생활스킬효과"))
    {
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고,궁극기문구,부모스킬ID,이름
DualBlades_OuterSlash,TRUE,공격,상대,2,0,60,궁극기 게이지,,150,FALSE,"두 검을 찔러 넣고 휘둘러 4회 피해를 주고, 방어력 무시 1중첩을 얻는다. 질풍의 기운이 4개면 스플릿 다운으로 바뀐다.",{caster}가 두 검을 {target}에게 깊숙이 찔러 넣고 날개를 펼치듯 휘두릅니다!,"원본 재사용 대기 12초→2턴, 대미지 3,835×4, 방어력 무시 10%(중첩당, 최대 4, 45초→8턴). 질풍의 기운 4개면 즉시 파생 split_slash_down으로 스플릿 다운. 피해 원본 ×0.53. 이동은 1:1이라 제외",,,
DualBlades_OuterSlash_Strong,TRUE,공격,상대,2,0,0,,,0,FALSE,두 검을 내려 찍어 4회 큰 피해를 주고 방어력 무시 3중첩을 얻는다.,{caster}가 질풍을 실어 두 검으로 {target}을 내려 찍습니다!,"파생 전용(질풍의 기운 4개 소모). 대미지 7,670×4, 방어력 무시 3중첩. 피해 원본 ×0.53",,DualBlades_OuterSlash,
DualBlades_DoubleCrescent,TRUE,공격,상대,3,0,60,궁극기 게이지,,150,FALSE,크게 회전하며 올려 쳐 2회 피해와 브레이크 1칸을 준다. 브레이크된 적에게는 피해가 2배다. 질풍의 기운이 4개면 라이징 크레센트로 바뀐다.,{caster}가 몸을 크게 회전하며 {target}을 올려 칩니다!,"원본 재사용 대기 17초→3턴, 대미지 6,085×2(브레이크된 적 12,171×2 = 조건부피해증가 100%), 브레이크 1칸. 피해 원본 ×0.53. 띄우기 연출은 생략",,,
DualBlades_DoubleCrescent_Strong,TRUE,공격,상대,3,0,0,,,0,FALSE,질풍을 실어 회전하며 6회 올려 치고 브레이크 2칸을 준다. 브레이크된 적에게는 피해가 2배다.,{caster}가 회오리처럼 솟구치며 {target}을 연달아 올려 칩니다!,"파생 전용(질풍의 기운 4개 소모). 대미지 3,866×6(브레이크된 적 7,732×6), 브레이크 2칸. 피해 원본 ×0.53",,DualBlades_DoubleCrescent,
DualBlades_HurricaneDance,TRUE,공격,상대,2,0,60,궁극기 게이지,,150,FALSE,회오리처럼 휘돌며 7회 벤다. 질풍의 기운이 4개면 스크류 러시로 바뀐다.,{caster}가 회오리바람처럼 휘돌며 {target}을 베어냅니다!,"원본 재사용 대기 12초→2턴, 대미지 2,914×7. 피해 원본 ×0.53. 이동·범위는 1:1이라 제외",,,
DualBlades_HurricaneDance_Strong,TRUE,공격,상대,2,0,0,,,0,FALSE,질풍을 실어 휘돌며 7회 베고 상처를 남긴다.,{caster}가 질풍과 함께 휘돌며 {target}을 난도질합니다!,"파생 전용(질풍의 기운 4개 소모). 대미지 3,886×7, 상처 지속 대미지 6,136(지속 시간 미기재라 도적 상처처럼 2턴에 나눔). 피해 원본 ×0.53",,DualBlades_HurricaneDance,
DualBlades_HowlingGale,TRUE,공격,상대,4,0,60,궁극기 게이지,,150,FALSE,"버티고 서서 17회 연타와 2회 마무리 공격을 퍼붓고, 다음 행동까지 받는 피해가 50% 줄어든다. 질풍의 기운이 4개면 하울링 템페스트로 바뀐다.",{caster}가 제자리에 버티고 서서 {target}에게 쉴 새 없이 검을 휘두릅니다!,"원본 재사용 대기 23초→4턴, 연타 1,534×17, 마무리 6,392×2, 사용 중 받는 대미지 -50%(턴제에 시전 중 피격이 없어 사용 후 다음 행동까지 1턴, 대검전사 불굴과 같은 해석). 피해 원본 ×0.53. 밀치기 면역은 밀치기가 없어 제외",,,
DualBlades_HowlingGale_Strong,TRUE,공격,상대,4,0,0,,,0,FALSE,"질풍을 실어 17회 연타와 2회 마무리 공격을 퍼붓고, 받는 피해 감소와 함께 추가타 확률·급소 회피가 오른다.",{caster}가 거센 폭풍처럼 {target}에게 검격을 퍼붓습니다!,"파생 전용(질풍의 기운 4개 소모). 연타 2,045×17, 마무리 9,818×2, 사용 후 추가타 확률 +15%·급소 회피 +30%(60초→10턴), 받는 대미지 -50%(1턴). 피해 원본 ×0.53",,DualBlades_HowlingGale,
DualBlades_GlidingFury,TRUE,공격,상대,3,0,60,궁극기 게이지,,150,FALSE,강력하게 돌진해 2회 피해를 준다. 돌진이 치명타로 적중하면 추가 피해를 준다. 질풍의 기운이 4개면 라이트닝 퓨리로 바뀐다.,{caster}가 바람처럼 파고들어 {target}을 꿰뚫고 지나갑니다!,"원본 재사용 대기 18초→3턴, 대미지 8,770×2, 치명타 적중 시 추가 타격 8,770(치명타·추가타 없음, 사용당 1회, 패시브 피어오르는 열망 행으로 판정). 피해 원본 ×0.53",,,
DualBlades_GlidingFury_Strong,TRUE,공격,상대,3,0,0,,,0,FALSE,질풍의 기운을 실어 번개처럼 돌진해 2회 큰 피해를 준다. 치명타로 적중하면 추가 피해를 준다.,{caster}가 번개처럼 쇄도해 {target}을 베고 지나갑니다!,"파생 전용(질풍의 기운 4개 소모). 대미지 17,540×2, 치명타 적중 시 추가 타격 17,540. 피해 원본 ×0.53",,DualBlades_GlidingFury,
DualBlades_FinalHit,TRUE,궁극기,상대,0,0,100,궁극기 게이지,300,,TRUE,빛처럼 쇄도해 16회 연격과 2회 마무리 공격을 쏟아낸다.,{caster}가 한계를 넘어선 몸으로 {target}에게 빛처럼 쇄도합니다!,"원본 연타 5,369×16, 마무리 21,478×2. 피해 원본 ×0.25. 타겟이 사라지면 다른 대상 공격은 1:1이라 제외. 엠블럼 장착 필요는 사용 조건이 아니라 제외",『무수한 검의 궤적은 결코 떨쳐낼 수 없다』,,
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
split_slash_down,DualBlades_OuterSlash,DualBlades_OuterSlash_Strong,대체,1,1,자원보유,dual_gale>=4,FALSE,즉시,질풍의 기운이 4개면 강화 스킬로 바뀐다(인게임 버튼 전환). 강화 스킬이 질풍을 모두 소모,100
double_crescent_rising,DualBlades_DoubleCrescent,DualBlades_DoubleCrescent_Strong,대체,1,1,자원보유,dual_gale>=4,FALSE,즉시,질풍의 기운이 4개면 강화 스킬로 바뀐다(인게임 버튼 전환). 강화 스킬이 질풍을 모두 소모,100
spin_rush_screw,DualBlades_HurricaneDance,DualBlades_HurricaneDance_Strong,대체,1,1,자원보유,dual_gale>=4,FALSE,즉시,질풍의 기운이 4개면 강화 스킬로 바뀐다(인게임 버튼 전환). 강화 스킬이 질풍을 모두 소모,100
howling_gale_tempest,DualBlades_HowlingGale,DualBlades_HowlingGale_Strong,대체,1,1,자원보유,dual_gale>=4,FALSE,즉시,질풍의 기운이 4개면 강화 스킬로 바뀐다(인게임 버튼 전환). 강화 스킬이 질풍을 모두 소모,100
gliding_fury_lightning,DualBlades_GlidingFury,DualBlades_GlidingFury_Strong,대체,1,1,자원보유,dual_gale>=4,FALSE,즉시,질풍의 기운이 4개면 강화 스킬로 바뀐다(인게임 버튼 전환). 강화 스킬이 질풍을 모두 소모,100
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
dss_01,DualBlades_OuterSlash,1,피해,상대,,2033,4,1,0,,0,,"원본 3,835×4. 질풍의 기운 4개면 스플릿 다운으로 바뀌므로 실행하지 않는다",자신,자원보유,dual_gale,<=,3,,,
dss_02,DualBlades_OuterSlash,2,자원증가,자신,,1,1,1,0,dual_pierce,0,,"방어력 무시 1중첩(45초→8턴, 최대 4)",자신,자원보유,dual_gale,<=,3,,,
dss_03,DualBlades_OuterSlash,3,상태효과,자신,,0,1,1,8,dual_pierce_power,0,,중첩당 방어력 무시 10%,자신,자원보유,dual_gale,<=,3,,,
dsd_01,DualBlades_OuterSlash_Strong,1,피해,상대,,4065,4,1,0,,0,,"원본 7,670×4",,,,,,,,
dsd_02,DualBlades_OuterSlash_Strong,2,자원증가,자신,,3,1,1,0,dual_pierce,0,,방어력 무시 3중첩을 즉시 얻는다,,,,,,,,
dsd_03,DualBlades_OuterSlash_Strong,3,상태효과,자신,,0,1,1,8,dual_pierce_power,0,,중첩당 방어력 무시 10%,,,,,,,,
dsd_04,DualBlades_OuterSlash_Strong,4,자원소모,자신,,0,1,1,0,dual_gale,0,,질풍의 기운을 모두 소모,,,,,,,전부,
ddc_01,DualBlades_DoubleCrescent,1,조건부피해증가,상대,,100,1,1,0,,0,,"이미 브레이크된 적은 12,171×2(피해 2배)",상대,상태효과유형보유,브레이크,,,,,
ddc_02,DualBlades_DoubleCrescent,2,피해,상대,,3225,2,1,0,,0,,"원본 6,085×2",자신,자원보유,dual_gale,<=,3,,,
ddc_03,DualBlades_DoubleCrescent,3,브레이크피해,상대,,1,1,1,0,,0,,브레이크 대미지 1칸,자신,자원보유,dual_gale,<=,3,,,
drc_01,DualBlades_DoubleCrescent_Strong,1,조건부피해증가,상대,,100,1,1,0,,0,,"이미 브레이크된 적은 7,732×6(피해 2배)",상대,상태효과유형보유,브레이크,,,,,
drc_02,DualBlades_DoubleCrescent_Strong,2,피해,상대,,2049,6,1,0,,0,,"원본 3,866×6",,,,,,,,
drc_03,DualBlades_DoubleCrescent_Strong,3,브레이크피해,상대,,2,1,1,0,,0,,브레이크 대미지 2칸,,,,,,,,
drc_04,DualBlades_DoubleCrescent_Strong,4,자원소모,자신,,0,1,1,0,dual_gale,0,,질풍의 기운을 모두 소모,,,,,,,전부,
dsr_01,DualBlades_HurricaneDance,1,피해,상대,,1544,7,1,0,,0,,"원본 2,914×7",자신,자원보유,dual_gale,<=,3,,,
dsc_01,DualBlades_HurricaneDance_Strong,1,피해,상대,,2060,7,1,0,,0,,"원본 3,886×7",,,,,,,,
dsc_02,DualBlades_HurricaneDance_Strong,2,지속피해,상대,,1626,1,1,2,dual_wound,0,상처,"상처 6,136을 2턴에 나눔(지속 시간 미기재)",,,,,,,,
dsc_03,DualBlades_HurricaneDance_Strong,3,자원소모,자신,,0,1,1,0,dual_gale,0,,질풍의 기운을 모두 소모,,,,,,,전부,
dhg_01,DualBlades_HowlingGale,1,상태효과,자신,,0,1,1,1,dual_howling,0,,받는 대미지 -50%(사용 중 → 다음 행동까지),자신,자원보유,dual_gale,<=,3,,,
dhg_02,DualBlades_HowlingGale,2,피해,상대,,813,17,1,0,,0,,"연타 원본 1,534×17",자신,자원보유,dual_gale,<=,3,,,
dhg_03,DualBlades_HowlingGale,3,피해,상대,,3388,2,1,0,,0,,"마무리 원본 6,392×2",자신,자원보유,dual_gale,<=,3,,,
dht_01,DualBlades_HowlingGale_Strong,1,상태효과,자신,,0,1,1,1,dual_howling,0,,받는 대미지 -50%(사용 중 → 다음 행동까지),,,,,,,,
dht_02,DualBlades_HowlingGale_Strong,2,피해,상대,,1084,17,1,0,,0,,"연타 원본 2,045×17",,,,,,,,
dht_03,DualBlades_HowlingGale_Strong,3,피해,상대,,5204,2,1,0,,0,,"마무리 원본 9,818×2",,,,,,,,
dht_04,DualBlades_HowlingGale_Strong,4,상태효과,자신,,0,1,1,10,dual_tempest,0,,추가타 확률 +15%·급소 회피 +30%(60초→10턴),,,,,,,,
dht_05,DualBlades_HowlingGale_Strong,5,자원소모,자신,,0,1,1,0,dual_gale,0,,질풍의 기운을 모두 소모,,,,,,,전부,
dgf_01,DualBlades_GlidingFury,1,피해,상대,,4648,2,1,0,,0,,"원본 8,770×2. 치명타 추가 타격은 패시브 rd_04",자신,자원보유,dual_gale,<=,3,,,
dlf_01,DualBlades_GlidingFury_Strong,1,피해,상대,,9296,2,1,0,,0,,"원본 17,540×2. 치명타 추가 타격은 패시브 rd_05",,,,,,,,
dlf_02,DualBlades_GlidingFury_Strong,2,자원소모,자신,,0,1,1,0,dual_gale,0,,질풍의 기운을 모두 소모,,,,,,,전부,
dfh_01,DualBlades_FinalHit,1,자원설정,자신,,1,1,1,0,dual_ult_mark,0,,궁극기 사용 표식(피어오르는 열망이 질풍을 주지 않음),,,,,,,,
dfh_02,DualBlades_FinalHit,2,피해,상대,,1342,16,1,0,,0,,"연타 원본 5,369×16",,,,,,,,
dfh_03,DualBlades_FinalHit,3,피해,상대,,5370,2,1,0,,0,,"마무리 원본 21,478×2",,,,,,,,
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
DualBlades_RisingAspirations,TRUE,"궁극기를 제외한 스킬(강화 스킬 포함, 나무위키) 사용 완료 시 질풍의 기운 +1, 25% 확률로 +1. 4개가 되면 다음 스킬이 강화 스킬로 바뀐다(배틀스킬파생). 궁극기는 dual_ult_mark 표식으로 제외. 글라이딩·라이트닝 퓨리의 치명타 추가 타격도 이 패시브 행이 판정한다(클래스 공통 스킬 규칙, 도적 오버 차지 충전과 같은 방식)."
DualBlades_Recharge,TRUE,"기본 공격마다 무작위 스킬 하나의 재사용 대기 0.5~1.5초 감소. 기본 공격 한 번(4타)을 평균 4초로 보고, 모든 스킬 5개×6초=30초의 약 13%로 환산해 13% 확률로 모든 스킬 쿨다운 1턴 감소. 피해를 준 스킬 턴도 기본 공격을 섞어 친 것으로 보고 센다(도적 퀵 핸즈와 같은 해석, 2026-10-03 사용자 확인). 위력 비례 강화는 제외."
DualBlades_BreakedHit_Passive_03,TRUE,"받는 기본 공격 피해 -10%, 무방비 피해 +5%(브레이크 상태만 무방비). 레벨 30 어시스트 해금 문구는 구현 범위에서 제외(항상 만렙 가정)."
DualBlades_Vigor_Passive_04,TRUE,"연타 피해 +25%(상시, 멀티히트피해증가). 연타 적중 시 다음 강화 스킬·궁극기 피해 +30%는 모든 스킬이 연타라 사실상 항상 충족되어, 강화 스킬 5종과 파이널 히트에 상시 스킬피해증가로 단순화."
DualBlades_Wind_Passive_05,TRUE,기본 공격 4타 발동 확률 6.25/7.91/14.58/18.75%를 기본 공격 한 번에 한 번 이상 발동할 확률 40%로 묶었다. 발동 시 추가 피해(일반 공격 한 타 375의 50%를 고정값 750으로) + 질풍의 기운 +1. 피해를 준 스킬 턴도 기본 공격을 섞어 친 것으로 본다(2026-10-03 사용자 확인).
DualBlades_VacuumShura_Passive_06,TRUE,"십자 베기 스킬(스플릿 슬래시·다운, 하울링 게일·템페스트, 파이널 히트) 적중 시 균열 11,364 피해. 크로스 스트라이크는 배틀 스킬에 없어 제외, 범위는 1:1이라 대상 하나. 해금 Lv.60은 항상 충족 가정."
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
rd_01,DualBlades_RisingAspirations,1,자원증가,자신,1,1,1,0,dual_gale,0,,자신,자원보유,dual_ult_mark,<=,0,,,스킬사용완료시,,,궁극기를 제외한 스킬 사용 시 질풍의 기운 +1,
rd_02,DualBlades_RisingAspirations,2,자원증가,자신,1,1,0.25,0,dual_gale,0,,자신,자원보유,dual_ult_mark,<=,0,,,스킬사용완료시,,,25% 확률로 질풍의 기운 1 추가,
rd_03,DualBlades_RisingAspirations,3,자원소모,자신,0,1,1,0,dual_ult_mark,0,,,,,,,,전부,스킬사용완료시,,,궁극기 표식 정리,
rd_04,DualBlades_RisingAspirations,4,추가피해,상대,1162,1,1,0,,0,,,,,,,,,치명타적중시,DualBlades_GlidingFury,,"글라이딩 퓨리 돌진 치명타 시 추가 타격 8,770(치명타·추가타 없음, 방어 차감 없음, ×0.25 환산 후 원본 ×0.53). 사용당 1회",1
rd_05,DualBlades_RisingAspirations,5,추가피해,상대,2324,1,1,0,,0,,,,,,,,,치명타적중시,DualBlades_GlidingFury_Strong,,"라이트닝 퓨리 돌진 치명타 시 추가 타격 17,540(×0.25 환산 후 원본 ×0.53). 사용당 1회",1
rch_01,DualBlades_Recharge,1,쿨다운감소,자신,1,1,0.13,0,,0,,,,,,,,,스킬적중완료시,,,스킬 턴의 기본 공격으로 해석. 13% 확률로 모든 스킬 쿨다운 1턴 감소,
rch_02,DualBlades_Recharge,2,쿨다운감소,자신,1,1,0.13,0,,0,,,,,,,,,기본공격적중시,,,기본 공격 적중 시 13%,
cmdd_01,DualBlades_BreakedHit_Passive_03,1,받는기본공격피해감소,자신,0,1,1,0,dual_mastery_guard,0,,,,,,,,,전투시작,,,받는 기본 공격 대미지 -10%,
cmdd_02,DualBlades_BreakedHit_Passive_03,2,무방비피해증가,자신,0,1,1,0,dual_mastery_unguarded,0,,,,,,,,,전투시작,,,무방비 대미지 +5%,
vg_01,DualBlades_Vigor_Passive_04,1,멀티히트피해증가,자신,0,1,1,0,dual_vigor_multi,0,,,,,,,,,전투시작,,,연타 대미지 +25%,
vg_02,DualBlades_Vigor_Passive_04,2,스킬피해증가,자신,0,1,1,0,dual_vigor_split_down,0,,,,,,,,,전투시작,,,split_down 피해 +30%,
vg_03,DualBlades_Vigor_Passive_04,3,스킬피해증가,자신,0,1,1,0,dual_vigor_rising_crescent,0,,,,,,,,,전투시작,,,rising_crescent 피해 +30%,
vg_04,DualBlades_Vigor_Passive_04,4,스킬피해증가,자신,0,1,1,0,dual_vigor_screw_rush,0,,,,,,,,,전투시작,,,screw_rush 피해 +30%,
vg_05,DualBlades_Vigor_Passive_04,5,스킬피해증가,자신,0,1,1,0,dual_vigor_howling_tempest,0,,,,,,,,,전투시작,,,howling_tempest 피해 +30%,
vg_06,DualBlades_Vigor_Passive_04,6,스킬피해증가,자신,0,1,1,0,dual_vigor_lightning_fury,0,,,,,,,,,전투시작,,,lightning_fury 피해 +30%,
vg_07,DualBlades_Vigor_Passive_04,7,스킬피해증가,자신,0,1,1,0,dual_vigor_final_hit,0,,,,,,,,,전투시작,,,final_hit 피해 +30%,
wb_01,DualBlades_Wind_Passive_05,1,자원설정,자신,1,1,0.4,0,dual_wind_proc,0,,,,,,,,,스킬적중완료시,,,스킬 턴의 기본 공격으로 해석. 40%,
wb_02,DualBlades_Wind_Passive_05,2,자원설정,자신,1,1,0.4,0,dual_wind_proc,0,,,,,,,,,기본공격적중시,,,기본 공격 적중 시 40%,
wb_03,DualBlades_Wind_Passive_05,3,피해,상대,750,1,1,0,,0,,자신,자원보유,dual_wind_proc,>=,1,,,자원획득시,,dual_wind_proc,적을 한 번 더 베는 추가 피해(일반 공격 한 타의 50%),
wb_04,DualBlades_Wind_Passive_05,4,자원증가,자신,1,1,1,0,dual_gale,0,,자신,자원보유,dual_wind_proc,>=,1,,,자원획득시,,dual_wind_proc,질풍의 기운 +1,
wb_05,DualBlades_Wind_Passive_05,5,자원소모,자신,0,1,1,0,dual_wind_proc,0,,,,,,,,전부,자원획득시,,dual_wind_proc,판정 표식 정리,
cs_01,DualBlades_VacuumShura_Passive_06,1,피해,상대,6023,1,1,0,,0,,,,,,,,,스킬적중완료시,DualBlades_OuterSlash,,"십자 베기 균열 원본 11,364. 원본 ×0.53",
cs_02,DualBlades_VacuumShura_Passive_06,2,피해,상대,6023,1,1,0,,0,,,,,,,,,스킬적중완료시,DualBlades_OuterSlash_Strong,,"십자 베기 균열 원본 11,364. 원본 ×0.53",
cs_03,DualBlades_VacuumShura_Passive_06,3,피해,상대,6023,1,1,0,,0,,,,,,,,,스킬적중완료시,DualBlades_HowlingGale,,"십자 베기 균열 원본 11,364. 원본 ×0.53",
cs_04,DualBlades_VacuumShura_Passive_06,4,피해,상대,6023,1,1,0,,0,,,,,,,,,스킬적중완료시,DualBlades_HowlingGale_Strong,,"십자 베기 균열 원본 11,364. 원본 ×0.53",
cs_05,DualBlades_VacuumShura_Passive_06,5,피해,상대,6023,1,1,0,,0,,,,,,,,,스킬적중완료시,DualBlades_FinalHit,,"십자 베기 균열 원본 11,364. 원본 ×0.53",
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
dual_gale,질풍의 기운,자원,4,0,0,가산,TRUE,"듀얼블레이드 질풍의 기운(최대 4). 궁극기를 제외한 스킬 사용 시 1(25% 확률로 1 추가), 바람 칼날 발동 시 1을 얻는다. 4개면 다음 스킬이 강화 스킬로 바뀌어 모두 소모한다.",
dual_pierce,방어력 무시,자원,4,0,8,가산,TRUE,"스플릿 슬래시 1·스플릿 다운 3중첩(최대 4, 45초→8턴). 중첩당 방어력 무시 10%(dual_pierce_power).",
dual_ult_mark,궁극기 사용 표식,자원,1,0,0,교체,TRUE,파이널 히트 사용 표식. 피어오르는 열망의 질풍 획득을 막고 사용 완료 시 소모.,TRUE
dual_wind_proc,바람 칼날 발동,자원,1,0,0,교체,TRUE,바람 칼날 발동 판정 표식. 추가 피해와 질풍 획득을 같은 판정으로 묶는다.,TRUE
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
break_broken,브레이크,브레이크|받는피해증가,0.2,브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외),,,,,,,,
dual_pierce_power,방어력 무시,방어무시,0.1,스플릿 슬래시·다운의 근접 방어 무시. 방어력 무시 중첩(dual_pierce)당 공격 시 상대 방어력 10% 무시.,,dual_pierce,,,,,,TRUE
dual_wound,상처,없음,0,스크류 러시의 지속 피해.,,,,,,,,
dual_howling,하울링 게일,받는피해감소,0.5,하울링 게일·템페스트 사용 중 받는 대미지 -50%. 턴제에 시전 중 피격이 없어 사용 후 다음 행동까지 유지.,,,,,,,,
dual_tempest,하울링 템페스트,추가타확률증가|받는치명타확률감소,0,"하울링 템페스트 사용 후 추가타 확률 +15%, 급소 회피 +30%(60초→10턴).",,,,,0.15|0.3,,,
dual_mastery_guard,전투 숙련: 파멸,받는기본공격피해감소,0.1,상대 일반 공격으로 받는 피해 -10%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,,,
dual_mastery_unguarded,전투 숙련: 파멸,무방비피해증가,0.05,무방비 대미지 +5%. 배틀에서는 브레이크 상태만 무방비로 본다(도적 스닉 어택과 같은 해석).,,,,,,,,
dual_vigor_multi,활기,멀티히트피해증가,0.25,연타 대미지 +25%(상시). 듀얼블레이드 스킬은 모두 연타라 다단 피해 증가로 표현.,,,,,,,,
dual_vigor_split_down,활기,스킬피해증가,0.3,활기: 강화 스킬과 궁극기 피해 +30%. 모든 스킬이 연타라 연타 적중 조건은 사실상 항상 충족되어 상시로 단순화.,DualBlades_OuterSlash_Strong,,,,,,,TRUE
dual_vigor_rising_crescent,활기,스킬피해증가,0.3,활기: 강화 스킬과 궁극기 피해 +30%. 모든 스킬이 연타라 연타 적중 조건은 사실상 항상 충족되어 상시로 단순화.,DualBlades_DoubleCrescent_Strong,,,,,,,TRUE
dual_vigor_screw_rush,활기,스킬피해증가,0.3,활기: 강화 스킬과 궁극기 피해 +30%. 모든 스킬이 연타라 연타 적중 조건은 사실상 항상 충족되어 상시로 단순화.,DualBlades_HurricaneDance_Strong,,,,,,,TRUE
dual_vigor_howling_tempest,활기,스킬피해증가,0.3,활기: 강화 스킬과 궁극기 피해 +30%. 모든 스킬이 연타라 연타 적중 조건은 사실상 항상 충족되어 상시로 단순화.,DualBlades_HowlingGale_Strong,,,,,,,TRUE
dual_vigor_lightning_fury,활기,스킬피해증가,0.3,활기: 강화 스킬과 궁극기 피해 +30%. 모든 스킬이 연타라 연타 적중 조건은 사실상 항상 충족되어 상시로 단순화.,DualBlades_GlidingFury_Strong,,,,,,,TRUE
dual_vigor_final_hit,활기,스킬피해증가,0.3,활기: 강화 스킬과 궁극기 피해 +30%. 모든 스킬이 연타라 연타 적중 조건은 사실상 항상 충족되어 상시로 단순화.,DualBlades_FinalHit,,,,,,,TRUE
"""",
    };
}
