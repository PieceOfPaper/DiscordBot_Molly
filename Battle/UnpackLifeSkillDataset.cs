using System.Text.Json;
using Molly.Unpack;

namespace Molly.Battle;

/// <param name="Id">게임 데이터의 LivingSkill.Id(예: Logging).</param>
/// <param name="Name">표시 이름(예: 나무 베기). 배틀생활스킬.생활스킬이름이 이 값과 정확히 일치해야 합니다.</param>
public sealed record LifeSkillData(string Id, string Name);

public sealed class LifeSkillTable(IEnumerable<LifeSkillData> items)
{
    public IReadOnlyList<LifeSkillData> Items { get; } = Array.AsReadOnly(items.ToArray());
    public IReadOnlyCollection<string> Names { get; } = items.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// 게임 데이터 저장소의 LivingSkill 시트로 생활스킬 목록을 만듭니다. 배틀이 배틀생활스킬 시트의 이름을 확인할 때 씁니다.
/// Google Sheets `생활스킬` 탭을 대신합니다. 순서는 LivingSkill 시트 순서입니다.
/// </summary>
public sealed class UnpackLifeSkillDataset : IUnpackDataset<LifeSkillTable>
{
    public string Name => "생활스킬";
    public string CacheName => "life-skills";

    public async Task<LifeSkillTable> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var items = new List<LifeSkillData>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var livingSkill = await sheets("LivingSkill", ct))
        {
            foreach (var row in UnpackSheets.Rows(livingSkill))
            {
                if (UnpackSheets.Reference(row, "Id") is not { } id) continue;
                var name = UnpackSheets.Text(row, "DisplayName").Trim();
                if (name.Length == 0) throw new InvalidDataException($"생활스킬 {id}의 이름이 비어 있습니다.");
                if (!names.Add(name)) throw new InvalidDataException($"생활스킬 이름 '{name}'이 중복됩니다.");
                items.Add(new LifeSkillData(id, name));
            }
        }
        if (items.Count == 0) throw new InvalidDataException("LivingSkill 시트에 생활스킬이 없습니다.");
        return new LifeSkillTable(items);
    }

    public void Write(Utf8JsonWriter writer, LifeSkillTable data)
    {
        writer.WriteStartArray();
        foreach (var item in data.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            writer.WriteString("name", item.Name);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public LifeSkillTable Read(JsonElement data)
    {
        var items = data.EnumerateArray().Select(x => new LifeSkillData(x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!)).ToArray();
        if (items.Length == 0) throw new InvalidDataException("캐시에 생활스킬이 없습니다.");
        return new LifeSkillTable(items);
    }

    public string Describe(LifeSkillTable data) => $"생활스킬 {data.Items.Count}개";
}
