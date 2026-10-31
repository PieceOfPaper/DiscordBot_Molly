using System.Text.Json;
using Molly.Unpack;

namespace Molly.World;

/// <param name="Id">게임 데이터의 GameSpace.Id(예: Dunbarton).</param>
/// <param name="Name">표시 이름(예: 던바튼).</param>
/// <param name="Category">마을 또는 사냥터.</param>
public sealed record RegionData(string Id, string Name, string Category);

/// <param name="Id">같은 이름의 NPC가 여럿이면 Id가 가장 앞선 NPC.Id.</param>
/// <param name="Regions">배치된 지역 표시 이름. 지역 목록 순서입니다.</param>
/// <param name="ProfileImage">프로필 이미지 에셋 경로(예: Sprites/NPCProfile/Abenzio.png). 없거나 기본 실루엣이면 null.</param>
public sealed record NpcData(string Id, string Name, IReadOnlyList<string> Regions, string? ProfileImage);

public sealed class WorldTable(IEnumerable<RegionData> regions, IEnumerable<NpcData> npcs)
{
    public IReadOnlyList<RegionData> Regions { get; } = Array.AsReadOnly(regions.ToArray());
    public IReadOnlyList<NpcData> Npcs { get; } = Array.AsReadOnly(npcs.ToArray());
}

/// <summary>
/// 게임에 열린 지역과 그 지역에 배치된 NPC 목록을 게임 데이터 저장소에서 만듭니다. `/자음퀴즈`(NPC·지역)와 `/이미지퀴즈`(NPC)가 씁니다.
/// - 지역: 지도 빠른 이동(QuickNavigatorShortcut)이 있는 채널의 GameSpace. 채널 규칙이 Field면 마을, BattleField면 사냥터입니다.
///   개발 중인 지역은 빠른 이동이 없어 자연히 빠집니다.
/// - NPC: 지역 월드 배치 파일(WorldData/&lt;월드&gt;/NPCDeploy)에 있는 NPC 중 칭호(Title)가 있는 NPC만 담습니다.
///   칭호가 `-`인 게시판·보관함·이름 없는 엑스트라와, 이벤트·퀘스트로만 나오는 NPC는 빠집니다. 같은 이름은 하나로 합칩니다.
/// </summary>
public sealed class UnpackWorldDataset : IUnpackDataset<WorldTable>
{
    public const string WorldDataDirectory = "Patch/ResourcesPackage/Starter/WorldData";
    // 프로필 이미지가 없는 NPC가 쓰는 기본 실루엣. 이미지 문제로 내지 않습니다.
    public const string SilhouetteImage = "Sprites/NPCProfile/Extra.png";

    public string Name => "NPC·지역";
    public string CacheName => "world";

    public static string NpcDeployPath(string world) => $"{WorldDataDirectory}/{world}/NPCDeploy.sjon.json";

    public Task<WorldTable> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct) =>
        throw new InvalidOperationException("NPC·지역 데이터는 월드 배치 파일도 함께 받아야 합니다.");

    public async Task<WorldTable> BuildAsync(UnpackSheetFetcher sheets, UnpackFileFetcher files, CancellationToken ct)
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);
        using (var shortcuts = await sheets("QuickNavigatorShortcut", ct))
        {
            foreach (var row in UnpackSheets.Rows(shortcuts))
                if (UnpackSheets.Reference(row, "ChannelSheetData") is { } channel) channels.Add(channel);
        }

        // GameSpace → 채널 규칙. 빠른 이동이 있는 채널의 GameSpace만 지역 후보입니다.
        var rules = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using (var channelSheet = await sheets("Channel", ct))
        {
            var spaces = new HashSet<string>(StringComparer.Ordinal);
            var rows = UnpackSheets.Rows(channelSheet).ToArray();
            foreach (var row in rows)
                if (UnpackSheets.Reference(row, "Id") is { } id && channels.Contains(id) && UnpackSheets.Reference(row, "GameSpace") is { } space)
                    spaces.Add(space);
            foreach (var row in rows)
            {
                if (UnpackSheets.Reference(row, "GameSpace") is not { } space || !spaces.Contains(space)) continue;
                if (!rules.TryGetValue(space, out var set)) rules[space] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(UnpackSheets.Text(row, "ChannelRule"));
            }
        }

        var regions = new List<(RegionData Region, string World)>();
        using (var gameSpaces = await sheets("GameSpace", ct))
        {
            foreach (var row in UnpackSheets.Rows(gameSpaces))
            {
                if (UnpackSheets.Reference(row, "Id") is not { } id || !rules.TryGetValue(id, out var set)) continue;
                var town = set.Contains("Field");
                var field = set.Contains("BattleField");
                if (town && field) throw new InvalidDataException($"지역 {id}의 채널이 마을(Field)과 사냥터(BattleField)에 모두 속합니다.");
                // 빠른 이동이 있어도 마을·사냥터가 아닌 공간(미니게임 등)은 지역으로 내지 않습니다.
                if (!town && !field) continue;
                var name = UnpackSheets.Text(row, "DisplayName").Trim();
                if (name.Length == 0) throw new InvalidDataException($"지역 {id}의 이름이 비어 있습니다.");
                var world = row.TryGetProperty("WorldDataKey", out var key) && key.ValueKind == JsonValueKind.Object
                    ? UnpackSheets.Reference(key, "pstr") : null;
                if (world is null) throw new InvalidDataException($"지역 {id}의 WorldDataKey가 없습니다.");
                regions.Add((new RegionData(id, name, town ? "마을" : "사냥터"), world));
            }
        }
        regions.Sort((a, b) => string.CompareOrdinal(a.Region.Id, b.Region.Id));
        if (regions.Count == 0) throw new InvalidDataException("빠른 이동이 있는 마을·사냥터가 하나도 없습니다.");
        if (regions.Select(x => x.Region.Name).Distinct(StringComparer.Ordinal).Count() != regions.Count)
            throw new InvalidDataException("지역 이름이 중복됩니다.");

        // NPC.Id → 배치된 지역 이름(지역 순서).
        var placed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (region, world) in regions)
        {
            foreach (var npc in await ReadDeployAsync(files, world, ct))
            {
                if (!placed.TryGetValue(npc, out var list)) placed[npc] = list = [];
                if (!list.Contains(region.Name)) list.Add(region.Name);
            }
        }

        var byName = new Dictionary<string, (string Id, List<string> Regions, string? Image)>(StringComparer.Ordinal);
        using (var npcSheet = await sheets("NPC", ct))
        {
            var rows = UnpackSheets.Rows(npcSheet)
                .Select(row => (Id: UnpackSheets.Reference(row, "Id"), Row: row))
                .Where(x => x.Id is not null && placed.ContainsKey(x.Id))
                .OrderBy(x => x.Id, StringComparer.Ordinal);
            foreach (var (id, row) in rows)
            {
                var name = UnpackSheets.Text(row, "DisplayName").Trim();
                var title = UnpackSheets.Text(row, "Title").Trim();
                if (name.Length == 0 || title is "" or "-") continue;
                var image = UnpackSheets.Text(row, "ProfileImage").Trim();
                var profile = image.Length == 0 || image == SilhouetteImage ? null : image;
                var regionNames = placed[id!];
                if (byName.TryGetValue(name, out var existing))
                {
                    foreach (var regionName in regionNames.Where(x => !existing.Regions.Contains(x))) existing.Regions.Add(regionName);
                    byName[name] = (existing.Id, existing.Regions, existing.Image ?? profile);
                }
                else byName[name] = (id!, [.. regionNames], profile);
            }
        }
        if (byName.Count == 0) throw new InvalidDataException("지역에 배치된 NPC가 하나도 없습니다.");

        var regionOrder = regions.Select((x, i) => (x.Region.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        var npcs = byName
            .Select(x => new NpcData(x.Value.Id, x.Key, x.Value.Regions.OrderBy(r => regionOrder[r]).ToArray(), x.Value.Image))
            .OrderBy(x => x.Id, StringComparer.Ordinal);
        return new WorldTable(regions.Select(x => x.Region), npcs);
    }

    // 배치 파일의 SpawnGroups[].Spawns[].NPC는 [해시, "NPC.Id"] 형식입니다. 파일이 없는 월드는 배치된 NPC가 없는 것으로 봅니다.
    private static async Task<IReadOnlyList<string>> ReadDeployAsync(UnpackFileFetcher files, string world, CancellationToken ct)
    {
        JsonDocument document;
        try { document = await files(NpcDeployPath(world), ct); }
        catch (UnpackDataException ex) when (ex.Status == UnpackRepoStatus.NotFound) { return []; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("SpawnGroups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{world} NPC 배치 파일 형식이 아닙니다(data.SpawnGroups 확인).");
            var result = new List<string>();
            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("Spawns", out var spawns) || spawns.ValueKind != JsonValueKind.Array) continue;
                foreach (var spawn in spawns.EnumerateArray())
                {
                    if (spawn.TryGetProperty("NPC", out var npc) && npc.ValueKind == JsonValueKind.Array && npc.GetArrayLength() >= 2 &&
                        npc[1].ValueKind == JsonValueKind.String && npc[1].GetString() is { Length: > 0 } id && id != "None")
                        result.Add(id);
                }
            }
            return result;
        }
    }

    public void Write(Utf8JsonWriter writer, WorldTable data)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("regions");
        foreach (var region in data.Regions)
        {
            writer.WriteStartObject();
            writer.WriteString("id", region.Id);
            writer.WriteString("name", region.Name);
            writer.WriteString("category", region.Category);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("npcs");
        foreach (var npc in data.Npcs)
        {
            writer.WriteStartObject();
            writer.WriteString("id", npc.Id);
            writer.WriteString("name", npc.Name);
            writer.WriteStartArray("regions");
            foreach (var region in npc.Regions) writer.WriteStringValue(region);
            writer.WriteEndArray();
            if (npc.ProfileImage is { } image) writer.WriteString("profileImage", image);
            else writer.WriteNull("profileImage");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public WorldTable Read(JsonElement data)
    {
        var regions = data.GetProperty("regions").EnumerateArray()
            .Select(x => new RegionData(x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!, x.GetProperty("category").GetString()!))
            .ToArray();
        var npcs = data.GetProperty("npcs").EnumerateArray()
            .Select(x => new NpcData(x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!,
                x.GetProperty("regions").EnumerateArray().Select(r => r.GetString()!).ToArray(),
                x.GetProperty("profileImage").ValueKind == JsonValueKind.String ? x.GetProperty("profileImage").GetString() : null))
            .ToArray();
        if (regions.Length == 0 || npcs.Length == 0) throw new InvalidDataException("캐시에 지역 또는 NPC가 없습니다.");
        return new WorldTable(regions, npcs);
    }

    public string Describe(WorldTable data) => $"지역 {data.Regions.Count}곳, NPC {data.Npcs.Count}명";
}
