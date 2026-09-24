namespace Molly.Market;

// 거래소 시세 도메인 모델. 기능은 이 인터페이스만 사용하고, 실제 공급 경로(현재 모비라이프 OpenAPI)는 구현체에서 교체합니다.
public sealed record MarketPrice(long KindId, string Name, string Category, long MinPrice, long TotalCount, bool IsSoldOut, DateTimeOffset PricedAtUtc);

// Prices가 null이면 실패이며, FailureMessage는 사용자에게 보여줄 수 있는 한국어 안내입니다.
public sealed record MarketPriceSearchResult(IReadOnlyList<MarketPrice>? Prices, string? FailureMessage = null)
{
    public bool IsSuccess => Prices is not null;
    public static MarketPriceSearchResult Fail(string message) => new(null, message);
}

public interface IMarketPriceSource
{
    // 공급 경로의 출처 표기 문구(예: "모비라이프 제공"). 시세를 보여주는 응답에 반드시 붙입니다.
    string Attribution { get; }

    // 이름에 검색어가 포함된 아이템의 최신 시세 전체를 돌려줍니다.
    Task<MarketPriceSearchResult> SearchAsync(string keyword, CancellationToken ct);
}
