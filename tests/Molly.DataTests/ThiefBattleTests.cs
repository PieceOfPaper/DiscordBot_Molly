using Molly.Battle;

/// <summary>
/// 도적의 오버 차지 단계·아드레날린+·은신·스닉 어택·포이즌 트랩·포이즌 익스플로전·블리츠 러시·퀵 핸즈 흐름을 실제 시트 행으로 검사한다.
/// Sheets의 클래스별 시트는 2026-09-27 배틀 시트에서 도적 행(과 참조하는 공용 행)만 추린 사본이다. 시트의 도적 행을 바꾸면 이 사본도 함께 갱신한다.
/// 배틀규칙·돌발 이벤트·생활스킬 시트는 석궁사수 사본을 그대로 쓴다.
/// </summary>
internal static class ThiefBattleTests
{
    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow);
        OverchargeTests(data);
        PoisonTests(data);
        StealthTests(data);
        BlitzRushTests(data);
    }

    private static void OverchargeTests(BattleDataSnapshot data)
    {
        // 난수 0.9: 치명타·추가타·확률 효과는 모두 실패한다. 쓰로잉 봄만 가진 도적은 충전 3단계로 시작하면 폭탄 4개(3단계 3개 + 아드레날린+ 1개)를 던진다.
        var turns = Turns(Duel(data, ["throwing_bomb"], maxActions: 6, initial: [("thief_bomb_charge", 3)]));
        Assert(turns[0].Skill == "쓰로잉 봄" && turns[0].Hits == 4 && turns[0].Statuses.Contains("아드레날린") && !turns[0].Statuses.Contains("아드레날린: 쓰로잉 봄") && turns[0].Resources.Contains("오버 차지 단계 +3 (현재 3)"),
            "쓰로잉 봄 3단계는 비용 1을 낸 뒤 남은 충전으로 단계를 정해 폭탄 4개를 던지고 아드레날린을 얻는다");
        // 다음 턴 시작에 충전 +1 → 1단계 폭탄 2개. 3단계 아드레날린(2턴)의 쓰로잉 봄 +48%와 폭발 여파(받는 피해 +10%)가 한 타에 모두 붙는다.
        // 한 타 = (5,943×0.25 − 방어 100) × 편차 1.08 × 주는 피해(연타 +5%, 아드레날린 +48%) × 받는 피해(폭발 여파 +10%).
        var perHit = (5943 * .25 - 200 * .5) * (.9 + .9 * .2);
        Assert(turns[1].Skill == "쓰로잉 봄" && turns[1].Hits == 2 && turns[1].Resources.Contains("오버 차지 단계 +1 (현재 1)")
            && turns[0].Damage == 4 * (int)Math.Round(perHit * 1.05) && turns[1].Damage == 2 * (int)Math.Round(perHit * 1.53 * 1.1),
            "충전은 턴마다 1단계씩 쌓이고, 3단계 아드레날린의 쓰로잉 봄 피해 증가와 폭발 여파가 다음 폭탄에 적용된다");

        // 기습은 비용 2라 사용 뒤 두 턴을 기다려야 다시 1단계가 되고, 한 턴 더 모으면(충전 3) 2단계다.
        var ambush = Turns(Duel(data, ["ambush"], maxActions: 8, initial: [("thief_ambush_charge", 3)]));
        Assert(ambush[0].Skill == "기습" && ambush[0].Statuses.Contains("상처") && ambush[1].Skill == "(일반 공격)" && ambush[2].Skill == "기습" && !ambush[2].Statuses.Contains("상처"),
            "기습 2단계는 상처를 남기고, 충전 2(1단계)가 될 때까지 한 턴은 일반 공격을 한다");

        // 퀵 핸즈: 피해를 준 스킬 턴 세 번이면 쿨다운 감소 뒤 초기화된다.
        // 카운트 자원은 로그숨김이라 쿨다운 감소 로그로 확인한다(세 번째 폭탄의 쿨다운 1이 바로 0이 된다).
        var quick = Duel(data, ["throwing_bomb"], maxActions: 6).Events;
        Assert(quick.Count(x => x.Type == "CooldownReduced" && x.Actor == "A") == 1 && Turns(Duel(data, ["throwing_bomb"], maxActions: 4)).All(x => x.Skill == "쓰로잉 봄")
            && !quick.Any(x => x.Type == "ResourceChanged" && x.Detail!.StartsWith("퀵 핸즈")),
            "퀵 핸즈는 스킬 적중 턴을 기본 공격으로 세어 3회마다 쿨다운을 줄이고, 카운트는 로그에 남기지 않는다");
    }

    private static void PoisonTests(BattleDataSnapshot data)
    {
        // 포이즌 트랩(독 지대·중독·둔화) → 상대 턴에 중독 1틱 → 스크류 대거 2단계가 남은 중독 1틱을 폭발시킨다(+5%, 방어력은 한 번만 뺀다).
        var result = Duel(data, ["screw_dagger", "poison_trap"], maxActions: 4);
        var turns = Turns(result);
        var detonation = result.Events.FirstOrDefault(x => x.Type == "StatusDetonated");
        Assert(turns[0].Skill == "포이즌 트랩" && turns[0].Statuses.Contains("독 지대") && turns[0].Statuses.Contains("중독") && turns[0].Statuses.Contains("둔화")
            && result.Events.Count(x => x.Type == "StatusDamage" && x.Target == "B" && x.Detail == "중독") == 1,
            "포이즌 트랩은 독 지대·중독·둔화를 걸고 중독이 상대 턴에 한 번 들어간다");
        Assert(turns[1].Skill == "스크류 대거" && turns[1].Hits == 6 && detonation is { Actor: "A", Target: "B", Detail: "중독" } && detonation.Amount == (int)Math.Round((1053 * .25 - 200 * .5) * 1.05)
            && !result.Events.SkipWhile(x => x != detonation).Any(x => x.Type == "StatusDamage" && x.Detail == "중독"),
            "스크류 대거 2단계(6타)는 남은 중독 피해를 한 번에 폭발시키고 중독을 없앤다");
        // 난수 0.1이면 포이즌 어택(35%)이 스킬 적중 턴에 중독을 건다.
        var attack = Duel(data, ["throwing_bomb"], maxActions: 2, random: .1).Events;
        Assert(attack.Any(x => x.Type == "StatusApplied" && x.Actor == "B" && x.Detail == "중독"), "포이즌 어택은 스킬 적중 턴에 35% 확률로 중독을 건다");
    }

    private static void StealthTests(BattleDataSnapshot data)
    {
        // 은신 → 스크류 대거: 은신 추가 타격과 기절(브레이크 1칸)을 주고 은신이 풀리며 스닉 어택(추가타 확률)을 얻는다.
        var result = Duel(data, ["screw_dagger", "stealth"], maxActions: 4);
        var turns = Turns(result);
        Assert(turns[0].Skill == "은신" && turns[0].Statuses.Contains("은신") && turns[1].Skill == "스크류 대거"
            && turns[1].Statuses.Contains("스닉 어택") && turns[1].Hits == 7
            && result.Events.Any(x => x.Type == "StatusExpired" && x.Actor == "A" && x.Detail == "은신")
            && result.Events.Count(x => x.Type == "BreakGaugeChanged" && x.Target == "B") == 2,
            "은신 뒤 스크류 대거는 추가 타격·브레이크를 더하고 은신을 풀며 스닉 어택을 얻는다");
        // 쓰로잉 봄(2턴을 모아 2단계, 폭탄 3개)은 단검 공격이 아니라 은신을 쓰지 않는다. 은신이 다음 행동 뒤 만료되면서 스닉 어택이 발동한다.
        var expiredResult = Duel(data, ["throwing_bomb", "stealth"], maxActions: 4);
        var expired = Turns(expiredResult);
        Assert(expired[1].Skill == "쓰로잉 봄" && expired[1].Hits == 3 && expired[1].Statuses.Contains("스닉 어택")
            && expiredResult.Events.Any(x => x.Type == "StatusExpired" && x.Actor == "A" && x.Detail == "은신"),
            "쓰로잉 봄은 은신을 소모하지 않고, 은신이 만료되면 스닉 어택이 발동한다");
        // 무방비(브레이크 상태)의 적에게는 스닉 어택의 무방비 피해 +20%가 붙는다. 브레이크 게이지 1칸이면 은신 추가 타격이 바로 브레이크를 걸어,
        // 이어지는 스크류 대거 2단계 첫 타 = (5,034×0.25 − 100) × 편차 1.08 × (1 + 연타 5% + 무방비 20%) × 브레이크 받는 피해 1.25.
        var broken = Duel(data, ["screw_dagger", "stealth"], maxActions: 4, rules: [("break_gauge_maximum", "1")]).Events;
        var withoutSneak = Duel(data, ["screw_dagger", "stealth"], maxActions: 4, rules: [("break_gauge_maximum", "1")], withoutPassives: ["sneak_attack"]).Events;
        int ScrewFirstHit(IReadOnlyList<BattleEvent> events) => events.SkipWhile(x => x.Type != "BreakActivated").First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
        var baseHit = (5034 * .25 - 200 * .5) * (.9 + .9 * .2) * 1.25;
        Assert(ScrewFirstHit(broken) == (int)Math.Round(baseHit * 1.25) && ScrewFirstHit(withoutSneak) == (int)Math.Round(baseHit * 1.05),
            "스닉 어택의 무방비 피해 +20%는 상대가 브레이크 상태일 때 모든 공격에 붙는다");
        // 난수 0.1이면 은신 중 상대 공격이 대상 해제 판정(65%)으로 빗나간다.
        var evaded = Duel(data, ["stealth"], maxActions: 2, random: .1).Events;
        Assert(evaded.Any(x => x.Type == "AttackEvaded" && x.Actor == "B" && x.Target == "A"), "은신 중에는 상대의 공격을 흘려낼 수 있다");
    }

    private static void BlitzRushTests(BattleDataSnapshot data)
    {
        // 첫 턴 시작에 쓰로잉 봄 충전 1 → 블리츠 러시 +1 → 다음 턴 시작 +1 = 3단계(폭탄 4개). 초기화가 없으면 2단계(3개)다.
        var turns = Turns(Duel(data, ["throwing_bomb", "blitz_rush"], maxActions: 4, initial: [("ultimate_gauge", 300)]));
        Assert(turns[0].Skill == "블리츠 러시" && turns[0].Hits == 8 && turns[1].Skill == "쓰로잉 봄" && turns[1].Hits == 4,
            "블리츠 러시는 8타를 주고 쓰로잉 봄·기습의 재사용 대기 초기화를 충전분 획득으로 반영한다");
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources, IReadOnlyList<string> Statuses, int Hits);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, int maxActions = 12, double random = .9, (string Id, int Value)[]? initial = null, (string Id, string Value)[]? rules = null, string[]? withoutPassives = null)
    {
        var ruleMap = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", maxActions.ToString()) }.Concat(rules ?? [])) ruleMap[id] = ruleMap[id] with { Value = value };
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in initial ?? []) resources[id] = resources[id] with { InitialValue = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = ruleMap,
            Classes = new Dictionary<string, BattleClass> { ["thief"] = data.Classes["thief"] with { SkillIds = skills, PassiveIds = data.Classes["thief"].PassiveIds.Except(withoutPassives ?? []).ToArray() }, ["idle"] = new("idle", "대상", Array.Empty<string>()) },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, LoadedAt = data.LoadedAt
        };
        // 도적이 항상 먼저 행동하도록 첫 난수(선공 판정)는 0으로 준다.
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "thief", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new FirstThenConstantRandom(random));
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

    public static readonly IReadOnlyDictionary<string, string> Sheets = new Dictionary<string, string>(CrossbowBattleTests.Sheets.Where(x => x.Key is "배틀규칙" or "배틀돌발이벤트" or "배틀돌발이벤트효과" or "생활스킬" or "배틀생활스킬" or "배틀생활스킬효과"))
    {
        ["클래스"] = """"
ID,이름,계열,설명,스킬1,스킬2,스킬3,스킬4,스킬5,궁극기,패시브1,패시브2,패시브3,패시브4,패시브5,패시브6
thief,도적,도적,"한 자루 단검을 자유자재로 다루며 빈틈을 파고드는 클래스. 그림자 틈에 숨어들어 흡사 바람처럼 전장을 휩쓸고 나면, 그 자리에는 아무것도 남아있지 않다. 가벼운 갑옷을 선호하며, 숨소리조차 죽여 다가간 후 가뿐하게 사라진다.",throwing_bomb,screw_dagger,ambush,poison_trap,stealth,blitz_rush,adrenaline_plus,sneak_attack,combat_mastery_swift_thief,poison_attack,poison_explosion,quick_hands
"""",
        ["스킬"] = """"
ID,이름,스킬구분,부모스킬ID,설명,태그,기타
throwing_bomb,쓰로잉 봄,일반,,"숨겨둔 폭탄을 불시에 던지는 투척 공격. 날아간 폭탄은 폭발과 함께 주변의 적들에게 범위 피해를 준다. 피격된 적은 일정 시간 동안 더 큰 피해를 받는다. 오버 차지 단계 발동 시, 폭탄의 수량과 강도가 증가한다.","강타, 방해",등급: 에픽; 강화 레벨: +28; 1단계 대미지: 27014; 2단계 대미지: 27014 × 2; 3단계 대미지: 27014 × 3; [시너지] 받는 대미지 증가: 10%; [시너지] 받는 대미지 증가 지속 시간: 15초; 재사용 대기 시간: 5초; 사거리: 12m; 범위: 3m
screw_dagger,스크류 대거,일반,,"회오리처럼 날아올라 주변 적을 연타하는 상승 공격. 적을 브레이크 시키면 공중으로 높이 떠오른다. 오버 차지 단계 발동 시, 공격 횟수가 증가하며 추가 타격이 발생한다.","연타, 방해",등급: 레어; 강화 레벨: +14; 1단계 대미지: 12692 × 3; 2단계 대미지: 22880 × 3; 2단계 추가 타격 대미지: 3173 × 3; 브레이크 대미지: 1칸; 재사용 대기 시간: 6초; 사거리: 2.5m; 범위: 2.5m
ambush,기습,일반,,"신속하게 움직여 적의 배후를 파고드는 기습 공격. 무방비한 적의 등 뒤를 노려 단검을 깊숙이 찔러 넣는다. 오버 차지 단계 발동 시, 적의 뒤로 즉시 이동하며 추가로 지속 피해: 상처를 준다.","강타, 이동, 방해",등급: 에픽; 강화 레벨: +28; 1단계 대미지: 53040; 2단계 대미지: 98174; 상처 지속 대미지: 9883; 재사용 대기 시간: 7초; 사거리: 12m
poison_trap,포이즌 트랩,일반,,"독이 가득한 주머니를 터뜨려 독 지대를 만드는 함정 공격. 독 지대는 일정 시간 동안 유지되며 범위 내의 적들에게 지속 피해: 중독을 주고, 이동 속도와 공격 속도를 감소시킨다.","연타, 방해, 소환",등급: 에픽; 강화 레벨: +26; 대미지: 0.5초당 3191; 독 지대 지속 시간: 7초; 중독 지속 대미지: 9574; 재사용 대기 시간: 12초; 사거리: 2.5m; 범위: 3.5m; 공격 속도 감소: 25%; 이동 속도 감소: 50%
stealth,은신,일반,,"그림자를 휘감아 적의 시야로부터 벗어나는 은신 기술. 범위 피해를 제외한 적의 공격으로부터 자유로워진다. 은신 상태에서 단검을 이용한 직접 공격 시, 추가 타격과 함께 타겟을 기절시킨다.","생존, 이동, 방해","등급: 레어; 강화 레벨: +14; 은신 지속 시간: 6초; 은신 추가타격 대미지: 85792; 은신 브레이크 대미지: 1칸; 추가 효과 적용: 기본 공격, 스크류 대거, 기습, 블리츠 러시; 재사용 대기 시간: 15.84초; 사거리: 12m"
blitz_rush,블리츠 러시,궁극기,,"보이지 않는 속도로 적들을 휩쓰는 비기. 바람처럼 뛰쳐나가 타겟을 벤 후, 혜성처럼 달리며 주변의 적들에게 범위 피해를 준다. 범위 공격 시, 범위 피해를 제외한 적의 공격으로부터 자유로워진다. 추가로 쓰로잉 봄, 기습의 재사용 대기 시간이 초기화된다. 『맘껏 쫓아와 봐, 따라올 수만 있다면.』","궁극기, 연타, 생존","등급: 레어; 강화 레벨: +10; 타겟 대미지: 104615; 범위 대미지: 21076 × 7; 재사용 대기 시간 초기화: 쓰로잉 봄, 기습; 궁극기 비용: 300; 사거리: 12m"
"""",
        ["패시브스킬"] = """"
ID,이름,설명,기타
adrenaline_plus,아드레날린+,"몸놀림 하나하나에 각인된 기민한 전투 감각. 쓰로잉 봄, 스크류 대거, 기습 스킬의 오버 차지 단계 발동 시, 일정 시간 동안 기본 공격이 빨라진다. 쓰로잉 봄 사용 시, 폭탄 1개를 추가로 투척한다. 공격 속도[%] 증가량에 비례하여 쓰로잉 봄 피해가 증가한다. 공격 속도[%] 증가량에 비례하여 기습 피해가 증가한다.",공격 속도 증가: 40%; 1단계 지속 시간: 4초; 2단계 지속 시간: 7초; 3단계 지속 시간: 10초; 쓰로잉 봄 투척 수 증가: 1개; 쓰로잉 봄 스킬 대미지 증가: 공격 속도 1%당 1.2%; 기습 스킬 대미지 증가: 공격 속도 1%당 1%
sneak_attack,스닉 어택,"약점을 포착한 순간 놓치지 않고 달려드는 전투 본능. 적에게 첫 번째 공격을 적중시키거나 은신 상태에서 벗어나면, 일정 시간 동안 추가타 확률이 증가한다. 적 처치 시, 기습의 재사용 대기 시간이 초기화된다. 무방비 피해가 상시 증가한다.",추가타 확률: 15%; 효과 지속 시간: 7초; 초기화 효과 발동 제한: 3초; 무방비 대미지 증가: 20%
combat_mastery_swift_thief,전투 숙련: 쾌속,"근거리에서 공격을 수행하는 숙련된 전투 기법. 기본 공격으로 받는 피해가 감소하고, 적에게 주는 연타 피해가 증가한다. 도적 클래스 레벨이 30에 도달하면 전투 시 클래스 특화 어시스트를 사용할 수 있다.",받는 기본 공격 대미지 감소: 10%; 연타 대미지 증가: 5%; 클래스 특화 어시스트 해금: 도적 클래스 레벨 30
poison_attack,포이즌 어택,"순간의 빈틈 사이로 독을 퍼뜨리는 은밀한 손놀림. 기본 공격 적중 시, 일정 확률로 지속 피해: 중독을 건다.",중독 지속 대미지: 5147; 중독 발동 확률: 기본 공격 시 35%
poison_explosion,포이즌 익스플로전,"적의 몸에 퍼진 맹독을 단번에 폭발시키는 기술. 스크류 대거, 기습 스킬 사용 시, 적이 보유한 지속 피해: 중독이 바로 폭발한다. 중독이 폭발하면 남아 있는 중독 피해를 일시에 주며, 폭발 시 주는 중독 피해가 증가한다.",중독 대미지 증가: 5%
quick_hands,퀵 핸즈,재빠른 손놀림으로 기회를 포착하는 전투 기술. 기본 공격을 세 번 사용할 때마다 모든 스킬의 재사용 대기 시간이 감소한다.,재사용 대기 시간 감소: 15%
"""",
        ["배틀스킬"] = """"
ID,활성화,행동분류,대상유형,기본쿨다운,최초쿨다운,사용우선순위,사용조건,자원유형,자원소모,자원획득,궁극기여부,배틀설명,사용문구,비고
throwing_bomb,TRUE,공격·방해,상대,1,0,60,오버 차지 1단계 이상,thief_bomb_charge,1,,FALSE,폭탄을 던져 피해를 주고 받는 피해를 늘린다. 오래 모을수록(오버 차지 1~3단계) 폭탄이 늘어난다.,{caster}가 숨겨둔 폭탄을 {target}에게 던집니다!,"원본 재사용 대기 5초→1턴, 오버 차지 단계마다 약 5초→턴마다 1단계. 1~3단계 27,014×1~3에 아드레날린+의 폭탄 1개 추가를 더해 2~4개. 도적 캡처(에픽 +28 등)는 다른 클래스보다 기준 전투력이 훨씬 높아 원본×0.22로 보정"
screw_dagger,TRUE,공격·연타,상대,1,0,60,오버 차지 1단계 이상,thief_screw_charge,1,,FALSE,회오리처럼 날아올라 연타하고 브레이크 피해를 준다. 2단계는 공격 횟수가 늘고 추가 타격이 붙는다. 적의 중독을 폭발시킨다.,{caster}가 회오리처럼 날아올라 {target}을 연타합니다!,"원본 재사용 대기 6초→턴마다 1단계. 1단계 12,692×3, 2단계 22,880×3+추가 타격 3,173×3, 브레이크 1칸. 공중으로 띄우는 연출은 생략. 도적 캡처(에픽 +28 등)는 다른 클래스보다 기준 전투력이 훨씬 높아 원본×0.22로 보정"
ambush,TRUE,공격·이동,상대,1,0,65,오버 차지 1단계 이상,thief_ambush_charge,2,,FALSE,적의 등 뒤로 파고들어 단검을 찔러 넣는다. 2단계는 더 강하게 찌르고 상처를 남긴다. 적의 중독을 폭발시킨다.,{caster}가 {target}의 배후로 파고듭니다!,"원본 재사용 대기 7초→1단계까지 2턴(비용 2), 한 턴 더 모으면 2단계. 1단계 53,040, 2단계 98,174+상처 9,883(2턴에 나눔). 도적 캡처(에픽 +28 등)는 다른 클래스보다 기준 전투력이 훨씬 높아 원본×0.22로 보정"
poison_trap,TRUE,공격·방해,상대,2,0,60,항상,궁극기 게이지,,150,FALSE,독 지대를 만들어 지속 피해와 중독을 주고 적의 움직임을 늦춘다.,{caster}가 독 주머니를 터뜨려 독 지대를 만듭니다!,"원본 재사용 대기 12초→2턴. 독 지대 0.5초마다 3,191×7초(14회)를 2턴에 나눔, 중독 9,574를 2턴에 나눔. 이동·공격 속도 감소는 쿨다운증가 1(2턴). 도적 캡처(에픽 +28 등)는 다른 클래스보다 기준 전투력이 훨씬 높아 원본×0.22로 보정"
stealth,TRUE,생존·방해,자신,3,0,55,항상,궁극기 게이지,,150,FALSE,"그림자에 숨어 적의 공격을 흘려내고, 다음 단검 공격에 추가 타격과 기절을 싣는다.",{caster}가 그림자를 휘감고 모습을 감춥니다!,"원본 재사용 대기 15.84초→3턴, 은신 6초→다음 행동까지. 추가 타격 85,792·브레이크 1칸은 스크류 대거·기습·블리츠 러시의 은신 행으로 반영(기본 공격은 드물어 제외). 도적 캡처(에픽 +28 등)는 다른 클래스보다 기준 전투력이 훨씬 높아 원본×0.22로 보정"
blitz_rush,TRUE,궁극기·연타,상대,0,0,100,궁극기 게이지 300,궁극기 게이지,300,,TRUE,보이지 않는 속도로 적을 베고 휩쓴 뒤 쓰로잉 봄·기습을 다시 준비한다.,{caster}가 바람처럼 뛰쳐나가 {target}을 휩씁니다!,"원본 타겟 104,615 + 범위 21,076×7은 기본기보다 수 배 커서 원본×0.12로 보정. 쓰로잉 봄·기습 재사용 대기 초기화는 각 1단계 충전분(+1·+2) 즉시 획득으로 반영. 범위 공격 중 무적은 사용 중 연출이라 생략."
"""",
        ["배틀스킬효과"] = """"
ID,스킬ID,실행순서,효과유형,대상,계수기준,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,비고,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,연속치명타배율
throwing_bomb_01,throwing_bomb,1,피해,상대,,5943,4,1,0,,0,3단계 오버 차지! 폭탄 4개가 {target}에게 쏟아져 총 {damage}의 피해!,"3단계 27,014×3 + 아드레날린+ 1개. 원본×0.22",자신,자원보유,thief_bomb_charge,>=,2,,,
throwing_bomb_02,throwing_bomb,2,자원설정,자신,,3,1,1,0,thief_overcharge,0,,이번 오버 차지 단계(아드레날린+ 지속 시간 판정),자신,자원보유,thief_bomb_charge,>=,2,,,
throwing_bomb_03,throwing_bomb,3,자원소모,자신,,0,1,1,0,thief_bomb_charge,0,,남은 충전 소모(아래 단계 행이 다시 실행되지 않도록),자신,자원보유,thief_bomb_charge,>=,2,,전부,
throwing_bomb_04,throwing_bomb,4,피해,상대,,5943,3,1,0,,0,2단계 오버 차지! 폭탄 3개가 {target}에게 총 {damage}의 피해!,"2단계 27,014×2 + 아드레날린+ 1개. 원본×0.22",자신,자원보유,thief_bomb_charge,>=,1,,,
throwing_bomb_05,throwing_bomb,5,자원설정,자신,,2,1,1,0,thief_overcharge,0,,,자신,자원보유,thief_bomb_charge,>=,1,,,
throwing_bomb_06,throwing_bomb,6,자원소모,자신,,0,1,1,0,thief_bomb_charge,0,,,자신,자원보유,thief_bomb_charge,>=,1,,전부,
throwing_bomb_07,throwing_bomb,7,피해,상대,,5943,2,1,0,,0,폭탄 2개가 {target}에게 총 {damage}의 피해!,"1단계 27,014 + 아드레날린+ 1개. 원본×0.22",자신,자원보유,thief_overcharge,<=,0,,,
throwing_bomb_08,throwing_bomb,8,자원설정,자신,,1,1,1,0,thief_overcharge,0,,,자신,자원보유,thief_overcharge,<=,0,,,
throwing_bomb_09,throwing_bomb,9,상태효과,상대,,0,1,1,3,thief_bomb_vulnerable,1,폭발 여파로 {target}이 받는 피해가 증가합니다.,"[시너지] 받는 대미지 증가 10%, 15초→3턴",,,,,,,,
throwing_bomb_10,throwing_bomb,10,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,오버 차지 자원을 비용으로 쓰는 스킬이라 궁극기 게이지는 효과 행으로 적립(석궁사수 소모 스킬과 같은 방식),,,,,,,,
screw_dagger_01,screw_dagger,1,피해,상대,,18874,1,1,0,,0,그림자 속에서 날아든 일격! {target}에게 {damage}의 피해!,"은신 추가 타격 원본 85,792×0.22",자신,자원보유,thief_stealth,>=,1,,,
screw_dagger_02,screw_dagger,2,브레이크피해,상대,,1,1,1,0,,0,{target}이 기습에 휘청입니다!,은신 공격의 기절은 스턴 브레이크라 브레이크 대미지 1칸,자신,자원보유,thief_stealth,>=,1,,,
screw_dagger_03,screw_dagger,3,자원소모,자신,,0,1,1,0,thief_stealth,0,,은신에서 벗어남(은신 회피 해제·스닉 어택 발동),자신,자원보유,thief_stealth,>=,1,,전부,
screw_dagger_04,screw_dagger,4,피해,상대,,5034,3,1,0,,0,2단계 오버 차지! 회오리 연타로 {target}에게 총 {damage}의 피해!,"2단계 22,880×3. 원본×0.22",자신,자원보유,thief_screw_charge,>=,1,,,
screw_dagger_05,screw_dagger,5,피해,상대,,698,3,1,0,,0,이어지는 추가 타격으로 총 {damage}의 피해!,"2단계 추가 타격 3,173×3. 원본×0.22",자신,자원보유,thief_screw_charge,>=,1,,,
screw_dagger_06,screw_dagger,6,자원설정,자신,,2,1,1,0,thief_overcharge,0,,,자신,자원보유,thief_screw_charge,>=,1,,,
screw_dagger_07,screw_dagger,7,자원소모,자신,,0,1,1,0,thief_screw_charge,0,,,자신,자원보유,thief_screw_charge,>=,1,,전부,
screw_dagger_08,screw_dagger,8,피해,상대,,2792,3,1,0,,0,회오리 연타로 {target}에게 총 {damage}의 피해!,"1단계 12,692×3. 원본×0.22",자신,자원보유,thief_overcharge,<=,0,,,
screw_dagger_09,screw_dagger,9,자원설정,자신,,1,1,1,0,thief_overcharge,0,,,자신,자원보유,thief_overcharge,<=,0,,,
screw_dagger_10,screw_dagger,10,브레이크피해,상대,,1,1,1,0,,0,{target}의 균형이 무너집니다!,브레이크 대미지 1칸,,,,,,,,
screw_dagger_11,screw_dagger,11,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,오버 차지 자원을 비용으로 쓰는 스킬이라 궁극기 게이지는 효과 행으로 적립(석궁사수 소모 스킬과 같은 방식),,,,,,,,
ambush_01,ambush,1,피해,상대,,18874,1,1,0,,0,그림자 속에서 날아든 일격! {target}에게 {damage}의 피해!,"은신 추가 타격 원본 85,792×0.22",자신,자원보유,thief_stealth,>=,1,,,
ambush_02,ambush,2,브레이크피해,상대,,1,1,1,0,,0,{target}이 기습에 휘청입니다!,은신 공격의 기절은 스턴 브레이크라 브레이크 대미지 1칸,자신,자원보유,thief_stealth,>=,1,,,
ambush_03,ambush,3,자원소모,자신,,0,1,1,0,thief_stealth,0,,은신에서 벗어남(은신 회피 해제·스닉 어택 발동),자신,자원보유,thief_stealth,>=,1,,전부,
ambush_04,ambush,4,피해,상대,,21598,1,1,0,,0,2단계 오버 차지! {target}의 등 뒤로 순식간에 파고들어 {damage}의 피해!,"2단계 98,174. 원본×0.22",자신,자원보유,thief_ambush_charge,>=,1,,,
ambush_05,ambush,5,지속피해,상대,,1087,1,1,2,thief_wound,1,{target}에게 깊은 상처가 남습니다.,"2단계 상처 지속 대미지 9,883을 2턴에 나눔(힐러 두려움과 같은 방식). 원본×0.22",자신,자원보유,thief_ambush_charge,>=,1,,,
ambush_06,ambush,6,자원설정,자신,,2,1,1,0,thief_overcharge,0,,,자신,자원보유,thief_ambush_charge,>=,1,,,
ambush_07,ambush,7,자원소모,자신,,0,1,1,0,thief_ambush_charge,0,,,자신,자원보유,thief_ambush_charge,>=,1,,전부,
ambush_08,ambush,8,피해,상대,,11669,1,1,0,,0,{target}의 등 뒤에 단검을 깊숙이 찔러 {damage}의 피해!,"1단계 53,040. 원본×0.22",자신,자원보유,thief_overcharge,<=,0,,,
ambush_09,ambush,9,자원설정,자신,,1,1,1,0,thief_overcharge,0,,,자신,자원보유,thief_overcharge,<=,0,,,
ambush_10,ambush,10,자원증가,자신,,150,1,1,0,ultimate_gauge,0,,오버 차지 자원을 비용으로 쓰는 스킬이라 궁극기 게이지는 효과 행으로 적립(석궁사수 소모 스킬과 같은 방식),,,,,,,,
poison_trap_01,poison_trap,1,지속피해,상대,,4914,1,1,2,thief_poison_zone,1,독 지대가 {target}을 뒤덮습니다.,"원본 0.5초마다 3,191×7초(14회)=44,674를 2턴에 나눔. 원본×0.22",,,,,,,,
poison_trap_02,poison_trap,2,지속피해,상대,,1053,1,1,2,thief_poison,1,{target}이 중독되었습니다.,"중독 지속 대미지 9,574를 2턴에 나눔. 원본×0.22",,,,,,,,
poison_trap_03,poison_trap,3,상태효과,상대,,0,1,1,2,thief_poison_slow,1,독 기운에 {target}의 움직임이 느려집니다.,"이동 속도 -50%·공격 속도 -25%를 쿨다운증가 1로 단순화, 독 지대 7초→2턴",,,,,,,,
stealth_01,stealth,1,자원설정,자신,,1,1,1,0,thief_stealth,0,,은신 6초→다음 행동까지,,,,,,,,
stealth_02,stealth,2,상태효과,자신,,0,1,1,1,thief_stealth_evade,1,{caster}의 모습이 그림자 속으로 사라집니다.,범위 피해를 제외한 공격으로부터 자유로움 → 대상 해제 회피. 은신이 풀리면 함께 해제,,,,,,,,
blitz_rush_01,blitz_rush,1,피해,상대,,18874,1,1,0,,0,그림자 속에서 날아든 일격! {target}에게 {damage}의 피해!,"은신 추가 타격 원본 85,792×0.22",자신,자원보유,thief_stealth,>=,1,,,
blitz_rush_02,blitz_rush,2,브레이크피해,상대,,1,1,1,0,,0,{target}이 기습에 휘청입니다!,은신 공격의 기절은 스턴 브레이크라 브레이크 대미지 1칸,자신,자원보유,thief_stealth,>=,1,,,
blitz_rush_03,blitz_rush,3,자원소모,자신,,0,1,1,0,thief_stealth,0,,은신에서 벗어남(은신 회피 해제·스닉 어택 발동),자신,자원보유,thief_stealth,>=,1,,전부,
blitz_rush_04,blitz_rush,4,피해,상대,,12554,1,1,0,,0,눈에 보이지 않는 일격이 {target}을 베어 {damage}의 피해!,"타겟 대미지 104,615. 원본×0.12",,,,,,,,
blitz_rush_05,blitz_rush,5,피해,상대,,2529,7,1,0,,0,혜성처럼 휩쓸며 {target}에게 총 {damage}의 피해!,"범위 대미지 21,076×7을 단일 대상에 적용. 원본×0.12",,,,,,,,
blitz_rush_06,blitz_rush,6,자원증가,자신,,1,1,1,0,thief_bomb_charge,0,,쓰로잉 봄 재사용 대기 초기화 → 1단계 충전분 즉시 획득,,,,,,,,
blitz_rush_07,blitz_rush,7,자원증가,자신,,2,1,1,0,thief_ambush_charge,0,,기습 재사용 대기 초기화 → 1단계 충전분(2) 즉시 획득,,,,,,,,
"""",
        ["배틀스킬파생"] = """"
ID,부모스킬ID,파생스킬ID,발동방식,가중치,발동확률,조건유형,조건값,중복허용,실행시점,비고,우선순위
fixture_placeholder,throwing_bomb,screw_dagger,대체,1,1,자원보유,thief_sneak_ready=2,FALSE,즉시,테스트 자리표시(도적은 파생이 없다. 행이 없으면 헤더를 읽지 못한다),0
"""",
        ["배틀패시브"] = """"
ID,활성화,비고
adrenaline_plus,TRUE,"오버 차지 단계 발동(쓰로잉 봄·스크류 대거·기습) 뒤 기본 공격 속도 +40%는 일반 공격 피해 +40%로, 공격 속도 비례 피해 증가는 쓰로잉 봄 +48%·기습 +40%로 같은 지속 동안 반영. 단계별 4·7·10초→1·2·2턴. 쓰로잉 봄 폭탄 1개 추가는 쓰로잉 봄 피해 행 횟수에 포함. 도적 스킬 공통의 오버 차지 충전(턴마다 +1) 영구 상태도 이 패시브의 전투시작 행으로 건다."
sneak_attack,TRUE,첫 공격 적중(1:1이라 전투당 1회)과 은신 해제 시 추가타 확률 +15%(7초→2턴). 적 처치 시 기습 초기화는 1:1에서 처치가 곧 전투 종료라 제외. 무방비 피해 +20%는 브레이크 상태를 무방비로 보고 상대가 브레이크 상태일 때 모든 공격에 적용(2026-09-27 사용자 확인).
combat_mastery_swift_thief,TRUE,"받는 기본 공격 피해 -10%, 연타 피해 +5%. 댄서 전투 숙련: 쾌속과 같은 상태를 쓴다. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외(항상 만렙 가정)."
poison_attack,TRUE,"기본 공격 적중 시 35% 중독 5,147(2턴에 나눔). 도적은 스킬 쿨다운이 짧아 일반 공격을 거의 하지 않으므로 피해를 준 스킬 턴에도 기본 공격을 섞어 친 것으로 보고 스킬적중완료시에도 판정(2026-09-27 사용자 확인)."
poison_explosion,TRUE,"스크류 대거·기습 사용 완료 시 적의 중독(포이즌 트랩·포이즌 어택)을 지속피해폭발로 남은 틱 피해를 한 번에 주고 해제, 폭발 피해 +5%. 독 지대 피해는 중독이 아니라 제외."
quick_hands,TRUE,"기본 공격 3회마다 모든 스킬 쿨다운 15% 감소를 쿨다운 1턴 감소로 근사. 피해를 준 스킬 턴도 기본 공격을 섞어 친 것으로 보고 센다(포이즌 어택과 같은 해석, 2026-09-27 사용자 확인). 오버 차지 스킬은 충전 자원이 사용 시점을 정하므로 실질적으로 포이즌 트랩·은신에만 영향."
"""",
        ["배틀패시브효과"] = """"
ID,패시브ID,실행순서,효과유형,대상,고정값,횟수,발동확률,지속턴,상태효과ID,최대중첩,효과문구,조건대상,조건유형,조건ID,조건연산자,조건값,수치참조ID,수치참조방식,발동시점,대상스킬ID,대상자원ID,비고,재발동대기턴
ap_01,adrenaline_plus,1,턴당자원증가,자신,0,1,1,0,thief_charge_bomb,0,,,,,,,,,전투시작,,,도적 스킬 공통 오버 차지 충전(쓰로잉 봄),
ap_02,adrenaline_plus,2,턴당자원증가,자신,0,1,1,0,thief_charge_screw,0,,,,,,,,,전투시작,,,오버 차지 충전(스크류 대거),
ap_03,adrenaline_plus,3,턴당자원증가,자신,0,1,1,0,thief_charge_ambush,0,,,,,,,,,전투시작,,,오버 차지 충전(기습),
ap_04,adrenaline_plus,4,상태효과,자신,0,1,1,2,thief_adrenaline,0,아드레날린이 솟구칩니다!,자신,자원보유,thief_overcharge,>=,2,,,스킬사용완료시,,,"2·3단계 7·10초→2턴, 공격 속도 +40%",
ap_05,adrenaline_plus,5,상태효과,자신,0,1,1,2,thief_adrenaline_bomb,0,,자신,자원보유,thief_overcharge,>=,2,,,스킬사용완료시,,,,
ap_06,adrenaline_plus,6,상태효과,자신,0,1,1,2,thief_adrenaline_ambush,0,,자신,자원보유,thief_overcharge,>=,2,,,스킬사용완료시,,,,
ap_07,adrenaline_plus,7,상태효과,자신,0,1,1,1,thief_adrenaline,0,,자신,자원보유,thief_overcharge,>=,1,,,스킬사용완료시,,,1단계 4초→1턴(2·3단계면 위 2턴이 유지됨),
ap_08,adrenaline_plus,8,상태효과,자신,0,1,1,1,thief_adrenaline_bomb,0,,자신,자원보유,thief_overcharge,>=,1,,,스킬사용완료시,,,,
ap_09,adrenaline_plus,9,상태효과,자신,0,1,1,1,thief_adrenaline_ambush,0,,자신,자원보유,thief_overcharge,>=,1,,,스킬사용완료시,,,,
ap_10,adrenaline_plus,10,자원소모,자신,0,1,1,0,thief_overcharge,0,,자신,자원보유,thief_overcharge,>=,1,,전부,스킬사용완료시,,,,
sa_01,sneak_attack,1,자원설정,자신,1,1,1,0,thief_sneak_ready,0,,,,,,,,,전투시작,,,적에게 첫 번째 공격(1:1이라 전투당 1회),
sa_02,sneak_attack,2,무방비피해증가,자신,0,1,1,0,thief_unguarded,0,,,,,,,,,전투시작,,,무방비 대미지 +20%(상대가 브레이크 상태일 때),
sa_03,sneak_attack,3,상태효과,자신,0,1,1,2,thief_sneak_attack,0,약점을 포착했습니다!,자신,자원보유,thief_sneak_ready,>=,1,,,스킬적중완료시,,,"첫 공격 적중: 추가타 확률 +15%, 7초→2턴",
sa_04,sneak_attack,4,자원소모,자신,0,1,1,0,thief_sneak_ready,0,,자신,자원보유,thief_sneak_ready,>=,1,,전부,스킬적중완료시,,,,
sa_05,sneak_attack,5,상태효과,자신,0,1,1,2,thief_sneak_attack,0,,자신,자원보유,thief_sneak_ready,>=,1,,,기본공격적중시,,,,
sa_06,sneak_attack,6,자원소모,자신,0,1,1,0,thief_sneak_ready,0,,자신,자원보유,thief_sneak_ready,>=,1,,전부,기본공격적중시,,,,
sa_07,sneak_attack,7,상태효과,자신,0,1,1,2,thief_sneak_attack,0,,,,,,,,,자원소진시,,thief_stealth,은신 상태에서 벗어나면(공격·만료),
cmst_01,combat_mastery_swift_thief,1,받는기본공격피해감소,자신,0,1,1,0,combat_mastery_swift_guard,0,,,,,,,,,전투시작,,,받는 기본 공격 대미지 -10%,
cmst_02,combat_mastery_swift_thief,2,멀티히트피해증가,자신,0,1,1,0,combat_mastery_swift_multi,0,,,,,,,,,전투시작,,,연타 대미지 +5%,
pa_01,poison_attack,1,지속피해,상대,566,1,0.35,2,thief_poison_attack,0,독 묻은 손놀림이 {target}을 중독시킵니다.,,,,,,,,스킬적중완료시,,,"스킬 턴의 기본 공격으로 해석. 중독 5,147을 2턴에 나눔. 원본×0.22",
pa_02,poison_attack,2,지속피해,상대,566,1,0.35,2,thief_poison_attack,0,독 묻은 손놀림이 {target}을 중독시킵니다.,,,,,,,,기본공격적중시,,,기본 공격 적중 시 35%,
pe_01,poison_explosion,1,지속피해폭발,상대,5,1,1,0,thief_poison,0,,,,,,,,,스킬사용완료시,screw_dagger,,"남은 중독 피해를 한 번에, 폭발 피해 +5%",
pe_02,poison_explosion,2,지속피해폭발,상대,5,1,1,0,thief_poison_attack,0,,,,,,,,,스킬사용완료시,screw_dagger,,,
pe_03,poison_explosion,3,지속피해폭발,상대,5,1,1,0,thief_poison,0,,,,,,,,,스킬사용완료시,ambush,,,
pe_04,poison_explosion,4,지속피해폭발,상대,5,1,1,0,thief_poison_attack,0,,,,,,,,,스킬사용완료시,ambush,,,
qh_01,quick_hands,1,자원증가,자신,1,1,1,0,thief_quick_hands,0,,,,,,,,,스킬적중완료시,,,스킬 턴의 기본 공격으로 해석,
qh_02,quick_hands,2,자원증가,자신,1,1,1,0,thief_quick_hands,0,,,,,,,,,기본공격적중시,,,,
qh_03,quick_hands,3,쿨다운감소,자신,1,1,1,0,,0,재빠른 손놀림으로 스킬을 다시 준비합니다.,,,,,,,,자원최대치도달시,,thief_quick_hands,모든 스킬 재사용 대기 15% 감소를 1턴으로 근사,
qh_04,quick_hands,4,자원소모,자신,0,1,1,0,thief_quick_hands,0,,,,,,,,전부,자원최대치도달시,,thief_quick_hands,,
"""",
        ["배틀자원"] = """"
ID,이름,분류,최대값,초기값,지속턴,중첩방식,전투종료시제거,설명,로그숨김
ultimate_gauge,궁극기 게이지,궁극기 게이지,300,0,0,가산,TRUE,모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다.,
thief_bomb_charge,쓰로잉 봄 오버 차지,오버차지,3,0,0,가산,TRUE,"쓰로잉 봄 충전 단계. 오버 차지 충전(thief_charge_bomb)으로 도적의 턴 시작마다 +1(원본 단계마다 약 5초, 6초=1턴), 최대 3단계. 사용 시 1을 비용으로 내고 남은 양으로 2·3단계를 판정한 뒤 모두 소모한다. 초기값 0이라 첫 턴 시작에 1단계가 된다.",TRUE
thief_screw_charge,스크류 대거 오버 차지,오버차지,2,0,0,가산,TRUE,"스크류 대거 충전 단계(최대 2단계, 원본 재사용 6초→턴마다 +1). 사용 시 1을 비용으로 내고 남은 양이 있으면 2단계. 초기값 0이라 첫 턴 시작에 1단계가 된다.",TRUE
thief_ambush_charge,기습 오버 차지,오버차지,3,1,0,가산,TRUE,"기습 충전량. 원본 재사용 7초라 1단계에 2턴이 걸리도록 비용 2, 한 턴 더 기다리면(3) 2단계. 초기값 1이라 첫 턴 시작에 1단계(2)가 된다.",TRUE
thief_overcharge,오버 차지 단계,자원,3,0,0,가산,TRUE,쓰로잉 봄·스크류 대거·기습이 이번에 발동한 오버 차지 단계(1~3). 스킬 사용 완료 시 아드레날린+가 단계별 지속 시간으로 읽고 모두 소모한다.,
thief_stealth,은신,자원,1,0,1,교체,TRUE,은신 상태(원본 6초→다음 행동까지). 스크류 대거·기습·블리츠 러시가 은신 추가 타격과 기절(브레이크 1칸)을 주고 소모한다. 0이 되면(공격·만료) 은신 회피(thief_stealth_evade)가 풀리고 스닉 어택이 발동한다.,TRUE
thief_sneak_ready,스닉 어택 준비,자원,1,0,0,교체,TRUE,전투 첫 공격 적중을 판정하는 표식(1:1이라 전투당 1회).,TRUE
thief_quick_hands,퀵 핸즈,중첩,3,0,0,가산,TRUE,"기본 공격 횟수. 도적은 자동전투에서 스킬 쿨다운이 짧아 일반 공격을 거의 하지 않으므로, 피해를 준 스킬 턴에도 기본 공격을 섞어 친 것으로 보고 +1(2026-09-27 사용자 확인). 3이 되면 모든 스킬 쿨다운 1턴 감소 후 초기화.",TRUE
"""",
        ["배틀상태효과"] = """"
ID,이름,효과유형,값,설명,대상스킬ID,중첩자원ID,시너지,지속방식,효과별값,대상자원ID,유지자원ID,로그숨김
break_broken,브레이크,브레이크|받는피해증가,0.25,브레이크 시 다음 행동을 잃고 받는 피해 +25%,,,,,,,,
combat_mastery_swift_guard,전투 숙련: 쾌속,받는기본공격피해감소,0.1,상대 일반 공격으로 받는 피해 -10%. 레벨 30 어시스트 해금 문구는 구현 범위에서 제외,,,,,,,,
combat_mastery_swift_multi,전투 숙련: 쾌속,멀티히트피해증가,0.05,적에게 주는 연타(다단) 피해 +5%,,,,,,,,
thief_charge_bomb,오버 차지 충전: 쓰로잉 봄,턴당자원증가,1,전투 시작에 거는 영구 상태. 도적의 턴 시작마다 쓰로잉 봄 오버 차지 +1(행동을 잃는 턴에도).,,,,,,thief_bomb_charge,,TRUE
thief_charge_screw,오버 차지 충전: 스크류 대거,턴당자원증가,1,전투 시작에 거는 영구 상태. 도적의 턴 시작마다 스크류 대거 오버 차지 +1.,,,,,,thief_screw_charge,,TRUE
thief_charge_ambush,오버 차지 충전: 기습,턴당자원증가,1,전투 시작에 거는 영구 상태. 도적의 턴 시작마다 기습 오버 차지 +1.,,,,,,thief_ambush_charge,,TRUE
thief_bomb_vulnerable,폭발 여파,받는피해증가,0.1,쓰로잉 봄에 맞은 적의 [시너지] 받는 대미지 증가 10%(15초→3턴).,,,받는피해증가,,,,,
thief_wound,상처,없음,0,기습 2단계의 지속 피해.,,,,,,,,
thief_poison_zone,독 지대,없음,0,포이즌 트랩 독 지대의 지속 피해(중독과 별개라 포이즌 익스플로전으로 폭발하지 않는다).,,,,,,,,
thief_poison,중독,없음,0,포이즌 트랩의 지속 피해: 중독. 스크류 대거·기습 사용 시 포이즌 익스플로전으로 폭발한다.,,,,,,,,
thief_poison_attack,중독,없음,0,포이즌 어택의 지속 피해: 중독(포이즌 트랩 중독과 따로 걸리도록 별도 ID). 포이즌 익스플로전으로 폭발한다.,,,,,,,,
thief_poison_slow,둔화,쿨다운증가,1,독 지대의 이동 속도 -50%·공격 속도 -25%를 석궁사수 둔화와 같은 쿨다운증가 1로 단순화(7초→2턴).,,,,,,,,
thief_stealth_evade,은신,대상해제,0,범위 피해를 제외한 적의 공격으로부터 자유로워짐을 죽은 척과 같은 대상 해제 회피 판정으로 단순화. 은신(thief_stealth)이 0이 되면 해제된다.,,,,,,,thief_stealth,
thief_adrenaline,아드레날린,기본공격피해증가,0.4,오버 차지 발동 후 기본 공격 속도 +40%를 일반 공격 피해 +40%로 단순화.,,,,,,,,
thief_adrenaline_bomb,아드레날린: 쓰로잉 봄,스킬피해증가,0.48,아드레날린의 공격 속도 +40% × 1.2%/1% = 쓰로잉 봄 피해 +48%.,throwing_bomb,,,,,,,TRUE
thief_adrenaline_ambush,아드레날린: 기습,스킬피해증가,0.4,아드레날린의 공격 속도 +40% × 1%/1% = 기습 피해 +40%.,ambush,,,,,,,TRUE
thief_sneak_attack,스닉 어택,추가타확률증가,0.15,첫 공격 적중·은신 해제 시 추가타 확률 +15%(7초→2턴).,,,,,,,,
thief_unguarded,스닉 어택: 무방비,무방비피해증가,0.2,"무방비 피해 +20%(상시). 공식 가이드의 무방비 중 배틀에서는 브레이크 상태만 무방비로 보고, 상대가 브레이크 상태일 때 모든 공격 피해 +20%(2026-09-27 사용자 확인).",,,,,,,,
"""",
    };
}
