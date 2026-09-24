using System.Globalization;
using Molly.Market;

namespace Molly.MobiLife;

/// <summary>모비라이프 OpenAPI의 market/prices로 거래소 시세를 공급합니다.</summary>
public sealed class MobiLifeMarketPriceSource(MobiLifeApiClient client) : IMarketPriceSource
{
    public const int PageSize = 100;
    // 검색 한 번이 요청을 무한히 늘리지 않도록 페이지 수를 제한합니다(약관의 남용 방지).
    public const int MaxPages = 5;

    public string Attribution => MobiLifeAttribution.Text;

    public async Task<MarketPriceSearchResult> SearchAsync(string keyword, CancellationToken ct)
    {
        var prices = new List<MarketPrice>();
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await client.GetAsync<MobiLifeMarketPricesResponse>("market/prices",
            [
                new("search", keyword),
                new("sort", "pct_change_24h_desc"),
                new("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
                new("offset", (page * PageSize).ToString(CultureInfo.InvariantCulture)),
            ], ct);
            if (!result.IsSuccess) return MarketPriceSearchResult.Fail(result.UserMessage);
            if (result.Value!.Data is null) return MarketPriceSearchResult.Fail("모비라이프 시세 응답 형식이 예상과 다릅니다.");

            foreach (var item in result.Value.Data)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || item.MinPrice < 0 || item.TotalCount < 0 ||
                    !DateTimeOffset.TryParse(item.LastVersion, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pricedAt))
                    return MarketPriceSearchResult.Fail("모비라이프 시세 응답 형식이 예상과 다릅니다.");
                prices.Add(new MarketPrice(item.KindId, item.Name.Trim(), item.ParentCategory, item.MinPrice, item.TotalCount, item.IsSoldOut, pricedAt.ToUniversalTime()));
            }
            if (result.Value.Data.Count < PageSize) return new MarketPriceSearchResult(prices);
        }
        // 마지막 페이지까지 가득 찼다면 결과가 잘렸을 수 있지만, 필요한 이름이 빠졌는지는 호출자가 확인합니다.
        return new MarketPriceSearchResult(prices);
    }
}
