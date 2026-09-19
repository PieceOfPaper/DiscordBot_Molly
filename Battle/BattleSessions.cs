using System.Collections.Concurrent;

namespace Molly.Battle;

/// <summary>전투 런타임만 보관합니다. 전적·로그·난수는 저장하지 않습니다.</summary>
public sealed class BattleSessions
{
    private readonly ConcurrentDictionary<ulong, byte> guilds = new();
    public bool TryEnter(ulong guildId) => guilds.TryAdd(guildId, 0);
    public void Leave(ulong guildId) => guilds.TryRemove(guildId, out _);
}
