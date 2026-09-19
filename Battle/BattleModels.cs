using System.Collections.ObjectModel;

namespace Molly.Battle;

/// <summary>Google Sheets에서 검증해 한 번에 교체하는 전투 데이터입니다.</summary>
public sealed class BattleDataSnapshot
{
    public static readonly BattleDataSnapshot Empty = new();
    public IReadOnlyDictionary<string, BattleRule> Rules { get; init; } = new ReadOnlyDictionary<string, BattleRule>(new Dictionary<string, BattleRule>());
    public IReadOnlyDictionary<string, BattleClass> Classes { get; init; } = new ReadOnlyDictionary<string, BattleClass>(new Dictionary<string, BattleClass>());
    public IReadOnlyDictionary<string, BattleSkill> Skills { get; init; } = new ReadOnlyDictionary<string, BattleSkill>(new Dictionary<string, BattleSkill>());
    public DateTimeOffset LoadedAt { get; init; }
    public bool IsUsable => Rules.Count > 0 && Classes.Count > 0 && Skills.Count > 0;
}

public sealed record BattleRule(string Id, string Category, string ValueType, string Value, string Description)
{
    public double Number => double.Parse(Value, System.Globalization.CultureInfo.InvariantCulture);
    public int Integer => int.Parse(Value, System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record BattleClass(string Id, string Name, IReadOnlyList<string> SkillIds, bool IsBattleReady = true);
public sealed record BattleSkill(string Id, string Name, string Kind, string? ParentSkillId, bool Enabled,
    int Cooldown, int InitialCooldown, int Priority, double Weight, IReadOnlyList<BattleEffect> Effects);
public sealed record BattleEffect(
    string Id, int Order, string Type, string Target, int Count, double Chance,
    int Duration, string? StatusId, int MaxStacks, string? Message,
    string? ConditionTarget, string? ConditionType, string? ConditionId,
    string? ConditionOperator, string? ConditionValue, string? NumericReferenceId,
    string? NumericReferenceMode);

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
