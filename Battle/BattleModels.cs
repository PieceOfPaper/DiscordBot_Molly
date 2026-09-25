using System.Collections.ObjectModel;

namespace Molly.Battle;

/// <summary>Google Sheets에서 검증해 한 번에 교체하는 전투 데이터입니다.</summary>
public sealed class BattleDataSnapshot
{
    public static readonly BattleDataSnapshot Empty = new();
    public IReadOnlyDictionary<string, BattleRule> Rules { get; init; } = new ReadOnlyDictionary<string, BattleRule>(new Dictionary<string, BattleRule>());
    public IReadOnlyDictionary<string, BattleClass> Classes { get; init; } = new ReadOnlyDictionary<string, BattleClass>(new Dictionary<string, BattleClass>());
    public IReadOnlyDictionary<string, BattleSkill> Skills { get; init; } = new ReadOnlyDictionary<string, BattleSkill>(new Dictionary<string, BattleSkill>());
    public IReadOnlyDictionary<string, BattlePassive> Passives { get; init; } = new ReadOnlyDictionary<string, BattlePassive>(new Dictionary<string, BattlePassive>());
    public IReadOnlyDictionary<string, BattleResource> Resources { get; init; } = new ReadOnlyDictionary<string, BattleResource>(new Dictionary<string, BattleResource>());
    public IReadOnlyDictionary<string, BattleStatus> Statuses { get; init; } = new ReadOnlyDictionary<string, BattleStatus>(new Dictionary<string, BattleStatus>());
    public IReadOnlyList<BattleDerivation> Derivations { get; init; } = Array.Empty<BattleDerivation>();
    /// <summary>생활력으로 드물게 주요 행동을 대신하는 배틀생활스킬. 비어 있으면 생활스킬 판정을 하지 않고 난수도 소비하지 않는다.</summary>
    public IReadOnlyList<BattleLifeSkill> LifeSkills { get; init; } = Array.Empty<BattleLifeSkill>();
    public DateTimeOffset LoadedAt { get; init; }
    public bool IsUsable => Rules.Count > 0 && Classes.Count > 0 && Skills.Count > 0;
    private IReadOnlySet<string>? battleReadyClassIds;
    /// <summary>전투를 시작할 수 있는 클래스 ID. 시트 로딩 때 미리 계산해 두고, /배틀 신청 단계에서 스레드를 만들기 전에 판정한다.</summary>
    public IReadOnlySet<string> BattleReadyClassIds { get => battleReadyClassIds ??= ComputeBattleReadyClassIds(Classes, Skills); init => battleReadyClassIds = value; }
    public bool IsClassBattleReady(string classId) => BattleReadyClassIds.Contains(classId);
    /// <summary>클래스 시트의 스킬 칸이 모두 채워져 있고, 그 스킬이 모두 배틀스킬 시트에 있어야 전투할 수 있다.</summary>
    public static IReadOnlySet<string> ComputeBattleReadyClassIds(IReadOnlyDictionary<string, BattleClass> classes, IReadOnlyDictionary<string, BattleSkill> skills)
        => classes.Values.Where(x => x.IsBattleReady && x.SkillIds.All(skills.ContainsKey)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
}

public sealed record BattleRule(string Id, string Category, string ValueType, string Value, string Description)
{
    public double Number => double.Parse(Value, System.Globalization.CultureInfo.InvariantCulture);
    public int Integer => int.Parse(Value, System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record BattleClass(string Id, string Name, IReadOnlyList<string> SkillIds, bool IsBattleReady = true, IReadOnlyList<string>? PassiveIds = null)
{
    public IReadOnlyList<string> PassiveIds { get; init; } = PassiveIds ?? Array.Empty<string>();
}
public sealed record BattleSkill(string Id, string Name, string Kind, string? ParentSkillId, bool Enabled,
    int Cooldown, int InitialCooldown, int Priority, double Weight, IReadOnlyList<BattleEffect> Effects,
    string? ResourceId = null, string? ResourceCost = null, string? ResourceGain = null);
/// <summary>클래스가 상시로 갖고 있는 패시브입니다. 행동 후보로 선택되지 않고 <see cref="BattleEffect.Trigger"/> 시점마다 자동 발동합니다.</summary>
public sealed record BattlePassive(string Id, bool Enabled, IReadOnlyList<BattleEffect> Effects);
public sealed record BattleEffect(
    string Id, int Order, string Type, string Target, int FixedValue, int Count, double Chance,
    int Duration, string? StatusId, int MaxStacks, string? Message,
    string? ConditionTarget, string? ConditionType, string? ConditionId,
    string? ConditionOperator, string? ConditionValue, string? NumericReferenceId,
    string? NumericReferenceMode, double CriticalChanceMultiplierPerHit = 1d,
    string? Trigger = null, string? TriggerSkillId = null, string? TriggerResourceId = null, int ReactivationCooldown = 0);
public sealed record BattleResource(string Id, string Name, string Kind, int Maximum, int InitialValue, int Duration, string Stacking);
public sealed record BattleStatus(string Id, string Name, string EffectType, double Value, string Description, string? TargetSkillId = null, string? StackResourceId = null)
{
    /// <summary>복합 상태의 효과별 값. 시트의 <c>효과별값</c>에 <c>1|0.1</c>처럼 효과유형 순서대로 적으면 효과마다 다른 값을 쓴다. 비어 있으면 모든 효과가 <see cref="Value"/>를 쓴다.</summary>
    public IReadOnlyList<double> Values { get; init; } = Array.Empty<double>();
    /// <summary>[시너지] 옵션인 효과유형. 같은 효과유형의 시너지 옵션끼리는 가장 높은 값 하나만 적용된다.</summary>
    public IReadOnlySet<string> SynergyTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>true이면 이미 보유 중일 때 다시 받으면 남은 턴에 지속턴을 더한다. false이면 남은 턴과 새 지속턴 중 큰 값으로 갱신한다.</summary>
    public bool AccumulatesDuration { get; init; }
    public IReadOnlyList<string> EffectTypes => EffectType.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    /// <summary>복합 상태는 시트에서 <c>효과A|효과B</c>로 선언한다.</summary>
    public bool HasEffectType(string effectType) => EffectTypes.Contains(effectType, StringComparer.Ordinal);
    public double ValueOf(string effectType)
    {
        if (Values.Count == 0) return Value;
        var index = EffectTypes.ToList().IndexOf(effectType);
        return index >= 0 && index < Values.Count ? Values[index] : Value;
    }
    public bool IsSynergy(string effectType) => SynergyTypes.Contains(effectType);
}
public sealed record BattleDerivation(string Id, string ParentSkillId, string ChildSkillId, string ActivationMode,
    double Weight, double Chance, string? ConditionType, string? ConditionValue, bool AllowDuplicate, string Timing, int Priority);

/// <summary><c>배틀생활스킬</c> 한 행. 이름은 <c>생활스킬</c> 시트의 원본 이름과 같다.</summary>
public sealed record BattleLifeSkill(string Id, string Name, bool Enabled,
    double BaseChance, double LifeReference, double LifeChanceCoefficient, double MaxChance, int MaxUses, double Weight,
    string ConditionTarget, string ConditionType, string ConditionOperator, double ConditionValue,
    string ActionMessage, IReadOnlyList<BattleLifeSkillEffect> Effects)
{
    /// <summary>행동당 사용 확률. 생활력 보정은 기준값의 2배에서 멈춘다.</summary>
    public double UseChance(int lifePower)
        => Math.Clamp(BaseChance + Math.Clamp(Math.Max(0, lifePower) / LifeReference, 0d, 2d) * LifeChanceCoefficient, 0d, MaxChance);
}

/// <summary><c>배틀생활스킬효과</c> 한 행. <see cref="SelectionGroup"/>이 같은 행은 가중치로 하나만 실행한다.</summary>
public sealed record BattleLifeSkillEffect(string Id, string LifeSkillId, int Order, string Type, string Target,
    string Basis, double Coefficient, int FixedValue, bool LifeScaled, double LifeReference, double MinMultiplier, double MaxMultiplier,
    int Count, int Duration, string? Message, string? SelectionGroup, double SelectionWeight)
{
    public double LifeMultiplier(int lifePower)
        => LifeScaled ? Math.Clamp(Math.Sqrt(Math.Max(0, lifePower) / LifeReference), MinMultiplier, MaxMultiplier) : 1d;
}

public sealed record CharacterBattleSnapshot(ulong DiscordUserId, string CharacterName, string ClassId,
    int CombatPower, int LifePower, int CharmPower);

public enum BattleOutcome { FighterAWin, FighterBWin, Draw }
public sealed record BattleEvent(string Type, string Actor, string? Target = null, int? Amount = null, string? Detail = null);
public sealed record BattleResult(BattleOutcome Outcome, int MajorActions, int FighterAHp, int FighterAMaxHp,
    int FighterBHp, int FighterBMaxHp, IReadOnlyList<BattleEvent> Events);

public interface IBattleRandom
{
    double NextDouble();
}

public sealed class SystemBattleRandom : IBattleRandom
{
    private readonly Random random = Random.Shared;
    public double NextDouble() => random.NextDouble();
}
