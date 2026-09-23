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
        // 패시브의 상시 효과(전투시작 트리거)는 두 파이터가 모두 구성된 뒤, 실제 전투 루프가 시작되기 전에 적용한다.
        FirePassiveTrigger(left, right, "전투시작", null, null, random, rules, events);
        FirePassiveTrigger(right, left, "전투시작", null, null, random, rules, events);
        var actor = random.NextDouble() < .5 ? left : right;
        var major = 0;
        var surpriseCooldown = 0;
        while (major < rules.MaxActions && left.Hp > 0 && right.Hp > 0)
        {
            var target = ReferenceEquals(actor, left) ? right : left;
            events.Add(new("TurnStarted", actor.Name, target.Name));
            actor.TickCooldowns();
            // 지속턴은 보유자의 턴이 돌아올 때 차감하지만, 0이 된 상태·자원도 이번 행동까지는 적용하고 행동이 끝난 뒤 제거한다.
            // 따라서 지속턴 N은 "적용된 행동을 제외한 보유자의 다음 N회 행동"이다. 상대에게 건 디버프는 상대의 턴으로 센다.
            actor.TickResources();
            actor.TickStatuses();
            var periodic = actor.TickPeriodicEffects(rules, events);
            if (periodic.Damaged && actor.Hp > 0 || periodic.Healed) events.Add(new("HpStatus", actor.Name, Detail: HpStatus(actor)));
            if (actor.Hp <= 0) break;
            var actionBroken = actor.HasStatusEffect("브레이크");
            if (actionBroken)
            {
                events.Add(new("BreakActionLost", target.Name, actor.Name));
                EndTurn(actor, target, data, random, rules, events);
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
                targetDamaged = Attack(actor, target, actor.Attack * normalAttackMultiplier * surpriseMultiplier, 1, random, rules, events, normalAttack: true);
            }
            else
            {
                var selectedId = skill.Id;
                if (SelectDerivation(actor, skill.Id, "재사용 시", data, null) is { } reuse)
                {
                    skill = reuse.Child;
                    // 재사용은 진행 중인 상태를 끝내는 동작이다(죽은 척 중 재사용 → 기상). 조건이 된 상태를 조용히 제거해
                    // 행동이 끝난 뒤 같은 상태의 "상태만료 시" 파생이 한 번 더 발동하지 않게 한다.
                    if (reuse.Rule.ConditionType == "상태효과보유" && reuse.Rule.ConditionValue is { } reusedStatusId) actor.RemoveStatus(reusedStatusId);
                }
                SpendSkillResource(actor, target, skill, data, random, rules, events);
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
                var performedSkillId = skill.Id;
                foreach (var derived in derivedSkills)
                {
                    if (!hideParent) events.Add(new("DerivedSkillUsed", actor.Name, target.Name, Detail: derived.Name));
                    var child = ExecuteEffects(actor, target, derived, surpriseMultiplier, random, rules, events);
                    resolved.TargetDamaged |= child.TargetDamaged;
                    resolved.ActorHealed |= child.ActorHealed;
                    if (actor.Hp > 0) FirePassiveTrigger(actor, target, "스킬사용완료시", derived.Id, null, random, rules, events);
                    performedSkillId = derived.Id;
                }
                GainSkillResource(actor, target, skill, data, random, rules, events);
                SpendDeferredSkillResource(actor, target, skill, data, random, rules, events);
                targetDamaged = resolved.TargetDamaged;
                actorHealed = resolved.ActorHealed;
                if (actor.Hp > 0 && derivedSkills.Length == 0) FirePassiveTrigger(actor, target, "스킬사용완료시", performedSkillId, null, random, rules, events);
            }
            if (targetDamaged && target.Hp > 0) events.Add(new("HpStatus", target.Name, Detail: HpStatus(target)));
            if (actorHealed) events.Add(new("HpStatus", actor.Name, Detail: HpStatus(actor)));
            if (actor.Hp > 0 && target.Hp > 0) EndTurn(actor, target, data, random, rules, events);
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

    /// <summary>행동이 끝난 뒤 이번 턴에 지속턴이 0이 된 상태·자원을 제거한다. 행동 중 다시 부여·갱신된 것은 남는다.</summary>
    private static void EndTurn(Fighter actor, Fighter target, BattleDataSnapshot data, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        foreach (var expired in actor.ExpireStatuses(events))
        {
            var scheduled = SelectDerivation(actor, expired.SourceSkillId, "상태만료 시", data, random, expired.Id);
            if (scheduled is not null) actor.PendingSkillId = scheduled.Value.Child.Id;
        }
        foreach (var resourceId in actor.ExpireResources(events))
            FirePassiveTrigger(actor, target, "자원소진시", null, resourceId, random, rules, events);
    }

    private static double PowerScale(int power, double basePower, Rules rules) => Math.Clamp(Math.Pow(Math.Max(1d, power) / basePower, rules.PowerExponent), rules.PowerMin, rules.PowerMax);
    private static double SkillMultiplier(BattleSkill? skill, Rules rules)
    {
        if (skill is null) return rules.SkillDamageMinMultiplier;
        if (skill.Kind == "궁극기" || skill.Id.Contains("finale", StringComparison.OrdinalIgnoreCase) || skill.Id.Contains("symphony", StringComparison.OrdinalIgnoreCase)) return rules.UltimateDamageMultiplier;
        var variation = (uint)StringComparer.Ordinal.GetHashCode(skill.Id) % 100 / 100d;
        return rules.SkillDamageMinMultiplier + variation * (rules.SkillDamageMaxMultiplier - rules.SkillDamageMinMultiplier);
    }
    // 회복도 피해와 동일하게 공격력 기반 배율(또는 고정값)로 계산한다. 최대 HP 비율 기반 회복은 스킬 위력과 무관해지므로 쓰지 않는다.
    private static double HealMultiplier(BattleSkill? skill, Rules rules)
    {
        if (skill is null) return rules.SkillHealMinMultiplier;
        if (skill.Kind == "궁극기" || skill.Id.Contains("finale", StringComparison.OrdinalIgnoreCase) || skill.Id.Contains("symphony", StringComparison.OrdinalIgnoreCase)) return rules.UltimateHealMultiplier;
        var variation = (uint)StringComparer.Ordinal.GetHashCode(skill.Id) % 100 / 100d;
        return rules.SkillHealMinMultiplier + variation * (rules.SkillHealMaxMultiplier - rules.SkillHealMinMultiplier);
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
        // 조건부피해증가(멜로디 쇼크의 "현기증 보유 시 150%")는 스킬이 효과를 실행하기 전 상태로 판정해 같은 스킬의 모든 피해에 곱한다.
        // 고정값은 증가 퍼센트다(50 = ×1.5).
        var conditionalDamage = skill.Effects.Where(x => x.Type == "조건부피해증가" && CanApplyEffect(actor, target, x)).Aggregate(1d, (product, x) => product * (1d + x.FixedValue / 100d));
        foreach (var effect in skill.Effects)
        {
            if (actor.Hp <= 0 || target.Hp <= 0) break;
            var receiver = effect.Target == "자신" ? actor : target;
            if (!CanApplyEffect(actor, target, effect)) continue;
            ApplyEffect(actor, target, receiver, effect, skill, surpriseMultiplier, random, rules, events, resolution, conditionalDamage);
        }
        return resolution;
    }

    /// <summary>패시브가 <see cref="BattleEffect.Trigger"/> 시점에 발동시키는 효과 하나를 실행한다. 스킬 효과와 동일한 <see cref="ApplyEffect"/> 처리기를 재사용한다.</summary>
    private static void FirePassiveTrigger(Fighter owner, Fighter opponent, string trigger, string? skillId, string? resourceId, IBattleRandom random, Rules rules, List<BattleEvent> events, double? ownerHpRatioBeforeHeal = null)
    {
        foreach (var passive in owner.Passives)
        {
            if (owner.Hp <= 0) return;
            foreach (var effect in passive.Effects)
            {
                if (effect.Trigger != trigger) continue;
                if (effect.TriggerSkillId is { } scopedSkillId && scopedSkillId != skillId) continue;
                if (effect.TriggerResourceId is { } scopedResourceId && !MatchesResource(owner, scopedResourceId, resourceId)) continue;
                if (owner.Hp <= 0 || opponent.Hp <= 0) return;
                var receiver = effect.Target == "자신" ? owner : opponent;
                if (effect.ReactivationCooldown > 0 && owner.PassiveCooldowns.GetValueOrDefault(effect.Id) > 0) continue;
                if (!CanApplyEffect(owner, opponent, effect, ownerHpRatioBeforeHeal)) continue;
                if (ApplyEffect(owner, opponent, receiver, effect, null, 1d, random, rules, events, new EffectResolution()) && effect.ReactivationCooldown > 0)
                    owner.PassiveCooldowns[effect.Id] = effect.ReactivationCooldown;
            }
        }
    }

    private static bool MatchesResource(Fighter owner, string scope, string? resourceId)
        => resourceId is not null && (resourceId == scope || (owner.ResourceDefinitions.TryGetValue(resourceId, out var definition) && definition.Kind == scope));

    /// <summary>효과 한 행을 실행한다. <paramref name="skill"/>이 null이면 패시브 트리거에서 직접 발동한 효과다(고정값 기반 효과만 안전하게 지원).</summary>
    /// <returns>행 단위 발동확률 판정을 통과해 효과를 실행했으면 true.</returns>
    private static bool ApplyEffect(Fighter actor, Fighter target, Fighter receiver, BattleEffect effect, BattleSkill? skill, double surpriseMultiplier, IBattleRandom random, Rules rules, List<BattleEvent> events, EffectResolution resolution, double conditionalDamageMultiplier = 1d)
    {
        // 피해·회복·추가피해·브레이크피해는 아래에서 타격마다 발동확률을 판정한다. 나머지 효과(자원·상태·다음행동지정 등)는
        // 효과 한 행 단위로 한 번 판정한다. 확률이 1이면 난수를 소비하지 않아 기존 고정 난수 시퀀스를 바꾸지 않는다.
        if (effect.Type is not ("피해" or "회복" or "추가피해" or "브레이크피해") && effect.Chance < 1 && random.NextDouble() >= effect.Chance) return false;
        switch (effect.Type)
        {
            case "피해":
                // 다단 효과의 총 기본 위력은 유지하고 타격마다 나눕니다.
                // 각 타격은 별도로 치명타·추가타를 판정하므로 연출과 변동성은 남습니다.
                var totalBaseDamage = effect.FixedValue > 0 ? effect.FixedValue * effect.Count * rules.FixedDamageScale : actor.Attack * SkillMultiplier(skill, rules) * surpriseMultiplier;
                var hitBaseDamage = totalBaseDamage * conditionalDamageMultiplier / effect.Count;
                var multiHitBonus = effect.Count > 1 ? actor.StatusValue("멀티히트피해증가") : 0d;
                for (var hit = 0; hit < effect.Count && target.Hp > 0; hit++)
                {
                    if (random.NextDouble() >= effect.Chance) continue;
                    // 연속치명타배율 <1이면 타격마다(0번째 제외) 누적 제곱으로 치명타 확률이 감소한다(거스팅 볼트: 매 발사 0.75배).
                    var criticalChanceMultiplier = Math.Pow(effect.CriticalChanceMultiplierPerHit, hit);
                    resolution.TargetDamaged |= Attack(actor, receiver, hitBaseDamage, 1, random, rules, events, skill, criticalChanceMultiplier, multiHitBonus);
                }
                break;
            case "지속피해" when effect.StatusId is { } periodicStatusId && effect.Duration > 0:
                receiver.ApplyStatus(periodicStatusId, effect.Duration, skill?.Id ?? "passive", events);
                // 고정값이 있으면 보유자 턴마다 들어가는 1회 피해량(원본 수치)이다. 없으면 공격력 배율 총량을 횟수(총 타격 수)로 나눈다.
                receiver.ApplyPeriodicEffect(periodicStatusId, actor.Name,
                    effect.FixedValue > 0 ? effect.FixedValue * rules.FixedDamageScale : actor.Attack * SkillMultiplier(skill, rules) * surpriseMultiplier / Math.Max(1, effect.Count),
                    effect.Message ?? periodicStatusId);
                break;
            case "지속회복" when effect.StatusId is { } regenStatusId && effect.Duration > 0 && effect.FixedValue > 0:
                // 고양·활력처럼 보유자 턴마다 고정량을 회복한다. 회복량 증가는 부여 시점의 시전자 기준으로 고정한다.
                // 지속회복의 틱은 회복적용시 트리거를 발생시키지 않는다(활력이 자기 회복으로 다시 발동하는 것을 막는다).
                receiver.ApplyStatus(regenStatusId, effect.Duration, skill?.Id ?? "passive", events);
                receiver.ApplyPeriodicEffect(regenStatusId, actor.Name, effect.FixedValue * rules.FixedDamageScale * (1d + actor.StatusValue("회복량증가")), effect.Message ?? regenStatusId, heal: true);
                break;
            case "조건부피해증가":
                // ExecuteEffects가 스킬 실행 전에 판정해 피해 배율로 반영한다. 행 자체는 아무것도 하지 않는다.
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
            {
                // 피해와 동일한 구조: 다단 효과의 총 회복량은 유지하고 타격마다 나눈다.
                var totalBaseHeal = (effect.FixedValue > 0 ? effect.FixedValue * effect.Count * rules.FixedDamageScale : actor.Attack * HealMultiplier(skill, rules)) * (1d + actor.StatusValue("회복량증가"));
                var healPerTick = totalBaseHeal / effect.Count;
                var totalHealed = 0;
                // 활력의 "체력이 40% 미만인 아군을 회복시키면"은 회복 전 체력으로 판정한다.
                var hpRatioBeforeHeal = (double)receiver.Hp / receiver.MaxHp;
                for (var healIndex = 0; healIndex < effect.Count; healIndex++)
                {
                    if (random.NextDouble() >= effect.Chance) continue;
                    var amount = Math.Max(1, (int)Math.Round(healPerTick));
                    var healed = Math.Min(amount, receiver.MaxHp - receiver.Hp);
                    receiver.Hp += healed;
                    totalHealed += healed;
                    if (healed > 0)
                    {
                        events.Add(new("HealApplied", actor.Name, receiver.Name, healed));
                        resolution.ActorHealed |= ReferenceEquals(receiver, actor);
                    }
                }
                if (totalHealed > 0)
                {
                    var otherFighter = ReferenceEquals(receiver, actor) ? target : actor;
                    FirePassiveTrigger(receiver, otherFighter, "회복적용시", null, null, random, rules, events, hpRatioBeforeHeal);
                }
                break;
            }
            case "자원설정" when effect.StatusId is { } resourceId:
            {
                var other = ReferenceEquals(receiver, actor) ? target : actor;
                SetResource(receiver, other, resourceId, ResolveFixedAmount(receiver, effect), random, rules, events);
                break;
            }
            case "자원소모" when effect.StatusId is { } resourceId:
            {
                var other = ReferenceEquals(receiver, actor) ? target : actor;
                SetResource(receiver, other, resourceId, effect.NumericReferenceMode == "전부" ? 0 : receiver.Resources.GetValueOrDefault(resourceId) - ResolveFixedAmount(receiver, effect), random, rules, events);
                break;
            }
            case "자원증가" when effect.StatusId is { } resourceId:
            {
                var other = ReferenceEquals(receiver, actor) ? target : actor;
                AddResource(receiver, other, resourceId, ResolveFixedAmount(receiver, effect), random, rules, events);
                break;
            }
            case "브레이크피해":
                if (random.NextDouble() < effect.Chance)
                    ApplyBreakDamage(actor, receiver, Math.Max(1, ResolveFixedAmount(actor, effect)), random, rules, events);
                break;
            case "쿨다운감소":
                receiver.ReduceCooldowns(Math.Max(1, ResolveFixedAmount(receiver, effect)), events);
                break;
            case "다음행동지정" when effect.StatusId is { } scheduledSkillId:
                // 패시브가 다음 주요 행동을 특정 스킬로 강제 지정한다(즉흥 연주처럼 "즉시 1회 연주"를 단순화한 표현).
                // 원칙상 파생·돌발 이벤트는 행동을 소비하지 않아야 하지만, 엔진에 별도의 무행동 삽입 경로가 없어
                // 다음 행동 하나를 대체하는 것으로 단순화했다(비고에 근거를 남긴다).
                receiver.PendingSkillId = scheduledSkillId;
                break;
            default:
                if (effect.StatusId is { } statusId && (effect.Duration > 0 || effect.Trigger == "전투시작"))
                {
                    if (effect.Trigger == "전투시작") receiver.ApplyPermanentStatus(statusId, events);
                    else receiver.ApplyStatus(statusId, effect.Duration, skill?.Id ?? "passive", events);
                }
                break;
        }
        return true;
    }

    private static bool CanApplyEffect(Fighter actor, Fighter target, BattleEffect effect, double? actorHpRatioOverride = null)
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
            // 패시브의 "회복적용시"처럼 HP 비율을 조건으로 거는 효과(예: 활력의 40% 미만)에 사용한다.
            "HP비율" => CompareRatio(ReferenceEquals(conditionOwner, actor) && actorHpRatioOverride is { } ratio ? ratio : (double)conditionOwner.Hp / conditionOwner.MaxHp, effect.ConditionOperator, effect.ConditionValue),
            _ => false
        };
    }

    private static bool CompareRatio(double actual, string? op, string? value)
    {
        if (value is null || !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var expected)) return false;
        return op switch { "=" or "==" => actual == expected, ">" => actual > expected, ">=" => actual >= expected, "<" => actual < expected, "<=" => actual <= expected, _ => false };
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

    private static void ApplyBreakDamage(Fighter actor, Fighter target, int amount, IBattleRandom random, Rules rules, List<BattleEvent> events)
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
        // 자신 또는 상대가 브레이크되었을 때 반응하는 패시브를 양쪽 관점에서 모두 검사한다.
        FirePassiveTrigger(target, actor, "브레이크발생시", null, null, random, rules, events);
        if (actor.Hp > 0) FirePassiveTrigger(actor, target, "브레이크발생시", null, null, random, rules, events);
    }

    private static void SetResource(Fighter fighter, Fighter opponent, string id, int value, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        if (!fighter.ResourceDefinitions.TryGetValue(id, out var definition)) return;
        var capped = definition.Maximum == 0 ? Math.Max(0, value) : Math.Clamp(value, 0, definition.Maximum);
        if (capped > 0 && definition.Stacking == "상호배타")
            foreach (var peer in fighter.ResourceDefinitions.Values.Where(x => x.Id != id && x.Kind == definition.Kind && x.Stacking == "상호배타"))
                if (fighter.Resources.GetValueOrDefault(peer.Id) > 0) ChangeResource(fighter, opponent, peer, 0, random, rules, events);
        ChangeResource(fighter, opponent, definition, capped, random, rules, events);
    }

    private static void AddResource(Fighter fighter, Fighter opponent, string id, int amount, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        if (!fighter.ResourceDefinitions.TryGetValue(id, out var definition)) return;
        SetResource(fighter, opponent, id, fighter.Resources.GetValueOrDefault(id) + amount, random, rules, events);
    }

    private static void ChangeResource(Fighter fighter, Fighter opponent, BattleResource definition, int value, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        var previous = fighter.Resources.GetValueOrDefault(definition.Id);
        if (previous == value) return;
        fighter.Resources[definition.Id] = value;
        if (definition.Duration > 0) fighter.ResourceTurns[definition.Id] = definition.Duration;
        events.Add(new("ResourceChanged", fighter.Name, Detail: definition.Name + " " + (value > previous ? "+" : "") + (value - previous) + " (현재 " + value + ")"));
        // 자원 변화 자체를 구독하는 패시브(악상 획득 시, 리듬이 임계값에 도달했을 때, 템포가 소진됐을 때 등)를 발동한다.
        // 상호배타·1개 상한 자원(악상 등)은 이미 보유 중이면 재설정이 무시되므로(위 previous==value 조기 반환) 매 증가마다 발동해도 실질적으로는 최초 획득 때만 발동한다.
        if (value > previous && value > 0) FirePassiveTrigger(fighter, opponent, "자원획득시", null, definition.Id, random, rules, events);
        if (definition.Maximum > 0 && value >= definition.Maximum && previous < definition.Maximum) FirePassiveTrigger(fighter, opponent, "자원최대치도달시", null, definition.Id, random, rules, events);
        if (previous > 0 && value <= 0) FirePassiveTrigger(fighter, opponent, "자원소진시", null, definition.Id, random, rules, events);
    }

    private static bool CanPaySkillResource(Fighter actor, BattleSkill skill, BattleDataSnapshot data)
        => ResolveSkillResource(skill, data) is not { } resource || !int.TryParse(skill.ResourceCost, out var cost) || actor.Resources.GetValueOrDefault(resource.Id) >= cost;

    private static void SpendSkillResource(Fighter actor, Fighter opponent, BattleSkill skill, BattleDataSnapshot data, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        if (ResolveSkillResource(skill, data) is not { } resource || skill.ResourceCost == "전부" || !int.TryParse(skill.ResourceCost, out var cost) || cost == 0) return;
        SetResource(actor, opponent, resource.Id, actor.Resources.GetValueOrDefault(resource.Id) - cost, random, rules, events);
    }

    private static void SpendDeferredSkillResource(Fighter actor, Fighter opponent, BattleSkill skill, BattleDataSnapshot data, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        if (skill.ResourceCost != "전부" || ResolveSkillResource(skill, data) is not { } resource) return;
        SetResource(actor, opponent, resource.Id, 0, random, rules, events);
    }

    private static void GainSkillResource(Fighter actor, Fighter opponent, BattleSkill skill, BattleDataSnapshot data, IBattleRandom random, Rules rules, List<BattleEvent> events)
    {
        if (ResolveSkillResource(skill, data) is not { } resource || !int.TryParse(skill.ResourceGain, out var gain) || gain == 0) return;
        AddResource(actor, opponent, resource.Id, gain, random, rules, events);
    }

    private static BattleResource? ResolveSkillResource(BattleSkill skill, BattleDataSnapshot data)
    {
        if (skill.ResourceId is not { } id) return null;
        if (data.Resources.TryGetValue(id, out var resource)) return resource;
        var matches = data.Resources.Values.Where(x => x.Kind == id).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static bool Attack(Fighter actor, Fighter target, double baseDamage, int count, IBattleRandom random, Rules rules, List<BattleEvent> events, BattleSkill? sourceSkill = null, double criticalChanceMultiplier = 1d, double extraDamageMultiplier = 0d, bool normalAttack = false)
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
            var outgoing = 1d + actor.StatusValue("주는피해증가") + actor.MelodySkillDamageBonus(sourceSkill) + extraDamageMultiplier;
            var incoming = Math.Max(.1d, 1d + target.StatusValue("받는피해증가") - target.StatusValue("받는피해감소") - (normalAttack ? target.StatusValue("받는기본공격피해감소") : 0d));
            // 약점 노출의 "방어도 무시 50%"는 이 타격에 반영되는 상대 방어력만 줄인다.
            var defenseIgnore = Math.Clamp(target.StatusValue("받는방어무시"), 0d, 1d);
            var amount = Math.Max(1, baseDamage - target.Defense * (1d - defenseIgnore) * rules.DefenseCoefficient) * outgoing * incoming * (rules.DamageVarianceMin + random.NextDouble() * (rules.DamageVarianceMax - rules.DamageVarianceMin));
            var criticalChance = Math.Clamp((rules.CriticalChance + actor.StatusValue("치명타확률증가") + target.StatusValue("받는치명타확률증가") - target.StatusValue("받는치명타확률감소")) * criticalChanceMultiplier, 0d, 1d);
            var critical = random.NextDouble() < criticalChance;
            // 약점 노출은 "첫 스킬 공격"에만 적용된다. 다단 스킬도 첫 타격이 판정된 순간 소모되어 나머지 타격에는 적용되지 않는다.
            // 치명타 반응 패시브(활력)가 이 타격으로 새 약점 노출을 거는 경우를 살리기 위해 트리거보다 먼저 소모한다.
            if (sourceSkill is not null && !normalAttack) target.ConsumeStatuses("첫스킬타격소모", events);
            if (critical)
            {
                amount *= rules.CriticalMultiplier + actor.StatusValue("치명타피해증가");
                events.Add(new("CriticalHit", actor.Name, target.Name));
                // 일반 공격·패시브 피해도 치명타 판정 대상이다. 특정 스킬로 한정한 패시브는 대상스킬ID가 맞지 않아 반응하지 않는다.
                FirePassiveTrigger(actor, target, "치명타적중시", sourceSkill?.Id, null, random, rules, events);
            }
            else FirePassiveTrigger(actor, target, "치명타미적중시", sourceSkill?.Id, null, random, rules, events);
            var damage = Math.Max(1, (int)Math.Round(amount)); target.Hp = Math.Max(0, target.Hp - damage);
            damaged = true;
            events.Add(new("DamageDealt", actor.Name, target.Name, damage));
            if (target.Hp == 0) events.Add(new("CharacterDefeated", actor.Name, target.Name));
            if (target.Hp > 0 && random.NextDouble() < rules.AdditionalHitChance + actor.StatusValue("추가타확률증가"))
            {
                // 추가타는 이미 확정된 피해의 일부만 더하고, 치명타 판정을 따로 하지 않습니다.
                var additionalDamage = Math.Max(1, (int)Math.Round(damage * rules.AdditionalHitDamageRatio));
                target.Hp = Math.Max(0, target.Hp - additionalDamage);
                events.Add(new("AdditionalHit", actor.Name, target.Name, additionalDamage));
                if (target.Hp == 0) events.Add(new("CharacterDefeated", actor.Name, target.Name));
            }
        }
        // 선수필승처럼 "상대에게 먼저 공격받았는지"에 반응하는 패시브를 피격자 관점에서 발동한다.
        if (damaged && target.Hp > 0) FirePassiveTrigger(target, actor, "피격시", sourceSkill?.Id, null, random, rules, events);
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
        public IReadOnlyList<BattlePassive> Passives = Array.Empty<BattlePassive>();
        public Dictionary<string, int> Cooldowns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Resources { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ResourceTurns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Statuses { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> StatusSources { get; } = new(StringComparer.Ordinal);
        // 재발동대기턴이 있는 패시브 효과(활력)의 남은 대기턴. 보유자의 턴이 돌아올 때 1씩 줄어든다.
        public Dictionary<string, int> PassiveCooldowns { get; } = new(StringComparer.Ordinal);
        // 패시브의 전투시작 트리거로 부여되는 상시 효과. 매 행동 턴 감소 대상인 Statuses와 달리 전투가 끝날 때까지 유지된다.
        public HashSet<string> PermanentStatuses { get; } = new(StringComparer.Ordinal);
        private Dictionary<string, PeriodicEffect> PeriodicEffects { get; } = new(StringComparer.Ordinal);
        public static Fighter Create(CharacterBattleSnapshot source, double scale, Rules rules, BattleDataSnapshot data)
        {
            var c = data.Classes[source.ClassId]; var max = Math.Max(1, (int)Math.Round(rules.BaseHp * scale));
            var fighter = new Fighter { Name = source.CharacterName, MaxHp = max, Hp = max, Attack = rules.BaseAttack * scale, Defense = rules.BaseDefense * scale, LifePower = source.LifePower, CharmPower = source.CharmPower, SkillIds = c.SkillIds, ResourceDefinitions = data.Resources, StatusDefinitions = data.Statuses };
            foreach (var id in c.SkillIds) if (data.Skills.TryGetValue(id, out var skill)) fighter.Cooldowns[id] = skill.InitialCooldown;
            foreach (var resource in data.Resources.Values) fighter.Resources[resource.Id] = resource.InitialValue;
            // 아직 배틀패시브가 구현되지 않은 ID는 스킬의 활성화=FALSE와 동일하게 조용히 무시한다.
            fighter.Passives = c.PassiveIds.Select(id => data.Passives.GetValueOrDefault(id)).Where(x => x is { Enabled: true }).Cast<BattlePassive>().ToArray();
            return fighter;
        }
        public void TickCooldowns()
        {
            // 대상스킬ID가 없는 쿨다운감소·쿨다운증가는 기존처럼 모든 스킬에 적용하고, 대상스킬ID가 있으면 그 스킬에만 더 적용한다.
            // 쿨다운증가(상대의 이동·행동 속도 저하 단순화)는 기본 감소분을 상쇄해 그 턴의 쿨다운 감소를 늦추거나 없앨 뿐, 남은 쿨다운을 늘리지는 않는다.
            // 패시브가 전투시작에 부여한 영구 상태(예: 퀵 어택)도 쿨다운 감소에 포함한다.
            foreach (var id in PassiveCooldowns.Keys.ToArray()) PassiveCooldowns[id] = Math.Max(0, PassiveCooldowns[id] - 1);
            var active = ActiveStatusIds().Select(id => StatusDefinitions.GetValueOrDefault(id)).Where(status => status is not null).Cast<BattleStatus>().ToArray();
            var genericReduction = active.Where(status => status.HasEffectType("쿨다운감소") && status.TargetSkillId is null).Sum(status => ScaledValue(status, "쿨다운감소"));
            var genericSlow = active.Where(status => status.HasEffectType("쿨다운증가") && status.TargetSkillId is null).Sum(status => ScaledValue(status, "쿨다운증가"));
            var scopedReduction = active
                .Where(status => status.HasEffectType("쿨다운감소") && status.TargetSkillId is not null)
                .GroupBy(status => status.TargetSkillId!)
                .ToDictionary(group => group.Key, group => group.Sum(status => ScaledValue(status, "쿨다운감소")));
            var scopedSlow = active
                .Where(status => status.HasEffectType("쿨다운증가") && status.TargetSkillId is not null)
                .GroupBy(status => status.TargetSkillId!)
                .ToDictionary(group => group.Key, group => group.Sum(status => ScaledValue(status, "쿨다운증가")));
            foreach (var id in Cooldowns.Keys.ToArray())
            {
                var reduction = Math.Max(0, 1 + (int)Math.Round(genericReduction + scopedReduction.GetValueOrDefault(id) - genericSlow - scopedSlow.GetValueOrDefault(id)));
                Cooldowns[id] = Math.Max(0, Cooldowns[id] - reduction);
            }
        }

        private IEnumerable<string> ActiveStatusIds() => Statuses.Keys.Concat(PermanentStatuses).Distinct(StringComparer.Ordinal);
        // 중첩자원ID가 있는 상태는 그 자원의 현재 보유량(중첩 수)만큼 값을 곱한다. 자원이 0이면 효과도 0이다.
        private double ScaledValue(BattleStatus status, string effectType) => status.StackResourceId is { } stackResourceId ? status.ValueOf(effectType) * Resources.GetValueOrDefault(stackResourceId) : status.ValueOf(effectType);
        public double StatusValue(string effectType)
        {
            // 일반 옵션은 모두 합산하고, [시너지] 옵션은 같은 효과유형끼리 가장 높은 값 하나만 더한다.
            var matching = ActiveStatusIds().Select(id => StatusDefinitions.GetValueOrDefault(id)).Where(status => status is not null && status.HasEffectType(effectType)).Cast<BattleStatus>().ToArray();
            var synergy = matching.Where(status => status.IsSynergy(effectType)).Select(status => ScaledValue(status, effectType)).DefaultIfEmpty(0d).Max();
            return matching.Where(status => !status.IsSynergy(effectType)).Sum(status => ScaledValue(status, effectType)) + synergy;
        }
        public bool HasStatusEffect(string effectType) => Statuses.Keys.Concat(PermanentStatuses).Any(id => StatusDefinitions.TryGetValue(id, out var status) && status.HasEffectType(effectType));
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
        /// <summary>턴 시작에 자원 지속턴을 1 줄인다. 0이 된 자원은 이번 행동까지 유지하고 <see cref="ExpireResources"/>에서 제거한다.</summary>
        public void TickResources()
        {
            foreach (var id in ResourceTurns.Keys.ToArray()) ResourceTurns[id] = Math.Max(0, ResourceTurns[id] - 1);
        }
        /// <returns>지속턴 만료로 사라진 자원 ID. 호출자가 자원소진시 패시브를 발동한다(예: 템포가 시간 초과로 사라질 때).</returns>
        public IReadOnlyList<string> ExpireResources(List<BattleEvent> events)
        {
            var expired = new List<string>();
            foreach (var id in ResourceTurns.Keys.ToArray())
            {
                // 행동 중 다시 얻어 지속턴이 새로 채워진 자원은 남는다.
                if (ResourceTurns[id] > 0) continue;
                ResourceTurns.Remove(id);
                if (Resources.GetValueOrDefault(id) > 0 && ResourceDefinitions.TryGetValue(id, out var resource))
                {
                    Resources[id] = 0;
                    events.Add(new("ResourceChanged", Name, Detail: resource.Name + "이(가) 사라졌습니다."));
                    expired.Add(id);
                }
            }
            return expired;
        }
        public void ApplyStatus(string id, int duration, string sourceSkillId, List<BattleEvent> events)
        {
            var previous = Statuses.GetValueOrDefault(id);
            // 지속방식=누적인 상태(고양)는 다시 받을 때마다 남은 턴에 더하고, 나머지는 큰 값으로 갱신한다.
            var accumulates = StatusDefinitions.GetValueOrDefault(id)?.AccumulatesDuration == true && Statuses.ContainsKey(id);
            var applied = accumulates ? previous + duration : Math.Max(previous, duration);
            if (applied == previous && Statuses.ContainsKey(id)) return;
            Statuses[id] = applied;
            StatusSources[id] = sourceSkillId;
            var name = StatusDefinitions.GetValueOrDefault(id)?.Name ?? id;
            events.Add(new("StatusApplied", Name, Amount: applied, Detail: name));
        }
        public void ApplyPermanentStatus(string id, List<BattleEvent> events)
        {
            if (!PermanentStatuses.Add(id)) return;
            var name = StatusDefinitions.GetValueOrDefault(id)?.Name ?? id;
            events.Add(new("PassiveApplied", Name, Detail: name));
        }
        public void ApplyPeriodicEffect(string statusId, string sourceName, double baseAmount, string message, bool heal = false)
            => PeriodicEffects.TryAdd(statusId, new PeriodicEffect(sourceName, baseAmount, message, heal));
        public (bool Damaged, bool Healed) TickPeriodicEffects(Rules rules, List<BattleEvent> events)
        {
            var damaged = false;
            var healed = false;
            foreach (var (statusId, periodic) in PeriodicEffects.ToArray())
            {
                if (!Statuses.ContainsKey(statusId)) { PeriodicEffects.Remove(statusId); continue; }
                if (periodic.Heal)
                {
                    var restored = Math.Min(MaxHp - Hp, Math.Max(1, (int)Math.Round(periodic.BaseAmount)));
                    if (restored <= 0) continue;
                    Hp += restored;
                    healed = true;
                    events.Add(new("StatusHeal", periodic.SourceName, Name, restored, StatusDefinitions.GetValueOrDefault(statusId)?.Name ?? periodic.Message));
                    continue;
                }
                var incoming = Math.Max(.1d, 1d + StatusValue("받는피해증가") - StatusValue("받는피해감소"));
                var amount = Math.Max(1, (int)Math.Round(Math.Max(1, periodic.BaseAmount - Defense * rules.DefenseCoefficient) * incoming));
                Hp = Math.Max(0, Hp - amount);
                damaged = true;
                var name = StatusDefinitions.GetValueOrDefault(statusId)?.Name ?? periodic.Message;
                events.Add(new("StatusDamage", periodic.SourceName, Name, amount, name));
                if (Hp == 0) events.Add(new("CharacterDefeated", periodic.SourceName, Name));
            }
            return (damaged, healed);
        }
        /// <summary>턴 시작에 상태 지속턴을 1 줄인다. 0이 된 상태도 이번 행동까지 적용하고 <see cref="ExpireStatuses"/>에서 제거한다.</summary>
        public void TickStatuses()
        {
            foreach (var id in Statuses.Keys.ToArray()) Statuses[id] = Math.Max(0, Statuses[id] - 1);
        }
        public IReadOnlyList<(string Id, string SourceSkillId)> ExpireStatuses(List<BattleEvent> events)
        {
            var expired = new List<(string Id, string SourceSkillId)>();
            foreach (var id in Statuses.Keys.ToArray())
            {
                // 행동 중 다시 부여되어 지속턴이 새로 채워진 상태는 남는다.
                if (Statuses[id] > 0) continue;
                var source = RemoveStatus(id);
                var name = StatusDefinitions.GetValueOrDefault(id)?.Name ?? id;
                events.Add(new("StatusExpired", Name, Detail: name));
                if (!string.IsNullOrEmpty(source)) expired.Add((id, source));
            }
            return expired;
        }
        /// <summary>상태를 즉시 제거한다. "상태만료 시" 파생은 발동하지 않는다.</summary>
        public string? RemoveStatus(string id)
        {
            if (!Statuses.Remove(id)) return null;
            PeriodicEffects.Remove(id);
            var source = StatusSources.GetValueOrDefault(id);
            StatusSources.Remove(id);
            return source;
        }
        /// <summary>지정한 효과유형을 가진 보유 상태를 소모한다(약점 노출의 첫 스킬 공격).</summary>
        public void ConsumeStatuses(string effectType, List<BattleEvent> events)
        {
            foreach (var id in Statuses.Keys.Where(id => StatusDefinitions.TryGetValue(id, out var status) && status.HasEffectType(effectType)).ToArray())
            {
                RemoveStatus(id);
                events.Add(new("StatusConsumed", Name, Detail: StatusDefinitions[id].Name));
            }
        }
        public BattleSkill? TakePendingSkill(BattleDataSnapshot data)
        {
            if (PendingSkillId is not { } id) return null;
            PendingSkillId = null;
            return data.Skills.GetValueOrDefault(id);
        }
    }

    private sealed record PeriodicEffect(string SourceName, double BaseAmount, string Message, bool Heal);

    private sealed class Rules(IReadOnlyDictionary<string, BattleRule> values)
    {
        private double Number(string id) => values.TryGetValue(id, out var rule) && double.TryParse(rule.Value, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidDataException($"배틀규칙 시트의 필수 규칙 '{id}'이(가) 없습니다.");
        public double BaseHp => Number("base_max_hp"); public double BaseAttack => Number("base_attack"); public double BaseDefense => Number("base_defense");
        public double PowerExponent => Number("power_scale_exponent"); public double PowerMin => Number("power_scale_min"); public double PowerMax => Number("power_scale_max");
        public double DefenseCoefficient => Number("defense_coefficient"); public double DamageVarianceMin => Number("damage_variance_min"); public double DamageVarianceMax => Number("damage_variance_max"); public double FixedDamageScale => Number("fixed_damage_scale");
        public double CriticalChance => Number("base_critical_chance"); public double CriticalMultiplier => Number("critical_damage_multiplier");
        public int MaxActions => checked((int)Number("max_major_actions")); public double DrawThreshold => Number("draw_hp_ratio_threshold"); public double TargetReleaseEvasionChance => Number("target_release_evasion_chance");
        public double NormalAttackMultiplier => Number("normal_attack_multiplier"); public double SkillDamageMinMultiplier => Number("skill_damage_min_multiplier"); public double SkillDamageMaxMultiplier => Number("skill_damage_max_multiplier"); public double UltimateDamageMultiplier => Number("ultimate_damage_multiplier");
        public double SkillHealMinMultiplier => Number("skill_heal_min_multiplier"); public double SkillHealMaxMultiplier => Number("skill_heal_max_multiplier"); public double UltimateHealMultiplier => Number("ultimate_heal_multiplier");
        public int MinimumSkillCooldown => checked((int)Number("minimum_skill_cooldown")); public int MaxSurpriseEvents => checked((int)Number("max_surprise_events_per_actor")); public int SurpriseCooldown => checked((int)Number("surprise_event_global_cooldown"));
        public double LifeSurpriseHpRatioThreshold => Number("life_surprise_hp_ratio_threshold"); public double LifeSurpriseHealRatio => Number("life_surprise_heal_ratio"); public double CharmSurpriseDamageMultiplier => Number("charm_surprise_damage_multiplier");
        public double AdditionalHitChance => Number("additional_hit_chance"); public double AdditionalHitDamageRatio => Number("additional_hit_damage_ratio");
        public int BreakGaugeMaximum => checked((int)Number("break_gauge_maximum")); public int BreakDuration => checked((int)Number("break_duration_turns"));
        public double LifeSurpriseChance(int lifePower) => SurpriseChance("life_surprise", lifePower);
        public double CharmSurpriseChance(int charmPower) => SurpriseChance("charm_surprise", charmPower);
        private double SurpriseChance(string prefix, int value) => Math.Min(Number(prefix + "_max_chance"), Number(prefix + "_base_chance") + Math.Min(2d, Math.Max(0, value) / Number(prefix + "_stat_reference")) * Number(prefix + "_stat_coefficient"));
    }
}
