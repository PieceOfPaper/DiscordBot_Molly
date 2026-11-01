using Molly.Battle;

/// <summary>
/// 마법사의 마나·엘리멘탈 하모니(강화 지속 피해·발동 제한)·엘리멘탈 마스터·아케인 파워·오버서지·마나 스톰(마나 실드·블링크)·마나 인피니티 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-10-03 배틀 시트에서 마법사 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 마법사 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·배틀스킬AI·돌발 이벤트·배틀생활스킬 시트는 석궁사수 사본을 그대로 쓴다.
/// </summary>
internal static class MageBattleTests
{
    private static double Variance(double random) => .9 + random * .2;
    private static int Hit(double shownDamage, double outgoing = 1, double incoming = 1, double random = .9) => (int)Math.Round((shownDamage * .25 - 200 * .5) * outgoing * incoming * Variance(random));

    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow, BattleTestGame.Data);
        LightningTests(data);
        HarmonyTests(data);
        FireBallTelekinesisTests(data);
        ManaStormTests(data);
        InfinityTests(data);
    }

    private static void LightningTests(BattleDataSnapshot data)
    {
        var result = Duel(data, ["HighMage_Lightning"], maxActions: 3);
        var turns = Turns(result);
        Assert(turns[0].Skill == "라이트닝" && turns[0].Damage == Hit(8785) && turns[0].Statuses.Contains("감전")
            && turns[0].Resources.Contains("마나 -5 (현재 95)") && turns[0].Resources.Contains("마나 +1 (현재 96)"),
            "라이트닝은 마나 5를 쓰고 감전을 걸며, 적중하면 메디테이션으로 마나 1을 회복한다");
        Assert(result.Events.Any(x => x.Type == "StatusDamage" && x.Target == "B" && x.Detail == "감전" && x.Amount == (int)Math.Round((1318 * .25 - 100) * 1.03)),
            "감전 상태인 상대는 엘리멘탈 마스터로 받는 피해가 3% 늘어 감전 지속 피해에도 붙는다");
        Assert(turns[1].Skill == "라이트닝" && turns[1].Damage == Hit(8785, 1 + .03 + 5 * .002, 1.03) && turns[1].Resources.Contains("마나 +4 (현재 100)"),
            "다음 행동에는 아케인 파워(전기 +3%)·오버서지(마나 5 → +1%)·감전 대상 피해 증가가 붙고, 메디테이션이 턴마다 마나 12를 회복한다");
    }

    private static void HarmonyTests(BattleDataSnapshot data)
    {
        var enhanced = Duel(data, ["HighMage_Lightning"], maxActions: 2, initial: [("mage_harmony_lightning", 1)]);
        Assert(enhanced.Events.Any(x => x.Type == "StatusDamage" && x.Detail == "감전" && x.Amount == (int)Math.Round((2196 * .25 - 100) * 1.03)),
            "엘리멘탈 하모니로 강화된 라이트닝은 더 강한 감전을 남긴다");

        // 라이트닝(전기)이 냉기·화염 스킬을 강화하므로 이어지는 아이스 대거가 빙결을 건다.
        var chain = Turns(Duel(data, ["HighMage_IceDagger", "HighMage_Lightning"], maxActions: 3));
        Assert(chain[0].Skill == "라이트닝" && chain[1].Skill == "아이스 대거" && chain[1].Statuses.Contains("빙결") && chain[1].Statuses.Contains("둔화")
            && chain[1].Hits == 12 && chain[1].Damage == 6 * Hit(703, 1.09, 1.03) + 6 * Hit(2811, 1.09, 1.03),
            "라이트닝을 쓰면 아이스 대거가 강화되어 빙결을 걸고, 아이스 대거는 둔화와 함께 범위·투사체 여섯 번씩 적중한다(연타 +5%·전기 +3%·마나 5 +1%·감전 대상 +3%)");

        var locked = Turns(Duel(data, ["HighMage_IceDagger", "HighMage_Lightning"], maxActions: 3, initial: [("mage_harmony_lock", 1)]));
        Assert(locked[1].Skill == "아이스 대거" && !locked[1].Statuses.Contains("빙결"),
            "스킬 강화 발동 제한 중에는 다른 속성을 강화하지 않는다");
    }

    private static void FireBallTelekinesisTests(BattleDataSnapshot data)
    {
        // 파이어 볼(하모니 강화)이 브레이크를 걸고 화상을 남긴 뒤, 텔레키네시스 바위 파편은 브레이크 대상 수치로 들어간다.
        var result = Duel(data, ["HighMage_Telekinesis", "HighMage_Fireball"], maxActions: 3, initial: [("mage_harmony_fire", 1)], rules: [("break_gauge_maximum", "1")]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "파이어 볼" && turns[0].Damage == Hit(8855) && turns[0].Broken && turns[0].Statuses.Contains("화염 지대") && turns[0].Statuses.Contains("화상")
            && turns[0].Resources.Contains("마나 -35 (현재 65)"),
            "파이어 볼은 마나 35를 쓰고 넘어뜨려 브레이크 피해를 주며, 화염 지대와 하모니 화상을 남긴다");
        var incoming = 1 + .2 + .03;
        Assert(turns[1].Skill == "텔레키네시스" && turns[1].Hits == 9 && turns[1].Damage == Hit(10190, 1.10, incoming) + 8 * Hit(6325, 1.15, incoming),
            "브레이크된 상대에게 텔레키네시스 바위 파편은 8,433 수치로 들어가고, 아케인 파워(화염)·오버서지(마나 35)·화상 대상 피해 증가가 붙는다");
    }

    private static void ManaStormTests(BattleDataSnapshot data)
    {
        // 상대 마법사의 파이어 볼이 마나 실드 중 브레이크 피해를 주면 블링크로 실드가 터지며 끝난다.
        var broken = Duel(data, ["HighMage_ManaStorm"], maxActions: 2, opponentSkills: ["HighMage_Fireball"]);
        var turns = Turns(broken);
        Assert(turns[0].Skill == "마나 스톰" && turns[0].Hits == 5 && turns[0].Damage == 5 * Hit(878, 1.05) && turns[0].Statuses.Contains("마나 실드") && turns[0].Statuses.Contains("재사용 대기 가속"),
            "마나 스톰은 다섯 번 피해를 주고 마나 실드와 재사용 대기 가속을 얻는다");
        var afterStorm = broken.Events.SkipWhile(x => !(x.Type == "SkillUsed" && x.Actor == "B")).ToArray();
        Assert(afterStorm.Any(x => x.Type == "DamageDealt" && x.Actor == "B" && x.Amount == Hit(8855, incoming: .5))
            && afterStorm.Any(x => x.Type == "StatusApplied" && x.Actor == "A" && x.Detail == "블링크") && afterStorm.Any(x => x.Type == "DamageDealt" && x.Actor == "A" && x.Amount == Hit(2459))
            && afterStorm.Any(x => x.Type == "StatusExpired" && x.Actor == "A" && x.Detail == "마나 실드"),
            "마나 실드는 받는 피해를 50% 줄이고, 브레이크 피해를 받으면 블링크로 실드를 터뜨려 피해를 준 뒤 끝난다");

        // 마나 실드 중 마나 스톰을 다시 쓰면 블링크가 나가고, 마나 스톰 쿨다운이 블링크의 3턴으로 덮여 다음 행동에는 나오지 않는다.
        var reuse = Turns(Duel(data, ["HighMage_ManaStorm"], maxActions: 5));
        Assert(reuse[1].Skill == "마나 스톰" && reuse[1].Hits == 1 && reuse[1].Damage == Hit(2459) && reuse[2].Skill == "(일반 공격)",
            "마나 실드 중 재사용하면 블링크(마나실드 폭발)가 나간다");
    }

    private static void InfinityTests(BattleDataSnapshot data)
    {
        var result = Duel(data, ["HighMage_Lightning", "HighMage_InfinityManaLoop"], maxActions: 6, initial: [("ultimate_gauge", 300)]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "마나 인피니티" && turns[0].Statuses.Contains("마나 인피니티") && turns[0].Damage == 0,
            "마나 인피니티는 피해 없이 2턴 동안 잠재력을 발휘한다");
        Assert(turns[1].Skill == "라이트닝" && turns[1].Hits == 2 && turns[1].Damage == Hit(8785) + Hit(4392) && turns[2].Skill == "라이트닝" && turns[2].Hits == 2,
            "마나 인피니티 중 라이트닝은 두 번(두 번째 50%) 발사하고 쿨다운이 빨라져 매 행동 다시 쓴다");
        Assert(result.Events.Count(x => x.Type == "StatusDamage" && x.Detail == "감전" && x.Amount == (int)Math.Round((2196 * .25 - 100) * 1.03)) == 2,
            "마나 인피니티 중 라이트닝은 계속 엘리멘탈 하모니로 강화되어 강한 감전을 남긴다");
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses, int Hits, bool Broken);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, (string Id, int Value)[]? initial = null, (string Id, string Value)[]? rules = null, string[]? opponentSkills = null)
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
                ["mage"] = data.Classes["mage"] with { SkillIds = skills },
                ["idle"] = opponentSkills is null ? new("idle", "대상", Array.Empty<string>()) : data.Classes["mage"] with { Id = "idle", SkillIds = opponentSkills }
            },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, SkillAiRules = data.SkillAiRules, LoadedAt = data.LoadedAt
        };
        // 마법사가 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "mage", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
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

    public static readonly IReadOnlyDictionary<string, string> Sheets = new Dictionary<string, string>(CrossbowBattleTests.Sheets.Where(x => x.Key is "배틀규칙" or "배틀이름교체" or "배틀스킬AI" or "배틀돌발이벤트" or "배틀돌발이벤트효과" or "배틀생활스킬" or "배틀생활스킬효과"))
    {
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고,궁극기문구,부모스킬ID,이름
HighMage_Lightning,TRUE,공격·방해,상대,1,0,65,mage_mana,5,,FALSE,번개 줄기로 피해를 주고 감전시킨다. 엘리멘탈 하모니로 강화되면 더 강한 감전을 남긴다.,{caster}가 맹렬한 번개 줄기를 {target}에게 뿜어냅니다!,"원본 대미지 11,713, 감전 3,514(하모니 5,856), 마나 5, 재사용 대기 0.5초→1턴. 피해 원본 ×0.75. 연쇄 3회는 1:1이라 대상 하나. 감전 지속 시간은 원본에 없어 2턴으로 나눔. 마나를 비용으로 쓰므로 궁극기 게이지는 효과 행으로 적립",,,
HighMage_Fireball,TRUE,공격·방해,상대,2,0,60,mage_mana,35,,FALSE,커다란 화염구로 피해와 브레이크 피해를 주고 화염 지대를 남긴다. 엘리멘탈 하모니로 강화되면 화상을 입힌다.,{caster}가 커다란 화염구를 {target}에게 힘껏 던집니다!,"원본 대미지 11,807, 화염 지대 1초마다 1,147×5초(1턴에 5,735), 하모니 화상 9,839, 마나 35, 재사용 대기 10초→2턴. 피해 원본 ×0.75. 넘어뜨리기는 브레이크 1칸. 최대 스택 3은 충전 개념이 없어 제외. 화상 지속 시간은 원본에 없어 2턴으로 나눔",,,
HighMage_IceDagger,TRUE,공격·방해,상대,1,0,65,mage_mana,7,,FALSE,회전하는 얼음 파편 여섯 개로 휩쓸고 발사해 피해를 주고 둔화시킨다. 엘리멘탈 하모니로 강화되면 빙결시킨다.,{caster}가 얼음 파편 여섯 개를 불러 {target}에게 쏘아 보냅니다!,"원본 범위 대미지 937×6, 투사체 대미지 3,748×6, 하모니 빙결 8,902, 이동 속도 감소 50% 6초→1턴(쿨다운증가), 마나 7, 재사용 대기 5초→1턴. 피해 원본 ×0.75. 여러 명 분산 발사는 1:1이라 모두 한 대상. 빙결 지속 시간은 원본에 없어 2턴으로 나눔",,,
HighMage_Telekinesis,TRUE,공격,상대,2,0,60,mage_mana,15,,FALSE,마력 충격으로 밀쳐낸 뒤 바위 파편 여덟 개를 몰아친다. 브레이크된 상대에게는 바위 파편이 더 큰 피해를 준다.,{caster}가 무형의 마력 충격으로 {target}을 밀쳐내고 바위 파편을 몰아칩니다!,"원본 최초 대미지 13,587, 바위 파편 5,622×8(브레이크된 적 8,433×8), 마나 15, 재사용 대기 7.5초→2턴. 피해 원본 ×0.75. 밀쳐내기는 다른 클래스와 같이 생략(브레이크 없음)",,,
HighMage_ManaStorm,TRUE,생존·보조,자신·상대,4,0,60,궁극기 게이지,,150,FALSE,마나 폭풍으로 피해를 주며 마나를 회복하고 재사용 대기를 가속한다. 받는 피해를 줄이는 마나 실드를 얻고 엘리멘탈 하모니를 한 번 활성화한다.,{caster}가 순환하는 마나를 가속시켜 거센 마나 폭풍을 일으킵니다!,"원본 대미지 1초마다 780~1,561×5초(평균 1,171×5), 마나 1초마다 15%×5초=75, 재사용 대기 회복 속도 +50% 5초→1턴(다음 행동 쿨다운 1 추가 감소, 마나 스톰 제외), 마나 실드 받는 대미지 -50% 20초→4턴, 재사용 대기 23.375초→4턴. 피해 원본 ×0.75. 마나 실드 중 다시 쓰면(재사용 시 파생) 블링크, 브레이크 피해를 받으면 패시브 메디테이션 행이 블링크 폭발 후 실드를 해제. 엘리멘탈 하모니 한 번 활성화는 세 속성 모두 강화로 해석",,,
HighMage_ManaStorm_Blink,TRUE,생존,상대,3,0,0,,,,FALSE,마나 실드를 터뜨려 주변에 피해를 주고 뒤로 순간 이동한다.,{caster}가 마나 실드를 터뜨리며 순식간에 뒤로 물러납니다!,"원본 마나실드 폭발 대미지 3,279. 피해 원본 ×0.75. 마나 실드 중 마나 스톰 재사용 시 파생. 재사용 파생은 부모 쿨다운을 이 행의 기본쿨다운으로 덮으므로, 마나 실드가 끝나기 전에 마나 스톰이 다시 나오지 않게 3턴. 브레이크 피해로 발동하는 블링크는 패시브 메디테이션 행",,HighMage_ManaStorm,
HighMage_InfinityManaLoop,TRUE,궁극기·보조,자신,0,0,100,궁극기 게이지,300,,TRUE,"모든 스킬의 재사용 대기를 초기화하고 마나를 가득 채운다. 2턴 동안 재사용 대기가 빨라지고, 라이트닝·파이어 볼·아이스 대거가 항상 엘리멘탈 하모니로 강화되며 라이트닝·파이어 볼·텔레키네시스가 더 많이 발사된다.",{caster}가 미지의 가능성을 일깨워 마나를 끝없이 순환시킵니다!,"원본 지속 12초→2턴, 재사용 대기 초기화 100%(쿨다운감소 99), 마나 회복 100%, 지속 마나 1초마다 100(턴당 100), 캐스팅 시간 -50%(턴제에 시전 시간이 없어 2턴 동안 쿨다운 1 추가 감소로 근사), 라이트닝 2회(두 번째 50%), 파이어 볼 3개(중복 타격 50%), 텔레키네시스 바위 12개, 하모니 발동: 라이트닝·파이어 볼·아이스 대거. 궁극기 자체 피해는 원본에 없다. 엠블럼 장착 필요는 사용 조건이 아니라 제외",『끝없는 가능성을 일깨워 한계를 뛰어넘어라』,,
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
mana_storm_blink_reuse,HighMage_ManaStorm,HighMage_ManaStorm_Blink,조건,0,1,상태효과보유,mage_mana_shield,FALSE,재사용 시,마나 실드 중 마나 스톰을 다시 쓰면 블링크가 나간다. 재사용 시 상태효과보유 조건이라 마나 실드는 엔진이 해제한다,100
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
ltn_01,HighMage_Lightning,1,피해,상대,,8785,1,1,0,,0,,"원본 11,713",,,,,,,,
ltn_02,HighMage_Lightning,2,피해,상대,,4392,1,1,0,,0,,"마나 인피니티: 라이트닝 2회 발사, 두 번째 대미지 50%",자신,상태효과보유,mage_infinity,,,,,
ltn_03,HighMage_Lightning,3,상태해제,상대,,0,1,1,0,mage_shock,0,,하모니 감전이 기본 감전을 대신한다,자신,자원보유,mage_harmony_lightning,>=,1,,,
ltn_04,HighMage_Lightning,4,지속피해,상대,,2196,1,1,2,mage_shock_harmony,1,감전,"엘리멘탈 하모니 감전 5,856 → 2턴",자신,자원보유,mage_harmony_lightning,>=,1,,,
ltn_05,HighMage_Lightning,5,지속피해,상대,,1318,1,1,2,mage_shock,1,감전,"감전 3,514 → 2턴. 더 강한 하모니 감전이 있으면 걸지 않음",상대,상태효과미보유,mage_shock_harmony,,,,,
ltn_06,HighMage_Lightning,6,자원소모,자신,,0,1,1,0,mage_harmony_lightning,0,,강화를 사용했다,,,,,,,전부,
ltn_07,HighMage_Lightning,7,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,궁극기 게이지는 마나를 비용으로 쓰는 스킬이라 효과 행으로 적립,,,,,,,,
fbl_01,HighMage_Fireball,1,피해,상대,,8855,1,1,0,,0,,"원본 11,807",,,,,,,,
fbl_02,HighMage_Fireball,2,피해,상대,,4428,2,1,0,,0,,"마나 인피니티: 파이어 볼 3개, 중복 타격 대미지 50%",자신,상태효과보유,mage_infinity,,,,,
fbl_03,HighMage_Fireball,3,브레이크피해,상대,,1,1,1,0,,0,,폭발에 휘말려 넘어짐 → 브레이크 1칸,,,,,,,,
fbl_04,HighMage_Fireball,4,지속피해,상대,,4301,1,1,1,mage_fire_zone,1,화염 지대,"화염 지대 1초마다 1,147×5초 → 1턴",,,,,,,,
fbl_05,HighMage_Fireball,5,지속피해,상대,,3690,1,1,2,mage_burn,1,화상,"엘리멘탈 하모니 화상 9,839 → 2턴",자신,자원보유,mage_harmony_fire,>=,1,,,
fbl_06,HighMage_Fireball,6,자원소모,자신,,0,1,1,0,mage_harmony_fire,0,,강화를 사용했다,,,,,,,전부,
fbl_07,HighMage_Fireball,7,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,궁극기 게이지는 마나를 비용으로 쓰는 스킬이라 효과 행으로 적립,,,,,,,,
idg_01,HighMage_IceDagger,1,피해,상대,,703,6,1,0,,0,,범위 대미지 937×6,,,,,,,,
idg_02,HighMage_IceDagger,2,피해,상대,,2811,6,1,0,,0,,"투사체 대미지 3,748×6",,,,,,,,
idg_03,HighMage_IceDagger,3,상태효과,상대,,0,1,1,1,mage_slowed,1,,이동 속도 감소 50% 6초→1턴,,,,,,,,
idg_04,HighMage_IceDagger,4,지속피해,상대,,3338,1,1,2,mage_frost,1,빙결,"엘리멘탈 하모니 빙결 8,902 → 2턴",자신,자원보유,mage_harmony_ice,>=,1,,,
idg_05,HighMage_IceDagger,5,자원소모,자신,,0,1,1,0,mage_harmony_ice,0,,강화를 사용했다,,,,,,,전부,
idg_06,HighMage_IceDagger,6,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,궁극기 게이지는 마나를 비용으로 쓰는 스킬이라 효과 행으로 적립,,,,,,,,
tlk_01,HighMage_Telekinesis,1,피해,상대,,10190,1,1,0,,0,,"최초 대미지 13,587",,,,,,,,
tlk_02,HighMage_Telekinesis,2,피해,상대,,4216,8,1,0,,0,,"바위 파편 5,622×8",상대,상태효과유형미보유,브레이크,,,,,
tlk_03,HighMage_Telekinesis,3,피해,상대,,6325,8,1,0,,0,,"브레이크된 적 공격 시 바위 파편 8,433×8",상대,상태효과유형보유,브레이크,,,,,
tlk_04,HighMage_Telekinesis,4,피해,상대,,4216,4,1,0,,0,,마나 인피니티: 바위 파편 12개(4개 추가). 브레이크 대상 수치는 구분하지 않음,자신,상태효과보유,mage_infinity,,,,,
tlk_05,HighMage_Telekinesis,5,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,궁극기 게이지는 마나를 비용으로 쓰는 스킬이라 효과 행으로 적립,,,,,,,,
mst_01,HighMage_ManaStorm,1,피해,상대,,878,5,1,0,,0,,"1초마다 780~1,561(평균 1,171)×5초",,,,,,,,
mst_02,HighMage_ManaStorm,2,자원증가,자신,,75,1,1,0,mage_mana,0,,마나 1초마다 15%×5초,,,,,,,,
mst_03,HighMage_ManaStorm,3,상태효과,자신,,0,1,1,1,mage_storm_haste,1,,재사용 대기 회복 속도 +50% 5초→1턴,,,,,,,,
mst_04,HighMage_ManaStorm,4,상태효과,자신,,0,1,1,1,mage_storm_haste_self,1,,마나 스톰 자신은 가속에서 제외,,,,,,,,
mst_05,HighMage_ManaStorm,5,상태효과,자신,,0,1,1,4,mage_mana_shield,1,,마나 실드 받는 대미지 -50% 20초→4턴,,,,,,,,
mst_06,HighMage_ManaStorm,6,자원설정,자신,,1,1,1,0,mage_harmony_fire,0,,엘리멘탈 하모니 한 번 활성화(세 속성 모두),,,,,,,,
mst_07,HighMage_ManaStorm,7,자원설정,자신,,1,1,1,0,mage_harmony_ice,0,,엘리멘탈 하모니 한 번 활성화(세 속성 모두),,,,,,,,
mst_08,HighMage_ManaStorm,8,자원설정,자신,,1,1,1,0,mage_harmony_lightning,0,,엘리멘탈 하모니 한 번 활성화(세 속성 모두),,,,,,,,
msb_01,HighMage_ManaStorm_Blink,1,피해,상대,,2459,1,1,0,,0,,"마나실드 폭발 대미지 3,279",,,,,,,,
mif_01,HighMage_InfinityManaLoop,1,쿨다운감소,자신,,99,1,1,0,,0,모든 스킬의 재사용 대기 시간이 초기화됩니다!,모든 스킬 재사용 대기 초기화(큰 값으로 근사),,,,,,,,
mif_02,HighMage_InfinityManaLoop,2,자원설정,자신,,100,1,1,0,mage_mana,0,,마나 회복 100%,,,,,,,,
mif_03,HighMage_InfinityManaLoop,3,상태효과,자신,,0,1,1,2,mage_infinity,1,,잠재력 12초→2턴,,,,,,,,
mif_04,HighMage_InfinityManaLoop,4,상태효과,자신,,0,1,1,2,mage_infinity_mana,1,,지속 마나 회복 1초마다 100 → 턴당 100,,,,,,,,
mif_05,HighMage_InfinityManaLoop,5,자원설정,자신,,1,1,1,0,mage_harmony_fire,0,,엘리멘탈 하모니 발동: 파이어 볼,,,,,,,,
mif_06,HighMage_InfinityManaLoop,6,자원설정,자신,,1,1,1,0,mage_harmony_ice,0,,엘리멘탈 하모니 발동: 아이스 대거,,,,,,,,
mif_07,HighMage_InfinityManaLoop,7,자원설정,자신,,1,1,1,0,mage_harmony_lightning,0,,엘리멘탈 하모니 발동: 라이트닝,,,,,,,,
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
HighMage_Meditation,TRUE,"지속 마나 회복 1초마다 2 → 턴당 12, 스킬 적중 1(1:1이라 적중한 적 하나), 기본 공격 적중 3. 캐스팅 속도에 따른 회복 가속·재사용 대기 감소는 수치가 없어 제외. 마나 스톰 마나 실드 중 브레이크 피해를 받으면 블링크(폭발 피해 후 실드 해제)도 이 패시브 행으로 처리(발동시점 브레이크피격시)"
HighMage_EnchantMagic,TRUE,"화염·냉기·전기 스킬(파이어 볼·아이스 대거·라이트닝)을 쓰면 다른 두 속성 스킬을 강화(3턴, 가정). 강화된 스킬은 화상·빙결·강한 감전을 추가로 준다. 스킬 강화 발동 제한 7초→2턴. 마나 소모량 감소(중첩당 2.5%)는 마나가 행동을 거의 막지 않아 제외"
HighMage_Multihit_Passive_03,TRUE,멀티히트 대미지 +5%(화염술사와 같은 fire_mastery_multi 상태 재사용). 레벨 30 어시스트 해금은 제외
HighMage_ElementalMaster_Passive_04,TRUE,화상·빙결·감전 상태인 적에게 주는 피해 각각 +3%. 1:1이라 지속 피해를 건 스킬이 적중하면 같은 2턴 동안 상대에게 받는 피해 +3% 숨김 상태를 건다(상대가 이미 그 지속 피해를 가진 채 맞으면 함께 갱신)
HighMage_ElementalList_Passive_05,TRUE,화염·냉기·전기 피해를 주면 각각 공격력 +3%(최대 9%). 5초지만 지속 피해도 속성 피해라 2턴. 원소 스킬 적중 시에만 판정(지속 피해 틱은 트리거가 없음)
HighMage_OverSurge_Passive_06,TRUE,"소모한 마나 1당 오버서지 1중첩(최대 50, 중첩마다 5초→1턴, 개별), 중첩당 스킬 피해 +0.2%. 스킬 사용 완료 시 쌓여 다음 행동에 적용. Lv.60 해금은 항상 충족으로 가정"
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
med_01,HighMage_Meditation,1,턴당자원증가,자신,0,1,1,0,mage_meditation,0,,,,,,,,,전투시작,,,지속 마나 회복 1초마다 2 → 턴당 12,
med_02,HighMage_Meditation,2,자원증가,자신,1,1,1,0,mage_mana,0,,,,,,,,,스킬적중완료시,,,스킬 적중마다 마나 1(1:1이라 한 명),
med_03,HighMage_Meditation,3,자원증가,자신,3,1,1,0,mage_mana,0,,,,,,,,,기본공격적중시,,,기본 공격 적중 시 마나 3,
med_04,HighMage_Meditation,4,상태효과,자신,0,1,1,1,mage_blink,0,,자신,상태효과보유,mage_mana_shield,,,,,브레이크피격시,,,블링크 로그 표시용,
med_05,HighMage_Meditation,5,피해,상대,2459,1,1,0,,0,,자신,상태효과보유,mage_mana_shield,,,,,브레이크피격시,,,"마나 실드 중 브레이크 피해를 받으면 블링크: 마나실드 폭발 3,279, 원본 ×0.75",
med_06,HighMage_Meditation,6,상태해제,자신,0,1,1,0,mage_blink,0,,,,,,,,,브레이크피격시,,,표시용 상태 해제,
med_07,HighMage_Meditation,7,상태해제,자신,0,1,1,0,mage_mana_shield,0,,자신,상태효과보유,mage_mana_shield,,,,,브레이크피격시,,,블링크로 마나 실드 종료,
ehm_01,HighMage_EnchantMagic,1,자원설정,자신,1,1,1,0,mage_harmony_fire,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_Lightning,,전기 → 화염 강화,
ehm_02,HighMage_EnchantMagic,2,자원설정,자신,1,1,1,0,mage_harmony_ice,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_Lightning,,전기 → 냉기 강화,
ehm_03,HighMage_EnchantMagic,3,자원설정,자신,1,1,1,0,mage_harmony_lock,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_Lightning,,발동 제한 7초→2턴,
ehm_04,HighMage_EnchantMagic,4,자원설정,자신,1,1,1,0,mage_harmony_ice,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_Fireball,,화염 → 냉기 강화,
ehm_05,HighMage_EnchantMagic,5,자원설정,자신,1,1,1,0,mage_harmony_lightning,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_Fireball,,화염 → 전기 강화,
ehm_06,HighMage_EnchantMagic,6,자원설정,자신,1,1,1,0,mage_harmony_lock,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_Fireball,,발동 제한 7초→2턴,
ehm_07,HighMage_EnchantMagic,7,자원설정,자신,1,1,1,0,mage_harmony_fire,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_IceDagger,,냉기 → 화염 강화,
ehm_08,HighMage_EnchantMagic,8,자원설정,자신,1,1,1,0,mage_harmony_lightning,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_IceDagger,,냉기 → 전기 강화,
ehm_09,HighMage_EnchantMagic,9,자원설정,자신,1,1,1,0,mage_harmony_lock,0,,자신,자원보유,mage_harmony_lock,<=,0,,,스킬사용완료시,HighMage_IceDagger,,발동 제한 7초→2턴,
ehm_10,HighMage_EnchantMagic,10,자원설정,자신,1,1,1,0,mage_harmony_lightning,0,,자신,상태효과보유,mage_infinity,,,,,스킬사용완료시,HighMage_Lightning,,마나 인피니티 중에는 라이트닝이 계속 강화,
ehm_11,HighMage_EnchantMagic,11,자원설정,자신,1,1,1,0,mage_harmony_fire,0,,자신,상태효과보유,mage_infinity,,,,,스킬사용완료시,HighMage_Fireball,,마나 인피니티 중에는 파이어 볼이 계속 강화,
ehm_12,HighMage_EnchantMagic,12,자원설정,자신,1,1,1,0,mage_harmony_ice,0,,자신,상태효과보유,mage_infinity,,,,,스킬사용완료시,HighMage_IceDagger,,마나 인피니티 중에는 아이스 대거가 계속 강화,
cmfm_01,HighMage_Multihit_Passive_03,1,멀티히트피해증가,자신,0,1,1,0,fire_mastery_multi,0,,,,,,,,,전투시작,,,멀티히트 대미지 +5%,
emt_01,HighMage_ElementalMaster_Passive_04,1,상태효과,상대,0,1,1,2,mage_master_shock,0,,,,,,,,,스킬적중완료시,HighMage_Lightning,,감전 상태인 적에게 주는 피해 +3%(라이트닝은 항상 감전을 건다),
emt_02,HighMage_ElementalMaster_Passive_04,2,상태효과,상대,0,1,1,2,mage_master_burn,0,,상대,상태효과보유,mage_burn,,,,,스킬적중완료시,HighMage_Fireball,,화상 상태인 적에게 주는 피해 +3%,
emt_03,HighMage_ElementalMaster_Passive_04,3,상태효과,상대,0,1,1,2,mage_master_frost,0,,상대,상태효과보유,mage_frost,,,,,스킬적중완료시,HighMage_IceDagger,,빙결 상태인 적에게 주는 피해 +3%,
arc_01,HighMage_ElementalList_Passive_05,1,상태효과,자신,0,1,1,2,mage_arcane_lightning,0,,,,,,,,,스킬적중완료시,HighMage_Lightning,,전기 피해 → 공격력 +3%,
arc_02,HighMage_ElementalList_Passive_05,2,상태효과,자신,0,1,1,2,mage_arcane_fire,0,,,,,,,,,스킬적중완료시,HighMage_Fireball,,화염 피해 → 공격력 +3%,
arc_03,HighMage_ElementalList_Passive_05,3,상태효과,자신,0,1,1,2,mage_arcane_ice,0,,,,,,,,,스킬적중완료시,HighMage_IceDagger,,냉기 피해 → 공격력 +3%,
ovs_01,HighMage_OverSurge_Passive_06,1,주는피해증가,자신,0,1,1,0,mage_surge_power,0,,,,,,,,,전투시작,,,"중첩자원ID=mage_surge, 중첩당 +0.2%",
ovs_02,HighMage_OverSurge_Passive_06,2,자원증가,자신,5,1,1,0,mage_surge,0,,,,,,,,,스킬사용완료시,HighMage_Lightning,,마나 5 소모,
ovs_03,HighMage_OverSurge_Passive_06,3,자원증가,자신,35,1,1,0,mage_surge,0,,,,,,,,,스킬사용완료시,HighMage_Fireball,,마나 35 소모,
ovs_04,HighMage_OverSurge_Passive_06,4,자원증가,자신,7,1,1,0,mage_surge,0,,,,,,,,,스킬사용완료시,HighMage_IceDagger,,마나 7 소모,
ovs_05,HighMage_OverSurge_Passive_06,5,자원증가,자신,15,1,1,0,mage_surge,0,,,,,,,,,스킬사용완료시,HighMage_Telekinesis,,마나 15 소모,
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
mage_mana,마나,자원,100,100,0,가산,TRUE,"마법사 마나(최대 100으로 가정, 마나 스톰 1초마다 15%·인피니티 1초마다 100 기준). 라이트닝 5·파이어 볼 35·아이스 대거 7·텔레키네시스 15를 비용으로 쓴다. 메디테이션으로 턴마다 12, 스킬 적중 1, 기본 공격 3을 회복한다.",
mage_harmony_fire,엘리멘탈 하모니: 화염,하모니,1,0,3,교체,TRUE,라이트닝·아이스 대거 사용으로 강화된 파이어 볼. 다음 파이어 볼이 화상을 입히고 소모한다. 강화 시간은 원본에 없어 3턴으로 가정.,TRUE
mage_harmony_ice,엘리멘탈 하모니: 냉기,하모니,1,0,3,교체,TRUE,라이트닝·파이어 볼 사용으로 강화된 아이스 대거. 다음 아이스 대거가 빙결시키고 소모한다. 강화 시간은 원본에 없어 3턴으로 가정.,TRUE
mage_harmony_lightning,엘리멘탈 하모니: 전기,하모니,1,0,3,교체,TRUE,파이어 볼·아이스 대거 사용으로 강화된 라이트닝. 다음 라이트닝이 강한 감전을 남기고 소모한다. 강화 시간은 원본에 없어 3턴으로 가정.,TRUE
mage_harmony_lock,엘리멘탈 하모니 발동 제한,자원,1,0,2,교체,TRUE,스킬 강화 발동 제한 7초→2턴. 하모니로 다른 속성을 강화한 뒤 2턴 동안은 다시 강화하지 않는다.,TRUE
mage_surge,오버서지,중첩,50,0,1,개별,TRUE,"소모한 마나 1당 1중첩(최대 50, 중첩마다 5초→1턴). 중첩당 스킬 피해 +0.2%.",TRUE
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
break_broken,브레이크,브레이크|받는피해증가,0.2,브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외),,,,,,,,
fire_mastery_multi,전투 숙련: 기교,멀티히트피해증가,0.05,적에게 주는 멀티히트(다단) 피해 +5%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,,,
mage_shock,감전,없음,0,"라이트닝 감전(3,514 → 2턴).",,,,,,,,
mage_shock_harmony,감전,없음,0,"엘리멘탈 하모니로 강화된 라이트닝 감전(5,856 → 2턴). 기본 감전을 대신한다.",,,,,,,,
mage_burn,화상,없음,0,"엘리멘탈 하모니로 강화된 파이어 볼 화상(9,839 → 2턴).",,,,,,,,
mage_frost,빙결,없음,0,"엘리멘탈 하모니로 강화된 아이스 대거 빙결(8,902 → 2턴).",,,,,,,,
mage_master_shock,엘리멘탈 마스터: 감전,받는피해증가,0.03,감전 상태인 적에게 주는 피해 +3%. 1:1이라 감전과 같은 2턴 동안 받는 피해 증가로 표현.,,,,,,,,TRUE
mage_master_burn,엘리멘탈 마스터: 화상,받는피해증가,0.03,화상 상태인 적에게 주는 피해 +3%. 화상과 같은 2턴 동안 받는 피해 증가로 표현.,,,,,,,,TRUE
mage_master_frost,엘리멘탈 마스터: 빙결,받는피해증가,0.03,빙결 상태인 적에게 주는 피해 +3%. 빙결과 같은 2턴 동안 받는 피해 증가로 표현.,,,,,,,,TRUE
mage_fire_zone,화염 지대,없음,0,"파이어 볼 화염 지대(1초마다 1,147×5초 → 1턴). 원본 지속 피해: 화상과는 다른 지대라 엘리멘탈 마스터 대상이 아니다.",,,,,,,,
mage_slowed,둔화,쿨다운증가,1,아이스 대거 이동 속도 감소 50%(6초→1턴). 지속 중 상대의 쿨다운이 줄지 않는다(석궁사수·궁수 둔화와 같은 단순화).,,,,,,,,
mage_mana_shield,마나 실드,받는피해감소,0.5,"마나 스톰 마나 실드(받는 대미지 -50%, 20초→4턴). 마나 스톰을 다시 쓰거나(블링크) 브레이크 피해를 받으면 끝난다.",,,,,,,,
mage_blink,블링크,없음,0,마나 실드 중 브레이크 피해를 받아 실드를 터뜨리며 뒤로 순간 이동한다. 로그 표시용으로 걸었다가 바로 해제한다.,,,,,,,,
mage_storm_haste,재사용 대기 가속,쿨다운감소,1,마나 스톰 재사용 대기 회복 속도 +50%(5초→1턴). 다음 행동에 쿨다운 1 추가 감소.,,,,,,,,
mage_storm_haste_self,재사용 대기 가속: 마나 스톰 제외,쿨다운증가,1,재사용 대기 가속은 마나 스톰을 제외한다. 마나 스톰 쿨다운의 추가 감소를 상쇄한다.,HighMage_ManaStorm,,,,,,,TRUE
mage_meditation,메디테이션,턴당자원증가,12,전투 시작에 거는 영구 상태. 지속 마나 회복 1초마다 2 → 턴당 12.,,,,,,mage_mana,,TRUE
mage_arcane_fire,아케인 파워: 화염,주는피해증가,0.03,화염 피해를 주면 공격력 +3%(5초). 화상·화염 지대도 화염 피해라 지속 피해가 이어지는 동안을 감안해 2턴.,,,,,,,,TRUE
mage_arcane_ice,아케인 파워: 냉기,주는피해증가,0.03,냉기 피해를 주면 공격력 +3%(5초). 빙결 지속 피해를 감안해 2턴.,,,,,,,,TRUE
mage_arcane_lightning,아케인 파워: 전기,주는피해증가,0.03,전기 피해를 주면 공격력 +3%(5초). 감전 지속 피해를 감안해 2턴.,,,,,,,,TRUE
mage_surge_power,오버서지,주는피해증가,0.002,오버서지 중첩당 스킬 피해 +0.2%(최대 50중첩 10%).,,mage_surge,,,,,,TRUE
mage_infinity,마나 인피니티,쿨다운감소,1,"마나 인피니티(12초→2턴). 캐스팅 시간 -50%를 쿨다운 1 추가 감소로 근사하고, 라이트닝·파이어 볼·아이스 대거가 항상 엘리멘탈 하모니로 강화되며 라이트닝 2회·파이어 볼 3개·바위 파편 12개를 발사한다.",,,,,,,,
mage_infinity_mana,마나 인피니티: 마나,턴당자원증가,100,마나 인피니티 지속 마나 회복 1초마다 100 → 턴당 100.,,,,,,mage_mana,,TRUE
"""",
    };
}
