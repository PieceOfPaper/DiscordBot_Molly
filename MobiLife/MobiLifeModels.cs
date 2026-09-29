using System.Text.Json.Serialization;

namespace Molly.MobiLife;

// 모비라이프 응답 DTO. JSON 필드는 snake_case이며 MobiLifeApiClient가 자동 변환합니다.
// 기능을 만들 때 필요한 응답만 이곳에 추가하고, 기능 코드에는 도메인 모델로 바꿔 넘기세요.

public sealed record MobiLifeCategory(string ParentCategory, int ItemCount);

public sealed record MobiLifeCategoriesResponse(IReadOnlyList<MobiLifeCategory> Data);

public sealed record MobiLifePagination(int Limit, int Offset, int Count);

// 변화량 필드는 숫자 뒤에 밑줄이 있어 이름을 직접 지정합니다. 누락되면 null입니다.
public sealed record MobiLifeMarketPrice(
    long KindId, string Name, string ParentCategory, long MinPrice, long TotalCount, bool IsSoldOut, string LastVersion,
    [property: JsonPropertyName("pct_change_1h")] decimal? PctChange1h = null,
    [property: JsonPropertyName("pct_change_24h")] decimal? PctChange24h = null,
    [property: JsonPropertyName("pct_change_7d")] decimal? PctChange7d = null,
    [property: JsonPropertyName("count_change_24h")] decimal? CountChange24h = null);

public sealed record MobiLifeMarketPricesResponse(IReadOnlyList<MobiLifeMarketPrice> Data, MobiLifePagination Pagination);
