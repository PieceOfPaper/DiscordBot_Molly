using Molly.Battle;

/// <summary>
/// 배틀생활스킬(기획서 7.2) 로딩 검증과 엔진 동작을 검사한다.
/// LifeSheets는 2026-09-25 배틀생활스킬·배틀생활스킬효과 시트의 사본이고, 생활스킬은 이름 열만 남긴 사본이다. 시트를 바꾸면 이 사본도 함께 갱신한다.
/// 엔진 검사는 방어·편차·치명타·추가타를 끈 규칙으로 피해 비율을 정확히 비교한다.
/// </summary>
internal static class LifeSkillBattleTests
{
    public static void Run()
    {
        var data = BattleCatalog.Parse(Tables(), DateTimeOffset.UtcNow);
        ParseTests(data);
        FormulaTests(data);
        DamageTests(data);
        CandidateTests(data);
        StatusTests(data);
        RenderTests();
    }

    private static void ParseTests(BattleDataSnapshot data)
    {
        Assert(data.LifeSkills.Count == 19 && data.LifeSkills.All(x => x.Enabled) && data.LifeSkills.All(x => x.Name != "연금술"),
            "배틀생활스킬 시트는 연금술을 제외한 생활스킬 19개를 모두 불러온다");
        Assert(Life(data, "fishing").Effects.Count == 3 && Life(data, "fishing").Effects.All(x => x.SelectionGroup == "catch") && Life(data, "daily_gathering").Effects.Count == 4,
            "낚시·일상 채집의 무작위 결과 행은 같은 선택그룹으로 불러온다");
        Assert(Throws(() => Tables(("배틀생활스킬", "\"나무 베기\"", "\"나무베기\""))).Contains("생활스킬 시트에 없습니다"),
            "생활스킬 시트에 없는 이름은 로드 오류다");
        Assert(Throws(() => Tables(("배틀생활스킬", "\"아르바이트\"", "\"연금술\""))).Contains("연금술"),
            "생활력과 무관한 연금술은 배틀생활스킬로 만들 수 없다");
        Assert(Throws(() => Tables(("배틀생활스킬", "\"핸디크래프트\"", "\"아르바이트\""))).Contains("중복"),
            "같은 생활스킬 이름을 두 번 쓰면 로드 오류다");
        Assert(Throws(() => Tables(("배틀생활스킬효과", "\"woodcutting_01\",\"woodcutting\",\"1\",\"피해\",\"상대\",\"공격력\"", "\"woodcutting_01\",\"woodcutting\",\"1\",\"피해\",\"상대\",\"최대HP\""))).Contains("계수기준"),
            "효과유형과 계수기준이 맞지 않으면 로드 오류다");
        Assert(Throws(() => Tables(("배틀생활스킬효과", "\"다음피해감소\"", "\"다음피해경감\""))).Contains("지원하지 않습니다"),
            "엔진이 모르는 생활스킬 효과유형은 조용히 무시하지 않고 로드 오류다");
        Assert(Throws(() => Tables(("배틀생활스킬", "\"mining\",\"광석 캐기\",\"TRUE\",\"0.06\"", "\"mining\",\"광석 캐기\",\"TRUE\",\"0.05\""))).Contains("같아야"),
            "사용 판정 값이 행마다 다르면 로드 오류다");
        Assert(Throws(() => Tables(("배틀생활스킬", "\"daily_gathering\",\"일상 채집\",\"TRUE\",\"0.06\"", "\"daily_gathering\",\"일상 채집\",\"TRUE\",\"6.0%\""))).Contains("기본사용확률"),
            "퍼센트 서식으로 내보낸 확률(6.0%)은 로드 오류다");
        var withoutPartTime = Tables();
        withoutPartTime["배틀생활스킬"] = string.Join("\n", withoutPartTime["배틀생활스킬"].Split('\n').Where(x => !x.StartsWith("\"part_time_job\"", StringComparison.Ordinal)));
        withoutPartTime["배틀생활스킬효과"] = string.Join("\n", withoutPartTime["배틀생활스킬효과"].Split('\n').Where(x => !x.StartsWith("\"part_time_job_", StringComparison.Ordinal)));
        Assert(Throws(() => withoutPartTime).Contains("누락"), "연금술 외 생활스킬이 배틀생활스킬 시트에서 빠지면 로드 오류다");
    }

    private static void FormulaTests(BattleDataSnapshot data)
    {
        var skill = Life(data, "woodcutting");
        Assert(Near(skill.UseChance(0), .06) && Near(skill.UseChance(30000), .11) && Near(skill.UseChance(60000), .16) && Near(skill.UseChance(500000), .16),
            "사용 확률은 생활력 0에서 6%, 기준값 30,000에서 11%, 60,000 이상에서 최대 16%다");
        var effect = skill.Effects[0];
        Assert(Near(effect.LifeMultiplier(30000), 1) && Near(effect.LifeMultiplier(20000), Math.Sqrt(2d / 3)) && Near(effect.LifeMultiplier(7500), .7) && Near(effect.LifeMultiplier(120000), 1.5),
            "생활력효과배율은 sqrt(생활력/30000)을 0.7~1.5로 제한한다");
    }

    private static void DamageTests(BattleDataSnapshot data)
    {
        // 난수 0(선공 A)·0(사용 판정)·0(후보 선택) 뒤로는 0.5가 이어진다. B는 0.5로 사용 판정에 실패하고 A는 전투당 1회라 두 번째 행동은 일반 공격이다.
        foreach (var (lifePower, multiplier) in new[] { (30000, 1d), (20000, Math.Sqrt(2d / 3)), (120000, 1.5) })
        {
            var result = Simulate(Snapshot(data, ["woodcutting"]), "idle", lifePower, "idle", [0, 0, 0]);
            var life = result.Events.First(x => x.Type == "LifeSkillEffect" && x.Actor == "A").Amount ?? 0;
            var normal = result.Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
            Assert(Near(life, normal * 5.5 / 1.5 * multiplier, 2), $"나무 베기는 공격력 × 5.5 × 생활력효과배율로 클래스 행동에 버금가는 한 방이다(생활력 {lifePower:N0})");
        }
        var once = Simulate(Snapshot(data, ["woodcutting"]), "idle", 30000, "idle", Enumerable.Repeat(0d, 200));
        Assert(once.Events.Count(x => x.Type == "LifeSkillUsed" && x.Actor == "A") == 1 && once.Events.Count(x => x.Type == "LifeSkillUsed" && x.Actor == "B") == 1,
            "매 행동 판정이 성공해도 배틀생활스킬은 캐릭터당 전투에서 1회만 사용한다");
        Assert(once.Events.SkipWhile(x => x.Type != "LifeSkillUsed").Skip(1).First().Type == "LifeSkillNarration"
            && once.Events.First(x => x.Type == "LifeSkillNarration").Detail == "A이(가) 도끼를 크게 휘둘러 B을(를) 내려찍습니다!",
            "생활스킬 사용 로그 뒤에 시트의 행동문구를 이름과 조사를 채워 남긴다");

        // 0.95로 선택그룹을 뽑으면 가중치 3·5·2 중 마지막인 낡은 장화, 0이면 첫 번째인 월척이다.
        var boot = Simulate(Snapshot(data, ["fishing"]), "idle", 30000, "idle", [0, 0, 0, .95]);
        var bootEffect = boot.Events.First(x => x.Type == "LifeSkillEffect");
        var bootNormal = boot.Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
        Assert(bootEffect.Detail!.StartsWith("낡은 장화", StringComparison.Ordinal) && Near(bootEffect.Amount ?? 0, bootNormal * 3.0 / 1.5, 2),
            "낚시는 선택그룹에서 하나만 실행하며 낡은 장화는 가장 약한 결과(계수 3.0)다");
        var bigCatch = Simulate(Snapshot(data, ["fishing"]), "idle", 30000, "idle", [0, 0, 0, 0]);
        Assert(bigCatch.Events.Count(x => x.Type == "LifeSkillEffect") == 1 && bigCatch.Events.First(x => x.Type == "LifeSkillEffect").Detail!.StartsWith("월척!", StringComparison.Ordinal),
            "낚시 월척이 나오면 다른 결과 행은 실행하지 않는다");
    }

    private static void CandidateTests(BattleDataSnapshot data)
    {
        // HP가 가득 찬 동안 물약 조제(HP 50% 미만)는 후보가 아니다. 후보가 없으면 난수를 소비하지 않아 생활스킬이 없는 전투와 로그가 같다.
        double[] sequence = [0, .31, .72, .13, .94, .45, .66, .27, .88, .09];
        var withPotion = Simulate(Snapshot(data, ["potion_making"]), "idle", 30000, "idle", sequence);
        var withoutLife = Simulate(Snapshot(data, []), "idle", 30000, "idle", sequence);
        Assert(withPotion.Events.Select(x => x.ToString()).SequenceEqual(withoutLife.Events.Select(x => x.ToString())),
            "효과가 없는 상황의 생활스킬은 후보에서 빠지고, 후보가 없으면 난수를 소비하지 않는다");

        // B가 먼저(0.9) 약화를 걸면 A에게 해로운 상태가 생겨 약초 채집이 후보가 된다. 해로운 상태가 하나뿐이면 제거 대상 선택에 난수를 쓰지 않는다.
        var herb = Simulate(Snapshot(data, ["herb_gathering"]), "idle", 30000, "hexer", [.9, .5, 0, 0]);
        Assert(herb.Events.Any(x => x.Type == "StatusCleansed" && x.Actor == "A" && x.Detail == "약화")
            && herb.Events.Any(x => x.Type == "LifeSkillEffect" && x.Detail == "해로운 상태 1개를 제거했습니다."),
            "약초 채집은 상대가 건 해로운 상태를 제거한다");
        var selfBuff = Simulate(Snapshot(data, ["magic_craft", "herb_gathering"]), "idle", 30000, "idle", [0, 0, 0]);
        Assert(!selfBuff.Events.Any(x => x.Type == "StatusCleansed"),
            "자신이 건 생활스킬 강화 상태는 해로운 상태가 아니다");
    }

    private static void StatusTests(BattleDataSnapshot data)
    {
        // 양털 깎기(80%)는 HP 90% 미만에서만 후보라 조건을 풀어 검사한다. B의 다음 공격 행동 한 번만 줄이고 소모된다.
        var sheep = Simulate(Snapshot(data, ["sheep_shearing"], Always), "idle", 30000, "idle", [0, 0, 0]);
        var bHits = sheep.Events.Where(x => x.Type == "DamageDealt" && x.Actor == "B").Select(x => x.Amount ?? 0).ToArray();
        Assert(Near(bHits[0], bHits[1] * .2, 2) && sheep.Events.Count(x => x.Type == "StatusConsumed" && x.Actor == "A" && x.Detail == "양털 깎기") == 1,
            "양털 깎기는 다음에 받는 공격 한 번의 피해를 80% 줄이고 그 공격 뒤 소모된다");
        // 다단 공격(2타)은 두 타격 모두 줄어든 뒤 소모된다.
        var multi = Simulate(Snapshot(data, ["sheep_shearing"], Always), "idle", 30000, "doubler", [0, 0, 0, .5]);
        var multiHits = multi.Events.Where(x => x.Type == "DamageDealt" && x.Actor == "B").Select(x => x.Amount ?? 0).ToArray();
        Assert(multiHits.Length >= 4 && Near(multiHits[0], multiHits[2] * .2, 2) && Near(multiHits[1], multiHits[3] * .2, 2),
            "양털 깎기는 다단 공격의 모든 타격에 적용되고 그 공격 행동이 끝나면 소모된다");
        var capped = Simulate(Snapshot(data, ["insect_collecting"]), "idle", 120000, "idle", [0, 0, 0]);
        var cappedHits = capped.Events.Where(x => x.Type == "DamageDealt" && x.Actor == "B").Select(x => x.Amount ?? 0).ToArray();
        Assert(Near(cappedHits[0], cappedHits[1] * .2, 2),
            "약화 비율은 생활력 배율(0.8×1.5=120%)을 곱한 뒤 life_skill_ratio_cap(80%)으로 제한한다");

        // 대장 기술(250%)은 다음 클래스 스킬에만 적용되고 그 스킬 행동 뒤 소모된다. 베기는 쿨다운 1이라 A는 두 번 연속 베기를 쓴다.
        var smith = Simulate(Snapshot(data, ["blacksmithing"]), "slasher", 30000, "idle", [0, 0, 0]);
        var slashes = smith.Events.Where(x => x.Type == "DamageDealt" && x.Actor == "A").Select(x => x.Amount ?? 0).ToArray();
        Assert(Near(slashes[0], slashes[1] * 3.5, 2) && smith.Events.Any(x => x.Type == "StatusConsumed" && x.Actor == "A" && x.Detail == "대장 기술"),
            "대장 기술은 다음 전투 스킬 피해를 250% 올리고 그 스킬 뒤 소모된다");
        var smithIdle = Simulate(Snapshot(data, ["blacksmithing"]), "idle", 30000, "idle", [0, 0, 0]);
        var aNormal = smithIdle.Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount;
        var bNormal = smithIdle.Events.First(x => x.Type == "DamageDealt" && x.Actor == "B").Amount;
        Assert(aNormal == bNormal && !smithIdle.Events.Any(x => x.Type == "StatusConsumed"),
            "다음 전투 스킬 강화는 일반 공격에 적용되거나 소모되지 않는다");

        // 목공은 다음 클래스 스킬을 치명타로 만들고(기본 치명타 확률 0) 치명타 피해를 200% 더한다.
        var carpentry = Simulate(Snapshot(data, ["carpentry"]), "slasher", 30000, "idle", [0, 0, 0]);
        var carpentryHits = carpentry.Events.Where(x => x.Type == "DamageDealt" && x.Actor == "A").Select(x => x.Amount ?? 0).ToArray();
        Assert(carpentry.Events.Count(x => x.Type == "CriticalHit" && x.Actor == "A") == 1 && Near(carpentryHits[0], carpentryHits[1] * 3.5, 2),
            "목공은 다음 전투 스킬을 치명타(1.5 + 2.0배)로 적중시킨다");

        // 경갑 제작(75%) 중 B의 공격은 회피 판정 0.1에서 빗나간다. 회피 상태가 없으면 회피 판정 난수를 쓰지 않는다.
        var light = Simulate(Snapshot(data, ["light_armor_crafting"]), "idle", 30000, "idle", [0, 0, 0, .9, .1]);
        Assert(light.Events.Any(x => x.Type == "AttackEvaded" && x.Target == "A"), "경갑 제작은 상대 공격을 회피 확률로 흘려낸다");

        // 곤충 채집(피해 3.0 + 상대 다음 행동 피해 80% 감소)은 B의 다음 행동 한 번만 약화한다.
        var insect = Simulate(Snapshot(data, ["insect_collecting"]), "idle", 30000, "idle", [0, 0, 0]);
        var insectHits = insect.Events.Where(x => x.Type == "DamageDealt" && x.Actor == "B").Select(x => x.Amount ?? 0).ToArray();
        Assert(Near(insectHits[0], insectHits[1] * .2, 2) && insect.Events.Any(x => x.Type == "StatusExpired" && x.Actor == "B" && x.Detail == "곤충 채집"),
            "곤충 채집은 상대의 다음 행동 피해를 80% 줄이고 그 행동이 끝나면 풀린다");
    }

    private static void RenderTests()
    {
        Assert(BattleEngine.RenderLifeText("{caster}가 {target}을 향해 {target}와 함께", "종잇장", "나무") == "종잇장이 나무를 향해 나무와 함께"
            && BattleEngine.RenderLifeText("{caster}는 {target}에게 {damage}의 피해!", "ZEL", "B", damage: 12345) == "ZEL은(는) B에게 12,345의 피해!",
            "생활스킬 문구는 이름의 받침에 맞춰 조사를 고르고, 한글이 아니면 병기한다");
    }

    private static BattleLifeSkill Life(BattleDataSnapshot data, string id) => data.LifeSkills.Single(x => x.Id == id);

    /// <summary>HP 조건이 있는 생활스킬을 전투 첫 행동에서 검사하도록 조건을 항상 참으로 바꾼다.</summary>
    private static BattleLifeSkill Always(BattleLifeSkill skill) => skill with { ConditionTarget = "자신", ConditionType = "생존", ConditionOperator = "=", ConditionValue = 1 };

    private static BattleDataSnapshot Snapshot(BattleDataSnapshot data, string[] lifeSkillIds, Func<BattleLifeSkill, BattleLifeSkill>? adjust = null)
    {
        var rules = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "1000000"), ("base_defense", "0"), ("max_surprise_events_per_actor", "0"), ("additional_hit_chance", "0"), ("damage_variance_min", "1"), ("damage_variance_max", "1"), ("base_critical_chance", "0"), ("max_major_actions", "8"), ("skill_damage_min_multiplier", "2"), ("skill_damage_max_multiplier", "2") })
            rules[id] = rules[id] with { Value = value };
        rules["life_skill_max_uses_per_actor"] = new("life_skill_max_uses_per_actor", "생활스킬", "integer", "1", "");
        rules["life_skill_ratio_cap"] = new("life_skill_ratio_cap", "생활스킬", "number", "0.8", "");
        var skills = data.Skills.ToDictionary(x => x.Key, x => x.Value);
        skills["slash"] = new("slash", "베기", "일반", null, true, 0, 0, 0, 1d, [new BattleEffect("slash_01", 1, "피해", "상대", 0, 1, 1d, 0, null, 0, null, null, null, null, null, null, null, null)]);
        skills["double"] = new("double", "연타", "일반", null, true, 0, 0, 0, 1d, [new BattleEffect("double_01", 1, "피해", "상대", 0, 2, 1d, 0, null, 0, null, null, null, null, null, null, null, null)]);
        skills["hex"] = new("hex", "저주", "일반", null, true, 0, 0, 0, 1d, [new BattleEffect("hex_01", 1, "약화부여", "상대", 0, 1, 1d, 2, "weak", 0, null, null, null, null, null, null, null, null)]);
        var statuses = data.Statuses.ToDictionary(x => x.Key, x => x.Value);
        statuses["weak"] = new BattleStatus("weak", "약화", "받는피해증가", .5, "");
        return new BattleDataSnapshot
        {
            Rules = rules,
            Classes = new Dictionary<string, BattleClass> { ["idle"] = new("idle", "대상", Array.Empty<string>()), ["slasher"] = new("slasher", "검객", ["slash"]), ["hexer"] = new("hexer", "주술사", ["hex"]), ["doubler"] = new("doubler", "연타꾼", ["double"]) },
            Skills = skills, Passives = data.Passives, Resources = data.Resources, Statuses = statuses, Derivations = data.Derivations,
            LifeSkills = lifeSkillIds.Select(id => adjust is null ? Life(data, id) : adjust(Life(data, id))).ToArray(), LoadedAt = data.LoadedAt
        };
    }

    private static BattleResult Simulate(BattleDataSnapshot snapshot, string classA, int lifePowerA, string classB, IEnumerable<double> random)
        => new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", classA, 100, lifePowerA, 0), new CharacterBattleSnapshot(2, "B", classB, 100, 30000, 0), snapshot, new SequenceRandom(random));

    private static Dictionary<string, string> Tables(params (string Sheet, string From, string To)[] edits)
    {
        var tables = CrossbowBattleTests.Sheets.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (sheet, csv) in LifeSheets) tables[sheet] = csv;
        foreach (var (sheet, from, to) in edits)
        {
            var index = tables[sheet].IndexOf(from, StringComparison.Ordinal);
            if (index < 0) throw new Exception("테스트 전제: " + sheet + " 사본에 '" + from + "'이(가) 없습니다.");
            tables[sheet] = tables[sheet][..index] + to + tables[sheet][(index + from.Length)..];
        }
        return tables;
    }

    private static string Throws(Func<Dictionary<string, string>> tables)
    {
        try { BattleCatalog.Parse(tables(), DateTimeOffset.UtcNow); }
        catch (InvalidDataException ex) { return ex.Message; }
        return "(예외 없음)";
    }

    private static bool Near(double actual, double expected, double tolerance = 1e-9) => Math.Abs(actual - expected) <= tolerance;

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class SequenceRandom(IEnumerable<double> values) : IBattleRandom
    {
        private readonly Queue<double> values = new(values);
        public double NextDouble() => values.Count == 0 ? .5 : values.Dequeue();
    }

    private static readonly IReadOnlyDictionary<string, string> LifeSheets = new Dictionary<string, string>
    {
        ["생활스킬"] = """"
        "이름","설명","기타"
        "일상 채집","",""
        "나무 베기","",""
        "광석 캐기","",""
        "약초 채집","",""
        "양털 깎기","",""
        "추수","",""
        "호미질","",""
        "곤충 채집","",""
        "낚시","",""
        "대장 기술","",""
        "목공","",""
        "매직 크래프트","",""
        "중갑 제작","",""
        "경갑 제작","",""
        "천옷 제작","",""
        "물약 조제","",""
        "요리","",""
        "핸디크래프트","",""
        "연금술","",""
        "아르바이트","",""
        """",
        ["배틀생활스킬"] = """"
        "ID","생활스킬이름","활성화","기본사용확률","생활력기준값","생활력확률보정계수","최대사용확률","전투당최대횟수","선택가중치","조건대상","조건유형","조건연산자","조건값","행동문구","비고"
        "daily_gathering","일상 채집","TRUE","0.06","30000","0.05","0.16","1","1","자신","HP비율","<","0.8","{caster}가 주변을 뒤져 손에 잡히는 것을 집어 듭니다!","무작위 채집물 1개(달걀·사과·거미줄·황금 네잎클로버). 원본: 맨손으로 달걀·사과·거미줄 등 채집"
        "woodcutting","나무 베기","TRUE","0.06","30000","0.05","0.16","1","1.1","상대","생존","=","1","{caster}가 도끼를 크게 휘둘러 {target}을 내려찍습니다!","강한 단타. 원본: 벌목 도끼"
        "mining","광석 캐기","TRUE","0.06","30000","0.05","0.16","1","1","상대","생존","=","1","{caster}가 곡괭이로 {target}의 단단한 방어를 깨뜨립니다!","직접 피해와 높은 브레이크. 원본: 곡괭이"
        "herb_gathering","약초 채집","TRUE","0.06","30000","0.05","0.16","1","1.25","자신","해로운상태개수",">","0","{caster}가 급히 약초를 다듬어 몸을 추스릅니다.","해로운 상태 제거와 회복. 원본: 약초 괭이로 약초·버섯 채집"
        "sheep_shearing","양털 깎기","TRUE","0.06","30000","0.05","0.16","1","1","자신","HP비율","<","0.9","{caster}가 풍성한 양털을 둘러 충격에 대비합니다.","다음 1회 받는 피해 크게 감소와 소량 회복. 원본: 양털 가위"
        "harvesting","추수","TRUE","0.06","30000","0.05","0.16","1","1.05","상대","생존","=","1","{caster}가 낫을 연달아 휘둘러 {target}을 베어 냅니다!","2회 연속 공격. 원본: 낫"
        "hoeing","호미질","TRUE","0.06","30000","0.05","0.16","1","1.05","상대","생존","=","1","{caster}가 {target}의 발밑을 호미로 파헤쳐 자세를 무너뜨립니다!","직접 피해와 브레이크. 원본: 호미로 땅속 작물 채굴"
        "insect_collecting","곤충 채집","TRUE","0.06","30000","0.05","0.16","1","1","상대","생존","=","1","{caster}가 채집망을 휘둘러 {target}의 시야를 가립니다!","직접 피해와 상대 다음 행동 피해 감소. 원본: 곤충 채집망"
        "fishing","낚시","TRUE","0.06","30000","0.05","0.16","1","1.1","상대","생존","=","1","{caster}가 {target}을 향해 낚싯대를 힘껏 던집니다!","무작위 결과(월척·잔챙이·낡은 장화). 원본: 낚싯대"
        "blacksmithing","대장 기술","TRUE","0.06","30000","0.05","0.16","1","1","자신","생존","=","1","{caster}가 무기를 빠르게 벼려 다음 일격을 준비합니다.","다음 전투 스킬 피해 크게 증가. 원본: 금속으로 근거리 무기 제작"
        "carpentry","목공","TRUE","0.06","30000","0.05","0.16","1","1","자신","생존","=","1","{caster}가 무기의 균형을 맞춰 급소를 겨눕니다.","다음 전투 스킬 치명타 확정과 치명타 피해 증가. 원본: 목재로 원거리 무기 제작"
        "magic_craft","매직 크래프트","TRUE","0.06","30000","0.05","0.16","1","1","자신","생존","=","1","{caster}가 무기에 마력을 불어넣어 마법 무기로 벼려 냅니다!","2턴 동안 주는 피해 증가. 원본: 다양한 재료로 마법 무기 제작"
        "heavy_armor_crafting","중갑 제작","TRUE","0.06","30000","0.05","0.16","1","1","자신","생존","=","1","{caster}가 갑옷의 약한 부분을 보강해 단단한 방어 자세를 취합니다.","2턴 동안 받는 피해 감소. 원본: 금속으로 중갑 방어구 제작"
        "light_armor_crafting","경갑 제작","TRUE","0.06","30000","0.05","0.16","1","1","자신","생존","=","1","{caster}가 가죽 장비를 가볍게 손봐 몸놀림을 끌어올립니다.","2턴 동안 회피 확률 증가. 원본: 가죽으로 활동성 높은 경갑 제작"
        "tailoring","천옷 제작","TRUE","0.06","30000","0.05","0.16","1","1","자신","생존","=","1","{caster}가 옷매무새를 고쳐 흐트러지지 않는 자세를 갖춥니다.","해로운 상태 1개 제거, 2턴 동안 받는 피해 감소와 브레이크 면역. 원본: 옷감과 실크로 몸에 맞는 천옷 제작"
        "potion_making","물약 조제","TRUE","0.06","30000","0.05","0.16","1","1.3","자신","HP비율","<","0.5","{caster}가 즉석에서 물약을 조제해 단숨에 들이켜고 붕대를 감습니다!","큰 즉시 회복과 붕대 지속 회복. 원본: 물약과 붕대 제작"
        "cooking","요리","TRUE","0.06","30000","0.05","0.16","1","1.1","자신","HP비율","<","0.8","{caster}가 간단한 요리를 먹고 기운을 되찾습니다.","회복 후 다음 전투 스킬 피해 증가. 원본: 먹으면 기운이 나는 음식 제작"
        "handicraft","핸디크래프트","TRUE","0.06","30000","0.05","0.16","1","1.1","상대","생존","=","1","{caster}가 즉석 도구를 만들어 {target}에게 던집니다!","도구 폭발 피해와 브레이크. 원본: 소모품·도구 제작"
        "part_time_job","아르바이트","TRUE","0.06","30000","0.05","0.16","1","1.05","자신","HP비율","<","0.7","{caster}가 익숙한 잡일 솜씨로 전열을 빠르게 정비합니다.","모든 전투 스킬 재사용 대기시간 2턴 감소와 큰 회복. 원본: 아르바이트로 보상 획득"
        """",
        ["배틀생활스킬효과"] = """"
        "ID","배틀생활스킬ID","실행순서","효과유형","대상","계수기준","계수","고정값","생활력비례","생활력기준값","생활력최소배율","생활력최대배율","횟수","지속턴","상태효과ID","최대중첩","효과문구","비고","선택그룹","선택가중치"
        "daily_gathering_01","daily_gathering","1","피해","상대","공격력","6","0","TRUE","30000","0.7","1.5","1","0","","0","달걀을 던져 {target}에게 {damage}의 피해!","","gather","3"
        "daily_gathering_02","daily_gathering","2","회복","자신","최대HP","0.35","0","TRUE","30000","0.7","1.5","1","0","","0","사과를 베어 물어 체력을 {heal} 회복했습니다.","","gather","3"
        "daily_gathering_03","daily_gathering","3","다음행동피해감소","상대","비율","0.8","0","TRUE","30000","0.7","1.5","1","1","","0","거미줄이 {target}에게 엉겨 다음 행동 피해가 {value}% 감소합니다.","초안 효과유형: 엔진 확장 필요","gather","3"
        "daily_gathering_04","daily_gathering","4","다음스킬치명타확률증가","자신","비율","1","0","FALSE","30000","0.7","1.5","1","1","","0","황금 네잎클로버! 다음 전투 스킬이 반드시 치명타로 적중합니다.","초안 효과유형: 엔진 확장 필요. 드문 결과","gather","1"
        "woodcutting_01","woodcutting","1","피해","상대","공격력","5.5","0","TRUE","30000","0.7","1.5","1","0","","0","도끼가 {target}에게 {damage}의 피해!","","",""
        "mining_01","mining","1","피해","상대","공격력","3.4","0","TRUE","30000","0.7","1.5","1","0","","0","곡괭이가 {target}에게 {damage}의 피해!","","",""
        "mining_02","mining","2","브레이크피해","상대","개수","0","2","FALSE","30000","0.7","1.5","1","0","","0","{target}의 자세가 크게 흔들립니다!","강한 브레이크 성격","",""
        "herb_gathering_01","herb_gathering","1","해로운상태제거","자신","개수","0","1","FALSE","30000","0.7","1.5","1","0","","0","해로운 상태 1개를 제거했습니다.","초안 효과유형: 엔진 확장 필요","",""
        "herb_gathering_02","herb_gathering","2","회복","자신","최대HP","0.28","0","TRUE","30000","0.7","1.5","1","0","","0","약초의 힘으로 체력을 {heal} 회복했습니다.","","",""
        "sheep_shearing_01","sheep_shearing","1","다음피해감소","자신","비율","0.8","0","TRUE","30000","0.7","1.5","1","1","","0","양털이 다음 충격을 {value}% 줄입니다.","초안 효과유형: 엔진 확장 필요. 피격 1회 후 소모","",""
        "sheep_shearing_02","sheep_shearing","2","회복","자신","최대HP","0.2","0","TRUE","30000","0.7","1.5","1","0","","0","폭신한 양털 덕분에 체력을 {heal} 회복했습니다.","","",""
        "harvesting_01","harvesting","1","피해","상대","공격력","5.6","0","TRUE","30000","0.7","1.5","2","0","","0","낫이 {target}을 연달아 베어 총 {damage}의 피해!","총 계수를 2회로 나눔","",""
        "hoeing_01","hoeing","1","피해","상대","공격력","4.5","0","TRUE","30000","0.7","1.5","1","0","","0","호미가 {target}에게 {damage}의 피해!","","",""
        "hoeing_02","hoeing","2","브레이크피해","상대","개수","0","1","FALSE","30000","0.7","1.5","1","0","","0","{target}의 자세가 흔들립니다!","","",""
        "insect_collecting_01","insect_collecting","1","피해","상대","공격력","3","0","TRUE","30000","0.7","1.5","1","0","","0","채집망이 {target}을 후려쳐 {damage}의 피해!","","",""
        "insect_collecting_02","insect_collecting","2","다음행동피해감소","상대","비율","0.8","0","TRUE","30000","0.7","1.5","1","1","","0","흩어진 곤충 때문에 {target}의 다음 행동 피해가 {value}% 감소합니다.","초안 효과유형: 엔진 확장 필요","",""
        "fishing_01","fishing","1","피해","상대","공격력","8","0","TRUE","30000","0.7","1.5","1","0","","0","월척! 거대한 물고기가 {target}을 덮쳐 {damage}의 피해!","","catch","3"
        "fishing_02","fishing","2","피해","상대","공격력","4.3","0","TRUE","30000","0.7","1.5","1","0","","0","잔챙이가 {target}의 얼굴을 철썩 때려 {damage}의 피해!","","catch","5"
        "fishing_03","fishing","3","피해","상대","공격력","3","0","TRUE","30000","0.7","1.5","1","0","","0","낡은 장화가 걸려 올라와 {target}에게 {damage}의 피해…","꽝 결과","catch","2"
        "blacksmithing_01","blacksmithing","1","다음스킬피해증가","자신","비율","2.5","0","TRUE","30000","0.7","1.5","1","1","","0","벼린 무기로 다음 전투 스킬 피해가 {value}% 증가합니다.","초안 효과유형: 엔진 확장 필요. 스킬피해증가 + 1회 소모","",""
        "carpentry_01","carpentry","1","다음스킬치명타확률증가","자신","비율","1","0","FALSE","30000","0.7","1.5","1","1","","0","다음 전투 스킬이 반드시 치명타로 적중합니다.","초안 효과유형: 엔진 확장 필요. 치명타확률증가 + 1회 소모","",""
        "carpentry_02","carpentry","2","다음스킬치명타피해증가","자신","비율","2","0","TRUE","30000","0.7","1.5","1","1","","0","다음 전투 스킬의 치명타 피해가 {value}% 증가합니다.","초안 효과유형: 엔진 확장 필요. 치명타피해증가 + 1회 소모","",""
        "magic_craft_01","magic_craft","1","주는피해증가","자신","비율","0.9","0","TRUE","30000","0.7","1.5","1","2","","0","마력이 깃든 무기로 2턴 동안 주는 피해가 {value}% 증가합니다.","기존 상태 효과 종류 주는피해증가로 매핑","",""
        "heavy_armor_crafting_01","heavy_armor_crafting","1","받는피해감소","자신","비율","0.7","0","TRUE","30000","0.7","1.5","1","2","","0","2턴 동안 받는 피해가 {value}% 감소합니다.","기존 상태 효과 종류 받는피해감소로 매핑","",""
        "light_armor_crafting_01","light_armor_crafting","1","회피확률증가","자신","비율","0.75","0","TRUE","30000","0.7","1.5","1","2","","0","2턴 동안 {value}% 확률로 공격을 회피합니다.","초안 효과유형: 엔진 확장 필요","",""
        "tailoring_01","tailoring","1","해로운상태제거","자신","개수","0","1","FALSE","30000","0.7","1.5","1","0","","0","해로운 상태 1개를 제거했습니다.","초안 효과유형: 엔진 확장 필요","",""
        "tailoring_02","tailoring","2","받는피해감소","자신","비율","0.7","0","TRUE","30000","0.7","1.5","1","2","","0","2턴 동안 받는 피해가 {value}% 감소합니다.","기존 상태 효과 종류 받는피해감소로 매핑","",""
        "tailoring_03","tailoring","3","브레이크면역","자신","턴","0","0","FALSE","30000","0.7","1.5","1","2","","0","2턴 동안 자세가 무너지지 않습니다.","기존 상태 효과 종류 브레이크면역으로 매핑","",""
        "potion_making_01","potion_making","1","회복","자신","최대HP","0.26","0","TRUE","30000","0.7","1.5","1","0","","0","물약으로 체력을 {heal} 회복했습니다.","","",""
        "potion_making_02","potion_making","2","지속회복","자신","최대HP","0.05","0","TRUE","30000","0.7","1.5","1","2","","0","붕대를 감아 매 턴 체력을 {heal} 회복합니다.","지속회복 최대HP 계수형 확장 필요","",""
        "cooking_01","cooking","1","회복","자신","최대HP","0.2","0","TRUE","30000","0.7","1.5","1","0","","0","요리로 체력을 {heal} 회복했습니다.","","",""
        "cooking_02","cooking","2","다음스킬피해증가","자신","비율","0.6","0","TRUE","30000","0.7","1.5","1","1","","0","든든하게 먹어 다음 전투 스킬 피해가 {value}% 증가합니다.","초안 효과유형: 엔진 확장 필요","",""
        "handicraft_01","handicraft","1","피해","상대","공격력","4.6","0","TRUE","30000","0.7","1.5","1","0","","0","즉석 도구가 폭발해 {target}에게 {damage}의 피해!","","",""
        "handicraft_02","handicraft","2","브레이크피해","상대","개수","0","1","FALSE","30000","0.7","1.5","1","0","","0","폭발의 충격에 {target}의 자세가 흔들립니다!","","",""
        "part_time_job_01","part_time_job","1","쿨다운감소","자신","턴","0","2","FALSE","30000","0.7","1.5","1","0","","0","모든 전투 스킬의 재사용 대기시간이 2턴 감소했습니다.","기존 쿨다운감소(전체 감소)","",""
        "part_time_job_02","part_time_job","2","회복","자신","최대HP","0.3","0","TRUE","30000","0.7","1.5","1","0","","0","빠른 정비로 체력을 {heal} 회복했습니다.","","",""
        """"
    };
}
