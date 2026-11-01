using Molly.Battle;

/// <summary>
/// 석궁사수 패시브(드라이빙 포스·포츈 볼트·퀵 어택) 흐름을 실제 시트 행으로 검사한다(GitHub Issue #7).
/// Sheets는 2026-09-25 배틀 시트에서 석궁사수 행만 그대로 추린 사본이다. 시트의 석궁사수 행을 바꾸면 이 사본도 함께 갱신한다.
/// 테스트마다 클래스의 스킬 구성과 시작 자원만 바꿔 행동 순서를 고정하고, 효과·패시브·자원·상태 행은 건드리지 않는다(배틀스킬파생의 자리표시 행만 예외).
/// </summary>
internal static class CrossbowBattleTests
{
    public static void Run()
    {
        var data = BattleCatalog.Parse(Sheets, DateTimeOffset.UtcNow, BattleTestGame.Data);
        DrivingForceTests(data);
        FortuneBoltTests(data);
        QuickAttackTests(data);
    }

    private static void DrivingForceTests(BattleDataSnapshot data)
    {
        // 난수 0.9: 치명타·포츈 볼트(67%) 판정은 모두 실패하고, 스킬은 후보 목록의 마지막 쪽이 선택된다.
        // 거스팅·스프레딩 순서면 스프레딩→거스팅→스프레딩, 반대 순서면 거스팅→스프레딩→거스팅으로 탄창 3개를 쓴다.
        var spreadingFirst = Turns(Duel(data, ["Arbalist_GustingVolt", "Arbalist_SpreadingVolt"], .9, magazine: 3));
        var gustingFirst = Turns(Duel(data, ["Arbalist_SpreadingVolt", "Arbalist_GustingVolt"], .9, magazine: 3));
        Assert(spreadingFirst.Take(3).Select(x => x.Skill).SequenceEqual(["스프레딩 볼트", "거스팅 볼트", "스프레딩 볼트"]) && gustingFirst.Take(3).Select(x => x.Skill).SequenceEqual(["거스팅 볼트", "스프레딩 볼트", "거스팅 볼트"]),
            "석궁사수 시나리오 전제: 탄창 3개로 거스팅·스프레딩 볼트를 번갈아 3연속 사용한다");
        Assert(Near(spreadingFirst[2].Damage, spreadingFirst[0].Damage * 1.6) && Near(gustingFirst[2].Damage, gustingFirst[0].Damage * 1.6)
            && Near(spreadingFirst[1].Damage, gustingFirst[0].Damage * 1.3) && Near(gustingFirst[1].Damage, spreadingFirst[0].Damage * 1.3),
            "드라이빙 포스는 연속 조건을 충족한 볼트 자신부터 적용되어 두 번째 볼트 +30%, 세 번째 볼트 +60%다");

        // 거스팅→스프레딩→헬 파이어: 드라이빙 포스 중첩이 있어도 헬 파이어 피해는 강화되지 않는다.
        var hellAfterBolts = Turns(Duel(data, ["Arbalist_GustingVolt", "Arbalist_SpreadingVolt", "Arbalist_BoltExplosion"], .9, magazine: 2));
        var hellAlone = Turns(Duel(data, ["Arbalist_GustingVolt", "Arbalist_SpreadingVolt", "Arbalist_BoltExplosion"], .9, gauge: 300));
        Assert(hellAfterBolts.Take(3).Select(x => x.Skill).SequenceEqual(["스프레딩 볼트", "거스팅 볼트", "헬 파이어"]) && hellAlone[0].Skill == "헬 파이어"
            && hellAfterBolts[2].Resources.Any(x => x.StartsWith("드라이빙 포스 ")) == false && hellAfterBolts[1].Resources.Contains("드라이빙 포스 +1 (현재 2)")
            && hellAfterBolts[2].Damage == hellAlone[0].Damage,
            "드라이빙 포스 2중첩 상태의 헬 파이어는 중첩 없는 헬 파이어와 피해가 같다(볼트 전용 강화)");

        // 거스팅→버스터 샷→거스팅: 사이에 장전 스킬이 끼면 중첩이 사라져 두 번째 거스팅은 강화되지 않는다.
        var interrupted = Turns(Duel(data, ["Arbalist_MountingShock", "Arbalist_GustingVolt"], .9, magazine: 1));
        Assert(interrupted.Take(3).Select(x => x.Skill).SequenceEqual(["거스팅 볼트", "버스터 샷", "거스팅 볼트"])
            && interrupted[1].Resources.Contains("드라이빙 포스이(가) 사라졌습니다.") && interrupted[2].Damage == interrupted[0].Damage
            && interrupted[1].Damage > 0,
            "소모 볼트 사이에 장전 스킬을 쓰면 드라이빙 포스 중첩이 끝나고, 장전 스킬 자체도 강화되지 않는다");
    }

    private static void FortuneBoltTests(BattleDataSnapshot data)
    {
        // 판정 실패(난수 0.9): 3연속 뒤 연속 횟수만 초기화되고, 탄창이 없어 다음 행동은 일반 공격이다.
        var failed = Turns(Duel(data, ["Arbalist_GustingVolt", "Arbalist_SpreadingVolt"], .9, magazine: 3));
        Assert(failed[0].Resources.Contains("포츈 볼트 연속 사용 +1 (현재 1)") && failed[1].Resources.Contains("포츈 볼트 연속 사용 +1 (현재 2)")
            && failed[2].Resources.Contains("포츈 볼트 연속 사용 -3 (현재 0)") && !failed.Take(3).Any(x => x.Resources.Any(r => r.StartsWith("포츈 볼트(다음 행동 무료 발사)")))
            && failed[3].Skill == "(일반 공격)",
            "포츈 볼트 판정(67%)에 실패하면 추가 발동 없이 연속 횟수만 초기화된다");

        // 판정 성공(난수 0.5): 세 번째 볼트 뒤 다른 볼트가 다음 행동으로 지정되고, 탄창 0에서도 탄창 없이 발사된다.
        var success = Turns(Duel(data, ["Arbalist_GustingVolt", "Arbalist_SpreadingVolt"], .5, magazine: 3));
        var (third, free) = (success[2], success[3]);
        Assert(success.Take(4).Select(x => x.Skill).SequenceEqual(["거스팅 볼트", "스프레딩 볼트", "거스팅 볼트", "스프레딩 볼트"]),
            "석궁사수 시나리오 전제: 3연속 사용 뒤 포츈 볼트가 반대 볼트를 다음 행동으로 지정한다");
        Assert(third.Resources.Contains("포츈 볼트(다음 행동 무료 발사) +1 (현재 1)") && third.Resources.Contains("강화 볼트 탄창 +1 (현재 1)")
            && third.Resources.Contains("드라이빙 포스 -2 (현재 0)") && third.Resources.Contains("포츈 볼트 연속 사용 -3 (현재 0)"),
            "포츈 볼트 성공 시 무료 발사 표식·탄창 1개 보충·드라이빙 포스 제거·연속 횟수 초기화가 함께 일어난다");
        Assert(free.Resources.SequenceEqual(["강화 볼트 탄창 -1 (현재 0)", "포츈 볼트(다음 행동 무료 발사) -1 (현재 0)"])
            && Near(free.Damage * 1.3, success[1].Damage),
            "포츈 볼트 추가 발동은 탄창 순소모 없이 발사되고 궁극기 게이지·드라이빙 포스·연속 횟수를 얻지 않으며 드라이빙 포스 강화도 받지 않는다");
        Assert(success[4].Skill == "(일반 공격)",
            "포츈 볼트 추가 발동 뒤에는 탄창이 0이라 다음 행동에서 소모 볼트를 쓸 수 없다");

        // 연속 1회: 사이에 장전 스킬이 끼면 연속 횟수가 만료되어 다시 1부터 센다.
        var reset = Turns(Duel(data, ["Arbalist_MountingShock", "Arbalist_GustingVolt"], .9, magazine: 1));
        Assert(reset[1].Resources.Contains("포츈 볼트 연속 사용이(가) 사라졌습니다.") && reset[2].Resources.Contains("포츈 볼트 연속 사용 +1 (현재 1)"),
            "포츈 볼트 연속 횟수는 다음 행동에 소모 볼트를 쓰지 않으면 만료된다");
    }

    private static void QuickAttackTests(BattleDataSnapshot data)
    {
        // 탄창이 이미 가득(3) 찬 상태의 장전 스킬도 "장전 동작"으로 보고 퀵 어택을 쌓는다.
        var full = Turns(Duel(data, ["Arbalist_MountingShock", "Arbalist_ShockExplosion"], .9, magazine: 3));
        Assert(full[0].Resources.Contains("퀵 어택 +1 (현재 1)") && !full[0].Resources.Any(x => x.StartsWith("강화 볼트 탄창")),
            "퀵 어택은 장전 스킬 사용 완료 기준이라 탄창이 가득 차 실제 장전량이 0이어도 쌓인다");
    }

    private sealed record Turn(string Skill, int Damage, IReadOnlyList<string> Resources);

    private static BattleResult Duel(BattleDataSnapshot data, string[] skills, double randomValue, int magazine = 0, int gauge = 0)
    {
        var resources = data.Resources.ToDictionary(x => x.Key, x => x.Value);
        resources["cb_bolt_magazine"] = resources["cb_bolt_magazine"] with { InitialValue = magazine };
        resources["ultimate_gauge"] = resources["ultimate_gauge"] with { InitialValue = gauge };
        var rules = data.Rules.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, value) in new[] { ("base_max_hp", "10000000"), ("max_surprise_events_per_actor", "0"), ("max_major_actions", "12") }) rules[id] = rules[id] with { Value = value };
        var snapshot = new BattleDataSnapshot
        {
            Rules = rules,
            Classes = new Dictionary<string, BattleClass> { ["crossbowman"] = data.Classes["crossbowman"] with { SkillIds = skills }, ["idle"] = new("idle", "대상", Array.Empty<string>()) },
            Skills = data.Skills, Passives = data.Passives, Resources = resources, Statuses = data.Statuses, Derivations = data.Derivations, LoadedAt = data.LoadedAt
        };
        return new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "crossbowman", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "idle", 100, 0, 0), snapshot, new ConstantRandom(randomValue));
    }

    /// <summary>A의 행동별로 사용 스킬·A가 준 피해 합계·A의 자원 변화 로그를 모은다.</summary>
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
                    current.Where(x => x.Type == "ResourceChanged" && x.Actor == "A").Select(x => x.Detail ?? "").ToArray()));
            current = e.Actor == "A" ? [] : null;
        }
        return turns;
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= Math.Max(2, expected * .001);

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class ConstantRandom(double value) : IBattleRandom
    {
        public double NextDouble() => value;
    }

    public static readonly IReadOnlyDictionary<string, string> Sheets = new Dictionary<string, string>
    {
        ["배틀스킬"] = """"
        "ID","활성화","행동분류","대상유형","기본쿨다운","최초쿨다운","사용우선순위","사용조건","자원유형","자원소모","자원획득","궁극기여부","배틀설명","사용문구","비고",부모스킬ID,이름
        Arbalist_MountingShock,"TRUE","공격·이동","자신·상대","2","0","60","항상","궁극기 게이지","","150","FALSE","충격파로 적을 공격하고 강화 볼트 탄창을 장전하며 이동 속도가 빨라진다.","{caster}가 버스터 샷으로 {target}에게 충격파를 날립니다!","원본 재사용 대기 시간 10초, 표시 피해 18,023. 이동 속도 증가 10%·최대 중첩 2는 쿨다운 감소 1회로 단순화(cb_haste)",,
        Arbalist_ShockExplosion,"TRUE","공격·방해","상대","2","0","65","항상","궁극기 게이지","","150","FALSE","쇼크 볼트로 적을 공격해 기절시키고 브레이크 피해를 주며 강화 볼트 탄창을 장전한다.","{caster}가 쇼크 익스플로전으로 {target}을 뒤흔듭니다!","원본 재사용 대기 시간 8초, 표시 피해 8,814. 원본의 기절 효과는 브레이크 피해 1칸으로 반영",,
        Arbalist_SlipThrough,"TRUE","공격·이동·방해","자신·상대","2","0","58","항상","궁극기 게이지","","150","FALSE","뒤로 미끄러지며 볼트를 연사해 적을 공격하고, 강화 볼트 탄창을 장전하며 받는 피해가 줄어든다.","{caster}가 슬라이딩 스텝으로 {target}에게 볼트를 퍼붓습니다!","원본 재사용 대기 시간 8초, 표시 피해 18,023×2. 대상 이동 속도 감소(30%·8초)는 대응 효과가 없어 반영하지 않음. 자신 받는 피해 감소 20%·지속 5초→1턴",,
        Arbalist_GustingVolt,"TRUE","공격·연타","상대","0","0","80","강화 볼트 탄창 보유","cb_bolt_magazine","1","","FALSE","강화 볼트 탄창을 소모해 적에게 볼트를 난사한다.","{caster}가 거스팅 볼트로 {target}을 난사합니다!","표시 피해 3,950×10. 원본 재사용 대기 시간 없음(탄창 소모로만 제한). 발사할수록 치명타 확률이 감소하는 효과는 연속치명타배율로 반영. 드라이빙 포스 강화는 패시브 driving_force(cb_driving_force_gusting)로 반영",,
        Arbalist_SpreadingVolt,"TRUE","공격·강타","상대","0","0","78","강화 볼트 탄창 보유","cb_bolt_magazine","1","","FALSE","강화 볼트 탄창을 소모해 적에게 부채꼴로 볼트를 퍼붓는다.","{caster}가 스프레딩 볼트로 {target} 주변을 휩씁니다!","표시 피해 22,467. 원본 재사용 대기 시간 없음(탄창 소모로만 제한). 드라이빙 포스 강화는 패시브 driving_force(cb_driving_force_spreading)로 반영",,
        Arbalist_BoltExplosion,"TRUE","궁극기·공격","상대","0","0","100","궁극기 게이지 300","궁극기 게이지","300","","TRUE","일반 볼트를 모두 발사한 뒤 크게 도약해 폭발시키고 강화 볼트 탄창을 모두 재장전한다.","{caster}의 헬 파이어가 {target} 일대를 초토화합니다!","원본 화살 대미지 3,456×9, 폭발 대미지 110,091. 다른 기본기 대비 5배 이상 벌어져 있어 밸런스 시뮬레이션 결과를 반영해 각각 1,000×9 / 22,500으로 낮춰 저장. 엠블럼 장착 필요 조건은 실제 사용 조건이 아니므로 반영하지 않음",,
        """",
        ["배틀스킬효과"] = """"
        "ID","스킬ID","실행순서","효과유형","대상","계수기준","고정값","횟수","발동확률","지속턴","상태효과ID","최대중첩","효과문구","비고","조건대상","조건유형","조건ID","조건연산자","조건값","수치참조ID","수치참조방식","연속치명타배율"
        "buster_shot_01",Arbalist_MountingShock,"1","피해","상대","","11000","1","1","0","","0","{caster}의 충격파가 {target}에게 {damage}의 피해!"," 표시 피해 18,023. 밸런스 시뮬레이션(2,000회 × 2회) 결과를 반영해 11,000으로 조정(1차 8,800은 과도하게 약해졌음)","","","","","","","",""
        "buster_shot_02",Arbalist_MountingShock,"2","자원증가","자신","","1","1","1","0","cb_bolt_magazine","0","강화 볼트 탄창을 장전했습니다.","1개 장전","","","","","","","",""
        "buster_shot_03",Arbalist_MountingShock,"3","이동속도증가","자신","","0","1","1","1","cb_haste","1","이동 속도가 빨라집니다.","원본 이동 속도 증가 10%·지속 5초·최대 중첩 2를 쿨다운 감소 1회·1턴으로 단순화","","","","","","","",""
        "shock_explosion_01",Arbalist_ShockExplosion,"1","피해","상대","","8814","1","1","0","","0","{caster}의 폭발이 {target}에게 {damage}의 피해!","표시 피해 8,814","","","","","","","",""
        "shock_explosion_02",Arbalist_ShockExplosion,"2","브레이크피해","상대","","1","1","1","0","","0","{target}이 크게 휘청입니다!","원본 기절 효과를 브레이크 피해 1칸으로 반영","","","","","","","",""
        "shock_explosion_03",Arbalist_ShockExplosion,"3","자원증가","자신","","1","1","1","0","cb_bolt_magazine","0","강화 볼트 탄창을 장전했습니다.","1개 장전","","","","","","","",""
        "sliding_step_01",Arbalist_SlipThrough,"1","피해","상대","","6300","2","1","0","","0","볼트가 연달아 적중해 총 {damage}의 피해!"," 표시 피해 18,023×2. 밸런스 시뮬레이션 결과를 반영해 6,300×2로 조정(1차 5,000은 과도하게 약해졌음)","","","","","","","",""
        "sliding_step_02",Arbalist_SlipThrough,"2","받는피해감소","자신","","0","1","1","1","cb_guard","1","받는 피해가 줄어듭니다.","원본 받는 피해 감소 20%·지속 5초를 1턴으로 환산","","","","","","","",""
        "sliding_step_03",Arbalist_SlipThrough,"3","자원증가","자신","","1","1","1","0","cb_bolt_magazine","0","강화 볼트 탄창을 장전했습니다.","1개 장전","","","","","","","",""
        "gusting_bolt_01",Arbalist_GustingVolt,"1","피해","상대","","2000","10","1","0","","0","강화 볼트가 {target}을 난타해 총 {damage}의 피해!","피해 3,950×10. 밸런스 시뮬레이션 결과를 반영해 2,000×10으로 조정(1차 1,600은 과도하게 약해졌음). 발사할수록 치명타 확률이 감소하는 원본 효과는 연속치명타배율 0.75로 반영. 드라이빙 포스 강화는 패시브 driving_force로 반영","","","","","","","","0.75"
        "gusting_bolt_02",Arbalist_GustingVolt,"2","자원증가","자신","","150","1","1","0","ultimate_gauge","0","궁극기 게이지가 상승합니다.","기본기 공통 150 적립(자원유형 필드가 강화 볼트 탄창 소모에 쓰여 효과 행으로 대체)","자신","자원보유","cb_fortune_free","<=","0","","",""
        "spreading_bolt_01",Arbalist_SpreadingVolt,"1","피해","상대","","20000","1","1","0","","0","강화 볼트가 부채꼴로 퍼져 {target}에게 {damage}의 피해!","표시 피해 22,467. 밸런스 시뮬레이션 결과를 반영해 20,000으로 조정(1차 16,000은 과도하게 약해졌음). 드라이빙 포스 강화는 패시브 driving_force로 반영","","","","","","","",""
        "spreading_bolt_02",Arbalist_SpreadingVolt,"2","자원증가","자신","","150","1","1","0","ultimate_gauge","0","궁극기 게이지가 상승합니다.","기본기 공통 150 적립(자원유형 필드가 강화 볼트 탄창 소모에 쓰여 효과 행으로 대체)","자신","자원보유","cb_fortune_free","<=","0","","",""
        "hell_fire_01",Arbalist_BoltExplosion,"1","피해","상대","","1000","9","1","0","","0","일반 볼트가 쏟아져 {target}에게 총 {damage}의 피해!"," 화살 대미지 3,456×9. 밸런스 시뮬레이션 결과를 반영해 1,000×9로 조정(1차 800은 과도하게 약해졌음)","","","","","","","",""
        "hell_fire_02",Arbalist_BoltExplosion,"2","피해","상대","","22500","1","1","0","","0","익스플로전 볼트가 폭발해 {target}에게 {damage}의 피해!"," 폭발 대미지 110,091. 밸런스 시뮬레이션 결과를 반영해 22,500으로 조정(1차 18,000은 과도하게 약해졌음)","","","","","","","",""
        "hell_fire_03",Arbalist_BoltExplosion,"3","자원설정","자신","","3","1","1","0","cb_bolt_magazine","3","강화 볼트 탄창을 모두 재장전했습니다.","전량 재장전(최대값 3)","","","","","","","",""
        "sliding_step_04",Arbalist_SlipThrough,"4","쿨다운증가","상대","","0","1","1","2","cb_slowed","1","{target}의 움직임이 둔해집니다.","원본 이동 속도 감소 30%·지속 8초를 쿨다운증가 상태(2턴)로 단순화. 보유 중 해당 대상의 쿨다운 감소가 멈춘다","","","","","","","",""
        """",
        // 석궁사수는 파생 행이 없다. 로더가 데이터 행이 없는 시트의 헤더를 읽지 못해, 조건상 절대 발동하지 않는 자리표시 행을 둔다(cb_fortune_free 최대값 1).
        ["배틀스킬파생"] = """"
        "ID","부모스킬ID","파생스킬ID","발동방식","가중치","발동확률","조건유형","조건값","중복허용","실행시점","비고","우선순위"
        "fixture_placeholder",Arbalist_MountingShock,Arbalist_ShockExplosion,"대체","1","1","자원보유","cb_fortune_free=2","FALSE","즉시","테스트 자리표시","0"
        """",
        // 석궁사수 사본은 AI 조건 없이 기본 가중치로 추첨한다(헤더만 있는 시트). AI 가산 동작은 Program.cs의 배틀스킬AI 테스트가 검사한다.
        ["배틀스킬AI"] = """"
        "ID","스킬ID","활성화","조건대상","조건유형","조건ID","조건연산자","조건값","가산가중치","단계크기","단계당가산","최대가산","비고"
        """",
        ["배틀패시브"] = """"
        "ID","활성화","비고"
        Arbalist_ExtraAction,"TRUE","공격 횟수 2배는 장전 스킬의 피해를 한 번 더 적용하는 것으로 표현. 10초→2턴."
        Arbalist_DrivingForce,"TRUE","동일 소모 스킬 연속 사용은 최소 쿨다운·직전 스킬 제외 규칙상 불가능해 거스팅·스프레딩 볼트 간 연속 사용으로 완화. 피해 증가는 연속 조건을 충족한 볼트 자신부터 거스팅·스프레딩 볼트에만 적용(2026-09-25 GitHub Issue #7)."
        Arbalist_Critical,"TRUE","레벨 30 어시스트 해금 문구는 구현 범위에서 제외(항상 만렙 가정)."
        Arbalist_QuickAttack,"TRUE","스킬 사용 속도 5%/중첩을 쿨다운 추가 감소 0.34/중첩(반올림, 2중첩부터 1회)으로 근사. ""장전 시""는 장전 스킬 사용 완료 기준(탄창이 가득 차 있어도 중첩, 2026-09-25 GitHub Issue #7)."
        Arbalist_ReinforcedBolt,"FALSE","장전 스킬 충전 횟수(스택) 2는 엔진에 스킬 충전 개념이 없고, 최소 쿨다운·직전 스킬 제외 규칙상 같은 스킬 연속 사용도 불가능해 효과가 없어 비활성. 강화 볼트 탄창 최대 보유량(cb_bolt_magazine 최대값)과는 다른 개념. 대체 효과 없이 제외 유지 확정(2026-09-25 GitHub Issue #7)."
        Arbalist_Equilibrium,"TRUE","연속 사용 제한 2초는 다음 행동까지로 근사. 추가 발동은 다음 행동을 지정(행동 소모, 전투 로그에 cb_fortune_free 이름으로 표시). 궁극기 게이지 미획득은 소모 스킬의 궁극기 게이지 효과 행 조건(cb_fortune_free=0)으로, 드라이빙 포스 미발생은 판정 직후 중첩 제거와 df_01·df_02 조건으로 구현."
        """",
        ["배틀패시브효과"] = """"
        "ID","패시브ID","실행순서","효과유형","대상","고정값","횟수","발동확률","지속턴","상태효과ID","최대중첩","효과문구","조건대상","조건유형","조건ID","조건연산자","조건값","수치참조ID","수치참조방식","발동시점","대상스킬ID","대상자원ID","비고","재발동대기턴"
        "ea_01",Arbalist_ExtraAction,"1","피해","상대","11000","1","1","0","","0","엑스트라 액션! 쇼크 볼트를 한 발 더 발사합니다.","자신","자원보유","cb_extra_action",">=","1","","","스킬사용완료시",Arbalist_MountingShock,"","공격 횟수 2배를 같은 피해 1회 추가로 표현(buster_shot_01과 동일 값)",""
        "ea_02",Arbalist_ExtraAction,"2","피해","상대","8814","1","1","0","","0","엑스트라 액션! 쇼크 볼트를 한 발 더 발사합니다.","자신","자원보유","cb_extra_action",">=","1","","","스킬사용완료시",Arbalist_ShockExplosion,"","원문대로 추가 볼트는 브레이크 피해 없음",""
        "ea_03",Arbalist_ExtraAction,"3","피해","상대","6300","2","1","0","","0","엑스트라 액션! 볼트를 더 발사합니다.","자신","자원보유","cb_extra_action",">=","1","","","스킬사용완료시",Arbalist_SlipThrough,"","sliding_step_01과 동일 값",""
        "ea_04",Arbalist_ExtraAction,"4","자원소모","자신","0","1","1","0","cb_extra_action","0","","자신","자원보유","cb_extra_action",">=","1","","전부","스킬사용완료시",Arbalist_MountingShock,"","",""
        "ea_05",Arbalist_ExtraAction,"5","자원소모","자신","0","1","1","0","cb_extra_action","0","","자신","자원보유","cb_extra_action",">=","1","","전부","스킬사용완료시",Arbalist_ShockExplosion,"","",""
        "ea_06",Arbalist_ExtraAction,"6","자원소모","자신","0","1","1","0","cb_extra_action","0","","자신","자원보유","cb_extra_action",">=","1","","전부","스킬사용완료시",Arbalist_SlipThrough,"","",""
        "ea_07",Arbalist_ExtraAction,"7","자원설정","자신","1","1","1","0","cb_extra_action","0","","","","","","","","","스킬사용완료시",Arbalist_GustingVolt,"","강화 볼트 소모 스킬 사용 시 다음 장전 스킬 강화(10초→2턴)",""
        "ea_08",Arbalist_ExtraAction,"8","자원설정","자신","1","1","1","0","cb_extra_action","0","","","","","","","","","스킬사용완료시",Arbalist_SpreadingVolt,"","",""
        "df_01",Arbalist_DrivingForce,"1","자원증가","자신","1","1","1","0","cb_driving_force","0","드라이빙 포스! 조준이 날카로워집니다.","자신","자원보유","cb_fortune_free","<=","0","","","스킬사용완료시",Arbalist_GustingVolt,"","소모 스킬 사용마다 중첩 +1(최대 2, 다음 행동까지). 중첩은 거스팅·스프레딩 볼트 피해에만 적용되므로 연속으로 쓴 두 번째 볼트 +30%, 세 번째 볼트 +60%. 원문 ""동일한 소모 스킬 4초 내 연속""은 최소 쿨다운·직전 스킬 제외 규칙상 불가능해 소모 스킬끼리의 연속 사용으로 완화. 포츈 볼트 추가 발동 스킬은 드라이빙 포스를 발생시키지 않음",""
        "df_02",Arbalist_DrivingForce,"2","자원증가","자신","1","1","1","0","cb_driving_force","0","드라이빙 포스! 조준이 날카로워집니다.","자신","자원보유","cb_fortune_free","<=","0","","","스킬사용완료시",Arbalist_SpreadingVolt,"","",""
        "df_03",Arbalist_DrivingForce,"3","스킬피해증가","자신","0","1","1","0","cb_driving_force_gusting","0","","","","","","","","","전투시작","","","중첩자원ID=cb_driving_force로 거스팅 볼트 피해 중첩당 +30%(최대 2)",""
        "df_04",Arbalist_DrivingForce,"4","스킬피해증가","자신","0","1","1","0","cb_driving_force_spreading","0","","","","","","","","","전투시작","","","중첩자원ID=cb_driving_force로 스프레딩 볼트 피해 중첩당 +30%(최대 2)",""
        "cmrc_01",Arbalist_Critical,"1","치명타피해증가","자신","0","1","1","0","combat_mastery_risk_crossbow_crit","0","","","","","","","","","전투시작","","","",""
        "qa_01",Arbalist_QuickAttack,"1","쿨다운감소","자신","0","1","1","0","cb_quick_attack_haste","0","","","","","","","","","전투시작","","","중첩자원ID=cb_quick_attack로 중첩당 쿨다운 추가 감소 0.34(2중첩부터 1회)",""
        "qa_02",Arbalist_QuickAttack,"2","자원증가","자신","1","1","1","0","cb_quick_attack","0","","","","","","","","","스킬사용완료시",Arbalist_MountingShock,"","강화 볼트 장전 스킬 사용 완료 시 중첩 +1(탄창이 가득 차 실제 장전량이 0이어도 적용, 최대 3, 10초→2턴)",""
        "qa_03",Arbalist_QuickAttack,"3","자원증가","자신","1","1","1","0","cb_quick_attack","0","","","","","","","","","스킬사용완료시",Arbalist_ShockExplosion,"","강화 볼트 장전 스킬 사용 완료 시 중첩 +1(탄창이 가득 차 실제 장전량이 0이어도 적용, 최대 3, 10초→2턴)",""
        "qa_04",Arbalist_QuickAttack,"4","자원증가","자신","1","1","1","0","cb_quick_attack","0","","","","","","","","","스킬사용완료시",Arbalist_SlipThrough,"","강화 볼트 장전 스킬 사용 완료 시 중첩 +1(탄창이 가득 차 실제 장전량이 0이어도 적용, 최대 3, 10초→2턴)",""
        "qa_05",Arbalist_QuickAttack,"5","자원증가","자신","1","1","1","0","cb_quick_attack","0","","","","","","","","","스킬사용완료시",Arbalist_BoltExplosion,"","강화 볼트 장전 스킬 사용 완료 시 중첩 +1(탄창이 가득 차 실제 장전량이 0이어도 적용, 최대 3, 10초→2턴)",""
        "fo_01",Arbalist_Equilibrium,"1","자원증가","자신","1","1","1","0","cb_fortune_count","0","","자신","자원보유","cb_fortune_free","<=","0","","","스킬사용완료시",Arbalist_GustingVolt,"","소모 스킬 연속 사용 횟수(다음 행동에 소모 스킬을 쓰지 않으면 지속턴 만료로 초기화)",""
        "fo_02",Arbalist_Equilibrium,"2","자원증가","자신","1","1","1","0","cb_fortune_count","0","","자신","자원보유","cb_fortune_free","<=","0","","","스킬사용완료시",Arbalist_SpreadingVolt,"","",""
        "fo_03",Arbalist_Equilibrium,"3","자원소모","자신","0","1","1","0","cb_fortune_free","0","","자신","자원보유","cb_fortune_free",">=","1","","전부","스킬사용완료시","","","추가 발동 스킬 사용이 끝나면 표식 제거",""
        "fo_04",Arbalist_Equilibrium,"4","자원설정","자신","1","1","0.67","0","cb_fortune_free","0","포츈 볼트! 비장의 한 발을 준비합니다.","자신","자원보유","cb_fortune_count",">=","3","","","스킬사용완료시",Arbalist_GustingVolt,"","연속 3회마다 67% 판정",""
        "fo_05",Arbalist_Equilibrium,"5","자원설정","자신","1","1","0.67","0","cb_fortune_free","0","포츈 볼트! 비장의 한 발을 준비합니다.","자신","자원보유","cb_fortune_count",">=","3","","","스킬사용완료시",Arbalist_SpreadingVolt,"","",""
        "fo_06",Arbalist_Equilibrium,"6","다음행동지정","자신","0","1","1","0",Arbalist_SpreadingVolt,"0","","자신","자원보유","cb_fortune_free",">=","1","","","스킬사용완료시",Arbalist_GustingVolt,"","다른 소모 스킬을 다음 행동으로 지정(즉흥 연주와 같은 단순화로 행동 하나를 사용)",""
        "fo_07",Arbalist_Equilibrium,"7","다음행동지정","자신","0","1","1","0",Arbalist_GustingVolt,"0","","자신","자원보유","cb_fortune_free",">=","1","","","스킬사용완료시",Arbalist_SpreadingVolt,"","",""
        "fo_08",Arbalist_Equilibrium,"8","자원증가","자신","1","1","1","0","cb_bolt_magazine","0","","자신","자원보유","cb_fortune_free",">=","1","","","스킬사용완료시","","","탄창 없이 사용: 추가 발동 스킬이 소모할 탄창 1개를 미리 보충해 상쇄",""
        "fo_09",Arbalist_Equilibrium,"9","자원소모","자신","0","1","1","0","cb_driving_force","0","","자신","자원보유","cb_fortune_free",">=","1","","전부","스킬사용완료시","","","추가 발동 스킬은 드라이빙 포스를 받거나 발생시키지 않음: 판정 직후 중첩 제거",""
        "fo_10",Arbalist_Equilibrium,"10","자원소모","자신","0","1","1","0","cb_fortune_count","0","","자신","자원보유","cb_fortune_count",">=","3","","전부","스킬사용완료시","","","판정 후 연속 횟수 초기화",""
        """",
        ["배틀자원"] = """"
        "ID","이름","분류","최대값","초기값","지속턴","중첩방식","전투종료시제거","설명"
        "ultimate_gauge","궁극기 게이지","궁극기 게이지","300","0","0","가산","TRUE","모든 클래스가 공유하는 궁극기 자원 분류. 기본기 사용마다 쌓이며 궁극기 사용 시 300을 소모한다."
        "cb_bolt_magazine","강화 볼트 탄창","자원","3","0","0","가산","TRUE","석궁사수가 버스터 샷·쇼크 익스플로전·슬라이딩 스텝으로 장전하고 거스팅 볼트·스프레딩 볼트로 소모하는 탄창. 최대값 3은 인게임 탄창 표시(3칸)로 확인했으며 헬 파이어의 전량 재장전 수치(3개)와도 일치한다."
        "cb_extra_action","엑스트라 액션","자원","1","0","2","교체","TRUE","강화 볼트 소모 스킬 사용 시 얻는 다음 장전 스킬 추가 공격 표식. (2026-09-24 6초=1턴 가이드라인: 원문 10초→2턴)"
        "cb_driving_force","드라이빙 포스","중첩","2","0","1","가산","TRUE","드라이빙 포스 중첩(최대 2). 강화 볼트 소모 스킬 사용마다 +1, 다음 행동까지 유지. 거스팅·스프레딩 볼트 피해에만 중첩당 +30%(연속 두 번째 볼트 +30%, 세 번째 +60%). (2026-09-25 GitHub Issue #7)"
        "cb_quick_attack","퀵 어택","중첩","3","0","2","가산","TRUE","퀵 어택 중첩(최대 3). 장전 스킬 사용 시 +1. (2026-09-24 6초=1턴 가이드라인: 원문 10초→2턴)"
        "cb_fortune_count","포츈 볼트 연속 사용","중첩","3","0","1","가산","TRUE","거스팅·스프레딩 볼트 연속 사용 횟수. 다음 행동에 소모 스킬을 쓰지 않으면 만료된다. (2026-09-24 6초=1턴 가이드라인: 다음 행동까지 유지)"
        "cb_fortune_free","포츈 볼트(다음 행동 무료 발사)","자원","1","0","0","교체","TRUE","포츈 볼트로 지정된 추가 발동 스킬 표식. 추가 발동은 다음 행동 하나를 사용하며, 이 표식이 있는 동안 소모 스킬은 탄창을 실질적으로 쓰지 않고 궁극기 게이지와 드라이빙 포스를 얻지 않는다. 전투 로그에 다음 행동이 무료 발사임을 드러내도록 이름을 정했다(2026-09-25 GitHub Issue #7)."
        """",
        ["배틀상태효과"] = """"
        "ID","이름","효과유형","값","설명","대상스킬ID","중첩자원ID","시너지","지속방식","효과별값"
        "break_broken","브레이크","브레이크|받는피해증가","0.2","브레이크 시 다음 행동을 잃고 무방비 대미지 120%(받는 피해 +20%). 모든 브레이크 타입 공통인 무방비만 반영(강타·연타·속성 대미지 증가는 배틀에 구분이 없어 제외)","","","","",""
        "cb_haste","가속","쿨다운감소","1","버스터 샷 사용 후 스킬 쿨다운을 1회 추가 감소시킨다. 원본 이동 속도 증가 10%·지속 5초(2턴로 환산)·최대 중첩 2를 단순화했다.","","","","",""
        "cb_guard","회피 기동","받는피해감소","0.2","슬라이딩 스텝 사용 후 받는 피해 20% 감소. 원본 지속 5초를 2턴으로 환산했다.","","","","",""
        "cb_slowed","둔화","쿨다운증가","1","슬라이딩 스텝이 상대에게 남기는 이동 속도 감소(30%·8초)의 단순화. 보유 중 해당 대상의 쿨다운 감소가 멈춘다(쿨다운감소와 대칭 효과유형).","","","","",""
        "cb_driving_force_gusting","드라이빙 포스(거스팅)","스킬피해증가","0.3","드라이빙 포스 중첩당 거스팅 볼트 피해 +30%(최대 2중첩).",Arbalist_GustingVolt,"cb_driving_force","","",""
        "combat_mastery_risk_crossbow_crit","전투 숙련: 위협(석궁사수)","치명타피해증가","0.05","치명타 피해 +5%.","","","","",""
        "cb_quick_attack_haste","퀵 어택","쿨다운감소","0.34","퀵 어택 중첩당 스킬 사용 속도 +5%를 쿨다운 추가 감소 0.34로 근사(반올림, 2중첩부터 매 행동 1회).","","cb_quick_attack","","",""
        "cb_driving_force_spreading","드라이빙 포스(스프레딩)","스킬피해증가","0.3","드라이빙 포스 중첩당 스프레딩 볼트 피해 +30%(최대 2중첩).",Arbalist_SpreadingVolt,"cb_driving_force","","",""
        """",
        ["배틀이름교체"] = """"
        ID,표시이름,비고
        """",
        ["배틀규칙"] = """"
        "ID","분류","값유형","값","설명"
        "base_max_hp","전투능력치","number","20000","동일 전투력 기준 최대 HP"
        "base_attack","전투능력치","number","1000","동일 전투력 기준 공격력"
        "base_defense","전투능력치","number","200","동일 전투력 기준 방어력"
        "power_scale_exponent","전투능력치","number","0.5","전투력 비율 압축 지수"
        "power_scale_min","전투능력치","number","0.75","전투력 보정 하한"
        "power_scale_max","전투능력치","number","1.25","전투력 보정 상한"
        "defense_coefficient","피해","number","0.5","피해 계산 시 방어력 반영 계수"
        "damage_variance_min","피해","number","0.9","피해량 무작위 편차 하한"
        "damage_variance_max","피해","number","1.1","피해량 무작위 편차 상한"
        "base_critical_chance","치명타","number","0.22","기본 치명타 확률"
        "critical_damage_multiplier","치명타","number","1.5","치명타 피해 배율"
        "max_major_actions","종료","integer","20","최대 주요 행동 수"
        "draw_hp_ratio_threshold","종료","number","0.05","최대 행동 도달 시 무승부 HP 비율 차이"
        "max_surprise_events_per_actor","돌발이벤트","integer","2","한 캐릭터가 한 전투에서 발생시킬 수 있는 돌발 이벤트 총합"
        "surprise_event_global_cooldown","돌발이벤트","integer","2","돌발 이벤트 발생 후 다음 판정까지 필요한 주요 행동 수"
        "normal_attack_multiplier","피해","number","1.5","일반 공격 피해 배율"
        "skill_damage_min_multiplier","피해","number","2","일반 스킬 피해 배율 하한"
        "skill_damage_max_multiplier","피해","number","2.4","일반 스킬 피해 배율 상한"
        "ultimate_damage_multiplier","피해","number","2.7","궁극기 피해 배율"
        "minimum_skill_cooldown","행동","integer","1","반복 사용 방지를 위한 최소 쿨다운. 2026-09-24 쿨다운을 6초=1턴 가이드라인으로 재계산하면서 2→1로 낮췄다"
        "skill_heal_min_multiplier","회복","number","1.5","회복 효과 1회 사용의 기본 배율 하한(공격력 × 배율). 고정값이 있으면 피해와 동일하게 고정값×횟수×fixed_damage_scale을 대신 사용"
        "life_surprise_base_chance","돌발이벤트","number","0.2","생활력 돌발 이벤트 기본 확률"
        "skill_heal_max_multiplier","회복","number","2","회복 효과 1회 사용의 기본 배율 상한(공격력 × 배율)"
        "life_surprise_stat_reference","돌발이벤트","number","100000","생활력 확률 보정 기준값"
        "life_surprise_stat_coefficient","돌발이벤트","number","0.1","생활력 능력치 보정 계수"
        "life_surprise_max_chance","돌발이벤트","number","0.35","생활력 돌발 이벤트 확률 상한"
        "life_surprise_heal_ratio","돌발이벤트","number","0.08","생활력이 높은 캐릭터의 응급처치 회복 비율"
        "charm_surprise_damage_multiplier","돌발이벤트","number","1.3","매력이 높은 캐릭터의 환호 피해 배율"
        "additional_hit_chance","추가타","number","0.28","피해 직후 추가타 발생 확률"
        "additional_hit_damage_ratio","추가타","number","0.35","추가타가 직전 피해에 적용하는 비율. 치명타 없음"
        "charm_surprise_base_chance","돌발이벤트","number","0.2","매력 돌발 이벤트 기본 확률"
        "charm_surprise_stat_reference","돌발이벤트","number","100000","매력 확률 보정 기준값"
        "charm_surprise_stat_coefficient","돌발이벤트","number","0.1","매력 능력치 보정 계수"
        "charm_surprise_max_chance","돌발이벤트","number","0.35","매력 돌발 이벤트 확률 상한"
        "life_surprise_hp_ratio_threshold","돌발이벤트","number","0.5","야전 응급처치를 판정하는 HP 비율 상한"
        "break_gauge_maximum","브레이크","integer","3","브레이크 상태가 되는 데 필요한 게이지"
        "break_duration_turns","브레이크","integer","1","브레이크로 행동하지 못하는 턴 수"
        "target_release_evasion_chance","상태효과","number","0.65","대상 해제 상태인 캐릭터가 상대 공격을 회피할 확률"
        "fixed_damage_scale","피해","number","0.25","화면에서 확인한 고정 피해 1타를 배틀 HP 기준으로 환산하는 공통 배율"
        "ultimate_heal_multiplier","회복","number","2.4","궁극기(회복 포함) 사용의 회복 배율. 현재 궁극기 중 회복 효과를 쓰는 스킬은 없어 예비값이다"
        """",
        ["배틀돌발이벤트"] = """"
        "ID","이름","활성화","능력치유형","발동시점","기본확률","능력치기준값","능력치보정계수","최대확률","전투당최대횟수","조건대상","조건유형","조건값","대상유형","이벤트문구","비고"
        """",
        ["배틀돌발이벤트효과"] = """"
        "ID","이벤트ID","실행순서","효과유형","대상","계수기준","계수","고정값","횟수","지속턴","상태효과ID","최대중첩","효과문구","비고"
        """",
        // 배틀생활스킬은 LifeSkillBattleTests에서 실제 시트 사본으로 검사한다. 석궁사수 흐름에는 생활스킬 행을 두지 않는다.
        ["배틀생활스킬"] = """"
        "ID","생활스킬이름","활성화","기본사용확률","생활력기준값","생활력확률보정계수","최대사용확률","전투당최대횟수","선택가중치","조건대상","조건유형","조건연산자","조건값","행동문구","비고"
        """",
        ["배틀생활스킬효과"] = """"
        "ID","배틀생활스킬ID","실행순서","효과유형","대상","계수기준","계수","고정값","생활력비례","생활력기준값","생활력최소배율","생활력최대배율","횟수","지속턴","상태효과ID","최대중첩","효과문구","비고","선택그룹","선택가중치"
        """"
    };
}
