using Molly.Battle;

/// <summary>
/// 배틀 테스트용 게임 데이터(배틀 스킬 원본). 2026-11-01 게임 데이터 저장소의 CombatJob·SkillSlot·Skill에서
/// 공개 전직 클래스와 배틀 시트가 쓰는 스킬·패시브의 이름·슬롯만 추린 사본이다. 실제 봇은 <see cref="UnpackBattleGameDataset"/>로 만든다.
/// 배틀 시트에 새 스킬을 넣어 클래스별 테스트 사본을 갱신하면 여기에도 그 스킬의 행을 추가한다.
/// </summary>
internal static class BattleTestGame
{
    public static readonly BattleGameData Data = Parse(Classes, Skills);

    /// <summary>CombatJob.Id|표시 이름. 순서는 CombatJob.Order.</summary>
    private const string Classes = """
        HighWarrior|전사
        GreatSwordWarrior|대검전사
        SwordMaster|검술사
        Knight|기사
        HighArcher|궁수
        LongBowMan|장궁병
        Arbalist|석궁사수
        HighMage|마법사
        IceMage|빙결술사
        FireMage|화염술사
        LightningMage|전격술사
        HighHealer|힐러
        Priest|사제
        Monk|수도사
        DarkShaman|암흑술사
        HighThief|도적
        DualBlades|듀얼블레이드
        Fighter|격투가
        HighBard|음유시인
        BattleMusician|악사
        Dancer|댄서
        """;

    /// <summary>Skill.Id|Skill.DisplayName|CombatJob.Id/SlotIndex;…</summary>
    private const string Skills = """
        ExpertWarrior_RapidSlash|연속 베기|HighWarrior/Slot01
        ExpertWarrior_BladeSmash|블레이드 스매시|HighWarrior/Slot02
        ExpertWarrior_ShieldBash|방패 치기|HighWarrior/Slot03
        ExpertWarrior_DashStab|찌르기|HighWarrior/Slot04
        ExpertWarrior_BattleCry|전장의 함성|HighWarrior/Slot05
        ExpertWarrior_SwordQuake|블레이드 임팩트|HighWarrior/Slot06
        ExpertWarrior_AmaranthKick|아마란스 킥|HighWarrior/Slot03
        ExpertWarrior_BreakExtend|퀘이크|HighWarrior/Slot02
        ExpertWarrior_Block|카운터 어택|HighWarrior/PassiveSlot01
        ExpertWarrior_FightingSpirit|투지|HighWarrior/PassiveSlot02
        ExpertWarrior_Passive3_Evasion|전투 숙련: 수호|HighWarrior/PassiveSlot03
        ExpertWarrior_Passive4_Vengeance|복수심|HighWarrior/PassiveSlot04
        ExpertWarrior_Passive5_DownAttack|기회 포착|HighWarrior/PassiveSlot05
        ExpertWarrior_Passive6_Instinct|직감|HighWarrior/PassiveSlot06
        GreatSwordWarrior_SpinningSlash|회전 베기|GreatSwordWarrior/Slot01
        GreatSwordWarrior_UpperSmash|라이징 스매시|GreatSwordWarrior/Slot02
        GreatSwordWarrior_Blockade_Back|어깨 치기|GreatSwordWarrior/Slot03
        GreatSwordWarrior_Slash|가르기|GreatSwordWarrior/Slot04
        GreatSwordWarrior_Finish|대검술|GreatSwordWarrior/Slot05
        GreatSwordWarrior_MortalStrike|필사의 일격|GreatSwordWarrior/Slot06
        GreatSwordWarrior_Blockade_Front|발구르기|GreatSwordWarrior/Slot03
        GreatSwordWarrior_Finish_Second|대지 휩쓸기|GreatSwordWarrior/Slot05
        GreatSwordWarrior_Finish_First|심장 찌르기|GreatSwordWarrior/Slot05
        GreatSwordWarrior_Finish_Third|바람 가르기|GreatSwordWarrior/Slot05
        GreatSwordWarrior_Assault|맹공|GreatSwordWarrior/PassiveSlot01
        GreatSwordWarrior_Fortitude|불굴|GreatSwordWarrior/PassiveSlot02
        GreatSwordWarrior_CombatMastery|전투 숙련: 패기|GreatSwordWarrior/PassiveSlot03
        GreatSwordWarrior_Revenge|보복|GreatSwordWarrior/PassiveSlot04
        GreatSwordWarrior_Brawl|난투|GreatSwordWarrior/PassiveSlot05
        GreatSwordWarrior_CrushedWound|뭉개진 상처|GreatSwordWarrior/PassiveSlot06
        SwordMaster_SteelWedge|강철 쐐기|SwordMaster/Slot01
        SwordMaster_SwordAndScabbard|칼집 치기|SwordMaster/Slot02
        SwordMaster_SwordDance|질풍 베기|SwordMaster/Slot03
        SwordMaster_Hiken|비검|SwordMaster/Slot04
        SwordMaster_Detection|간파|SwordMaster/Slot05
        SwordMaster_Flash|일섬|SwordMaster/Slot06
        SwordMaster_Hiken_SteelWedge|비검: 강철 쐐기|SwordMaster/Slot04
        SwordMaster_Hiken_SwordAndScabbard|비검: 칼집 치기|SwordMaster/Slot04
        SwordMaster_Hiken_SwordDance|비검: 질풍 베기|SwordMaster/Slot04
        SwordMaster_Focus|집중|SwordMaster/PassiveSlot01
        SwordMaster_SwiftPosture|질풍태세|SwordMaster/PassiveSlot02
        SwordMaster_CombatMastery|전투 숙련: 위협|SwordMaster/PassiveSlot03
        SwordMaster_ComboSwordSkill|연계 검술|SwordMaster/PassiveSlot04
        SwordMaster_KeenEye|날카로운 눈|SwordMaster/PassiveSlot05
        SwordMaster_Initiative|선수필승|SwordMaster/PassiveSlot06
        HighArcher_ArrowRevolver|애로우 리볼버|HighArcher/Slot01
        HighArcher_MagnumShot|매그넘 샷|HighArcher/Slot02
        HighArcher_SideStep|사이드 스텝|HighArcher/Slot03
        HighArcher_HawkShot|호크 샷|HighArcher/Slot04
        HighArcher_EscapeStep|이스케이프 스텝|HighArcher/Slot05
        HighArcher_FireArrow|작열의 궤적|HighArcher/Slot06
        HighArcher_RushingWind|바람결|HighArcher/PassiveSlot01
        ExpertArcher_PiercingWeakness|약점 관통|HighArcher/PassiveSlot02
        ExpertArcher_CombatMastery|전투 숙련: 쾌속|HighArcher/PassiveSlot03
        HighArcher_Knockback|다발사격|HighArcher/PassiveSlot04
        ExpertArcher_Impetus|추진력|HighArcher/PassiveSlot05
        ExpertArcher_Tailwind|순풍|HighArcher/PassiveSlot06
        Arbalist_MountingShock|버스터 샷|Arbalist/Slot01
        Arbalist_ShockExplosion|쇼크 익스플로전|Arbalist/Slot02
        Arbalist_SlipThrough|슬라이딩 스텝|Arbalist/Slot03
        Arbalist_GustingVolt|거스팅 볼트|Arbalist/Slot04
        Arbalist_SpreadingVolt|스프레딩 볼트|Arbalist/Slot05
        Arbalist_BoltExplosion|헬 파이어|Arbalist/Slot06
        Arbalist_ExtraAction|엑스트라 액션|Arbalist/PassiveSlot01
        Arbalist_DrivingForce|드라이빙 포스|Arbalist/PassiveSlot02
        Arbalist_Critical|전투 숙련: 위협|Arbalist/PassiveSlot03
        Arbalist_QuickAttack|퀵 어택|Arbalist/PassiveSlot04
        Arbalist_ReinforcedBolt|익스텐드 매거진|Arbalist/PassiveSlot05
        Arbalist_Equilibrium|포츈 볼트|Arbalist/PassiveSlot06
        HighMage_Lightning|라이트닝|HighMage/Slot01
        HighMage_Fireball|파이어 볼|HighMage/Slot02
        HighMage_IceDagger|아이스 대거|HighMage/Slot03
        HighMage_Telekinesis|텔레키네시스|HighMage/Slot04
        HighMage_ManaStorm|마나 스톰|HighMage/Slot05
        HighMage_InfinityManaLoop|마나 인피니티|HighMage/Slot06
        HighMage_ManaStorm_Blink|마나 스톰|HighMage/Slot05
        HighMage_Meditation|메디테이션|HighMage/PassiveSlot01
        HighMage_EnchantMagic|엘리멘탈 하모니|HighMage/PassiveSlot02
        HighMage_Multihit_Passive_03|전투 숙련: 기교|HighMage/PassiveSlot03
        HighMage_ElementalMaster_Passive_04|엘리멘탈 마스터|HighMage/PassiveSlot04
        HighMage_ElementalList_Passive_05|아케인 파워|HighMage/PassiveSlot05
        HighMage_OverSurge_Passive_06|오버서지|HighMage/PassiveSlot06
        FireMage_RapidFire|래피드 파이어|FireMage/Slot01
        FireMage_Firestorm|파이어 스톰|FireMage/Slot02
        FireMage_FlameCannon|플레임 캐논|FireMage/Slot03
        FireMage_FlashOver|플래시오버|FireMage/Slot04
        FireMage_Ignite|익스플로전|FireMage/Slot05
        FireMage_Incinerate|인페르노|FireMage/Slot06
        FireMage_FlameCannon_Enhanced_Dummy|버스트 캐논|FireMage/Slot03
        FireMage_BurningSoul|버닝 소울|FireMage/PassiveSlot01
        FireMage_Flame|블레이즈|FireMage/PassiveSlot02
        FireMage_Passive3_MultiHit|전투 숙련: 기교|FireMage/PassiveSlot03
        FireMage_Passive4_Spark|스파크|FireMage/PassiveSlot04
        FireMage_Passive5_Overheat|오버히트|FireMage/PassiveSlot05
        FireMage_Passive6_Ignite|이그나이트|FireMage/PassiveSlot06
        IceMage_CrystalEdge|크리스탈 엣지|IceMage/Slot01
        IceMage_SplitSlash|글래시어 커터|IceMage/Slot02
        IceMage_IceSpike|아이스 스파이크|IceMage/Slot03
        IceMage_FreezingField|프리징 필드|IceMage/Slot04
        IceMage_FrozenOrb|프로즌 오브|IceMage/Slot05
        IceMage_IceAge|앱솔루트 제로|IceMage/Slot06
        IceMage_BreakExtend|아이시클 섀클|IceMage/Slot02
        IceMage_VeilOfWinter|베일 오브 윈터|IceMage/PassiveSlot01
        IceMage_MarkOfWinter|아이시클 마크|IceMage/PassiveSlot02
        IceMage_CombatMastery|전투 숙련: 수호|IceMage/PassiveSlot03
        IceMage_Fluttering_Frost|흩날리는 서리|IceMage/PassiveSlot04
        IceMage_Piercing_Chill|사무치는 냉기|IceMage/PassiveSlot05
        IceMage_GuideoftheNorthWind|북풍의 인도자|IceMage/PassiveSlot06
        HighHealer_LifeLink|라이프 링크|HighHealer/Slot01
        HighHealer_PhantomPain|팬텀 페인|HighHealer/Slot02
        HighHealer_NerveStrike|생명의 고동|HighHealer/Slot03
        HighHealer_LifeShield|프로텍션|HighHealer/Slot04
        HighHealer_SummonAngel|서먼 루미너스|HighHealer/Slot05
        HighHealer_WingOfAngel|윙 오브 엔젤|HighHealer/Slot06
        HighHealer_LifeLink_Fairy_1|서먼 스프라이트|HighHealer/Slot01
        HighHealer_PhantomPain_Panic|나이트메어|HighHealer/Slot02
        HighHealer_AutoShield|오토 실드|HighHealer/PassiveSlot01
        ExpertHealer_Energize|바이탈 부스트|HighHealer/PassiveSlot02
        ExpertHealer_Passive3_Support|전투 숙련: 지원|HighHealer/PassiveSlot03
        ExpertHealer_Passive4_Renew|소생|HighHealer/PassiveSlot04
        ExpertHealer_Passive5_Trauma|전이|HighHealer/PassiveSlot05
        ExpertHealer_Passive6_LuminousShard|루미너스 샤드|HighHealer/PassiveSlot06
        HighBard_StringShot|스트링 샷|HighBard/Slot01
        HighBard_MelodyShock|멜로디 쇼크|HighBard/Slot02
        HighBard_TripleStroke|트리플 스트로크|HighBard/Slot03
        HighBard_BardsTale|바즈 테일|HighBard/Slot04
        HighBard_FeignDeath|죽은 척 하기|HighBard/Slot05
        HighBard_SymphonyOfDestruction|배틀 심포니|HighBard/Slot06
        HighBard_BardsTale_Valor|용맹의 찬가|HighBard/Slot04
        HighBard_BardsTale_Revenge|반격의 서곡|HighBard/Slot04
        HighBard_BardsTale_Reprise|희망의 송가|HighBard/Slot04
        HighBard_FeignDeath_Windmill|라이징 윈드밀|HighBard/Slot05
        ExpertBard_FineTuning|파인 튜닝|HighBard/Slot05
        ExpertBard_Fever|피버|HighBard/PassiveSlot01
        ExpertBard_BattleSymphony|즉흥 연주|HighBard/PassiveSlot02
        ExpertBard_Passive3_Support|전투 숙련: 지원|HighBard/PassiveSlot03
        ExpertBard_Passive4_Inspiration|전장의 노래|HighBard/PassiveSlot04
        ExpertBard_Passive5_Frenzy|광란의 연주|HighBard/PassiveSlot05
        ExpertBard_Passive6_Invigorate|활력|HighBard/PassiveSlot06
        Dancer_NaturalTurn|내추럴 턴|Dancer/Slot01
        Dancer_Windmill|윈드밀|Dancer/Slot02
        Dancer_FencingLine|프론트 스텝|Dancer/Slot03
        Dancer_Pirouette|피루엣|Dancer/Slot04
        Dancer_Finale_Ready|피날레|Dancer/Slot06
        Dancer_Adlib_Passion|애드리브|Dancer/Slot05
        Dancer_Adlib_Elegance|애드리브|Dancer/Slot05
        Dancer_Adlib_Combination|애드리브|Dancer/Slot05
        Dancer_Finale_Finish|피날레|Dancer/Slot06
        Dancer_Inspiration|영감|Dancer/PassiveSlot01
        Dancer_Encore|앵콜|Dancer/PassiveSlot02
        Dancer_CombatMastery|전투 숙련: 쾌속|Dancer/PassiveSlot03
        Dancer_ExpertDance|숙련된 댄스|Dancer/PassiveSlot04
        Dancer_ClosedPosition|클로즈드 포지션|Dancer/PassiveSlot05
        Dancer_FlowerAndButterfly|꽃과 나비|Dancer/PassiveSlot06
        HighThief_ThrowingBomb|쓰로잉 봄|HighThief/Slot01
        HighThief_ScrewDagger|스크류 대거|HighThief/Slot02
        HighThief_BackStab|기습|HighThief/Slot03
        HighThief_PoisonTrap|포이즌 트랩|HighThief/Slot04
        HighThief_Hide|은신|HighThief/Slot05
        HighThief_BlitzRush|블리츠 러시|HighThief/Slot06
        HighThief_SneakAttack|스닉 어택|HighThief/PassiveSlot02
        HighThief_ComboAttack_Passive_03|전투 숙련: 쾌속|HighThief/PassiveSlot03
        HighThief_PoisonAttack_Passive_05|포이즌 어택|HighThief/PassiveSlot04
        HighThief_ExtraBlow_Passive_06|포이즌 익스플로전|HighThief/PassiveSlot05
        ExpertThief_AdrenalinePlus_C1|아드레날린+|HighThief/PassiveSlot01
        HighThief_QuickHands_Passive_06|퀵 핸즈|HighThief/PassiveSlot06
        DualBlades_OuterSlash|스플릿 슬래시|DualBlades/Slot01
        DualBlades_DoubleCrescent|더블 크레센트|DualBlades/Slot02
        DualBlades_HurricaneDance|스핀 러시|DualBlades/Slot03
        DualBlades_HowlingGale|하울링 게일|DualBlades/Slot04
        DualBlades_GlidingFury|글라이딩 퓨리|DualBlades/Slot05
        DualBlades_FinalHit|파이널 히트|DualBlades/Slot06
        DualBlades_OuterSlash_Strong|스플릿 다운|DualBlades/Slot01
        DualBlades_DoubleCrescent_Strong|라이징 크레센트|DualBlades/Slot02
        DualBlades_HurricaneDance_Strong|스크류 러시|DualBlades/Slot03
        DualBlades_HowlingGale_Strong|하울링 템페스트|DualBlades/Slot04
        DualBlades_GlidingFury_Strong|라이트닝 퓨리|DualBlades/Slot05
        DualBlades_RisingAspirations|피어오르는 열망|DualBlades/PassiveSlot01
        DualBlades_Recharge|재충전|DualBlades/PassiveSlot02
        DualBlades_BreakedHit_Passive_03|전투 숙련: 파멸|DualBlades/PassiveSlot03
        DualBlades_Vigor_Passive_04|활기|DualBlades/PassiveSlot04
        DualBlades_Wind_Passive_05|바람 칼날|DualBlades/PassiveSlot05
        DualBlades_VacuumShura_Passive_06|교차하는 검격|DualBlades/PassiveSlot06
        """;

    private static BattleGameData Parse(string classes, string skills)
        => new(classes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Split('|')).Select(x => new BattleGameClass(x[0], x[1])),
            skills.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Split('|'))
                .Select(x => new BattleGameSkill(x[0], x[1], x[2].Split(';').Select(s => s.Split('/')).Select(s => new BattleGameSlot(s[0], s[1])).ToArray())));
}
