using Molly.Battle;

/// <summary>
/// 음유시인 악상 흐름(멜로디 쇼크·브레이크된 적 공격 → 용맹, 연주 스킬 사용 완료 → 희망, 죽은 척 → 반격, 먼저 떠올린 악상 하나만 유지, 바즈 테일이 악상의 변주곡으로 바뀌고 변주곡이 악상을 소모)을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-10-02 배틀 시트에서 음유시인 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 음유시인 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·배틀스킬AI·돌발 이벤트·배틀생활스킬 시트는 석궁사수 사본을 그대로 쓴다.
/// </summary>
internal static class BardBattleTests
{
    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow, BattleTestGame.Data);
        MelodyShockTests(data);
        BardsTaleTests(data);
        FirstComeTests(data);
        BrokenTargetTests(data);
        SymphonyTests(data);
    }

    private static void MelodyShockTests(BattleDataSnapshot data)
    {
        // 브레이크 게이지 1칸이면 멜로디 쇼크가 상대를 브레이크한다. 상대를 브레이크한 것으로는 반격 악상이 생기지 않는다.
        var result = Duel(data, ["HighBard_BardsTale", "HighBard_MelodyShock"], maxActions: 3, rules: [("break_gauge_maximum", "1")]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "멜로디 쇼크" && turns[0].Broken && turns[0].Resources.Contains("용맹 악상 +1 (현재 1)") && !turns[0].Resources.Any(x => x.StartsWith("반격 악상", StringComparison.Ordinal)),
            "멜로디 쇼크는 브레이크 피해 1칸을 주고 용맹 악상을 떠올린다(상대 브레이크로는 반격 악상이 생기지 않는다)");
        Assert(turns[1].Skill == "용맹의 찬가" && turns[1].Resources.Contains("용맹 악상 -1 (현재 0)") && turns[1].Statuses.Contains("고양(용맹)"),
            "용맹 악상이 있으면 바즈 테일이 용맹의 찬가로 바뀌고, 찬가가 악상을 소모한다");
    }

    private static void BardsTaleTests(BattleDataSnapshot data)
    {
        var result = Duel(data, ["HighBard_BardsTale"], maxActions: 3);
        var turns = Turns(result);
        Assert(turns[0].Skill == "바즈 테일" && turns[0].Hits == 5 && turns[0].Statuses.Contains("현기증") && turns[0].Statuses.Contains("고양") && turns[0].Resources.Contains("희망 악상 +1 (현재 1)"),
            "악상이 없으면 바즈 테일을 그대로 연주해 피해·현기증·고양을 주고 희망 악상을 떠올린다");
        Assert(turns[1].Skill == "희망의 송가" && turns[1].Resources.Contains("희망 악상 -1 (현재 0)") && turns[1].Statuses.Contains("고양(희망)"),
            "희망 악상이 있으면 바즈 테일 대신 희망의 송가가 나가고 악상을 소모한다");
    }

    private static void FirstComeTests(BattleDataSnapshot data)
    {
        // 악상은 하나만 든다. 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점).
        var triple = Turns(Duel(data, ["HighBard_TripleStroke"], maxActions: 1, initial: [("bard_valor", 1)]));
        Assert(triple[0].Skill == "트리플 스트로크" && !triple[0].Resources.Any(x => x.StartsWith("용맹 악상", StringComparison.Ordinal) || x.StartsWith("희망 악상", StringComparison.Ordinal)),
            "용맹 악상이 있으면 트리플 스트로크를 마쳐도 희망 악상이 떠오르지 않는다");
        var feign = Turns(Duel(data, ["HighBard_FeignDeath"], maxActions: 1, initial: [("bard_hope", 1)]));
        Assert(feign[0].Skill == "죽은 척 하기" && !feign[0].Resources.Any(x => x.StartsWith("반격 악상", StringComparison.Ordinal) || x.StartsWith("희망 악상", StringComparison.Ordinal)),
            "희망 악상이 있으면 죽은 척 하기로 반격 악상이 떠오르지 않는다");
        var counter = Turns(Duel(data, ["HighBard_FeignDeath"], maxActions: 1));
        Assert(counter[0].Resources.Contains("반격 악상 +1 (현재 1)"), "악상이 없으면 죽은 척 하기로 반격 악상을 떠올린다");
    }

    private static void BrokenTargetTests(BattleDataSnapshot data)
    {
        // 시험용 브레이크 스킬로 상대를 브레이크한 뒤, 상대가 행동을 잃은 다음 트리플 스트로크(연주 스킬)로 공격한다.
        // 적중이 사용 완료보다 먼저 판정되므로 희망 대신 용맹 악상이 떠오른다.
        var breaker = new BattleSkill("test_break", "시험용 브레이크", "일반", null, true, 9, 0, 99, 1, [new BattleEffect("test_break_1", 1, "브레이크피해", "상대", 3, 1, 1, 0, null, 0, null, null, null, null, null, null, null, null)]);
        var turns = Turns(Duel(data, ["HighBard_TripleStroke", "test_break"], maxActions: 3, extraSkill: breaker));
        Assert(turns[0].Skill == "시험용 브레이크" && turns[0].Broken && !turns[0].Resources.Any(x => x.Contains("악상", StringComparison.Ordinal)),
            "상대를 브레이크하는 것만으로는 악상이 떠오르지 않는다");
        Assert(turns[1].Skill == "트리플 스트로크" && turns[1].Resources.Contains("용맹 악상 +1 (현재 1)") && !turns[1].Resources.Any(x => x.StartsWith("희망 악상", StringComparison.Ordinal)),
            "브레이크 상태인 적을 연주 스킬로 공격하면 희망보다 용맹 악상이 먼저 떠오른다");
    }

    private static void SymphonyTests(BattleDataSnapshot data)
    {
        // 배틀 심포니는 브레이크 대미지 1칸을 준다. 그 타격으로 상대가 브레이크되면 적중 시 용맹이 먼저 떠올라 연주 완료의 희망은 떠오르지 않는다.
        var turns = Turns(Duel(data, ["HighBard_SymphonyOfDestruction"], maxActions: 1, initial: [("ultimate_gauge", 300)], rules: [("break_gauge_maximum", "1")]));
        Assert(turns[0].Skill == "배틀 심포니" && turns[0].Broken && turns[0].Resources.Contains("용맹 악상 +1 (현재 1)") && !turns[0].Resources.Any(x => x.StartsWith("희망 악상", StringComparison.Ordinal)),
            "배틀 심포니는 브레이크 피해를 주고, 브레이크된 적을 친 것으로 용맹 악상이 희망보다 먼저 떠오른다");
        var plain = Turns(Duel(data, ["HighBard_SymphonyOfDestruction"], maxActions: 1, initial: [("ultimate_gauge", 300)]));
        Assert(plain[0].Resources.Contains("희망 악상 +1 (현재 1)"), "상대가 브레이크되지 않으면 배틀 심포니를 마친 뒤 희망 악상을 떠올린다");
    }

    private sealed record Turn(string Skill, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses, int Hits, bool Broken);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, (string Id, int Value)[]? initial = null, (string Id, string Value)[]? rules = null, BattleSkill? extraSkill = null)
    {
        var ruleMap = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", maxActions.ToString()) }.Concat(rules ?? [])) ruleMap[id] = ruleMap[id] with { Value = value };
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in initial ?? []) resources[id] = resources[id] with { InitialValue = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = ruleMap,
            Classes = new Dictionary<string, BattleClass> { ["bard"] = data.Classes["bard"] with { SkillIds = skills }, ["idle"] = new("idle", "대상", Array.Empty<string>()) },
            Skills = extraSkill is null ? data.Skills : new Dictionary<string, BattleSkill>(data.Skills) { [extraSkill.Id] = extraSkill }, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, LoadedAt = data.LoadedAt
        };
        // 음유시인이 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "bard", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
    }

    /// <summary>A의 행동별로 사용 스킬·A의 자원 변화·새로 걸린 상태·A의 타격 수·브레이크 발동 여부를 모은다.</summary>
    private static List<Turn> Turns(BattleResult result)
    {
        var turns = new List<Turn>();
        List<BattleEvent>? current = null;
        foreach (var e in result.Events.Append(new BattleEvent("TurnStarted", "")))
        {
            if (e.Type != "TurnStarted") { current?.Add(e); continue; }
            if (current is not null)
                turns.Add(new(current.FirstOrDefault(x => x.Type is "SkillUsed" or "NormalAttackUsed") is { } used ? used.Detail ?? "(일반 공격)" : "(행동 없음)",
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

    public static readonly IReadOnlyDictionary<string, string> Sheets = new Dictionary<string, string>(CrossbowBattleTests.Sheets.Where(x => x.Key is "배틀규칙" or "배틀이름교체" or "배틀스킬AI" or "배틀돌발이벤트" or "배틀돌발이벤트효과" or "배틀생활스킬" or "배틀생활스킬효과"))
    {
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고,궁극기문구,부모스킬ID,이름
HighBard_StringShot,TRUE,공격·방해,상대,2,0,65,궁극기 게이지,,150,FALSE,적을 공격하고 약점 노출을 남긴다.,{caster}가 현을 튕겨 {target}에게 스트링 샷을 날립니다!,"캡처 수치를 공격력 56,024로 나눈 추정 계수",,,
HighBard_MelodyShock,TRUE,공격·강타,상대,3,0,70,궁극기 게이지,,150,FALSE,적을 후려쳐 브레이크 피해를 주고 용맹 악상을 떠올린다. 현기증 상태의 적에게 더 큰 피해를 준다.,{caster}가 류트로 {target}을 힘껏 후려칩니다!,"원본 재사용 대기 15초→3턴, 브레이크 대미지 1칸, 현기증 대상 150%. 사용 시 용맹 악상. 브레이크 시 남은 재사용 대기 50% 감소·최대 스택 2는 제외",,,
HighBard_TripleStroke,TRUE,공격·방해,상대,3,0,75,궁극기 게이지,,150,FALSE,세 번 공격하고 현기증을 남긴다.,{caster}가 다채로운 음표를 연달아 쏘아냅니다!,현기증 중인 대상에게는 두려움 추가,,,
HighBard_BardsTale,TRUE,공격·지원,자신·상대,1,0,85,궁극기 게이지,0,150,FALSE,영웅의 서사를 연주해 피해·현기증·회복·고양을 주고 희망 악상을 떠올린다. 악상을 떠올린 상태면 그 악상의 변주곡으로 바뀐다.,{caster}가 영웅의 서사를 연주하기 시작합니다!,"원본 대미지 26,499×5, 회복 2,062×5, 고양 3초(공격 속도 15%, 3초마다 1,374), 현기증 9초, 재사용 대기 6초→1턴. 악상(용맹·희망·반격)이 있으면 재사용 시 파생으로 용맹의 찬가·희망의 송가·반격의 서곡이 쿨다운과 관계없이 나가고 그 악상을 소모한다",,,
HighBard_BardsTale_Valor,TRUE,공격·지원,자신·상대,0,0,0,,,0,FALSE,공격 속도와 연주 위력을 높이는 악상을 연주한다.,용맹의 찬가가 전장에 울려 퍼집니다!,파생 전용,,HighBard_BardsTale,
HighBard_BardsTale_Revenge,TRUE,회복·지원,자신·상대,0,0,0,,,0,FALSE,많은 체력을 회복하고 행동 속도를 높인다.,반격의 서곡이 꺾이지 않는 의지를 불러냅니다!,파생 전용,,HighBard_BardsTale,
HighBard_BardsTale_Reprise,TRUE,공격·지원,자신·상대,0,0,0,,,0,FALSE,공격과 방어를 함께 강화한다.,희망의 송가가 굳건한 기세를 북돋웁니다!,파생 전용,,HighBard_BardsTale,
HighBard_FeignDeath,TRUE,생존·파생,자신,4,0,95,궁극기 게이지,,150,FALSE,죽은 척하며 회복하고 다음 역습을 준비한다.,{caster}가 갑자기 쓰러져 죽은 척을 합니다!,1턴 생존기로 단순화. 원본 재사용 대기 19초→4턴,,,
HighBard_FeignDeath_Windmill,TRUE,반격·공격,상대,0,0,0,,,0,FALSE,기상하며 회전 공격으로 반격한다.,{caster}가 벌떡 일어나 라이징 윈드밀을 사용합니다!,파생 전용,,HighBard_FeignDeath,
ExpertBard_FineTuning,TRUE,공격·강화,상대,0,0,0,,,0,FALSE,강한 마무리 타격 후 악상 스킬의 피해를 강화한다.,{caster}가 류트를 조율하듯 {target}을 연달아 타격합니다!,파생 전용,,HighBard_FeignDeath,
HighBard_SymphonyOfDestruction,TRUE,궁극기·지원,자신·상대,0,0,100,궁극기 게이지,300,0,TRUE,강렬한 연주로 피해를 주며 공격력과 쿨다운을 강화한다.,{caster}의 배틀 심포니가 전장을 뒤흔듭니다!,원본 쿨다운 감소 효과를 2턴 감소로 단순화,"『들리는가, 아군을 격려하고 적을 뒤흔드는 이 격정의 선율이!』",,
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
string_shot_01,HighBard_StringShot,1,피해,상대,공격력,0,1,1,0,,0,특제 탄이 {target}을 꿰뚫어 {damage}의 피해!,"표시 피해 37,536",,,,,,,,
string_shot_02,HighBard_StringShot,2,상태효과,상대,,0,1,1,3,weak_point_exposure,1,{target}에게 약점 노출이 남았습니다.,원본 15초→3턴. 스킬 공격의 첫 타격(다단은 첫 타)에 치명타 확률 +50%·방어도 무시 50%를 적용하고 소모,,,,,,,,
melody_shock_01,HighBard_MelodyShock,1,피해,상대,공격력,0,1,1,0,,0,거대한 울림이 {target}에게 {damage}의 피해!,"표시 피해 16,891",,,,,,,,
melody_shock_02,HighBard_MelodyShock,2,조건부피해증가,상대,,50,1,1,0,,0,현기증에 빠진 {target}이 더 큰 충격을 받습니다!,"현기증 보유 시 피해 배율 150%. 고정값 50 = 피해 ×1.5(곱연산), 스킬 실행 전 상태로 판정",상대,상태효과보유,dizziness,존재,,,,
triple_stroke_01,HighBard_TripleStroke,1,피해,상대,공격력,0,3,1,0,,0,음표가 연달아 적중해 총 {damage}의 피해!,"표시 피해 28,012 × 3",,,,,,,,
triple_stroke_02,HighBard_TripleStroke,3,지속피해,상대,,5040,2,1,2,dizziness,1,마지막 음표가 {target}에게 현기증을 남깁니다.,원본 현기증 1초마다 1120×9초=총 10080을 2턴(9초→2턴)에 5040씩 나눔. 이동 속도 감소 50%는 생략,,,,,,,,
triple_stroke_03,HighBard_TripleStroke,2,지속피해,상대,,5602,2,1,2,fear,1,두려움이 {target}을 잠식해 {damage}의 피해!,이미 현기증을 보유한 대상에게만 부여(현기증 부여 행보다 먼저 판정). 원본 두려움 지속 대미지 11204를 2턴에 5602씩 나눔,상대,상태효과보유,dizziness,존재,,,,
heroic_hymn_01,HighBard_BardsTale_Valor,1,피해,상대,공격력,0,5,1,0,,0,용맹한 선율이 {target}을 휩씁니다!,"표시 피해 16,022 × 5",,,,,,,,
heroic_hymn_02,HighBard_BardsTale_Valor,2,회복,자신,공격력,0,5,1,0,,0,용맹의 찬가로 {heal}의 체력을 회복합니다.,"표시 회복 2,062 × 5",,,,,,,,
heroic_hymn_03,HighBard_BardsTale_Valor,3,지속피해,상대,,5040,2,1,2,dizziness,1,{target}이 현기증에 빠집니다.,원본 현기증 1초마다 1120×9초=총 10080을 2턴(9초→2턴)에 5040씩 나눔. 이동 속도 감소 50%는 생략,,,,,,,,
heroic_hymn_04,HighBard_BardsTale_Valor,4,지속회복,자신,,2748,1,1,3,uplift_courage,1,{actor}에게 고양(용맹)이 깃듭니다.,"고양(용맹): 공격 속도 15%·연주 스킬 위력 10%. 고양 회복 3초마다 1374→턴당 2748. 연주곡 회복 5회×3초=15초→3턴을 한 번에 부여하고, 다시 받으면 남은 턴에 3턴을 더한다(지속방식=누적)",,,,,,,,
counter_overture_01,HighBard_BardsTale_Revenge,1,피해,상대,공격력,0,5,1,0,,0,불굴의 선율이 {target}을 휩씁니다!,"표시 피해 16,022 × 5",,,,,,,,
counter_overture_02,HighBard_BardsTale_Revenge,2,회복,자신,공격력,0,5,1,0,,0,반격의 서곡으로 {heal}의 체력을 회복합니다.,"표시 회복 5,076 × 5",,,,,,,,
counter_overture_03,HighBard_BardsTale_Revenge,3,지속피해,상대,,5040,2,1,2,dizziness,1,{target}이 현기증에 빠집니다.,원본 현기증 1초마다 1120×9초=총 10080을 2턴(9초→2턴)에 5040씩 나눔. 이동 속도 감소 50%는 생략,,,,,,,,
counter_overture_04,HighBard_BardsTale_Revenge,4,지속회복,자신,,6768,1,1,3,uplift_indomitable,1,{actor}에게 고양(반격)이 깃듭니다.,"고양(반격): 공격 속도 15%(이동 속도 20%는 생략). 고양 회복 3초마다 3384→턴당 6768. 연주곡 회복 5회×3초=15초→3턴을 한 번에 부여하고, 다시 받으면 남은 턴에 3턴을 더한다(지속방식=누적)",,,,,,,,
song_of_hope_01,HighBard_BardsTale_Reprise,1,피해,상대,공격력,0,5,1,0,,0,희망의 선율이 {target}을 휩씁니다!,"표시 피해 16,022 × 5",,,,,,,,
song_of_hope_02,HighBard_BardsTale_Reprise,2,회복,자신,공격력,0,5,1,0,,0,희망의 송가로 {heal}의 체력을 회복합니다.,"표시 회복 2,062 × 5",,,,,,,,
song_of_hope_03,HighBard_BardsTale_Reprise,3,지속피해,상대,,5040,2,1,2,dizziness,1,{target}이 현기증에 빠집니다.,원본 현기증 1초마다 1120×9초=총 10080을 2턴(9초→2턴)에 5040씩 나눔. 이동 속도 감소 50%는 생략,,,,,,,,
song_of_hope_04,HighBard_BardsTale_Reprise,4,지속회복,자신,,2748,1,1,3,uplift_hope,1,{actor}에게 고양(희망)이 깃듭니다.,"고양(희망): 공격 속도 15%·[시너지] 주는 피해 10%·[시너지] 받는 피해 -10%. 고양 회복 3초마다 1374→턴당 2748. 연주곡 회복 5회×3초=15초→3턴을 한 번에 부여하고, 다시 받으면 남은 턴에 3턴을 더한다(지속방식=누적)",,,,,,,,
feign_death_01,HighBard_FeignDeath,1,회복,자신,공격력,0,8,1,0,,0,죽은 척하는 동안 {heal}의 체력을 회복합니다.,"표시 회복 3,147 × 8",,,,,,,,
feign_death_02,HighBard_FeignDeath,2,받는피해감소,자신,,0,1,1,1,feign_death,1,죽은 척하여 받는 피해가 40% 감소합니다.,원본 지속 4초를 1턴으로 변환,,,,,,,,
feign_death_03,HighBard_FeignDeath,3,대상해제,자신,,0,1,1,1,feign_death,1,상대가 잠시 {caster}를 놓칩니다.,다음 공격 회피 판정에 사용,,,,,,,,
rising_windmill_01,HighBard_FeignDeath_Windmill,1,피해,상대,공격력,0,4,1,0,,0,회전 공격이 적중해 총 {damage}의 피해!,"표시 피해 14,263 × 4",,,,,,,,
rising_windmill_02,HighBard_FeignDeath_Windmill,2,브레이크면역,자신,,0,1,1,1,break_immunity,1,역습하는 동안 브레이크되지 않습니다.,원본 효과,,,,,,,,
fine_tuning_01,ExpertBard_FineTuning,1,피해,상대,공격력,0,2,1,0,,0,류트로 연속 타격해 {damage}의 피해!,"표시 피해 7,507 × 2",,,,,,,,
fine_tuning_02,ExpertBard_FineTuning,2,피해,상대,공격력,0,1,1,0,,0,마지막 강타가 {target}에게 {damage}의 피해!,"표시 피해 67,564",,,,,,,,
fine_tuning_03,ExpertBard_FineTuning,3,악상피해증가,자신,,0,1,1,5,fine_tuned,1,악상을 적용받는 스킬의 피해가 10% 증가합니다.,"원본 30초를 5턴으로 변환(6초=1턴, 올림)",,,,,,,,
battle_symphony_01,HighBard_SymphonyOfDestruction,1,지속피해,상대,공격력,0,5,1,5,battle_symphony_dot,1,격렬한 선율이 {target}에게 {damage}의 피해!,"연주 피해 24,023 × 5",,,,,,,,
battle_symphony_02,HighBard_SymphonyOfDestruction,2,피해,상대,공격력,0,1,1,0,,0,연주의 절정이 {target}에게 {damage}의 피해!,"타격 피해 50,298",,,,,,,,
battle_symphony_03,HighBard_SymphonyOfDestruction,3,주는피해증가,자신,,0,1,1,5,battle_symphony,1,배틀 심포니로 주는 피해가 30% 증가합니다.,원본 시너지 효과,,,,,,,,
battle_symphony_04,HighBard_SymphonyOfDestruction,4,브레이크면역,자신,,0,1,1,5,battle_symphony,1,연주 중에는 브레이크되지 않습니다.,원본 효과,,,,,,,,
battle_symphony_05,HighBard_SymphonyOfDestruction,5,쿨다운감소,자신,,2,1,1,0,,0,모든 스킬의 남은 쿨다운이 2턴 감소합니다.,원본 2초 × 5를 배틀용으로 단순화,,,,,,,,
melody_shock_03,HighBard_MelodyShock,3,자원설정,자신,,1,1,1,0,bard_valor,1,연주에 용맹 악상이 깃듭니다.,용맹 악상을 떠올린다. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점),,,,,,,,
feign_death_04,HighBard_FeignDeath,4,자원설정,자신,,1,1,1,0,bard_counter,1,죽은 척하며 반격 악상을 준비합니다.,죽은 척 하기 사용 시 반격 악상. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점),,,,,,,,
battle_symphony_07,HighBard_SymphonyOfDestruction,6,브레이크피해,상대,,1,1,1,0,,0,격정의 선율이 {target}의 균형을 무너뜨립니다!,원본 브레이크 대미지 1칸,,,,,,,,
melody_shock_04,HighBard_MelodyShock,4,브레이크피해,상대,,1,1,1,0,,0,{target}이 크게 휘청입니다!,브레이크 대미지 1칸,,,,,,,,
bards_tale_02,HighBard_BardsTale,1,피해,상대,공격력,0,5,1,0,,0,영웅의 서사가 {target}을 휩씁니다!,"표시 피해 26,499 × 5",,,,,,,,
bards_tale_03,HighBard_BardsTale,2,회복,자신,공격력,0,5,1,0,,0,바즈 테일로 {heal}의 체력을 회복합니다.,"표시 회복 2,062 × 5",,,,,,,,
bards_tale_04,HighBard_BardsTale,3,지속피해,상대,,5040,2,1,2,dizziness,1,{target}이 현기증에 빠집니다.,원본 현기증 1초마다 1120×9초=총 10080을 2턴(9초→2턴)에 5040씩 나눔. 이동 속도 감소 50%는 생략,,,,,,,,
bards_tale_05,HighBard_BardsTale,4,지속회복,자신,,2748,1,1,3,uplift_base,1,{actor}에게 고양이 깃듭니다.,"고양: 공격 속도 15%. 고양 회복 3초마다 1374→턴당 2748. 변주곡과 같이 3턴, 다시 받으면 남은 턴에 더한다(지속방식=누적)",,,,,,,,
heroic_hymn_05,HighBard_BardsTale_Valor,5,자원소모,자신,,0,1,1,0,bard_valor,0,,연주한 용맹 악상을 소모(피해 뒤에 소모해 악상 피해 증가가 적용된다),,,,,,,전부,
counter_overture_05,HighBard_BardsTale_Revenge,5,자원소모,자신,,0,1,1,0,bard_counter,0,,연주한 반격 악상을 소모,,,,,,,전부,
song_of_hope_05,HighBard_BardsTale_Reprise,5,자원소모,자신,,0,1,1,0,bard_hope,0,,연주한 희망 악상을 소모,,,,,,,전부,
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
bards_tale_heroic,HighBard_BardsTale,HighBard_BardsTale_Valor,조건,0,1,자원보유,bard_valor=1,FALSE,재사용 시,용맹 악상이 있으면 바즈 테일 버튼이 용맹의 찬가로 바뀐다(쿨다운과 관계없이 나가고 부모 효과는 실행하지 않음). 변주곡이 악상을 소모한다,100
bards_tale_indomitable,HighBard_BardsTale,HighBard_BardsTale_Revenge,조건,0,1,자원보유,bard_counter=1,FALSE,재사용 시,반격 악상이 있으면 바즈 테일 버튼이 반격의 서곡으로 바뀐다(쿨다운과 관계없이 나가고 부모 효과는 실행하지 않음). 변주곡이 악상을 소모한다,100
bards_tale_hope,HighBard_BardsTale,HighBard_BardsTale_Reprise,조건,0,1,자원보유,bard_hope=1,FALSE,재사용 시,희망 악상이 있으면 바즈 테일 버튼이 희망의 송가로 바뀐다(쿨다운과 관계없이 나가고 부모 효과는 실행하지 않음). 변주곡이 악상을 소모한다,100
feign_death_rising_expire,HighBard_FeignDeath,HighBard_FeignDeath_Windmill,조건,0,1,상태효과보유,feign_death,FALSE,상태만료 시,죽은 척 상태가 끝나면 자동으로 기상 반격,100
feign_death_rising_reuse,HighBard_FeignDeath,HighBard_FeignDeath_Windmill,조건,0,1,상태효과보유,feign_death,FALSE,재사용 시,죽은 척 상태 중 재사용하면 즉시 기상 반격,100
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
ExpertBard_Fever,TRUE,"대상 수 비례 강화는 1:1이라 최솟값으로 고정. ""적중 시"" 궁극기 게이지 보너스는 명중이 아닌 치명타 적중으로 단순화."
ExpertBard_BattleSymphony,TRUE,"즉흥 연주가 악상을 떠올리게 하는 패시브로 본다. 멜로디 쇼크 → 용맹(스킬 효과 행), 브레이크 상태인 적 공격 → 용맹, 연주 스킬(바즈 테일·트리플 스트로크·배틀 심포니) 사용 완료 → 희망, 자신 브레이크 → 반격, 죽은 척 하기 → 반격(스킬 효과 행). 공격 적중이 사용 완료보다 먼저 판정되므로 브레이크된 적을 연주 스킬로 공격하면 용맹이 우선한다. 악상은 먼저 떠올린 하나만 유지(선점). ""즉시 1회 연주할 수 있다""는 바즈 테일 재사용 시 파생(쿨다운 무시)으로 표현한다. 악상 획득 시 재사용 대기 감소는 전체 쿨다운 1턴 감소로 단순화"
ExpertBard_Passive3_Support,TRUE,레벨 30 어시스트 해금 문구는 구현 범위에서 제외(항상 만렙 가정).
ExpertBard_Passive4_Inspiration,TRUE,원문 최대 3중첩은 엔진이 상태 수치 중첩을 지원하지 않아(MaxStacks 미구현) 단일 값으로 단순화.
ExpertBard_Passive5_Frenzy,TRUE,피버 보유 중 조건은 피버가 상시 패시브라 상시 적용으로 단순화.
ExpertBard_Passive6_Invigorate,TRUE,방어도 무시 50%는 대응 효과유형이 없어 구현하지 못함(엔진 한계). 아군 회복은 1:1이라 자기 회복으로 해석.
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
fever_01,ExpertBard_Fever,1,주는피해증가,자신,0,1,1,0,fever_hit,0,피버 효과로 타격 스킬 피해가 증가합니다.,,,,,,,,전투시작,,,원문은 타격 스킬 한정·대상 수 비례지만 스킬 계열 구분·다중 대상 개념이 없어 전역 상시 최소값으로 단순화,
fever_02,ExpertBard_Fever,2,악상피해증가,자신,0,1,1,0,fever_melody,0,피버 효과로 연주 스킬 피해가 증가합니다.,,,,,,,,전투시작,,,연주 스킬 전용 범위는 기존 정밀 조율과 동일한 판정(MelodySkillDamageBonus)을 재사용,
fever_03,ExpertBard_Fever,3,자원증가,자신,1,1,1,0,ultimate_gauge,0,스트링 샷 적중으로 궁극기 게이지를 더 얻습니다.,,,,,,,,치명타적중시,HighBard_StringShot,,원문의 '적중 시'를 치명타 적중으로 단순화,
fever_04,ExpertBard_Fever,4,자원증가,자신,1,1,1,0,ultimate_gauge,0,멜로디 쇼크 적중으로 궁극기 게이지를 더 얻습니다.,,,,,,,,치명타적중시,HighBard_MelodyShock,,원문의 '적중 시'를 치명타 적중으로 단순화,
imp_01,ExpertBard_BattleSymphony,1,자원설정,자신,0,1,1,0,bard_counter,0,쓰러질 뻔한 순간 반격 악상을 떠올립니다!,자신,상태효과유형보유,브레이크,,,,,브레이크발생시,,,자신이 브레이크되면 반격 악상. 브레이크발생시는 양쪽 관점에서 발동하므로 자신이 브레이크 상태일 때만 실행. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점),
imp_02,ExpertBard_BattleSymphony,2,자원설정,자신,1,1,1,0,bard_valor,0,무방비한 적을 보고 용맹 악상을 떠올립니다!,상대,상태효과유형보유,브레이크,,,,,스킬적중완료시,,,브레이크 상태인 적을 스킬로 공격하면 용맹 악상. 스킬사용완료시(희망)보다 먼저 판정되어 우선한다. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점),
imp_03,ExpertBard_BattleSymphony,3,자원설정,자신,1,1,1,0,bard_valor,0,무방비한 적을 보고 용맹 악상을 떠올립니다!,상대,상태효과유형보유,브레이크,,,,,기본공격적중시,,,브레이크 상태인 적을 기본 공격하면 용맹 악상. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점),
imp_04,ExpertBard_BattleSymphony,4,자원설정,자신,1,1,1,0,bard_hope,0,연주를 마치며 희망 악상을 떠올립니다.,,,,,,,,스킬사용완료시,HighBard_TripleStroke,,트리플 스트로크 사용을 끝까지 마치면 희망 악상(스킬 효과 행에서 옮김). 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점),
imp_05,ExpertBard_BattleSymphony,5,자원설정,자신,1,1,1,0,bard_hope,0,연주를 마치며 희망 악상을 떠올립니다.,,,,,,,,스킬사용완료시,HighBard_SymphonyOfDestruction,,배틀 심포니 사용을 끝까지 마치면 희망 악상. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점),
imp_06,ExpertBard_BattleSymphony,6,쿨다운감소,자신,1,1,1,0,,0,악상을 얻어 관련 스킬의 재사용 대기시간이 줄어듭니다.,,,,,,,,자원획득시,,악상,악상에 따라 변화하는 스킬'의 범위가 불명확해 보유자의 전체 쿨다운 1턴 감소로 단순화,
cms_01,ExpertBard_Passive3_Support,1,회복량증가,자신,0,1,1,0,combat_mastery_support_heal,0,전투 숙련: 지원으로 회복량이 증가합니다.,,,,,,,,전투시작,,,,
cms_02,ExpertBard_Passive3_Support,2,멀티히트피해증가,자신,0,1,1,0,combat_mastery_support_multi,0,전투 숙련: 지원으로 다단 공격 피해가 증가합니다.,,,,,,,,전투시작,,,,
bs_01,ExpertBard_Passive4_Inspiration,1,치명타확률증가,자신,0,1,1,5,battlefield_song_stack,3,전장의 노래로 치명타·추가타 확률이 잠시 증가합니다.,,,,,,,,자원획득시,,악상,원문 30초→5턴. 최대 3중첩은 엔진이 상태 수치 중첩을 지원하지 않아 단일 값으로 단순화,
ep_01,ExpertBard_Passive5_Frenzy,1,치명타피해증가,자신,0,1,1,0,ecstatic_performance_crit,0,광란의 연주로 치명타 피해가 증가합니다.,,,,,,,,전투시작,,,피버 보유 중' 조건은 피버가 상시 패시브라 상시 적용으로 단순화,
vit_01,ExpertBard_Passive6_Invigorate,1,지속회복,자신,10920,2,1,2,vitality_regen,0,활력이 넘쳐 체력을 빠르게 회복합니다.,자신,HP비율,,<,0.4,,,회복적용시,,,회복 전 체력 40% 미만일 때 발동. 2730×8을 2턴에 나눠 턴당 10920 지속 회복. 원본 대상별 재발동 45초→8턴. 지속 회복 틱은 활력을 다시 발동시키지 않는다,8
vit_02,ExpertBard_Passive6_Invigorate,2,상태효과,상대,0,1,1,3,weak_point_exposure,0,약점을 노출시켜 받는 치명타 확률이 증가합니다.,,,,,,,,치명타적중시,HighBard_MelodyShock,,"스트링 샷과 같은 약점 노출(치명타 +50%·방어도 무시 50%, 첫 스킬 공격 소모). 원본 15초→3턴",
vit_03,ExpertBard_Passive6_Invigorate,3,상태효과,상대,0,1,1,3,weak_point_exposure,0,약점을 노출시켜 받는 치명타 확률이 증가합니다.,,,,,,,,치명타적중시,ExpertBard_FineTuning,,"스트링 샷과 같은 약점 노출(치명타 +50%·방어도 무시 50%, 첫 스킬 공격 소모). 원본 15초→3턴",
imp_07,ExpertBard_BattleSymphony,7,자원설정,자신,1,1,1,0,bard_hope,0,연주를 마치며 희망 악상을 떠올립니다.,,,,,,,,스킬사용완료시,HighBard_BardsTale,,악상이 없을 때 연주한 바즈 테일 사용을 끝까지 마치면 희망 악상(변주곡은 다른 스킬 ID라 해당 없음). 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점),
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
bard_valor,용맹 악상,악상,1,0,2,선점,TRUE,멜로디 쇼크를 쓰거나 브레이크 상태인 적을 공격하면 떠올리는 음유시인 악상. 바즈 테일이 용맹의 찬가로 바뀌고 찬가가 소모한다. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점). 원본 7초→2턴,
bard_hope,희망 악상,악상,1,0,2,선점,TRUE,연주 스킬(바즈 테일·트리플 스트로크·배틀 심포니) 사용을 끝까지 마치면 떠올리는 음유시인 악상. 바즈 테일이 희망의 송가로 바뀌고 송가가 소모한다. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점). 원본 7초→2턴,
bard_counter,반격 악상,악상,1,0,2,선점,TRUE,음유시인이 브레이크되거나 죽은 척 하기를 쓰면 떠올리는 악상. 바즈 테일이 반격의 서곡으로 바뀌고 서곡이 소모한다. 악상은 하나만 들 수 있어 이미 악상이 있으면 새 악상을 떠올리지 않는다(중첩방식=선점). 원본 7초→2턴,
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
weak_point_exposure,약점 노출,받는치명타확률증가|받는방어무시|첫스킬타격소모,0.5,"대상이 받는 첫 스킬 공격(다단은 첫 타격)의 치명타 확률 +50%, 방어도 50% 무시. 적중하면 소모된다. 스트링 샷·활력이 부여",,,,,,,,
dizziness,현기증,없음,0,지속 피해 상태. 원본 1초마다 1120·9초(총 10080)를 2턴에 나눠 준다. 이동 속도 감소는 생략. 받는 피해 증가 효과는 원본 근거가 없어 제거(2026-09-24),,,,,,,,
fear,두려움,없음,0,대상 행동 시작마다 지속피해를 주는 상태,,,,,,,,
uplift_courage,고양(용맹),쿨다운감소|악상피해증가,1,"용맹의 찬가 고양. 공격 속도 15%(쿨다운 1회 추가 감소), 연주 스킬 위력 +10%, 턴당 지속 회복",,,,누적,1|0.1,,,
uplift_indomitable,고양(반격),쿨다운감소,1,"반격의 서곡 고양. 공격 속도 15%(쿨다운 1회 추가 감소), 더 많은 턴당 지속 회복. 이동 속도 20%는 생략",,,,누적,,,,
uplift_hope,고양(희망),쿨다운감소|주는피해증가|받는피해감소,1,"희망의 송가 고양. 공격 속도 15%(쿨다운 1회 추가 감소), [시너지] 주는 피해 +10%, [시너지] 받는 피해 -10%, 턴당 지속 회복",,,주는피해증가|받는피해감소,누적,1|0.1|0.1,,,
feign_death,죽은 척,받는피해감소|대상해제,0.4,"받는 피해 -40%, 다음 상대 공격은 대상 해제 회피 판정",,,,,,,,
break_immunity,브레이크 면역,브레이크면역,0,라이징 윈드밀 사용 중 브레이크 게이지 피해를 무시,,,,,,,,
fine_tuned,정밀 조율,악상피해증가,0.1,활성 악상이 적용된 스킬의 피해 +10%,,,,,,,,
battle_symphony,격정의 선율,주는피해증가|브레이크면역,0.3,"배틀 심포니 중 주는 피해 +30%, 브레이크 면역",,,주는피해증가,,,,,
battle_symphony_dot,격정의 선율,없음,0,배틀 심포니가 상대에게 남기는 지속 피해 상태,,,,,,,,
break_broken,브레이크,브레이크|받는피해증가,0.2,브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외),,,,,,,,
combat_mastery_support_heal,전투 숙련: 지원,회복량증가,0.1,회복량 +10%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,,,
combat_mastery_support_multi,전투 숙련: 지원,멀티히트피해증가,0.03,적에게 주는 멀티히트(다단) 피해 +3%,,,,,,,,
fever_hit,피버,주는피해증가,0.03,"타격 스킬 피해 +3%(원문 3~15%의 최솟값, 대상 수 비례 강화는 1:1이라 단순화)",,,,,,,,
fever_melody,피버: 연주,악상피해증가,0.02,연주 스킬(바즈 테일 계열) 피해 +2%(원문 2~10%의 최솟값),,,,,,,,
ecstatic_performance_crit,광란의 연주,치명타피해증가,0.2,피버 효과로 치명타 피해 +20%,,,,,,,,
battlefield_song_stack,전장의 노래,치명타확률증가|추가타확률증가,0.03,악상 획득 시 치명타·추가타 확률 +3%. 원문 최대 3중첩은 엔진 한계로 단일 값 적용,,,,,,,,
vitality_regen,활력,없음,0,활력 지속 회복 상태. 보유자 턴마다 고정량을 회복한다,,,,,,,,
uplift_base,고양,쿨다운감소,1,"바즈 테일 고양. 공격 속도 15%(쿨다운 1회 추가 감소), 턴당 지속 회복",,,,누적,,,,
"""",
    };
}
