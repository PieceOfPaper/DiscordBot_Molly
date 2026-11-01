using System.Collections.ObjectModel;
using Molly.Runes;

namespace Molly.RuneEnhance;

/// <summary>
/// 각인 강화 한 단계(게임 데이터 RuneEnhanceCost 한 행). Step은 목표 단계입니다(+10 → +11은 Step 11).
/// </summary>
/// <param name="CumulativePoint">이 단계에 도달하는 누적 강화 포인트(EnhancementPoint). 단계마다 필요한 양이 아니라 누적 기준값입니다.</param>
/// <param name="SuccessProbability">1회 판정 성공 확률(0~1].</param>
/// <param name="Gold">1회 시도 골드(Cost.Gold).</param>
/// <param name="ShardAmount">1회 시도 룬의 파편(Cost.Ingredients[0].Amount). ReturnAmount는 실패 시 반환량이 아니라서 담지 않습니다.</param>
/// <param name="CatalystCount">촉매 사용 시 1회 시도에 드는 강화 재연소 촉매 수. 0이면 이 단계에서는 촉매를 쓸 수 없습니다.</param>
public sealed record RuneEnhanceStep(int Step, long CumulativePoint, double SuccessProbability, long Gold, int ShardAmount, int CatalystCount);

/// <summary>부위 하나의 단계별 비용표(EnhanceCostGroupId 하나). Steps[i].Step == i + 1입니다.</summary>
public sealed class RuneEnhanceCostTable
{
    public RuneCategory Category { get; }
    public string GroupId { get; }
    public IReadOnlyList<RuneEnhanceStep> Steps { get; }

    public RuneEnhanceCostTable(RuneCategory category, string groupId, IEnumerable<RuneEnhanceStep> steps)
    {
        Category = category;
        GroupId = groupId;
        Steps = Array.AsReadOnly(steps.ToArray());
    }

    /// <summary>각인 강화 최대 단계(CarvingRuneEnhanceMaxLevel과 같은지 데이터셋이 검증합니다).</summary>
    public int MaxLevel => Steps.Count;

    /// <summary>+level에서 +level+1로 가는 단계.</summary>
    public RuneEnhanceStep StepFrom(int level) => Steps[level];

    /// <summary>+level에 도달하는 누적 강화 포인트. +0은 0입니다.</summary>
    public long CumulativeAt(int level) => level == 0 ? 0 : Steps[level - 1].CumulativePoint;

    /// <summary>+level에서 강화 포인트 0% → 100%에 필요한 포인트(= 성공 시 받는 포인트).</summary>
    public long Width(int level) => CumulativeAt(level + 1) - CumulativeAt(level);

    /// <summary>+level에서 실패 1회에 받는 포인트.</summary>
    public long FailPoints(int level) => RuneEnhanceRules.FailPoints(Width(level), StepFrom(level).SuccessProbability);
}

/// <summary>
/// 게임 데이터 저장소에서 만든 각인 강화 비용표(<see cref="UnpackRuneEnhanceDataset"/>).
/// 지원 범위(시즌 2 ★10 전설·신화)의 부위별 비용표와 재료 표시 이름을 담습니다.
/// </summary>
/// <param name="RuneCounts">부위별로 이 비용표를 쓰는 룬 수(전설+·전설++·신화 포함). 로그·검증용입니다.</param>
public sealed class RuneEnhanceData
{
    public int Season { get; }
    public int Tier { get; }
    public string ShardName { get; }
    public string CatalystName { get; }
    public IReadOnlyDictionary<RuneCategory, RuneEnhanceCostTable> Tables { get; }
    public IReadOnlyDictionary<RuneCategory, int> RuneCounts { get; }

    public RuneEnhanceData(int season, int tier, string shardName, string catalystName,
        IEnumerable<RuneEnhanceCostTable> tables, IReadOnlyDictionary<RuneCategory, int> runeCounts)
    {
        Season = season;
        Tier = tier;
        ShardName = shardName;
        CatalystName = catalystName;
        Tables = new ReadOnlyDictionary<RuneCategory, RuneEnhanceCostTable>(tables.ToDictionary(x => x.Category));
        RuneCounts = new ReadOnlyDictionary<RuneCategory, int>(runeCounts.ToDictionary(x => x.Key, x => x.Value));
    }
}
