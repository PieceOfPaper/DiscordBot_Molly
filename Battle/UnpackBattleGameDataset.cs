using System.Text.Json;
using Molly.Unpack;

namespace Molly.Battle;

/// <param name="CombatJobId">게임 데이터의 CombatJob.Id(예: HighWarrior). 몰리 클래스 ID는 <see cref="ClassIcons.FromCombatJob"/>로 바꿉니다.</param>
/// <param name="Name">표시 이름(예: 전사).</param>
public sealed record BattleGameClass(string CombatJobId, string Name);

/// <param name="CombatJobId">이 스킬이 들어 있는 클래스.</param>
/// <param name="Slot">SkillSlot.SlotIndex(Slot01~06, PassiveSlot01~06).</param>
public sealed record BattleGameSlot(string CombatJobId, string Slot)
{
    public bool IsPassive => Slot.StartsWith("PassiveSlot", StringComparison.Ordinal);
}

/// <param name="Id">게임 데이터의 Skill.Id(예: Arbalist_MountingShock). 배틀스킬·배틀패시브 ID가 이 값을 그대로 씁니다.</param>
/// <param name="Name">Skill.DisplayName.</param>
/// <param name="Slots">이 스킬이 들어 있는 클래스 슬롯. 시즌·룬 변형을 가리지 않고 모읍니다.</param>
public sealed record BattleGameSkill(string Id, string Name, IReadOnlyList<BattleGameSlot> Slots);

public sealed class BattleGameData(IEnumerable<BattleGameClass> classes, IEnumerable<BattleGameSkill> skills)
{
    /// <summary>공개된 전직 클래스. 순서는 CombatJob.Order입니다.</summary>
    public IReadOnlyList<BattleGameClass> Classes { get; } = Array.AsReadOnly(classes.ToArray());
    public IReadOnlyDictionary<string, BattleGameSkill> Skills { get; } = skills.ToDictionary(x => x.Id, StringComparer.Ordinal);
}

/// <summary>
/// 게임 데이터 저장소의 CombatJob·SkillSlot·Skill 시트로 배틀이 쓰는 클래스 목록, 스킬 이름, 클래스별 스킬 슬롯을 만듭니다.
/// Google Sheets `클래스`·`스킬`·`패시브스킬` 탭을 대신합니다. 배틀 동작(쿨다운·효과·파생 등)은 Google Sheets 배틀* 시트에 둡니다.
/// 공개된 전직 클래스(ShowClass=true)의 Slot01~06·PassiveSlot01~06에 들어 있는 스킬만 담습니다.
/// </summary>
public sealed class UnpackBattleGameDataset : IUnpackDataset<BattleGameData>
{
    public string Name => "배틀 스킬 원본";
    public string CacheName => "battle-game";

    public async Task<BattleGameData> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var classes = new List<(int Order, BattleGameClass Data)>();
        using (var combatJob = await sheets("CombatJob", ct))
        {
            foreach (var row in UnpackSheets.Rows(combatJob))
            {
                if (UnpackSheets.Text(row, "CombatJobType") != "FirstAdvanceCombatJob") continue;
                if (!row.TryGetProperty("ShowClass", out var show) || show.ValueKind != JsonValueKind.True) continue;
                if (UnpackSheets.Reference(row, "Id") is not { } id) continue;
                var name = UnpackSheets.Text(row, "DisplayName").Trim();
                if (name.Length == 0) throw new InvalidDataException($"클래스 {id}의 이름이 비어 있습니다.");
                classes.Add(((int)(UnpackSheets.Number(row, "Order") ?? 0), new BattleGameClass(id, name)));
            }
        }
        if (classes.Count == 0) throw new InvalidDataException("CombatJob 시트에 공개된 전직 클래스가 없습니다.");
        var jobIds = classes.Select(x => x.Data.CombatJobId).ToHashSet(StringComparer.Ordinal);

        var slots = new Dictionary<string, List<BattleGameSlot>>(StringComparer.Ordinal);
        using (var skillSlot = await sheets("SkillSlot", ct))
        {
            foreach (var row in UnpackSheets.Rows(skillSlot))
            {
                if (UnpackSheets.Reference(row, "CombatJobId") is not { } job || !jobIds.Contains(job)) continue;
                if (UnpackSheets.Reference(row, "SkillId") is not { } skillId) continue;
                var slot = UnpackSheets.Text(row, "SlotIndex");
                if (!IsSkillSlot(slot)) continue;
                if (!slots.TryGetValue(skillId, out var list)) slots[skillId] = list = [];
                var entry = new BattleGameSlot(job, slot);
                if (!list.Contains(entry)) list.Add(entry);
            }
        }
        if (slots.Count == 0) throw new InvalidDataException("SkillSlot 시트에 공개 클래스의 스킬 슬롯이 없습니다.");

        var skills = new List<BattleGameSkill>();
        using (var skill = await sheets("Skill", ct))
        {
            foreach (var row in UnpackSheets.Rows(skill))
            {
                if (UnpackSheets.Reference(row, "Id") is not { } id || !slots.TryGetValue(id, out var skillSlots)) continue;
                var name = UnpackSheets.Text(row, "DisplayName").Trim();
                if (name.Length == 0) continue;
                skills.Add(new BattleGameSkill(id, name, skillSlots));
            }
        }
        if (skills.Count == 0) throw new InvalidDataException("Skill 시트에 공개 클래스의 스킬이 없습니다.");
        return new BattleGameData(classes.OrderBy(x => x.Order).ThenBy(x => x.Data.CombatJobId, StringComparer.Ordinal).Select(x => x.Data), skills);
    }

    /// <summary>Slot01~06·PassiveSlot01~06만 씁니다. 기본 공격·변신 등 시스템 슬롯은 배틀 스킬 구성에 들어가지 않습니다.</summary>
    public static bool IsSkillSlot(string slot)
        => slot.Length == 6 && slot.StartsWith("Slot0", StringComparison.Ordinal) && slot[5] is >= '1' and <= '6'
           || slot.Length == 13 && slot.StartsWith("PassiveSlot0", StringComparison.Ordinal) && slot[12] is >= '1' and <= '6';

    public void Write(Utf8JsonWriter writer, BattleGameData data)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("classes");
        foreach (var item in data.Classes)
        {
            writer.WriteStartObject();
            writer.WriteString("job", item.CombatJobId);
            writer.WriteString("name", item.Name);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("skills");
        foreach (var item in data.Skills.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            writer.WriteString("name", item.Name);
            writer.WriteStartArray("slots");
            foreach (var slot in item.Slots) writer.WriteStringValue(slot.CombatJobId + "/" + slot.Slot);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public BattleGameData Read(JsonElement data)
    {
        var classes = data.GetProperty("classes").EnumerateArray().Select(x => new BattleGameClass(x.GetProperty("job").GetString()!, x.GetProperty("name").GetString()!)).ToArray();
        var skills = data.GetProperty("skills").EnumerateArray().Select(x => new BattleGameSkill(x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!,
            x.GetProperty("slots").EnumerateArray().Select(s => s.GetString()!.Split('/', 2)).Select(s => new BattleGameSlot(s[0], s[1])).ToArray())).ToArray();
        if (classes.Length == 0 || skills.Length == 0) throw new InvalidDataException("캐시에 배틀 스킬 원본이 없습니다.");
        return new BattleGameData(classes, skills);
    }

    public string Describe(BattleGameData data) => $"클래스 {data.Classes.Count}개, 스킬 {data.Skills.Count}개";
}
