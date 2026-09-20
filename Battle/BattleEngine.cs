namespace Molly.Battle;

/// <summary>Discord·DB·HTTP를 참조하지 않는 단일 전투 시뮬레이터입니다.</summary>
public sealed class BattleEngine
{
    public BattleResult Simulate(CharacterBattleSnapshot a, CharacterBattleSnapshot b, BattleDataSnapshot data, IBattleRandom random)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(random);
        if (!data.Classes.TryGetValue(a.ClassId, out var aClass) || !aClass.IsBattleReady || !data.Classes.TryGetValue(b.ClassId, out var bClass) || !bClass.IsBattleReady) throw new InvalidDataException("등록 캐릭터의 클래스에 해당하는 배틀 스킬 데이터가 아직 준비되지 않았습니다.");
        var rules = new Rules(data.Rules);
        var basePower = Math.Sqrt(Math.Max(1d, a.CombatPower) * Math.Max(1d, b.CombatPower));
        var left = Fighter.Create(a, PowerScale(a.CombatPower, basePower, rules), rules, data);
        var right = Fighter.Create(b, PowerScale(b.CombatPower, basePower, rules), rules, data);
        var events = new List<BattleEvent> { new("BattleStarted", left.Name, right.Name) };
        var actor = random.NextDouble() < .5 ? left : right;
        var major = 0;
        var surpriseCooldown = 0;
        while (major < rules.MaxActions && left.Hp > 0 && right.Hp > 0)
        {
            var target = ReferenceEquals(actor, left) ? right : left;
            events.Add(new("TurnStarted", actor.Name, target.Name));
            actor.TickCooldowns();
            var surpriseMultiplier = TrySurprise(actor, random, rules, ref surpriseCooldown, events);
            var actorHealed = false;
            var targetDamaged = false;
            var skill = ChooseSkill(actor, data, random);
            if (skill is null)
            {
                events.Add(new("NormalAttackUsed", actor.Name, target.Name));
                targetDamaged = Attack(actor, target, actor.Attack * rules.NormalAttackMultiplier * surpriseMultiplier, 1, random, rules, events);
            }
            else
            {
                var selectedId = skill.Id;
                skill = ResolveReuse(actor, skill, data) ?? skill;
                actor.Cooldowns[selectedId] = Math.Max(skill.Cooldown, rules.MinimumSkillCooldown);
                actor.LastSkillId = skill.Id;
                var derivedSkills = SelectImmediateDerivations(actor, skill, data, random).ToArray();
                var hideParent = skill.Effects.Count == 0 && derivedSkills.Length == 1 && data.Derivations.Any(x => x.ParentSkillId == skill.Id && x.ChildSkillId == derivedSkills[0].Id && x.ActivationMode == "무작위");
                if (!hideParent) events.Add(new("SkillUsed", actor.Name, target.Name, Detail: skill.Name));
                if (hideParent) events.Add(new("SkillUsed", actor.Name, target.Name, Detail: derivedSkills[0].Name));
                var resolved = ExecuteEffects(actor, target, skill, surpriseMultiplier, random, rules, events);
                foreach (var derived in derivedSkills)
                {
                    if (!hideParent) events.Add(new("DerivedSkillUsed", actor.Name, target.Name, Detail: derived.Name));
                    var child = ExecuteEffects(actor, target, derived, surpriseMultiplier, random, rules, events);
                    resolved.TargetDamaged |= child.TargetDamaged;
                    resolved.ActorHealed |= child.ActorHealed;
                }
                targetDamaged = resolved.TargetDamaged;
                actorHealed = resolved.ActorHealed;
            }
            if (targetDamaged && target.Hp > 0) events.Add(new("HpStatus", target.Name, Detail: HpStatus(target)));
            if (actorHealed) events.Add(new("HpStatus", actor.Name, Detail: HpStatus(actor)));
            major++;
            surpriseCooldown = Math.Max(0, surpriseCooldown - 1);
            if (left.Hp <= 0 || right.Hp <= 0) break;
            actor = target;
        }
        var outcome = left.Hp <= 0 && right.Hp <= 0 ? BattleOutcome.Draw
            : left.Hp <= 0 ? BattleOutcome.FighterBWin
            : right.Hp <= 0 ? BattleOutcome.FighterAWin
            : Math.Abs((double)left.Hp / left.MaxHp - (double)right.Hp / right.MaxHp) <= rules.DrawThreshold ? BattleOutcome.Draw
            : (double)left.Hp / left.MaxHp > (double)right.Hp / right.MaxHp ? BattleOutcome.FighterAWin : BattleOutcome.FighterBWin;
        events.Add(new("BattleEnded", outcome == BattleOutcome.FighterBWin ? right.Name : outcome == BattleOutcome.FighterAWin ? left.Name : "무승부"));
        return new BattleResult(outcome, major, left.Hp, left.MaxHp, right.Hp, right.MaxHp, events);
    }

    private static double PowerScale(int power, double basePower, Rules rules) => Math.Clamp(Math.Pow(Math.Max(1d, power) / basePower, rules.PowerExponent), rules.PowerMin, rules.PowerMax);
    private static double SkillMultiplier(BattleSkill skill, Rules rules)
    {
        if (skill.Kind == "궁극기" || skill.Id.Contains("finale", StringComparison.OrdinalIgnoreCase) || skill.Id.Contains("symphony", StringComparison.OrdinalIgnoreCase)) return rules.UltimateDamageMultiplier;
        var variation = (uint)StringComparer.Ordinal.GetHashCode(skill.Id) % 100 / 100d;
        return rules.SkillDamageMinMultiplier + variation * (rules.SkillDamageMaxMultiplier - rules.SkillDamageMinMultiplier);
    }

    private static double TrySurprise(Fighter actor, IBattleRandom random, Rules rules, ref int cooldown, List<BattleEvent> events)
    {
        if (cooldown > 0 || actor.SurpriseCount >= rules.MaxSurpriseEvents) return 1d;
        // 생활력과 매력은 서로 비교하지 않는다. 각각 자신만의 기준값·보정계수·상한으로 판정한다.
        if ((double)actor.Hp / actor.MaxHp <= rules.LifeSurpriseHpRatioThreshold && random.NextDouble() < rules.LifeSurpriseChance(actor.LifePower))
        {
            actor.SurpriseCount++; cooldown = rules.SurpriseCooldown;
            var healed = Math.Min(actor.MaxHp - actor.Hp, (int)Math.Round(actor.MaxHp * rules.LifeSurpriseHealRatio)); actor.Hp += healed;
            events.Add(new("SurpriseEventTriggered", actor.Name, Detail: "야전 응급처치")); events.Add(new("HealApplied", actor.Name, actor.Name, healed)); events.Add(new("HpStatus", actor.Name, Detail: HpStatus(actor)));
            return 1d;
        }
        if (random.NextDouble() >= rules.CharmSurpriseChance(actor.CharmPower)) return 1d;
        actor.SurpriseCount++; cooldown = rules.SurpriseCooldown;
        events.Add(new("SurpriseEventTriggered", actor.Name, Detail: "관중의 환호"));
        return rules.CharmSurpriseDamageMultiplier;
    }
    private static BattleSkill? ChooseSkill(Fighter actor, BattleDataSnapshot data, IBattleRandom random)
    {
        var choices = actor.SkillIds.Select(id => data.Skills.GetValueOrDefault(id)).Where(x => x is { Enabled: true, Kind: not "파생" } && actor.Cooldowns.GetValueOrDefault(x.Id) == 0 && x.Weight > 0).Cast<BattleSkill>().ToArray();
        if (choices.Length == 0) return null;
        var varied = choices.Where(x => x.Id != actor.LastSkillId).ToArray();
        if (varied.Length > 0) choices = varied;
        var point = random.NextDouble() * choices.Sum(x => x.Weight * (1d + x.Priority / 100d));
        foreach (var skill in choices) { point -= skill.Weight * (1d + skill.Priority / 100d); if (point <= 0) return skill; }
        return choices[^1];
    }

    private static BattleSkill? ResolveReuse(Fighter actor, BattleSkill skill, BattleDataSnapshot data)
        => data.Derivations.Where(x => x.ParentSkillId == skill.Id && x.Timing == "재사용 시" && IsDerivationEligible(actor, x))
            .OrderByDescending(x => x.Priority).Select(x => data.Skills[x.ChildSkillId]).FirstOrDefault();

    private static IEnumerable<BattleSkill> SelectImmediateDerivations(Fighter actor, BattleSkill skill, BattleDataSnapshot data, IBattleRandom random)
    {
        var candidates = data.Derivations.Where(x => x.ParentSkillId == skill.Id && x.Timing == "즉시" && IsDerivationEligible(actor, x)).GroupBy(x => x.Priority).OrderByDescending(x => x.Key).FirstOrDefault();
        if (candidates is null) return [];
        var available = candidates.Where(x => x.ActivationMode != "확률" || random.NextDouble() < x.Chance).ToArray();
        if (available.Length == 0) return [];
        var point = random.NextDouble() * available.Sum(x => Math.Max(1, x.Weight));
        foreach (var rule in available) { point -= Math.Max(1, rule.Weight); if (point <= 0) return [data.Skills[rule.ChildSkillId]]; }
        return [data.Skills[available[^1].ChildSkillId]];
    }

    private static bool IsDerivationEligible(Fighter actor, BattleDerivation rule)
    {
        if (rule.ConditionType != "자원보유") return rule.ActivationMode is "무작위" or "확률";
        var parts = rule.ConditionValue?.Split(['=', '>'], 2) ?? [];
        return parts.Length == 2 && int.TryParse(parts[1], out var value) && actor.Resources.GetValueOrDefault(parts[0]) >= value;
    }

    private static EffectResolution ExecuteEffects(Fighter actor, Fighter target, BattleSkill skill, double surpriseMultiplier, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        var resolution = new EffectResolution();
        foreach (var effect in skill.Effects)
        {
            if (actor.Hp <= 0 || target.Hp <= 0) break;
            var receiver = effect.Target == "자신" ? actor : target;
            switch (effect.Type)
            {
                case "피해":
                    // 다단 효과의 총 기본 위력은 유지하고 타격마다 나눕니다.
                    // 각 타격은 별도로 치명타·추가타를 판정하므로 연출과 변동성은 남습니다.
                    var hitBaseDamage = actor.Attack * SkillMultiplier(skill, rules) * surpriseMultiplier / effect.Count;
                    for (var hit = 0; hit < effect.Count && target.Hp > 0; hit++)
                    {
                        if (random.NextDouble() >= effect.Chance) continue;
                        resolution.TargetDamaged |= Attack(actor, receiver, hitBaseDamage, 1, random, rules, events);
                    }
                    break;
                case "회복":
                    for (var healIndex = 0; healIndex < effect.Count; healIndex++)
                    {
                        if (random.NextDouble() >= effect.Chance) continue;
                        var amount = Math.Max(1, (int)Math.Round(actor.MaxHp * rules.SkillHealRatio));
                        var healed = Math.Min(amount, receiver.MaxHp - receiver.Hp);
                        receiver.Hp += healed;
                        if (healed > 0)
                        {
                            events.Add(new("HealApplied", actor.Name, receiver.Name, healed));
                            resolution.ActorHealed |= ReferenceEquals(receiver, actor);
                        }
                    }
                    break;
                case "자원설정":
                    SetResource(actor, effect.StatusId, effect.FixedValue, events);
                    break;
                case "자원소모" when effect.StatusId is { } resourceId:
                    SetResource(actor, resourceId, effect.NumericReferenceMode == "전부" ? 0 : actor.Resources.GetValueOrDefault(resourceId) - effect.FixedValue, events);
                    break;
            }
        }
        return resolution;
    }
    private static void SetResource(Fighter fighter, string? id, int value, List<BattleEvent> events)
    {
        if (id is null || !fighter.ResourceDefinitions.TryGetValue(id, out var definition)) return;
        var capped = definition.Maximum == 0 ? Math.Max(0, value) : Math.Clamp(value, 0, definition.Maximum);
        fighter.Resources[id] = capped;
        events.Add(new("ResourceChanged", fighter.Name, Detail: definition.Name + " " + capped));
    }
    private static bool Attack(Fighter actor, Fighter target, double baseDamage, int count, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        for (var i = 0; i < count && target.Hp > 0; i++)
        {
            var amount = Math.Max(1, baseDamage - target.Defense * rules.DefenseCoefficient) * (rules.DamageVarianceMin + random.NextDouble() * (rules.DamageVarianceMax - rules.DamageVarianceMin));
            var critical = random.NextDouble() < rules.CriticalChance;
            if (critical) { amount *= rules.CriticalMultiplier; events.Add(new("CriticalHit", actor.Name, target.Name)); }
            var damage = Math.Max(1, (int)Math.Round(amount)); target.Hp = Math.Max(0, target.Hp - damage);
            events.Add(new("DamageDealt", actor.Name, target.Name, damage));
            if (target.Hp == 0) events.Add(new("CharacterDefeated", actor.Name, target.Name));
            if (target.Hp > 0 && random.NextDouble() < rules.AdditionalHitChance)
            {
                // 추가타는 이미 확정된 피해의 일부만 더하고, 치명타 판정을 따로 하지 않습니다.
                var additionalDamage = Math.Max(1, (int)Math.Round(damage * rules.AdditionalHitDamageRatio));
                target.Hp = Math.Max(0, target.Hp - additionalDamage);
                events.Add(new("AdditionalHit", actor.Name, target.Name, additionalDamage));
                if (target.Hp == 0) events.Add(new("CharacterDefeated", actor.Name, target.Name));
            }
        }
        return true;
    }

    private static string HpStatus(Fighter fighter) => ((double)fighter.Hp / fighter.MaxHp) switch
    {
        >= .85 => "아직 끄떡없습니다.", >= .60 => "조금씩 밀리기 시작합니다.", >= .30 => "상태가 심상치 않습니다.", _ => "간신히 버티고 있습니다."
    };

    private sealed class EffectResolution
    {
        public bool TargetDamaged;
        public bool ActorHealed;
    }

    private sealed class Fighter
    {
        public required string Name; public required int MaxHp; public required int Hp; public required double Attack; public required double Defense; public required int LifePower; public required int CharmPower; public required IReadOnlyList<string> SkillIds; public required IReadOnlyDictionary<string, BattleResource> ResourceDefinitions;
        public int SurpriseCount; public string? LastSkillId;
        public Dictionary<string, int> Cooldowns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Resources { get; } = new(StringComparer.Ordinal);
        public static Fighter Create(CharacterBattleSnapshot source, double scale, Rules rules, BattleDataSnapshot data)
        {
            var c = data.Classes[source.ClassId]; var max = Math.Max(1, (int)Math.Round(rules.BaseHp * scale));
            var fighter = new Fighter { Name = source.CharacterName, MaxHp = max, Hp = max, Attack = rules.BaseAttack * scale, Defense = rules.BaseDefense * scale, LifePower = source.LifePower, CharmPower = source.CharmPower, SkillIds = c.SkillIds, ResourceDefinitions = data.Resources };
            foreach (var id in c.SkillIds) if (data.Skills.TryGetValue(id, out var skill)) fighter.Cooldowns[id] = skill.InitialCooldown;
            foreach (var resource in data.Resources.Values) fighter.Resources[resource.Id] = resource.InitialValue;
            return fighter;
        }
        public void TickCooldowns() { foreach (var id in Cooldowns.Keys.ToArray()) Cooldowns[id] = Math.Max(0, Cooldowns[id] - 1); }
    }

    private sealed class Rules(IReadOnlyDictionary<string, BattleRule> values)
    {
        private double Number(string id) => values.TryGetValue(id, out var rule) && double.TryParse(rule.Value, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidDataException($"배틀규칙 시트의 필수 규칙 '{id}'이(가) 없습니다.");
        public double BaseHp => Number("base_max_hp"); public double BaseAttack => Number("base_attack"); public double BaseDefense => Number("base_defense");
        public double PowerExponent => Number("power_scale_exponent"); public double PowerMin => Number("power_scale_min"); public double PowerMax => Number("power_scale_max");
        public double DefenseCoefficient => Number("defense_coefficient"); public double DamageVarianceMin => Number("damage_variance_min"); public double DamageVarianceMax => Number("damage_variance_max");
        public double CriticalChance => Number("base_critical_chance"); public double CriticalMultiplier => Number("critical_damage_multiplier");
        public int MaxActions => checked((int)Number("max_major_actions")); public double DrawThreshold => Number("draw_hp_ratio_threshold");
        public double NormalAttackMultiplier => Number("normal_attack_multiplier"); public double SkillDamageMinMultiplier => Number("skill_damage_min_multiplier"); public double SkillDamageMaxMultiplier => Number("skill_damage_max_multiplier"); public double UltimateDamageMultiplier => Number("ultimate_damage_multiplier");
        public int MinimumSkillCooldown => checked((int)Number("minimum_skill_cooldown")); public double SkillHealRatio => Number("skill_heal_ratio"); public int MaxSurpriseEvents => checked((int)Number("max_surprise_events_per_actor")); public int SurpriseCooldown => checked((int)Number("surprise_event_global_cooldown"));
        public double LifeSurpriseHpRatioThreshold => Number("life_surprise_hp_ratio_threshold"); public double LifeSurpriseHealRatio => Number("life_surprise_heal_ratio"); public double CharmSurpriseDamageMultiplier => Number("charm_surprise_damage_multiplier");
        public double AdditionalHitChance => Number("additional_hit_chance"); public double AdditionalHitDamageRatio => Number("additional_hit_damage_ratio");
        public double LifeSurpriseChance(int lifePower) => SurpriseChance("life_surprise", lifePower);
        public double CharmSurpriseChance(int charmPower) => SurpriseChance("charm_surprise", charmPower);
        private double SurpriseChance(string prefix, int value) => Math.Min(Number(prefix + "_max_chance"), Number(prefix + "_base_chance") + Math.Min(2d, Math.Max(0, value) / Number(prefix + "_stat_reference")) * Number(prefix + "_stat_coefficient"));
    }
}
