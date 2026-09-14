using Molly.Quiz;

internal static class QuizFlowTests
{
    public static async Task RunAsync()
    {
        Assert(QuizQuestions.ConsonantHint("거대한 분노+") == "ㄱㄷㅎ ㅂㄴ", "초성 힌트 공백 보존·기호 제거");
        Assert(QuizQuestions.ConsonantHint("폭염+!") == "ㅍㅇ", "초성 힌트 특수문자 제거");
        Assert(QuizQuestions.ConsonantHint("까  쌍").Normalize() == "ㄲ  ㅆ", "쌍자음 및 연속 공백 보존");
        Assert(QuizQuestions.ConsonantHint("폭염".Normalize(System.Text.NormalizationForm.FormD)) == "ㅍㅇ", "분해된 한글 초성 변환");
        var normalClock = new Clock();
        var normalRoom = new Room(normalClock);
        await QuizCountdown.RunAsync(normalRoom, 30, "시작까지", false, normalClock.Delay, default, normalClock);
        Assert(normalClock.Seconds == 30, "5초 단위 카운트다운도 전체 시간 대기");
        Assert(normalRoom.Messages.SequenceEqual(new[] { "⏱️ **시작까지 30초**", "⏱️ **시작까지 25초**", "⏱️ **시작까지 20초**", "⏱️ **시작까지 15초**", "⏱️ **시작까지 10초**", "⏱️ **시작까지 5초**" }), "시작·문제 사이 알림은 5초 단위 일반 메시지");
        Assert(normalRoom.DeletedMessageIds.SequenceEqual(new ulong[] { 1, 2, 3, 4, 5, 6 }), "남은 시간 메시지는 다음 안내와 종료 시 삭제");

        var dramaticClock = new Clock();
        var dramaticRoom = new Room(dramaticClock);
        await QuizCountdown.RunAsync(dramaticRoom, 10, "문제 1 남은 시간", true, dramaticClock.Delay, default, dramaticClock);
        Assert(dramaticClock.Seconds == 10, "문제 카운트다운 전체 시간 대기");
        Assert(dramaticRoom.Messages.Count == 5 && dramaticRoom.Messages.Take(2).All(x => x.Contains("초")) &&
               dramaticRoom.Messages.Skip(2).All(x => x.Contains("마지막")), "문제만 마지막 3초 강조 메시지");
        Assert(dramaticRoom.Messages[^1] == "🚨🚨 **마지막 1초!** 🚨🚨", "마지막 1초 한 줄 긴장감 강조");
        Assert(dramaticRoom.DeletedMessageIds.SequenceEqual(new ulong[] { 1, 2, 3, 4, 5 }), "마지막 3·2·1 메시지도 순차 삭제");

        var lagClock = new Clock();
        var lagRoom = new Room(lagClock) { Send = _ => lagClock.Advance(.25) };
        await QuizCountdown.RunAsync(lagRoom, 10, "남은 시간", false, lagClock.Delay, default, lagClock);
        Assert(lagClock.Seconds == 10, "메시지 전송 지연 누적 없이 마감 시각 유지");

        var deletedCountdownClock = new Clock();
        var deletedCountdownRoom = new Room(deletedCountdownClock) { DeleteFailure = new IOException("unknown message") };
        await QuizCountdown.RunAsync(deletedCountdownRoom, 10, "남은 시간", false, deletedCountdownClock.Delay, default, deletedCountdownClock);
        Assert(deletedCountdownClock.Seconds == 10, "이미 삭제된 카운트다운 메시지도 출제를 중단하지 않음");

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
            room.Send = text =>
            {
                if (force && text.Contains("문제 1 남은 시간") && !submitted)
                {
                    submitted = service.Submit(1, room.Id, 42, 999, "정답");
                    service.ForceStop(1);
                }
            };
            await service.StartAsync(1, QuizTopic.Season2RuneEffect,
                new[] { new QuizQuestion("효과", "정답"), new QuizQuestion("효과2", "정답2") }, 6,
                _ => Task.FromResult<IQuizRoom>(room), (_, _) => Task.CompletedTask, 99);
            await room.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (force)
                Assert(submitted && room.Final.Contains("<@42>: 1점") && room.Ended && room.Locked, "정답 직후 강제 종료 점수 보존·채널 잠금");
            else
                Assert(room.QuestionTimes.SequenceEqual(new[] { 30d, 46d }) && room.Final.Contains("우승자가 없습니다") &&
                       room.Final.Contains("30초 뒤 보기 전용") && room.Final.Contains("<#99>") && room.Messages.Last() == "🔒 이 몰리 놀이터 채널은 닫혔습니다." && room.Ended && room.Locked,
                    "30초 준비·6초 문제·10초 휴식·종료 채널 잠금·닫힘 안내");
            await service.StopAsync();
        }
        var failureClock = new Clock();
        var failureRoom = new Room(failureClock) { Send = _ => throw new IOException("send failed") };
        var errors = new List<string>();
        var failedService = new SpeedQuizService((duration, ct) => duration.TotalSeconds == 6 ? Task.Delay(Timeout.Infinite, ct) : failureClock.Delay(duration, ct), errors.Add, failureClock);
        await failedService.StartAsync(2, QuizTopic.Season2RuneEffect, new[] { new QuizQuestion("효과", "정답") }, 6,
            _ => Task.FromResult<IQuizRoom>(failureRoom), (_, _) => Task.CompletedTask, 99);
        await failedService.StopAsync();
        Assert(errors.Any(x => x.Contains("send failed")), "카운트다운 메시지 전송 실패 관찰 및 종료");

        var lockFailureClock = new Clock();
        var lockFailureRoom = new Room(lockFailureClock) { LockFailure = new IOException("missing permissions") };
        var lockFailureErrors = new List<string>();
        var lockFailureService = new SpeedQuizService((duration, ct) => duration.TotalSeconds == 6 ? Task.Delay(Timeout.Infinite, ct) : lockFailureClock.Delay(duration, ct), lockFailureErrors.Add, lockFailureClock);
        await lockFailureService.StartAsync(3, QuizTopic.Season2RuneEffect, new[] { new QuizQuestion("효과", "정답") }, 6,
            _ => Task.FromResult<IQuizRoom>(lockFailureRoom), (_, _) => Task.CompletedTask, 99);
        await lockFailureRoom.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(lockFailureRoom.Messages.Any(x => x.Contains("채널 자동 잠금에 실패")) && lockFailureErrors.Any(x => x.Contains("종료 채널 잠금 실패")),
            "채널 잠금 실패는 종료 결과와 분리해 안내");
        await lockFailureService.StopAsync();

        var exclusiveService = new SpeedQuizService((_, ct) => Task.Delay(Timeout.Infinite, ct));
        var exclusiveRoom = new Room(new Clock());
        await exclusiveService.StartAsync(4, QuizTopic.Season2RuneEffect, new[] { new QuizQuestion("효과", "정답") }, 1,
            _ => Task.FromResult<IQuizRoom>(exclusiveRoom), (_, _) => Task.CompletedTask, 99);
        var blocked = await exclusiveService.StartAsync(4, "자음퀴즈", "자음을 보고 맞혀주세요.", new[] { new QuizQuestion("종류: **NPC**\n🔤 자음: **ㄷ**", "답") }, 1,
            _ => Task.FromResult<IQuizRoom>(new Room(new Clock())), (_, _) => Task.CompletedTask, 99);
        Assert(!blocked.Started && blocked.ChannelId == exclusiveRoom.Id, "길드 내 스피드퀴즈·자음퀴즈 동시 진행 차단");
        await exclusiveService.StopAsync();
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
        public List<string> Messages = new();
        public List<ulong> DeletedMessageIds = new();
        public List<double> QuestionTimes = new();
        public Action<string>? Send;
        public string Final = "";
        public bool Ended;
        public bool Locked;
        public Exception? LockFailure;
        public Exception? DeleteFailure;
        public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong id;
        public Task<ulong> SendAsync(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Messages.Add(text);
            Send?.Invoke(text);
            if (text.Contains("채널 자동 잠금에 실패")) Finished.TrySetResult();
            return Task.FromResult(++id);
        }
        public Task DeleteAsync(ulong messageId, CancellationToken ct)
        {
            if (DeleteFailure != null) throw DeleteFailure;
            DeletedMessageIds.Add(messageId);
            return Task.CompletedTask;
        }
        public Task<ulong> SendEmbedAsync(string title, string description, uint color, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (title.StartsWith("🧩")) QuestionTimes.Add(clock.Seconds);
            if (title.Contains("종료")) Final = description;
            return Task.FromResult(++id);
        }
        public Task<ulong> SendEmbedWithButtonsAsync(string title, string description, uint color, CancellationToken ct)
            => SendEmbedAsync(title, description, color, ct);
        public Task MarkEndedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Ended = true;
            return Task.CompletedTask;
        }
        public Task LockAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (LockFailure != null) throw LockFailure;
            Locked = true;
            Finished.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
