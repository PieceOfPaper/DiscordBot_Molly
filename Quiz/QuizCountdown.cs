namespace Molly.Quiz;

public static class QuizCountdown
{
    public static async Task RunAsync(IQuizRoom room, ulong messageId, string title, string body,
        int seconds, string label, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        var duration = TimeSpan.FromSeconds(seconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = Math.Max(0, (int)Math.Ceiling((duration - clock.GetElapsedTime(started)).TotalSeconds));
            await room.EditEmbedAsync(messageId, title,
                $"{(remaining <= 3 ? "🚨" : "⏱️")} {label}: **{remaining}초**\n\n{body}",
                remaining <= 3 ? 0xE67E22u : 0x3498DBu, ct);
            if (remaining == 0) return;
            // API 응답 대기 시간을 빼고 다음 초 경계까지만 기다립니다.
            var untilNext = TimeSpan.FromSeconds(seconds - remaining + 1) - clock.GetElapsedTime(started);
            if (untilNext > TimeSpan.Zero) await delay(untilNext, ct);
        }
    }
}
