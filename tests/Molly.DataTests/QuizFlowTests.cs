using Molly.Quiz;

internal static class QuizFlowTests
{
    public static async Task RunAsync()
    {
        Assert(QuizQuestions.ConsonantHint("거대한 분노+") == "ㄱㄷㅎ ㅂㄴ", "초성 힌트 공백 보존·기호 제거");
        Assert(QuizQuestions.ConsonantHint("폭염+!") == "ㅍㅇ", "초성 힌트 특수문자 제거");
        Assert(QuizQuestions.ConsonantHint("까  쌍").Normalize() == "ㄲ  ㅆ", "쌍자음 및 연속 공백 보존");
        Assert(QuizQuestions.ConsonantHint("폭염".Normalize(System.Text.NormalizationForm.FormD)) == "ㅍㅇ", "분해된 한글 초성 변환");
        foreach (var seconds in new[] { 1, 2, 3, 10, 30 })
        {
            var clock = new Clock();
            var room = new Room(clock);
            await QuizCountdown.RunAsync(room, 1, "안내", "본문", seconds, "남은 시간", clock.Delay, default, clock);
            Assert(clock.Seconds == seconds, $"{seconds}초 전체 대기");
            Assert(room.Edits.Count == seconds + 1 && room.Edits[^1].Contains("**0초**"), "모든 초와 0초 표시");
        }
        var lagClock = new Clock();
        var lagRoom = new Room(lagClock) { Edit = _ => lagClock.Advance(.25) };
        await QuizCountdown.RunAsync(lagRoom, 1, "문제", "본문", 10, "남은 시간", lagClock.Delay, default, lagClock);
        Assert(lagClock.Seconds == 10.25, "메시지 지연 누적 없이 마감 시각 유지");

        var raceClock = new Clock();
        var round = new QuizRound("정답", 1, TimeSpan.FromSeconds(30), raceClock);
        raceClock.Advance(2);
        var accepted = 0;
        Parallel.For(0, 50, i => { if (round.Submit((ulong)i + 1, (ulong)i + 2, "정답")) Interlocked.Increment(ref accepted); });
        raceClock.Advance(5);
        Assert(accepted == 1 && round.ElapsedSeconds == 2, "동시 정답 한 명 및 정답 시각 고정");

        foreach (var force in new[] { false, true })
        {
            var clock = new Clock();
            var room = new Room(clock);
            var service = new SpeedQuizService((duration, ct) => duration.TotalSeconds == 6
                ? Task.Delay(Timeout.Infinite, ct) : clock.Delay(duration, ct), clock: clock);
            var submitted = false;
            room.Edit = title =>
            {
                if (force && title.StartsWith("🧩") && !submitted)
                {
                    submitted = service.Submit(1, room.Id, 42, 999, "정답");
                    service.ForceStop(1);
                }
            };
            await service.StartAsync(1, QuizTopic.Season2RuneEffect,
                new[] { new QuizQuestion("효과", "정답"), new QuizQuestion("효과2", "정답2") }, 6,
                _ => Task.FromResult<IQuizRoom>(room), (_, _) => Task.CompletedTask);
            await room.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (force)
                Assert(submitted && room.Final.Contains("<@42>: 1점"), "정답 직후 강제 종료 점수 보존");
            else
                Assert(room.QuestionTimes.SequenceEqual(new[] { 30d, 46d }) && room.Final.Contains("우승자가 없습니다"),
                    "30초 준비·6초 문제·10초 휴식·정상 종료");
            await service.StopAsync();
        }
        var failureClock = new Clock();
        var failureRoom = new Room(failureClock) { Edit = title => { if (title.StartsWith("🧩")) throw new IOException("edit failed"); } };
        var errors = new List<string>();
        var failedService = new SpeedQuizService((duration, ct) => duration.TotalSeconds == 6 ? Task.Delay(Timeout.Infinite, ct) : failureClock.Delay(duration, ct), errors.Add, failureClock);
        await failedService.StartAsync(2, QuizTopic.Season2RuneEffect, new[] { new QuizQuestion("효과", "정답") }, 6,
            _ => Task.FromResult<IQuizRoom>(failureRoom), (_, _) => Task.CompletedTask);
        await failedService.StopAsync();
        Assert(errors.Any(x => x.Contains("edit failed")), "Embed 수정 실패 관찰 및 종료");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public double Seconds => TimeSpan.FromTicks(ticks).TotalSeconds;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ticks += duration.Ticks;
            return Task.CompletedTask;
        }
    }

    private sealed class Room(Clock clock) : IQuizRoom
    {
        public ulong Id => 50;
        public List<string> Edits = new();
        public List<double> QuestionTimes = new();
        public Action<string>? Edit;
        public string Final = "";
        public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong id;
        public Task<ulong> SendAsync(string text, CancellationToken ct) => Task.FromResult(++id);
        public Task<ulong> SendEmbedAsync(string title, string description, uint color, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (title.StartsWith("🧩")) QuestionTimes.Add(clock.Seconds);
            if (title.Contains("종료")) { Final = description; Finished.TrySetResult(); }
            return Task.FromResult(++id);
        }
        public Task EditEmbedAsync(ulong messageId, string title, string description, uint color, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Edits.Add(description);
            Edit?.Invoke(title);
            return Task.CompletedTask;
        }
    }
}
