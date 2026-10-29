using Discord;

namespace Molly.OfficialNews;

public interface IOfficialNewsSender
{
    /// <summary>Discord에 연결돼 메시지를 보낼 수 있는 상태인지. 아니면 worker가 작업을 가져가지 않는다.</summary>
    bool IsReady { get; }

    /// <summary>보낸 메시지 ID를 돌려준다. 실패는 <see cref="NewsDeliveryException"/>으로 영구·일시 실패를 구분해 알린다.</summary>
    Task<ulong> SendAsync(ulong channelId, NewsMessage message, CancellationToken ct);
}

/// <summary>Permanent면 다시 시도하지 않는다(채널 삭제·봇 추방·권한 상실 등).</summary>
public sealed class NewsDeliveryException(string code, bool permanent, string message) : Exception(message)
{
    public string Code { get; } = code;
    public bool Permanent { get; } = permanent;
}

/// <summary>
/// 공식 소식 발송 작업을 DB에서 가져가 보내는 단일 worker. 서버별 작업은 독립적으로 성공·재시도·실패하며, 한 서버의 실패가 다른 서버 전송을 막지 않는다.
/// 일시 실패는 1분→5분→15분→60분 간격으로 최대 <see cref="MaxAttempts"/>번까지 시도한다.
/// </summary>
public sealed class OfficialNewsDeliveryWorker
{
    public const int MaxAttempts = 8;
    public const int BatchSize = 10;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_IdlePoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] s_Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    private readonly OfficialNewsStore m_Store;
    private readonly IOfficialNewsSender m_Sender;
    private readonly Func<DateTimeOffset> m_UtcNow;
    private readonly Action<string> m_Log;
    private readonly SemaphoreSlim m_Wake = new(0, 1);
    private int m_Started;

    public OfficialNewsDeliveryWorker(OfficialNewsStore store, IOfficialNewsSender sender, Func<DateTimeOffset>? utcNow = null, Action<string>? log = null)
    {
        m_Store = store;
        m_Sender = sender;
        m_UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        m_Log = log ?? (msg => Console.WriteLine($"[공식소식] {msg}"));
    }

    public static TimeSpan BackoffFor(int attemptsMade) => s_Backoff[Math.Clamp(attemptsMade - 1, 0, s_Backoff.Length - 1)];

    /// <summary>새 작업이 생겼음을 알려 대기 중인 worker를 바로 깨운다.</summary>
    public void Signal()
    {
        try { m_Wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>앱 수명 동안 한 번만 실행된다. Discord 재연결과 무관하게 두 번째 호출은 바로 끝난다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref m_Started, 1) != 0) return;
        try
        {
            var recovered = await m_Store.RecoverInterruptedAsync(m_UtcNow(), ct).ConfigureAwait(false);
            if (recovered > 0) m_Log($"이전 실행에서 처리 중이던 발송 작업 {recovered}개를 다시 대기열에 넣었습니다.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            m_Log($"처리 중 작업 복구 실패: {ex.GetType().Name}: {ex.Message}");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (m_Sender.IsReady && await ProcessDueAsync(ct).ConfigureAwait(false) >= BatchSize) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                m_Log($"발송 처리 오류, 잠시 후 다시 시도합니다: {ex.GetType().Name}: {ex.Message}");
            }

            try { await m_Wake.WaitAsync(s_IdlePoll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>보낼 때가 된 작업을 한 묶음 가져가 처리하고 가져간 개수를 돌려준다.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        var deliveries = await m_Store.ClaimDueAsync(m_UtcNow(), Lease, BatchSize, ct).ConfigureAwait(false);
        foreach (var delivery in deliveries)
            await ProcessAsync(delivery, ct).ConfigureAwait(false);
        return deliveries.Count;
    }

    private async Task ProcessAsync(NewsDelivery delivery, CancellationToken ct)
    {
        var target = $"guild={delivery.GuildId} channel={delivery.ChannelId} event={delivery.Event.EventId}";
        if (!m_Sender.IsReady)
        {
            // 묶음 처리 중 연결이 끊기면 시도 횟수를 쓰지 않고 대기로 돌린다.
            await m_Store.ReleaseAsync(delivery.Id, "discord_not_ready", m_UtcNow(), ct).ConfigureAwait(false);
            return;
        }
        if (!await m_Store.IsSubscriptionCurrentAsync(delivery.GuildId, delivery.Board, delivery.ChannelId, delivery.Generation, ct).ConfigureAwait(false))
        {
            // 해제·채널 교체·해제 후 재등록 뒤에는 이전 작업을 보내지 않고, 새 채널로 소급 전송하지도 않는다.
            await m_Store.MarkSkippedAsync(delivery.Id, "subscription_changed", m_UtcNow(), ct).ConfigureAwait(false);
            m_Log($"구독이 해제·변경돼 건너뜀: {target}");
            return;
        }
        if (NewsBoards.Find(delivery.Board) is not { } board)
        {
            await m_Store.MarkFailedAsync(delivery.Id, "unknown_board", m_UtcNow(), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            var message = board == NewsBoards.Maintenance ? MaintenanceMessages.Build(delivery.Event) : OfficialNewsMessages.Build(board, delivery.Event);
            var messageId = await m_Sender.SendAsync(delivery.ChannelId, message, timeout.Token).ConfigureAwait(false);
            await m_Store.MarkSentAsync(delivery.Id, messageId, m_UtcNow(), ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 종료 중에는 sending으로 남기고 다음 시작 때 복구한다.
            throw;
        }
        catch (NewsDeliveryException ex) when (ex.Permanent)
        {
            await m_Store.MarkFailedAsync(delivery.Id, ex.Code, m_UtcNow(), ct).ConfigureAwait(false);
            m_Log($"발송 실패(재시도 안 함) {ex.Code}: {target}");
            return;
        }
        catch (Exception ex)
        {
            var code = ex switch
            {
                NewsDeliveryException delivered => delivered.Code,
                OperationCanceledException => "timeout",
                _ => "unexpected_" + ex.GetType().Name,
            };
            var attemptsMade = delivery.Attempts + 1;
            if (attemptsMade >= MaxAttempts)
            {
                await m_Store.MarkFailedAsync(delivery.Id, "retry_exhausted:" + code, m_UtcNow(), ct).ConfigureAwait(false);
                m_Log($"발송 실패(재시도 {attemptsMade}회 소진) {code}: {target}");
                return;
            }
            var next = m_UtcNow() + BackoffFor(attemptsMade);
            await m_Store.MarkRetryAsync(delivery.Id, next, code, m_UtcNow(), ct).ConfigureAwait(false);
            m_Log($"발송 실패({attemptsMade}회) {code}, {next:HH:mm:ss} UTC에 다시 시도: {target}");
        }
    }
}
