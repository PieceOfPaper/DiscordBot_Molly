using Molly.Battle;

/// <summary>
/// 힐러의 라이프 링크·서먼 스프라이트·팬텀 페인(멘탈 브레이크)·나이트메어·오든 실드·감속·쇠약 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-09-26 배틀 시트에서 힐러 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 힐러 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·돌발 이벤트·생활스킬 시트는 석궁사수 사본을 그대로 쓴다.
/// </summary>
internal static class HealerBattleTests
{
    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow);
        LinkAndSpriteTests(data);
        OathShieldTests(data);
        ControlTests(data);
    }

    private static void ControlTests(BattleDataSnapshot data)
    {
        // 공포(멘탈 브레이크)는 따로 거는 상태가 아니라 팬텀 페인의 브레이크 대미지 1칸이다. 팬텀 페인 세 번이면 브레이크가 걸려 상대가 행동을 잃는다.
        var terror = Duel(data, ["phantom_pain"], maxActions: 20).Events;
        Assert(!terror.Any(x => x.Type == "StatusApplied" && x.Actor == "B" && x.Detail == "공포") && terror.Any(x => x.Type == "BreakGaugeChanged" && x.Target == "B" && x.Amount == 1)
            && terror.Any(x => x.Type == "BreakActivated" && x.Target == "B") && terror.Any(x => x.Type == "BreakActionLost" && x.Target == "B"),
            "팬텀 페인의 공포는 브레이크 대미지 1칸으로 처리되어 게이지가 차면 상대가 브레이크로 행동을 잃는다");
        Assert(terror.Any(x => x.Type == "StatusApplied" && x.Actor == "A" && x.Detail == "나이트메어 준비"), "팬텀 페인이 자신에게 거는 표식은 나이트메어 준비로 표시된다");

        var slowed = Duel(data, ["pain_of_life"], maxActions: 2).Events;
        Assert(slowed.Any(x => x.Type == "StatusApplied" && x.Actor == "B" && x.Detail == "감속" && x.Amount == 1), "생명의 고통은 상대에게 감속 1턴을 남긴다");

        // 라이프 링크 → 프로텍션(스프라이트 발산을 막아 연결 유지): 연결된 적에게만 쇠약을 남기고, 쇠약 지속 피해는 상대 체력이 가득할수록 크다(×(1+HP 비율)).
        var weakness = Duel(data, ["protection", "life_link"], maxActions: 5, sprite: false).Events;
        var weaknessTick = weakness.First(x => x.Type == "StatusDamage" && x.Target == "B" && x.Detail == "쇠약").Amount ?? 0;
        var unlinked = Duel(data, ["protection"], maxActions: 2).Events;
        Assert(weakness.Any(x => x.Type == "StatusApplied" && x.Actor == "B" && x.Detail == "쇠약") && !unlinked.Any(x => x.Type == "StatusApplied" && x.Detail == "쇠약")
            && weaknessTick > (6033 * .25 - 200 * .5) * 1.9,
            "쇠약은 생명의 띠가 걸린 적에게만 걸리고, 거의 가득 찬 체력에서는 기본 지속 피해의 약 2배가 들어간다");
    }

    private static void LinkAndSpriteTests(BattleDataSnapshot data)
    {
        // 난수 0.9: 치명타·추가타는 모두 실패하고 스킬은 후보 목록의 마지막 쪽이 선택된다.
        // 라이프 링크(연결) → 다음 내 턴 시작에 빛의 결정체 +2 → 라이프 링크 재사용이 먼저 선택되어 서먼 스프라이트(2단계)로 발산·해제 → 다시 연결.
        var result = Duel(data, ["phantom_pain", "life_link"]);
        var turns = Turns(result);
        Assert(turns.Take(4).Select(x => x.Skill).SequenceEqual(["라이프 링크", "서먼 스프라이트", "라이프 링크", "서먼 스프라이트"]),
            "힐러 시나리오 전제: 라이프 링크로 연결한 다음 턴에 결정체 2개가 쌓이면 곧바로 서먼 스프라이트로 발산하고 다시 연결한다");
        Assert(turns[0].Statuses.Contains("생명의 띠") && turns[0].Statuses.Contains("생명의 띠 연결") && turns[0].Statuses.Contains("생명의 띠: 기본 공격 강화")
            && turns[0].Damage == 0 && !turns[0].Resources.Any(x => x.StartsWith("빛의 결정체")),
            "라이프 링크는 적에게 생명의 띠를, 자신에게 연결 표식과 기본 공격 강화를 걸고 결정체는 다음 턴부터 쌓는다");
        Assert(turns[1].Resources.Contains("빛의 결정체 +2 (현재 2)") && turns[1].Resources.Contains("빛의 결정체 -2 (현재 0)") && turns[1].Damage > 0,
            "연결 중 내 턴 시작에 결정체 2개가 쌓이고, 서먼 스프라이트가 모두 소모한다");
        var sprites = result.Events.Count(x => x.Type == "SkillUsed" && x.Detail == "서먼 스프라이트");
        Assert(sprites >= 2 && result.Events.Count(x => x.Type == "StatusExpired" && x.Actor == "B" && x.Detail == "생명의 띠") == sprites
            && result.Events.Count(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail == "빛의 결정체 +2 (현재 2)") == sprites,
            "서먼 스프라이트는 사용할 때마다 적의 생명의 띠를 해제하고, 결정체는 연결 중인 내 턴에만 쌓인다");
    }

    private static void OathShieldTests(BattleDataSnapshot data)
    {
        // 힐러는 서먼 루미너스만 가진 채(5턴 쿨다운 동안 일반 공격) 일반 공격만 하는 상대와 싸운다.
        // 오든 실드 6,996(×0.25=1,749)이 상대 공격을 먼저 흡수하고, 모두 깎이면 8턴 뒤 다시 생긴다.
        var result = Duel(data, ["summon_luminous"], maxActions: 24);
        var absorbed = result.Events.Where(x => x.Type == "ShieldAbsorbed" && x.Target == "A").Select(x => x.Amount ?? 0).ToArray();
        var resources = result.Events.Where(x => x.Type == "ResourceChanged" && x.Actor == "A").Select(x => x.Detail ?? "").ToList();
        var recharge = resources.IndexOf("오든 실드 재생 대기 +1 (현재 1)");
        var regenerated = resources.FindIndex(recharge + 1, x => x == "보호막 +6996 (현재 6996)");
        Assert(resources.First() == "보호막 +6996 (현재 6996)" && absorbed.Take(2).Sum() == 1749 && recharge > 0
            && resources.IndexOf("오든 실드 재생 대기이(가) 사라졌습니다.") is var expired && expired > recharge && regenerated == expired + 1,
            "오든 실드는 전투 시작에 생기고, 흡수 한도(1,749)를 다 쓰면 재생 대기 8턴이 끝난 직후 다시 생긴다");
        var aTurnsBetween = result.Events.SkipWhile(x => !(x.Type == "ResourceChanged" && x.Detail == "오든 실드 재생 대기 +1 (현재 1)"))
            .TakeWhile(x => !(x.Type == "ResourceChanged" && x.Detail == "오든 실드 재생 대기이(가) 사라졌습니다.")).Count(x => x.Type == "TurnStarted" && x.Actor == "A");
        Assert(aTurnsBetween == 8, "오든 실드 재생 대기(45초)는 힐러의 행동 8턴이다");
        // 소생: 보호막을 얻을 때마다 1초마다 최대 체력 0.03%×10초 → 2턴 동안 턴당 144×0.25×(1+회복량 10%)=40.
        Assert(result.Events.Count(x => x.Type == "StatusApplied" && x.Actor == "A" && x.Detail == "소생: 치유" && x.Amount == 2) >= 2
            && result.Events.Any(x => x.Type == "StatusHeal" && x.Target == "A" && x.Detail == "소생: 치유" && x.Amount == 40),
            "소생은 오든 실드를 얻을 때(전투 시작·재생)마다 2턴 동안 턴당 40을 지속 회복한다");
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, bool sprite = true)
    {
        var rules = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", maxActions.ToString()) }) rules[id] = rules[id] with { Value = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = rules,
            Classes = new Dictionary<string, BattleClass> { ["healer"] = data.Classes["healer"] with { SkillIds = skills }, ["idle"] = new("idle", "대상", Array.Empty<string>()) },
            Skills = data.Skills, Passives = data.Passives, Resources = data.Resources, Statuses = data.Statuses, Derivations = sprite ? data.Derivations : data.Derivations.Where(x => x.Id != "life_link_sprite").ToArray(), LoadedAt = data.LoadedAt
        };
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "healer", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new ConstantRandom(random));
    }

    /// <summary>A의 행동별로 사용 스킬·A가 준 피해 합계·A의 자원 변화·새로 걸린 상태를 모은다.</summary>
    private static List<Turn> Turns(BattleResult result)
    {
        var turns = new List<Turn>();
        List<BattleEvent>? current = null;
        foreach (var e in result.Events.Append(new BattleEvent("TurnStarted", "")))
        {
            if (e.Type != "TurnStarted") { current?.Add(e); continue; }
            if (current is not null)
                turns.Add(new(current.FirstOrDefault(x => x.Type is "SkillUsed" or "NormalAttackUsed") is { } used ? used.Detail ?? "(일반 공격)" : "(행동 없음)",
                    current.Where(x => x.Type is "DamageDealt" or "AdditionalHit" or "AdditionalDamage" && x.Actor == "A").Sum(x => x.Amount ?? 0),
                    current.Where(x => x.Type == "ResourceChanged" && x.Actor == "A").Select(x => x.Detail ?? "").ToArray(),
                    current.Where(x => x.Type == "StatusApplied").Select(x => x.Detail ?? "").ToArray()));
            current = e.Actor == "A" ? [] : null;
        }
        return turns;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class ConstantRandom(double value) : IBattleRandom
    {
        public double NextDouble() => value;
    }

    public static readonly IReadOnlyDictionary<string, string> Sheets = new Dictionary<string, string>(CrossbowBattleTests.Sheets.Where(x => x.Key is "배틀규칙" or "배틀돌발이벤트" or "배틀돌발이벤트효과" or "생활스킬" or "배틀생활스킬" or "배틀생활스킬효과"))
    {
        ["클래스"] = """"
ID,이름,계열,설명,스킬1,스킬2,스킬3,스킬4,스킬5,궁극기,패시브1,패시브2,패시브3,패시브4,패시브5,패시브6
healer,힐러,힐러,"신의 권능을 완드에 담아 아군에게는 치료를, 적군에게는 공포와 고통을 선사하는 클래스. 높은 지력에 기원한 넓은 치유력과 비물리적인 공격력을 발하는 전장의 빛. 천으로 만든 옷을 선호하며, 집중력을 한껏 끌어올려 필요한 곳에 두루 사용한다.",life_link,phantom_pain,pain_of_life,protection,summon_luminous,wing_of_angel,oath_shield,vital_boost,combat_mastery_support_healer,resurgence,transference,luminous_shard
"""",
        ["스킬"] = """"
ID,이름,스킬구분,부모스킬ID,설명,태그,기타
life_link,라이프 링크,일반,,"대상과 자신을 생명의 띠로 연결하는 마법. 버튼을 짧게 누르면 타겟과 연결하여 지속 피해를 주고 기본 공격이 강화된다. 버튼을 길게 누르면 가장 가까운 아군과 연결하여 지속 회복 효과를 주고 적에게 주는 피해를 증가시키며, 대상의 공격마다 추가 피해를 주는 빛의 파동을 일으킨다. 연결이 유지되는 동안 빛의 결정체가 주기적으로 쌓이며, 서먼 스프라이트 스킬을 통해 발산할 수 있다.","연타, 보조",등급: 에픽; 강화 레벨: +24; 대미지: 1초마다 2714; 연결 해제 대미지: 2714; 추가 타격 대미지: 1357; 빛의 파동 총합 대미지: 11764; [시너지] 대미지 증가: 25%; 회복량: 1초마다 328; 빛의 결정체 충전 시간: 3초; 재사용 대기 시간: 1초; 빛의 파동 재사용 대기 시간: 3초; 사거리: 10m
summon_sprite,서먼 스프라이트,파생,life_link,"생명의 띠에서 흘러나온 빛의 결정체를 발산하는 신성 마법. 라이프 링크와 연결된 타겟이 적이면 피해를 주고 아군이면 회복시킨다. 서먼 루미너스 사용 시 다음 1회의 서먼 스프라이트가 강화되어, 연결한 아군과 힐러 자신이 적에게 주는 피해를 추가로 증가시킨다.","연타, 보조",등급: 에픽; 강화 레벨: +24; 1단계 총합 대미지: 1809 × 12; 1단계 총합 회복량: 164 × 6; 2단계 총합 대미지: 2714 × 12; 2단계 총합 회복량: 328 × 6; 3단계 총합 대미지: 3619 × 12; 3단계 총합 회복량: 493 × 6; 지속 시간: 6초; [시너지] 강화 시 대미지 증가: 40%; 대미지 증가 지속 시간: 9초
phantom_pain,팬텀 페인,일반,,불안정한 마력 구체로 다수의 적을 제압하는 제어 마법. 구체가 폭발하며 흩뿌려진 마력이 타겟과 주변의 적들에게 스며들어 지속 피해: 침식을 남기고 공포에 휩싸이게 만든다.,"강타, 방해",등급: 고급; 강화 레벨: +8; 대미지: 10452; 브레이크 대미지: 1칸; 침식 대미지: 1초마다 1583; 침식 지속 시간: 10초; 재사용 대기 시간: 12초; 사거리: 10m; 범위: 4m
nightmare,나이트메어,파생,phantom_pain,적에게 스며든 불안정한 마력을 증폭시키는 제어 마법. 팬텀 페인의 영향을 받는 모든 적에게 피해와 함께 더욱 강력한 지속 피해: 두려움을 준다. 브레이크된 적의 경우는 그 자리에서 기절한다.,"강타, 방해",등급: 고급; 강화 레벨: +8; 대미지: 14253; 두려움 지속 대미지: 4751
pain_of_life,생명의 고통,일반,,"응축한 생명의 띠를 주변에 퍼뜨리는 신성 마법. 정신을 집중하여 주변의 적 다수에게 피해를 주고 감속시키며, 자신의 체력을 소량 회복한다. 라이프 링크가 사용 중일 경우, 연결된 타겟으로부터 효과가 발동한다. 우호적인 대상과 라이프 링크를 연결한 채 사용할 경우, 주변 아군을 치유하고 잠시 동안 적에게 주는 피해를 상승시킨다.","연타, 보조",등급: 에픽; 강화 레벨: +24; 대미지: 4208 × 9; 브레이크된 적 대미지: 6334 × 9; 총합 회복량: 591 × 9; 시전자 회복량: 295 × 9; [시너지] 대미지 증가: 20%; 최대 타겟 수: 4; 채널링 시간: 3초; 재사용 대기 시간: 10초; 사거리: 10m
protection,프로텍션,일반,,"신실한 기도로 보호막을 만드는 신성 마법. 보호막을 만들어 적의 공격을 반격해 밀쳐내고, 받는 피해를 흡수하며 보호막이 유지되는 동안 지속적으로 자신의 체력이 회복된다. 또한 라이프 링크로 연결된 타겟이 아군이면 보호막을, 적이면 약화 효과: 쇠약을 남긴다. 약화 효과: 쇠약은 적의 남은 체력 비율이 높을수록 더 큰 피해를 준다.","보조, 생존, 방해",등급: 에픽; 강화 레벨: +24; 대미지: 7239; 지속 시간: 15초; 피해 흡수량: 4337; 회복량: 1초마다 413; 쇠약 대미지: 18099; 쇠약 대미지 증폭: 최대 200%; 쇠약 지속 시간: 15초; 쇠약 [시너지] 공격력 감소: 10%; 재사용 대기 시간: 15초
summon_luminous,서먼 루미너스,일반,,성스러운 빛의 결정체를 소환하는 신성 마법. 불러낸 빛의 결정체는 일정 시간 동안 자신의 주변을 배회하며 적에게 피해를 주고 아군을 치유한다.,"연타, 보조, 소환",등급: 고급; 강화 레벨: +8; 대미지: 1583 × 3; 회복량: 386; 소환수 행동 간격: 1.5초; 소환수 지속 시간: 15초; 최대 타겟 수: 3; 재사용 대기 시간: 25초; 사거리: 10m
wing_of_angel,윙 오브 엔젤,궁극기,,"찬란한 빛의 날개를 부르는 고결한 기도. 일정 시간, 아군과 적 모두에게 영향을 주는 성스러운 빛의 날개를 불러낸다. 신성한 빛줄기로 주변의 모든 적에게 지속적인 피해를 주는 반면, 주변의 모든 아군에게는 날개의 빛을 퍼뜨려 지속적으로 회복시킨다. 『두려워 말라, 빛의 날개가 길을 인도하리라.』","궁극기, 연타, 보조",등급: 고급; 강화 레벨: +8; 엠블럼 장착 필요; 대미지: 1초마다 14253; 회복량: 1초마다 3092; 지속 시간: 10초; 궁극기 비용: 300; 범위: 10m
"""",
        ["패시브스킬"] = """"
ID,이름,설명,기타
oath_shield,오든 실드,"생명의 기운으로 몸을 보호하는 신성한 가호. 전투 시, 피해를 흡수하는 보호막을 만든다. 보호막이 파괴되면 일정 시간 후 재생된다.",흡수량: 6996; 재사용 대기 시간: 45초
vital_boost,바이탈 부스트,생명의 기운으로 아군의 전투 능력을 향상시키는 신성한 가호. 자신이 회복시킨 아군의 적에게 주는 피해량과 이동 속도가 짧은 시간 동안 증가한다.,[시너지] 대미지 증가: 10%; 이동 속도 증가: 15%; 지속 시간: 3초
resurgence,소생,"몸에 두른 신성한 힘과 반응해 스스로를 치유하고 강화하는 재생의 가호. 오든 실드와 프로텍션의 보호막 효과를 얻을 때, 자신의 체력을 일부 회복한다. 또한, 자신을 제외한 아군을 회복시키거나 공격이 추가타로 적중하면 일정 확률로 잠시 동안 적에게 주는 피해가 증가한다.",회복량: 최대 체력의 0.03%; 아군 회복 시 발동 확률: 30%; 추가타 적중 시 발동 확률: 100%; 대미지 증가: 중첩당 1.5%; 지속 시간: 각 10초
transference,전이,"섬세하게 조율된 마력으로 적의 정신을 파고드는 제어 마법. 기본 공격 시, 일정 확률로 타겟에게 지속 피해: 두려움을 준다. 또한 생명의 고통 스킬 사용 시, 자신이 타겟에게 부여한 강화 및 약화 효과를 주변에 퍼뜨린다. 우호적인 대상에게 라이프 링크를 연결한 경우, 빛의 파동이 추가로 적이 받은 지속 피해: 두려움을 퍼뜨린다. 추가로 최종 피해량이 증가한다.",발동 확률: 40%; 두려움 지속 대미지: 7541; 최종 대미지 증가: 10%
luminous_shard,루미너스 샤드,"떠도는 빛을 모아 광명시키는 비술. 빛 무리를 소환할 때마다 빛의 결정을 얻는다. 윙 오브 엔젤 사용 시, 축적한 빛의 결정을 모두 소모하여 주변의 적들에게 피해를 주고 아군을 치유한다. 피해량과 치유량은 소모한 빛의 결정의 양에 비례하여 증가한다.",서먼 스프라이트 사용 시 빛의 결정 획득: 1~3개; 서먼 루미너스 사용 시 빛의 결정 획득: 10개; 대미지: 0.33초마다 중첩당 62; 회복량: 1초마다 중첩당 2; 최대 빛의 결정 중첩 수: 200개; 지속 시간: 900초
combat_mastery_support_healer,전투 숙련: 지원,후방에서 아군을 치료하는 숙련된 전투 기법. 자신의 회복력과 적에게 주는 연타 피해가 증가한다. 힐러 클래스 레벨이 30에 도달하면 전투 시 클래스 특화 어시스트를 사용할 수 있다.,회복량 증가: 10%; 연타 대미지 증가: 3%; 클래스 특화 어시스트 해금: 힐러 클래스 레벨 30
"""",
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,사용조건,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고
life_link,TRUE,공격·보조,상대,1,0,70,항상,궁극기 게이지,,150,FALSE,적과 생명의 띠를 연결해 해제할 때까지 지속 피해를 주고 기본 공격을 강화하며 빛의 결정체를 모은다. 다시 누르면 서먼 스프라이트로 발산한다.,{caster}가 {target}에게 생명의 띠를 연결합니다!,"원본 재사용 대기 1초→1턴. 짧게 누르기(적 연결)만 반영하고 길게 누르기(아군 연결·빛의 파동)는 1:1이라 제외. 연결은 서먼 스프라이트로 해제할 때까지 유지(99턴)되고, 연결 중 턴마다 빛의 결정체 2개가 쌓인다. 결정체 2개 이상에서 다시 누르면(재사용 시 파생 life_link_sprite) 서먼 스프라이트로 발산·해제"
summon_sprite,TRUE,공격·파생,상대,0,0,0,라이프 링크 재사용·빛의 결정체 2개 이상,,,0,FALSE,모은 빛의 결정체를 발산해 결정체 수(1~3단계)에 비례한 피해를 주고 생명의 띠 연결을 해제한다.,{caster}가 생명의 띠에 모인 빛의 결정체를 발산합니다!,"파생 전용. 1~3단계를 결정체 1~3개로 판정. 원본 단계별 1809/2714/3619×12를 그대로 반영(단계 차이는 추가 피해 행 2,715×4로 표현. 라이프 링크 행동과 한 세트라 낮추지 않음). 원본 연결 해제 대미지 2,714를 주고 연결 상태를 모두 해제한다"
phantom_pain,TRUE,공격·방해,상대,2,0,65,항상,궁극기 게이지,,150,FALSE,마력 구체로 적을 공격해 브레이크 피해와 침식을 남긴다. 다음 행동은 나이트메어로 이어진다.,{caster}가 불안정한 마력 구체를 {target}에게 터뜨립니다!,"원본 재사용 대기 12초→2턴, 표시 피해 10,452, 브레이크 1칸. 침식 1초마다 1583×10초를 2턴에 나눔. 원문의 '공포에 휩싸이게 만든다'는 멘탈 브레이크(공식 가이드 브레이크 타입: 몬스터가 도망침)라 브레이크 대미지 1칸으로 반영하고 따로 부여하지 않음"
nightmare,TRUE,공격·방해,상대,2,0,0,팬텀 페인 재사용,,,0,FALSE,침식을 증폭시켜 피해와 두려움을 준다.,{caster}가 {target}에게 스며든 마력을 증폭시킵니다!,"파생 전용. 나이트메어 준비(heal_phantom_mark) 중 재사용하면 실행. 기본쿨다운 2는 재사용 시 팬텀 페인의 쿨다운으로 적용된다. 표시 피해 14,253. 브레이크된 적 기절은 스턴 브레이크라 상대가 브레이크 상태일 때 브레이크를 다시 거는 것으로 반영(GitHub Issue #11)"
pain_of_life,TRUE,공격·회복,자신·상대,2,0,62,항상,궁극기 게이지,,150,FALSE,생명의 띠를 퍼뜨려 연속 피해를 주고 체력을 조금 회복한다. 브레이크된 적에게 더 큰 피해를 준다.,{caster}가 정신을 집중해 생명의 띠를 퍼뜨립니다!,"원본 재사용 대기 10초→2턴. 표시 피해 4,208×9(브레이크 6,334×9)가 다른 힐러 스킬보다 3배 이상 커서 2,000×9(3,000×9)로 보정. 감속은 원본 수치가 없어 채널링 3초 기준 1턴 쿨다운증가(heal_slowed)로 반영"
protection,TRUE,생존·방해,자신·상대,3,0,60,항상,궁극기 게이지,,150,FALSE,보호막을 만들고 반격 피해를 주며 체력을 지속 회복한다. 생명의 띠로 연결된 적에게는 쇠약을 남긴다.,{caster}가 신실한 기도로 보호막을 두릅니다!,원본 재사용 대기 15초→3턴. 공격을 받을 때의 반격은 사용 시 1회 피해로 단순화
summon_luminous,TRUE,공격·회복,자신·상대,5,0,60,항상,궁극기 게이지,,150,FALSE,빛의 결정체를 소환해 3턴 동안 적에게 피해를 주고 자신을 치유한다. 다음 서먼 스프라이트를 강화한다.,{caster}가 성스러운 빛의 결정체를 소환합니다!,"원본 재사용 대기 25초→5턴(6초=1턴, 올림). 소환수 1.5초마다 행동×15초를 턴당 4회×3턴으로 환산"
wing_of_angel,TRUE,궁극기·회복,자신·상대,0,0,100,궁극기 게이지 300,궁극기 게이지,300,,TRUE,"빛의 날개를 불러 2턴 동안 적에게 지속 피해를, 자신에게 지속 회복을 준다.",{caster}의 등 뒤로 찬란한 빛의 날개가 펼쳐집니다!,"원본 1초마다 피해 14,253·회복 3,092×10초는 다른 궁극기보다 수 배 커서 턴당 12,000·2,600×2턴으로 보정. 엠블럼 장착 필요 조건은 실제 사용 조건이 아니므로 반영하지 않음"
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
life_link_03,life_link,3,상태효과,자신,,0,1,1,99,heal_link_self,1,생명의 띠가 이어져 빛의 결정체가 모이기 시작합니다.,연결 유지 표식(앞 행들의 연결 전 조건을 위해 마지막 순서). 턴당자원증가로 연결 중 턴 시작마다 빛의 결정체 +2(충전 3초 → 6초 턴당 2개). 서먼 스프라이트가 해제할 때까지 유지(99턴),자신,상태효과미보유,heal_link_self,,,,,
life_link_01,life_link,1,지속피해,상대,,5428,1,1,99,heal_life_link,1,생명의 띠가 {target}의 생명력을 갉아먹습니다.,"원본 1초마다 2,714. 연결은 해제할 때까지 유지(99턴)되고 턴당 5,428(2초분)로 환산",자신,상태효과미보유,heal_link_self,,,,,
life_link_02,life_link,2,상태효과,자신,,0,1,1,99,heal_link_basic,1,생명의 띠로 기본 공격이 강화됩니다.,"원본 기본 공격 추가 타격 1,357을 일반 공격 피해 +20%로 단순화. 연결이 유지되는 동안",자신,상태효과미보유,heal_link_self,,,,,
summon_sprite_01,summon_sprite,1,피해,상대,,1809,12,1,0,,0,빛의 결정체가 쏟아져 {target}에게 총 {damage}의 피해!,"1단계(결정체 1개) 원본 1,809×12",자신,자원보유,heal_light_orb,>=,1,,,
summon_sprite_02,summon_sprite,2,피해,상대,,2715,4,1,0,,0,2단계 결정체가 더해져 {damage}의 피해!,"2단계(결정체 2개 이상) 추가분. 원본 2,714×12와 1단계의 차이 905×12를 2,715×4로 표현",자신,자원보유,heal_light_orb,>=,2,,,
summon_sprite_03,summon_sprite,3,피해,상대,,2715,4,1,0,,0,3단계 결정체가 더해져 {damage}의 피해!,"3단계(결정체 3개) 추가분. 원본 3,619×12와 2단계의 차이 905×12를 2,715×4로 표현",자신,자원보유,heal_light_orb,>=,3,,,
summon_sprite_04,summon_sprite,4,상태효과,자신,,0,1,1,2,heal_sprite_power,1,강화된 빛의 결정체로 주는 피해가 증가합니다.,"서먼 루미너스 이후 첫 서먼 스프라이트만. [시너지] 대미지 증가 40%, 9초→2턴",자신,자원보유,heal_sprite_boost,>=,1,,,
summon_sprite_05,summon_sprite,5,자원소모,자신,,0,1,1,0,heal_sprite_boost,0,,강화 표식 소모,자신,자원보유,heal_sprite_boost,>=,1,,전부,
summon_sprite_06,summon_sprite,6,자원소모,자신,,0,1,1,0,heal_light_orb,0,모은 빛의 결정체를 모두 발산했습니다.,빛의 결정체 전부 소모,,,,,,,전부,
summon_sprite_07,summon_sprite,7,피해,상대,,2714,1,1,0,,0,끊어지는 생명의 띠가 {target}에게 {damage}의 피해!,"원본 연결 해제 대미지 2,714",,,,,,,,
summon_sprite_08,summon_sprite,8,상태해제,상대,,0,1,1,0,heal_life_link,0,,라이프 링크 연결 해제(적의 생명의 띠),,,,,,,,
summon_sprite_09,summon_sprite,9,상태해제,자신,,0,1,1,0,heal_link_basic,0,,라이프 링크 연결 해제(기본 공격 강화),,,,,,,,
summon_sprite_10,summon_sprite,10,상태해제,자신,,0,1,1,0,heal_link_self,0,,라이프 링크 연결 해제(결정체 적립 중지),,,,,,,,
phantom_pain_01,phantom_pain,1,피해,상대,,10452,1,1,0,,0,마력 구체가 폭발해 {target}에게 {damage}의 피해!,"표시 피해 10,452",,,,,,,,
phantom_pain_02,phantom_pain,2,브레이크피해,상대,,1,1,1,0,,0,{target}의 균형이 흔들립니다!,브레이크 대미지 1칸,,,,,,,,
phantom_pain_03,phantom_pain,3,지속피해,상대,,7915,1,1,2,heal_erosion,1,흩뿌려진 마력이 {target}에게 침식을 남깁니다.,"원본 침식 1초마다 1,583×10초=15,830을 2턴(10초→2턴)에 나눔",,,,,,,,
phantom_pain_04,phantom_pain,4,상태효과,자신,,0,1,1,2,heal_phantom_mark,1,,나이트메어 재사용 판정용 자신의 표식(나이트메어 준비). 침식과 같은 2턴,,,,,,,,
nightmare_01,nightmare,1,피해,상대,,14253,1,1,0,,0,증폭된 마력이 {target}을 덮쳐 {damage}의 피해!,"표시 피해 14,253",,,,,,,,
nightmare_02,nightmare,2,지속피해,상대,,2376,1,1,2,heal_fear,1,{target}이 두려움에 휩싸입니다.,"원본 두려움 지속 대미지 4,751을 2턴에 나눔(음유시인 두려움과 같은 방식)",,,,,,,,
nightmare_04,nightmare,4,상태효과,상대,,0,1,1,1,break_broken,1,브레이크된 {target}이 그 자리에서 기절합니다!,원본 '브레이크된 적은 그 자리에서 기절'. 기절은 스턴 브레이크(공식 가이드 브레이크 타입)이므로 상대가 브레이크 상태일 때 브레이크를 다시 건다. 브레이크가 상대의 다음 행동에서 끝나 실제로는 발동하지 않으며 GitHub Issue #11에서 해석을 정한다,상대,상태효과보유,break_broken,,,,,
pain_of_life_01,pain_of_life,1,피해,상대,,2000,9,1,0,,0,퍼져 나간 생명의 띠가 {target}에게 총 {damage}의 피해!,"원본 4,208×9를 2,000×9로 보정. 브레이크되지 않은 적",상대,상태효과미보유,break_broken,,,,,
pain_of_life_02,pain_of_life,2,피해,상대,,3000,9,1,0,,0,무너진 {target}을 생명의 띠가 파고들어 총 {damage}의 피해!,"원본 브레이크된 적 대미지 6,334×9를 3,000×9로 보정",상대,상태효과보유,break_broken,,,,,
pain_of_life_03,pain_of_life,3,회복,자신,,295,9,1,0,,0,생명의 띠가 {caster}의 체력을 {heal} 회복시킵니다.,원본 시전자 회복량 295×9,,,,,,,,
pain_of_life_05,pain_of_life,5,쿨다운증가,상대,,0,1,1,1,heal_slowed,1,{target}의 움직임이 느려집니다.,"원본 감속은 수치·지속시간이 없어 채널링 3초 기준 1턴, 석궁사수 둔화(cb_slowed)와 같은 쿨다운증가 1로 반영",,,,,,,,
protection_01,protection,1,피해,상대,,7239,1,1,0,,0,보호막이 {target}을 밀쳐내 {damage}의 피해!,"원본 표시 피해 7,239(공격을 반격해 밀쳐냄)를 사용 시 1회 피해로 단순화",,,,,,,,
protection_02,protection,2,자원증가,자신,,4337,1,1,0,heal_shield,0,보호막이 피해를 흡수할 준비를 합니다.,"원본 피해 흡수량 4,337. 보호막 자원은 fixed_damage_scale을 곱한 만큼 배틀 피해를 흡수한다",,,,,,,,
protection_03,protection,3,지속회복,자신,,2478,1,1,3,heal_protection_regen,1,보호막 안에서 체력이 차오릅니다.,"원본 1초마다 413×15초. 15초→3턴, 턴당 413×6",,,,,,,,
protection_04,protection,4,지속피해,상대,,6033,1,1,3,heal_weakness,1,생명의 띠를 타고 {target}에게 쇠약이 스며듭니다.,"적과 연결된 경우의 약화 효과: 쇠약. 원본 18,099를 3턴(15초)에 나눔. 남은 체력 비율 비례 증폭(최대 200%)은 체력비례지속피해증폭(체력이 가득하면 ×2)으로 반영. [시너지] 공격력 감소 10%는 상태로 반영",상대,상태효과보유,heal_life_link,,,,,
summon_luminous_01,summon_luminous,1,지속피해,상대,,6332,1,1,3,heal_luminous_summon,1,빛의 결정체가 {target} 주위를 맴돌며 공격합니다.,"원본 1,583을 1.5초마다(턴당 4회)×15초(3턴)",,,,,,,,
summon_luminous_02,summon_luminous,2,지속회복,자신,,1544,1,1,3,heal_luminous_regen,1,빛의 결정체가 {caster}를 치유합니다.,원본 회복량 386을 턴당 4회×3턴,,,,,,,,
summon_luminous_03,summon_luminous,3,자원설정,자신,,1,1,1,0,heal_sprite_boost,1,다음 서먼 스프라이트가 강화됩니다.,서먼 스프라이트 설명의 '서먼 루미너스 사용 시 다음 1회 강화',,,,,,,,
wing_of_angel_01,wing_of_angel,1,지속피해,상대,,12000,1,1,2,heal_wing,1,신성한 빛줄기가 {target}에게 쏟아집니다.,"원본 1초마다 14,253×10초(10초→2턴). 다른 궁극기와 배율을 맞추려고 턴당 12,000로 보정",,,,,,,,
wing_of_angel_02,wing_of_angel,2,지속회복,자신,,2600,1,1,2,heal_wing_regen,1,빛의 날개가 {caster}를 감싸 치유합니다.,"원본 1초마다 3,092×10초. 피해와 같은 비율로 턴당 2,600로 보정",,,,,,,,
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
life_link_sprite,life_link,summon_sprite,조건,0,1,자원보유,heal_light_orb>=2,FALSE,재사용 시,연결 중 빛의 결정체가 2개 이상이면 라이프 링크를 다시 눌러 서먼 스프라이트로 발산·해제(원본: 한 번 더 누르면 서먼 스프라이트 사용 후 해제). 재사용 후보는 먼저 선택되므로 조건이 되면 바로 발산한다,100
phantom_pain_nightmare,phantom_pain,nightmare,조건,0,1,상태효과보유,heal_phantom_mark,FALSE,재사용 시,팬텀 페인 여파가 남은 동안 재사용하면 나이트메어. 원본의 '팬텀 페인의 영향을 받는 적'을 자신의 표식으로 판정,100
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
oath_shield,TRUE,"전투 시작 시 보호막 6,996. 파괴되면 45초→8턴 뒤 재생. 프로텍션 보호막과 같은 보호막 자원을 쓰므로 둘이 합쳐 모두 깎였을 때를 파괴로 본다."
vital_boost,TRUE,아군 회복은 1:1이라 자기 회복으로 해석. 즉시 회복(생명의 고통·소생)만 회복적용시를 일으키고 지속 회복 틱은 발동하지 않는다. 이동 속도 증가는 생략.
combat_mastery_support_healer,TRUE,"회복량 +10%, 연타(다단) 피해 +3%. 음유시인 전투 숙련: 지원과 같은 상태를 쓴다. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외(항상 만렙 가정)."
resurgence,TRUE,"보호막 획득 시 회복은 1초마다 최대 체력 0.03%×10초(기본 HP 기준 턴당 36×2턴)로 해석. 지속 회복이라 바이탈 부스트(회복적용시)는 일으키지 않는다. 자신을 제외한 아군 회복 30%는 1:1이라 제외. 추가타 적중마다 중첩, 최대 중첩은 원본에 없어 10으로 가정. 중첩마다 따로 2턴(10초) 뒤 만료(중첩방식=개별)."
transference,TRUE,"기본 공격 시 40% 두려움 7,541을 2턴에 나눔. 생명의 고통의 효과 전파, 아군 연결 빛의 파동 전파는 1:1이라 제외. 최종 피해 +10%는 상시."
luminous_shard,TRUE,서먼 스프라이트 1~3개는 1개 + 50%×2로 근사. 윙 오브 엔젤 사용 시 중첩당 피해를 소모중첩배율 추가피해로 반영. 중첩당 회복은 원문 1초마다 2×10초=20을 소모중첩배율 회복으로 반영(원문 수치라 미미함). 지속 시간 900초는 전투보다 길어 생략.
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
os_01,oath_shield,1,자원증가,자신,6996,1,1,0,heal_shield,0,생명의 기운이 보호막이 되어 몸을 감쌉니다.,,,,,,,,전투시작,,,"원문 흡수량 6,996",
os_02,oath_shield,2,자원설정,자신,1,1,1,0,heal_oath_recharge,0,,자신,자원보유,heal_oath_recharge,<=,0,,,자원소진시,,heal_shield,보호막이 모두 깎이면 재생 대기(45초→8턴)를 시작. 이미 대기 중이면 새로 시작하지 않음,
os_03,oath_shield,3,자원증가,자신,6996,1,1,0,heal_shield,0,오든 실드가 다시 생겨납니다.,,,,,,,,자원소진시,,heal_oath_recharge,재생 대기가 끝나면 보호막 재생,
vb_01,vital_boost,1,상태효과,자신,0,1,1,1,heal_vital_boost,0,회복의 기운으로 주는 피해가 증가합니다.,,,,,,,,회복적용시,,,원문 [시너지] 대미지 증가 10%·3초→1턴. 아군 회복은 자기 회복으로 해석,
cmsh_01,combat_mastery_support_healer,1,회복량증가,자신,0,1,1,0,combat_mastery_support_heal,0,전투 숙련: 지원으로 회복량이 증가합니다.,,,,,,,,전투시작,,,회복량 +10%,
cmsh_02,combat_mastery_support_healer,2,멀티히트피해증가,자신,0,1,1,0,combat_mastery_support_multi,0,전투 숙련: 지원으로 연타 피해가 증가합니다.,,,,,,,,전투시작,,,연타 대미지 +3%,
rs_01,resurgence,1,지속회복,자신,144,1,1,2,heal_resurgence_regen,0,신성한 보호막에 반응해 체력이 차오릅니다.,,,,,,,,자원획득시,,heal_shield,"원문 최대 체력의 0.03%에 시간 표기가 없어 1초마다 0.03%×10초로 해석(2026-09-26 사용자 확인). 기본 HP 20,000 기준 턴(6초)당 36을 고정값 144×fixed_damage_scale로 환산, 10초→2턴. 전투력에 따른 최대 HP 차이는 반영하지 않음",
rs_02,resurgence,2,주는피해증가,자신,0,1,1,0,heal_resurgence_power,0,,,,,,,,,전투시작,,,중첩자원ID=heal_resurgence로 중첩당 +1.5%,
rs_03,resurgence,3,자원증가,자신,1,1,1,0,heal_resurgence,0,소생의 힘이 공격에 깃듭니다.,,,,,,,,추가타적중시,,,추가타 적중 시 발동 확률 100%,
tf_01,transference,1,지속피해,상대,3771,1,0.4,2,heal_transference_fear,0,기본 공격에 실린 마력이 {target}에게 두려움을 심습니다.,,,,,,,,기본공격적중시,,,"원문 발동 확률 40%, 두려움 지속 대미지 7,541을 2턴에 나눔",
tf_02,transference,2,주는피해증가,자신,0,1,1,0,heal_transference_power,0,전이로 최종 피해가 증가합니다.,,,,,,,,전투시작,,,최종 대미지 증가 10%,
ls_01,luminous_shard,1,자원증가,자신,10,1,1,0,heal_luminous_shard,0,빛의 결정을 10개 모았습니다.,,,,,,,,스킬사용완료시,summon_luminous,,서먼 루미너스 사용 시 10개,
ls_02,luminous_shard,2,자원증가,자신,1,1,1,0,heal_luminous_shard,0,,,,,,,,,스킬사용완료시,summon_sprite,,서먼 스프라이트 1~3개 중 기본 1개,
ls_03,luminous_shard,3,자원증가,자신,1,1,0.5,0,heal_luminous_shard,0,,,,,,,,,스킬사용완료시,summon_sprite,,추가 1개 50%,
ls_04,luminous_shard,4,자원증가,자신,1,1,0.5,0,heal_luminous_shard,0,,,,,,,,,스킬사용완료시,summon_sprite,,추가 1개 50%,
ls_05,luminous_shard,5,추가피해,상대,65,1,1,0,,0,모아 둔 빛의 결정이 한꺼번에 터집니다!,자신,자원보유,heal_luminous_shard,>=,1,heal_luminous_shard,소모중첩배율,스킬사용완료시,wing_of_angel,,원문 0.33초마다 중첩당 62. 윙 오브 엔젤과 같은 비율로 낮춰 중첩당 65 배틀 피해,
ls_07,luminous_shard,6,회복,자신,20,1,1,0,,0,모아 둔 빛의 결정이 {caster}를 치유합니다.,자신,자원보유,heal_luminous_shard,>=,1,heal_luminous_shard,소모중첩배율,스킬사용완료시,wing_of_angel,,원문 1초마다 중첩당 2를 윙 오브 엔젤 10초로 보고 중첩당 20(×fixed_damage_scale). 원문 수치 그대로라 미미함. 결정 소모(ls_06)보다 먼저 실행,
ls_06,luminous_shard,7,자원소모,자신,0,1,1,0,heal_luminous_shard,0,,자신,자원보유,heal_luminous_shard,>=,1,,전부,스킬사용완료시,wing_of_angel,,축적한 빛의 결정을 모두 소모,
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.
heal_light_orb,빛의 결정체,자원,3,0,0,가산,TRUE,"라이프 링크 연결 중 힐러의 턴 시작마다 2개씩 쌓이는 결정체(충전 3초, 6초=1턴, 최대 3, 서먼 스프라이트 1~3단계). 서먼 스프라이트가 모두 소모한다."
heal_shield,보호막,보호막,0,0,0,가산,TRUE,"힐러의 오든 실드·프로텍션 보호막(화면 흡수량). 분류=보호막 자원은 fixed_damage_scale을 곱한 만큼 받는 피해를 먼저 흡수하고, 모두 깎이면 자원소진시가 발동한다."
heal_oath_recharge,오든 실드 재생 대기,자원,1,0,8,교체,TRUE,보호막이 파괴된 뒤 오든 실드가 재생되기까지의 대기(45초→8턴). 만료되면 오든 실드가 다시 생긴다.
heal_sprite_boost,스프라이트 강화 준비,자원,1,0,0,교체,TRUE,서먼 루미너스 사용 후 다음 1회의 서먼 스프라이트를 강화하는 표식.
heal_resurgence,소생,중첩,10,0,2,개별,TRUE,공격이 추가타로 적중할 때마다 +1(최대 중첩은 원본에 없어 10으로 가정). 원문 각 10초→중첩마다 2턴(중첩방식=개별).
heal_luminous_shard,빛의 결정,중첩,200,0,0,가산,TRUE,루미너스 샤드가 서먼 루미너스(+10)·서먼 스프라이트(+1~3)로 모으는 결정(최대 200). 윙 오브 엔젤 사용 시 모두 소모해 비례 피해를 준다.
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID
break_broken,브레이크,브레이크|받는피해증가,0.25,브레이크 시 다음 행동을 잃고 받는 피해 +25%,,,,,,
combat_mastery_support_heal,전투 숙련: 지원,회복량증가,0.1,회복량 +10%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,
combat_mastery_support_multi,전투 숙련: 지원,멀티히트피해증가,0.03,적에게 주는 멀티히트(다단) 피해 +3%,,,,,,
heal_life_link,생명의 띠,없음,0,라이프 링크가 적에게 남기는 연결·지속 피해 상태(서먼 스프라이트가 해제할 때까지). 프로텍션은 이 상태의 적에게 쇠약을 남긴다.,,,,,,
heal_link_self,생명의 띠 연결,턴당자원증가,2,라이프 링크 연결 유지 표식. 보유자 턴 시작마다 빛의 결정체 +2(대상자원ID). 서먼 스프라이트가 해제한다.,,,,,,heal_light_orb
heal_link_basic,생명의 띠: 기본 공격 강화,기본공격피해증가,0.2,"라이프 링크 연결 중 기본 공격 강화(원본 추가 타격 1,357)를 일반 공격 피해 +20%로 단순화.",,,,,,
heal_erosion,침식,없음,0,팬텀 페인의 지속 피해.,,,,,,
heal_phantom_mark,나이트메어 준비,없음,0,팬텀 페인 사용 후 나이트메어로 이어갈 수 있는 자신의 표식(재사용 파생 조건). 상대에게 거는 효과가 아니다.,,,,,,
heal_fear,두려움,없음,0,나이트메어의 지속 피해.,,,,,,
heal_weakness,쇠약,주는피해감소|체력비례지속피해증폭,0.1,"프로텍션이 연결된 적에게 남기는 약화. 지속 피해와 [시너지] 공격력 감소 10%(주는 피해 -10%). 지속 피해는 대상의 남은 체력 비율만큼 증폭(가득하면 ×2, 원본 최대 200%).",,,주는피해감소,,0.1|1,
heal_protection_regen,프로텍션,없음,0,프로텍션 보호막이 유지되는 동안의 지속 회복.,,,,,,
heal_luminous_summon,서먼 루미너스,없음,0,소환된 빛의 결정체의 지속 피해.,,,,,,
heal_luminous_regen,서먼 루미너스: 치유,없음,0,소환된 빛의 결정체의 지속 회복.,,,,,,
heal_sprite_power,강화된 서먼 스프라이트,주는피해증가,0.4,서먼 루미너스 이후 첫 서먼 스프라이트가 주는 [시너지] 대미지 증가 40%. 9초→2턴.,,,주는피해증가,,,
heal_wing,윙 오브 엔젤,없음,0,윙 오브 엔젤의 지속 피해.,,,,,,
heal_wing_regen,윙 오브 엔젤: 치유,없음,0,윙 오브 엔젤의 지속 회복.,,,,,,
heal_vital_boost,바이탈 부스트,주는피해증가,0.1,회복을 받으면 [시너지] 주는 피해 +10%(3초→1턴). 이동 속도 증가는 생략.,,,주는피해증가,,,
heal_resurgence_power,소생,주는피해증가,0.015,소생 중첩당 주는 피해 +1.5%.,,heal_resurgence,,,,
heal_resurgence_regen,소생: 치유,없음,0,소생이 보호막을 얻을 때 주는 지속 회복(1초마다 최대 체력 0.03%×10초).,,,,,,
heal_transference_fear,두려움(전이),없음,0,전이가 기본 공격으로 남기는 지속 피해.,,,,,,
heal_slowed,감속,쿨다운증가,1,생명의 고통의 감속. 보유 중 쿨다운 감소가 멈춘다(석궁사수 둔화와 같은 단순화).,,,,,,
heal_transference_power,전이,주는피해증가,0.1,전이의 최종 피해량 +10%(상시).,,,,,,
"""",
    };
}
