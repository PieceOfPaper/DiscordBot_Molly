using System.Text.Json;
using Molly.Unpack;

namespace Molly.Crafting;

// 제작 레시피는 여러 기능이 함께 쓰는 공용 데이터입니다. 기능별 조건(예: 해연 아이템만)은 사용하는 쪽에서 거릅니다.
public sealed record CraftingIngredient(string Name, int Quantity);

/// <param name="Name">결과물 이름(거래소 아이템 이름과 같음, 예: 해연의 숏소드ZZ).</param>
/// <param name="Category">제작 분류: 무기·방어구·장신구·도구·음식·데코·아이템.</param>
/// <param name="Slot">장비 부위(게임 데이터 Equipment.EquipmentType: Weapon·Hat·Top·Bottom·Gloves·Shoes·Accessory·Ring). 장비가 아니면 빈 문자열입니다.</param>
/// <param name="ArmorType">방어구 재질(중갑·경갑·천옷). 방어구가 아니면 빈 문자열입니다.</param>
/// <param name="Quantity">한 번 제작할 때 나오는 개수.</param>
/// <param name="Id">게임 데이터의 CraftingRecipe.Id.</param>
public sealed record CraftingRecipe(string Name, IReadOnlyList<CraftingIngredient> Ingredients, string Category = "",
    string Slot = "", string ArmorType = "", int Quantity = 1, string Id = "");

public sealed class CraftingTable
{
    public IReadOnlyList<CraftingRecipe> Items { get; }
    public IReadOnlyDictionary<string, CraftingRecipe> ByName { get; }

    public CraftingTable(IEnumerable<CraftingRecipe> items)
    {
        Items = Array.AsReadOnly(items.ToArray());
        ByName = Items.ToDictionary(x => x.Name, StringComparer.Ordinal);
    }
}

/// <summary>
/// 게임 데이터 저장소의 CraftingRecipe 시트로 제작 레시피 목록을 만듭니다(관계는 저장소 Docs/SheetRelations.md).
/// - 제작 화면에 항상 보이는 레시피(DisplayType=Show)만 담습니다. 레시피 아이템을 가져야 보이는 레시피(Consumable, 재료가 절반인 해연 장비 등)·숨김·퀘스트 레시피는 뺍니다.
/// - 결과물은 Equipment·EquipmentTool·Consumable 중 하나이며, 이름은 각 시트의 DisplayName, 재료 이름은 Consumable.DisplayName입니다.
/// - 같은 결과물을 만드는 레시피가 여럿이면 시트 순서상 첫 레시피만 담습니다(이름이 시세·알림 상태의 키입니다).
/// - 방어구 재질은 Equipment.MaterialTypeSheetData가 가리키는 EquipmentMaterialType.DisplayName(중갑·경갑·천옷)입니다.
/// </summary>
public sealed class UnpackCraftingDataset : IUnpackDataset<CraftingTable>
{
    public const string WeaponCategory = "무기";
    public const string ArmorCategory = "방어구";
    public const string AccessoryCategory = "장신구";

    public string Name => "제작";
    public string CacheName => "crafting";

    private sealed record RawRecipe(string Id, string FacilityType, string? Equipment, string? Tool, string? Consumable, int Quantity,
        IReadOnlyList<(string Id, int Amount)> Ingredients);

    public async Task<CraftingTable> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct)
    {
        var raws = new List<RawRecipe>();
        using (var recipe = await sheets("CraftingRecipe", ct))
        {
            foreach (var row in UnpackSheets.Rows(recipe))
                if (ReadRecipe(row) is { } raw) raws.Add(raw);
        }
        if (raws.Count == 0) throw new InvalidDataException("CraftingRecipe 시트에 제작 화면에 보이는 레시피가 없습니다.");

        var equipments = new Dictionary<string, (string Name, string Type, string? Material)>(StringComparer.Ordinal);
        var wantedEquipments = raws.Select(x => x.Equipment).OfType<string>().ToHashSet(StringComparer.Ordinal);
        using (var equipment = await sheets("Equipment", ct))
        {
            foreach (var row in UnpackSheets.Rows(equipment))
                if (UnpackSheets.Reference(row, "Id") is { } id && wantedEquipments.Contains(id))
                    equipments[id] = (UnpackSheets.Text(row, "DisplayName").Trim(), UnpackSheets.Text(row, "EquipmentType"), UnpackSheets.Reference(row, "MaterialTypeSheetData"));
        }

        var materials = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var material = await sheets("EquipmentMaterialType", ct))
        {
            foreach (var row in UnpackSheets.Rows(material))
                if (UnpackSheets.Reference(row, "Id") is { } id) materials[id] = UnpackSheets.Text(row, "DisplayName").Trim();
        }

        var tools = await ReadNamesAsync(sheets, "EquipmentTool", raws.Select(x => x.Tool).OfType<string>(), ct);
        var consumables = await ReadNamesAsync(sheets, "Consumable",
            raws.Select(x => x.Consumable).OfType<string>().Concat(raws.SelectMany(x => x.Ingredients.Select(i => i.Id))), ct);

        var items = new List<CraftingRecipe>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in raws)
        {
            string name, category, slot = "", armorType = "";
            if (raw.Equipment is { } equipmentId)
            {
                var equipment = Lookup(equipments, equipmentId, "Equipment", raw.Id);
                (name, slot) = (equipment.Name, equipment.Type);
                category = CategoryOf(slot);
                if (category == ArmorCategory && equipment.Material is { } materialId)
                    armorType = materials.TryGetValue(materialId, out var materialName) ? materialName
                        : throw new InvalidDataException($"레시피 {raw.Id}의 방어구 재질 {materialId}가 EquipmentMaterialType 시트에 없습니다.");
            }
            else if (raw.Tool is { } toolId)
                (name, category) = (Lookup(tools, toolId, "EquipmentTool", raw.Id), "도구");
            else
                (name, category) = (Lookup(consumables, raw.Consumable!, "Consumable", raw.Id), raw.FacilityType switch
                {
                    "Crafting_FoodCooking" => "음식",
                    "Crafting_Decowork" => "데코",
                    _ => "아이템",
                });
            if (name.Length == 0) throw new InvalidDataException($"레시피 {raw.Id}의 결과물 이름이 비어 있습니다.");
            if (!names.Add(name)) continue;

            var ingredients = new List<CraftingIngredient>();
            foreach (var (id, amount) in raw.Ingredients)
            {
                var ingredientName = Lookup(consumables, id, "Consumable", raw.Id);
                if (ingredientName.Length == 0) throw new InvalidDataException($"레시피 {raw.Id}의 재료 {id} 이름이 비어 있습니다.");
                if (ingredients.Any(x => x.Name == ingredientName)) throw new InvalidDataException($"레시피 {raw.Id}에 재료 '{ingredientName}'가 중복됩니다.");
                ingredients.Add(new CraftingIngredient(ingredientName, amount));
            }
            items.Add(new CraftingRecipe(name, ingredients.AsReadOnly(), category, slot, armorType, raw.Quantity, raw.Id));
        }
        return new CraftingTable(items);
    }

    // 제작 화면에 항상 보이고, 재료가 모두 아이템이며, 결과물이 하나인 레시피만 읽습니다.
    private static RawRecipe? ReadRecipe(JsonElement row)
    {
        if (UnpackSheets.Text(row, "DisplayType") != "Show" || UnpackSheets.Reference(row, "Id") is not { } id) return null;
        if (row.TryGetProperty("FakeItemIngredients", out var fake) && fake.ValueKind == JsonValueKind.Array && fake.GetArrayLength() > 0) return null;
        if (row.TryGetProperty("FakeItemResults", out var fakeResults) && fakeResults.ValueKind == JsonValueKind.Array && fakeResults.GetArrayLength() > 0) return null;

        var ingredients = new List<(string, int)>();
        if (row.TryGetProperty("ConsumableIngredients", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var ingredient in list.EnumerateArray())
            {
                if (UnpackSheets.Reference(ingredient, "Id") is not { } ingredientId) continue;
                var amount = (int)(UnpackSheets.Number(ingredient, "Amount") ?? 0);
                if (amount <= 0) throw new InvalidDataException($"레시피 {id}의 재료 {ingredientId} 수량이 0 이하입니다.");
                ingredients.Add((ingredientId, amount));
            }
        if (ingredients.Count == 0) return null;

        var equipment = UnpackSheets.Reference(row, "EquipmentResultId");
        var tool = UnpackSheets.Reference(row, "EquipmentToolResultId");
        var results = row.TryGetProperty("ConsumableResults", out var resultList) && resultList.ValueKind == JsonValueKind.Array
            ? resultList.EnumerateArray().Select(x => (Id: UnpackSheets.Reference(x, "Id"), Amount: (int)(UnpackSheets.Number(x, "Amount") ?? 0)))
                .Where(x => x.Id is not null).ToArray()
            : [];
        if ((equipment is null ? 0 : 1) + (tool is null ? 0 : 1) + results.Length != 1) return null;
        var quantity = results.Length == 1 ? results[0].Amount : 1;
        if (quantity <= 0) throw new InvalidDataException($"레시피 {id}의 결과물 수량이 0 이하입니다.");
        return new RawRecipe(id, UnpackSheets.Text(row, "FacilityType"), equipment, tool, results.Length == 1 ? results[0].Id : null, quantity, ingredients);
    }

    // 큰 시트(Consumable 약 13MB)는 필요한 행의 이름만 남기고 바로 버립니다.
    private static async Task<Dictionary<string, string>> ReadNamesAsync(UnpackSheetFetcher sheets, string sheet, IEnumerable<string> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted.Count == 0) return result;
        using var document = await sheets(sheet, ct);
        foreach (var row in UnpackSheets.Rows(document))
            if (UnpackSheets.Reference(row, "Id") is { } id && wanted.Contains(id))
                result[id] = UnpackSheets.Text(row, "DisplayName").Trim();
        return result;
    }

    private static T Lookup<T>(IReadOnlyDictionary<string, T> values, string id, string sheet, string recipe) =>
        values.TryGetValue(id, out var value) ? value : throw new InvalidDataException($"레시피 {recipe}가 가리키는 {id}가 {sheet} 시트에 없습니다.");

    // Equipment.EquipmentType → 제작 분류(게임 제작 화면의 무기·방어구·장신구).
    private static string CategoryOf(string equipmentType) => equipmentType switch
    {
        "Weapon" => WeaponCategory,
        "Hat" or "Top" or "Bottom" or "Gloves" or "Shoes" => ArmorCategory,
        "Accessory" or "Ring" => AccessoryCategory,
        _ => "장비",
    };

    public void Write(Utf8JsonWriter writer, CraftingTable data)
    {
        writer.WriteStartArray();
        foreach (var item in data.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            writer.WriteString("name", item.Name);
            writer.WriteString("category", item.Category);
            if (item.Slot.Length > 0) writer.WriteString("slot", item.Slot);
            if (item.ArmorType.Length > 0) writer.WriteString("armorType", item.ArmorType);
            if (item.Quantity != 1) writer.WriteNumber("quantity", item.Quantity);
            writer.WriteStartArray("ingredients");
            foreach (var ingredient in item.Ingredients)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(ingredient.Name);
                writer.WriteNumberValue(ingredient.Quantity);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public CraftingTable Read(JsonElement data)
    {
        static string Optional(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        var items = data.EnumerateArray().Select(x => new CraftingRecipe(
            x.GetProperty("name").GetString()!,
            x.GetProperty("ingredients").EnumerateArray().Select(i => new CraftingIngredient(i[0].GetString()!, i[1].GetInt32())).ToArray(),
            x.GetProperty("category").GetString()!,
            Optional(x, "slot"),
            Optional(x, "armorType"),
            x.TryGetProperty("quantity", out var quantity) ? quantity.GetInt32() : 1,
            x.GetProperty("id").GetString()!)).ToArray();
        if (items.Length == 0) throw new InvalidDataException("캐시에 제작 레시피가 없습니다.");
        return new CraftingTable(items);
    }

    public string Describe(CraftingTable data) =>
        $"제작 레시피 {data.Items.Count}개(" + string.Join(" · ", data.Items.GroupBy(x => x.Category).Select(x => $"{x.Key} {x.Count()}")) + ")";
}
