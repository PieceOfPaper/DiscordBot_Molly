using System.Text.Json;
using Molly.Unpack;

namespace Molly.SkillQuiz;

/// <summary>클래스 슬롯 관계에서만 만든 스킬 아이콘 퀴즈 원본입니다. 이름 접두어로 클래스 소속을 추측하지 않습니다.</summary>
public sealed record SkillQuizEntry(string ClassId, string ClassName, string SkillId, string SkillName, bool Passive, string IconPath, bool RuneModified);
public sealed class SkillQuizTable(IEnumerable<SkillQuizEntry> items)
{
    public IReadOnlyList<SkillQuizEntry> Items { get; } = items.ToArray();
}

public sealed class UnpackSkillQuizDataset : IUnpackDataset<SkillQuizTable>
{
    public string Name => "스킬 아이콘 퀴즈";
    public string CacheName => "skill-icon-quiz";

    public async Task<SkillQuizTable> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        using var jobs = await sheets("CombatJob", ct);
        using var slots = await sheets("SkillSlot", ct);
        using var skills = await sheets("Skill", ct);
        var classes = UnpackSheets.Rows(jobs).Where(r => UnpackSheets.Text(r, "CombatJobType") == "FirstAdvanceCombatJob" &&
                r.TryGetProperty("ShowClass", out var shown) && shown.ValueKind == JsonValueKind.True)
            .Select(r => (Id: UnpackSheets.Reference(r, "Id"), Name: UnpackSheets.Text(r, "DisplayName").Trim()))
            .Where(x => x.Id is not null && x.Name.Length > 0).ToDictionary(x => x.Id!, x => x.Name, StringComparer.Ordinal);
        if (classes.Count == 0) throw new InvalidDataException("공개 전직 클래스가 없습니다.");
        var skillRows = UnpackSheets.Rows(skills).Select(r => (Id: UnpackSheets.Reference(r, "Id"), Row: r))
            .Where(x => x.Id is not null).ToDictionary(x => x.Id!, x => x.Row, StringComparer.Ordinal);
        var output = new List<SkillQuizEntry>();
        foreach (var row in UnpackSheets.Rows(slots))
        {
            var classId = Ref(row, "CombatJob", "CombatJobId", "Job");
            var skillId = Ref(row, "Skill", "SkillId", "SkillData");
            var slot = Text(row, "Slot", "SlotType", "SlotIndex", "Id");
            if (classId is null || skillId is null || !classes.TryGetValue(classId, out var className) || !skillRows.TryGetValue(skillId, out var skill)) continue;
            // Slot01~06 / PassiveSlot01~06만: SeasonSkillSlot·기본 공격·시스템 슬롯을 구조로 배제합니다.
            var passive = slot.StartsWith("PassiveSlot", StringComparison.Ordinal);
            if (!(passive || (slot.StartsWith("Slot", StringComparison.Ordinal) && slot.Length == 6 && char.IsDigit(slot[^1])))) continue;
            var name = Text(skill, "DisplayName", "Name").Trim();
            var icon = AssetPath(skill, "Icon", "IconPath", "SkillIcon", "Image");
            if (name.Length == 0 || icon.Length == 0 || IsCombatMastery(slot)) continue;
            // 룬 모딩 행은 SkillSlot의 별도 행으로도 들어오며, 일반 시즌 행은 버리지 않습니다.
            var modified = Ref(row, "SkillModding", "SkillModdingId", "SkillCard", "SkillCardId", "Rune", "ModdingSkill") is not null ||
                Text(skill, "OriginalSkill", "BaseSkill").Length > 0;
            output.Add(new SkillQuizEntry(classId, className, skillId, name, passive, icon, modified));
        }
        var unique = output.DistinctBy(x => (x.ClassId, x.SkillId, x.IconPath)).ToArray();
        if (unique.Length == 0)
        {
            var sample = UnpackSheets.Rows(slots).FirstOrDefault();
            var fields = sample.ValueKind == JsonValueKind.Object ? string.Join(", ", sample.EnumerateObject().Select(x => x.Name)) : "없음";
            throw new InvalidDataException($"공개 클래스의 액티브·패시브 SkillSlot 연결을 찾지 못했습니다(SkillSlot 필드: {fields}).");
        }
        return new SkillQuizTable(unique);
    }

    private static bool IsCombatMastery(string slotName)
    {
        // 최신 공개 전직 21개 모두에서 실제 연결을 확인한 PassiveSlot03이 전투숙련이다. 내부 ID 접두어는 쓰지 않는다.
        return slotName.Equals("PassiveSlot03", StringComparison.Ordinal);
    }
    private static string? Ref(JsonElement row, params string[] fields) => fields.Select(f => UnpackSheets.Reference(row, f)).FirstOrDefault(x => x is not null);
    private static string Text(JsonElement row, params string[] fields) => fields.Select(f => row.TryGetProperty(f, out var v) ? v.ToString() : "").FirstOrDefault(x => x.Length > 0) ?? "";
    private static string AssetPath(JsonElement row, params string[] fields)
    {
        foreach (var field in fields)
            if (row.TryGetProperty(field, out var value))
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } single) return single;
                if (value.ValueKind == JsonValueKind.Array)
                    foreach (var x in value.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } item) return item;
            }
        return "";
    }

    public void Write(Utf8JsonWriter w, SkillQuizTable data)
    {
        w.WriteStartArray(); foreach (var x in data.Items) { w.WriteStartObject(); w.WriteString("classId", x.ClassId); w.WriteString("className", x.ClassName); w.WriteString("skillId", x.SkillId); w.WriteString("skillName", x.SkillName); w.WriteBoolean("passive", x.Passive); w.WriteString("icon", x.IconPath); w.WriteBoolean("runeModified", x.RuneModified); w.WriteEndObject(); } w.WriteEndArray();
    }
    public SkillQuizTable Read(JsonElement data) => new(data.EnumerateArray().Select(x => new SkillQuizEntry(x.GetProperty("classId").GetString()!, x.GetProperty("className").GetString()!, x.GetProperty("skillId").GetString()!, x.GetProperty("skillName").GetString()!, x.GetProperty("passive").GetBoolean(), x.GetProperty("icon").GetString()!, x.TryGetProperty("runeModified", out var r) && r.GetBoolean())));
    public string Describe(SkillQuizTable data) => $"스킬 연결 {data.Items.Count}개(클래스 {data.Items.Select(x => x.ClassId).Distinct().Count()}개, 룬 변형 {data.Items.Count(x => x.RuneModified)}개)";
}
