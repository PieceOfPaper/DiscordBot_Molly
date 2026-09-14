using System.Collections.Concurrent;

namespace Molly.Quiz;

public interface IQuizRoom
{
    ulong Id { get; }
    Task<ulong> SendAsync(string text, CancellationToken ct);
    Task DeleteAsync(ulong messageId, CancellationToken ct);
    Task<ulong> SendEmbedAsync(string title, string description, uint color, CancellationToken ct);
    Task MarkEndedAsync(CancellationToken ct);
    Task LockAsync(CancellationToken ct);
}

public sealed record QuizStartResult(bool Started, ulong ChannelId);

public sealed class SpeedQuizService
{
    public const int StartDelaySeconds = 30;
    public const int BetweenQuestionsSeconds = 10;
    private readonly ConcurrentDictionary<ulong, Session> sessions = new();
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<string> log;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stopping = new();

    public SpeedQuizService(Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? log = null, TimeProvider? clock = null)
    {
        this.delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
        this.log = log ?? Console.WriteLine;
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<QuizStartResult> StartAsync(ulong guildId, QuizTopic topic, IReadOnlyList<QuizQuestion> questions,
        int seconds, Func<CancellationToken, Task<IQuizRoom>> createRoom,
        Func<ulong, CancellationToken, Task> announce, ulong returnChannelId, bool showConsonants = false)
        => await StartAsync(guildId, "스피드퀴즈", QuizQuestions.Description(topic), questions, seconds, createRoom, announce, returnChannelId, showConsonants);

    public async Task<QuizStartResult> StartAsync(ulong guildId, string quizName, string description, IReadOnlyList<QuizQuestion> questions,
        int seconds, Func<CancellationToken, Task<IQuizRoom>> createRoom,
        Func<ulong, CancellationToken, Task> announce, ulong returnChannelId, bool showConsonants = false)
    {
        if (seconds < 1 || questions.Count < 1) throw new ArgumentException("문제수와 제한시간은 최소 1입니다.");
        if (string.IsNullOrWhiteSpace(quizName) || string.IsNullOrWhiteSpace(description)) throw new ArgumentException("퀴즈 정보를 확인하세요.");
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
            var introTitle = $"🎮 {quizName} 참가 안내";
            var introDescription = $"📚 {description}\n\n🔢 문제수: {selected.Length}개\n⏱️ 문제당 제한시간: {seconds}초\n🏆 가장 먼저 맞힌 한 명에게 1점\n🔤 띄어쓰기와 영문 대소문자는 구분하지 않습니다.";
            await session.Room.SendEmbedAsync(introTitle, introDescription, 0x5865F2, ct);
            await announce(session.Room.Id, ct);
            _ = RunAsync(guildId, session, quizName, selected, seconds, returnChannelId, showConsonants);
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
        Volatile.Read(ref session.Round)?.Close();
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

    private async Task RunAsync(ulong guildId, Session session, string quizName, QuizQuestion[] questions, int seconds, ulong returnChannelId, bool showConsonants)
    {
        var ct = session.Stop.Token;
        var room = session.Room!;
        var scores = new Dictionary<ulong, int>();
        try
        {
            await QuizCountdown.RunAsync(room, StartDelaySeconds, $"{quizName} 시작까지", false, delay, ct, clock);
            for (var i = 0; i < questions.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var question = questions[i];
                var questionTitle = $"🧩 문제 {i + 1}/{questions.Length}";
                var body = question.Prompt + (showConsonants ? $"\n\n💡 자음 힌트: {QuizQuestions.ConsonantHint(question.Answer)}" : "");
                var questionDescription = $"⏱️ 남은 시간: **{seconds}초**\n\n{body}";
                var messageId = await room.SendEmbedAsync(questionTitle, questionDescription, 0x3498DB, ct);
                var round = new QuizRound(question.Answer, messageId, TimeSpan.FromSeconds(seconds), clock);
                Volatile.Write(ref session.Round, round);
                using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeout = delay(TimeSpan.FromSeconds(seconds), timerStop.Token);
                var countdown = QuizCountdown.RunAsync(room, seconds, $"문제 {i + 1} 남은 시간", true, delay, timerStop.Token, clock);
                try
                {
                    await Task.WhenAny(round.Completion, timeout, countdown);
                }
                finally
                {
                    // 취소나 Embed 수정 실패보다 먼저 이미 확정된 정답을 반영합니다.
                    round.Close();
                    var awarded = await round.Completion;
                    if (awarded is ulong userId) scores[userId] = scores.GetValueOrDefault(userId) + 1;
                    Volatile.Write(ref session.Round, null);
                    await timerStop.CancelAsync();
                    try { await countdown; }
                    catch (OperationCanceledException) when (timerStop.IsCancellationRequested) { }
                    try { await timeout; }
                    catch (OperationCanceledException) when (timerStop.IsCancellationRequested) { }
                }
                ct.ThrowIfCancellationRequested();
                var winner = await round.Completion;
                var outcome = winner is ulong id ? $"🎉 <@{id}>님 정답! {round.ElapsedSeconds:0.0}초 만에 맞혀 1점을 얻었습니다." : "⏰ 아무도 맞추지 못했어요.";
                var scoreText = i + 1 < questions.Length ? $"\n{FormatScores(scores)}" : "";
                var resultTitle = "📣 문제 결과";
                var resultDescription = $"{outcome}\n\n✅ 정답: {question.Answer}{scoreText}";
                await room.SendEmbedAsync(resultTitle, resultDescription, winner is ulong ? 0x57F287u : 0xED4245u, ct);
                if (i + 1 < questions.Length)
                    await QuizCountdown.RunAsync(room, BetweenQuestionsSeconds, "다음 문제까지", false, delay, ct, clock);
            }
            var winners = scores.Count == 0 ? "정답자가 없어 우승자가 없습니다." :
                $"{(scores.Count(x => x.Value == scores.Values.Max()) > 1 ? "공동 우승" : "우승")}: " +
                string.Join(", ", scores.Where(x => x.Value == scores.Values.Max()).OrderBy(x => x.Key).Select(x => $"<@{x.Key}>")) + $" ({scores.Values.Max()}점)";
            await FinishRoomAsync(room, $"🏁 {quizName} 종료", $"{winners}\n\n📊 {FormatScores(scores)}", 0xF1C40F, returnChannelId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (session.ForceEnd && session.Room != null)
            {
                var winners = scores.Count == 0 ? "정답자가 없어 우승자가 없습니다." :
                    $"{(scores.Count(x => x.Value == scores.Values.Max()) > 1 ? "공동 우승" : "현재 우승")}: " +
                    string.Join(", ", scores.Where(x => x.Value == scores.Values.Max()).Select(x => $"<@{x.Key}>")) + $" ({scores.Values.Max()}점)";
                using var finalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { await FinishRoomAsync(room, $"🛑 {quizName} 강제 종료", $"종료 요청으로 퀴즈가 종료되었습니다.\n\n🏆 {winners}\n\n📊 {FormatScores(scores)}", 0xE74C3C, returnChannelId, finalTimeout.Token); }
                catch (Exception ex) { log($"[{quizName}] 최종 결과 전송 실패: {ex.Message}"); }
            }
            else await ReportFailureAsync(session, $"{quizName}가 중단되었습니다. 새 게임을 다시 시작해주세요.");
        }
        catch (Exception ex)
        {
            log($"[{quizName}] 진행 실패: {ex.Message}");
            await ReportFailureAsync(session, "메시지를 전송하지 못해 스피드퀴즈를 종료했습니다. 채널 권한을 확인해주세요.");
        }
        finally { Release(guildId, session); }
    }

    public static string FormatScores(IReadOnlyDictionary<ulong, int> scores)
        => scores.Count == 0 ? "현재 점수: 아직 득점자가 없습니다." : "현재 점수\n" +
            string.Join("\n", scores.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => $"<@{x.Key}>: {x.Value}점"));

    private async Task FinishRoomAsync(IQuizRoom room, string title, string result, uint color, ulong returnChannelId, CancellationToken ct)
    {
        await room.MarkEndedAsync(ct);
        await room.SendEmbedAsync(title,
            $"{result}\n\n🔒 이 채널은 **30초 뒤 보기 전용으로 잠깁니다.**\n↩️ 돌아가기: <#{returnChannelId}>", color, ct);
        await QuizCountdown.RunAsync(room, 30, "이 채널이 잠기기까지", false, delay, ct, clock);
        try
        {
            await room.SendAsync("🔒 이 몰리 놀이터 채널은 닫혔습니다.", ct);
            await room.LockAsync(ct);
        }
        catch (Exception ex)
        {
            log($"[스피드퀴즈] 종료 채널 잠금 실패: {ex.Message}");
            await room.SendAsync("⚠️ 채널 자동 잠금에 실패했습니다. 봇의 역할 관리하기 권한과 몰리 놀이터 카테고리 권한을 확인해주세요.", ct);
        }
    }

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
