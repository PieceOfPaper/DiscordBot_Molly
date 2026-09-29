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

    public async Task<MarketPriceSearchResult> SearchAsync(string keyword, string? category, CancellationToken ct)
    {
        var prices = new List<MarketPrice>();
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await client.GetAsync<MobiLifeMarketPricesResponse>("market/prices",
            [
                new("search", keyword),
                new("parent_category", category),
                new("sort", "pct_change_24h_desc"),
                new("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
                new("offset", (page * PageSize).ToString(CultureInfo.InvariantCulture)),
            ], ct);
            if (!result.IsSuccess) return MarketPriceSearchResult.Fail(result.UserMessage);
            if (result.Value!.Data is null) return MarketPriceSearchResult.Fail("모비라이프 시세 응답 형식이 예상과 다릅니다.");

            foreach (var item in result.Value.Data)
            {
                if (string.IsNullOrWhiteSpace(item.Name) ||
                    !DateTimeOffset.TryParse(item.LastVersion, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pricedAt))
                    return MarketPriceSearchResult.Fail("모비라이프 시세 응답 형식이 예상과 다릅니다.");
                // 값이 말이 안 되는 아이템(예: 매물 수 -1)은 원본 쪽 일시적 오류로 보고 통신 오류처럼 이번 검색을 실패시킵니다.
                // 호출자는 회차를 건너뛰고 다시 시도합니다. 어느 값이 이상한지 로그에서 알 수 있게 메시지에 남깁니다.
                if (InvalidValue(item) is { } invalid)
                    return MarketPriceSearchResult.Fail($"모비라이프 시세 데이터가 일시적으로 비정상입니다({item.Name.Trim()} {invalid}).");
                prices.Add(new MarketPrice(item.KindId, item.Name.Trim(), item.ParentCategory, item.MinPrice, item.TotalCount, item.IsSoldOut, pricedAt.ToUniversalTime(),
                    item.PctChange1h, item.PctChange24h, item.PctChange7d, item.CountChange24h));
            }
            if (result.Value.Data.Count < PageSize) return new MarketPriceSearchResult(prices);
        }
        // 마지막 페이지까지 가득 찼다면 결과가 잘렸을 수 있습니다. 필요한 이름이 빠졌는지는 호출자가 확인합니다.
        return new MarketPriceSearchResult(prices) { IsTruncated = true };
    }

    // 비정상 값이 있으면 "필드=값"을, 없으면 null을 돌려줍니다.
    // 가격 등락은 퍼센트 단위로 봅니다. 가격은 100%보다 더 내릴 수 없으므로 그보다 작으면 이상입니다. 급등은 실제로 일어날 수 있어 상한은 두지 않습니다.
    // 매물 증감(퍼센트)은 원본이 매물 수 -1로 계산해 -100 아래가 나올 수 있으나, 매물 수 검사에서 걸러집니다.
    private static string? InvalidValue(MobiLifeMarketPrice item) =>
        item.MinPrice < 0 ? $"min_price={item.MinPrice}"
        : item.TotalCount < 0 ? $"total_count={item.TotalCount}"
        : item.PctChange1h < -100m ? $"pct_change_1h={item.PctChange1h}"
        : item.PctChange24h < -100m ? $"pct_change_24h={item.PctChange24h}"
        : item.PctChange7d < -100m ? $"pct_change_7d={item.PctChange7d}"
        : null;
}
