# 배틀 스킬 ID 대응표

2026-11-01에 배틀 스킬·패시브 ID를 몰리 자체 ID(`buster_shot` 등)에서 게임 데이터 `Skill.Id`로 바꿨다. 클래스 원본도 Google Sheets `클래스`·`스킬`·`패시브스킬` 탭 대신 게임 데이터(`CombatJob`·`SkillSlot`·`Skill`)에서 읽는다([배틀 설계 8.4](README.md#84-클래스-구성게임-데이터)).

- 이 표는 옛 문서·커밋·Issue에 남은 이전 ID를 현재 ID로 찾기 위한 기록이다. 새 작업에는 현재 ID만 쓴다.
- 바꿀 때 배틀 결과가 달라지지 않았는지 같은 난수로 13개 클래스 조합 3,120판을 돌려 이벤트 로그가 모두 같은지 확인했다.
- 구글 시트 이름이 게임 데이터와 달랐던 6개(`전투 숙련: 위험`→`위협`, `포춘 볼트`→`포츈 볼트`, `레피드 파이어`→`래피드 파이어`, `생명의 고통`→`생명의 고동`, `오든 실드`→`오토 실드`, `아이시클 섀터`→`아이시클 섀클`)는 게임 데이터 이름을 따랐고, 배틀 시트 문구의 같은 이름도 함께 고쳤다.
- `ad_lib`(애드리브)는 게임 데이터에 해당 스킬이 없는 배틀 전용 버튼이라 `Molly_Dancer_Adlib`으로 두었다. 애드리브 파생 3종과 피날레 마무리는 게임 데이터 이름이 같아 `배틀이름교체`로 구분한다.

| 클래스 | 구분 | 이전 ID | 현재 ID | 이름 | 슬롯 |
| --- | --- | --- | --- | --- | --- |
| 전사 | 일반 | `consecutive_slash` | `ExpertWarrior_RapidSlash` | 연속 베기 | Slot01 |
| 전사 | 일반 | `blade_smash` | `ExpertWarrior_BladeSmash` | 블레이드 스매시 | Slot02 |
| 전사 | 일반 | `shield_bash` | `ExpertWarrior_ShieldBash` | 방패 치기 | Slot03 |
| 전사 | 일반 | `thrust` | `ExpertWarrior_DashStab` | 찌르기 | Slot04 |
| 전사 | 일반 | `battlefield_shout` | `ExpertWarrior_BattleCry` | 전장의 함성 | Slot05 |
| 전사 | 궁극기 | `blade_impact` | `ExpertWarrior_SwordQuake` | 블레이드 임팩트 | Slot06 |
| 전사 | 파생 | `amaranth_kick` | `ExpertWarrior_AmaranthKick` | 아마란스 킥 | Slot03 |
| 전사 | 파생 | `quake` | `ExpertWarrior_BreakExtend` | 퀘이크 | Slot02 |
| 전사 | 패시브 | `counter_attack` | `ExpertWarrior_Block` | 카운터 어택 | PassiveSlot01 |
| 전사 | 패시브 | `fighting_spirit` | `ExpertWarrior_FightingSpirit` | 투지 | PassiveSlot02 |
| 전사 | 패시브 | `combat_mastery_protection_warrior` | `ExpertWarrior_Passive3_Evasion` | 전투 숙련: 수호 | PassiveSlot03 |
| 전사 | 패시브 | `vengeance` | `ExpertWarrior_Passive4_Vengeance` | 복수심 | PassiveSlot04 |
| 전사 | 패시브 | `seize_opportunity` | `ExpertWarrior_Passive5_DownAttack` | 기회 포착 | PassiveSlot05 |
| 전사 | 패시브 | `intuition` | `ExpertWarrior_Passive6_Instinct` | 직감 | PassiveSlot06 |
| 대검전사 | 일반 | `spinning_slash` | `GreatSwordWarrior_SpinningSlash` | 회전 베기 | Slot01 |
| 대검전사 | 일반 | `rising_smash` | `GreatSwordWarrior_UpperSmash` | 라이징 스매시 | Slot02 |
| 대검전사 | 일반 | `shoulder_bash` | `GreatSwordWarrior_Blockade_Back` | 어깨 치기 | Slot03 |
| 대검전사 | 일반 | `cleave` | `GreatSwordWarrior_Slash` | 가르기 | Slot04 |
| 대검전사 | 일반 | `greatsword_art` | `GreatSwordWarrior_Finish` | 대검술 | Slot05 |
| 대검전사 | 궁극기 | `desperate_strike` | `GreatSwordWarrior_MortalStrike` | 필사의 일격 | Slot06 |
| 대검전사 | 파생 | `stomp` | `GreatSwordWarrior_Blockade_Front` | 발구르기 | Slot03 |
| 대검전사 | 파생 | `earth_sweep` | `GreatSwordWarrior_Finish_Second` | 대지 휩쓸기 | Slot05 |
| 대검전사 | 파생 | `heart_pierce` | `GreatSwordWarrior_Finish_First` | 심장 찌르기 | Slot05 |
| 대검전사 | 파생 | `wind_cleave` | `GreatSwordWarrior_Finish_Third` | 바람 가르기 | Slot05 |
| 대검전사 | 패시브 | `onslaught` | `GreatSwordWarrior_Assault` | 맹공 | PassiveSlot01 |
| 대검전사 | 패시브 | `indomitable` | `GreatSwordWarrior_Fortitude` | 불굴 | PassiveSlot02 |
| 대검전사 | 패시브 | `combat_mastery_might_greatsword` | `GreatSwordWarrior_CombatMastery` | 전투 숙련: 패기 | PassiveSlot03 |
| 대검전사 | 패시브 | `retaliation_greatsword` | `GreatSwordWarrior_Revenge` | 보복 | PassiveSlot04 |
| 대검전사 | 패시브 | `brawl` | `GreatSwordWarrior_Brawl` | 난투 | PassiveSlot05 |
| 대검전사 | 패시브 | `crushed_wound` | `GreatSwordWarrior_CrushedWound` | 뭉개진 상처 | PassiveSlot06 |
| 검술사 | 일반 | `steel_thrust` | `SwordMaster_SteelWedge` | 강철 쐐기 | Slot01 |
| 검술사 | 일반 | `scabbard_strike` | `SwordMaster_SwordAndScabbard` | 칼집 치기 | Slot02 |
| 검술사 | 일반 | `gale_slash` | `SwordMaster_SwordDance` | 질풍 베기 | Slot03 |
| 검술사 | 일반 | `secret_sword` | `SwordMaster_Hiken` | 비검 | Slot04 |
| 검술사 | 일반 | `insight` | `SwordMaster_Detection` | 간파 | Slot05 |
| 검술사 | 궁극기 | `single_flash` | `SwordMaster_Flash` | 일섬 | Slot06 |
| 검술사 | 파생 | `secret_sword_steel_thrust` | `SwordMaster_Hiken_SteelWedge` | 비검: 강철 쐐기 | Slot04 |
| 검술사 | 파생 | `secret_sword_scabbard_strike` | `SwordMaster_Hiken_SwordAndScabbard` | 비검: 칼집 치기 | Slot04 |
| 검술사 | 파생 | `secret_sword_gale_slash` | `SwordMaster_Hiken_SwordDance` | 비검: 질풍 베기 | Slot04 |
| 검술사 | 패시브 | `concentration` | `SwordMaster_Focus` | 집중 | PassiveSlot01 |
| 검술사 | 패시브 | `sword_stance` | `SwordMaster_SwiftPosture` | 질풍태세 | PassiveSlot02 |
| 검술사 | 패시브 | `combat_mastery_risk` | `SwordMaster_CombatMastery` | 전투 숙련: 위협 | PassiveSlot03 |
| 검술사 | 패시브 | `linked_swordsmanship` | `SwordMaster_ComboSwordSkill` | 연계 검술 | PassiveSlot04 |
| 검술사 | 패시브 | `sharp_eye` | `SwordMaster_KeenEye` | 날카로운 눈 | PassiveSlot05 |
| 검술사 | 패시브 | `first_strike` | `SwordMaster_Initiative` | 선수필승 | PassiveSlot06 |
| 궁수 | 일반 | `arrow_revolver` | `HighArcher_ArrowRevolver` | 애로우 리볼버 | Slot01 |
| 궁수 | 일반 | `magnum_shot` | `HighArcher_MagnumShot` | 매그넘 샷 | Slot02 |
| 궁수 | 일반 | `side_step` | `HighArcher_SideStep` | 사이드 스텝 | Slot03 |
| 궁수 | 일반 | `hawk_shot` | `HighArcher_HawkShot` | 호크 샷 | Slot04 |
| 궁수 | 일반 | `escape_step` | `HighArcher_EscapeStep` | 이스케이프 스텝 | Slot05 |
| 궁수 | 궁극기 | `blazing_trail` | `HighArcher_FireArrow` | 작열의 궤적 | Slot06 |
| 궁수 | 패시브 | `wind_flow` | `HighArcher_RushingWind` | 바람결 | PassiveSlot01 |
| 궁수 | 패시브 | `weak_point_piercing` | `ExpertArcher_PiercingWeakness` | 약점 관통 | PassiveSlot02 |
| 궁수 | 패시브 | `combat_mastery_swift_archer` | `ExpertArcher_CombatMastery` | 전투 숙련: 쾌속 | PassiveSlot03 |
| 궁수 | 패시브 | `multi_shot` | `HighArcher_Knockback` | 다발사격 | PassiveSlot04 |
| 궁수 | 패시브 | `propulsion` | `ExpertArcher_Impetus` | 추진력 | PassiveSlot05 |
| 궁수 | 패시브 | `tailwind` | `ExpertArcher_Tailwind` | 순풍 | PassiveSlot06 |
| 석궁사수 | 일반 | `buster_shot` | `Arbalist_MountingShock` | 버스터 샷 | Slot01 |
| 석궁사수 | 일반 | `shock_explosion` | `Arbalist_ShockExplosion` | 쇼크 익스플로전 | Slot02 |
| 석궁사수 | 일반 | `sliding_step` | `Arbalist_SlipThrough` | 슬라이딩 스텝 | Slot03 |
| 석궁사수 | 일반 | `gusting_bolt` | `Arbalist_GustingVolt` | 거스팅 볼트 | Slot04 |
| 석궁사수 | 일반 | `spreading_bolt` | `Arbalist_SpreadingVolt` | 스프레딩 볼트 | Slot05 |
| 석궁사수 | 궁극기 | `hell_fire` | `Arbalist_BoltExplosion` | 헬 파이어 | Slot06 |
| 석궁사수 | 패시브 | `extra_action` | `Arbalist_ExtraAction` | 엑스트라 액션 | PassiveSlot01 |
| 석궁사수 | 패시브 | `driving_force` | `Arbalist_DrivingForce` | 드라이빙 포스 | PassiveSlot02 |
| 석궁사수 | 패시브 | `combat_mastery_risk_crossbow` | `Arbalist_Critical` | 전투 숙련: 위협 | PassiveSlot03 |
| 석궁사수 | 패시브 | `quick_attack` | `Arbalist_QuickAttack` | 퀵 어택 | PassiveSlot04 |
| 석궁사수 | 패시브 | `extend_magazine` | `Arbalist_ReinforcedBolt` | 익스텐드 매거진 | PassiveSlot05 |
| 석궁사수 | 패시브 | `fortune_bolt` | `Arbalist_Equilibrium` | 포츈 볼트 | PassiveSlot06 |
| 마법사 | 일반 | `lightning` | `HighMage_Lightning` | 라이트닝 | Slot01 |
| 마법사 | 일반 | `fire_ball` | `HighMage_Fireball` | 파이어 볼 | Slot02 |
| 마법사 | 일반 | `ice_dagger` | `HighMage_IceDagger` | 아이스 대거 | Slot03 |
| 마법사 | 일반 | `telekinesis` | `HighMage_Telekinesis` | 텔레키네시스 | Slot04 |
| 마법사 | 일반 | `mana_storm` | `HighMage_ManaStorm` | 마나 스톰 | Slot05 |
| 마법사 | 궁극기 | `mana_infinity` | `HighMage_InfinityManaLoop` | 마나 인피니티 | Slot06 |
| 마법사 | 파생 | `mana_storm_blink` | `HighMage_ManaStorm_Blink` | 마나 스톰 | Slot05 |
| 마법사 | 패시브 | `meditation` | `HighMage_Meditation` | 메디테이션 | PassiveSlot01 |
| 마법사 | 패시브 | `elemental_harmony` | `HighMage_EnchantMagic` | 엘리멘탈 하모니 | PassiveSlot02 |
| 마법사 | 패시브 | `combat_mastery_finesse_mage` | `HighMage_Multihit_Passive_03` | 전투 숙련: 기교 | PassiveSlot03 |
| 마법사 | 패시브 | `elemental_master` | `HighMage_ElementalMaster_Passive_04` | 엘리멘탈 마스터 | PassiveSlot04 |
| 마법사 | 패시브 | `arcane_power` | `HighMage_ElementalList_Passive_05` | 아케인 파워 | PassiveSlot05 |
| 마법사 | 패시브 | `over_surge` | `HighMage_OverSurge_Passive_06` | 오버서지 | PassiveSlot06 |
| 화염술사 | 일반 | `rapid_fire` | `FireMage_RapidFire` | 래피드 파이어 | Slot01 |
| 화염술사 | 일반 | `fire_storm` | `FireMage_Firestorm` | 파이어 스톰 | Slot02 |
| 화염술사 | 일반 | `flame_cannon` | `FireMage_FlameCannon` | 플레임 캐논 | Slot03 |
| 화염술사 | 일반 | `flashover` | `FireMage_FlashOver` | 플래시오버 | Slot04 |
| 화염술사 | 일반 | `explosion` | `FireMage_Ignite` | 익스플로전 | Slot05 |
| 화염술사 | 궁극기 | `inferno` | `FireMage_Incinerate` | 인페르노 | Slot06 |
| 화염술사 | 파생 | `burst_cannon` | `FireMage_FlameCannon_Enhanced_Dummy` | 버스트 캐논 | Slot03 |
| 화염술사 | 패시브 | `burning_soul` | `FireMage_BurningSoul` | 버닝 소울 | PassiveSlot01 |
| 화염술사 | 패시브 | `blaze` | `FireMage_Flame` | 블레이즈 | PassiveSlot02 |
| 화염술사 | 패시브 | `combat_mastery_finesse` | `FireMage_Passive3_MultiHit` | 전투 숙련: 기교 | PassiveSlot03 |
| 화염술사 | 패시브 | `spark` | `FireMage_Passive4_Spark` | 스파크 | PassiveSlot04 |
| 화염술사 | 패시브 | `overheat` | `FireMage_Passive5_Overheat` | 오버히트 | PassiveSlot05 |
| 화염술사 | 패시브 | `ignite` | `FireMage_Passive6_Ignite` | 이그나이트 | PassiveSlot06 |
| 빙결술사 | 일반 | `crystal_edge` | `IceMage_CrystalEdge` | 크리스탈 엣지 | Slot01 |
| 빙결술사 | 일반 | `glacier_cutter` | `IceMage_SplitSlash` | 글래시어 커터 | Slot02 |
| 빙결술사 | 일반 | `ice_spikes` | `IceMage_IceSpike` | 아이스 스파이크 | Slot03 |
| 빙결술사 | 일반 | `freezing_field` | `IceMage_FreezingField` | 프리징 필드 | Slot04 |
| 빙결술사 | 일반 | `frozen_orb` | `IceMage_FrozenOrb` | 프로즌 오브 | Slot05 |
| 빙결술사 | 궁극기 | `absolute_zero` | `IceMage_IceAge` | 앱솔루트 제로 | Slot06 |
| 빙결술사 | 파생 | `icicle_shatter` | `IceMage_BreakExtend` | 아이시클 섀클 | Slot02 |
| 빙결술사 | 패시브 | `veil_of_winter` | `IceMage_VeilOfWinter` | 베일 오브 윈터 | PassiveSlot01 |
| 빙결술사 | 패시브 | `icicle_mark` | `IceMage_MarkOfWinter` | 아이시클 마크 | PassiveSlot02 |
| 빙결술사 | 패시브 | `combat_mastery_protection_ice_mage` | `IceMage_CombatMastery` | 전투 숙련: 수호 | PassiveSlot03 |
| 빙결술사 | 패시브 | `scattered_frost` | `IceMage_Fluttering_Frost` | 흩날리는 서리 | PassiveSlot04 |
| 빙결술사 | 패시브 | `piercing_cold` | `IceMage_Piercing_Chill` | 사무치는 냉기 | PassiveSlot05 |
| 빙결술사 | 패시브 | `guide_of_north_wind` | `IceMage_GuideoftheNorthWind` | 북풍의 인도자 | PassiveSlot06 |
| 힐러 | 일반 | `life_link` | `HighHealer_LifeLink` | 라이프 링크 | Slot01 |
| 힐러 | 일반 | `phantom_pain` | `HighHealer_PhantomPain` | 팬텀 페인 | Slot02 |
| 힐러 | 일반 | `pain_of_life` | `HighHealer_NerveStrike` | 생명의 고동 | Slot03 |
| 힐러 | 일반 | `protection` | `HighHealer_LifeShield` | 프로텍션 | Slot04 |
| 힐러 | 일반 | `summon_luminous` | `HighHealer_SummonAngel` | 서먼 루미너스 | Slot05 |
| 힐러 | 궁극기 | `wing_of_angel` | `HighHealer_WingOfAngel` | 윙 오브 엔젤 | Slot06 |
| 힐러 | 파생 | `summon_sprite` | `HighHealer_LifeLink_Fairy_1` | 서먼 스프라이트 | Slot01 |
| 힐러 | 파생 | `nightmare` | `HighHealer_PhantomPain_Panic` | 나이트메어 | Slot02 |
| 힐러 | 패시브 | `oath_shield` | `HighHealer_AutoShield` | 오토 실드 | PassiveSlot01 |
| 힐러 | 패시브 | `vital_boost` | `ExpertHealer_Energize` | 바이탈 부스트 | PassiveSlot02 |
| 힐러 | 패시브 | `combat_mastery_support_healer` | `ExpertHealer_Passive3_Support` | 전투 숙련: 지원 | PassiveSlot03 |
| 힐러 | 패시브 | `resurgence` | `ExpertHealer_Passive4_Renew` | 소생 | PassiveSlot04 |
| 힐러 | 패시브 | `transference` | `ExpertHealer_Passive5_Trauma` | 전이 | PassiveSlot05 |
| 힐러 | 패시브 | `luminous_shard` | `ExpertHealer_Passive6_LuminousShard` | 루미너스 샤드 | PassiveSlot06 |
| 음유시인 | 일반 | `string_shot` | `HighBard_StringShot` | 스트링 샷 | Slot01 |
| 음유시인 | 일반 | `melody_shock` | `HighBard_MelodyShock` | 멜로디 쇼크 | Slot02 |
| 음유시인 | 일반 | `triple_stroke` | `HighBard_TripleStroke` | 트리플 스트로크 | Slot03 |
| 음유시인 | 일반 | `bards_tale` | `HighBard_BardsTale` | 바즈 테일 | Slot04 |
| 음유시인 | 일반 | `feign_death` | `HighBard_FeignDeath` | 죽은 척 하기 | Slot05 |
| 음유시인 | 궁극기 | `battle_symphony` | `HighBard_SymphonyOfDestruction` | 배틀 심포니 | Slot06 |
| 음유시인 | 파생 | `heroic_hymn` | `HighBard_BardsTale_Valor` | 용맹의 찬가 | Slot04 |
| 음유시인 | 파생 | `counter_overture` | `HighBard_BardsTale_Revenge` | 반격의 서곡 | Slot04 |
| 음유시인 | 파생 | `song_of_hope` | `HighBard_BardsTale_Reprise` | 희망의 송가 | Slot04 |
| 음유시인 | 파생 | `rising_windmill` | `HighBard_FeignDeath_Windmill` | 라이징 윈드밀 | Slot05 |
| 음유시인 | 파생 | `fine_tuning` | `ExpertBard_FineTuning` | 파인 튜닝 | Slot05 |
| 음유시인 | 패시브 | `fever` | `ExpertBard_Fever` | 피버 | PassiveSlot01 |
| 음유시인 | 패시브 | `improvised_performance` | `ExpertBard_BattleSymphony` | 즉흥 연주 | PassiveSlot02 |
| 음유시인 | 패시브 | `combat_mastery_support` | `ExpertBard_Passive3_Support` | 전투 숙련: 지원 | PassiveSlot03 |
| 음유시인 | 패시브 | `battlefield_song` | `ExpertBard_Passive4_Inspiration` | 전장의 노래 | PassiveSlot04 |
| 음유시인 | 패시브 | `ecstatic_performance` | `ExpertBard_Passive5_Frenzy` | 광란의 연주 | PassiveSlot05 |
| 음유시인 | 패시브 | `vitality` | `ExpertBard_Passive6_Invigorate` | 활력 | PassiveSlot06 |
| 댄서 | 일반 | `ad_lib` | `Molly_Dancer_Adlib` | 애드리브 | Slot05(파생에서 물려받음) |
| 댄서 | 일반 | `natural_turn` | `Dancer_NaturalTurn` | 내추럴 턴 | Slot01 |
| 댄서 | 일반 | `windmill` | `Dancer_Windmill` | 윈드밀 | Slot02 |
| 댄서 | 일반 | `front_step` | `Dancer_FencingLine` | 프론트 스텝 | Slot03 |
| 댄서 | 일반 | `pirouette` | `Dancer_Pirouette` | 피루엣 | Slot04 |
| 댄서 | 궁극기 | `finale` | `Dancer_Finale_Ready` | 피날레 | Slot06 |
| 댄서 | 파생 | `ad_lib_passion` | `Dancer_Adlib_Passion` | 애드리브: 정열 | Slot05 |
| 댄서 | 파생 | `ad_lib_grace` | `Dancer_Adlib_Elegance` | 애드리브: 우아 | Slot05 |
| 댄서 | 파생 | `ad_lib_harmony` | `Dancer_Adlib_Combination` | 애드리브: 화합 | Slot05 |
| 댄서 | 파생 | `finale_finish` | `Dancer_Finale_Finish` | 피날레: 마무리 | Slot06 |
| 댄서 | 패시브 | `inspiration` | `Dancer_Inspiration` | 영감 | PassiveSlot01 |
| 댄서 | 패시브 | `encore` | `Dancer_Encore` | 앵콜 | PassiveSlot02 |
| 댄서 | 패시브 | `combat_mastery_swift` | `Dancer_CombatMastery` | 전투 숙련: 쾌속 | PassiveSlot03 |
| 댄서 | 패시브 | `skilled_dance` | `Dancer_ExpertDance` | 숙련된 댄스 | PassiveSlot04 |
| 댄서 | 패시브 | `closed_position` | `Dancer_ClosedPosition` | 클로즈드 포지션 | PassiveSlot05 |
| 댄서 | 패시브 | `flower_and_butterfly` | `Dancer_FlowerAndButterfly` | 꽃과 나비 | PassiveSlot06 |
| 도적 | 일반 | `throwing_bomb` | `HighThief_ThrowingBomb` | 쓰로잉 봄 | Slot01 |
| 도적 | 일반 | `screw_dagger` | `HighThief_ScrewDagger` | 스크류 대거 | Slot02 |
| 도적 | 일반 | `ambush` | `HighThief_BackStab` | 기습 | Slot03 |
| 도적 | 일반 | `poison_trap` | `HighThief_PoisonTrap` | 포이즌 트랩 | Slot04 |
| 도적 | 일반 | `stealth` | `HighThief_Hide` | 은신 | Slot05 |
| 도적 | 궁극기 | `blitz_rush` | `HighThief_BlitzRush` | 블리츠 러시 | Slot06 |
| 도적 | 패시브 | `sneak_attack` | `HighThief_SneakAttack` | 스닉 어택 | PassiveSlot02 |
| 도적 | 패시브 | `combat_mastery_swift_thief` | `HighThief_ComboAttack_Passive_03` | 전투 숙련: 쾌속 | PassiveSlot03 |
| 도적 | 패시브 | `poison_attack` | `HighThief_PoisonAttack_Passive_05` | 포이즌 어택 | PassiveSlot04 |
| 도적 | 패시브 | `poison_explosion` | `HighThief_ExtraBlow_Passive_06` | 포이즌 익스플로전 | PassiveSlot05 |
| 도적 | 패시브 | `adrenaline_plus` | `ExpertThief_AdrenalinePlus_C1` | 아드레날린+ | PassiveSlot01 |
| 도적 | 패시브 | `quick_hands` | `HighThief_QuickHands_Passive_06` | 퀵 핸즈 | PassiveSlot06 |
| 듀얼블레이드 | 일반 | `split_slash` | `DualBlades_OuterSlash` | 스플릿 슬래시 | Slot01 |
| 듀얼블레이드 | 일반 | `double_crescent` | `DualBlades_DoubleCrescent` | 더블 크레센트 | Slot02 |
| 듀얼블레이드 | 일반 | `spin_rush` | `DualBlades_HurricaneDance` | 스핀 러시 | Slot03 |
| 듀얼블레이드 | 일반 | `howling_gale` | `DualBlades_HowlingGale` | 하울링 게일 | Slot04 |
| 듀얼블레이드 | 일반 | `gliding_fury` | `DualBlades_GlidingFury` | 글라이딩 퓨리 | Slot05 |
| 듀얼블레이드 | 궁극기 | `final_hit` | `DualBlades_FinalHit` | 파이널 히트 | Slot06 |
| 듀얼블레이드 | 파생 | `split_down` | `DualBlades_OuterSlash_Strong` | 스플릿 다운 | Slot01 |
| 듀얼블레이드 | 파생 | `rising_crescent` | `DualBlades_DoubleCrescent_Strong` | 라이징 크레센트 | Slot02 |
| 듀얼블레이드 | 파생 | `screw_rush` | `DualBlades_HurricaneDance_Strong` | 스크류 러시 | Slot03 |
| 듀얼블레이드 | 파생 | `howling_tempest` | `DualBlades_HowlingGale_Strong` | 하울링 템페스트 | Slot04 |
| 듀얼블레이드 | 파생 | `lightning_fury` | `DualBlades_GlidingFury_Strong` | 라이트닝 퓨리 | Slot05 |
| 듀얼블레이드 | 패시브 | `rising_desire` | `DualBlades_RisingAspirations` | 피어오르는 열망 | PassiveSlot01 |
| 듀얼블레이드 | 패시브 | `recharge` | `DualBlades_Recharge` | 재충전 | PassiveSlot02 |
| 듀얼블레이드 | 패시브 | `combat_mastery_destruction_dual_blade` | `DualBlades_BreakedHit_Passive_03` | 전투 숙련: 파멸 | PassiveSlot03 |
| 듀얼블레이드 | 패시브 | `vigor` | `DualBlades_Vigor_Passive_04` | 활기 | PassiveSlot04 |
| 듀얼블레이드 | 패시브 | `wind_blade` | `DualBlades_Wind_Passive_05` | 바람 칼날 | PassiveSlot05 |
| 듀얼블레이드 | 패시브 | `crossing_slash` | `DualBlades_VacuumShura_Passive_06` | 교차하는 검격 | PassiveSlot06 |
