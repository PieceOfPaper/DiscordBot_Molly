using System.Globalization;
using System.Text.Json;
using Molly.Runes;
using Molly.Unpack;

namespace Molly.RuneEnhance;

/// <summary>
/// 게임 데이터 저장소의 시트로 각인 강화 비용표를 만듭니다(관계는 저장소 Docs/SheetRelations.md).
/// - Rune: 시즌 2(Season02) ★10(Tier 10) 전설·신화 룬의 부위(RuneAttachableType)·EnhanceCostGroupId·CarvingRuneEnhanceMaxLevel.
///   전설+·전설++는 별도 희귀도가 아니라 같은 등급의 다른 룬이므로 함께 담깁니다. 비용표 이름의 Tier 숫자(RuneEnhance_Tier11)는
///   Rune.Tier와 다르므로 이름으로 고르지 않고 EnhanceCostGroupId 참조로만 연결합니다.
///   부위마다 비용표·최대 단계가 하나로 같아야 하며, 다르면 검증 실패로 봅니다(이 기능은 부위만 입력받기 때문).
/// - RuneEnhanceCost: 그 비용표의 단계별 누적 포인트·성공 확률·골드·재료·촉매.
/// - Consumable.DisplayName: 재료(룬의 파편)·촉매 표시 이름.
/// </summary>
public sealed class UnpackRuneEnhanceDataset : IUnpackDataset<RuneEnhanceData>
{
    public string Name => "룬 각인 강화";
    public string CacheName => "rune-enhance";

    private sealed record RuneGroup(string GroupId, int MaxLevel, string FirstRune);

    public async Task<RuneEnhanceData> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var groups = new Dictionary<RuneCategory, RuneGroup>();
        var counts = new Dictionary<RuneCategory, int>();
        using (var rune = await sheets("Rune", ct))
        {
            foreach (var row in UnpackSheets.Rows(rune))
            {
                if (UnpackSheets.Text(row, "Season") != $"Season{RuneEnhanceRules.SupportedSeason:00}") continue;
                if (UnpackSheets.Text(row, "Rarity") is not ("Legendary" or "Mythic")) continue;
                if (UnpackSheets.Number(row, "Tier") is not { } tier || (int)tier != RuneEnhanceRules.SupportedTier) continue;
                if (CategoryOf(UnpackSheets.Text(row, "RuneAttachableType")) is not { } category) continue;
                var id = UnpackSheets.Reference(row, "Id") ?? "";
                if (UnpackSheets.Reference(row, "EnhanceCostGroupId") is not { } groupId)
                    throw new InvalidDataException($"{id}에 EnhanceCostGroupId가 없습니다.");
                var maxLevel = (int)(UnpackSheets.Number(row, "CarvingRuneEnhanceMaxLevel") ?? 0);
                if (groups.TryGetValue(category, out var existing))
                {
                    if (existing.GroupId != groupId || existing.MaxLevel != maxLevel)
                        throw new InvalidDataException($"{category} 룬의 각인 강화 비용표가 서로 다릅니다" +
                                                       $"({existing.FirstRune} {existing.GroupId}/+{existing.MaxLevel}, {id} {groupId}/+{maxLevel}).");
                }
                else groups[category] = new RuneGroup(groupId, maxLevel, id);
                counts[category] = counts.GetValueOrDefault(category) + 1;
            }
        }
        foreach (var category in Enum.GetValues<RuneCategory>())
        {
            if (!groups.TryGetValue(category, out var group))
                throw new InvalidDataException($"시즌 {RuneEnhanceRules.SupportedSeason} ★{RuneEnhanceRules.SupportedTier} 전설·신화 {category} 룬이 없습니다.");
            if (group.MaxLevel <= 0) throw new InvalidDataException($"{group.FirstRune}의 CarvingRuneEnhanceMaxLevel이 없습니다.");
        }

        var wanted = groups.Values.Select(x => x.GroupId).ToHashSet(StringComparer.Ordinal);
        var rows = new Dictionary<string, SortedDictionary<int, (RuneEnhanceStep Step, string Shard, string Catalyst)>>(StringComparer.Ordinal);
        using (var cost = await sheets("RuneEnhanceCost", ct))
        {
            foreach (var row in UnpackSheets.Rows(cost))
            {
                if (UnpackSheets.Reference(row, "GroupId") is not { } groupId || !wanted.Contains(groupId)) continue;
                var parsed = ReadStep(row, groupId);
                if (!rows.TryGetValue(groupId, out var steps)) rows[groupId] = steps = [];
                if (!steps.TryAdd(parsed.Step.Step, parsed)) throw new InvalidDataException($"{groupId}의 {parsed.Step.Step}단계가 중복됩니다.");
            }
        }

        string? shardId = null, catalystId = null;
        var tables = new List<RuneEnhanceCostTable>();
        foreach (var (category, group) in groups.OrderBy(x => x.Key))
        {
            if (!rows.TryGetValue(group.GroupId, out var steps)) throw new InvalidDataException($"RuneEnhanceCost에 {group.GroupId}가 없습니다.");
            var list = new List<RuneEnhanceStep>();
            for (var step = 1; step <= group.MaxLevel; step++)
            {
                if (!steps.TryGetValue(step, out var parsed)) throw new InvalidDataException($"{group.GroupId}에 {step}단계가 없습니다.");
                if (list.Count > 0 && parsed.Step.CumulativePoint <= list[^1].CumulativePoint)
                    throw new InvalidDataException($"{group.GroupId} {step}단계의 누적 강화 포인트가 이전 단계보다 크지 않습니다.");
                shardId ??= parsed.Shard;
                catalystId ??= parsed.Catalyst;
                if (parsed.Shard != shardId || parsed.Catalyst != catalystId)
                    throw new InvalidDataException($"{group.GroupId} {step}단계의 재료·촉매 종류가 다른 단계와 다릅니다({parsed.Shard}, {parsed.Catalyst}).");
                list.Add(parsed.Step);
            }
            var table = new RuneEnhanceCostTable(category, group.GroupId, list);
            for (var level = 0; level < table.MaxLevel; level++)
                if (table.StepFrom(level).SuccessProbability < 1 && table.FailPoints(level) <= 0)
                    throw new InvalidDataException($"{group.GroupId} +{level}의 실패 포인트가 0입니다.");
            tables.Add(table);
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var consumable = await sheets("Consumable", ct))
        {
            foreach (var row in UnpackSheets.Rows(consumable))
                if (UnpackSheets.Reference(row, "Id") is { } id && (id == shardId || id == catalystId))
                    names[id] = UnpackSheets.Text(row, "DisplayName").Trim();
        }
        string Name(string id) => names.TryGetValue(id, out var name) && name.Length > 0
            ? name
            : throw new InvalidDataException($"Consumable에 {id}의 이름이 없습니다.");

        return new RuneEnhanceData(RuneEnhanceRules.SupportedSeason, RuneEnhanceRules.SupportedTier, Name(shardId!), Name(catalystId!), tables, counts);
    }

    // Cost = {"Catalyst", "CatalystCount", "Gold", "Ingredients": [{"Consumable", "Amount", "ReturnAmount"}]}
    // ReturnAmount는 실패 반환량이 아니어서(실측: 실패해도 전량 소모) 읽지 않습니다.
    private static (RuneEnhanceStep Step, string Shard, string Catalyst) ReadStep(JsonElement row, string groupId)
    {
        var step = (int)(UnpackSheets.Number(row, "EnhanceStep") ?? 0);
        var point = UnpackSheets.Number(row, "EnhancementPoint") ?? 0;
        var probability = UnpackSheets.Number(row, "SuccessProbability") ?? 0;
        if (step <= 0) throw new InvalidDataException($"{groupId}에 EnhanceStep이 없는 행이 있습니다.");
        if (point <= 0) throw new InvalidDataException($"{groupId} {step}단계의 EnhancementPoint가 없습니다.");
        if (probability is <= 0 or > 1) throw new InvalidDataException($"{groupId} {step}단계의 성공 확률이 올바르지 않습니다({probability.ToString(CultureInfo.InvariantCulture)}).");
        if (!row.TryGetProperty("Cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{groupId} {step}단계에 Cost가 없습니다.");
        var gold = UnpackSheets.Number(cost, "Gold") ?? -1;
        var catalystCount = UnpackSheets.Number(cost, "CatalystCount") ?? -1;
        var catalyst = UnpackSheets.Reference(cost, "Catalyst") ?? throw new InvalidDataException($"{groupId} {step}단계에 촉매 종류가 없습니다.");
        if (gold < 0 || catalystCount < 0) throw new InvalidDataException($"{groupId} {step}단계의 골드·촉매 수가 올바르지 않습니다.");
        if (!cost.TryGetProperty("Ingredients", out var ingredients) || ingredients.ValueKind != JsonValueKind.Array || ingredients.GetArrayLength() != 1)
            throw new InvalidDataException($"{groupId} {step}단계의 재료가 하나가 아닙니다.");
        var ingredient = ingredients[0];
        var shard = UnpackSheets.Reference(ingredient, "Consumable") ?? throw new InvalidDataException($"{groupId} {step}단계의 재료 종류가 없습니다.");
        var amount = UnpackSheets.Number(ingredient, "Amount") ?? 0;
        if (amount <= 0) throw new InvalidDataException($"{groupId} {step}단계의 재료 수량이 없습니다.");
        return (new RuneEnhanceStep(step, (long)point, probability, (long)gold, (int)amount, (int)catalystCount), shard, catalyst);
    }

    private static RuneCategory? CategoryOf(string attachableType) => attachableType switch
    {
        "Weapon" => RuneCategory.Weapon,
        "Armor" => RuneCategory.Armor,
        "Emblem" => RuneCategory.Emblem,
        "AccessoryAndRing" => RuneCategory.Accessory,
        _ => null,
    };

    public void Write(Utf8JsonWriter writer, RuneEnhanceData data)
    {
        writer.WriteStartObject();
        writer.WriteNumber("season", data.Season);
        writer.WriteNumber("tier", data.Tier);
        writer.WriteString("shard", data.ShardName);
        writer.WriteString("catalyst", data.CatalystName);
        writer.WriteStartArray("tables");
        foreach (var table in data.Tables.Values.OrderBy(x => x.Category))
        {
            writer.WriteStartObject();
            writer.WriteString("category", table.Category.ToString());
            writer.WriteString("group", table.GroupId);
            writer.WriteNumber("runes", data.RuneCounts.GetValueOrDefault(table.Category));
            writer.WriteStartArray("steps");
            foreach (var step in table.Steps)
            {
                writer.WriteStartObject();
                writer.WriteNumber("step", step.Step);
                writer.WriteNumber("point", step.CumulativePoint);
                writer.WriteNumber("p", step.SuccessProbability);
                writer.WriteNumber("gold", step.Gold);
                writer.WriteNumber("shards", step.ShardAmount);
                writer.WriteNumber("catalysts", step.CatalystCount);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public RuneEnhanceData Read(JsonElement data)
    {
        var tables = new List<RuneEnhanceCostTable>();
        var counts = new Dictionary<RuneCategory, int>();
        foreach (var table in data.GetProperty("tables").EnumerateArray())
        {
            var category = Enum.Parse<RuneCategory>(table.GetProperty("category").GetString()!);
            counts[category] = table.GetProperty("runes").GetInt32();
            tables.Add(new RuneEnhanceCostTable(category, table.GetProperty("group").GetString()!,
                table.GetProperty("steps").EnumerateArray().Select(x => new RuneEnhanceStep(
                    x.GetProperty("step").GetInt32(), x.GetProperty("point").GetInt64(), x.GetProperty("p").GetDouble(),
                    x.GetProperty("gold").GetInt64(), x.GetProperty("shards").GetInt32(), x.GetProperty("catalysts").GetInt32()))));
        }
        if (Enum.GetValues<RuneCategory>().Any(c => tables.All(t => t.Category != c)))
            throw new InvalidDataException("캐시에 빠진 부위 비용표가 있습니다.");
        if (tables.Any(t => t.Steps.Count == 0 || t.Steps.Select((s, i) => s.Step != i + 1).Any(x => x)))
            throw new InvalidDataException("캐시의 각인 강화 단계가 올바르지 않습니다.");
        return new RuneEnhanceData(data.GetProperty("season").GetInt32(), data.GetProperty("tier").GetInt32(),
            data.GetProperty("shard").GetString()!, data.GetProperty("catalyst").GetString()!, tables, counts);
    }

    public string Describe(RuneEnhanceData data) =>
        $"시즌 {data.Season} ★{data.Tier} 전설·신화 " + string.Join(" · ", data.Tables.Values.OrderBy(x => x.Category)
            .Select(x => $"{x.Category} {x.GroupId}(+{x.MaxLevel}, 룬 {data.RuneCounts.GetValueOrDefault(x.Category)}개)"));
}
