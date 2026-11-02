using System.Globalization;
using Discord;

namespace Molly.Shop;

/// <summary>
/// /상점검색 결과 Embed. 받는 아이템 하나가 Embed 하나이며, 판매 방법(비용)마다 필드 하나에 판매처(모두)와 구매 제한을 인용 줄로 묶어 적습니다.
/// 판매처가 많아 한 Embed가 글자 수 한도를 넘으면 같은 아이템을 `(이어서)` Embed로 나누고,
/// Discord 제한(메시지당 Embed 10개·합계 6,000자)에 맞춰 여러 메시지로 나눕니다.
/// </summary>
public static class ShopMessages
{
    public const int MaxEmbedsPerMessage = 10;
    // Discord 합계 한도 6,000자에서 여유를 둡니다.
    public const int MaxCharactersPerMessage = 5500;
    private const int MaxFieldValueLength = 1024;

    public static readonly Color ShopColor = new(0x3BA55D);
    public static readonly Color BarterColor = new(0x5865F2);

    // 필드에 쓰는 아이콘 설명. 요약 줄 아래에 작은 글씨로 붙입니다.
    public const string Legend = "-# 💰 재화로 구매 · 🔄 아이템과 물물교환 · 🎁 무료 · → N개 한 번에 받는 개수 · 📍 판매처 · ⏳ 구매 제한(초기화 주기) · ♾️ 제한 없음";

    public static string Header(string query, ShopSearchResult result)
    {
        var header = $"🔍 검색어 **{Format.Sanitize(query.Trim())}** · 지금 판매 중 · 상점 검색 결과 **{result.Total}개**\n{Legend}";
        if (result.Total > result.Shown) header += $"\n-# 결과가 많아 {result.Shown}개만 보여드려요. 아이템 이름을 더 자세히 입력해 주세요.";
        return header;
    }

    public static string NoResult(string query) =>
        $"🔍 검색어 **{Format.Sanitize(query.Trim())}**(으)로 지금 판매 중인 상점 상품을 찾지 못했어요.";

    /// <summary>아이템 하나의 Embed. 보통 하나이며, 글자 수 한도를 넘으면 `(이어서)` Embed로 나눕니다.</summary>
    /// <param name="footer">마지막 Embed에 붙일 출처 문구(예: 게임 데이터 패치).</param>
    public static IReadOnlyList<Embed> Build(ShopItemResult item, string? footer = null)
    {
        // 물물교환으로만 얻는 아이템은 색으로 구분합니다.
        var color = item.Offers.All(x => x.Product.Barter) ? BarterColor : ShopColor;
        var embeds = new List<Embed>();
        EmbedBuilder? embed = null;
        var length = 0;
        for (var i = 0; i < item.Offers.Count; i++)
        {
            var name = FieldName(item.Offers[i].Product);
            // 판매 방법 사이에 빈 줄을 두어 세부 줄이 어느 비용에 속하는지 보이게 합니다.
            var value = FieldValue(item.Offers[i]) + (i < item.Offers.Count - 1 ? "\n\u200b" : "");
            if (embed is not null && length + name.Length + value.Length > MaxCharactersPerMessage - 200)
            {
                embeds.Add(embed.Build());
                embed = null;
            }
            if (embed is null)
            {
                embed = new EmbedBuilder().WithTitle(embeds.Count == 0 ? $"🛒 {item.Item}" : $"🛒 {item.Item} (이어서)").WithColor(color);
                length = embed.Title.Length;
            }
            embed.AddField(name, value);
            length += name.Length + value.Length;
        }
        if (footer is not null) embed!.WithFooter(footer);
        embeds.Add(embed!.Build());
        return embeds;
    }

    /// <summary>메시지별 Embed 묶음.</summary>
    public static IReadOnlyList<IReadOnlyList<Embed>> BuildPages(ShopSearchResult result, string? footer = null)
    {
        var pages = new List<IReadOnlyList<Embed>>();
        var page = new List<Embed>();
        var length = 0;
        foreach (var embed in result.Items.SelectMany((x, i) => Build(x, i == result.Items.Count - 1 ? footer : null)))
        {
            var size = embed.Length;
            if (page.Count > 0 && (page.Count == MaxEmbedsPerMessage || length + size > MaxCharactersPerMessage))
            {
                pages.Add(page);
                page = [];
                length = 0;
            }
            page.Add(embed);
            length += size;
        }
        if (page.Count > 0) pages.Add(page);
        return pages;
    }

    // 예: 💰 1,500 골드 · 🔄 케이틴 특제 통밀빵 10개 → 10개
    public static string FieldName(ShopProduct product)
    {
        var amount = product.Amount == 1 ? "" : $" → {product.Amount:N0}개";
        if (product.Barter) return $"🔄 {product.Currency} {Number(product.Price)}개{amount}";
        return product.Price == 0 ? $"🎁 무료{amount}" : $"💰 {Number(product.Price)} {product.Currency}{amount}";
    }

    public static string FieldValue(ShopOffer offer)
    {
        var locations = offer.Locations.Count == 0 ? "판매처 정보 없음" : string.Join(", ", offer.Locations);
        // 인용 막대로 판매처·제한 줄을 위 비용에 묶습니다.
        var value = $"> 📍 {locations}\n> " + Limit(offer.Product);
        // 판매처가 아주 많아 필드 한도를 넘으면 자릅니다(현재 가장 많은 공용 탭도 한도 안).
        return value.Length <= MaxFieldValueLength ? value : value[..(MaxFieldValueLength - 1)] + "…";
    }

    // 제한 횟수는 구매 횟수입니다(한 번에 여러 개를 받는 상품도 1회).
    public static string Limit(ShopProduct product)
    {
        if (product.Limit <= 0) return "♾️ 구매 제한 없음";
        var who = product.Restrict.Length == 0 ? "" : $"{product.Restrict} ";
        return product.Cycle.Length == 0 ? $"⏳ {who}{product.Limit}회" : $"⏳ {who}{product.Limit}회 · {product.Cycle}";
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
