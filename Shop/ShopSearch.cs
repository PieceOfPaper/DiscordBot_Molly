using System.Text;
using Molly.World;

namespace Molly.Shop;

/// <param name="Locations">지금 열린 판매처. NPC·지역 데이터에 배치 지역이 있으면 `지역-NPC`, 없으면 NPC(또는 상점) 이름입니다.</param>
public sealed record ShopOffer(ShopProduct Product, IReadOnlyList<string> Locations);

/// <summary>받는 아이템 이름 하나에 대한 판매 방법 목록.</summary>
public sealed record ShopItemResult(string Item, IReadOnlyList<ShopOffer> Offers);

/// <param name="Total">조건에 맞는 판매 방법 전체 수. 보여주는 수(<see cref="Shown"/>)보다 많으면 잘린 것입니다.</param>
public sealed record ShopSearchResult(IReadOnlyList<ShopItemResult> Items, int Total)
{
    public int Shown => Items.Sum(x => x.Offers.Count);
}

/// <summary>`/상점검색`: 지금 판매 중인 상품 중 아이템 이름(물물교환이면 내는 아이템 이름도)에 검색어가 들어간 상품을 찾습니다.</summary>
public static class ShopSearch
{
    // 결과가 너무 많으면 메시지가 길어지므로 이만큼만 보여줍니다. 한 아이템의 판매 방법이 모두 한 Embed(필드 25개)에 들어가는 수입니다.
    public const int MaxResults = 25;

    public static ShopSearchResult Find(ShopTable shops, WorldTable? world, string itemName, DateTimeOffset now, int maxResults = MaxResults)
    {
        var keyword = Normalize(itemName);
        if (keyword.Length == 0) return new ShopSearchResult([], 0);
        var regions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var npc in world?.Npcs ?? []) regions.TryAdd(npc.Name, npc.Regions);

        var matched = shops.Items
            .Where(x => x.IsOnSale(now) && (Normalize(x.Item).Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                                            x.Barter && Normalize(x.Currency).Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // 이름이 검색어와 같은 아이템을 먼저, 그다음 이름에 검색어가 들어간 아이템, 마지막으로 재화로만 걸린 물물교환을 보여줍니다.
        var ordered = matched
            .GroupBy(x => x.Item, StringComparer.Ordinal)
            .OrderBy(g => Normalize(g.Key) == keyword ? 0 : Normalize(g.Key).Contains(keyword, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ToList();

        var items = new List<ShopItemResult>();
        var remaining = maxResults;
        foreach (var group in ordered)
        {
            if (remaining <= 0) break;
            var offers = group.Take(remaining).Select(x => new ShopOffer(x, Locations(x, regions, now))).ToArray();
            items.Add(new ShopItemResult(group.Key, offers));
            remaining -= offers.Length;
        }
        return new ShopSearchResult(items, matched.Count);
    }

    private static IReadOnlyList<string> Locations(ShopProduct product, Dictionary<string, IReadOnlyList<string>> regions, DateTimeOffset now) =>
        product.Sellers.Where(x => x.IsOpen(now))
            .SelectMany(x => regions.TryGetValue(x.Name, out var names) && names.Count > 0 ? names.Select(r => $"{r}-{x.Name}") : [x.Name])
            .Distinct().ToArray();

    // 공백을 무시하고 ㅐ/ㅔ를 같게 봅니다(예: 게/개).
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(ch switch
            {
                'ᅢ' => 'ᅦ', // ㅐ -> ㅔ
                'ㅐ' => 'ㅔ', // ㅐ -> ㅔ (compat)
                _ => ch,
            });
        }
        return sb.ToString();
    }
}
