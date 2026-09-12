using System.Collections.Concurrent;

namespace Molly.Quiz;

public interface IQuizRoom
{
    ulong Id { get; }
    Task<ulong> SendAsync(string text, CancellationToken ct);
    Task<ulong> SendEmbedAsync(string title, string description, uint color, CancellationToken ct);
    Task EditEmbedAsync(ulong messageId, string title, string description, uint color, CancellationToken ct);
}

public sealed record QuizStartResult(bool Started, ulong ChannelId);

public sealed class SpeedQuizService
{
    public const int StartDelaySeconds = 30;
    public const int BetweenQuestionsSeconds = 10;
    private readonly ConcurrentDictionary<ulong, Session> sessions = new();
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<string> log;
    private readonly CancellationTokenSource stopping = new();

    public SpeedQuizService(Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? log = null)
    {
        this.delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
        this.log = log ?? Console.WriteLine;
    }

    public async Task<QuizStartResult> StartAsync(ulong guildId, QuizTopic topic, IReadOnlyList<QuizQuestion> questions,
        int seconds, Func<CancellationToken, Task<IQuizRoom>> createRoom,
        Func<ulong, CancellationToken, Task> announce)
    {
        if (seconds < 1 || questions.Count < 1) throw new ArgumentException("문제수와 제한시간은 최소 1입니다.");
        var description = QuizQuestions.Description(topic);
        // 호출자가 원본 목록을 바꾸거나 Sheets가 갱신되어도 진행 중 문제는 고정합니다.
        var selected = questions.ToArray();
        var session = new Session(stopping.Token);
        if (!sessions.TryAdd(guildId, session))
        {
            session.Stop.Dispose();
            if (sessions.TryGetValue(guildId, out var existing))
                return new(false, await existing.RoomReady.Task);
            return new(false, 0);
        }
        try
        {
            var ct = session.Stop.Token;
            ct.ThrowIfCancellationRequested();
            session.Room = await createRoom(ct);
            session.RoomReady.TrySetResult(session.Room.Id);
            var introTitle = "🎮 스피드퀴즈 참가 안내";
            var introDescription = $"📚 {description}\n\n🔢 문제수: {selected.Length}개\n⏱️ 문제당 제한시간: {seconds}초\n🏆 가장 먼저 맞힌 한 명에게 1점\n🔤 띄어쓰기와 영문 대소문자는 구분하지 않습니다.";
            var introMessageId = await session.Room.SendEmbedAsync(introTitle, introDescription, 0x5865F2, ct);
            await announce(session.Room.Id, ct);
            _ = RunAsync(guildId, session, selected, seconds, introMessageId, introTitle, introDescription);
            return new(true, session.Room.Id);
        }
        catch
        {
            await ReportFailureAsync(session, "퀴즈 준비에 실패하여 취소되었습니다. 채널 권한을 확인한 뒤 다시 시작해주세요.");
            Release(guildId, session);
            throw;
        }
    }

    public bool Submit(ulong guildId, ulong channelId, ulong userId, ulong messageId, string content)
    {
        if (!sessions.TryGetValue(guildId, out var session) || session.Room?.Id != channelId || session.Stop.IsCancellationRequested) return false;
        return Volatile.Read(ref session.Round)?.Submit(userId, messageId, content) ?? false;
    }

    public bool ForceStop(ulong guildId)
    {
        if (!sessions.TryGetValue(guildId, out var session)) return false;
        session.ForceEnd = true;
        session.Stop.Cancel();
        return true;
    }

    public void CancelChannel(ulong channelId)
    {
        foreach (var session in sessions.Values)
            if (session.Room?.Id == channelId) session.Stop.Cancel();
    }

    public async Task StopAsync()
    {
        await stopping.CancelAsync();
        await Task.WhenAll(sessions.Values.Select(s => s.Done.Task));
    }

    private async Task RunAsync(ulong guildId, Session session, QuizQuestion[] questions, int seconds, ulong introMessageId, string introTitle, string introDescription)
    {
        var ct = session.Stop.Token;
        var room = session.Room!;
        var scores = new Dictionary<ulong, int>();
        try
        {
            await RunCountdownAsync(room, introMessageId, introTitle, introDescription, StartDelaySeconds, new TaskCompletionSource<ulong?>().Task, ct, "시작까지");
            for (var i = 0; i < questions.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var question = questions[i];
                var questionTitle = $"🧩 문제 {i + 1}/{questions.Length}";
                var questionDescription = $"⏱️ 남은 시간: **{seconds}초**\n\n{question.Prompt}";
                var messageId = await room.SendEmbedAsync(questionTitle, questionDescription, 0x3498DB, ct);
                var round = new QuizRound(question.Answer, messageId, TimeSpan.FromSeconds(seconds));
                Volatile.Write(ref session.Round, round);
                using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    var timeout = delay(TimeSpan.FromSeconds(seconds), timerStop.Token);
                    var countdown = RunCountdownAsync(room, messageId, questionTitle, question.Prompt, seconds, round.Completion, timerStop.Token);
                    await Task.WhenAny(round.Completion, timeout);
                    ct.ThrowIfCancellationRequested();
                    round.Close();
                    var winner = await round.Completion;
                    if (winner is ulong userId) scores[userId] = scores.GetValueOrDefault(userId) + 1;
                    Volatile.Write(ref session.Round, null);
                    var outcome = winner is ulong id ? $"🎉 <@{id}>님 정답! {round.ElapsedSeconds:0.0}초 만에 맞혀 1점을 얻었습니다." : "⏰ 아무도 맞추지 못했어요.";
                    var next = "";
                    var scoreText = i + 1 < questions.Length ? $"\n{FormatScores(scores)}" : "";
                    var resultTitle = "📣 문제 결과";
                    var resultDescription = $"{outcome}\n\n✅ 정답: {question.Answer}{scoreText}{next}";
                    var resultMessageId = await room.SendEmbedAsync(resultTitle, resultDescription, winner is ulong ? 0x57F287u : 0xED4245u, ct);
                    if (i + 1 < questions.Length)
                        await RunCountdownAsync(room, resultMessageId, resultTitle, resultDescription, BetweenQuestionsSeconds, new TaskCompletionSource<ulong?>().Task, ct, "다음 문제까지");
                }
                finally
                {
                    round.Close();
                    Volatile.Write(ref session.Round, null);
                    await timerStop.CancelAsync();
                }
                if (i + 1 < questions.Length) await delay(TimeSpan.FromSeconds(BetweenQuestionsSeconds), ct);
            }
            var winners = scores.Count == 0 ? "정답자가 없어 우승자가 없습니다." :
                $"{(scores.Count(x => x.Value == scores.Values.Max()) > 1 ? "공동 우승" : "우승")}: " +
                string.Join(", ", scores.Where(x => x.Value == scores.Values.Max()).OrderBy(x => x.Key).Select(x => $"<@{x.Key}>")) + $" ({scores.Values.Max()}점)";
            await room.SendEmbedAsync("🏁 스피드퀴즈 종료", $"{winners}\n\n📊 {FormatScores(scores)}", 0xF1C40F, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (session.ForceEnd && session.Room != null)
            {
                var winners = scores.Count == 0 ? "정답자가 없어 우승자가 없습니다." :
                    $"{(scores.Count(x => x.Value == scores.Values.Max()) > 1 ? "공동 우승" : "현재 우승")}: " +
                    string.Join(", ", scores.Where(x => x.Value == scores.Values.Max()).Select(x => $"<@{x.Key}>")) + $" ({scores.Values.Max()}점)";
                await room.SendEmbedAsync("🛑 스피드퀴즈 강제 종료", $"관리자에 의해 퀴즈가 종료되었습니다.\n\n🏆 {winners}\n\n📊 {FormatScores(scores)}", 0xE74C3C, CancellationToken.None);
            }
            else await ReportFailureAsync(session, "스피드퀴즈가 중단되었습니다. 새 게임은 /스피드퀴즈로 시작해주세요.");
        }
        catch (Exception ex)
        {
            log($"[스피드퀴즈] 진행 실패: {ex.Message}");
            await ReportFailureAsync(session, "메시지를 전송하지 못해 스피드퀴즈를 종료했습니다. 채널 권한을 확인해주세요.");
        }
        finally { Release(guildId, session); }
    }

    private async Task RunCountdownAsync(IQuizRoom room, ulong messageId, string title, string body, int seconds, Task<ulong?> completed, CancellationToken ct, string label = "남은 시간")
    {
        var elapsed = 0;
        while (!ct.IsCancellationRequested && !completed.IsCompleted && elapsed < seconds)
        {
            var remainingBefore = seconds - elapsed;
            if (remainingBefore <= 3)
            {
                for (var number = remainingBefore; number >= 1 && !completed.IsCompleted; number--)
                {
                    await room.EditEmbedAsync(messageId, title, $"🚨 {label}: **{number}초**\n\n{body}", 0xE67E22, ct);
                    if (number > 1) await delay(TimeSpan.FromSeconds(1), ct);
                }
                return;
            }
            var step = 1;
            await delay(TimeSpan.FromSeconds(step), ct);
            if (completed.IsCompleted || ct.IsCancellationRequested) return;
            elapsed += step;
            var remaining = seconds - elapsed;
            if (remaining > 3)
            {
                await room.EditEmbedAsync(messageId, title, $"⏱️ {label}: **{remaining}초**\n\n{body}", 0x95A5A6, ct);
            }
            else return;
        }
    }

    public static string FormatScores(IReadOnlyDictionary<ulong, int> scores)
        => scores.Count == 0 ? "현재 점수: 아직 득점자가 없습니다." : "현재 점수\n" +
            string.Join("\n", scores.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => $"<@{x.Key}>: {x.Value}점"));

    private async Task ReportFailureAsync(Session session, string text)
    {
        if (session.Room == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await session.Room.SendAsync(text, timeout.Token); }
        catch (Exception ex) { log($"[스피드퀴즈] 종료 안내 실패: {ex.Message}"); }
    }

    private void Release(ulong guildId, Session session)
    {
        Volatile.Read(ref session.Round)?.Close();
        Volatile.Write(ref session.Round, null);
        session.RoomReady.TrySetResult(0);
        sessions.TryRemove(new KeyValuePair<ulong, Session>(guildId, session));
        session.Done.TrySetResult();
        // 채널 삭제 이벤트가 같은 Session을 참조할 수 있어 CTS는 여기서 Dispose하지 않습니다.
    }

    private sealed class Session(CancellationToken appToken)
    {
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        public TaskCompletionSource<ulong> RoomReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IQuizRoom? Room;
        public QuizRound? Round;
        public bool ForceEnd;
    }
}
