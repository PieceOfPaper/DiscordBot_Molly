namespace Molly.KeywordMarket;

/// <summary>
/// 검색어로 찾은 거래소 아이템 전체를 추적하는 시세 모니터링 하나의 정의.
/// Id는 DB 구분값이라 바꾸면 기존 등록·상태가 이어지지 않습니다.
/// </summary>
public sealed record KeywordMarketDefinition(string Id, string DisplayName, string Emoji, string Keyword, string? Category)
{
    // 사용자 안내용 검색 조건 설명(예: "'아이템' 분류에서 '상자'").
    public string SearchDescription => Category is null ? $"'{Keyword}'" : $"'{Category}' 분류에서 '{Keyword}'";
}

/// <summary>/상자시세모니터링·/패키지시세모니터링의 조절 가능한 기준값. 값을 바꾸면 다음 정각 갱신부터 적용됩니다.</summary>
public static class KeywordMarketRules
{
    public static readonly KeywordMarketDefinition Box = new("box", "상자", "📦", "상자", "아이템");
    public static readonly KeywordMarketDefinition Package = new("package", "패키지", "🎁", "패키지", null);

    // 과거시세 대비 이 비율 이상 변동하면 알립니다(0.10 = 10%).
    public const decimal ChangeThreshold = 0.10m;

    // 매물이 이 수보다 적으면 최저가를 믿기 어려워 해당 회차의 변동 판정에서 뺍니다(시세 저장은 합니다).
    public const long MinListingCount = 3;

    // 검색 결과에서 이 횟수만큼 연속으로 빠져야 사라진 아이템으로 알립니다. 일시적인 누락으로 사라짐·신규 알림이 반복되지 않게 합니다.
    public const int RemovalMissingRuns = 2;

    // 시간별 시세 이력 보관 기간.
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
}
