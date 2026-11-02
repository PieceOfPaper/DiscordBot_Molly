using System.Globalization;
using System.Text.Json;
using Molly.Unpack;

namespace Molly.Shop;

/// <summary>판매 기간. 시작·끝이 없으면 그쪽으로 열려 있습니다.</summary>
public sealed record ShopPeriod(DateTimeOffset? Start, DateTimeOffset? End)
{
    public bool Contains(DateTimeOffset now) => (Start is null || Start <= now) && (End is null || now <= End);
}

/// <param name="Name">NPC 표시 이름. NPC가 없는 상점이면 상점 이름입니다.</param>
/// <param name="Events">이벤트 기간에만 열리는 상점의 이벤트 기간(하나라도 진행 중이면 열림). 비어 있으면 항상 열립니다.</param>
public sealed record ShopSeller(string Name, IReadOnlyList<ShopPeriod> Events)
{
    public bool IsOpen(DateTimeOffset now) => Events.Count == 0 || Events.Any(x => x.Contains(now));
}

/// <param name="Item">받는 아이템 이름.</param>
/// <param name="Amount">한 번 구매로 받는 개수.</param>
/// <param name="Price">한 번 구매 비용.</param>
/// <param name="Currency">비용 재화 이름(골드·하트 토큰 같은 화폐 또는 아이템).</param>
/// <param name="Barter">비용이 아이템이면 true(물물교환). 재화 이름으로도 검색됩니다.</param>
/// <param name="Restrict">구매 제한 단위: 서버·캐릭터. 제한이 없으면 빈 문자열입니다.</param>
/// <param name="Limit">구매 제한 횟수. 제한이 없으면 0입니다.</param>
/// <param name="Cycle">구매 제한 초기화 주기: 일간·주간. 초기화되지 않으면 빈 문자열입니다.</param>
/// <param name="Sellers">이 상품을 파는 상점(같은 탭을 여러 상점이 함께 쓰면 여럿).</param>
/// <param name="Periods">상품·탭의 판매 기간. 모두 포함해야 판매 중입니다.</param>
public sealed record ShopProduct(string Item, int Amount, int Price, string Currency, bool Barter, string Restrict, int Limit, string Cycle,
    IReadOnlyList<ShopSeller> Sellers, IReadOnlyList<ShopPeriod> Periods)
{
    public bool IsOnSale(DateTimeOffset now) => Periods.All(x => x.Contains(now)) && Sellers.Any(x => x.IsOpen(now));
}

public sealed class ShopTable(IEnumerable<ShopProduct> items)
{
    public IReadOnlyList<ShopProduct> Items { get; } = Array.AsReadOnly(items.ToArray());
}

/// <summary>
/// 게임 데이터 저장소의 NPC 상점 시트로 상품 목록을 만듭니다(관계는 저장소 Docs/SheetRelations.md). `/상점검색`이 씁니다.
/// - NPCShop(상점·NPC·탭) → NPCShopTab(초기화 주기·판매 기간) → NPCShopProduct(구매 제한·판매 기간) → NPCShopProductPreset(비용·받는 아이템).
/// - 어느 상점에도 걸리지 않은 탭(테스트 등), 무작위 상품, 이름을 찾을 수 없는 보상(코스튬 프리셋·악보 앨범 등)은 뺍니다.
/// - 이벤트 상점(Activate의 EventOnGoing)은 Event.UnlockCondition 기간에만 열립니다. 기간 판정은 조회할 때 합니다.
/// - 비용 재화는 CurrencyInfo가 가리키는 화폐(MoneyInfo)·Consumable·EventItem의 이름입니다.
/// </summary>
public sealed class UnpackShopDataset : IUnpackDataset<ShopTable>
{
    public const string AccountRestrict = "서버";
    public const string CharacterRestrict = "캐릭터";

    public string Name => "상점";
    public string CacheName => "shops";

    // NPCShopProductPreset.RewardData 첫 값(보상 타입) → 이름을 찾을 시트.
    private static readonly (string Type, string Sheet, string NameField)[] RewardSheets =
    [
        ("Consumable", "Consumable", "DisplayName"),
        ("Equipment", "Equipment", "DisplayName"),
        ("EquipmentTool", "EquipmentTool", "DisplayName"),
        ("Socketable", "Socketable", "DisplayName"),
        ("Bag", "Bag", "DisplayName"),
        ("MusicNoteItem", "MusicNote", "Title"),
    ];

    private sealed record RawProduct(string Tab, string Preset, string Restrict, int Limit, IReadOnlyList<ShopPeriod> Periods);
    private sealed record RawPreset(string Currency, int Price, string RewardType, string RewardId, int Amount);

    public async Task<ShopTable> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        // 상점: 탭 → 그 탭을 쓰는 상점(NPC Id 또는 상점 이름, 이벤트 Id 목록)
        var tabShops = new Dictionary<string, List<(string? Npc, string ShopName, IReadOnlyList<string> Events)>>(StringComparer.Ordinal);
        using (var shop = await sheets("NPCShop", ct))
        {
            foreach (var row in UnpackSheets.Rows(shop))
            {
                if (UnpackSheets.Reference(row, "Id") is null || !row.TryGetProperty("Tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array) continue;
                var entry = (UnpackSheets.Reference(row, "NPC"), UnpackSheets.Text(row, "DisplayName").Trim(), EventIds(row));
                foreach (var tab in tabs.EnumerateArray())
                    if (tab.ValueKind == JsonValueKind.String && tab.GetString() is { Length: > 0 } tabId)
                    {
                        if (!tabShops.TryGetValue(tabId, out var list)) tabShops[tabId] = list = [];
                        list.Add(entry);
                    }
            }
        }

        var tabInfo = new Dictionary<string, (string Cycle, IReadOnlyList<ShopPeriod> Periods)>(StringComparer.Ordinal);
        using (var tab = await sheets("NPCShopTab", ct))
        {
            foreach (var row in UnpackSheets.Rows(tab))
                if (UnpackSheets.Reference(row, "Id") is { } id && tabShops.ContainsKey(id))
                    tabInfo[id] = (UnpackSheets.Text(row, "AutoRestockCycle") switch { "Daily" => "일간", "Weekly" => "주간", _ => "" }, TimeRanges(row, id));
        }

        var products = new List<RawProduct>();
        using (var product = await sheets("NPCShopProduct", ct))
        {
            foreach (var row in UnpackSheets.Rows(product))
            {
                if (UnpackSheets.Reference(row, "Id") is not { } id || UnpackSheets.Reference(row, "ShopTabSheetId") is not { } tabId || !tabInfo.ContainsKey(tabId)) continue;
                // ProductData는 [프리셋 Id, 0] 또는 무작위 상품 [0, 무작위 그룹 Id]입니다. 무작위 상품은 담지 않습니다.
                if (!row.TryGetProperty("ProductData", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0 ||
                    data[0].ValueKind != JsonValueKind.String || data[0].GetString() is not { Length: > 0 } preset) continue;
                var restrict = UnpackSheets.Text(row, "BuyRestrictStandard") switch { "Account" => AccountRestrict, "Character" => CharacterRestrict, _ => "" };
                var limit = restrict.Length == 0 ? 0 : (int)(UnpackSheets.Number(row, "BuyRestrictAmount") ?? 0);
                products.Add(new RawProduct(tabId, preset, limit > 0 ? restrict : "", Math.Max(limit, 0), TimeRanges(row, id)));
            }
        }
        if (products.Count == 0) throw new InvalidDataException("NPCShopProduct 시트에 상점에서 파는 상품이 없습니다.");

        var wantedPresets = products.Select(x => x.Preset).ToHashSet(StringComparer.Ordinal);
        var presets = new Dictionary<string, RawPreset>(StringComparer.Ordinal);
        using (var preset = await sheets("NPCShopProductPreset", ct))
        {
            foreach (var row in UnpackSheets.Rows(preset))
            {
                if (UnpackSheets.Reference(row, "Id") is not { } id || !wantedPresets.Contains(id)) continue;
                if (!row.TryGetProperty("RewardData", out var reward) || reward.ValueKind != JsonValueKind.Array || reward.GetArrayLength() < 3 ||
                    reward[0].ValueKind != JsonValueKind.String || reward[1].ValueKind != JsonValueKind.String) continue;
                presets[id] = new RawPreset(UnpackSheets.Reference(row, "CurrencyInfo") ?? "", (int)(UnpackSheets.Number(row, "Price") ?? 0),
                    reward[0].GetString()!, reward[1].GetString()!, (int)(UnpackSheets.Number(reward[2]) ?? 0));
            }
        }

        // 비용 재화: CurrencyInfo.CurrencyData = [종류, 화폐(MoneyType), Consumable Id, EventItem Id, …]
        var currencies = new Dictionary<string, (string Kind, string Id)>(StringComparer.Ordinal);
        var wantedCurrencies = presets.Values.Select(x => x.Currency).ToHashSet(StringComparer.Ordinal);
        using (var currency = await sheets("CurrencyInfo", ct))
        {
            foreach (var row in UnpackSheets.Rows(currency))
            {
                if (UnpackSheets.Reference(row, "Id") is not { } id || !wantedCurrencies.Contains(id)) continue;
                if (!row.TryGetProperty("CurrencyData", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() < 4) continue;
                var kind = data[0].GetString() ?? "";
                var target = kind switch { "Money" => data[1], "Consumable" => data[2], "EventItem" => data[3], _ => default };
                if (target.ValueKind == JsonValueKind.String && target.GetString() is { Length: > 0 } targetId) currencies[id] = (kind, targetId);
            }
        }

        var money = await ReadNamesAsync(sheets, "MoneyInfo", "MoneyType", "DisplayName",
            currencies.Values.Where(x => x.Kind == "Money").Select(x => x.Id).Concat(presets.Values.Where(x => x.RewardType == "Money").Select(x => x.RewardId)), ct);
        var eventItems = await ReadNamesAsync(sheets, "EventItem", "Id", "DisplayName", currencies.Values.Where(x => x.Kind == "EventItem").Select(x => x.Id), ct);
        var rewards = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal) { ["Money"] = money };
        foreach (var (type, sheet, field) in RewardSheets)
        {
            var ids = presets.Values.Where(x => x.RewardType == type).Select(x => x.RewardId);
            if (type == "Consumable") ids = ids.Concat(currencies.Values.Where(x => x.Kind == "Consumable").Select(x => x.Id));
            rewards[type] = await ReadNamesAsync(sheets, sheet, "Id", field, ids, ct);
        }

        var npcs = await ReadNamesAsync(sheets, "NPC", "Id", "DisplayName",
            tabShops.Values.SelectMany(x => x).Select(x => x.Npc).OfType<string>(), ct);
        var events = await ReadEventPeriodsAsync(sheets, tabShops.Values.SelectMany(x => x).SelectMany(x => x.Events), ct);

        var sellers = new Dictionary<string, IReadOnlyList<ShopSeller>>(StringComparer.Ordinal);
        foreach (var (tabId, shops) in tabShops)
        {
            var list = new List<ShopSeller>();
            foreach (var (npc, shopName, eventIds) in shops)
            {
                var name = npc is not null && npcs.TryGetValue(npc, out var npcName) ? npcName : shopName;
                if (name.Length == 0) continue;
                // 이벤트 상점인데 이벤트 기간을 찾지 못하면 열리는 때를 알 수 없어 뺍니다.
                var periods = eventIds.Select(x => events.GetValueOrDefault(x)).OfType<ShopPeriod>().ToArray();
                if (eventIds.Count > 0 && periods.Length == 0) continue;
                if (list.All(x => x.Name != name || !x.Events.SequenceEqual(periods))) list.Add(new ShopSeller(name, periods));
            }
            sellers[tabId] = list;
        }

        var items = new List<ShopProduct>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in products)
        {
            if (!presets.TryGetValue(raw.Preset, out var preset) || preset.Amount <= 0 || preset.Price < 0) continue;
            if (!rewards.TryGetValue(preset.RewardType, out var names) || !names.TryGetValue(preset.RewardId, out var item) || item.Length == 0) continue;
            if (!currencies.TryGetValue(preset.Currency, out var currency)) continue;
            var currencyName = currency.Kind switch
            {
                "Money" => money.GetValueOrDefault(currency.Id),
                "Consumable" => rewards["Consumable"].GetValueOrDefault(currency.Id),
                _ => eventItems.GetValueOrDefault(currency.Id),
            };
            if (string.IsNullOrEmpty(currencyName) || sellers[raw.Tab] is not { Count: > 0 } tabSellers) continue;

            var (cycle, tabPeriods) = tabInfo[raw.Tab];
            var shopProduct = new ShopProduct(item, preset.Amount, preset.Price, currencyName, currency.Kind != "Money", raw.Restrict, raw.Limit,
                raw.Limit > 0 ? cycle : "", tabSellers, [.. tabPeriods, .. raw.Periods]);
            // 같은 탭에 구매 조건만 다른 같은 상품이 여러 행 있으면 한 번만 담습니다.
            if (seen.Add(Key(shopProduct))) items.Add(shopProduct);
        }
        if (items.Count == 0) throw new InvalidDataException("이름과 비용을 확인할 수 있는 상점 상품이 없습니다.");
        return new ShopTable(items);
    }

    private static string Key(ShopProduct x) => string.Join('\u001f', x.Item, x.Amount, x.Price, x.Currency, x.Restrict, x.Limit, x.Cycle,
        string.Join('\u001e', x.Sellers.Select(s => s.Name + string.Concat(s.Events.Select(Describe)))), string.Concat(x.Periods.Select(Describe)));

    private static string Describe(ShopPeriod x) => $"[{x.Start:O}~{x.End:O}]";

    // NPCShop.Activate의 EventOnGoing 조건이 가리키는 Event Id 목록.
    private static IReadOnlyList<string> EventIds(JsonElement row)
    {
        var ids = new List<string>();
        if (!row.TryGetProperty("Activate", out var activate) || !activate.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var condition in conditions.EnumerateArray())
            if (UnpackSheets.Text(condition, "_0") == "EventOnGoing")
                ids.AddRange(Values(condition));
        return ids;
    }

    // EnableRequirements의 TimeRange 조건: _1 = [[시간대], [시작], [끝]?].
    private static IReadOnlyList<ShopPeriod> TimeRanges(JsonElement row, string id)
    {
        var periods = new List<ShopPeriod>();
        if (!row.TryGetProperty("EnableRequirements", out var requirements) || requirements.ValueKind != JsonValueKind.Array) return periods;
        foreach (var requirement in requirements.EnumerateArray())
        {
            if (UnpackSheets.Text(requirement, "_0") != "TimeRange") continue;
            var values = Values(requirement);
            if (values.Count < 2) throw new InvalidDataException($"{id}의 판매 기간(TimeRange) 형식이 다릅니다.");
            periods.Add(new ShopPeriod(ParseTime(values[0], values[1], id), values.Count > 2 ? ParseTime(values[0], values[2], id) : null));
        }
        return periods;
    }

    private static List<string> Values(JsonElement condition)
    {
        var values = new List<string>();
        if (condition.TryGetProperty("_1", out var args) && args.ValueKind == JsonValueKind.Array)
            foreach (var arg in args.EnumerateArray())
            {
                var value = arg.ValueKind == JsonValueKind.Array && arg.GetArrayLength() > 0 ? arg[0] : arg;
                if (value.ValueKind == JsonValueKind.String) values.Add(value.GetString()!);
            }
        return values;
    }

    public static DateTimeOffset ParseTime(string zone, string text, string id)
    {
        var offset = zone switch
        {
            "KST" => TimeSpan.FromHours(9),
            "UTC" => TimeSpan.Zero,
            _ => throw new InvalidDataException($"{id}의 시간대 '{zone}'을(를) 지원하지 않습니다."),
        };
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new InvalidDataException($"{id}의 시각 '{text}' 형식이 다릅니다.");
        return new DateTimeOffset(time, offset);
    }

    // Event.UnlockCondition = [시간대, 시작, 끝, …].
    private static async Task<Dictionary<string, ShopPeriod>> ReadEventPeriodsAsync(UnpackSheetFetcher sheets, IEnumerable<string> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var periods = new Dictionary<string, ShopPeriod>(StringComparer.Ordinal);
        if (wanted.Count == 0) return periods;
        using var sheet = await sheets("Event", ct);
        foreach (var row in UnpackSheets.Rows(sheet))
        {
            if (UnpackSheets.Reference(row, "Id") is not { } id || !wanted.Contains(id)) continue;
            if (!row.TryGetProperty("UnlockCondition", out var unlock) || unlock.ValueKind != JsonValueKind.Array || unlock.GetArrayLength() < 3 ||
                unlock[0].GetString() is not { } zone || unlock[1].GetString() is not { } start || unlock[2].GetString() is not { } end) continue;
            periods[id] = new ShopPeriod(ParseTime(zone, start, id), ParseTime(zone, end, id));
        }
        return periods;
    }

    // 큰 시트(Consumable 약 13MB)는 필요한 행의 이름만 남기고 바로 버립니다.
    private static async Task<Dictionary<string, string>> ReadNamesAsync(UnpackSheetFetcher sheets, string sheet, string idField, string nameField,
        IEnumerable<string> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted.Count == 0) return names;
        using var document = await sheets(sheet, ct);
        foreach (var row in UnpackSheets.Rows(document))
            if (UnpackSheets.Reference(row, idField) is { } id && wanted.Contains(id))
                names[id] = UnpackSheets.Text(row, nameField).Trim();
        return names;
    }

    public void Write(Utf8JsonWriter writer, ShopTable data)
    {
        writer.WriteStartArray();
        foreach (var item in data.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("item", item.Item);
            writer.WriteNumber("amount", item.Amount);
            writer.WriteNumber("price", item.Price);
            writer.WriteString("currency", item.Currency);
            writer.WriteBoolean("barter", item.Barter);
            writer.WriteString("restrict", item.Restrict);
            writer.WriteNumber("limit", item.Limit);
            writer.WriteString("cycle", item.Cycle);
            writer.WriteStartArray("sellers");
            foreach (var seller in item.Sellers)
            {
                writer.WriteStartObject();
                writer.WriteString("name", seller.Name);
                WritePeriods(writer, "events", seller.Events);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WritePeriods(writer, "periods", item.Periods);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WritePeriods(Utf8JsonWriter writer, string name, IReadOnlyList<ShopPeriod> periods)
    {
        writer.WriteStartArray(name);
        foreach (var period in periods)
        {
            writer.WriteStartObject();
            if (period.Start is { } start) writer.WriteString("start", start);
            if (period.End is { } end) writer.WriteString("end", end);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public ShopTable Read(JsonElement data)
    {
        var items = data.EnumerateArray().Select(x => new ShopProduct(
            x.GetProperty("item").GetString()!, x.GetProperty("amount").GetInt32(), x.GetProperty("price").GetInt32(),
            x.GetProperty("currency").GetString()!, x.GetProperty("barter").GetBoolean(), x.GetProperty("restrict").GetString()!,
            x.GetProperty("limit").GetInt32(), x.GetProperty("cycle").GetString()!,
            x.GetProperty("sellers").EnumerateArray().Select(s => new ShopSeller(s.GetProperty("name").GetString()!, ReadPeriods(s.GetProperty("events")))).ToArray(),
            ReadPeriods(x.GetProperty("periods")))).ToArray();
        if (items.Length == 0) throw new InvalidDataException("캐시에 상점 상품이 없습니다.");
        return new ShopTable(items);
    }

    private static ShopPeriod[] ReadPeriods(JsonElement periods) => periods.EnumerateArray().Select(p => new ShopPeriod(
        p.TryGetProperty("start", out var start) ? start.GetDateTimeOffset() : null,
        p.TryGetProperty("end", out var end) ? end.GetDateTimeOffset() : null)).ToArray();

    public string Describe(ShopTable data) => $"상품 {data.Items.Count}개";
}
