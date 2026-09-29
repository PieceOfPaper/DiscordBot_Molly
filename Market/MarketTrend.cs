namespace Molly.Market;

public enum MarketTrendDirection { Up, Down }

// 같은 방향이 이어질 가능성. 매물 증감으로만 추정하며, 매물 변화가 없거나 값이 없으면 Unknown입니다.
public enum MarketTrendOutlook { Unknown, High, Low }

/// <summary>최근 가격 방향과 같은 방향이 이어질 가능성. 정확한 예측이 아니라 모비라이프 변화량을 읽기 쉬운 문구로 바꾼 것입니다.</summary>
public sealed record MarketTrend(MarketTrendDirection Direction, MarketTrendOutlook Outlook)
{
    // 목록 한 줄에 붙이는 이모지. 방향(📈·📉) 뒤에 가능성을 붙입니다. 예: "📉⏬" = 하락 중 · 하락 가능성 높음
    // 뜻은 목록 아래 범례(MarketTrendEvaluator.Legend)로 설명합니다.
    public string Emoji => (Direction, Outlook) switch
    {
        (MarketTrendDirection.Up, MarketTrendOutlook.High) => "📈⏫",
        (MarketTrendDirection.Up, MarketTrendOutlook.Low) => "📈🔼",
        (MarketTrendDirection.Down, MarketTrendOutlook.High) => "📉⏬",
        (MarketTrendDirection.Down, MarketTrendOutlook.Low) => "📉🔽",
        (MarketTrendDirection.Up, _) => "📈",
        _ => "📉",
    };

    // 판정 이유 한 문장. 목록에는 붙이지 않고 한 항목을 자세히 보여줄 때 씁니다.
    public string Reason => (Direction, Outlook) switch
    {
        (MarketTrendDirection.Down, MarketTrendOutlook.High) => "가격은 내리고 매물은 늘고 있어요.",
        (MarketTrendDirection.Down, MarketTrendOutlook.Low) => "가격은 내리고 있지만 매물은 줄고 있어요.",
        (MarketTrendDirection.Up, MarketTrendOutlook.High) => "가격은 오르고 매물은 줄고 있어요.",
        (MarketTrendDirection.Up, MarketTrendOutlook.Low) => "가격은 오르고 있지만 매물도 늘고 있어요.",
        (MarketTrendDirection.Down, _) => "최근 1시간과 24시간 가격이 모두 내렸어요.",
        _ => "최근 1시간과 24시간 가격이 모두 올랐어요.",
    };
}

/// <summary>시세 변화 문구 판정 기준. 값을 바꾸면 다음 조회부터 적용됩니다.</summary>
public static class MarketTrendRules
{
    // 1시간·24시간 가격 등락이 모두 이 퍼센트 이상(절댓값)이어야 방향을 정합니다. 그보다 작으면 미세 변화로 보고 표시하지 않습니다.
    public const decimal MinPriceChangePercent = 1m;

    // 매물이 이 수보다 적으면 최저가를 믿기 어려워 판정하지 않습니다.
    public const long MinListingCount = 3;
}

/// <summary>
/// 마지막 시세의 변화량으로 변화 문구를 판정하는 순수 로직입니다. API·DB를 참조하지 않으며 호출을 늘리지 않습니다.
/// 확신하기 어려우면 null을 돌려주고, 호출자는 아무것도 표시하지 않습니다.
/// </summary>
public static class MarketTrendEvaluator
{
    public static MarketTrend? Evaluate(MarketPrice price)
    {
        if (price.IsSoldOut || price.MinPrice <= 0 || price.TotalCount < MarketTrendRules.MinListingCount) return null;
        if (DirectionOf(price.PriceChange1hPercent) is not { } recent || DirectionOf(price.PriceChange24hPercent) is not { } daily) return null;
        if (recent != daily) return null;

        // 매물 감소는 구매뿐 아니라 등록 취소·만료로도 생길 수 있어 '가능성'으로만 표현합니다.
        var outlook = price.CountChange24hPercent switch
        {
            > 0 => recent == MarketTrendDirection.Down ? MarketTrendOutlook.High : MarketTrendOutlook.Low,
            < 0 => recent == MarketTrendDirection.Up ? MarketTrendOutlook.High : MarketTrendOutlook.Low,
            _ => MarketTrendOutlook.Unknown,
        };
        return new MarketTrend(recent, outlook);
    }

    // 목록 한 줄 끝에 붙일 이모지. 판정하지 않으면 빈 문자열입니다.
    public static string Suffix(MarketPrice price) => Evaluate(price) is { } trend ? " · " + trend.Emoji : "";

    // 판정이 없을 때(표시 없음) 변화 알림에서 쓰는 이모지.
    public const string NoneEmoji = "➖";

    public static string EmojiOf(MarketTrend? trend) => trend?.Emoji ?? NoneEmoji;

    // 목록 맨 아래에 붙이는 이모지 설명(Discord 작은 글씨).
    public static readonly IReadOnlyList<string> Legend =
    [
        "-# 📈 상승 중 · ⏫ 상승 가능성 높음 · 🔼 상승 가능성 낮음",
        "-# 📉 하락 중 · ⏬ 하락 가능성 높음 · 🔽 하락 가능성 낮음",
    ];

    public const string NoneLegend = "-# ➖ 방향 판단 어려움(표시 없음)";

    // 보여준 시세 중 하나라도 변화 이모지가 붙었으면 목록 끝에 한 줄 띄우고 범례를 붙입니다.
    public static IReadOnlyList<string> AppendLegend(IReadOnlyList<string> lines, IEnumerable<MarketPrice> shownPrices) =>
        AppendLegend(lines, shownPrices.Any(x => Evaluate(x) is not null), includeNone: false);

    public static IReadOnlyList<string> AppendLegend(IReadOnlyList<string> lines, bool hasTrend, bool includeNone) =>
        !hasTrend && !includeNone ? lines : [.. lines, "", .. Legend, .. includeNone ? new[] { NoneLegend } : []];

    /// <summary>
    /// 직전 회차 시세와 이번 회차 시세의 판정(이모지)이 바뀐 아이템을 찾습니다. 표시 없음으로 바뀌거나 표시 없음에서 생겨도 알립니다.
    /// 직전 회차에 없던 아이템(첫 수집·새 아이템)은 비교할 대상이 없어 알리지 않습니다.
    /// </summary>
    public static IReadOnlyList<MarketTrendChangeAlert> EvaluateChanges<TKey>(
        IEnumerable<MarketPrice> current, IReadOnlyDictionary<TKey, MarketPrice>? previous, Func<MarketPrice, TKey> key) where TKey : notnull
    {
        if (previous is null) return [];
        var alerts = new List<MarketTrendChangeAlert>();
        foreach (var price in current)
        {
            if (!previous.TryGetValue(key(price), out var last)) continue;
            var before = Evaluate(last);
            var after = Evaluate(price);
            if (before != after) alerts.Add(new MarketTrendChangeAlert(price, before, after));
        }
        return alerts;
    }

    private static MarketTrendDirection? DirectionOf(decimal? percent) => percent switch
    {
        null => null,
        >= MarketTrendRules.MinPriceChangePercent => MarketTrendDirection.Up,
        <= -MarketTrendRules.MinPriceChangePercent => MarketTrendDirection.Down,
        _ => null,
    };
}

/// <summary>상승·하락 판정(이모지)이 바뀐 아이템. null은 표시 없음입니다.</summary>
public sealed record MarketTrendChangeAlert(MarketPrice Price, MarketTrend? Previous, MarketTrend? Current)
{
    public string Name => Price.Name;
    public string ChangeText => $"{MarketTrendEvaluator.EmojiOf(Previous)} → {MarketTrendEvaluator.EmojiOf(Current)}";
}
