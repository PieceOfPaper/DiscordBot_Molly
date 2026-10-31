using System.Globalization;
using System.Text.Json;
using Molly.Unpack;

namespace Molly.MagicResistance;

/// <summary>
/// 게임 데이터 저장소의 시트로 마도저항 기준값을 만듭니다(관계는 저장소 Docs/SheetRelations.md).
/// - 어비스: AbyssDungeon의 RequiredArcaneResistance(필요 마도저항)·ArcanePressure(마도 압력),
///   AbyssDungeonGroup.Season으로 시즌, AbyssDungeonDifficulty의 HellStep·DisplayName으로 지옥 단계와 이름을 정합니다.
///   기준값이 있는 가장 최근 시즌의 지옥 난이도만 담고, 같은 시즌의 던전끼리 값이 다르면 검증 실패로 봅니다.
/// - 해연 장비: Equipment에서 이름이 '해연의 '로 시작하는 장비의 EquipmentArcaneResistance를 따라가
///   EquipmentArcaneResistance의 MinValue(최초 수치)·MaxValue(최대 수치)를 부위별로 담습니다.
/// </summary>
public sealed class UnpackMagicResistanceDataset : IUnpackDataset<MagicResistanceData>
{
    public const string HaeyeonPrefix = "해연의 ";
    private const string HellPrefix = "Hell_";
    private const string SeasonPrefix = "Abyss_Season_";

    public string Name => "마도저항";
    public string CacheName => "magic-resistance";

    public async Task<MagicResistanceData> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var (season, hell) = await ReadAbyssAsync(sheets, ct);
        var haeyeon = await ReadHaeyeonAsync(sheets, ct);
        return new MagicResistanceData(season, hell, haeyeon);
    }

    private static async Task<(int Season, IReadOnlyList<MagicPressureContent> Hell)> ReadAbyssAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var seasons = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var group = await sheets("AbyssDungeonGroup", ct))
        {
            foreach (var row in UnpackSheets.Rows(group))
            {
                var text = UnpackSheets.Text(row, "Season");
                if (UnpackSheets.Reference(row, "Id") is { } id && text.StartsWith(SeasonPrefix, StringComparison.Ordinal) &&
                    int.TryParse(text.AsSpan(SeasonPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var season))
                    seasons[id] = season;
            }
        }

        var difficulties = new Dictionary<string, (int Step, string Name)>(StringComparer.Ordinal);
        using (var difficulty = await sheets("AbyssDungeonDifficulty", ct))
        {
            foreach (var row in UnpackSheets.Rows(difficulty))
                if (UnpackSheets.Reference(row, "Id") is { } id && id.StartsWith(HellPrefix, StringComparison.Ordinal) &&
                    UnpackSheets.Number(row, "HellStep") is { } step && step > 0)
                    difficulties[id] = ((int)step, UnpackSheets.Text(row, "DisplayName").Trim());
        }

        // 시즌 → 지옥 난이도 → (필요 마도저항, 마도 압력, 처음 본 던전)
        var values = new Dictionary<int, Dictionary<string, (int Required, int Pressure, string Dungeon)>>();
        using (var dungeon = await sheets("AbyssDungeon", ct))
        {
            foreach (var row in UnpackSheets.Rows(dungeon))
            {
                if (UnpackSheets.Reference(row, "Id") is not { } id || UnpackSheets.Reference(row, "Difficulty") is not { } difficultyId ||
                    !difficulties.ContainsKey(difficultyId) || UnpackSheets.Reference(row, "Group") is not { } groupId ||
                    !seasons.TryGetValue(groupId, out var season))
                    continue;
                var required = (int)(UnpackSheets.Number(row, "RequiredArcaneResistance") ?? 0);
                var pressure = (int)(UnpackSheets.Number(row, "ArcanePressure") ?? 0);
                if (required <= 0 && pressure <= 0) continue;
                if (!values.TryGetValue(season, out var bySeason)) values[season] = bySeason = new(StringComparer.Ordinal);
                if (bySeason.TryGetValue(difficultyId, out var existing))
                {
                    if (existing.Required != required || existing.Pressure != pressure)
                        throw new InvalidDataException($"어비스 시즌 {season} {difficultyId}의 마도저항 기준이 던전마다 다릅니다" +
                                                       $"({existing.Dungeon} {existing.Required}/{existing.Pressure}, {id} {required}/{pressure}).");
                }
                else bySeason[difficultyId] = (required, pressure, id);
            }
        }
        if (values.Count == 0) throw new InvalidDataException("AbyssDungeon 시트에 마도저항 기준이 있는 지옥 난이도가 없습니다.");

        var latest = values.Keys.Max();
        var hell = values[latest]
            .OrderBy(x => difficulties[x.Key].Step)
            .Select(x => new MagicPressureContent(difficulties[x.Key].Name is { Length: > 0 } name ? name : $"지옥{difficulties[x.Key].Step}", x.Value.Required, x.Value.Pressure))
            .ToArray();
        if (hell.Any(x => x.RequiredResistance < 0 || x.Pressure <= 0))
            throw new InvalidDataException($"어비스 시즌 {latest}의 마도 압력이 비어 있는 난이도가 있습니다.");
        return (latest, hell);
    }

    private static async Task<IReadOnlyDictionary<ArcaneEquipmentPart, EquipmentResistanceRange>> ReadHaeyeonAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var references = new Dictionary<ArcaneEquipmentPart, (string Resistance, string Equipment)>();
        using (var equipment = await sheets("Equipment", ct))
        {
            foreach (var row in UnpackSheets.Rows(equipment))
            {
                if (!UnpackSheets.Text(row, "DisplayName").StartsWith(HaeyeonPrefix, StringComparison.Ordinal)) continue;
                if (PartOf(UnpackSheets.Text(row, "EquipmentType")) is not { } part) continue;
                var id = UnpackSheets.Reference(row, "Id") ?? "";
                if (UnpackSheets.Reference(row, "EquipmentArcaneResistance") is not { } resistance)
                    throw new InvalidDataException($"해연 장비 {id}에 마도저항 정보가 없습니다.");
                if (references.TryGetValue(part, out var existing))
                {
                    if (existing.Resistance != resistance)
                        throw new InvalidDataException($"해연 {part} 장비의 마도저항 기준이 서로 다릅니다({existing.Equipment} {existing.Resistance}, {id} {resistance}).");
                }
                else references[part] = (resistance, id);
            }
        }
        foreach (var part in Enum.GetValues<ArcaneEquipmentPart>())
            if (!references.ContainsKey(part)) throw new InvalidDataException($"해연 {part} 장비를 Equipment 시트에서 찾지 못했습니다.");

        var ranges = new Dictionary<string, EquipmentResistanceRange>(StringComparer.Ordinal);
        var wanted = references.Values.Select(x => x.Resistance).ToHashSet(StringComparer.Ordinal);
        using (var resistance = await sheets("EquipmentArcaneResistance", ct))
        {
            foreach (var row in UnpackSheets.Rows(resistance))
                if (UnpackSheets.Reference(row, "Id") is { } id && wanted.Contains(id))
                    ranges[id] = new EquipmentResistanceRange((int)(UnpackSheets.Number(row, "MinValue") ?? 0), (int)(UnpackSheets.Number(row, "MaxValue") ?? 0));
        }

        var result = new Dictionary<ArcaneEquipmentPart, EquipmentResistanceRange>();
        foreach (var (part, reference) in references)
        {
            if (!ranges.TryGetValue(reference.Resistance, out var range))
                throw new InvalidDataException($"EquipmentArcaneResistance 시트에 {reference.Resistance}가 없습니다.");
            if (range.Initial <= 0 || range.Max < range.Initial)
                throw new InvalidDataException($"{reference.Resistance}의 마도저항 범위가 올바르지 않습니다({range.Initial}~{range.Max}).");
            result[part] = range;
        }
        return result;
    }

    // Equipment.EquipmentType → 마도저항 부위 묶음. 그 밖의 종류(엠블럼 등)는 담지 않습니다.
    private static ArcaneEquipmentPart? PartOf(string equipmentType) => equipmentType switch
    {
        "Weapon" => ArcaneEquipmentPart.Weapon,
        "Hat" or "Top" or "Bottom" or "Gloves" or "Shoes" => ArcaneEquipmentPart.Armor,
        "Accessory" => ArcaneEquipmentPart.Necklace,
        "Ring" => ArcaneEquipmentPart.Ring,
        _ => null,
    };

    public void Write(Utf8JsonWriter writer, MagicResistanceData data)
    {
        writer.WriteStartObject();
        writer.WriteNumber("abyssSeason", data.AbyssSeason);
        writer.WriteStartArray("abyssHell");
        foreach (var content in data.AbyssHell)
        {
            writer.WriteStartObject();
            writer.WriteString("name", content.Name);
            writer.WriteNumber("required", content.RequiredResistance);
            writer.WriteNumber("pressure", content.Pressure);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("haeyeon");
        foreach (var (part, range) in data.HaeyeonEquipment.OrderBy(x => x.Key))
        {
            writer.WriteStartObject(part.ToString());
            writer.WriteNumber("initial", range.Initial);
            writer.WriteNumber("max", range.Max);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    public MagicResistanceData Read(JsonElement data)
    {
        var hell = data.GetProperty("abyssHell").EnumerateArray()
            .Select(x => new MagicPressureContent(x.GetProperty("name").GetString()!, x.GetProperty("required").GetInt32(), x.GetProperty("pressure").GetInt32()))
            .ToArray();
        var haeyeon = data.GetProperty("haeyeon").EnumerateObject().ToDictionary(
            x => Enum.Parse<ArcaneEquipmentPart>(x.Name),
            x => new EquipmentResistanceRange(x.Value.GetProperty("initial").GetInt32(), x.Value.GetProperty("max").GetInt32()));
        if (hell.Length == 0) throw new InvalidDataException("캐시에 어비스 마도저항 기준이 없습니다.");
        if (Enum.GetValues<ArcaneEquipmentPart>().Any(x => !haeyeon.ContainsKey(x))) throw new InvalidDataException("캐시에 해연 장비 마도저항이 빠져 있습니다.");
        return new MagicResistanceData(data.GetProperty("abyssSeason").GetInt32(), hell, haeyeon);
    }

    public string Describe(MagicResistanceData data) =>
        $"{data.AbyssName} 지옥 {data.AbyssHell.Count}단계, 해연 장비 " +
        string.Join(" · ", data.HaeyeonEquipment.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value.Initial}~{x.Value.Max}"));
}
