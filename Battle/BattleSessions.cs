using System.Collections.Concurrent;

namespace Molly.Battle;

/// <summary>전투 런타임만 보관합니다. 전적·로그·난수는 저장하지 않습니다.</summary>
public sealed class BattleSessions
{
    private readonly ConcurrentDictionary<ulong, BattleSession> guilds = new();

    public bool TryEnter(ulong guildId, out BattleSession session)
    {
        session = new BattleSession();
        if (guilds.TryAdd(guildId, session)) return true;
        session.Dispose();
        return false;
    }

    /// <summary>현재 길드의 전투 출력 중단을 요청합니다. 스레드 정리는 시작 명령이 마무리합니다.</summary>
    public bool TryStop(ulong guildId)
        => guilds.TryGetValue(guildId, out var session) && session.RequestStop();

    public void Leave(ulong guildId, BattleSession session)
    {
        if (guilds.TryGetValue(guildId, out var current) && ReferenceEquals(current, session))
            guilds.TryRemove(guildId, out _);
        session.Dispose();
    }
}

/// <summary>한 번의 배틀 진행에만 쓰는 취소 신호입니다.</summary>
public sealed class BattleSession : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private int stopRequested;

    public CancellationToken CancellationToken => cancellation.Token;
    public bool IsStopRequested => Volatile.Read(ref stopRequested) != 0;

    public bool RequestStop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) != 0) return false;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    public void Dispose() => cancellation.Dispose();
}
