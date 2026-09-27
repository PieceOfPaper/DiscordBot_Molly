using Molly.Battle;

/// <summary>
/// 화염술사의 열기·익스플로전(열기 비례 피해·버닝 소울 비축)·레피드 파이어 초기화·버스트 캐논·인페르노·블레이즈·이그나이트 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-09-27 배틀 시트에서 화염술사 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 화염술사 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·배틀스킬AI·돌발 이벤트·생활스킬 시트는 석궁사수 사본을 그대로 쓴다.
/// </summary>
internal static class FireMageBattleTests
{
    // 난수 0.9의 피해 편차: 0.9 + 0.9 × (1.1 − 0.9).
    private const double Variance = .9 + .9 * .2;
    private static int Hit(double shownDamage, double outgoing = 1, double incoming = 1) => (int)Math.Round((shownDamage * .25 - 200 * .5) * Variance * outgoing * incoming);
    // 전투 숙련: 기교의 멀티히트 피해 +5%는 다단 피해에만 붙는다.
    private const double Multi = 1.05;

    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow);
        ExplosionTests(data);
        CannonTests(data);
        InfernoTests(data);
        OtherSkillTests(data);
    }

    private static void ExplosionTests(BattleDataSnapshot data)
    {
        // 레피드 파이어(화염구 3개·작열) → 블레이즈 불줄기(불길)·이그나이트 첫 스킬 공격(추가 피해·점화 1) → 익스플로전.
        // 열기 100에서 비용 50을 내고 남은 50으로 ×1.5, 점화 1중첩으로 ×1.1. 소모한 열기 100(+점화 10)으로 버닝 소울 1단계.
        // 폭발로 레피드 파이어가 초기화되어 다음 행동에 화염구 5개를 쏜다(멀티히트 +5%, 버닝 소울 1단계 +3%).
        var explosion = Duel(data, ["explosion", "rapid_fire"], maxActions: 5, initial: [("fire_heat", 100)], withoutPassives: ["spark"]);
        var turns = Turns(explosion);
        Assert(turns[0].Skill == "레피드 파이어" && turns[0].Hits == 4 && turns[0].Damage == 3 * Hit(3340, Multi) + Hit(3479)
            && turns[0].Statuses.Contains("작열") && turns[0].Statuses.Contains("불길") && turns[0].Resources.Contains("점화 +1 (현재 1)"),
            "레피드 파이어는 화염구 3개와 작열을 주고, 첫 스킬 공격에 이그나이트 추가 피해·점화와 블레이즈 불줄기가 붙는다");
        Assert(turns[1].Skill == "익스플로전" && turns[1].Damage == Hit(9351 * 1.5 * 1.1) + Hit(4675 * 1.5 * 1.1)
            && turns[1].Resources.Contains("열기 -50 (현재 0)") && turns[1].Resources.Contains("버닝 소울 +1 (현재 1)")
            && turns[1].Resources.Contains("점화 -1 (현재 0)") && turns[1].Resources.Contains("레피드 파이어 초기화 +1 (현재 1)"),
            "익스플로전은 열기를 모두 소모해 작열·불길을 폭발시키고, 열기와 점화에 비례해 강해지며 버닝 소울을 얻는다");
        Assert(explosion.Events.Any(x => x.Type == "StatusExpired" && x.Actor == "B" && x.Detail == "불길") && !explosion.Events.Any(x => x.Detail == "불길: 열기"),
            "익스플로전은 불길을 없애고, 로그숨김 상태(불길: 열기)의 상태해제는 로그에 남기지 않는다");
        Assert(turns[2].Skill == "레피드 파이어" && turns[2].Hits == 5 && turns[2].Damage == 5 * Hit(3340, Multi + .03)
            && turns[2].Resources.Contains("레피드 파이어 초기화 -1 (현재 0)"),
            "폭발 뒤 레피드 파이어는 재사용 대기가 초기화되어 화염구 5개를 쏘고, 버닝 소울 1단계 피해 +3%를 받는다");

        // 버닝 소울 2단계에서 열기 100을 더 소모하면 3단계가 된다. 작열·불길이 없으면 익스플로전은 피해를 주지 않는다.
        var third = Turns(Duel(data, ["explosion"], maxActions: 1, initial: [("fire_heat", 100), ("fire_soul", 2)], withoutPassives: ["spark"]));
        Assert(third[0].Damage == 0 && third[0].Resources.Contains("버닝 소울 3단계 +1 (현재 1)"), "버닝 소울 2단계에서 열기 100을 더 소모하면 3단계가 된다");

        // 치명타 난수(0.1)에서는 레피드 파이어 화염구마다 열기 +1이 더 생긴다(4·5번째 화염구 50%도 발사).
        var critical = Duel(data, ["rapid_fire"], maxActions: 1, random: .1, withoutPassives: ["spark"]).Events;
        Assert(critical.Count(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail!.StartsWith("열기 +1 ", StringComparison.Ordinal)) == 5
            && critical.Any(x => x.Type == "ResourceChanged" && x.Detail == "열기 +10 (현재 15)"),
            "버닝 소울: 스킬이 치명타로 적중하면 열기를 더 생성한다");
    }

    private static void CannonTests(BattleDataSnapshot data)
    {
        // 이그나이트 첫 스킬 공격 추가 피해는 스킬 효과 뒤에 들어가 낙인(받는 피해 +20%)을 받는다.
        var cannon = Turns(Duel(data, ["flame_cannon"], maxActions: 1));
        Assert(cannon[0].Skill == "플레임 캐논" && cannon[0].Damage == Hit(17256) + Hit(3479, incoming: 1.2) && cannon[0].Statuses.Contains("낙인")
            && cannon[0].Resources.Contains("열기 +15 (현재 65)") && !cannon[0].Resources.Any(x => x.StartsWith("레피드 파이어 초기화", StringComparison.Ordinal)),
            "플레임 캐논은 피해·작열·낙인을 주고 열기 15를 만든다");

        // 버닝 소울 3단계(최종 피해 +15% 중 3단계 표식만 있으면 +9%)에서는 버스트 캐논으로 바뀌어 폭발 추가 피해를 주고 레피드 파이어를 초기화한다.
        var burst = Turns(Duel(data, ["flame_cannon"], maxActions: 1, initial: [("fire_soul_max", 1)]));
        Assert(burst[0].Skill == "버스트 캐논" && burst[0].Damage == Hit(17256, 1.09) + Hit(8350, 1.09) + Hit(3479, 1.09, 1.2) && burst[0].Statuses.Contains("낙인")
            && burst[0].Resources.Contains("열기 +15 (현재 65)") && burst[0].Resources.Contains("레피드 파이어 초기화 +1 (현재 1)"),
            "버닝 소울 3단계에서는 플레임 캐논이 버스트 캐논으로 강화되고 플레임 캐논 효과는 실행하지 않는다");

        // 낙인은 화염술사의 공격에 받는 피해 +20%다. 두 번째 행동(일반 공격)부터 붙는다.
        var brand = Duel(data, ["flame_cannon"], maxActions: 3).Events;
        var normal = brand.SkipWhile(x => x.Type != "NormalAttackUsed" || x.Actor != "A").First(x => x.Type == "DamageDealt");
        Assert(normal.Amount == (int)Math.Round((1500 - 100) * Variance * 1.2), "낙인이 걸린 상대는 화염술사의 공격에 20% 더 큰 피해를 받는다");
    }

    private static void InfernoTests(BattleDataSnapshot data)
    {
        var turns = Turns(Duel(data, ["inferno"], maxActions: 1, initial: [("ultimate_gauge", 300)]));
        Assert(turns[0].Skill == "인페르노" && turns[0].Damage == 6 * Hit(1408, Multi) + 10 * Hit(3188, Multi) + Hit(3188) + Hit(3479, 1.15)
            && turns[0].Resources.Contains("열기 +50 (현재 100)") && turns[0].Resources.Contains("버닝 소울 +2 (현재 2)")
            && turns[0].Resources.Contains("버닝 소울 3단계 +1 (현재 1)") && turns[0].Statuses.Contains("인페르노: 열기") && turns[0].Statuses.Contains("불길"),
            "인페르노는 두 단계 광선·폭발 피해와 열기, 불길, 버닝 소울 3단계를 준다(이그나이트 추가 피해는 3단계 +15%)");    }

    private static void OtherSkillTests(BattleDataSnapshot data)
    {
        var storm = Duel(data, ["fire_storm"], maxActions: 1);
        var stormTurn = Turns(storm)[0];
        Assert(stormTurn.Hits == 12 && stormTurn.Damage == 10 * Hit(1363, Multi) + Hit(8767) + Hit(3479)
            && storm.Events.Any(x => x.Type == "BreakGaugeChanged" && x.Amount == 1) && stormTurn.Statuses.Contains("불길")
            && stormTurn.Resources.Contains("레피드 파이어 초기화 +1 (현재 1)"),
            "파이어 스톰은 연속 피해·폭발·브레이크 1칸·작열·불길을 주고 레피드 파이어를 초기화한다");

        // 플래시오버는 사용 시 열기 10, 다음 두 턴 시작마다 열기 20을 만든다. 블레이즈 불줄기의 불길도 턴마다 열기 4를 만든다.
        var flashover = Turns(Duel(data, ["flashover"], maxActions: 5, withoutPassives: ["spark"]));
        Assert(flashover[0].Skill == "플래시오버" && flashover[0].Damage == Hit(2032) + Hit(3479) && flashover[0].Resources.Contains("열기 +10 (현재 10)")
            && flashover[1].Resources.Contains("열기 +20 (현재 30)") && flashover[1].Resources.Contains("열기 +4 (현재 34)") && flashover[2].Resources.Contains("열기 +20 (현재 54)"),
            "플래시오버는 화염 파동 피해와 턴마다 열기를 준다");
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses, int Hits);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, (string Id, int Value)[]? initial = null, string[]? withoutPassives = null)
    {
        var ruleMap = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", maxActions.ToString()) }) ruleMap[id] = ruleMap[id] with { Value = value };
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in initial ?? []) resources[id] = resources[id] with { InitialValue = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = ruleMap,
            Classes = new Dictionary<string, BattleClass> { ["fire_mage"] = data.Classes["fire_mage"] with { SkillIds = skills, PassiveIds = data.Classes["fire_mage"].PassiveIds.Except(withoutPassives ?? []).ToArray() }, ["idle"] = new("idle", "대상", Array.Empty<string>()) },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, LoadedAt = data.LoadedAt
        };
        // 화염술사가 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "fire_mage", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
    }

    /// <summary>A의 행동별로 사용 스킬·A가 준 피해 합계·A의 자원 변화·새로 걸린 상태·타격 수를 모은다.</summary>
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
                    current.Count(x => x.Type == "DamageDealt" && x.Actor == "A")));
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
fire_mage,화염술사,마법사,날뛰는 불꽃을 지배해 적을 불사르는 클래스. 케인이 향하는 곳마다 순식간에 번져나가는 불길은 일순간 맹렬하게 폭발해 주변의 모든 적을 집어삼킨다. 과격한 화염 마법의 흐름이 방해받지 않도록 간결한 천옷을 선호한다.,rapid_fire,fire_storm,flame_cannon,flashover,explosion,inferno,burning_soul,blaze,combat_mastery_finesse,spark,overheat,ignite
"""",
        ["스킬"] = """"
ID,이름,스킬구분,부모스킬ID,설명,태그,기타
rapid_fire,레피드 파이어,일반,,"빛발치는 작은 화염구를 연달아 쏘아내는 화염 마법. 최대 5개의 화염구를 무작위로 발사해 다수의 적에게 피해를 주고 고유 지속 피해: 작열을 부여한다. 다른 스킬로 폭발을 일으키면 재사용 대기 시간이 초기화되며, 1회에 한해 5개의 화염구를 더욱 빠르게 발사한다.","연타, 원소",등급: 에픽; 강화 레벨: +24; 화염구 대미지: 6072; 열기 생성: 10; 치명타 시 열기 생성: 1; 화염구 발사 수: 3~5; 캐스팅 시간: 1.6초; 재사용 대기 시간: 8초; 사거리: 10m
fire_storm,파이어 스톰,일반,,"맹렬한 불의 소용돌이를 일으키는 화염 마법. 불의 소용돌이는 일정 시간 동안 주변 적을 끌어당겨 높이 띄우고 피해를 준다. 추가로, 휩쓸린 적에게 고유 지속 피해: 작열을 부여한다. 불의 소용돌이가 사그라들면 큰 폭발과 함께 주변에 다수의 불길을 생성한다.","연타, 원소, 방해",등급: 고급; 강화 레벨: +8; 대미지: 2479 × 10; 폭발 대미지: 15940; 브레이크 대미지: 1칸; 열기 생성: 10; 치명타 시 열기 생성: 5; 지속 시간: 5초; 끌어당기는 범위: 4m; 재사용 대기 시간: 20초; 사거리: 10m; 범위: 2m
flame_cannon,플레임 캐논,일반,,"이글거리는 거대 화염구로 전방을 불사르는 화염 마법. 화염구는 적에게 고유 지속 피해: 작열과 약화 효과: 낙인을 남겨 지속 피해를 주고, 화염술사의 공격에 더 큰 피해를 받게 만든다. 버닝 소울 단계에 따라 버스트 캐논으로 강화된다.","원소, 방해",등급: 에픽; 강화 레벨: +24; 대미지: 31375; 열기 생성: 15; 치명타 시 열기 생성: 5; 밀어내는 거리: 6m; 낙인 지속 시간: 8초; 낙인 받는 대미지 증가: 20%; 최대 스택 수: 2; 캐스팅 시간: 2초; 재사용 대기 시간: 20초; 사거리: 10m; 범위: 10m
burst_cannon,버스트 캐논,파생,flame_cannon,"폭발하는 거대 화염구를 날려 전방을 불사르는 화염 마법. 화염구는 적에게 고유 지속 피해: 작열과 약화 효과: 낙인을 남겨 지속 피해를 주고, 화염술사의 공격에 더 큰 피해를 받게 만든다. 화염구는 사라질 때 폭발하여 주변의 적에게 추가 피해를 준다.","원소, 방해",등급: 에픽; 강화 레벨: +24; 대미지: 31375; 폭발 대미지: 15181; 열기 생성: 15; 치명타 시 열기 생성: 5; 밀어내는 거리: 6m; 낙인 지속 시간: 8초; 낙인 받는 대미지 증가: 20%; 최대 스택 수: 2; 캐스팅 시간: 1.5초; 재사용 대기 시간: 20초; 사거리: 10m; 범위: 10m
flashover,플래시오버,일반,,"불타는 마력 고리로 자신을 에워싸는 화염 마법. 일정 시간 동안 체력을 회복하고 열기를 생성하며, 광범위한 화염 파동으로 주변 적들에게 피해를 주고 고유 지속 피해: 작열을 남긴다.","연타, 원소",등급: 에픽; 강화 레벨: +24; 대미지: 2초마다 3694; 지속 시간: 10초; 열기 생성: 2초마다 10; 체력 회복: 2초마다 2%; 재사용 대기 시간: 40초; 범위: 10m
explosion,익스플로전,일반,,주변에 흩뿌린 불꽃을 일거에 터뜨리는 화염 마법의 정수. 열기를 모두 소모해 고유 지속 피해: 작열이 부여된 적과 불길을 폭발시켜 광역 피해를 준다. 소모한 열기의 양에 비례해 폭발의 위력이 증가한다.,"강타, 연타, 원소",등급: 고급; 강화 레벨: +8; 작열 폭발 대미지: 8501; 불길 폭발 대미지: 4250; 폭발 대미지 증가: 열기 1당 2%; 재사용 대기 시간: 2초; 최소 열기 소모량: 50; 범위: 4m
inferno,인페르노,궁극기,,"초고열 광선을 집중 방출하는 화염 마법의 비술. 정신을 집중할수록 강해지는 화염 광선으로 전방의 적들을 불사르고, 지속적으로 열기를 생성한다. 정신 집중 시간에 비례하여 일정 시간 동안 추가로 열기를 생성한다. 스킬 사용 완료 후, 폭발과 함께 전방을 불길로 뒤덮고, 3단계 버닝 소울을 얻는다. 『앞을 가로막는 무엇이든 묵묵히 불사를 뿐』","궁극기, 연타, 원소, 보조",등급: 고급; 강화 레벨: +8; 엠블럼 장착 필요; 1단계 광선 대미지: 9387 × 6; 2단계 광선 대미지: 21254 × 10; 채널링 시 열기 생성: 6 × 16; 폭발 대미지: 14169~28338; 채널링 시간: 5초; 버닝 소울 지속 시간: 25초; 열기 생성 효과 지속 시간: 공격 주기마다 2초; 열기 생성량: 1초마다 3; 궁극기 비용: 300; 사거리: 10m; 범위: 15m
"""",
        ["패시브스킬"] = """"
ID,이름,설명,기타
burning_soul,버닝 소울,"열기에서 무한히 피어오르는 불꽃에 감응하는 원소 친화력. 스킬 사용 시 열기를 생성한다. 스킬이 치명타로 적중할 경우 더 많이 생성한다. 열기 소모 시, 화염의 기운을 비축하여 더욱 강한 화력을 얻는다.","열기 최대치: 100; 1단계 최종 대미지 증가: 3%; 2단계 최종 대미지 증가: 6%; 3단계 최종 대미지 증가: 15%; 1, 2단계 지속 시간: 60초; 3단계 지속 시간: 25초"
blaze,블레이즈,"끊임없이 타오르는 불꽃을 능숙히 다루는 화염 마법의 경지. 일부 스킬 공격 시 적에게 고유 지속 피해: 작열을 주고, 불길로 주변 적을 태우는 동시에 열기를 생성한다. 주변에 불타는 적이 없다면 스킬 공격 시 불줄기를 뿜어낸다.","태그: 연타, 원소; 작열 총합 대미지: 8434; 작열 지속 시간: 10초; 불길 대미지: 2초마다 1124; 불길 지속 시간: 8초; 불길 공격 시 열기 생성: 2; 불줄기 생성 재사용 대기 시간: 20초"
combat_mastery_finesse,전투 숙련: 기교,원거리에서 공격을 수행하는 숙련된 전투 기법. 적에게 주는 멀티히트 피해가 증가한다. 화염술사 클래스 레벨이 30에 도달하면 전투 시 클래스 특화 어시스트를 사용할 수 있다.,멀티히트 대미지 증가: 5%; 클래스 특화 어시스트 해금: 화염술사 클래스 레벨 30
spark,스파크,"작은 불씨에 담긴 열기마저 통제하는 원소 제어 능력. 기본 공격 시 열기를 생성한다. 전투 시작 시, 대량의 열기를 생성한다.",기본 공격 시 열기 생성: 3; 전투 시작 시 열기 생성: 50
overheat,오버히트,원소 친화력을 대폭 끌어올려 빠르게 화염을 쏟아내는 전투 방식. 버닝 소울이 활성화된 동안 모든 스킬의 재사용 대기 시간이 감소한다.,1단계 재사용 대기 시간 감소: 5%; 2단계 재사용 대기 시간 감소: 10%; 3단계 재사용 대기 시간 감소: 20%
ignite,이그나이트,"순식간에 강렬한 불꽃을 피워내는 기술. 치명타 피해가 증가하며, 적에게 가하는 첫 번째 스킬 공격은 추가 피해를 주고 강화 효과: 점화를 얻는다. 익스플로전을 사용하면 점화 효과 중첩을 모두 소모하며, 중첩당 10의 열기를 추가 소모한 것으로 간주하여 익스플로전의 피해가 증가하고 버닝 소울 중첩을 획득한다.","태그: 원소, 방해; 해금 조건: 화염술사 Lv.60 이상; 치명타 대미지 증가: 10%; 대미지: 6325; 최대 중첩 수: 5; 중첩당 버닝 소울 증가: 10; 지속 시간: 30초"
"""",
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고,궁극기문구
rapid_fire,TRUE,공격,상대,2,0,65,궁극기 게이지,,150,FALSE,작은 화염구를 3~5개 연달아 쏘아 피해와 작열을 주고 열기를 만든다. 다른 스킬로 폭발을 일으키면 재사용 대기가 초기화되고 다음 사용은 화염구 5개를 쏜다.,{caster}가 작은 화염구를 연달아 쏘아냅니다!,"원본 재사용 대기 8초→2턴, 화염구 대미지 6,072×3~5(3개 + 2개를 각 50%, 평균 4개), 열기 10(치명타 시 화염구마다 +1). 피해·작열은 원본 × 0.55. 폭발 뒤 초기화는 fire_rapid_ready 표식과 fire_rapid_haste(쿨다운감소, 전투시작 영구 상태)로 반영",
fire_storm,TRUE,공격·방해,상대,4,0,60,궁극기 게이지,,150,FALSE,"불의 소용돌이로 연속 피해와 브레이크 피해·작열을 주고, 큰 폭발과 함께 불길을 만든다.",{caster}가 맹렬한 불의 소용돌이를 일으킵니다!,"원본 재사용 대기 20초→4턴, 대미지 2,479×10, 폭발 15,940, 브레이크 1칸, 열기 10(치명타 시 타격마다 +5). 원본 × 0.55. 끌어당겨 띄우기는 브레이크 1칸에 포함. 사그라들며 만드는 불길은 불길 지속 피해(2턴)로, 큰 폭발은 레피드 파이어 초기화로 반영",
flame_cannon,TRUE,공격·방해,상대,4,0,60,궁극기 게이지,,150,FALSE,거대 화염구로 피해를 주고 작열과 낙인(받는 피해 +20%)을 남긴다. 버닝 소울 3단계에서는 버스트 캐논으로 강화된다.,{caster}가 이글거리는 거대 화염구를 발사합니다!,"원본 재사용 대기 20초→4턴, 대미지 31,375, 열기 15(치명타 +5), 낙인 받는 대미지 +20% 8초→2턴. 원본 × 0.55. 밀어내기 생략, 최대 스택 2는 충전 개념이 없어 제외. 버닝 소울 3단계(fire_soul_max)이면 즉시 대체 파생으로 버스트 캐논이 나가며 이 행의 효과는 실행하지 않는다(조건 fire_soul_max<=0). 버닝 소울 3단계일 때 바뀐다(2026-09-27 사용자 확인)",
burst_cannon,TRUE,공격·방해,상대,0,0,0,,,0,FALSE,"폭발하는 거대 화염구로 피해·작열·낙인을 주고, 사라지며 폭발해 추가 피해를 준다.",{caster}가 폭발하는 거대 화염구를 날립니다!,"파생 전용(플레임 캐논 대체, 버닝 소울 3단계). 대미지 31,375, 폭발 15,181. 원본 × 0.55. 폭발로 레피드 파이어 초기화. 궁극기 게이지는 부모 플레임 캐논이 얻는다",
flashover,TRUE,공격·생존,자신·상대,7,0,60,궁극기 게이지,,150,FALSE,"불타는 마력 고리로 2턴 동안 체력을 회복하고 열기를 만들며, 화염 파동으로 피해와 작열을 준다.",{caster}가 불타는 마력 고리로 자신을 에워쌉니다!,"원본 재사용 대기 40초→7턴, 지속 10초→2턴. 2초마다 대미지 3,694·열기 10·체력 2% → 사용 시 1회 + 2턴 동안 턴당 2회분(피해 원본 × 0.55, 열기 10+20+20). 회복은 최대 체력 10%를 기본 HP 20,000 기준 턴당 1,000으로 환산",
explosion,TRUE,공격,상대,1,0,65,fire_heat,50,,FALSE,열기를 모두 소모해 작열이 걸린 적과 불길을 폭발시킨다. 소모한 열기 1당 폭발 위력이 2% 오른다.,{caster}가 흩뿌린 불꽃을 일거에 터뜨립니다!,"원본 재사용 대기 2초→1턴, 최소 열기 50, 작열 폭발 8,501, 불길 폭발 4,250, 열기 1당 +2%. 비용 50을 먼저 내므로 피해 고정값에 50의 ×2를 넣고 남은 열기 1당 +1%(조건부피해증가 수치참조)를 곱해 1+0.02×열기와 같게 했다. 원본 × 0.55. 열기를 비용으로 쓰므로 궁극기 게이지는 효과 행으로 적립",
inferno,TRUE,궁극기·공격,자신·상대,0,0,100,궁극기 게이지,300,,TRUE,"초고열 광선을 집중 방출해 연속 피해를 주고 열기를 만든 뒤, 폭발과 함께 불길을 남기고 버닝 소울 3단계를 얻는다.",{caster}가 정신을 집중해 초고열 광선을 방출합니다!,"원본 1단계 광선 9,387×6, 2단계 광선 21,254×10, 폭발 14,169~28,338(평균 21,253), 채널링 열기 6×16. 합계가 다른 궁극기의 9배 이상이라 원본 × 0.15로 낮춤(헬 파이어 31,500과 비슷). 채널링 5초를 끝까지 집중한 것으로 보고 두 단계 광선을 모두 준다. 추가 열기 1초마다 3×32초는 6턴 동안 턴당 18. 엠블럼 장착 필요는 사용 조건이 아니라 제외",『앞을 가로막는 무엇이든 묵묵히 불사를 뿐』
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
rapid_fire_01,rapid_fire,1,피해,상대,,3340,3,1,0,,0,화염구가 연달아 {target}에게 총 {damage}의 피해!,"화염구 6,072 × 기본 3개, 원본 × 0.55",,,,,,,,
rapid_fire_02,rapid_fire,2,피해,상대,,3340,2,0.5,0,,0,추가 화염구가 {target}에게 총 {damage}의 피해!,화염구 3~5개: 4·5번째를 각 50%,자신,자원보유,fire_rapid_ready,<=,0,,,
rapid_fire_03,rapid_fire,3,피해,상대,,3340,2,1,0,,0,빨라진 화염구가 {target}에게 총 {damage}의 피해!,폭발로 초기화된 뒤 1회는 5개 모두 발사,자신,자원보유,fire_rapid_ready,>=,1,,,
rapid_fire_04,rapid_fire,4,지속피해,상대,,2319,1,1,2,fire_burn,1,작열,"블레이즈 작열 총합 8,434/10초 → 2턴에 4,217씩, 원본 × 0.55",,,,,,,,
rapid_fire_05,rapid_fire,5,자원증가,자신,,10,1,1,0,fire_heat,0,,열기 10,,,,,,,,
rapid_fire_06,rapid_fire,6,자원소모,자신,,0,1,1,0,fire_rapid_ready,0,,초기화 1회 사용,자신,자원보유,fire_rapid_ready,>=,1,,전부,
fire_storm_01,fire_storm,1,피해,상대,,1363,10,1,0,,0,불의 소용돌이가 {target}을 휘감아 총 {damage}의 피해!,"대미지 2,479×10, 원본 × 0.55",,,,,,,,
fire_storm_02,fire_storm,2,피해,상대,,8767,1,1,0,,0,소용돌이가 사그라들며 폭발해 {target}에게 {damage}의 피해!,"폭발 대미지 15,940, 원본 × 0.55",,,,,,,,
fire_storm_03,fire_storm,3,브레이크피해,상대,,1,1,1,0,,0,{target}이 불길에 휩쓸려 휘청입니다!,브레이크 대미지 1칸(끌어당겨 띄우기 포함),,,,,,,,
fire_storm_04,fire_storm,4,지속피해,상대,,2319,1,1,2,fire_burn,1,작열,"블레이즈 작열 총합 8,434/10초 → 2턴에 4,217씩, 원본 × 0.55",,,,,,,,
fire_storm_05,fire_storm,5,지속피해,상대,,1236,1,1,2,fire_flame,1,불길,"사그라들며 만드는 불길: 2초마다 1,124×8초 → 2턴에 2,248씩, 원본 × 0.55",,,,,,,,
fire_storm_06,fire_storm,6,턴당자원증가,자신,,0,1,1,2,fire_flame_heat,1,,불길 공격 시 열기 2 → 턴당 4,,,,,,,,
fire_storm_07,fire_storm,7,자원증가,자신,,10,1,1,0,fire_heat,0,,열기 10,,,,,,,,
fire_storm_08,fire_storm,8,자원설정,자신,,1,1,1,0,fire_rapid_ready,0,,큰 폭발 → 레피드 파이어 초기화,,,,,,,,
flame_cannon_01,flame_cannon,1,피해,상대,,17256,1,1,0,,0,거대 화염구가 {target}을 불살라 {damage}의 피해!,"대미지 31,375, 원본 × 0.55",자신,자원보유,fire_soul_max,<=,0,,,
flame_cannon_02,flame_cannon,2,지속피해,상대,,2319,1,1,2,fire_burn,1,작열,"블레이즈 작열 총합 8,434/10초 → 2턴에 4,217씩, 원본 × 0.55",자신,자원보유,fire_soul_max,<=,0,,,
flame_cannon_03,flame_cannon,3,상태효과,상대,,0,1,1,2,fire_brand,1,{target}에게 낙인이 새겨집니다.,"낙인 받는 대미지 +20%, 8초→2턴",자신,자원보유,fire_soul_max,<=,0,,,
flame_cannon_04,flame_cannon,4,자원증가,자신,,15,1,1,0,fire_heat,0,,열기 15,자신,자원보유,fire_soul_max,<=,0,,,
burst_cannon_01,burst_cannon,1,피해,상대,,17256,1,1,0,,0,거대 화염구가 {target}을 불살라 {damage}의 피해!,"대미지 31,375, 원본 × 0.55",,,,,,,,
burst_cannon_02,burst_cannon,2,피해,상대,,8350,1,1,0,,0,화염구가 폭발하며 {target}에게 {damage}의 피해!,"폭발 대미지 15,181, 원본 × 0.55",,,,,,,,
burst_cannon_03,burst_cannon,3,지속피해,상대,,2319,1,1,2,fire_burn,1,작열,"블레이즈 작열 총합 8,434/10초 → 2턴에 4,217씩, 원본 × 0.55",,,,,,,,
burst_cannon_04,burst_cannon,4,상태효과,상대,,0,1,1,2,fire_brand,1,{target}에게 낙인이 새겨집니다.,"낙인 받는 대미지 +20%, 8초→2턴",,,,,,,,
burst_cannon_05,burst_cannon,5,자원증가,자신,,15,1,1,0,fire_heat,0,,열기 15,,,,,,,,
burst_cannon_06,burst_cannon,6,자원설정,자신,,1,1,1,0,fire_rapid_ready,0,,폭발 → 레피드 파이어 초기화,,,,,,,,
flashover_01,flashover,1,피해,상대,,2032,1,1,0,,0,화염 파동이 {target}에게 {damage}의 피해!,"2초마다 3,694 중 사용 시 1회, 원본 × 0.55",,,,,,,,
flashover_02,flashover,2,지속피해,상대,,4063,1,1,2,fire_flashover,1,화염 파동,"나머지 4회를 2턴에 2회분씩, 원본 × 0.55",,,,,,,,
flashover_03,flashover,3,지속피해,상대,,2319,1,1,2,fire_burn,1,작열,"블레이즈 작열 총합 8,434/10초 → 2턴에 4,217씩, 원본 × 0.55",,,,,,,,
flashover_04,flashover,4,지속회복,자신,,4000,1,1,2,fire_flashover_regen,1,불타는 마력 고리,"2초마다 체력 2%×10초 = 최대 체력 10%. 기본 HP 20,000 기준 턴당 1,000(고정값 4,000 × 0.25)",,,,,,,,
flashover_05,flashover,5,턴당자원증가,자신,,0,1,1,2,fire_flashover_heat,1,,2초마다 열기 10 → 2턴 동안 턴당 20,,,,,,,,
flashover_06,flashover,6,자원증가,자신,,10,1,1,0,fire_heat,0,,사용 시 열기 10,,,,,,,,
explosion_01,explosion,1,조건부피해증가,상대,,1,1,1,0,,0,,비용 50을 낸 뒤 남은 열기 1당 +1%(피해 고정값에 50의 ×2를 넣었으므로 합계 1+0.02×열기),,,,,,fire_heat,소모중첩배율,
explosion_02,explosion,2,조건부피해증가,상대,,10,1,1,0,,0,,이그나이트: 점화 중첩당 열기 10을 더 소모한 것으로 간주(+10%),,,,,,fire_ignite,소모중첩배율,
explosion_03,explosion,3,피해,상대,,9351,1,1,0,,0,작열이 한꺼번에 폭발해 {target}에게 {damage}의 피해!,"작열 폭발 8,501 × 2(최소 열기 50의 +100%), 원본 × 0.55",상대,상태효과보유,fire_burn,,,,,
explosion_04,explosion,4,피해,상대,,4675,1,1,0,,0,불길이 폭발해 {target}에게 {damage}의 피해!,"불길 폭발 4,250 × 2, 원본 × 0.55",상대,상태효과보유,fire_flame,,,,,
explosion_05,explosion,5,상태해제,상대,,0,1,1,0,fire_flame,0,,폭발한 불길은 사라진다,,,,,,,,
explosion_06,explosion,6,상태해제,자신,,0,1,1,0,fire_flame_heat,0,,불길이 사라져 불길 열기도 끝난다,,,,,,,,
explosion_07,explosion,7,자원증가,자신,,50,1,1,0,fire_soul_gauge,0,,버닝 소울: 비용으로 낸 열기 50,,,,,,,,
explosion_08,explosion,8,자원증가,자신,,1,1,1,0,fire_soul_gauge,0,,버닝 소울: 남은 열기,,,,,,fire_heat,소모중첩배율,
explosion_09,explosion,9,자원증가,자신,,10,1,1,0,fire_soul_gauge,0,,이그나이트: 점화 중첩당 버닝 소울 10,,,,,,fire_ignite,소모중첩배율,
explosion_10,explosion,10,자원소모,자신,,0,1,1,0,fire_heat,0,,열기를 모두 소모,,,,,,,전부,
explosion_11,explosion,11,자원소모,자신,,0,1,1,0,fire_ignite,0,,점화 중첩을 모두 소모,,,,,,,전부,
explosion_12,explosion,12,자원설정,자신,,1,1,1,0,fire_rapid_ready,0,,폭발 → 레피드 파이어 초기화,,,,,,,,
explosion_13,explosion,13,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,열기를 비용으로 쓰는 스킬이라 궁극기 게이지는 효과 행으로 적립,,,,,,,,
inferno_01,inferno,1,피해,상대,,1408,6,1,0,,0,화염 광선이 {target}을 불살라 총 {damage}의 피해!,"1단계 광선 9,387×6, 원본 × 0.15",,,,,,,,
inferno_02,inferno,2,피해,상대,,3188,10,1,0,,0,한층 뜨거워진 광선이 {target}에게 총 {damage}의 피해!,"2단계 광선 21,254×10, 원본 × 0.15",,,,,,,,
inferno_03,inferno,3,피해,상대,,3188,1,1,0,,0,폭발과 함께 전방이 불길로 뒤덮여 {target}에게 {damage}의 피해!,"폭발 14,169~28,338 평균, 원본 × 0.15",,,,,,,,
inferno_04,inferno,4,자원증가,자신,,96,1,1,0,fire_heat,0,,채널링 열기 6×16,,,,,,,,
inferno_05,inferno,5,턴당자원증가,자신,,0,1,1,6,fire_inferno_heat,1,,추가 열기 1초마다 3×32초 → 6턴 동안 턴당 18,,,,,,,,
inferno_06,inferno,6,지속피해,상대,,1236,1,1,2,fire_flame,1,불길,"전방을 뒤덮은 불길(블레이즈 불길 수치), 원본 × 0.55",,,,,,,,
inferno_07,inferno,7,턴당자원증가,자신,,0,1,1,2,fire_flame_heat,1,,불길 공격 시 열기,,,,,,,,
inferno_08,inferno,8,자원설정,자신,,2,1,1,0,fire_soul,0,,3단계 버닝 소울(1·2단계 포함),,,,,,,,
inferno_09,inferno,9,자원설정,자신,,1,1,1,0,fire_soul_max,0,,3단계 버닝 소울 25초→5턴,,,,,,,,
inferno_10,inferno,10,자원설정,자신,,1,1,1,0,fire_rapid_ready,0,,폭발 → 레피드 파이어 초기화,,,,,,,,
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
flame_cannon_burst,flame_cannon,burst_cannon,대체,0,1,자원보유,fire_soul_max>=1,FALSE,즉시,버닝 소울 3단계이면 플레임 캐논이 버스트 캐논으로 강화된다. 플레임 캐논 효과 행은 fire_soul_max<=0 조건이라 실행되지 않는다,100
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
burning_soul,TRUE,"스킬 치명타 시 열기 추가 생성. 익스플로전으로 소모한 열기 100마다 버닝 소울 단계 상승(1·2단계 최종 피해 +3%/+6% 10턴, 3단계 +15% 5턴). 레피드 파이어 초기화 영구 상태도 여기서 건다."
blaze,TRUE,작열은 각 스킬 행에 넣었다. 불길이 없을 때 스킬이 적중하면 불줄기로 불길을 만든다(재사용 20초→4턴).
combat_mastery_finesse,TRUE,멀티히트 대미지 +5%.
spark,TRUE,"기본 공격 시 열기 3, 전투 시작 시 열기 50."
overheat,TRUE,버닝 소울 단계별 재사용 대기 감소 5/10/20%.
ignite,TRUE,치명타 대미지 +10%. 첫 스킬 공격(1:1이라 전투당 1회) 추가 피해와 점화 1중첩. 익스플로전이 점화를 모두 소모.
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
bsoul_01,burning_soul,1,주는피해증가,자신,0,1,1,0,fire_soul_power,0,,,,,,,,,전투시작,,,"중첩자원ID=fire_soul, 단계당 +3%",
bsoul_02,burning_soul,2,주는피해증가,자신,0,1,1,0,fire_soul_power_max,0,,,,,,,,,전투시작,,,"중첩자원ID=fire_soul_max, 3단계 +9%(합계 15%)",
bsoul_03,burning_soul,3,쿨다운감소,자신,0,1,1,0,fire_rapid_haste,0,,,,,,,,,전투시작,,,"레피드 파이어 초기화(스킬 공통 규칙, 중첩자원ID=fire_rapid_ready)",
bsoul_04,burning_soul,4,자원증가,자신,1,1,1,0,fire_heat,0,,,,,,,,,치명타적중시,rapid_fire,,치명타 시 열기 생성 1(타격마다),
bsoul_05,burning_soul,5,자원증가,자신,5,1,1,0,fire_heat,0,,,,,,,,,치명타적중시,fire_storm,,치명타 시 열기 생성 5(타격마다),
bsoul_06,burning_soul,6,자원증가,자신,5,1,1,0,fire_heat,0,,,,,,,,,치명타적중시,flame_cannon,,치명타 시 열기 생성 5(타격마다),
bsoul_07,burning_soul,7,자원증가,자신,5,1,1,0,fire_heat,0,,,,,,,,,치명타적중시,burst_cannon,,치명타 시 열기 생성 5(타격마다),
bsoul_08,burning_soul,8,자원설정,자신,1,1,1,0,fire_soul_max,0,버닝 소울이 극한까지 타오릅니다!,자신,자원보유,fire_soul,>=,2,,,자원최대치도달시,,fire_soul_gauge,2단계에서 다시 오르면 3단계,
bsoul_09,burning_soul,9,자원증가,자신,1,1,1,0,fire_soul,0,,,,,,,,,자원최대치도달시,,fire_soul_gauge,"열기 100 소모마다 한 단계(최대 2, 이미 2면 지속턴 갱신)",
bsoul_10,burning_soul,10,자원소모,자신,0,1,1,0,fire_soul_gauge,0,,,,,,,,전부,자원최대치도달시,,fire_soul_gauge,비축 초기화,
blz_01,blaze,1,턴당자원증가,자신,0,1,1,2,fire_flame_heat,0,,상대,상태효과미보유,fire_flame,,,,,스킬적중완료시,,,불줄기가 만든 불길의 열기(불길 행보다 먼저: 조건이 같아야 함),4
blz_02,blaze,2,지속피해,상대,1236,1,1,2,fire_flame,0,불줄기,상대,상태효과미보유,fire_flame,,,,,스킬적중완료시,,,주변에 불타는 적이 없을 때 불줄기 → 불길이 없을 때 스킬 적중 시 불길로 근사. 재사용 20초→4턴. 원본 × 0.55,4
cmf_01,combat_mastery_finesse,1,멀티히트피해증가,자신,0,1,1,0,fire_mastery_multi,0,전투 숙련: 기교로 다단 공격 피해가 증가합니다.,,,,,,,,전투시작,,,,
spk_01,spark,1,자원증가,자신,50,1,1,0,fire_heat,0,작은 불씨가 번져 열기가 차오릅니다.,,,,,,,,전투시작,,,전투 시작 시 열기 50,
spk_02,spark,2,자원증가,자신,3,1,1,0,fire_heat,0,,,,,,,,,기본공격적중시,,,기본 공격 시 열기 3,
ovh_01,overheat,1,쿨다운감소,자신,0,1,1,0,fire_overheat,0,,,,,,,,,전투시작,,,중첩자원ID=fire_soul,
ovh_02,overheat,2,쿨다운감소,자신,0,1,1,0,fire_overheat_max,0,,,,,,,,,전투시작,,,중첩자원ID=fire_soul_max,
ign_01,ignite,1,치명타피해증가,자신,0,1,1,0,fire_ignite_crit,0,,,,,,,,,전투시작,,,치명타 대미지 +10%,
ign_02,ignite,2,피해,상대,3479,1,1,0,,0,순식간에 피어난 불꽃이 {target}에게 {damage}의 피해!,자신,자원보유,fire_ignite_used,<=,0,,,스킬적중완료시,,,"첫 스킬 공격 추가 대미지 6,325, 원본 × 0.55. 1:1이라 전투당 1회",
ign_03,ignite,3,자원증가,자신,1,1,1,0,fire_ignite,0,,자신,자원보유,fire_ignite_used,<=,0,,,스킬적중완료시,,,점화 1중첩(30초→5턴),
ign_04,ignite,4,자원설정,자신,1,1,1,0,fire_ignite_used,0,,자신,자원보유,fire_ignite_used,<=,0,,,스킬적중완료시,,,전투당 1회 표식,
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
fire_heat,열기,자원,100,0,0,가산,TRUE,화염술사 열기(최대 100). 스킬 사용으로 생성하고 치명타면 더 생성한다. 익스플로전이 50 이상을 모두 소모한다. 스파크로 전투 시작 시 50.,
fire_soul,버닝 소울,자원,2,0,10,교체,TRUE,"버닝 소울 1·2단계(최종 피해 +3%/+6%, 60초→10턴). 열기를 100 소모할 때마다 한 단계 오르고 다시 오르면 지속턴을 새로 채운다. 3단계는 fire_soul_max.",
fire_soul_max,버닝 소울 3단계,자원,1,0,5,교체,TRUE,"버닝 소울 3단계(최종 피해 +15%, 25초→5턴). 2단계에서 열기 100을 더 소모하거나 인페르노로 얻는다. 보유 중에는 플레임 캐논이 버스트 캐논으로 강화된다.",
fire_soul_gauge,버닝 소울 비축,자원,100,0,0,가산,TRUE,익스플로전으로 소모한 열기(점화 중첩당 10 포함)를 모아 100이 되면 버닝 소울 단계를 올리고 초기화한다.,TRUE
fire_rapid_ready,레피드 파이어 초기화,자원,1,0,0,교체,TRUE,다른 스킬로 폭발을 일으키면(익스플로전·파이어 스톰·버스트 캐논·인페르노) 얻는다. 보유 중에는 레피드 파이어 재사용 대기가 초기화되고 다음 레피드 파이어가 화염구 5개를 발사한 뒤 소모한다.,
fire_ignite,점화,중첩,5,0,5,가산,TRUE,"이그나이트 점화 중첩(최대 5, 30초→5턴). 익스플로전이 모두 소모하며 중첩당 열기 10을 더 소모한 것으로 본다.",
fire_ignite_used,이그나이트 사용,자원,1,0,0,교체,TRUE,"1:1에서 ""적에게 가하는 첫 번째 스킬 공격""은 전투당 한 번이므로 사용 여부를 남기는 표식.",TRUE
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
break_broken,브레이크,브레이크|받는피해증가,0.2,브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외),,,,,,,,
fire_burn,작열,없음,0,"화염술사 고유 지속 피해: 작열(블레이즈 총합 8,434/10초 → 2턴). 익스플로전이 작열이 있는 적을 폭발시킨다.",,,,,,,,
fire_flame,불길,없음,0,"파이어 스톰·인페르노·블레이즈 불줄기가 만드는 불길(2초마다 1,124×8초 → 2턴). 익스플로전이 폭발시키고 없앤다.",,,,,,,,
fire_flame_heat,불길: 열기,턴당자원증가,4,불길이 적을 태울 때마다 열기 2(2초마다) → 턴당 4. 불길과 같은 2턴.,,,,,,fire_heat,,TRUE
fire_brand,낙인,받는피해증가,0.2,플레임 캐논·버스트 캐논의 약화 효과: 화염술사의 공격에 받는 대미지 +20%(8초→2턴). 최대 스택 2는 충전 개념이 없어 제외.,,,,,,,,
fire_flashover,플래시오버,없음,0,"플래시오버 화염 파동(2초마다 3,694×10초). 사용 시 1회 + 2턴 동안 턴당 2회분.",,,,,,,,
fire_flashover_regen,플래시오버: 회복,없음,0,"플래시오버 체력 회복(2초마다 최대 체력 2%×10초 = 10%). 기본 HP 20,000 기준 2턴 동안 턴당 1,000.",,,,,,,,
fire_flashover_heat,플래시오버: 열기,턴당자원증가,20,플래시오버 열기 생성(2초마다 10 → 사용 시 10 + 2턴 동안 턴당 20).,,,,,,fire_heat,,TRUE
fire_inferno_heat,인페르노: 열기,턴당자원증가,18,"인페르노 채널링 뒤 추가 열기(1초마다 3, 공격 주기 16회×2초 = 32초 → 6턴 동안 턴당 18).",,,,,,fire_heat,,
fire_soul_power,버닝 소울,주는피해증가,0.03,"버닝 소울 단계당 최종 대미지 +3%(1단계 3%, 2단계 6%).",,fire_soul,,,,,,TRUE
fire_soul_power_max,버닝 소울 3단계,주는피해증가,0.09,버닝 소울 3단계 최종 대미지 15% = 2단계 6% + 9%.,,fire_soul_max,,,,,,TRUE
fire_overheat,오버히트,쿨다운감소,0.34,"버닝 소울 단계당 재사용 대기 감소(1단계 5%, 2단계 10%). 석궁사수 퀵 어택과 같은 환산(5% → 0.34, 반올림해 2단계부터 매 행동 1회 추가 감소).",,fire_soul,,,,,,TRUE
fire_overheat_max,오버히트 3단계,쿨다운감소,0.68,버닝 소울 3단계 재사용 대기 감소 20% = 2단계 0.68 + 0.68.,,fire_soul_max,,,,,,TRUE
fire_rapid_haste,레피드 파이어 초기화,쿨다운감소,9,"레피드 파이어 초기화 표식을 가진 동안 레피드 파이어 재사용 대기를 즉시 초기화(큰 값으로 근사, 댄서 화합 가속과 같은 방식).",rapid_fire,fire_rapid_ready,,,,,,TRUE
fire_mastery_multi,전투 숙련: 기교,멀티히트피해증가,0.05,적에게 주는 멀티히트(다단) 피해 +5%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,,,
fire_ignite_crit,이그나이트,치명타피해증가,0.1,치명타 대미지 +10%.,,,,,,,,
"""",
    };
}
