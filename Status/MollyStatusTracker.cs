using System.Collections.Concurrent;

namespace Molly.Status;

/// <summary>진행 중인 수집 시도 하나. 끝날 때 <see cref="MollyStatusTracker.CompleteAsync"/>에 넘긴다.</summary>
public sealed record JobRun(string JobId, long RunId, DateTimeOffset StartedAt);

/// <summary>
/// 수집 작업과 알림 전송이 상태를 남기는 창구. 이번 봇 실행의 시작 시각과 진행 중인 작업은 메모리에만 두어 재시작하면 초기화되고,
/// 마지막 결과는 <see cref="MollyStatusStore"/>에 영속화한다.
/// 상태 기록 실패는 기존 수집·알림 작업을 실패시키지 않는다. 로그를 남기고 실패 횟수를 세어 /몰리상태에서 드러낸다.
/// </summary>
public sealed class MollyStatusTracker
{
    private readonly ConcurrentDictionary<string, JobRun> m_Running = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> m_UtcNow;
    private readonly Action<string> m_Log;
    private long m_NextRunId;
    private int m_RecordFailures;
    private long m_LastRecordFailureTicks;

    public MollyStatusTracker(MollyStatusStore store, DateTimeOffset processStartedAt, Func<DateTimeOffset>? utcNow = null, Action<string>? log = null)
    {
        Store = store;
        ProcessStartedAt = processStartedAt;
        m_UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        m_Log = log ?? (msg => Console.WriteLine($"[몰리상태] {msg}"));
    }

    public MollyStatusStore Store { get; }

    /// <summary>이번 봇 실행이 시작된 시각(UTC).</summary>
    public DateTimeOffset ProcessStartedAt { get; }

    /// <summary>이번 실행에서 상태 기록에 실패한 횟수와 마지막 실패 시각.</summary>
    public (int Count, DateTimeOffset? LastAt) RecordFailures
    {
        get
        {
            var ticks = Interlocked.Read(ref m_LastRecordFailureTicks);
            return (Volatile.Read(ref m_RecordFailures), ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
        }
    }

    public JobRun Begin(string jobId)
    {
        var run = new JobRun(jobId, Interlocked.Increment(ref m_NextRunId), m_UtcNow());
        m_Running[jobId] = run;
        return run;
    }

    /// <summary>지금 진행 중인 시도의 시작 시각. 없으면 null.</summary>
    public DateTimeOffset? RunningSince(string jobId) => m_Running.TryGetValue(jobId, out var run) ? run.StartedAt : null;

    /// <summary>결과 없이 끝난 시도(호출자 취소·종료)의 진행 표시만 지운다.</summary>
    public void Abandon(JobRun run) =>
        // 같은 작업의 더 새로운 시도가 이미 시작됐으면 진행 표시는 그 시도의 것으로 둔다.
        ((ICollection<KeyValuePair<string, JobRun>>)m_Running).Remove(new KeyValuePair<string, JobRun>(run.JobId, run));

    /// <summary>시도를 끝내고 결과를 저장한다. 예외를 던지지 않는다.</summary>
    public async Task CompleteAsync(JobRun run, bool succeeded, string? errorKind)
    {
        Abandon(run);
        try { await Store.RecordJobAsync(run.JobId, run.StartedAt, succeeded, errorKind).ConfigureAwait(false); }
        catch (Exception ex) { OnRecordFailed($"{run.JobId} 수집 상태", ex); }
    }

    /// <summary>알림 전송 결과를 저장한다. 예외를 던지지 않는다.</summary>
    public async Task RecordDeliveryAsync(ulong guildId, string alertKind, ulong channelId, bool succeeded, string? errorKind = null)
    {
        try { await Store.RecordDeliveryAsync(guildId, alertKind, channelId, m_UtcNow(), succeeded, errorKind).ConfigureAwait(false); }
        catch (Exception ex) { OnRecordFailed($"{alertKind} 전송 상태(guild:{guildId})", ex); }
    }

    private void OnRecordFailed(string target, Exception ex)
    {
        Interlocked.Increment(ref m_RecordFailures);
        Interlocked.Exchange(ref m_LastRecordFailureTicks, m_UtcNow().UtcTicks);
        m_Log($"{target} 기록 실패: {ex.GetType().Name}: {ex.Message}");
    }
}
