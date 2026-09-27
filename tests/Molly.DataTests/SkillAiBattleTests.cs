using Molly.Battle;

/// <summary>
/// GitHub Issue #12: 배틀스킬AI 조건 가산(On/Off·비례·상한·음수 하한)과 재사용 후보의 일반 추첨, 로더 검증을 검사한다.
/// 석궁사수·힐러 시트 사본에 이 테스트 전용 배틀스킬AI 행만 붙인다. 난수를 한 값으로 고정해 추첨 지점을 계산할 수 있게 한다.
/// 기본 가중치는 1 + 사용우선순위/100이다(버스터 샷 1.60, 쇼크 익스플로전 1.65, 슬라이딩 스텝 1.58, 라이프 링크 1.70, 프로텍션 1.60).
/// </summary>
internal static class SkillAiBattleTests
{
    private const string Header = "\"ID\",\"스킬ID\",\"활성화\",\"조건대상\",\"조건유형\",\"조건ID\",\"조건연산자\",\"조건값\",\"가산가중치\",\"단계크기\",\"단계당가산\",\"최대가산\",\"비고\"";
    private static readonly string[] Loaders = ["buster_shot", "shock_explosion", "sliding_step"];

    public static void Run()
    {
        OnOffTests();
        ProportionalTests();
        ReuseTests();
        LoaderTests();
    }

    private static void OnOffTests()
    {
        // 난수 0.5: 추첨 지점은 가중치 합의 절반이다. AI가 없으면 4.83의 절반 2.415가 버스터 샷(1.60)을 지나 쇼크 익스플로전에 떨어진다.
        var plain = Crossbow();
        var baseline = Duel(plain, "crossbowman", Loaders, .5);
        Assert(FirstSkill(baseline) == "쇼크 익스플로전" && !baseline.Events.Any(x => x.Type == "SkillAiWeighted"), "배틀스킬AI가 비어 있으면 기본 가중치로 추첨하고 AI 기록을 남기지 않는다");

        // 상대가 브레이크 상태가 아니면 슬라이딩 스텝 +10: 1.60·1.65·11.58 중 절반 지점 7.415는 슬라이딩 스텝이다.
        var onOff = Duel(Crossbow("\"ai_slide\",\"sliding_step\",\"TRUE\",\"상대\",\"상태효과유형미보유\",\"브레이크\",\"\",\"\",\"10\",\"\",\"\",\"\",\"\""), "crossbowman", Loaders, .5);
        Assert(FirstSkill(onOff) == "슬라이딩 스텝" && onOff.Events.First(x => x.Type == "SkillAiWeighted" && x.Actor == "A").Detail == "sliding_step|ai_slide",
            "조건이 맞은 On/Off 가산은 가산가중치만큼 선택 가중치를 올리고, 선택한 스킬과 맞은 조건 ID를 기록한다");
        var unmet = Duel(Crossbow("\"ai_slide\",\"sliding_step\",\"TRUE\",\"상대\",\"상태효과유형보유\",\"브레이크\",\"\",\"\",\"10\",\"\",\"\",\"\",\"\""), "crossbowman", Loaders, .5);
        Assert(FirstSkill(unmet) == "쇼크 익스플로전" && !unmet.Events.Any(x => x.Type == "SkillAiWeighted" && x.Actor == "A"), "조건이 맞지 않은 AI 행은 가중치에 영향을 주지 않는다");

        // 난수 0.3, 순서 쇼크·버스터·슬라이딩: AI가 없으면 지점 1.449가 쇼크 익스플로전(1.65) 안에 있다.
        // 쇼크 익스플로전 -10은 하한 0.1로 남아 지점 0.984가 버스터 샷으로 넘어간다.
        string[] shockFirst = ["shock_explosion", "buster_shot", "sliding_step"];
        var negative = Duel(Crossbow("\"ai_shock_down\",\"shock_explosion\",\"TRUE\",\"자신\",\"상태효과유형미보유\",\"브레이크\",\"\",\"\",\"-10\",\"\",\"\",\"\",\"\""), "crossbowman", shockFirst, .3);
        Assert(FirstSkill(Duel(plain, "crossbowman", shockFirst, .3)) == "쇼크 익스플로전" && FirstSkill(negative) == "버스터 샷", "음수 가산은 선택 가중치를 낮추고, 가중치는 0.1 아래로 내려가지 않는다");
        // 하한이 없다면 후보에서 빠지지만, 0.1이 남아 있으면 지점이 앞쪽(0.03)일 때 여전히 뽑힌다.
        Assert(FirstSkill(Duel(Crossbow("\"ai_shock_down\",\"shock_explosion\",\"TRUE\",\"자신\",\"상태효과유형미보유\",\"브레이크\",\"\",\"\",\"-10\",\"\",\"\",\"\",\"\""), "crossbowman", shockFirst, .01)) == "쇼크 익스플로전",
            "음수 가산으로 하한에 닿은 스킬도 드물게는 선택된다");
    }

    private static void ProportionalTests()
    {
        // 궁극기 게이지 0 이상에서 10마다 슬라이딩 스텝 +1(상한 5). 가산 +1이면 합 5.83의 절반 2.915가 쇼크 익스플로전, +2면 3.415가 슬라이딩 스텝에 떨어진다.
        var rising = Crossbow("\"ai_gauge\",\"sliding_step\",\"TRUE\",\"자신\",\"자원보유\",\"ultimate_gauge\",\">=\",\"0\",\"0\",\"10\",\"1\",\"5\",\"\"");
        Assert(FirstSkill(Duel(rising, "crossbowman", Loaders, .5, [("ultimate_gauge", 19)])) == "쇼크 익스플로전" && FirstSkill(Duel(rising, "crossbowman", Loaders, .5, [("ultimate_gauge", 20)])) == "슬라이딩 스텝",
            "비례 가산(>=)은 조건값에서 단계크기만큼 멀어질 때마다 단계당가산을 더한다(19는 1단계, 20은 2단계)");
        var capped = Crossbow("\"ai_gauge\",\"sliding_step\",\"TRUE\",\"자신\",\"자원보유\",\"ultimate_gauge\",\">=\",\"0\",\"0\",\"10\",\"1\",\"1\",\"\"");
        Assert(FirstSkill(Duel(capped, "crossbowman", Loaders, .5, [("ultimate_gauge", 200)])) == "쇼크 익스플로전", "비례 가산은 최대가산에서 멈춘다");

        // 궁극기 게이지 100 미만에서 10씩 모자랄 때마다 +1: 90은 1단계(쇼크 익스플로전), 80은 2단계(슬라이딩 스텝). 100이면 조건 불충족이다.
        var falling = Crossbow("\"ai_low\",\"sliding_step\",\"TRUE\",\"자신\",\"자원보유\",\"ultimate_gauge\",\"<\",\"100\",\"0\",\"10\",\"1\",\"5\",\"\"");
        var atThreshold = Duel(falling, "crossbowman", Loaders, .5, [("ultimate_gauge", 100)]);
        Assert(FirstSkill(Duel(falling, "crossbowman", Loaders, .5, [("ultimate_gauge", 90)])) == "쇼크 익스플로전" && FirstSkill(Duel(falling, "crossbowman", Loaders, .5, [("ultimate_gauge", 80)])) == "슬라이딩 스텝"
            && !atThreshold.Events.Any(x => x.Type == "SkillAiWeighted" && x.Actor == "A"),
            "비례 가산(<)은 조건값보다 낮아진 거리로 단계를 센다");
    }

    private static void ReuseTests()
    {
        // 빛의 결정체 2개로 라이프 링크 재사용(서먼 스프라이트)이 가능하다. 난수 0.8: 합 3.30의 지점 2.64는 프로텍션이다.
        // 예전에는 재사용 후보만 남겨 무조건 서먼 스프라이트였다.
        string[] healer = ["life_link", "protection"];
        var plain = Duel(Healer(), "healer", healer, .8, [("heal_light_orb", 2)]);
        Assert(FirstSkill(plain) == "프로텍션", "재사용 후보도 일반 후보와 함께 추첨되어 바로 발산하지 않을 수 있다");
        // 결정체 2개 이상에서 라이프 링크 +10: 합 13.30의 지점 10.64는 라이프 링크 안이라 서먼 스프라이트로 발산한다.
        var weighted = Duel(Healer("\"ai_sprite\",\"life_link\",\"TRUE\",\"자신\",\"자원보유\",\"heal_light_orb\",\">=\",\"2\",\"10\",\"\",\"\",\"\",\"\""), "healer", healer, .8, [("heal_light_orb", 2)]);
        Assert(FirstSkill(weighted) == "서먼 스프라이트", "재사용 후보의 발산 시점은 배틀스킬AI 가산으로 앞당길 수 있다");
    }

    private static void LoaderTests()
    {
        var parsed = Crossbow("\"ai_off\",\"sliding_step\",\"FALSE\",\"자신\",\"HP비율\",\"\",\"<\",\"0.5\",\"1\",\"0.01\",\"0.1\",\"3\",\"\"");
        Assert(!parsed.SkillAiRules.ContainsKey("sliding_step"), "활성화=FALSE인 배틀스킬AI 행은 검증만 하고 제외한다");
        foreach (var (row, name) in new[]
        {
            ("\"bad\",\"sliding_step\",\"TRUE\",\"자신\",\"체력\",\"\",\"<\",\"0.5\",\"1\",\"\",\"\",\"\",\"\"", "모르는 조건유형"),
            ("\"bad\",\"nope\",\"TRUE\",\"자신\",\"HP비율\",\"\",\"<\",\"0.5\",\"1\",\"\",\"\",\"\",\"\"", "없는 스킬 ID"),
            ("\"bad\",\"sliding_step\",\"TRUE\",\"자신\",\"HP비율\",\"\",\"<\",\"50\",\"1\",\"\",\"\",\"\",\"\"", "HP비율을 퍼센트로 적은 값"),
            ("\"bad\",\"sliding_step\",\"TRUE\",\"자신\",\"HP비율\",\"\",\"<\",\"0.5\",\"1\",\"0.01\",\"0.1\",\"\",\"\"", "최대가산 없는 비례 가산"),
            ("\"bad\",\"sliding_step\",\"TRUE\",\"자신\",\"HP비율\",\"\",\"==\",\"0.5\",\"1\",\"0.01\",\"0.1\",\"3\",\"\"", "같다 조건의 비례 가산"),
            ("\"bad\",\"sliding_step\",\"TRUE\",\"상대\",\"상태효과유형보유\",\"브레이크\",\"\",\"\",\"1\",\"1\",\"1\",\"3\",\"\"", "보유 조건의 비례 가산"),
            ("\"bad\",\"sliding_step\",\"TRUE\",\"상대\",\"상태효과유형보유\",\"없는효과\",\"\",\"\",\"1\",\"\",\"\",\"\",\"\"", "없는 효과유형"),
            ("\"bad\",\"sliding_step\",\"TRUE\",\"자신\",\"자원보유\",\"none\",\">=\",\"1\",\"1\",\"\",\"\",\"\",\"\"", "없는 자원 ID"),
            ("\"bad\",\"sliding_step\",\"TRUE\",\"자신\",\"HP비율\",\"\",\"<\",\"0.5\",\"0\",\"\",\"\",\"\",\"\"", "가산이 모두 0인 행"),
        })
        {
            var rejected = false;
            try { Crossbow(row); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "배틀스킬AI 로더는 잘못된 행을 거부한다: " + name);
        }
    }

    private static BattleDataSnapshot Crossbow(params string[] rows) => Parse(CrossbowBattleTests.Sheets, rows);
    private static BattleDataSnapshot Healer(params string[] rows) => Parse(HealerBattleTests.Sheets, rows);

    private static BattleDataSnapshot Parse(IReadOnlyDictionary<string, string> sheets, string[] rows)
    {
        var tables = sheets.ToDictionary(x => x.Key, x => x.Value);
        tables["배틀스킬AI"] = string.Join("\n", rows.Prepend(Header));
        return BattleCatalog.Parse(tables, DateTimeOffset.UtcNow);
    }

    /// <summary>상대는 스킬 없이 일반 공격만 하는 "idle"이다. 체력을 크게 두고 돌발 이벤트를 끈다.</summary>
    private static BattleResult Duel(BattleDataSnapshot data, string classId, string[] skills, double random, (string Id, int Value)[]? initial = null)
    {
        var rules = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", "4") }) rules[id] = rules[id] with { Value = value };
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in initial ?? []) resources[id] = resources[id] with { InitialValue = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = rules,
            Classes = new Dictionary<string, BattleClass> { [classId] = data.Classes[classId] with { SkillIds = skills }, ["idle"] = new("idle", "대상", Array.Empty<string>()) },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, SkillAiRules = data.SkillAiRules, LoadedAt = data.LoadedAt
        };
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", classId, 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new ConstantRandom(random));
    }

    private static string? FirstSkill(BattleResult result) => result.Events.FirstOrDefault(x => x.Type == "SkillUsed" && x.Actor == "A")?.Detail;

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class ConstantRandom(double value) : IBattleRandom
    {
        public double NextDouble() => value;
    }
}
