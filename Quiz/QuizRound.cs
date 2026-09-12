namespace Molly.Quiz;

// 정답 제출과 시간 초과는 같은 잠금 안에서 판정하여 단 한 명만 승리합니다.
public sealed class QuizRound
{
    private readonly object gate = new();
    private readonly string answer;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly TimeSpan limit;
    private readonly ulong questionMessageId;
    private readonly TaskCompletionSource<ulong?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ulong?> Completion => completed.Task;
    private double? finishedSeconds;
    public double ElapsedSeconds { get { lock (gate) return finishedSeconds ?? clock.GetElapsedTime(started).TotalSeconds; } }

    public QuizRound(string answer, ulong questionMessageId, TimeSpan limit, TimeProvider? clock = null)
    {
        this.answer = QuizQuestions.NormalizeAnswer(answer);
        if (this.answer.Length == 0 || limit <= TimeSpan.Zero) throw new ArgumentException("정답과 제한시간을 확인하세요.");
        this.questionMessageId = questionMessageId;
        this.limit = limit;
        this.clock = clock ?? TimeProvider.System;
        started = this.clock.GetTimestamp();
    }

    public bool Submit(ulong userId, ulong messageId, string text)
    {
        lock (gate)
        {
            if (completed.Task.IsCompleted || messageId <= questionMessageId) return false;
            if (clock.GetElapsedTime(started) >= limit)
            {
                completed.TrySetResult(null);
                return false;
            }
            if (QuizQuestions.NormalizeAnswer(text) != answer) return false;
            finishedSeconds = clock.GetElapsedTime(started).TotalSeconds;
            return completed.TrySetResult(userId);
        }
    }

    public void Close()
    {
        lock (gate) completed.TrySetResult(null);
    }
}
