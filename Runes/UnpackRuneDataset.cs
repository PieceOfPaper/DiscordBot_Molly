using System.Globalization;
using System.Text.Json;
using Molly.Unpack;

namespace Molly.Runes;

/// <summary>
/// 게임 데이터 저장소의 시트로 룬 목록을 만듭니다(관계는 저장소 Docs/SheetRelations.md의 Rune 항목).
/// - Rune: 등급(Rarity)·시즌(Season)·부위(RuneAttachableType)·전용 클래스(CombatJobSheetId)·옵션(Option1~4)
/// - Socketable.DisplayName: 이름('무기 룬: 눈부신 잔영') / CombatJob.DisplayName: 클래스 이름
/// - RuneOption.Description: 효과 문장. 수치는 MagicEffect·Buff·SkillEffect 값으로 채웁니다(RuneEffectText).
///   MagicEffect 참조 Id가 없으면 게임 클라이언트처럼 RuneOption.MagicEffectList[0]을 대신 씁니다.
/// 전설·신화 등급만 담고, 같은 룬의 이전 버전(IsOriginal)은 제외합니다.
/// </summary>
public sealed class UnpackRuneDataset : IUnpackDataset<RuneTable>
{
    // 효과의 수치를 찾지 못한 룬이 이 비율을 넘으면 시트 구조가 바뀐 것으로 보고 교체하지 않습니다.
    public const double MaxUnresolvedRatio = 0.1;

    public string Name => "룬";
    public string CacheName => "runes";

    private sealed record Candidate(string Id, int Season, RuneGrade Grade, RuneCategory Category, string? CombatJob, IReadOnlyList<string> Options);

    public async Task<RuneTable> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var candidates = new List<Candidate>();
        using (var rune = await sheets("Rune", ct))
        {
            foreach (var row in UnpackSheets.Rows(rune))
            {
                if (row.TryGetProperty("IsOriginal", out var original) && original.ValueKind == JsonValueKind.True) continue;
                if (UnpackSheets.Reference(row, "Id") is not { } id) continue;
                var grade = UnpackSheets.Text(row, "Rarity") switch { "Legendary" => RuneGrade.Legendary, "Mythic" => (RuneGrade?)RuneGrade.Mythic, _ => null };
                if (grade is null) continue;
                var category = UnpackSheets.Text(row, "RuneAttachableType") switch
                {
                    "Weapon" => RuneCategory.Weapon,
                    "Armor" => RuneCategory.Armor,
                    "Emblem" => RuneCategory.Emblem,
                    "AccessoryAndRing" => (RuneCategory?)RuneCategory.Accessory,
                    _ => null,
                };
                var seasonText = UnpackSheets.Text(row, "Season");
                if (category is null || !seasonText.StartsWith("Season", StringComparison.Ordinal) ||
                    !int.TryParse(seasonText.AsSpan("Season".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var season))
                    continue;
                var options = new List<string>();
                foreach (var field in (string[])["Option1", "Option2", "Option3", "Option4"])
                    if (row.TryGetProperty(field, out var option) && UnpackSheets.Reference(option, "RuneOptionSheetId") is { } optionId)
                        options.Add(optionId);
                candidates.Add(new Candidate(id, season, grade.Value, category.Value, UnpackSheets.Reference(row, "CombatJobSheetId"), options));
            }
        }
        if (candidates.Count == 0) throw new InvalidDataException("Rune 시트에 전설·신화 룬이 없습니다.");
        var candidateIds = candidates.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var socketable = await sheets("Socketable", ct))
        {
            foreach (var row in UnpackSheets.Rows(socketable))
                if (UnpackSheets.Reference(row, "RuneSheetId") is { } runeId && candidateIds.Contains(runeId))
                    names.TryAdd(runeId, StripPrefix(UnpackSheets.Text(row, "DisplayName")));
        }

        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var combatJob = await sheets("CombatJob", ct))
        {
            foreach (var row in UnpackSheets.Rows(combatJob))
                if (UnpackSheets.Reference(row, "Id") is { } id)
                    classes[id] = UnpackSheets.Text(row, "DisplayName").Trim();
        }

        var optionIds = candidates.SelectMany(x => x.Options).ToHashSet(StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var fallbacks = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var runeOption = await sheets("RuneOption", ct))
        {
            foreach (var row in UnpackSheets.Rows(runeOption))
                if (UnpackSheets.Reference(row, "Id") is { } id && optionIds.Contains(id))
                {
                    descriptions[id] = UnpackSheets.Text(row, "Description");
                    if (row.TryGetProperty("MagicEffectList", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 &&
                        list[0].ValueKind == JsonValueKind.String && list[0].GetString() is { Length: > 0 } first)
                        fallbacks[id] = first;
                }
        }

        // 큰 시트(수 MB)는 설명이 참조하는 행만 남깁니다.
        var references = descriptions.Values.SelectMany(RuneEffectText.References)
            .Concat(fallbacks.Values.Select(id => (Sheet: "MagicEffect", Id: id)))
            .ToLookup(x => x.Sheet, x => x.Id);
        var values = new RuneTooltipValues();
        await ReadReferencedAsync(sheets, "MagicEffect", references, row => values.MagicEffects[UnpackSheets.Reference(row, "Id")!] = ReadMagicEffect(row), ct);
        await ReadReferencedAsync(sheets, "Buff", references, row => values.Buffs[UnpackSheets.Reference(row, "Id")!] = new RuneBuffValues(
            UnpackSheets.Number(row, "Duration") ?? 0, UnpackSheets.Number(row, "MaxStackCount") ?? 0, UnpackSheets.Number(row, "ShieldValue") ?? 0), ct);
        await ReadReferencedAsync(sheets, "SkillEffect", references, row => values.SkillEffects[UnpackSheets.Reference(row, "Id")!] = ReadSkillEffect(row), ct);

        var items = new List<RuneData>();
        var warnings = new List<string>();
        var keys = new HashSet<(int, RuneGrade, RuneCategory, string)>();
        var unresolvedRunes = 0;
        foreach (var candidate in candidates)
        {
            if (!names.TryGetValue(candidate.Id, out var name) || name.Length == 0)
            {
                warnings.Add($"{candidate.Id}: 이름(Socketable)이 없어 제외");
                continue;
            }
            var unresolved = 0;
            var effect = string.Join("\n", candidate.Options
                .Select(id => (Description: descriptions.TryGetValue(id, out var description) ? description : "", Fallback: fallbacks.GetValueOrDefault(id)))
                .Where(option => option.Description.Length > 0)
                .Select(option =>
                {
                    var text = RuneEffectText.Render(option.Description, values, out var missing, option.Fallback);
                    unresolved += missing;
                    return text;
                })
                .Where(text => text.Length > 0));
            if (effect.Length == 0)
            {
                warnings.Add($"{name}({candidate.Id}): 효과 설명이 없어 제외");
                continue;
            }
            if (!keys.Add((candidate.Season, candidate.Grade, candidate.Category, name)))
            {
                warnings.Add($"{name}({candidate.Id}): 같은 시즌·등급·분류·이름이 이미 있어 제외");
                continue;
            }
            if (unresolved > 0)
            {
                unresolvedRunes++;
                warnings.Add($"{name}({candidate.Id}): 수치 {unresolved}개를 찾지 못해 '{RuneEffectText.Unknown}'로 표시");
            }
            var className = candidate.CombatJob is { } job && classes.TryGetValue(job, out var display) ? display : "";
            items.Add(new RuneData(candidate.Id, candidate.Season, candidate.Grade, candidate.Category, className, name, effect));
        }

        foreach (var category in Enum.GetValues<RuneCategory>())
            if (items.All(x => x.Category != category))
                throw new InvalidDataException($"{category.Display()} 룬이 하나도 없습니다.");
        if (unresolvedRunes > items.Count * MaxUnresolvedRatio)
            throw new InvalidDataException($"효과 수치를 찾지 못한 룬이 {unresolvedRunes}/{items.Count}개로 많습니다. 시트 구조 변경을 확인하세요.");
        return new RuneTable(Sort(items), warnings);
    }

    // 최신 시즌부터, 같은 시즌에서는 부위·등급(신화 먼저)·이름 순.
    public static IEnumerable<RuneData> Sort(IEnumerable<RuneData> items) => items
        .OrderByDescending(x => x.Season).ThenBy(x => x.Category).ThenByDescending(x => x.Grade)
        .ThenBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal);

    // '무기 룬: 눈부신 잔영' → '눈부신 잔영'
    private static string StripPrefix(string displayName)
    {
        var separator = displayName.IndexOf(": ", StringComparison.Ordinal);
        return (separator >= 0 ? displayName[(separator + 2)..] : displayName).Trim();
    }

    private static async Task ReadReferencedAsync(UnpackSheetFetcher sheets, string sheet, ILookup<string, string> references, Action<JsonElement> add, CancellationToken ct)
    {
        var ids = references[sheet].ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return;
        using var document = await sheets(sheet, ct);
        foreach (var row in UnpackSheets.Rows(document))
            if (UnpackSheets.Reference(row, "Id") is { } id && ids.Contains(id))
                add(row);
    }

    // Action = ["ModifyParameter", ["AttackPowerMultiplier", "0.15"]]
    private static RuneMagicEffectValues ReadMagicEffect(JsonElement row)
    {
        var action = "";
        var args = new List<string>();
        if (row.TryGetProperty("Action", out var actionValue) && actionValue.ValueKind == JsonValueKind.Array)
        {
            var parts = actionValue.EnumerateArray().ToArray();
            if (parts.Length > 0 && parts[0].ValueKind == JsonValueKind.String) action = parts[0].GetString()!;
            if (parts.Length > 1 && parts[1].ValueKind == JsonValueKind.Array)
                args.AddRange(parts[1].EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()));
        }
        var activation = new List<string>();
        if (row.TryGetProperty("Activation", out var activationValue) && activationValue.ValueKind == JsonValueKind.Object)
            for (var i = 1; i <= 5; i++)
                activation.Add(UnpackSheets.Text(activationValue, $"Params{i}"));
        double cooldown = 0, successRate = 0;
        if (row.TryGetProperty("Execution", out var execution) && execution.ValueKind == JsonValueKind.Object)
        {
            cooldown = UnpackSheets.Number(execution, "Cooldown") ?? 0;
            successRate = UnpackSheets.Number(execution, "SuccessRate") ?? 0;
        }
        return new RuneMagicEffectValues(action, args, cooldown, successRate, activation);
    }

    // Effect1~5 = {"probability", "coefficient", "value", "reserved1", "reserved2", ...}
    private static IReadOnlyList<RuneSkillEffectSlot> ReadSkillEffect(JsonElement row)
    {
        var slots = new List<RuneSkillEffectSlot>();
        for (var i = 1; i <= 5; i++)
        {
            if (!row.TryGetProperty($"Effect{i}", out var effect) || effect.ValueKind != JsonValueKind.Object) break;
            slots.Add(new RuneSkillEffectSlot(
                UnpackSheets.Number(effect, "probability") ?? 0, UnpackSheets.Number(effect, "coefficient") ?? 0, UnpackSheets.Number(effect, "value") ?? 0,
                UnpackSheets.Number(effect, "reserved1") ?? 0, UnpackSheets.Number(effect, "reserved2") ?? 0));
        }
        return slots;
    }

    public void Write(Utf8JsonWriter writer, RuneTable data)
    {
        writer.WriteStartArray();
        foreach (var rune in data.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("id", rune.Id);
            writer.WriteNumber("season", rune.Season);
            writer.WriteString("grade", rune.Grade.ToString());
            writer.WriteString("category", rune.Category.ToString());
            writer.WriteString("class", rune.Class);
            writer.WriteString("name", rune.Name);
            writer.WriteString("effect", rune.Effect);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public RuneTable Read(JsonElement data)
    {
        var items = data.EnumerateArray().Select(x => new RuneData(
            x.GetProperty("id").GetString()!,
            x.GetProperty("season").GetInt32(),
            Enum.Parse<RuneGrade>(x.GetProperty("grade").GetString()!),
            Enum.Parse<RuneCategory>(x.GetProperty("category").GetString()!),
            x.GetProperty("class").GetString()!,
            x.GetProperty("name").GetString()!,
            x.GetProperty("effect").GetString()!)).ToArray();
        if (items.Length == 0) throw new InvalidDataException("캐시에 룬이 없습니다.");
        return new RuneTable(items);
    }

    public string Describe(RuneTable data) =>
        $"룬 {data.Items.Count}개" + (data.Warnings.Count == 0 ? "" : $", 참고 {data.Warnings.Count}건: {string.Join(" / ", data.Warnings.Take(5))}" +
                                      (data.Warnings.Count > 5 ? " …" : ""));
}
