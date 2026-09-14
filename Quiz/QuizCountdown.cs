namespace Molly.Quiz;

public static class QuizCountdown
{
    public static async Task RunAsync(IQuizRoom room, int seconds, string label, bool dramaticFinalSeconds,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        var duration = TimeSpan.FromSeconds(seconds);
        var previousRemaining = seconds + 1;
        ulong previousMessageId = 0;

        try
        {
            while (clock.GetElapsedTime(started) < duration)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = Math.Max(0, (int)Math.Ceiling((duration - clock.GetElapsedTime(started)).TotalSeconds));
                if (remaining == 0) return;

                if (remaining != previousRemaining && ShouldAnnounce(remaining, dramaticFinalSeconds))
                {
                    var messageId = await room.SendAsync(FormatMessage(label, remaining, dramaticFinalSeconds), ct);
                    if (previousMessageId != 0) await TryDeleteAsync(room, previousMessageId, ct);
                    previousMessageId = messageId;
                }
                previousRemaining = remaining;

                var nextRemaining = NextAnnouncement(remaining, dramaticFinalSeconds);
                var nextElapsed = nextRemaining is int next
                    ? TimeSpan.FromSeconds(seconds - next)
                    : duration;
                // 전송 요청이 늦어져도 절대 마감 시각을 기준으로 다음 알림을 잡습니다.
                var untilNext = nextElapsed - clock.GetElapsedTime(started);
                if (untilNext > TimeSpan.Zero) await delay(untilNext, ct);
            }
        }
        finally
        {
            if (previousMessageId != 0)
                await TryDeleteAsync(room, previousMessageId, CancellationToken.None);
        }
    }

    // 카운트다운 메시지는 정리용입니다. 다른 봇/사용자가 먼저 지웠거나 삭제 요청이 실패해도
    // 출제·채점 흐름을 중단하면 안 됩니다.
    private static async Task TryDeleteAsync(IQuizRoom room, ulong messageId, CancellationToken ct)
    {
        try { await room.DeleteAsync(messageId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
    }

    private static bool ShouldAnnounce(int remaining, bool dramaticFinalSeconds)
        => remaining % 5 == 0 || (dramaticFinalSeconds && remaining is >= 1 and <= 3);

    private static int? NextAnnouncement(int remaining, bool dramaticFinalSeconds)
    {
        var nextFiveSeconds = ((remaining - 1) / 5) * 5;
        var next = nextFiveSeconds > 0 ? nextFiveSeconds : 0;
        if (dramaticFinalSeconds && remaining > 3) next = Math.Max(next, 3);
        else if (dramaticFinalSeconds && remaining is 2 or 3) next = remaining - 1;
        return next == 0 ? null : next;
    }

    private static string FormatMessage(string label, int remaining, bool dramaticFinalSeconds)
    {
        if (dramaticFinalSeconds && remaining is >= 1 and <= 3)
            return $"🚨🚨 **마지막 {remaining}초!** 🚨🚨";

        return $"⏱️ **{label} {remaining}초**";
    }
}
