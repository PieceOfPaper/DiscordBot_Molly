using Molly.Battle;

/// <summary>
/// 전사의 가드·카운터 어택→아마란스 킥·투지 최고조 강화·블레이드 스매시→퀘이크(브레이크 익스텐드)·블레이드 임팩트 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-10-02 배틀 시트에서 전사 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 전사 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·배틀스킬AI·돌발 이벤트·생활스킬 시트는 석궁사수 사본을 그대로 쓴다.
/// </summary>
internal static class WarriorBattleTests
{
    private static double Variance(double random) => .9 + random * .2;
    private static int Hit(double shownDamage, double random = .9, double outgoing = 1, double incoming = 1, double critical = 1) => (int)Math.Round((shownDamage * .25 - 200 * .5) * outgoing * incoming * Variance(random) * critical);

    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow);
        GuardTests(data);
        FightingSpiritTests(data);
        QuakeTests(data);
        BladeImpactTests(data);
        SmashHitSpiritTests(data);
    }

    private static void GuardTests(BattleDataSnapshot data)
    {
        // 난수 0.3: 방패 치기의 기절(50%)과 가드(50%), 카운터 어택(50%)이 모두 성공하고 치명타·추가타는 나지 않는다.
        var result = Duel(data, ["shield_bash"], maxActions: 3, random: .3);
        var turns = Turns(result);
        Assert(turns[0].Skill == "방패 치기" && turns[0].Damage == Hit(7085, .3) && turns[0].Statuses.Contains("가드") && turns[0].Statuses.Contains("브레이크 면역")
            && result.Events.Any(x => x.Type == "BreakGaugeChanged" && x.Target == "B" && x.Amount == 1),
            "방패 치기는 피해를 주고 가드·브레이크 면역을 얻으며, 기절(브레이크 1칸)은 50% 확률이다");
        // 상대 일반 공격(1,500 − 방어 100)을 가드로 막으면 전투 숙련: 수호 15%에 더해 50%를 덜 받는다.
        var guardedHit = result.Events.SkipWhile(x => x.Type != "AttackGuarded").Skip(1).First(x => x.Type == "DamageDealt");
        Assert(result.Events.Any(x => x.Type == "AttackGuarded" && x.Actor == "B" && x.Target == "A") && guardedHit.Amount == (int)Math.Round(1400 * .85 * .5 * Variance(.3)),
            "가드에 성공하면 그 타격의 피해가 50% 줄어든다");
        Assert(new[] { "카운터 어택 +1 (현재 1)", "투지 +2 (현재 12)", "복수심 +1 (현재 1)" }.All(detail => result.Events.Any(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail == detail)),
            "가드에 성공하면 카운터 어택(50%)·투지 2·복수심 1중첩을 얻는다");
        Assert(turns[1].Skill == "아마란스 킥" && turns[1].Damage == Hit(15973, .3, outgoing: 1.05, critical: 1.5) && turns[1].Resources.Contains("카운터 어택 -1 (현재 0)"),
            "카운터 어택이 발동하면 방패 치기 쿨다운 중에도 아마란스 킥이 나가 반드시 치명타로 적중하고(복수심 +5%) 표식을 소모한다");

        // 투지 최고조 방패 치기는 가드 확률이 100%라 난수 0.9에서도 막는다. 카운터 어택(50%)은 실패해 다음 행동은 일반 공격이다.
        var full = Duel(data, ["shield_bash"], maxActions: 3, initial: [("warrior_spirit", 50)]);
        var fullTurns = Turns(full);
        Assert(fullTurns[0].Damage == Hit(9919) && fullTurns[0].Statuses.Contains("완벽한 가드") && full.Events.Any(x => x.Type == "AttackGuarded" && x.Target == "A")
            && fullTurns[1].Skill == "(일반 공격)",
            "투지 최고조 방패 치기는 더 세게 치고 모든 공격을 막으며, 카운터 어택이 나지 않으면 아마란스 킥도 없다");
    }

    private static void FightingSpiritTests(BattleDataSnapshot data)
    {
        // 투지 35에서 연속 베기(투지 15)로 50이 되면, 다음 연속 베기가 6회 베기로 강화되어 투지를 모두 쓰고 체력 4%(800)를 회복한다.
        var result = Duel(data, ["consecutive_slash"], maxActions: 3, initial: [("warrior_spirit", 35)]);
        var turns = Turns(result);
        Assert(turns[0].Hits == 3 && turns[0].Damage == 3 * Hit(3864) && turns[0].Resources.Contains("투지 +15 (현재 50)"), "연속 베기는 세 번 베고 투지 15를 얻는다");
        Assert(turns[1].Hits == 6 && turns[1].Damage == 6 * Hit(2962) && turns[1].Resources.Contains("투지 -50 (현재 0)") && turns[1].Resources.Contains("투지 +15 (현재 15)")
            && result.Events.Any(x => x.Type == "HealApplied" && x.Target == "A" && x.Amount == 800),
            "투지 최고조 연속 베기는 여섯 번 베고 투지를 모두 쓰며 체력 4%를 회복한다");
    }

    private static void QuakeTests(BattleDataSnapshot data)
    {
        // 브레이크 게이지 1칸이면 블레이드 스매시가 바로 브레이크를 건다. 상대가 행동을 잃은 뒤 스매시를 다시 누르면 퀘이크가 나가 브레이크를 익스텐드한다.
        var result = Duel(data, ["blade_smash"], maxActions: 7, rules: [("break_gauge_maximum", "1")], withoutPassives: ["intuition"]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "블레이드 스매시" && turns[0].Damage == Hit(8386) && turns[0].Broken && turns[0].Statuses.Contains("기회 포착"),
            "블레이드 스매시는 피해와 브레이크 피해를 주고, 강타 적중으로 기회 포착을 얻는다");
        // 퀘이크는 익스텐드 뒤에 맞아 무방비 130%를 받고, 전투 숙련: 수호의 무방비 +3%가 붙는다.
        Assert(turns[1].Skill == "퀘이크" && turns[1].Damage == Hit(10821, outgoing: 1.03, incoming: 1.3)
            && result.Events.Count(x => x.Type == "BreakExtended") == 1 && result.Events.Count(x => x.Type == "BreakActionLost" && x.Target == "B") == 2
            && turns[1].Statuses.Contains("퀘이크") && turns[1].Statuses.Contains("퀘이크: 무방비 강화"),
            "상대가 브레이크되면 블레이드 스매시가 퀘이크로 바뀌어 브레이크를 익스텐드하고 받는 피해·무방비 피해 증가를 건다");
        // 익스텐드 중 일반 공격: 무방비 +40%(퀘이크)·+3%(수호), 받는 피해 +30%(익스텐드)·+10%(퀘이크 시너지).
        Assert(turns[2].Skill == "(일반 공격)" && turns[2].Damage == (int)Math.Round(1400 * 1.43 * 1.4 * Variance(.9)),
            "퀘이크 뒤 익스텐드된 상대에게는 무방비 피해 증가와 받는 피해 증가가 함께 붙는다");
    }

    private static void BladeImpactTests(BattleDataSnapshot data)
    {
        var result = Duel(data, ["consecutive_slash", "blade_impact"], maxActions: 3, initial: [("ultimate_gauge", 300)]);
        var turns = Turns(result);
        Assert(turns[0].Skill == "블레이드 임팩트" && turns[0].Hits == 2 && turns[0].Damage == 2 * Hit(14653) && turns[0].Broken && turns[0].Statuses.Contains("블레이드 임팩트")
            && turns[0].Resources.Contains("블레이드 임팩트 +16000 (현재 16000)") && turns[0].Resources.Contains("투지 +50 (현재 50)"),
            "블레이드 임팩트는 두 번 타격해 한 번에 브레이크를 걸고, 받는 피해 감소·최대 체력 보호막·투지 최고조를 얻는다");
        Assert(turns[1].Hits == 6 && turns[1].Resources.Contains("투지 -50 (현재 0)") && turns[1].Resources.Contains("투지 +35 (현재 50)"),
            "블레이드 임팩트 동안에는 강화 스킬을 써도 투지가 다시 최고조로 채워진다");
    }

    private static void SmashHitSpiritTests(BattleDataSnapshot data)
    {
        // 강타 적중 투지 1(패시브 투지)은 피해가 실제로 적중했을 때만 준다(GitHub Issue #16). 스킬 자체의 투지 생성은 빗나가도 얻는다.
        foreach (var (skill, name, gain) in new[] { ("blade_smash", "블레이드 스매시", 15), ("thrust", "찌르기", 10) })
        {
            var hit = Turns(Duel(data, [skill], maxActions: 1))[0];
            Assert(hit.Skill == name && hit.Resources.Contains($"투지 +{gain} (현재 {gain})") && hit.Resources.Contains($"투지 +1 (현재 {gain + 1})"),
                $"{name}가 적중하면 투지 {gain}에 강타 적중 1을 더 얻는다");
            var evaded = Duel(data, [skill], maxActions: 1, evade: true);
            var missed = Turns(evaded)[0];
            Assert(missed.Skill == name && missed.Hits == 0 && evaded.Events.Any(x => x.Type == "AttackEvaded" && x.Actor == "A")
                && missed.Resources.Where(x => x.StartsWith("투지 ", StringComparison.Ordinal)).SequenceEqual([$"투지 +{gain} (현재 {gain})"]),
                $"{name}가 모두 빗나가면 스킬 투지 {gain}만 얻고 강타 적중 1은 없다");
        }
        // 투지 49 경계: 파생 전용 퀘이크·아마란스 킥을 바로 쓰게 해, 빗나가면 49에 머물러 다음 스킬이 강화되지 않는지 본다.
        foreach (var (skill, name, subject) in new[] { ("quake", "퀘이크", "퀘이크가"), ("amaranth_kick", "아마란스 킥", "아마란스 킥이") })
        {
            var hit = Turns(Duel(data, [skill], maxActions: 1, initial: [("warrior_spirit", 49)], standalone: [skill]))[0];
            Assert(hit.Skill == name && hit.Resources.Contains("투지 +1 (현재 50)"), $"투지 49에서 {subject} 적중하면 강타 적중 1로 최고조(50)가 된다");
            var missed = Turns(Duel(data, [skill], maxActions: 1, initial: [("warrior_spirit", 49)], standalone: [skill], evade: true))[0];
            Assert(missed.Skill == name && missed.Hits == 0 && !missed.Resources.Any(x => x.StartsWith("투지", StringComparison.Ordinal)),
                $"투지 49에서 {subject} 빗나가면 투지를 얻지 못해 최고조가 되지 않는다");
        }
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses, int Hits, bool Broken);

    /// <param name="evade">true이면 상대(B)가 회피 확률 100% 상태로 시작해 전사의 모든 공격을 피한다(경갑 제작과 같은 회피확률 상태).</param>
    /// <param name="standalone">파생 전용 스킬을 일반 스킬처럼 후보에 올려 바로 쓰게 한다(퀘이크·아마란스 킥 단독 검사용).</param>
    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, (string Id, int Value)[]? initial = null, (string Id, string Value)[]? rules = null, string[]? withoutPassives = null, bool evade = false, string[]? standalone = null)
    {
        var ruleMap = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", maxActions.ToString()) }.Concat(rules ?? [])) ruleMap[id] = ruleMap[id] with { Value = value };
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in initial ?? []) resources[id] = resources[id] with { InitialValue = value };
        var evasion = TestEvasion.Add(data);
        var snapshot = new BattleDataSnapshot
        {
            Rules = ruleMap,
            Classes = new Dictionary<string, BattleClass> { ["warrior"] = data.Classes["warrior"] with { SkillIds = skills, PassiveIds = data.Classes["warrior"].PassiveIds.Except(withoutPassives ?? []).ToArray() }, ["idle"] = new("idle", "대상", Array.Empty<string>(), PassiveIds: evade ? [TestEvasion.PassiveId] : null) },
            Skills = data.Skills.ToDictionary(x => x.Key, x => standalone?.Contains(x.Key) == true ? x.Value with { Kind = "일반" } : x.Value),
            Passives = evasion.Passives, Resources = resources, Statuses = evasion.Statuses, Derivations = data.Derivations, LoadedAt = data.LoadedAt
        };
        // 전사가 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "warrior", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
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
warrior,전사,전사,"검과 방패를 능숙하게 다루며 눈 앞의 적을 거침없이 제압하는 클래스. 단련된 신체에서 나오는 힘으로 적을 순식간에 쓰러뜨리고, 결정타를 날려 전투의 흐름을 주도한다. 두꺼운 갑옷을 선호하며, 단단한 방어와 강력한 공격으로 승리를 거머쥔다.",consecutive_slash,blade_smash,shield_bash,thrust,battlefield_shout,blade_impact,counter_attack,fighting_spirit,combat_mastery_protection_warrior,vengeance,seize_opportunity,intuition
"""",
        ["스킬"] = """"
ID,이름,스킬구분,부모스킬ID,설명,태그,기타
consecutive_slash,연속 베기,일반,,"쉴 틈 없이 적을 몰아붙이는 신속한 베기 공격. 타겟에게 빠르게 접근해 연속으로 검을 휘두른다. 투지가 최고조에 이르면, 공격 횟수가 증가하고 스킬을 더 빠르게 사용한다.","연타, 이동",등급: 에픽; 강화 레벨: +24; 타겟 대미지: 7026 × 3; 주변 대미지: 4215 × 3; 투지 최고조 시 타겟 대미지: 5386 × 6; 투지 최고조 시 주변 대미지: 3279 × 6; 투지 생성: 5 × 3; 재사용 대기 시간: 5초; 사거리: 6m; 범위: 2.5m
blade_smash,블레이드 스매시,일반,,"다수의 적을 일거에 제압하는 묵직한 베기 공격. 폭발적인 힘을 검에 실어 전방에 힘껏 휘두른다. 검의 반경에 있는 적들은 큰 충격으로 넘어진다. 투지가 최고조에 이르면, 더 빠른 속도로 스킬을 사용해 전방위의 적들에게 피해를 준다.","강타, 방해",등급: 고급; 강화 레벨: +8; 대미지: 15247; 브레이크 대미지: 1칸; 투지 생성: 15; 캐스팅 시간: 1.7초; 투지 최고조 시 캐스팅 시간: 0.85초; 재사용 대기 시간: 12초; 범위: 4m
quake,퀘이크,파생,blade_smash,"자세가 무너진 적을 확실하게 제압하는 도약 공격. 적을 방패로 강타하여 피해를 주고 브레이크 상태를 연장하며, 적의 브레이크 상태가 유지되는 동안 더 큰 피해를 입게 만든다. 사용 시, 잠시 동안 적에게 주는 무방비 피해가 증가하며 타겟이 받는 피해를 증가시킨다. 브레이크 익스텐드는 플레이어마다 각 대상에게 한 번씩만 적용할 수 있으며, 여러 명이 동시에 적용할 경우 효과가 점점 감소한다. 브레이크 익스텐드의 받는 피해 증가 효과는 중복되지 않는다.","강타, 방해",등급: 고급; 강화 레벨: +8; 대미지: 19674; 재사용 대기 시간: 1초; [시너지] 받는 대미지 증가: 10%; [시너지] 받는 대미지 증가 지속 시간: 15초; 무방비 대미지 증가: 40%; 무방비 대미지 증가 지속 시간: 15초; 사거리: 8m
shield_bash,방패 치기,일반,,"적의 공세를 단번에 끊어내는 육중한 방패 타격. 빈틈을 노려 타겟을 방패로 힘껏 치고 밀쳐낸다. 스킬을 준비 중인 타겟은 큰 충격으로 기절한다. 스킬 사용 후 잠시 동안 브레이크되지 않으며, 가드 상태가 되어 일정 확률로 적의 공격을 방어한다. 투지가 최고조에 이르면, 충격파를 일으켜 주변 적들에게 피해를 주고 잠시 동안 받는 모든 공격을 방어한다.","생존, 방해, 보조",등급: 에픽; 강화 레벨: +24; 대미지: 12881; 투지 최고조 시 대미지: 18034; 카운터 시 브레이크 대미지: 1칸; 투지 생성: 10; 브레이크 면역 지속 시간: 5초; 가드 지속 시간: 10초; 가드 확률: 50%; 투지 최고조 시 가드 확률: 100%; 가드 시 받는 대미지 감소: 50%; 가드 시 투지 생성: 2; 재사용 대기 시간: 10초; 최대 스택 수: 2; 투지 최고조 시 범위: 4m
amaranth_kick,아마란스 킥,파생,shield_bash,"상대의 빈틈을 노리는 강력한 역습. 공격 방어 후, 적의 허점을 포착해 거센 발차기를 날린다. 전방의 적들에게 큰 피해를 주고 기절시킨다. 카운터 어택 발동 시 1회 사용할 수 있다. 스킬 사용 후 잠시 동안 브레이크되지 않으며, 가드 상태가 되어 일정 확률로 적의 공격을 방어한다.","강타, 방해, 보조",등급: 에픽; 강화 레벨: +24; 대미지: 29042; 브레이크 대미지: 1칸; 추가 치명타 확률: 100%; 브레이크 면역 지속 시간: 5초; 가드 지속 시간: 10초; 가드 확률: 50%; 가드 시 받는 대미지 감소: 50%; 가드 시 투지 생성: 2; 범위: 5m; 사용 조건: 카운터 어택 발동 시 1회
thrust,찌르기,일반,,"순식간에 거리를 좁히는 거센 찌르기 공격. 맹렬한 기세로 돌진하여 전방의 적들을 꿰뚫는다. 브레이크된 적에게는 도약해 더 큰 피해를 준다. 투지가 최고조에 이르면, 충격파를 일으켜 주변 적들에게 피해를 준다.","강타, 이동, 방해",등급: 에픽; 강화 레벨: +24; 대미지: 16395; 투지 최고조 시 대미지: 24592; 도약 공격 시 대미지 증가: 50%; 투지 생성: 10; 재사용 대기 시간: 9초; 사거리: 12m; 범위: 4m; 투지 최고조 시 범위: 8m
battlefield_shout,전장의 함성,일반,,"기세로 적을 압도하는 함성. 큰 외침으로 주변의 적들을 도발해 자신을 공격하게 만들고, 아군의 사기를 끌어올린다. 잠시 동안 적에게 주는 피해가 증가하고 받는 피해가 감소한다. 투지가 대폭 증가한다.","생존, 보조",등급: 고급; 강화 레벨: +8; 대미지: 9017; 투지 생성: 25; 도발 지속 시간: 8초; [시너지] 대미지 증가: 25초 동안 10%; [시너지] 받는 대미지 감소: 8초 동안 10%; 피격 시 추가 투지 생성: 3; 재사용 대기 시간: 15초; 범위: 6m
blade_impact,블레이드 임팩트,궁극기,,폭발적인 힘으로 도약해 지면을 뒤흔드는 타격. 착지 지점에 충격파를 일으켜 큰 피해와 함께 적들을 넘어뜨리며 충격파의 중심부에 가까운 적에게는 더 큰 피해를 준다. 잠시 동안 최대 체력이 증가하고 받는 피해가 감소한다. 또한 최고조에 이른 투지를 즉시 얻으며 스킬을 사용해도 투지가 소모되지 않는다. 『맹렬한 투지는 땅마저 흔들고 뒤집으리』,"궁극기, 강타, 생존, 방해",등급: 고급; 강화 레벨: +8; 엠블럼 장착 필요; 대미지: 26642; 중심부 추가 대미지: 26642; 브레이크 대미지: 3칸; 지속 시간: 12초; 받는 대미지 감소: 50%; 최대 체력 증가: 20%; 궁극기 비용: 200; 중심부 범위: 5m; 범위: 10m
"""",
        ["패시브스킬"] = """"
ID,이름,설명,기타
counter_attack,카운터 어택,"적의 공격에 대응하고, 포착한 약점에 강타를 날리는 전투술. 방패로 적의 공격을 방어하면 일정 확률로 아마란스 킥을 사용할 수 있다.",발동 확률: 50%; 아마란스 킥 사용 가능 시간: 5초
fighting_spirit,투지,"공방을 주고받을 때마다 솟아오르는 전사의 본능적인 전투 의지. 적을 공격하거나 공격당할 때 투지를 획득한다. 적에게 강타 및 추가타를 적중시키거나 성공적으로 적의 공격을 방어하면 더 많이 획득한다. 투지가 가득 차면 다음 한 번의 공격이 대폭 강화되며, 강화 스킬 사용 시 체력을 회복한다.",투지 최대치: 50; 기본 공격 시 투지 생성: 2; 강타 적중 시 투지 생성: 1; 추가타 적중 시 투지 생성: 2; 체력 회복: 최대 체력의 4%
combat_mastery_protection_warrior,전투 숙련: 수호,"전방에서 아군을 보호하는 숙련된 전투 기법. 모든 공격으로부터 받는 피해가 감소하고, 적에게 주는 무방비 피해가 증가한다. 전사 클래스 레벨이 30에 도달하면 전투 시 클래스 특화 어시스트를 사용할 수 있다.",받는 대미지 감소: 15%; 무방비 대미지 증가: 3%; 클래스 특화 어시스트 해금: 전사 클래스 레벨 30
vengeance,복수심,적의 공격을 막을 때마다 솟구치는 전사의 뜨거운 마음가짐. 방패로 적의 공격을 방어하면 잠시 동안 공격력이 증가한다.,공격력 증가: 5%; 지속 시간: 25초; 최대 중첩 수: 5
seize_opportunity,기회 포착,"한 번 잡은 승기를 놓치지 않는 맹렬한 전투 의지. 브레이크 상태의 적을 공격하거나, 공격이 강타로 적중하면 잠시 동안 치명타 확률이 증가한다.",치명타 확률 증가: 10%; 지속 시간: 10초
intuition,직감,적의 빈틈을 헤집고 치명적인 일격을 가하는 날카로운 전투 감각. 무방비 공격 시마다 타겟에게 주는 피해가 점점 증가한다.,해금 조건: 전사 Lv.60 이상; 타겟에게 주는 대미지 증가: 중첩당 1%; 최대 중첩 수: 15; 지속 시간: 60초
"""",
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고,궁극기문구
consecutive_slash,TRUE,공격,상대,1,0,65,궁극기 게이지,,150,FALSE,빠르게 접근해 세 번 벤다. 투지 최고조면 여섯 번 베고 체력을 회복한다.,{caster}가 쉴 틈 없이 {target}에게 검을 휘두릅니다!,"원본 재사용 대기 5초→1턴. 타겟 대미지 7,026×3, 투지 최고조 시 5,386×6, 투지 생성 5×3. 피해 원본 ×0.55. 주변 대미지는 1:1이라 제외, 최고조 시 사용 속도 증가는 생략",
blade_smash,TRUE,공격·방해,상대,2,0,60,궁극기 게이지,,150,FALSE,묵직하게 베어 넘어뜨려 브레이크 피해를 준다. 상대가 브레이크되면 퀘이크로 바뀐다.,{caster}가 폭발적인 힘을 실어 {target}에게 검을 힘껏 휘두릅니다!,"원본 재사용 대기 12초→2턴, 대미지 15,247, 브레이크 1칸(넘어짐), 투지 15(+강타 적중 1). 피해 원본 ×0.55. 투지 최고조 효과(캐스팅 단축·전방위)는 1:1에서 차이가 없어 투지를 쓰지 않는다. 상대가 익스텐드 전 브레이크 상태이면 재사용 시 파생 blade_smash_quake로 퀘이크가 나간다",
quake,TRUE,공격·방해,상대,2,0,0,,,0,FALSE,"방패로 무너진 적을 강타해 브레이크를 연장(브레이크 익스텐드)하고, 받는 피해와 무방비 피해를 늘린다.",{caster}가 도약해 무너진 {target}을 방패로 내리찍습니다!,"파생 전용. 상대가 브레이크(익스텐드 전)일 때 블레이드 스매시를 다시 누르면 실행. 대미지 19,674, [시너지] 받는 대미지 증가 10%·무방비 대미지 증가 40%(15초→3턴). 피해 원본 ×0.55. 여러 명 적용 시 감소는 1:1이라 제외. 기본쿨다운 2는 블레이드 스매시의 쿨다운으로 적용(원본 1초)",
shield_bash,TRUE,생존·방해,자신·상대,2,0,60,궁극기 게이지,,150,FALSE,방패로 쳐서 피해를 주고 가드 태세를 취한다. 투지 최고조면 더 세게 치고 모든 공격을 막는다. 카운터 어택이 발동하면 아마란스 킥으로 바뀐다.,{caster}가 빈틈을 노려 {target}을 방패로 힘껏 칩니다!,"원본 재사용 대기 10초→2턴. 대미지 12,881(최고조 18,034), 브레이크 면역 5초→1턴, 가드 10초→2턴(확률 50%, 최고조 100%, 받는 대미지 -50%, 가드 시 투지 2). 스킬을 준비 중인 타겟 기절(카운터 브레이크 1칸)은 턴제에 시전 시간이 없어 50% 확률로 단순화. 피해 원본 ×0.55. 최대 스택 2는 충전 개념이 없어 제외. 카운터 어택 표식이 있으면 재사용 시 파생 shield_bash_amaranth로 아마란스 킥이 나간다",
amaranth_kick,TRUE,공격·방해,자신·상대,2,0,0,,,0,FALSE,공격을 막아낸 뒤 허점을 포착해 거센 발차기로 큰 피해를 주고 기절시킨다. 반드시 치명타로 적중한다.,{caster}가 허점을 드러낸 {target}에게 거센 발차기를 날립니다!,"파생 전용. 카운터 어택(가드 시 50%)이 발동한 뒤 방패 치기를 다시 누르면 1회 실행. 대미지 29,042, 브레이크 1칸, 추가 치명타 확률 100%, 브레이크 면역 1턴, 가드 2턴. 피해 원본 ×0.55. 기본쿨다운 2는 방패 치기의 쿨다운으로 적용",
thrust,TRUE,공격,상대,2,0,65,궁극기 게이지,,150,FALSE,"돌진해 꿰뚫는다. 브레이크된 적에게는 도약해 더 큰 피해를 주고, 투지 최고조면 충격파로 더 세게 친다.",{caster}가 맹렬한 기세로 돌진해 {target}을 꿰뚫습니다!,"원본 재사용 대기 9초→2턴. 대미지 16,395(최고조 24,592), 브레이크된 적에게 도약 공격 대미지 +50%, 투지 10(+강타 적중 1). 피해 원본 ×0.55",
battlefield_shout,TRUE,생존·보조,자신·상대,3,0,55,궁극기 게이지,,150,FALSE,큰 함성으로 상대를 도발하고 기세를 끌어올린다. 주는 피해가 늘고 받는 피해가 줄며 투지가 크게 오른다.,{caster}의 함성이 전장을 뒤흔듭니다!,"원본 재사용 대기 15초→3턴. 대미지 9,017, 투지 25, 도발 8초→2턴(도발 중 피격 시 투지 3), [시너지] 대미지 증가 10% 25초→5턴, [시너지] 받는 대미지 감소 10% 8초→2턴. 피해 원본 ×0.55. 1:1이라 공격 대상 강제는 의미가 없다",
blade_impact,TRUE,궁극기·생존,자신·상대,0,0,100,궁극기 게이지,300,,TRUE,"도약해 지면을 뒤흔드는 충격파로 큰 피해와 브레이크를 준다. 최대 체력이 늘고 받는 피해가 줄며, 투지가 최고조가 되어 잠시 소모되지 않는다.",{caster}가 폭발적인 힘으로 도약해 {target} 앞의 지면을 내리찍습니다!,"원본 대미지 26,642 + 중심부 추가 대미지 26,642(1:1이라 항상 중심부), 브레이크 3칸(한 번에 브레이크), 12초→2턴 동안 받는 대미지 -50%·최대 체력 +20%. 최대 체력 증가는 엔진에 개념이 없어 기본 HP의 20%(4,000) 보호막으로 근사. 투지 최고조를 즉시 얻고 2턴 동안 스킬을 써도 다시 채운다(패시브 투지). 궁극기 비용 200은 다른 궁극기와 같이 300으로 통일. 피해 원본 ×0.55. 엠블럼 장착 필요는 사용 조건이 아니라 제외",『맹렬한 투지는 땅마저 흔들고 뒤집으리』
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
cs_01,consecutive_slash,1,자원설정,자신,,1,1,1,0,warrior_spirit_active,0,,투지 최고조(50)면 이번 사용을 강화한다. 강화 판정용 내부 표식,자신,자원보유,warrior_spirit,>=,50,,,
cs_02,consecutive_slash,2,자원소모,자신,,0,1,1,0,warrior_spirit,0,,강화 공격에 투지를 모두 쓴다. 이후 행의 투지 생성은 새로 쌓인다,자신,자원보유,warrior_spirit,>=,50,,전부,
cs_03,consecutive_slash,3,피해,상대,,3864,3,1,0,,0,,"원본 7,026×3",자신,자원보유,warrior_spirit_active,<=,0,,,
cs_04,consecutive_slash,4,피해,상대,,2962,6,1,0,,0,,"투지 최고조 원본 5,386×6",자신,자원보유,warrior_spirit_active,>=,1,,,
cs_05,consecutive_slash,5,자원증가,자신,,15,1,1,0,warrior_spirit,0,,투지 생성 5×3,,,,,,,,
bs_01,blade_smash,1,피해,상대,,8386,1,1,0,,0,,"원본 15,247",,,,,,,,
bs_02,blade_smash,2,브레이크피해,상대,,1,1,1,0,,0,,브레이크 대미지 1칸(넘어짐),,,,,,,,
bs_03,blade_smash,3,자원증가,자신,,15,1,1,0,warrior_spirit,0,,투지 생성 15. 강타 적중 1은 bs_04,,,,,,,,
bs_04,blade_smash,4,자원증가,자신,,1,1,1,0,warrior_spirit,0,,강타 적중 투지 1. 피해가 모두 빗나가면 없음(피해적중),,피해적중,,,,,,
qk_01,quake,1,브레이크익스텐드,상대,,0,1,1,0,break_extended,0,,브레이크 익스텐드: 익스텐드 상태로 바꾸고 행동 불가를 브레이크 지속만큼 더함,,,,,,,,
qk_02,quake,2,피해,상대,,10821,1,1,0,,0,,"원본 19,674. 익스텐드 뒤에 맞아 무방비 130%가 붙는다",,,,,,,,
qk_03,quake,3,상태효과,상대,,0,1,1,3,warrior_quake_vuln,1,,"[시너지] 받는 대미지 증가 10%, 15초→3턴",,,,,,,,
qk_04,quake,4,상태효과,자신,,0,1,1,3,warrior_quake_power,1,,"무방비 대미지 증가 40%, 15초→3턴",,,,,,,,
qk_05,quake,5,자원증가,자신,,1,1,1,0,warrior_spirit,0,,강타 적중 투지 1. 피해가 모두 빗나가면 없음(피해적중),,피해적중,,,,,,
sb_01,shield_bash,1,자원설정,자신,,1,1,1,0,warrior_spirit_active,0,,투지 최고조(50)면 이번 사용을 강화한다. 강화 판정용 내부 표식,자신,자원보유,warrior_spirit,>=,50,,,
sb_02,shield_bash,2,자원소모,자신,,0,1,1,0,warrior_spirit,0,,강화 공격에 투지를 모두 쓴다. 이후 행의 투지 생성은 새로 쌓인다,자신,자원보유,warrior_spirit,>=,50,,전부,
sb_03,shield_bash,3,피해,상대,,7085,1,1,0,,0,,"원본 12,881",자신,자원보유,warrior_spirit_active,<=,0,,,
sb_04,shield_bash,4,피해,상대,,9919,1,1,0,,0,,"투지 최고조 원본 18,034",자신,자원보유,warrior_spirit_active,>=,1,,,
sb_05,shield_bash,5,브레이크피해,상대,,1,1,0.5,0,,0,,스킬을 준비 중인 타겟 기절(브레이크 1칸)을 50% 확률로 단순화,,,,,,,,
sb_06,shield_bash,6,상태효과,자신,,0,1,1,1,warrior_break_immune,1,,브레이크 면역 5초→1턴,,,,,,,,
sb_07,shield_bash,7,상태효과,자신,,0,1,1,2,warrior_guard,1,,"가드 10초→2턴, 확률 50%, 받는 대미지 -50%",,,,,,,,
sb_08,shield_bash,8,상태효과,자신,,0,1,1,2,warrior_guard_full,1,,투지 최고조 시 가드 확률 100%(잠시 동안 모든 공격 방어),자신,자원보유,warrior_spirit_active,>=,1,,,
sb_09,shield_bash,9,자원증가,자신,,10,1,1,0,warrior_spirit,0,,투지 생성 10,,,,,,,,
ak_01,amaranth_kick,1,상태효과,자신,,0,1,1,1,warrior_amaranth_crit,1,,추가 치명타 확률 100%(이번 스킬에만),,,,,,,,
ak_02,amaranth_kick,2,피해,상대,,15973,1,1,0,,0,,"원본 29,042",,,,,,,,
ak_03,amaranth_kick,3,브레이크피해,상대,,1,1,1,0,,0,,브레이크 대미지 1칸(기절),,,,,,,,
ak_04,amaranth_kick,4,상태효과,자신,,0,1,1,1,warrior_break_immune,1,,브레이크 면역 5초→1턴,,,,,,,,
ak_05,amaranth_kick,5,상태효과,자신,,0,1,1,2,warrior_guard,1,,"가드 10초→2턴, 확률 50%",,,,,,,,
ak_06,amaranth_kick,6,자원소모,자신,,0,1,1,0,warrior_counter_ready,0,,카운터 어택 발동 시 1회,,,,,,,전부,
ak_07,amaranth_kick,7,자원증가,자신,,1,1,1,0,warrior_spirit,0,,강타 적중 투지 1. 피해가 모두 빗나가면 없음(피해적중),,피해적중,,,,,,
th_01,thrust,1,자원설정,자신,,1,1,1,0,warrior_spirit_active,0,,투지 최고조(50)면 이번 사용을 강화한다. 강화 판정용 내부 표식,자신,자원보유,warrior_spirit,>=,50,,,
th_02,thrust,2,자원소모,자신,,0,1,1,0,warrior_spirit,0,,강화 공격에 투지를 모두 쓴다. 이후 행의 투지 생성은 새로 쌓인다,자신,자원보유,warrior_spirit,>=,50,,전부,
th_03,thrust,3,조건부피해증가,상대,,50,1,1,0,,0,,브레이크된 적에게 도약 공격 시 대미지 +50%,상대,상태효과유형보유,브레이크,,,,,
th_04,thrust,4,피해,상대,,9017,1,1,0,,0,,"원본 16,395",자신,자원보유,warrior_spirit_active,<=,0,,,
th_05,thrust,5,피해,상대,,13526,1,1,0,,0,,"투지 최고조 원본 24,592",자신,자원보유,warrior_spirit_active,>=,1,,,
th_06,thrust,6,자원증가,자신,,10,1,1,0,warrior_spirit,0,,투지 생성 10. 강타 적중 1은 th_07,,,,,,,,
th_07,thrust,7,자원증가,자신,,1,1,1,0,warrior_spirit,0,,강타 적중 투지 1. 피해가 모두 빗나가면 없음(피해적중),,피해적중,,,,,,
fs_01,battlefield_shout,1,피해,상대,,4959,1,1,0,,0,,"원본 9,017",,,,,,,,
fs_02,battlefield_shout,2,상태효과,자신,,0,1,1,2,warrior_taunt,1,,도발 8초→2턴,,,,,,,,
fs_03,battlefield_shout,3,상태효과,자신,,0,1,1,5,warrior_shout_power,1,,"[시너지] 대미지 증가 10%, 25초→5턴",,,,,,,,
fs_04,battlefield_shout,4,상태효과,자신,,0,1,1,2,warrior_shout_guard,1,,"[시너지] 받는 대미지 감소 10%, 8초→2턴",,,,,,,,
fs_05,battlefield_shout,5,자원증가,자신,,25,1,1,0,warrior_spirit,0,,투지 생성 25,,,,,,,,
bi_01,blade_impact,1,피해,상대,,14653,1,1,0,,0,,"원본 26,642",,,,,,,,
bi_02,blade_impact,2,피해,상대,,14653,1,1,0,,0,,"중심부 추가 원본 26,642",,,,,,,,
bi_03,blade_impact,3,브레이크피해,상대,,3,1,1,0,,0,,브레이크 대미지 3칸(넘어짐),,,,,,,,
bi_04,blade_impact,4,상태효과,자신,,0,1,1,2,warrior_impact_guard,1,,"받는 대미지 감소 50%, 12초→2턴",,,,,,,,
bi_05,blade_impact,5,자원증가,자신,,16000,1,1,0,warrior_impact_shield,0,,"최대 체력 +20%를 기본 HP 20,000의 20% 보호막으로 근사(흡수량 16,000×0.25=4,000)",,,,,,,,
bi_06,blade_impact,6,자원설정,자신,,50,1,1,0,warrior_spirit,0,,최고조에 이른 투지를 즉시 얻는다,,,,,,,,
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
shield_bash_amaranth,shield_bash,amaranth_kick,조건,0,1,자원보유,warrior_counter_ready>=1,FALSE,재사용 시,"카운터 어택 표식(가드 시 50%, 다음 1회 행동)이 있으면 방패 치기가 아마란스 킥으로 바뀐다(인게임 버튼 전환). 쿨다운과 관계없이 나간다",100
blade_smash_quake,blade_smash,quake,조건,0,1,상대상태효과보유,break_broken,FALSE,재사용 시,상대가 익스텐드 전 브레이크 상태이면 블레이드 스매시가 퀘이크로 바뀐다(빙결술사 아이시클 섀터와 같은 방식). 익스텐드되면 break_extended로 바뀌어 다시 나오지 않는다,100
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
counter_attack,TRUE,가드 성공 시 50%로 카운터 어택 표식(5초→다음 1회 행동). 방패 치기 재사용 시 아마란스 킥.
fighting_spirit,TRUE,"투지 최대 50. 기본 공격 2·추가타 2·가드 2·도발 중 피격 3, 강타 적중 1은 각 스킬의 투지 생성에 포함. 공격당할 때 기본 생성량은 원본에 수치가 없어 제외. 최고조면 다음 연속 베기·방패 치기·찌르기를 강화하고 체력 4% 회복."
combat_mastery_protection_warrior,TRUE,"받는 대미지 감소 15%, 무방비 대미지 +3%."
vengeance,TRUE,"가드 성공마다 공격력 +5%(최대 5중첩, 25초→5턴)."
seize_opportunity,TRUE,브레이크된 적 공격 또는 강타 스킬 적중 시 치명타 확률 +10%(10초→2턴). 강타 스킬은 원본 태그 기준 블레이드 스매시·퀘이크·아마란스 킥·찌르기·블레이드 임팩트.
intuition,TRUE,"브레이크된 적 공격마다 주는 피해 +1%(최대 15, 60초→10턴). 스킬 적중은 3중첩(인게임 타격마다 1), 기본 공격은 1중첩. Lv.60 조건은 항상 충족으로 가정."
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
ca_01,counter_attack,1,자원설정,자신,1,1,0.5,0,warrior_counter_ready,0,,,,,,,,,가드시,,,"발동 확률 50%, 아마란스 킥 사용 가능 시간 5초→다음 1회 행동",
fsp_01,fighting_spirit,1,자원증가,자신,2,1,1,0,warrior_spirit,0,,,,,,,,,기본공격적중시,,,기본 공격 시 투지 2,
fsp_02,fighting_spirit,2,자원증가,자신,2,1,1,0,warrior_spirit,0,,,,,,,,,추가타적중시,,,추가타 적중 시 투지 2,
fsp_03,fighting_spirit,3,자원증가,자신,2,1,1,0,warrior_spirit,0,,,,,,,,,가드시,,,가드 시 투지 2(방패 치기 기타),
fsp_04,fighting_spirit,4,자원증가,자신,3,1,1,0,warrior_spirit,0,,자신,상태효과보유,warrior_taunt,,,,,피격시,,,전장의 함성 도발 중 피격 시 추가 투지 3,
fsp_05,fighting_spirit,5,회복,자신,3200,1,1,0,,0,,자신,자원보유,warrior_spirit_active,>=,1,,,스킬사용완료시,,,"강화 스킬 사용 시 최대 체력 4% 회복. 기본 HP 20,000의 4%=800(고정값 3,200×0.25)",
fsp_06,fighting_spirit,6,자원소모,자신,0,1,1,0,warrior_spirit_active,0,,자신,자원보유,warrior_spirit_active,>=,1,,전부,스킬사용완료시,,,강화 표식 소모,
fsp_07,fighting_spirit,7,자원설정,자신,50,1,1,0,warrior_spirit,0,,자신,상태효과보유,warrior_impact_guard,,,,,스킬사용완료시,,,블레이드 임팩트 동안 스킬을 사용해도 투지가 소모되지 않는다 → 사용 후 다시 최고조로 채움,
cmpw_01,combat_mastery_protection_warrior,1,받는피해감소,자신,0,1,1,0,warrior_mastery_guard,0,,,,,,,,,전투시작,,,,
cmpw_02,combat_mastery_protection_warrior,2,무방비피해증가,자신,0,1,1,0,warrior_mastery_unguarded,0,,,,,,,,,전투시작,,,,
vg_01,vengeance,1,자원증가,자신,1,1,1,0,warrior_vengeance,0,,,,,,,,,가드시,,,"가드 성공마다 중첩 +1(최대 5, 25초→5턴)",
vg_02,vengeance,2,주는피해증가,자신,0,1,1,0,warrior_vengeance_power,0,,,,,,,,,전투시작,,,중첩자원ID=warrior_vengeance로 중첩당 +5%,
so_01,seize_opportunity,1,상태효과,자신,0,1,1,2,warrior_seize,0,,상대,상태효과유형보유,브레이크,,,,,스킬적중완료시,,,"브레이크 상태의 적을 공격하면 치명타 확률 +10%, 10초→2턴",
so_02,seize_opportunity,2,상태효과,자신,0,1,1,2,warrior_seize,0,,상대,상태효과유형보유,브레이크,,,,,기본공격적중시,,,기본 공격도 포함,
so_03,seize_opportunity,3,상태효과,자신,0,1,1,2,warrior_seize,0,,,,,,,,,스킬적중완료시,blade_smash,,강타 태그 스킬 적중,
so_04,seize_opportunity,4,상태효과,자신,0,1,1,2,warrior_seize,0,,,,,,,,,스킬적중완료시,quake,,강타 태그 스킬 적중,
so_05,seize_opportunity,5,상태효과,자신,0,1,1,2,warrior_seize,0,,,,,,,,,스킬적중완료시,amaranth_kick,,강타 태그 스킬 적중,
so_06,seize_opportunity,6,상태효과,자신,0,1,1,2,warrior_seize,0,,,,,,,,,스킬적중완료시,thrust,,강타 태그 스킬 적중,
so_07,seize_opportunity,7,상태효과,자신,0,1,1,2,warrior_seize,0,,,,,,,,,스킬적중완료시,blade_impact,,강타 태그 스킬 적중,
in_01,intuition,1,자원증가,자신,3,1,1,0,warrior_intuition,0,,상대,상태효과유형보유,브레이크,,,,,스킬적중완료시,,,무방비 공격 시마다 1중첩을 스킬 적중당 3으로 환산(인게임 다단·빠른 타격),
in_02,intuition,2,자원증가,자신,1,1,1,0,warrior_intuition,0,,상대,상태효과유형보유,브레이크,,,,,기본공격적중시,,,기본 공격 1중첩,
in_03,intuition,3,주는피해증가,자신,0,1,1,0,warrior_intuition_power,0,,,,,,,,,전투시작,,,중첩자원ID=warrior_intuition으로 중첩당 +1%,
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
warrior_spirit,투지,자원,50,0,0,가산,TRUE,"전사 투지(최대 50). 스킬 사용·기본 공격·추가타·가드로 쌓이고, 50이면 다음 연속 베기·방패 치기·찌르기가 강화되며 모두 소모한다. 블레이드 임팩트 동안에는 스킬을 써도 다시 50으로 채워진다.",
warrior_spirit_active,투지 최고조 강화,자원,1,0,0,교체,TRUE,이번 스킬이 투지 최고조로 강화되었다는 내부 표식. 스킬 첫 행에서 설정하고 사용 완료 시 패시브 투지가 체력 회복 후 소모한다.,TRUE
warrior_counter_ready,카운터 어택,자원,1,0,1,교체,TRUE,가드 성공 시 50% 확률로 얻는 아마란스 킥 사용 가능 표식(5초→다음 1회 행동). 방패 치기를 다시 누르면 아마란스 킥이 나가고 소모된다.,
warrior_vengeance,복수심,중첩,5,0,5,가산,TRUE,"가드 성공마다 +1(최대 5, 25초→5턴). 중첩당 주는 피해 +5%.",
warrior_intuition,직감,중첩,15,0,10,가산,TRUE,"상대가 브레이크(무방비)일 때 공격하면 쌓이는 중첩(최대 15, 60초→10턴). 중첩당 주는 피해 +1%.",
warrior_impact_shield,블레이드 임팩트,보호막,0,0,2,교체,TRUE,"블레이드 임팩트의 최대 체력 +20%를 기본 HP 20,000의 20%(4,000) 보호막으로 근사(화면 흡수량 16,000, 12초→2턴).",
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
break_broken,브레이크,브레이크|받는피해증가,0.2,브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외),,,,,,,,
break_extended,브레이크 익스텐드,브레이크|받는피해증가,0.3,브레이크 익스텐드(아이시클 섀터). 브레이크를 대신해 행동 불가를 브레이크 지속만큼 더 주고 무방비 대미지 130%(받는 피해 +30%). 모든 브레이크 타입 공통인 무방비만 반영,,,,,,,,
warrior_guard,가드,가드|가드피해감소,0.5,"방패 치기·아마란스 킥의 가드. 타격마다 50% 확률로 막아 받는 대미지 -50%, 가드 시 투지 +2·카운터 어택·복수심이 발동한다(10초→2턴).",,,,,0.5|0.5,,,
warrior_guard_full,완벽한 가드,가드,0.5,투지 최고조 방패 치기의 가드 확률 100%. 가드(50%)에 더해 모든 공격을 막는다(2턴).,,,,,,,,
warrior_break_immune,브레이크 면역,브레이크면역,0,방패 치기·아마란스 킥 사용 후 브레이크되지 않는다(5초→1턴).,,,,,,,,
warrior_amaranth_crit,아마란스 킥: 치명타,다음스킬치명타확률증가|다음스킬소모,1,아마란스 킥의 추가 치명타 확률 100%. 이번 스킬에만 적용하고 소모한다.,,,,,,,,TRUE
warrior_taunt,도발,없음,0,전장의 함성 도발(8초→2턴). 도발 중 피격 시 투지 +3(패시브 투지). 1:1이라 공격 대상 강제는 의미가 없다.,,,,,,,,
warrior_shout_power,전장의 함성,주는피해증가,0.1,[시너지] 대미지 증가 10%(25초→5턴).,,,주는피해증가,,,,,
warrior_shout_guard,전장의 함성: 방어,받는피해감소,0.1,[시너지] 받는 대미지 감소 10%(8초→2턴).,,,받는피해감소,,,,,
warrior_quake_vuln,퀘이크,받는피해증가,0.1,퀘이크를 맞은 적의 [시너지] 받는 대미지 증가 10%(15초→3턴).,,,받는피해증가,,,,,
warrior_quake_power,퀘이크: 무방비 강화,무방비피해증가,0.4,퀘이크 사용 후 주는 무방비 대미지 +40%(15초→3턴). 상대가 브레이크 상태일 때만 더한다.,,,,,,,,
warrior_impact_guard,블레이드 임팩트,받는피해감소,0.5,블레이드 임팩트 후 받는 대미지 감소 50%(12초→2턴). 보유 중에는 스킬을 써도 투지가 다시 최고조로 채워진다(패시브 투지).,,,,,,,,
warrior_mastery_guard,전투 숙련: 수호,받는피해감소,0.15,모든 공격으로부터 받는 대미지 감소 15%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,,,
warrior_mastery_unguarded,전투 숙련: 수호,무방비피해증가,0.03,적에게 주는 무방비 대미지 +3%.,,,,,,,,
warrior_vengeance_power,복수심,주는피해증가,0.05,복수심 중첩당 공격력 +5%를 주는 피해 +5%로 단순화.,,warrior_vengeance,,,,,,TRUE
warrior_seize,기회 포착,치명타확률증가,0.1,브레이크된 적을 공격하거나 강타 스킬이 적중하면 치명타 확률 +10%(10초→2턴).,,,,,,,,
warrior_intuition_power,직감,주는피해증가,0.01,직감 중첩당 타겟에게 주는 대미지 +1%(최대 15).,,warrior_intuition,,,,,,TRUE
"""",
    };
}
