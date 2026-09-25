using System.Globalization;
using System.Text;
using Discord;
using Molly.Crafting;
using Molly.Market;

namespace Molly.HaeyeonMarket;

public interface IHaeyeonAlertSender
{
    // 실패는 예외로 알립니다. 한 채널의 실패가 다른 채널 전송을 막지 않도록 호출자가 채널별로 처리합니다.
    Task SendAsync(HaeyeonMonitorChannel channel, IReadOnlyList<Embed> embeds, CancellationToken ct);
}

public sealed record HaeyeonRunResult(bool Success, string Message, HaeyeonEvaluation? Evaluation = null);

/// <summary>
/// 해연 제작 아이템·재료 시세를 매 정각 수집해 저장하고, 변동·유불리 전환 시 등록된 채널에 알립니다.
/// 알림을 보낸 뒤 과거시세·유불리 상태를 갱신합니다.
/// </summary>
public sealed class HaeyeonMarketMonitor
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<CraftingRecipe>>> m_Recipes;
    private readonly IMarketPriceSource m_Source;
    private readonly IHaeyeonAlertSender m_Sender;
    private readonly Func<DateTimeOffset> m_UtcNow;
    private readonly Action<string> m_Log;
    private readonly SemaphoreSlim m_RunGate = new(1, 1);

    public HaeyeonMarketMonitor(
        Func<CancellationToken, Task<IReadOnlyList<CraftingRecipe>>> recipes,
        IMarketPriceSource source,
        HaeyeonMarketStore store,
        IHaeyeonAlertSender sender,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? log = null)
    {
        m_Recipes = recipes;
        m_Source = source;
        Store = store;
        m_Sender = sender;
        m_UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        m_Log = log ?? (msg => Console.WriteLine($"[해연시세] {msg}"));
    }

    public HaeyeonMarketStore Store { get; }
    public string Attribution => m_Source.Attribution;

    public static DateTimeOffset NextHourUtc(DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
    }

    /// <summary>
    /// 시작 시 즉시 수집이 필요한지 판단합니다. 저장된 시세가 없거나, 마지막 수집이 가장 최근 정각보다 이전이면
    /// (예: 13:03 시작, 마지막 수집 12:57 → 13:00 수집을 놓침) 즉시 수집합니다.
    /// </summary>
    public static bool NeedsCatchUp(DateTimeOffset? lastCollectedUtc, DateTimeOffset nowUtc) =>
        lastCollectedUtc is null || lastCollectedUtc.Value < NextHourUtc(nowUtc).AddHours(-1);

    /// <summary>시작 시 DB에 시세가 없거나 직전 정각 수집을 놓쳤으면 즉시 수집하고, 이후 매 정각(KST·UTC 모두 정시)에 수집합니다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var last = await Store.GetLatestCollectedAtAsync(ct).ConfigureAwait(false);
            if (NeedsCatchUp(last, m_UtcNow()))
            {
                m_Log(last is null
                    ? "저장된 시세가 없어 즉시 수집합니다."
                    : $"마지막 수집({TimeZoneInfo.ConvertTime(last.Value, MobiTime.timezone):yyyy-MM-dd HH:mm} KST) 이후 정각 수집을 놓쳐 즉시 수집합니다.");
                await CollectAsync(ct).ConfigureAwait(false);
            }
            while (!ct.IsCancellationRequested)
            {
                var next = NextHourUtc(m_UtcNow());
                m_Log($"다음 수집: {TimeZoneInfo.ConvertTime(next, MobiTime.timezone):yyyy-MM-dd HH:mm} (KST)");
                // Task.Delay가 시계보다 조금 일찍 깨어날 수 있어 정각이 지날 때까지 다시 기다립니다.
                for (var remaining = next - m_UtcNow(); remaining > TimeSpan.Zero; remaining = next - m_UtcNow())
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                await CollectAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task<HaeyeonRunResult> CollectAsync(CancellationToken ct)
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
            return new HaeyeonRunResult(false, $"수집 실패: {ex.Message}");
        }
        finally { m_RunGate.Release(); }
    }

    private async Task<HaeyeonRunResult> CollectLockedAsync(CancellationToken ct)
    {
        var collectedAt = m_UtcNow();
        var recipes = await m_Recipes(ct).ConfigureAwait(false);
        if (recipes.Count == 0) return new HaeyeonRunResult(false, "제작 시트에 해연 아이템이 없어 수집하지 않았습니다.");
        var tracked = HaeyeonMarketEvaluator.TrackedNames(recipes);

        // 검색어 중 하나라도 실패하면 일부 시세만으로 잘못 알리지 않도록 이번 회차 전체를 건너뜁니다.
        var prices = new Dictionary<string, MarketPrice>(StringComparer.Ordinal);
        foreach (var keyword in HaeyeonMarketRules.SearchKeywords)
        {
            var search = await m_Source.SearchAsync(keyword, ct).ConfigureAwait(false);
            if (!search.IsSuccess) return new HaeyeonRunResult(false, $"'{keyword}' 시세 조회 실패로 이번 회차를 건너뜁니다: {search.FailureMessage}");
            foreach (var price in search.Prices!)
                if (tracked.ContainsKey(price.Name)) prices.TryAdd(price.Name, price);
        }

        var evaluation = HaeyeonMarketEvaluator.Evaluate(recipes, prices,
            await Store.LoadPriceStatesAsync(ct).ConfigureAwait(false),
            await Store.LoadCraftStatesAsync(ct).ConfigureAwait(false),
            collectedAt);
        if (evaluation.MissingNames.Count > 0)
            m_Log($"시세 검색 결과에 없는 이름 {evaluation.MissingNames.Count}개(시트 이름 또는 검색어 확인): {string.Join(", ", evaluation.MissingNames)}");

        var sent = 0;
        if (evaluation.HasAlerts)
        {
            var embeds = HaeyeonMarketMessages.BuildAlertEmbeds(evaluation, collectedAt, m_Source.Attribution);
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

        await Store.SaveRunAsync(collectedAt,
            prices.Values.Select(x => new HaeyeonPriceSnapshot(x, tracked[x.Name])),
            evaluation.PriceStates.Values, evaluation.CraftStates.Values, ct).ConfigureAwait(false);
        return new HaeyeonRunResult(true,
            $"수집 완료: 시세 {prices.Count}/{tracked.Count}개, 변동 알림 {evaluation.PriceAlerts.Count}건, 유불리 전환 {evaluation.CraftAlerts.Count}건, 전송 채널 {sent}곳",
            evaluation);
    }
}

public static class HaeyeonMarketMessages
{
    // 한 Embed 설명은 4096자, 한 메시지의 Embed 합계는 6000자 제한이라 여유 있게 나눕니다.
    public const int MaxDescriptionLength = 3500;

    public static IReadOnlyList<Embed> BuildAlertEmbeds(HaeyeonEvaluation evaluation, DateTimeOffset collectedAtUtc, string attribution, string titlePrefix = "")
    {
        var lines = new List<string>();
        if (evaluation.PriceAlerts.Count > 0)
        {
            lines.Add("**📊 시세 변동**");
            foreach (var alert in evaluation.PriceAlerts.OrderByDescending(x => x.IsProduct).ThenByDescending(x => Math.Abs(x.ChangeRate)))
            {
                var icon = alert.ChangeRate > 0 ? "📈" : "📉";
                var kind = alert.IsProduct ? "제작품" : "재료";
                lines.Add($"{icon} [{kind}] {alert.Name} {Price(alert.BaselinePrice)} → {Price(alert.CurrentPrice)} ({Percent(alert.ChangeRate)})");
            }
        }
        if (evaluation.CraftAlerts.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("**⚒️ 제작·구매 유불리 변동**");
            foreach (var alert in evaluation.CraftAlerts.OrderBy(x => x.Advantage).ThenBy(x => x.ProductName, StringComparer.Ordinal))
            {
                var text = alert.Advantage == CraftAdvantage.Craft
                    ? $"⚒️ {alert.ProductName}: 이제 **재료를 사서 제작**하는 쪽이 저렴해요"
                    : $"🛒 {alert.ProductName}: 이제 **완제품을 사는** 쪽이 저렴해요";
                lines.Add($"{text} · 완제품 {Price(alert.ProductPrice)} / 재료 합계 {Price(alert.MaterialCost)} (완제품이 {Percent(alert.PremiumRate)})");
            }
        }

        var title = $"{titlePrefix}💹 해연 시세 알림 · {Kst(collectedAtUtc)} 기준";
        return BuildEmbeds(title, lines, attribution, collectedAtUtc);
    }

    public static string Kst(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, MobiTime.timezone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // 줄 목록을 Discord 글자 수 제한 안의 Embed 여러 개로 나눕니다. 모든 Embed에 출처를 표기합니다.
    public static IReadOnlyList<Embed> BuildEmbeds(string title, IReadOnlyList<string> lines, string attribution, DateTimeOffset timestampUtc)
    {
        var footer = $"몰리 • 해연 시세 모니터링 • 데이터: {attribution}";
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
            .WithFooter(footer)
            .WithTimestamp(timestampUtc)
            .Build()).ToArray();
    }

    public static string Price(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    public static string Percent(decimal rate) => (rate >= 0 ? "+" : "") + (rate * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
