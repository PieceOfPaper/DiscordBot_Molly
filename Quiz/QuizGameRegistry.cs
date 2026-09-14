using System.Collections.Concurrent;

namespace Molly.Quiz;

// 서로 다른 퀴즈 서비스도 길드당 하나의 놀이 채널만 열 수 있도록 예약을 공유합니다.
internal static class QuizGameRegistry
{
    private static readonly ConcurrentDictionary<ulong, TaskCompletionSource<ulong>> rooms = new();

    public static bool TryReserve(ulong guildId, TaskCompletionSource<ulong> room) => rooms.TryAdd(guildId, room);
    public static Task<ulong>? ExistingRoom(ulong guildId) => rooms.TryGetValue(guildId, out var room) ? room.Task : null;
    public static void Release(ulong guildId, TaskCompletionSource<ulong> room) => rooms.TryRemove(new KeyValuePair<ulong, TaskCompletionSource<ulong>>(guildId, room));
}
