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
            actor.TickResources(events);
            var damagedByStatus = actor.TickPeriodicEffects(rules, events);
            if (damagedByStatus && actor.Hp > 0) events.Add(new("HpStatus", actor.Name, Detail: HpStatus(actor)));
            if (actor.Hp <= 0) break;
            var actionBroken = actor.HasStatusEffect("브레이크");
            foreach (var expired in actor.TickStatuses(events))
            {
                var scheduled = SelectDerivation(actor, expired.SourceSkillId, "상태만료 시", data, random, expired.Id);
                if (scheduled is not null) actor.PendingSkillId = scheduled.Value.Child.Id;
            }
            if (actionBroken)
            {
                events.Add(new("BreakActionLost", target.Name, actor.Name));
                major++;
                actor = target;
                continue;
            }
            var surpriseMultiplier = TrySurprise(actor, random, rules, ref surpriseCooldown, events);
            var actorHealed = false;
            var targetDamaged = false;
            var skill = actor.TakePendingSkill(data) ?? ChooseSkill(actor, data, random);
            if (skill is null)
            {
                events.Add(new("NormalAttackUsed", actor.Name, target.Name));
                var normalAttackMultiplier = rules.NormalAttackMultiplier * (1d + actor.StatusValue("기본공격피해증가"));
                targetDamaged = Attack(actor, target, actor.Attack * normalAttackMultiplier * surpriseMultiplier, 1, random, rules, events);
            }
            else
            {
                var selectedId = skill.Id;
                skill = ResolveReuse(actor, skill, data) ?? skill;
                SpendSkillResource(actor, skill, data, events);
                actor.Cooldowns[selectedId] = Math.Max(skill.Cooldown, rules.MinimumSkillCooldown);
                actor.LastSkillId = skill.Id;
                // 효과로 자원을 얻은 뒤에 조건 파생을 판정해야 한다. 다만 효과가 없는 무작위 부모는
                // 실제로 발동한 자식 스킬만 제목으로 보여주기 위해 미리 한 번 선택한다.
                var preselectedDerivations = skill.Effects.Count == 0 ? SelectImmediateDerivations(actor, skill, data, random).ToArray() : [];
                // 부모 효과가 악상을 만든 뒤에 그 악상으로 실제 연주곡을 고르는 스킬이 있다.
                // 이 경우 부모는 버튼/선택용 가상 스킬이므로, 효과 로그도 실제 연주곡 제목 아래에 둔다.
                var effectEvents = new List<BattleEvent>();
                var resolved = ExecuteEffects(actor, target, skill, surpriseMultiplier, random, rules, effectEvents);
                var derivedSkills = preselectedDerivations.Length > 0 ? preselectedDerivations : SelectImmediateDerivations(actor, skill, data, random).ToArray();
                var replacement = derivedSkills.Length == 1 && data.Derivations.Any(x => x.ParentSkillId == skill.Id && x.ChildSkillId == derivedSkills[0].Id && x.ActivationMode == "대체");
                var hiddenRandomParent = preselectedDerivations.Length == 1 && data.Derivations.Any(x => x.ParentSkillId == skill.Id && x.ChildSkillId == preselectedDerivations[0].Id && x.ActivationMode == "무작위");
                var hideParent = replacement || hiddenRandomParent;
                if (!hideParent) events.Add(new("SkillUsed", actor.Name, target.Name, Detail: skill.Name));
                if (hideParent) events.Add(new("SkillUsed", actor.Name, target.Name, Detail: derivedSkills[0].Name));
                events.AddRange(effectEvents);
                foreach (var derived in derivedSkills)
                {
                    if (!hideParent) events.Add(new("DerivedSkillUsed", actor.Name, target.Name, Detail: derived.Name));
                    var child = ExecuteEffects(actor, target, derived, surpriseMultiplier, random, rules, events);
                    resolved.TargetDamaged |= child.TargetDamaged;
                    resolved.ActorHealed |= child.ActorHealed;
                }
                GainSkillResource(actor, skill, data, events);
                SpendDeferredSkillResource(actor, skill, data, events);
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
        var choices = actor.SkillIds.Select(id => data.Skills.GetValueOrDefault(id)).Where(x => x is { Enabled: true, Kind: not "파생" } && (actor.Cooldowns.GetValueOrDefault(x.Id) == 0 || ResolveReuse(actor, x, data) is not null) && x.Weight > 0 && CanPaySkillResource(actor, x, data) && (x.Effects.Count > 0 || HasImmediateDerivation(actor, x, data))).Cast<BattleSkill>().ToArray();
        if (choices.Length == 0) return null;
        // 재사용 파생은 플레이어가 같은 버튼을 다시 누른 동작을 자동전투에서 표현하는 경로다.
        // 진행 중인 상태를 끝내는 재사용 후보가 있으면 일반 후보보다 먼저 선택한다.
        var reusable = choices.Where(x => ResolveReuse(actor, x, data) is not null).ToArray();
        if (reusable.Length > 0) choices = reusable;
        var varied = choices.Where(x => x.Id != actor.LastSkillId).ToArray();
        if (varied.Length > 0) choices = varied;
        var point = random.NextDouble() * choices.Sum(x => x.Weight * (1d + x.Priority / 100d));
        foreach (var skill in choices) { point -= skill.Weight * (1d + skill.Priority / 100d); if (point <= 0) return skill; }
        return choices[^1];
    }

    private static BattleSkill? ResolveReuse(Fighter actor, BattleSkill skill, BattleDataSnapshot data)
        => SelectDerivation(actor, skill.Id, "재사용 시", data, null)?.Child;

    private static bool HasImmediateDerivation(Fighter actor, BattleSkill skill, BattleDataSnapshot data)
        => data.Derivations.Any(x => x.ParentSkillId == skill.Id && x.Timing == "즉시" && IsDerivationEligible(actor, x));

    private static IEnumerable<BattleSkill> SelectImmediateDerivations(Fighter actor, BattleSkill skill, BattleDataSnapshot data, IBattleRandom random)
    {
        var selected = SelectDerivation(actor, skill.Id, "즉시", data, random);
        return selected is null ? [] : [selected.Value.Child];
    }

    private static (BattleDerivation Rule, BattleSkill Child)? SelectDerivation(Fighter actor, string parentSkillId, string timing, BattleDataSnapshot data, IBattleRandom? random, string? expiringStatusId = null)
    {
        foreach (var group in data.Derivations.Where(x => x.ParentSkillId == parentSkillId && x.Timing == timing && IsDerivationEligible(actor, x, expiringStatusId)).GroupBy(x => x.Priority).OrderByDescending(x => x.Key))
        {
            var available = group.Where(x => x.ActivationMode != "확률" || random is null || random.NextDouble() < x.Chance).ToArray();
            if (available.Length == 0) continue;
            var selectable = available.Where(x => x.ActivationMode != "확률" || random is not null).ToArray();
            if (selectable.Length == 0) continue;
            var selected = random is null || selectable.All(x => x.Weight <= 0)
                ? selectable[0]
                : PickWeighted(selectable, random);
            return (selected, data.Skills[selected.ChildSkillId]);
        }
        return null;
    }

    private static BattleDerivation PickWeighted(IReadOnlyList<BattleDerivation> rules, IBattleRandom random)
    {
        var point = random.NextDouble() * rules.Sum(x => Math.Max(1, x.Weight));
        foreach (var rule in rules) { point -= Math.Max(1, rule.Weight); if (point <= 0) return rule; }
        return rules[^1];
    }

    private static bool IsDerivationEligible(Fighter actor, BattleDerivation rule, string? expiringStatusId = null)
        => rule.ConditionType switch
        {
            null or "" => true,
            "자원보유" => ResourceCondition(actor, rule.ConditionValue),
            "상태효과보유" => rule.ConditionValue is { } id && (actor.Statuses.ContainsKey(id) || expiringStatusId == id),
            // 현재 악상 데이터는 바즈 테일이 세 곡 중 하나를 고르는 선택 표식이다.
            // 실제 보유 자원을 조건으로 쓰는 파생은 자원보유 형식으로 명시한다.
            "악상" when rule.ActivationMode == "무작위" => true,
            "새로운영감" when rule.ActivationMode == "확률" => true,
            _ => false
        };

    private static bool ResourceCondition(Fighter fighter, string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var match = System.Text.RegularExpressions.Regex.Match(expression, "^(?<id>[a-z0-9_]+)\\s*(?<op>==|=|>=|<=|>|<)\\s*(?<value>\\d+)$");
        // 시트에서 자원 ID만 적은 경우도 '1 이상 보유'라는 자연스러운 축약형으로 허용한다.
        // 비교식을 쓰면 기존처럼 정확한 수치 조건을 판정한다.
        if (!match.Success) return fighter.Resources.GetValueOrDefault(expression.Trim()) > 0;
        var value = fighter.Resources.GetValueOrDefault(match.Groups["id"].Value);
        var expected = int.Parse(match.Groups["value"].Value);
        return match.Groups["op"].Value switch { "=" or "==" => value == expected, ">" => value > expected, ">=" => value >= expected, "<" => value < expected, "<=" => value <= expected, _ => false };
    }

    private static EffectResolution ExecuteEffects(Fighter actor, Fighter target, BattleSkill skill, double surpriseMultiplier, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        var resolution = new EffectResolution();
        foreach (var effect in skill.Effects)
        {
            if (actor.Hp <= 0 || target.Hp <= 0) break;
            var receiver = effect.Target == "자신" ? actor : target;
            if (!CanApplyEffect(actor, target, effect)) continue;
            switch (effect.Type)
            {
                case "피해":
                    // 다단 효과의 총 기본 위력은 유지하고 타격마다 나눕니다.
                    // 각 타격은 별도로 치명타·추가타를 판정하므로 연출과 변동성은 남습니다.
                    var totalBaseDamage = effect.FixedValue > 0 ? effect.FixedValue * effect.Count * rules.FixedDamageScale : actor.Attack * SkillMultiplier(skill, rules) * surpriseMultiplier;
                    var hitBaseDamage = totalBaseDamage / effect.Count;
                    for (var hit = 0; hit < effect.Count && target.Hp > 0; hit++)
                    {
                        if (random.NextDouble() >= effect.Chance) continue;
                        resolution.TargetDamaged |= Attack(actor, receiver, hitBaseDamage, 1, random, rules, events, skill);
                    }
                    break;
                case "지속피해" when effect.StatusId is { } periodicStatusId && effect.Duration > 0:
                    receiver.ApplyStatus(periodicStatusId, effect.Duration, skill.Id, events);
                    // 횟수는 지속 시간 전체에 걸쳐 들어갈 총 타격 수다. 턴마다 한 번씩 나누어 적용한다.
                    receiver.ApplyPeriodicEffect(periodicStatusId, actor.Name,
                        actor.Attack * SkillMultiplier(skill, rules) * surpriseMultiplier / Math.Max(1, effect.Count),
                        effect.Message ?? periodicStatusId);
                    break;
                case "추가피해":
                    if (random.NextDouble() < effect.Chance)
                    {
                        var damage = ResolveFixedAmount(actor, effect);
                        ApplyAdditionalDamage(actor, receiver, damage, events);
                        resolution.TargetDamaged = true;
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
                case "자원설정" when effect.StatusId is { } resourceId:
                    SetResource(receiver, resourceId, ResolveFixedAmount(receiver, effect), events);
                    break;
                case "자원소모" when effect.StatusId is { } resourceId:
                    SetResource(receiver, resourceId, effect.NumericReferenceMode == "전부" ? 0 : receiver.Resources.GetValueOrDefault(resourceId) - ResolveFixedAmount(receiver, effect), events);
                    break;
                case "자원증가" when effect.StatusId is { } resourceId:
                    AddResource(receiver, resourceId, ResolveFixedAmount(receiver, effect), events);
                    break;
                case "브레이크피해":
                    if (random.NextDouble() < effect.Chance)
                        ApplyBreakDamage(actor, receiver, Math.Max(1, ResolveFixedAmount(actor, effect)), rules, events);
                    break;
                case "쿨다운감소":
                    receiver.ReduceCooldowns(Math.Max(1, ResolveFixedAmount(receiver, effect)), events);
                    break;
                default:
                    if (effect.StatusId is { } statusId && effect.Duration > 0)
                        receiver.ApplyStatus(statusId, effect.Duration, skill.Id, events);
                    break;
            }
        }
        return resolution;
    }

    private static bool CanApplyEffect(Fighter actor, Fighter target, BattleEffect effect)
    {
        // 1:1 자동전투에는 '주변 적'이 존재하지 않는다.
        if (effect.Target == "주변적") return false;
        if (effect.ConditionType is null or "") return true;
        var conditionOwner = effect.ConditionTarget == "상대" ? target : actor;
        return effect.ConditionType switch
        {
            "자원보유" when effect.ConditionId is { } id => ResourceCondition(conditionOwner, id + (effect.ConditionOperator ?? "=") + (effect.ConditionValue ?? "0")),
            // 악상처럼 같은 분류에서 하나만 유지하는 자원은, 이미 다른 값이 있으면 새 값을 만들지 않는다.
            "분류자원미보유" when effect.ConditionId is { } kind => !conditionOwner.ResourceDefinitions.Values.Any(x => x.Kind == kind && conditionOwner.Resources.GetValueOrDefault(x.Id) > 0),
            // 배틀스킬파생의 상태효과보유 판정과 동일한 의미다. 조건대상=상대로 상대의 상태를 검사할 수 있다.
            "상태효과보유" when effect.ConditionId is { } statusId => conditionOwner.Statuses.ContainsKey(statusId),
            "상태효과미보유" when effect.ConditionId is { } statusId => !conditionOwner.Statuses.ContainsKey(statusId),
            _ => false
        };
    }

    private static int ResolveFixedAmount(Fighter fighter, BattleEffect effect)
    {
        if (effect.NumericReferenceId is not { } referenceId || !fighter.Resources.TryGetValue(referenceId, out var current)) return effect.FixedValue;
        return effect.NumericReferenceMode switch
        {
            "소모중첩배율" => checked(effect.FixedValue * current),
            "현재값" => current,
            "최대값" when fighter.ResourceDefinitions.TryGetValue(referenceId, out var resource) => resource.Maximum,
            _ => effect.FixedValue
        };
    }

    private static void ApplyAdditionalDamage(Fighter actor, Fighter target, int amount, List<BattleEvent> events)
    {
        var damage = Math.Max(1, amount);
        target.Hp = Math.Max(0, target.Hp - damage);
        events.Add(new("AdditionalDamage", actor.Name, target.Name, damage));
        if (target.Hp == 0) events.Add(new("CharacterDefeated", actor.Name, target.Name));
    }

    private static void ApplyBreakDamage(Fighter actor, Fighter target, int amount, Rules rules, List<BattleEvent> events)
    {
        if (target.HasStatusEffect("브레이크면역"))
        {
            events.Add(new("BreakImmune", actor.Name, target.Name));
            return;
        }
        target.BreakGauge = Math.Min(rules.BreakGaugeMaximum, target.BreakGauge + amount);
        events.Add(new("BreakGaugeChanged", actor.Name, target.Name, target.BreakGauge, rules.BreakGaugeMaximum.ToString()));
        if (target.BreakGauge < rules.BreakGaugeMaximum) return;
        target.BreakGauge = 0;
        target.ApplyStatus("break_broken", rules.BreakDuration, "break", events);
        events.Add(new("BreakActivated", actor.Name, target.Name));
    }

    private static void SetResource(Fighter fighter, string id, int value, List<BattleEvent> events)
    {
        if (!fighter.ResourceDefinitions.TryGetValue(id, out var definition)) return;
        var capped = definition.Maximum == 0 ? Math.Max(0, value) : Math.Clamp(value, 0, definition.Maximum);
        if (capped > 0 && definition.Stacking == "상호배타")
            foreach (var peer in fighter.ResourceDefinitions.Values.Where(x => x.Id != id && x.Kind == definition.Kind && x.Stacking == "상호배타"))
                if (fighter.Resources.GetValueOrDefault(peer.Id) > 0) ChangeResource(fighter, peer, 0, events);
        ChangeResource(fighter, definition, capped, events);
    }

    private static void AddResource(Fighter fighter, string id, int amount, List<BattleEvent> events)
    {
        if (!fighter.ResourceDefinitions.TryGetValue(id, out var definition)) return;
        SetResource(fighter, id, fighter.Resources.GetValueOrDefault(id) + amount, events);
    }

    private static void ChangeResource(Fighter fighter, BattleResource definition, int value, List<BattleEvent> events)
    {
        var previous = fighter.Resources.GetValueOrDefault(definition.Id);
        if (previous == value) return;
        fighter.Resources[definition.Id] = value;
        if (definition.Duration > 0) fighter.ResourceTurns[definition.Id] = definition.Duration;
        events.Add(new("ResourceChanged", fighter.Name, Detail: definition.Name + " " + (value > previous ? "+" : "") + (value - previous) + " (현재 " + value + ")"));
    }

    private static bool CanPaySkillResource(Fighter actor, BattleSkill skill, BattleDataSnapshot data)
        => ResolveSkillResource(skill, data) is not { } resource || !int.TryParse(skill.ResourceCost, out var cost) || actor.Resources.GetValueOrDefault(resource.Id) >= cost;

    private static void SpendSkillResource(Fighter actor, BattleSkill skill, BattleDataSnapshot data, List<BattleEvent> events)
    {
        if (ResolveSkillResource(skill, data) is not { } resource || skill.ResourceCost == "전부" || !int.TryParse(skill.ResourceCost, out var cost) || cost == 0) return;
        SetResource(actor, resource.Id, actor.Resources.GetValueOrDefault(resource.Id) - cost, events);
    }

    private static void SpendDeferredSkillResource(Fighter actor, BattleSkill skill, BattleDataSnapshot data, List<BattleEvent> events)
    {
        if (skill.ResourceCost != "전부" || ResolveSkillResource(skill, data) is not { } resource) return;
        SetResource(actor, resource.Id, 0, events);
    }

    private static void GainSkillResource(Fighter actor, BattleSkill skill, BattleDataSnapshot data, List<BattleEvent> events)
    {
        if (ResolveSkillResource(skill, data) is not { } resource || !int.TryParse(skill.ResourceGain, out var gain) || gain == 0) return;
        AddResource(actor, resource.Id, gain, events);
    }

    private static BattleResource? ResolveSkillResource(BattleSkill skill, BattleDataSnapshot data)
    {
        if (skill.ResourceId is not { } id) return null;
        if (data.Resources.TryGetValue(id, out var resource)) return resource;
        var matches = data.Resources.Values.Where(x => x.Kind == id).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static bool Attack(Fighter actor, Fighter target, double baseDamage, int count, IBattleRandom random, Rules rules, List<BattleEvent> events, BattleSkill? sourceSkill = null)
    {
        // 대상 해제는 다단의 매 타격이 아니라 다음 공격 효과 전체를 한 번 회피하는 판정이다.
        if (target.HasStatusEffect("대상해제") && random.NextDouble() < rules.TargetReleaseEvasionChance)
        {
            events.Add(new("AttackEvaded", actor.Name, target.Name));
            return false;
        }
        var damaged = false;
        for (var i = 0; i < count && target.Hp > 0; i++)
        {
            var outgoing = 1d + actor.StatusValue("주는피해증가") + actor.MelodySkillDamageBonus(sourceSkill);
            var incoming = Math.Max(.1d, 1d + target.StatusValue("받는피해증가") - target.StatusValue("받는피해감소"));
            var amount = Math.Max(1, baseDamage - target.Defense * rules.DefenseCoefficient) * outgoing * incoming * (rules.DamageVarianceMin + random.NextDouble() * (rules.DamageVarianceMax - rules.DamageVarianceMin));
            var criticalChance = Math.Clamp(rules.CriticalChance + actor.StatusValue("치명타확률증가") + target.StatusValue("받는치명타확률증가"), 0d, 1d);
            var critical = random.NextDouble() < criticalChance;
            if (critical) { amount *= rules.CriticalMultiplier; events.Add(new("CriticalHit", actor.Name, target.Name)); }
            var damage = Math.Max(1, (int)Math.Round(amount)); target.Hp = Math.Max(0, target.Hp - damage);
            damaged = true;
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
        return damaged;
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
        public required string Name; public required int MaxHp; public required int Hp; public required double Attack; public required double Defense; public required int LifePower; public required int CharmPower; public required IReadOnlyList<string> SkillIds; public required IReadOnlyDictionary<string, BattleResource> ResourceDefinitions; public required IReadOnlyDictionary<string, BattleStatus> StatusDefinitions;
        public int SurpriseCount; public string? LastSkillId;
        public int BreakGauge;
        public string? PendingSkillId;
        public Dictionary<string, int> Cooldowns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Resources { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ResourceTurns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Statuses { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> StatusSources { get; } = new(StringComparer.Ordinal);
        private Dictionary<string, PeriodicEffect> PeriodicEffects { get; } = new(StringComparer.Ordinal);
        public static Fighter Create(CharacterBattleSnapshot source, double scale, Rules rules, BattleDataSnapshot data)
        {
            var c = data.Classes[source.ClassId]; var max = Math.Max(1, (int)Math.Round(rules.BaseHp * scale));
            var fighter = new Fighter { Name = source.CharacterName, MaxHp = max, Hp = max, Attack = rules.BaseAttack * scale, Defense = rules.BaseDefense * scale, LifePower = source.LifePower, CharmPower = source.CharmPower, SkillIds = c.SkillIds, ResourceDefinitions = data.Resources, StatusDefinitions = data.Statuses };
            foreach (var id in c.SkillIds) if (data.Skills.TryGetValue(id, out var skill)) fighter.Cooldowns[id] = skill.InitialCooldown;
            foreach (var resource in data.Resources.Values) fighter.Resources[resource.Id] = resource.InitialValue;
            return fighter;
        }
        public void TickCooldowns()
        {
            // 대상스킬ID가 없는 쿨다운감소는 기존처럼 모든 스킬에 적용하고, 대상스킬ID가 있으면 그 스킬에만 더 적용한다.
            var genericReduction = Statuses.Keys.Sum(id => StatusDefinitions.TryGetValue(id, out var status) && status.HasEffectType("쿨다운감소") && status.TargetSkillId is null ? status.Value : 0d);
            var scopedReduction = Statuses.Keys
                .Select(id => StatusDefinitions.GetValueOrDefault(id))
                .Where(status => status is not null && status.HasEffectType("쿨다운감소") && status.TargetSkillId is not null)
                .GroupBy(status => status!.TargetSkillId!)
                .ToDictionary(group => group.Key, group => group.Sum(status => status!.Value));
            foreach (var id in Cooldowns.Keys.ToArray())
            {
                var reduction = Math.Max(1, 1 + (int)Math.Round(genericReduction + scopedReduction.GetValueOrDefault(id)));
                Cooldowns[id] = Math.Max(0, Cooldowns[id] - reduction);
            }
        }

        public double StatusValue(string effectType) => Statuses.Keys.Sum(id => StatusDefinitions.TryGetValue(id, out var status) && status.HasEffectType(effectType) ? status.Value : 0d);
        public bool HasStatusEffect(string effectType) => Statuses.Keys.Any(id => StatusDefinitions.TryGetValue(id, out var status) && status.HasEffectType(effectType));
        public double MelodySkillDamageBonus(BattleSkill? skill)
        {
            if (skill is null || !HasStatusEffect("악상피해증가")) return 0d;
            var hasMelody = Resources.Any(x => x.Value > 0 && ResourceDefinitions.TryGetValue(x.Key, out var resource) && resource.Kind == "악상");
            var melodySkill = skill.ParentSkillId == "bards_tale" || skill.Effects.Any(x => x.ConditionType == "자원보유" && x.ConditionId?.StartsWith("bard_", StringComparison.Ordinal) == true);
            return hasMelody && melodySkill ? StatusValue("악상피해증가") : 0d;
        }
        public void ReduceCooldowns(int amount, List<BattleEvent> events)
        {
            var reduced = 0;
            foreach (var id in Cooldowns.Keys.ToArray())
            {
                var before = Cooldowns[id];
                Cooldowns[id] = Math.Max(0, before - amount);
                reduced += before - Cooldowns[id];
            }
            if (reduced > 0) events.Add(new("CooldownReduced", Name, Amount: reduced, Detail: amount.ToString()));
        }
        public void TickResources(List<BattleEvent> events)
        {
            foreach (var id in ResourceTurns.Keys.ToArray())
            {
                ResourceTurns[id]--;
                if (ResourceTurns[id] > 0) continue;
                ResourceTurns.Remove(id);
                if (Resources.GetValueOrDefault(id) > 0 && ResourceDefinitions.TryGetValue(id, out var resource))
                {
                    Resources[id] = 0;
                    events.Add(new("ResourceChanged", Name, Detail: resource.Name + "이(가) 사라졌습니다."));
                }
            }
        }
        public void ApplyStatus(string id, int duration, string sourceSkillId, List<BattleEvent> events)
        {
            var previous = Statuses.GetValueOrDefault(id);
            var applied = Math.Max(previous, duration);
            if (applied == previous) return;
            Statuses[id] = applied;
            StatusSources[id] = sourceSkillId;
            var name = StatusDefinitions.GetValueOrDefault(id)?.Name ?? id;
            events.Add(new("StatusApplied", Name, Amount: applied, Detail: name));
        }
        public void ApplyPeriodicEffect(string statusId, string sourceName, double baseDamage, string message)
            => PeriodicEffects.TryAdd(statusId, new PeriodicEffect(sourceName, baseDamage, message));
        public bool TickPeriodicEffects(Rules rules, List<BattleEvent> events)
        {
            var damaged = false;
            foreach (var (statusId, periodic) in PeriodicEffects.ToArray())
            {
                if (!Statuses.ContainsKey(statusId)) { PeriodicEffects.Remove(statusId); continue; }
                var incoming = Math.Max(.1d, 1d + StatusValue("받는피해증가") - StatusValue("받는피해감소"));
                var amount = Math.Max(1, (int)Math.Round(Math.Max(1, periodic.BaseDamage - Defense * rules.DefenseCoefficient) * incoming));
                Hp = Math.Max(0, Hp - amount);
                damaged = true;
                var name = StatusDefinitions.GetValueOrDefault(statusId)?.Name ?? periodic.Message;
                events.Add(new("StatusDamage", periodic.SourceName, Name, amount, name));
                if (Hp == 0) events.Add(new("CharacterDefeated", periodic.SourceName, Name));
            }
            return damaged;
        }
        public IReadOnlyList<(string Id, string SourceSkillId)> TickStatuses(List<BattleEvent> events)
        {
            var expired = new List<(string Id, string SourceSkillId)>();
            foreach (var id in Statuses.Keys.ToArray())
            {
                Statuses[id]--;
                if (Statuses[id] > 0) continue;
                Statuses.Remove(id);
                PeriodicEffects.Remove(id);
                var source = StatusSources.GetValueOrDefault(id) ?? string.Empty;
                StatusSources.Remove(id);
                var name = StatusDefinitions.GetValueOrDefault(id)?.Name ?? id;
                events.Add(new("StatusExpired", Name, Detail: name));
                if (!string.IsNullOrEmpty(source)) expired.Add((id, source));
            }
            return expired;
        }
        public BattleSkill? TakePendingSkill(BattleDataSnapshot data)
        {
            if (PendingSkillId is not { } id) return null;
            PendingSkillId = null;
            return data.Skills.GetValueOrDefault(id);
        }
    }

    private sealed record PeriodicEffect(string SourceName, double BaseDamage, string Message);

    private sealed class Rules(IReadOnlyDictionary<string, BattleRule> values)
    {
        private double Number(string id) => values.TryGetValue(id, out var rule) && double.TryParse(rule.Value, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidDataException($"배틀규칙 시트의 필수 규칙 '{id}'이(가) 없습니다.");
        public double BaseHp => Number("base_max_hp"); public double BaseAttack => Number("base_attack"); public double BaseDefense => Number("base_defense");
        public double PowerExponent => Number("power_scale_exponent"); public double PowerMin => Number("power_scale_min"); public double PowerMax => Number("power_scale_max");
        public double DefenseCoefficient => Number("defense_coefficient"); public double DamageVarianceMin => Number("damage_variance_min"); public double DamageVarianceMax => Number("damage_variance_max"); public double FixedDamageScale => Number("fixed_damage_scale");
        public double CriticalChance => Number("base_critical_chance"); public double CriticalMultiplier => Number("critical_damage_multiplier");
        public int MaxActions => checked((int)Number("max_major_actions")); public double DrawThreshold => Number("draw_hp_ratio_threshold"); public double TargetReleaseEvasionChance => Number("target_release_evasion_chance");
        public double NormalAttackMultiplier => Number("normal_attack_multiplier"); public double SkillDamageMinMultiplier => Number("skill_damage_min_multiplier"); public double SkillDamageMaxMultiplier => Number("skill_damage_max_multiplier"); public double UltimateDamageMultiplier => Number("ultimate_damage_multiplier");
        public int MinimumSkillCooldown => checked((int)Number("minimum_skill_cooldown")); public double SkillHealRatio => Number("skill_heal_ratio"); public int MaxSurpriseEvents => checked((int)Number("max_surprise_events_per_actor")); public int SurpriseCooldown => checked((int)Number("surprise_event_global_cooldown"));
        public double LifeSurpriseHpRatioThreshold => Number("life_surprise_hp_ratio_threshold"); public double LifeSurpriseHealRatio => Number("life_surprise_heal_ratio"); public double CharmSurpriseDamageMultiplier => Number("charm_surprise_damage_multiplier");
        public double AdditionalHitChance => Number("additional_hit_chance"); public double AdditionalHitDamageRatio => Number("additional_hit_damage_ratio");
        public int BreakGaugeMaximum => checked((int)Number("break_gauge_maximum")); public int BreakDuration => checked((int)Number("break_duration_turns"));
        public double LifeSurpriseChance(int lifePower) => SurpriseChance("life_surprise", lifePower);
        public double CharmSurpriseChance(int charmPower) => SurpriseChance("charm_surprise", charmPower);
        private double SurpriseChance(string prefix, int value) => Math.Min(Number(prefix + "_max_chance"), Number(prefix + "_base_chance") + Math.Min(2d, Math.Max(0, value) / Number(prefix + "_stat_reference")) * Number(prefix + "_stat_coefficient"));
    }
}
