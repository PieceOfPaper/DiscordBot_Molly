using System.Globalization;
using System.Text;
using Discord;
using Molly.Market;

namespace Molly.KeywordMarket;

public interface IKeywordAlertSender
{
    // 실패는 예외로 알립니다. 한 채널의 실패가 다른 채널 전송을 막지 않도록 호출자가 채널별로 처리합니다.
    Task SendAsync(KeywordMonitorChannel channel, IReadOnlyList<Embed> embeds, CancellationToken ct);
}

public sealed record KeywordRunResult(bool Success, string Message, KeywordEvaluation? Evaluation = null);

/// <summary>
/// 검색어로 찾은 거래소 아이템 전체의 시세를 매 정각 수집해 저장하고, 시세 변동·신규 아이템·사라진 아이템을 등록된 채널에 알립니다.
/// 알림을 보낸 뒤 과거시세 상태를 갱신합니다. 일정은 /해연시세모니터링과 같습니다(MarketSchedule).
/// </summary>
public sealed class KeywordMarketMonitor
{
    private readonly IMarketPriceSource m_Source;
    private readonly IKeywordAlertSender m_Sender;
    private readonly Func<DateTimeOffset> m_UtcNow;
    private readonly Action<string> m_Log;
    private readonly SemaphoreSlim m_RunGate = new(1, 1);

    public KeywordMarketMonitor(
        KeywordMarketDefinition definition,
        IMarketPriceSource source,
        KeywordMarketStore store,
        IKeywordAlertSender sender,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? log = null)
    {
        Definition = definition;
        m_Source = source;
        Store = store;
        m_Sender = sender;
        m_UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        m_Log = log ?? (msg => Console.WriteLine($"[{definition.DisplayName}시세] {msg}"));
    }

    public KeywordMarketDefinition Definition { get; }
    public KeywordMarketStore Store { get; }
    public string Attribution => m_Source.Attribution;

    /// <summary>시작 시 DB에 시세가 없거나 직전 정각 수집을 놓쳤으면 즉시 수집하고, 이후 매 정각(KST·UTC 모두 정시)에 수집합니다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var last = await Store.GetLatestCollectedAtAsync(ct).ConfigureAwait(false);
            if (MarketSchedule.NeedsCatchUp(last, m_UtcNow()))
            {
                m_Log(last is null
                    ? "저장된 시세가 없어 즉시 수집합니다."
                    : $"마지막 수집({KeywordMarketMessages.Kst(last.Value)} KST) 이후 정각 수집을 놓쳐 즉시 수집합니다.");
                await CollectAsync(ct).ConfigureAwait(false);
            }
            while (!ct.IsCancellationRequested)
            {
                var next = MarketSchedule.NextHourUtc(m_UtcNow());
                m_Log($"다음 수집: {KeywordMarketMessages.Kst(next)} (KST)");
                // Task.Delay가 시계보다 조금 일찍 깨어날 수 있어 정각이 지날 때까지 다시 기다립니다.
                for (var remaining = next - m_UtcNow(); remaining > TimeSpan.Zero; remaining = next - m_UtcNow())
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                await CollectAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task<KeywordRunResult> CollectAsync(CancellationToken ct)
    {
        await m_RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await CollectLockedAsync(ct).ConfigureAwait(false);
            m_Log(result.Message);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            m_Log($"수집 실패: {ex}");
            return new KeywordRunResult(false, $"수집 실패: {ex.Message}");
        }
        finally { m_RunGate.Release(); }
    }

    private async Task<KeywordRunResult> CollectLockedAsync(CancellationToken ct)
    {
        var collectedAt = m_UtcNow();
        var search = await m_Source.SearchAsync(Definition.Keyword, Definition.Category, ct).ConfigureAwait(false);
        if (!search.IsSuccess) return new KeywordRunResult(false, $"{Definition.SearchDescription} 시세 조회 실패로 이번 회차를 건너뜁니다: {search.FailureMessage}");

        var prices = search.Prices!;
        var previous = await Store.LoadStatesAsync(ct).ConfigureAwait(false);
        // 추적하던 아이템이 있는데 결과가 통째로 비면 검색어·분류 이름 변경이나 API 이상일 가능성이 커서 모두 사라짐으로 알리지 않습니다.
        if (prices.Count == 0 && previous.Count > 0)
            return new KeywordRunResult(false, $"{Definition.SearchDescription} 검색 결과가 0건이라 이번 회차를 건너뜁니다(분류·검색어 이름 변경 가능성 확인).");
        if (search.IsTruncated)
            m_Log($"검색 결과가 페이지 제한에 걸려 잘렸을 수 있어 이번 회차는 사라진 아이템을 판단하지 않습니다({prices.Count}건).");

        var isFirstRun = await Store.GetLatestCollectedAtAsync(ct).ConfigureAwait(false) is null;
        var evaluation = KeywordMarketEvaluator.Evaluate(prices, previous, isFirstRun, canDetectRemoval: !search.IsTruncated, collectedAt);

        var sent = 0;
        if (evaluation.HasAlerts)
        {
            var embeds = KeywordMarketMessages.BuildAlertEmbeds(Definition, evaluation, collectedAt, m_Source.Attribution);
            foreach (var channel in await Store.GetChannelsAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await m_Sender.SendAsync(channel, embeds, ct).ConfigureAwait(false);
                    sent++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { m_Log($"알림 전송 실패 (guild:{channel.GuildId}, channel:{channel.ChannelId}): {ex.Message}"); }
            }
        }

        await Store.SaveRunAsync(collectedAt, prices, evaluation.States.Values, ct).ConfigureAwait(false);
        return new KeywordRunResult(true,
            $"수집 완료: 시세 {prices.Count}개{(isFirstRun ? "(첫 수집, 기준으로 저장)" : "")}, 변동 알림 {evaluation.PriceAlerts.Count}건, " +
            $"신규 {evaluation.NewItems.Count}건, 사라짐 {evaluation.RemovedItems.Count}건, 전송 채널 {sent}곳",
            evaluation);
    }
}

public static class KeywordMarketMessages
{
    // 한 Embed 설명은 4096자, 한 메시지의 Embed 합계는 6000자 제한이라 여유 있게 나눕니다.
    public const int MaxDescriptionLength = 3500;

    public static IReadOnlyList<Embed> BuildAlertEmbeds(KeywordMarketDefinition definition, KeywordEvaluation evaluation, DateTimeOffset collectedAtUtc, string attribution, string titlePrefix = "")
    {
        var lines = new List<string>();
        if (evaluation.NewItems.Count > 0)
        {
            lines.Add("**🆕 새로 올라온 아이템**");
            foreach (var item in evaluation.NewItems.OrderBy(x => x.Price.Name, StringComparer.Ordinal))
                lines.Add($"🆕 {item.Price.Name} · {PriceText(item.Price)}");
        }
        if (evaluation.RemovedItems.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("**👋 사라진 아이템**");
            foreach (var item in evaluation.RemovedItems.OrderBy(x => x.Name, StringComparer.Ordinal))
                lines.Add($"👋 {item.Name} · {(item.LastPrice > 0 ? $"마지막 시세 {Price(item.LastPrice)}" : "확인된 시세 없음")}");
        }
        if (evaluation.PriceAlerts.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("**📊 시세 변동**");
            foreach (var alert in evaluation.PriceAlerts.OrderByDescending(x => Math.Abs(x.ChangeRate)))
            {
                var icon = alert.ChangeRate > 0 ? "📈" : "📉";
                lines.Add($"{icon} {alert.Name} {Price(alert.BaselinePrice)} → {Price(alert.CurrentPrice)} ({Percent(alert.ChangeRate)})");
            }
        }

        var title = $"{titlePrefix}{definition.Emoji} {definition.DisplayName} 시세 알림 · {Kst(collectedAtUtc)} 기준";
        return BuildEmbeds(definition, title, lines, attribution, collectedAtUtc);
    }

    public static string Footer(KeywordMarketDefinition definition, string attribution) => $"몰리 • {definition.DisplayName} 시세 모니터링 • 데이터: {attribution}";

    // 줄 목록을 Discord 글자 수 제한 안의 Embed 여러 개로 나눕니다. 모든 Embed에 출처를 표기합니다.
    public static IReadOnlyList<Embed> BuildEmbeds(KeywordMarketDefinition definition, string title, IReadOnlyList<string> lines, string attribution, DateTimeOffset timestampUtc)
    {
        var chunks = new List<string>();
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0 && builder.Length + line.Length + 1 > MaxDescriptionLength)
            {
                chunks.Add(builder.ToString().TrimEnd());
                builder.Clear();
            }
            builder.AppendLine(line);
        }
        if (builder.Length > 0) chunks.Add(builder.ToString().TrimEnd());

        return chunks.Select((description, index) => new EmbedBuilder()
            .WithTitle(chunks.Count == 1 ? title : $"{title} ({index + 1}/{chunks.Count})")
            .WithDescription(description)
            .WithColor(Color.Gold)
            .WithFooter(Footer(definition, attribution))
            .WithTimestamp(timestampUtc)
            .Build()).ToArray();
    }

    // /상자시세·/패키지시세: 마지막 저장 시세를 이름순으로 한 줄씩 보여줍니다.
    public static IReadOnlyList<string> BuildPriceLines(IReadOnlyList<MarketPrice> prices) =>
        prices.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => $"{x.Name} · {PriceText(x)}").ToArray();

    public static string PriceText(MarketPrice price)
    {
        if (price.IsSoldOut || price.MinPrice <= 0) return "매진";
        var few = price.TotalCount < KeywordMarketRules.MinListingCount ? ", 적음" : "";
        return $"**{Price(price.MinPrice)}** (매물 {Price(price.TotalCount)}개{few})";
    }

    public static string Kst(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, MobiTime.timezone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public static string Price(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    public static string Percent(decimal rate) => (rate >= 0 ? "+" : "") + (rate * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// 알림 테스트용 가상 판정. 마지막 저장 시세에서 아이템을 골라 신규 1개, 사라짐 1개, 시세 변동 1~2개를 서로 겹치지 않게 만듭니다.
    /// 신규·사라짐은 실제로 일어난 일이 아니라 형식 예시이며, 시세 변동의 현재시세는 실제 값·과거시세는 기준을 넘도록 만든 가상 값입니다.
    /// </summary>
    public static KeywordEvaluation? BuildTestEvaluation(IReadOnlyList<MarketPrice> prices, Random random)
    {
        if (prices.Count == 0) return null;
        var shuffled = prices.DistinctBy(x => x.KindId).OrderBy(_ => random.Next()).ToList();
        var newItem = shuffled[0];
        shuffled.RemoveAt(0);
        var removedItem = shuffled.FirstOrDefault();
        if (removedItem is not null) shuffled.RemoveAt(0);

        var priceAlerts = new List<KeywordPriceChangeAlert>();
        foreach (var price in shuffled.Where(x => !x.IsSoldOut && x.MinPrice > 0).Take(random.Next(1, 3)))
        {
            // 기준값보다 0~10%p 더 큰 가상 변동률로 과거시세를 역산합니다.
            var rate = ((double)KeywordMarketRules.ChangeThreshold + random.NextDouble() * 0.10) * (random.Next(2) == 0 ? 1 : -1);
            priceAlerts.Add(new KeywordPriceChangeAlert(price.Name, Math.Max(1, (long)Math.Round(price.MinPrice / (1 + rate))), price.MinPrice));
        }
        return new KeywordEvaluation(priceAlerts, [new KeywordNewItemAlert(newItem)],
            removedItem is null ? [] : [new KeywordRemovedItemAlert(removedItem.Name, removedItem.IsSoldOut ? 0 : removedItem.MinPrice)],
            new Dictionary<long, KeywordItemState>());
    }
}
