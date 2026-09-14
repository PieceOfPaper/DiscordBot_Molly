using System.Collections.Concurrent;
using Molly.Runes;

namespace Molly.Quiz;

public enum TrueFalseTopic
{
    Season2Rune,
    Season2WeaponRune,
    Season2ArmorRune,
    Season2EmblemRune,
    Season2AccessoryRune,
    Season2NonAccessoryRune
}

public sealed record TrueFalseQuestion(string Prompt, bool IsTrue, string Answer);

public static class TrueFalseQuestions
{
    public const string TrueEmoji = "✅";
    public const string FalseEmoji = "❌";

    public static string Description(TrueFalseTopic topic) => topic switch
    {
        TrueFalseTopic.Season2Rune => "시즌 2 룬의 이름과 효과가 서로 맞는지 판단해주세요.",
        TrueFalseTopic.Season2WeaponRune => "시즌 2 무기 룬의 이름과 효과가 서로 맞는지 판단해주세요.",
        TrueFalseTopic.Season2ArmorRune => "시즌 2 방어구 룬의 이름과 효과가 서로 맞는지 판단해주세요.",
        TrueFalseTopic.Season2EmblemRune => "시즌 2 앰블럼 룬의 이름과 효과가 서로 맞는지 판단해주세요.",
        TrueFalseTopic.Season2AccessoryRune => "시즌 2 장신구 룬의 이름과 효과가 서로 맞는지 판단해주세요.",
        TrueFalseTopic.Season2NonAccessoryRune => "시즌 2 장신구 룬을 제외한 이름과 효과가 서로 맞는지 판단해주세요.",
        _ => throw new ArgumentException("지원하지 않는 진혹거퀴즈 문제종목입니다.")
    };

    public static IReadOnlyList<TrueFalseQuestion> Pick(TrueFalseTopic topic, IEnumerable<RuneData> runes, int count)
    {
        _ = Description(topic);
        if (count < 1) throw new ArgumentException("문제수는 최소 1개입니다.");
        var pool = runes.Where(r => r.Season == 2 && !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Effect))
            .Where(r => topic switch
            {
                TrueFalseTopic.Season2WeaponRune => r.Category == "무기",
                TrueFalseTopic.Season2ArmorRune => r.Category == "방어구",
                TrueFalseTopic.Season2EmblemRune => r.Category == "앰블럼",
                TrueFalseTopic.Season2AccessoryRune => r.Category == "장신구",
                TrueFalseTopic.Season2NonAccessoryRune => r.Category != "장신구",
                _ => true
            }).ToArray();
        if (pool.Length == 0) throw new ArgumentException("현재 출제 가능한 룬이 없습니다.");

        Random.Shared.Shuffle(pool);
        var questions = new List<TrueFalseQuestion>();
        foreach (var rune in pool.Take(Math.Min(count, pool.Length)))
        {
            var alternatives = pool.Where(other => other.Category == rune.Category &&
                !string.Equals(other.Effect.Trim(), rune.Effect.Trim(), StringComparison.Ordinal)).ToArray();
            var isTrue = alternatives.Length == 0 || Random.Shared.Next(2) == 0;
            var effect = isTrue ? rune.Effect.Trim() : alternatives[Random.Shared.Next(alternatives.Length)].Effect.Trim();
            var name = rune.Name.TrimEnd('+').Trim();
            var prompt = $"🏷️ 룬 이름: **{name}**\n✨ 효과: {effect}";
            questions.Add(new TrueFalseQuestion(prompt, isTrue, $"{name} / {effect}"));
        }
        return questions.AsReadOnly();
    }
}

// 문제마다 참가자의 첫 버튼 선택 하나만 기록합니다.
public sealed class TrueFalseRound
{
    private readonly object gate = new();
    private readonly bool isTrue;
    private readonly long started;
    private readonly TimeSpan limit;
    private readonly TimeProvider clock;
    private readonly Dictionary<ulong, bool> choices = new();

    public TrueFalseRound(bool isTrue, TimeSpan limit, TimeProvider? clock = null)
    {
        if (limit <= TimeSpan.Zero) throw new ArgumentException("제한시간을 확인하세요.");
        this.isTrue = isTrue;
        this.limit = limit;
        this.clock = clock ?? TimeProvider.System;
        started = this.clock.GetTimestamp();
    }

    public TrueFalseChoiceResult Submit(ulong userId, bool selectedTrue)
    {
        lock (gate)
        {
            if (clock.GetElapsedTime(started) >= limit) return TrueFalseChoiceResult.Closed;
            if (choices.ContainsKey(userId)) return TrueFalseChoiceResult.AlreadySelected;
            choices[userId] = selectedTrue;
            return TrueFalseChoiceResult.Accepted;
        }
    }

    public IReadOnlyList<ulong> CloseAndGetWinners()
    {
        lock (gate)
        {
            return choices.Where(x => x.Value == isTrue).Select(x => x.Key).OrderBy(x => x).ToArray();
        }
    }
}

public enum TrueFalseChoiceResult { Accepted, AlreadySelected, Closed }

public sealed class TrueFalseQuizService
{
    private readonly ConcurrentDictionary<ulong, Session> sessions = new();
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<string> log;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stopping = new();

    public TrueFalseQuizService(Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? log = null, TimeProvider? clock = null)
    {
        this.delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
        this.log = log ?? Console.WriteLine;
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<QuizStartResult> StartAsync(ulong guildId, TrueFalseTopic topic, IReadOnlyList<TrueFalseQuestion> questions, int seconds,
        Func<CancellationToken, Task<IQuizRoom>> createRoom, Func<ulong, CancellationToken, Task> announce, ulong returnChannelId)
    {
        if (seconds < 1 || questions.Count < 1) throw new ArgumentException("문제수와 제한시간은 최소 1입니다.");
        var session = new Session(stopping.Token);
        if (!QuizGameRegistry.TryReserve(guildId, session.RoomReady))
        {
            var existing = QuizGameRegistry.ExistingRoom(guildId);
            return new(false, existing is null ? 0 : await existing);
        }
        if (!sessions.TryAdd(guildId, session)) throw new InvalidOperationException("퀴즈 세션을 만들지 못했습니다.");
        try
        {
            var ct = session.Stop.Token;
            session.Room = await createRoom(ct);
            session.RoomReady.TrySetResult(session.Room.Id);
            await session.Room.SendEmbedAsync("🎮 진혹거퀴즈 참가 안내",
                $"📚 {TrueFalseQuestions.Description(topic)}\n\n🔢 문제수: {questions.Count}개\n⏱️ 문제당 제한시간: {seconds}초\n\n✅ **진실** / ❌ **거짓** 버튼 중 하나를 눌러주세요.\n첫 선택은 변경할 수 없으며, 제한시간이 끝날 때 정답을 고른 모든 참가자가 1점을 얻습니다.", 0x5865F2, ct);
            await announce(session.Room.Id, ct);
            _ = RunAsync(guildId, session, questions.ToArray(), seconds, returnChannelId);
            return new(true, session.Room.Id);
        }
        catch
        {
            Release(guildId, session);
            throw;
        }
    }

    public TrueFalseChoiceResult SubmitChoice(ulong guildId, ulong channelId, ulong messageId, ulong userId, bool selectedTrue)
    {
        if (sessions.TryGetValue(guildId, out var session) && session.Room?.Id == channelId && session.MessageId == messageId)
            return session.Round?.Submit(userId, selectedTrue) ?? TrueFalseChoiceResult.Closed;
        return TrueFalseChoiceResult.Closed;
    }

    public bool ForceStop(ulong guildId)
    {
        if (!sessions.TryGetValue(guildId, out var session)) return false;
        session.Forced = true;
        session.Stop.Cancel();
        return true;
    }

    public void CancelChannel(ulong channelId) { foreach (var session in sessions.Values) if (session.Room?.Id == channelId) session.Stop.Cancel(); }
    public async Task StopAsync() { await stopping.CancelAsync(); await Task.WhenAll(sessions.Values.Select(x => x.Done.Task)); }

    private async Task RunAsync(ulong guildId, Session session, TrueFalseQuestion[] questions, int seconds, ulong returnChannelId)
    {
        var scores = new Dictionary<ulong, int>();
        var room = session.Room!;
        var ct = session.Stop.Token;
        try
        {
            await QuizCountdown.RunAsync(room, SpeedQuizService.StartDelaySeconds, "진혹거퀴즈 시작까지", false, delay, ct, clock);
            for (var index = 0; index < questions.Length; index++)
            {
                var question = questions[index];
                var description = $"다음 이름과 효과가 서로 맞을까요?\n\n{question.Prompt}\n\n✅ **진실** / ❌ **거짓**\n⏱️ 제한시간: **{seconds}초**";
                session.MessageId = await room.SendEmbedWithButtonsAsync($"🧩 문제 {index + 1}/{questions.Length}", description, 0x3498DB, ct);
                var round = new TrueFalseRound(question.IsTrue, TimeSpan.FromSeconds(seconds), clock);
                session.Round = round;
                using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var countdown = QuizCountdown.RunAsync(room, seconds, $"문제 {index + 1} 남은 시간", true, delay, timerStop.Token, clock);
                var timeout = delay(TimeSpan.FromSeconds(seconds), timerStop.Token);
                await Task.WhenAny(timeout, countdown);
                session.Round = null;
                await timerStop.CancelAsync();
                try { await countdown; } catch (OperationCanceledException) when (timerStop.IsCancellationRequested) { }
                var winners = round.CloseAndGetWinners();
                foreach (var winner in winners) scores[winner] = scores.GetValueOrDefault(winner) + 1;
                var answer = question.IsTrue ? "✅ 진실" : "❌ 거짓";
                var outcome = winners.Count == 0 ? "⏰ 정답자가 없어요." : $"🎉 정답: {string.Join(", ", winners.Select(x => $"<@{x}>"))} (+1점)";
                await room.SendEmbedAsync("📣 문제 결과", $"{outcome}\n\n정답: **{answer}**\n📖 출제 내용: {question.Answer}" +
                    (index + 1 < questions.Length ? $"\n\n{SpeedQuizService.FormatScores(scores)}" : ""), winners.Count > 0 ? 0x57F287u : 0xED4245u, ct);
                if (index + 1 < questions.Length) await QuizCountdown.RunAsync(room, SpeedQuizService.BetweenQuestionsSeconds, "다음 문제까지", false, delay, ct, clock);
            }
            await FinishAsync(room, "🏁 진혹거퀴즈 종료", Winners(scores), scores, returnChannelId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (session.Forced) await FinishAsync(room, "🛑 진혹거퀴즈 강제 종료", "종료 요청으로 퀴즈가 종료되었습니다.\n\n" + Winners(scores), scores, returnChannelId, new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);
        }
        catch (Exception ex)
        {
            log($"[진혹거퀴즈] 진행 실패: {ex.Message}");
            try { await room.SendAsync("진혹거퀴즈를 계속 진행할 수 없어요. 봇의 반응 추가와 메시지 기록 보기 권한을 확인해주세요.", CancellationToken.None); }
            catch (Exception reportEx) { log($"[진혹거퀴즈] 권한 안내 실패: {reportEx.Message}"); }
        }
        finally { Release(guildId, session); }
    }

    private static string Winners(IReadOnlyDictionary<ulong, int> scores) => scores.Count == 0 ? "정답자가 없어 우승자가 없습니다." :
        $"🏆 {(scores.Count(x => x.Value == scores.Values.Max()) > 1 ? "공동 우승" : "우승")}: " + string.Join(", ", scores.Where(x => x.Value == scores.Values.Max()).Select(x => $"<@{x.Key}>")) + $" ({scores.Values.Max()}점)";
    private async Task FinishAsync(IQuizRoom room, string title, string result, IReadOnlyDictionary<ulong, int> scores, ulong returnChannelId, CancellationToken ct)
    {
        await room.MarkEndedAsync(ct);
        await room.SendEmbedAsync(title, $"{result}\n\n📊 {SpeedQuizService.FormatScores(scores)}\n\n🔒 이 채널은 **30초 뒤 보기 전용으로 잠깁니다.**\n↩️ 돌아가기: <#{returnChannelId}>", 0xF1C40F, ct);
        await QuizCountdown.RunAsync(room, 30, "이 채널이 잠기기까지", false, delay, ct, clock);
        await room.SendAsync("🔒 이 몰리 놀이터 채널은 닫혔습니다.", ct);
        await room.LockAsync(ct);
    }
    private void Release(ulong guildId, Session session) { sessions.TryRemove(new KeyValuePair<ulong, Session>(guildId, session)); session.RoomReady.TrySetResult(0); QuizGameRegistry.Release(guildId, session.RoomReady); session.Done.TrySetResult(); }
    private sealed class Session(CancellationToken appToken)
    {
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        public TaskCompletionSource<ulong> RoomReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IQuizRoom? Room; public TrueFalseRound? Round; public ulong MessageId; public bool Forced;
    }
}
