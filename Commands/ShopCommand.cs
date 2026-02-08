using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using Discord.Interactions;

namespace DiscordBot_Molly.Commands;

public class ShopCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("상점검색", "아이템 기준으로 상점/공유상점/교환상점을 검색합니다.")]
    public async Task Command_Shop([Summary("아이템", "찾을 아이템 이름")] string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            await RespondAsync("아이템 이름을 입력해주세요.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: false);

        var keyword = Normalize(itemName);
        var results = new List<string>();

        foreach (var row in MobiShop.ShopTableDataList)
        {
            if (!ContainsNormalized(row.Item, keyword)) continue;
            var location = BuildNpcRegion(row.Npc, row.Region);
            var price = FormatPricePerItem(row.Cost, row.Currency, row.Item);
            var limit = FormatLimit(row.PurchaseLimitType, "주간", row.PurchaseLimitCount);
            results.Add(BuildBlock(location, price, limit));
        }

        foreach (var row in MobiShop.ShopShareTableDataList)
        {
            if (!ContainsNormalized(row.Item, keyword)) continue;
            var group = string.IsNullOrWhiteSpace(row.Group) ? "공유상점" : row.Group;
            var npc = string.IsNullOrWhiteSpace(row.Npc) ? "" : row.Npc;
            var location = string.IsNullOrWhiteSpace(npc) ? group : $"{group}({npc})";
            var price = FormatPricePerItem(row.Cost, row.Currency, row.Item);
            var limit = FormatLimit(row.PurchaseLimitType1, row.PurchaseLimitType2, row.PurchaseLimitCount);
            results.Add(BuildBlock(location, price, limit));
        }

        foreach (var row in MobiShop.ShopExchangeTableDataList)
        {
            var matchedBuy = ContainsNormalized(row.BuyItem, keyword);
            var matchedCurrency = ContainsNormalized(row.CurrencyItem, keyword);
            if (!matchedBuy && !matchedCurrency) continue;

            var location = BuildNpcRegion(row.Npc, row.Region);
            var exchange = FormatExchange(row.BuyItem, row.BuyCount, row.CurrencyItem, row.CurrencyCount);
            var limit = FormatLimit(row.PurchaseLimitType1, row.PurchaseLimitType2, row.PurchaseLimitCount);
            results.Add(BuildBlock(location, exchange, limit));
        }

        if (results.Count == 0)
        {
            await ModifyOriginalResponseAsync(m => m.Content = $"'{itemName}' 관련 항목을 찾지 못했어요.");
            return;
        }

        var sb = new StringBuilder();
        sb.Append($"🔎 {itemName}");
        if (results.Count > 0) sb.Append("\n\n");
        sb.Append(string.Join("\n\n", results));

        var chunks = SplitIntoDiscordChunks(sb.ToString()).ToList();
        if (chunks.Count == 0)
        {
            await ModifyOriginalResponseAsync(m => m.Content = "결과를 만들지 못했어요.");
            return;
        }

        await ModifyOriginalResponseAsync(m => m.Content = chunks[0]);
        for (int i = 1; i < chunks.Count; i++)
            await FollowupAsync(chunks[i], ephemeral: false, flags: MessageFlags.SuppressEmbeds);
    }

    private static string BuildNpcRegion(string? npc, string? region)
    {
        var n = npc?.Trim();
        var r = region?.Trim();
        if (!string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(r))
            return $"{r}-{n}";
        return n ?? r ?? "(알 수 없음)";
    }

    private static string BuildBlock(string location, string itemLine, string? limit)
    {
        var sb = new StringBuilder();
        sb.Append($"• {location}\n");
        sb.Append($"  {itemLine}");
        if (!string.IsNullOrWhiteSpace(limit))
            sb.Append($"\n  제한: {limit}");
        return sb.ToString();
    }

    private static string FormatPricePerItem(int? cost, string? currency, string? itemName)
    {
        var item = string.IsNullOrWhiteSpace(itemName) ? "아이템" : itemName;
        var itemBold = $"**{item}**";
        if (cost == null || string.IsNullOrWhiteSpace(currency))
            return $"{itemBold} — 가격 정보 없음";
        var price = cost.Value.ToString("N0", CultureInfo.InvariantCulture);
        return $"{itemBold} — {price} {currency} / 개";
    }

    private static string FormatExchange(string? buyItem, int? buyCount, string? currencyItem, int? currencyCount)
    {
        var bItem = string.IsNullOrWhiteSpace(buyItem) ? "구입 아이템" : buyItem;
        var bCount = buyCount ?? 1;
        var bItemBold = $"**{bItem} {bCount}개**";
        var cItem = string.IsNullOrWhiteSpace(currencyItem) ? "재화" : currencyItem;
        var cCount = currencyCount ?? 1;
        return $"{bItemBold} ↔ {cItem} {cCount}개";
    }

    private static string? FormatLimit(string? limitType1, string? limitType2, int? count)
    {
        if (count == null) return null;
        var who = string.IsNullOrWhiteSpace(limitType1) ? "" : $"{limitType1} ";
        var period = string.IsNullOrWhiteSpace(limitType2) ? "" : limitType2;
        if (!string.IsNullOrWhiteSpace(period))
            return $"{who}{count}개 · {period}";
        return $"{who}{count}개";
    }

    private static bool ContainsNormalized(string? value, string keyword)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Normalize(value).Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(FoldAeE(ch));
        }
        return sb.ToString().Trim();
    }

    private static char FoldAeE(char ch)
    {
        // Jamo vowel ㅐ/ㅔ (U+1162/U+1166) + Compatibility Jamo ㅐ/ㅔ (U+3150/U+3154)
        return ch switch
        {
            '\u1162' => '\u1166', // ㅐ -> ㅔ
            '\u3150' => '\u3154', // ㅐ -> ㅔ (compat)
            _ => ch
        };
    }

    private static IEnumerable<string> SplitIntoDiscordChunks(string text)
    {
        const int maxLen = 1900;
        if (text.Length <= maxLen)
        {
            yield return text;
            yield break;
        }

        var lines = text.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > maxLen)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
