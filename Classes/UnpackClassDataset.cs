using System.Text.Json;
using Molly.Unpack;

namespace Molly.Classes;

/// <param name="Id">게임 데이터의 CombatJob.Id(예: HighWarrior).</param>
/// <param name="Name">표시 이름(예: 전사).</param>
public sealed record ClassData(string Id, string Name);

public sealed class ClassTable(IEnumerable<ClassData> items)
{
    public IReadOnlyList<ClassData> Items { get; } = Array.AsReadOnly(items.ToArray());
}

/// <summary>
/// 게임 데이터 저장소의 CombatJob 시트로 플레이어가 고를 수 있는 클래스 목록을 만듭니다.
/// 전직 클래스(CombatJobType=FirstAdvanceCombatJob) 중 게임에 공개된 것(ShowClass=true)만 담습니다.
/// 견습 클래스와 클라이언트에 미리 들어 있는 미공개 클래스는 제외해 퀴즈 등에 노출되지 않게 합니다. 순서는 CombatJob.Order입니다.
/// </summary>
public sealed class UnpackClassDataset : IUnpackDataset<ClassTable>
{
    public string Name => "클래스";
    public string CacheName => "classes";

    public async Task<ClassTable> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var items = new List<(int Order, ClassData Data)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var combatJob = await sheets("CombatJob", ct))
        {
            foreach (var row in UnpackSheets.Rows(combatJob))
            {
                if (UnpackSheets.Text(row, "CombatJobType") != "FirstAdvanceCombatJob") continue;
                if (!row.TryGetProperty("ShowClass", out var show) || show.ValueKind != JsonValueKind.True) continue;
                if (UnpackSheets.Reference(row, "Id") is not { } id) continue;
                var name = UnpackSheets.Text(row, "DisplayName").Trim();
                if (name.Length == 0) throw new InvalidDataException($"클래스 {id}의 이름이 비어 있습니다.");
                if (!names.Add(name)) throw new InvalidDataException($"클래스 이름 '{name}'이 중복됩니다.");
                items.Add(((int)(UnpackSheets.Number(row, "Order") ?? 0), new ClassData(id, name)));
            }
        }
        if (items.Count == 0) throw new InvalidDataException("CombatJob 시트에 공개된 전직 클래스가 없습니다.");
        return new ClassTable(items.OrderBy(x => x.Order).ThenBy(x => x.Data.Id, StringComparer.Ordinal).Select(x => x.Data));
    }

    public void Write(Utf8JsonWriter writer, ClassTable data)
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

    public ClassTable Read(JsonElement data)
    {
        var items = data.EnumerateArray().Select(x => new ClassData(x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!)).ToArray();
        if (items.Length == 0) throw new InvalidDataException("캐시에 클래스가 없습니다.");
        return new ClassTable(items);
    }

    public string Describe(ClassTable data) => $"클래스 {data.Items.Count}개";
}
