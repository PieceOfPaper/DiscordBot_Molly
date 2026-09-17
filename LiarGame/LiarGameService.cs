using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;

namespace Molly.LiarGame;

public sealed class LiarGameService
{
    // 게임 조정 시 이 상수들만 바꾸면 됩니다.
    public const int JoinSeconds = 30;
    public const int QuestionSeconds = 30;
    public const int AnswerVoteSeconds = 30;
    public const int BeforeAccusationSeconds = 30;
    public const int AccusationSeconds = 30;
    public const int LiarGuessSeconds = 30;
    public const int CloseSeconds = 30;
    public const int MinimumPlayers = 3;
    public const int MaximumPlayers = 20;

    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();
    private static readonly (string Topic, string Word)[] Words =
    [
        ("마비노기 모바일", "캠프파이어"), ("마비노기 모바일", "던전"), ("마비노기 모바일", "룬"),
        ("음식", "비빔밥"), ("음식", "김치찌개"), ("음식", "떡볶이"),
        ("동물", "고양이"), ("동물", "펭귄"), ("동물", "코끼리")
    ];

    public async Task<(bool Started, ulong ChannelId)> StartAsync(SocketGuild guild, ulong returnChannelId, ulong hostId)
    {
        var session = new Session(guild.Id, returnChannelId, hostId);
        if (!_sessions.TryAdd(guild.Id, session)) return (false, 0);
        try
        {
            ICategoryChannel category;
            var existingCategory = guild.CategoryChannels.FirstOrDefault(x => x.Name == "몰리 놀이터");
            if (existingCategory is not null) category = existingCategory;
            else category = await guild.CreateCategoryChannelAsync("몰리 놀이터");
            var channel = await guild.CreateTextChannelAsync($"몰리놀이터-라이어-{MobiTime.now:yyyyMMddHHmm}", p =>
            {
                p.CategoryId = category.Id;
                p.PermissionOverwrites = category.PermissionOverwrites.ToArray();
            });
            session.Channel = channel;
            await channel.SendMessageAsync($"🎭 **라이어게임 참가 안내**\n\n참가 버튼을 눌러주세요. **{JoinSeconds}초** 뒤 3명 이상이면 시작합니다.\n라이어는 주제만 받고, 다른 참가자는 단어를 비공개로 받습니다. 질문은 O 또는 X로 답할 수 있게 작성하세요.",
                components: new ComponentBuilder().WithButton("참가하기", "liar:join", ButtonStyle.Success).Build(), allowedMentions: AllowedMentions.None);
            _ = RunAsync(guild, session);
            return (true, channel.Id);
        }
        catch
        {
            _sessions.TryRemove(new KeyValuePair<ulong, Session>(guild.Id, session));
            throw;
        }
    }

    public bool Join(ulong guildId, ulong channelId, ulong userId)
    {
        if (!_sessions.TryGetValue(guildId, out var s) || s.Channel?.Id != channelId || s.Phase != Phase.Joining) return false;
        lock (s.Gate)
        {
            if (s.Players.Count >= MaximumPlayers || s.Players.Contains(userId)) return false;
            s.Players.Add(userId);
            return true;
        }
    }

    public bool SubmitAnswerVote(ulong guildId, ulong channelId, ulong userId, bool answer)
    {
        if (!TryGet(guildId, channelId, Phase.AnswerVote, out var s)) return false;
        lock (s.Gate) return s.Round?.VoteAnswer(userId, answer) == true;
    }

    public bool SubmitAccusation(ulong guildId, ulong channelId, ulong userId, ulong targetId)
    {
        if (!TryGet(guildId, channelId, Phase.Accusation, out var s)) return false;
        lock (s.Gate) return s.Round?.Accuse(userId, targetId) == true;
    }

    public bool IsQuestionTurn(ulong guildId, ulong userId) => _sessions.TryGetValue(guildId, out var s) && s.Phase == Phase.Question && s.CurrentPlayer == userId;

    public async Task<bool> SubmitQuestionAsync(ulong guildId, ulong userId, string question)
    {
        if (!_sessions.TryGetValue(guildId, out var s) || s.Phase != Phase.Question || s.CurrentPlayer != userId || string.IsNullOrWhiteSpace(question)) return false;
        lock (s.Gate)
        {
            if (s.QuestionCompletion.Task.IsCompleted) return false;
            s.CurrentQuestion = question.Trim();
            s.QuestionCompletion.TrySetResult();
        }
        await Task.CompletedTask;
        return true;
    }

    public bool IsLiarGuessTurn(ulong guildId, ulong userId) => _sessions.TryGetValue(guildId, out var s) && s.Phase == Phase.LiarGuess && s.LiarId == userId;

    public bool SubmitLiarGuess(ulong guildId, ulong userId, string guess)
    {
        if (!_sessions.TryGetValue(guildId, out var s) || s.Phase != Phase.LiarGuess || s.LiarId != userId || string.IsNullOrWhiteSpace(guess)) return false;
        lock (s.Gate) return s.GuessCompletion.TrySetResult(guess.Trim());
    }

    public void CancelChannel(ulong channelId)
    {
        foreach (var pair in _sessions)
            if (pair.Value.Channel?.Id == channelId && _sessions.TryRemove(pair.Key, out var s)) s.Stop.Cancel();
    }

    private async Task RunAsync(SocketGuild guild, Session s)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(JoinSeconds), s.Stop.Token);
            List<ulong> players;
            lock (s.Gate) players = s.Players.ToList();
            if (players.Count < MinimumPlayers)
            {
                await Say(s, $"❌ 참가자가 {MinimumPlayers}명 이상이어야 해서 라이어게임을 취소합니다. (현재 {players.Count}명)");
                return;
            }
            var selected = Words[Random.Shared.Next(Words.Length)];
            s.LiarId = players[Random.Shared.Next(players.Count)];
            s.Word = selected.Word;
            s.Topic = selected.Topic;
            // 라이어가 첫 질문자가 되지 않도록, 이 순서 규칙은 공개 메시지에 절대 넣지 않습니다.
            s.Order = players.OrderBy(_ => Random.Shared.Next()).ToList();
            if (s.Order[0] == s.LiarId) (s.Order[0], s.Order[1]) = (s.Order[1], s.Order[0]);
            if (!await DeliverRolesAsync(guild, s))
            {
                await Say(s, "❌ 참가자 모두에게 비공개 역할 메시지를 보낼 수 없어 게임을 취소합니다. 봇 DM 허용 설정을 확인한 뒤 다시 시작해주세요.");
                return;
            }
            var roster = string.Join("\n", s.Order.Select((id, index) => $"참가자 {index + 1}: <@{id}>"));
            await Say(s, $"🎬 게임을 시작합니다! 참가자 {s.Order.Count}명이 차례로 익명 질문을 합니다. 질문마다 O/X 투표를 진행합니다.\n\n{roster}");
            foreach (var player in s.Order)
                await RunQuestionAsync(guild, s, player);

            await Say(s, $"🕵️ 모든 질문과 투표가 끝났습니다. **{BeforeAccusationSeconds}초** 뒤 라이어 지목을 시작합니다.");
            await Task.Delay(TimeSpan.FromSeconds(BeforeAccusationSeconds), s.Stop.Token);
            await RunAccusationAsync(s);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[라이어게임] 진행 실패: {ex.Message}");
            try { await Say(s, "❌ 진행 중 오류가 발생해 게임을 종료합니다."); } catch { }
        }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<ulong, Session>(s.GuildId, s));
            try { await CloseAsync(s); } catch (Exception ex) { Console.WriteLine($"[라이어게임] 채널 종료 실패: {ex.Message}"); }
        }
    }

    private async Task<bool> DeliverRolesAsync(SocketGuild guild, Session s)
    {
        foreach (var id in s.Order)
        {
            try
            {
                var user = await GetUserAsync(guild, id);
                var dm = await user.CreateDMChannelAsync();
                var text = id == s.LiarId
                    ? $"🎭 당신은 **라이어**입니다. 주제는 **{s.Topic}**입니다. 단어를 아는 척하며 들키지 마세요!"
                    : $"🔐 이번 단어는 **{s.Word}**입니다. 주제는 **{s.Topic}**입니다. 라이어를 찾아보세요!";
                await dm.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[라이어게임] 역할 DM 전송 실패 user={id}: {ex.Message}");
                return false;
            }
        }
        return true;
    }

    private async Task RunQuestionAsync(SocketGuild guild, Session s, ulong player)
    {
        s.Phase = Phase.Question;
        s.CurrentPlayer = player;
        s.CurrentQuestion = null;
        s.QuestionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var user = await GetUserAsync(guild, player);
            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync($"✍️ 지금은 당신의 질문 차례입니다. **{QuestionSeconds}초** 안에 O 또는 X로 답할 수 있는 질문을 작성해주세요.",
                components: new ComponentBuilder().WithButton("질문쓰기", $"liar:question:{s.GuildId}", ButtonStyle.Primary).Build(), allowedMentions: AllowedMentions.None);
        }
        catch { /* DM 차단도 시간 초과와 동일하게 다음 차례로 처리 */ }
        await Task.WhenAny(s.QuestionCompletion.Task, Task.Delay(TimeSpan.FromSeconds(QuestionSeconds), s.Stop.Token));
        if (string.IsNullOrWhiteSpace(s.CurrentQuestion))
        {
            await Say(s, "⏰ 질문이 제출되지 않아 다음 차례로 넘어갑니다.");
            return;
        }
        s.Round = new LiarGameRound(s.Order);
        s.Phase = Phase.AnswerVote;
        await Say(s, $"❓ **익명 질문**\n{s.CurrentQuestion}\n\n모든 참가자는 **{AnswerVoteSeconds}초** 안에 O 또는 X를 선택해주세요.",
            new ComponentBuilder().WithButton("O", "liar:answer:o", ButtonStyle.Success).WithButton("X", "liar:answer:x", ButtonStyle.Danger).Build());
        var until = DateTimeOffset.UtcNow.AddSeconds(AnswerVoteSeconds);
        while (!s.Round.AllAnswered && DateTimeOffset.UtcNow < until)
            await Task.Delay(TimeSpan.FromMilliseconds(250), s.Stop.Token);
        var answers = string.Join("\n", s.Order.Select(id => $"<@{id}>: {(s.Round.Answers.TryGetValue(id, out var yes) ? yes ? "O" : "X" : "미투표")}"));
        var missing = s.Round.MissingAnswers;
        await Say(s, $"📋 **투표 공개**\n{answers}" + (missing.Count > 0 ? $"\n\n⏰ 미투표: {string.Join(" ", missing.Select(id => $"<@{id}>"))}" : ""));
    }

    private async Task RunAccusationAsync(Session s)
    {
        s.Round = new LiarGameRound(s.Order);
        s.Phase = Phase.Accusation;
        var buttons = new ComponentBuilder();
        for (var i = 0; i < s.Order.Count; i++) buttons.WithButton($"참가자 {i + 1}", $"liar:accuse:{s.Order[i]}", ButtonStyle.Secondary, row: i / 5);
        await Say(s, $"🕵️ **라이어 지목**\n참가자 한 명을 선택하세요. **{AccusationSeconds}초** 안에 모두 투표하지 않으면 미투표도 결과에 포함됩니다.", buttons.Build());
        var until = DateTimeOffset.UtcNow.AddSeconds(AccusationSeconds);
        while (!s.Round.AllAccused && DateTimeOffset.UtcNow < until)
            await Task.Delay(TimeSpan.FromMilliseconds(250), s.Stop.Token);
        var rows = string.Join("\n", s.Order.Select(id => s.Round.Accusations.TryGetValue(id, out var target) ? $"<@{id}> → 참가자 {s.Order.IndexOf(target) + 1}" : $"<@{id}>: 미투표"));
        var result = s.Round.Decide(s.LiarId);
        await Say(s, $"📣 **지목 결과**\n{rows}\n\n{result.Reason}");
        if (!result.LiarFound)
        {
            await Say(s, $"🎭 **라이어 승리!** 라이어는 <@{s.LiarId}>였습니다. 단어는 **{s.Word}**였습니다.");
            return;
        }
        s.Phase = Phase.LiarGuess;
        s.GuessCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var liar = await GetUserAsync(s.Channel!.Guild, s.LiarId);
            var dm = await liar.CreateDMChannelAsync();
            await dm.SendMessageAsync($"🔐 라이어로 지목되었습니다. **{LiarGuessSeconds}초** 안에 단어를 맞히면 승리할 수 있습니다.",
                components: new ComponentBuilder().WithButton("단어 입력", $"liar:guess:{s.GuildId}", ButtonStyle.Primary).Build(), allowedMentions: AllowedMentions.None);
        }
        catch { }
        var completed = await Task.WhenAny(s.GuessCompletion.Task, Task.Delay(TimeSpan.FromSeconds(LiarGuessSeconds), s.Stop.Token));
        var guess = completed == s.GuessCompletion.Task ? await s.GuessCompletion.Task : null;
        var won = string.Equals(Normalize(guess), Normalize(s.Word), StringComparison.Ordinal);
        await Say(s, won
            ? $"🎭 **라이어 승리!** <@{s.LiarId}>님이 단어 **{s.Word}**을 맞혔습니다."
            : $"🎉 **시민 승리!** 라이어는 <@{s.LiarId}>였고, 정답은 **{s.Word}**였습니다.");
    }

    private static string Normalize(string? value) => (value ?? "").Trim().Replace(" ", "").ToLowerInvariant();
    private static async Task<IUser> GetUserAsync(IGuild guild, ulong userId)
    {
        if (guild is SocketGuild socketGuild && socketGuild.GetUser(userId) is { } cached) return cached;
        return await Program.instance.client.Rest.GetGuildUserAsync(guild.Id, userId);
    }
    private static async Task Say(Session s, string text, MessageComponent? components = null) => await s.Channel!.SendMessageAsync(text, components: components, allowedMentions: AllowedMentions.None);
    private bool TryGet(ulong guildId, ulong channelId, Phase phase, out Session session)
    {
        if (_sessions.TryGetValue(guildId, out session!) && session.Channel?.Id == channelId && session.Phase == phase) return true;
        session = null!;
        return false;
    }

    private enum Phase { Joining, Question, AnswerVote, Accusation, LiarGuess }
    private sealed class Session(ulong guildId, ulong returnChannelId, ulong hostId)
    {
        public ulong GuildId { get; } = guildId;
        public ulong ReturnChannelId { get; } = returnChannelId;
        public ulong HostId { get; } = hostId;
        public ITextChannel? Channel;
        public object Gate { get; } = new();
        public List<ulong> Players { get; } = [];
        public List<ulong> Order { get; set; } = [];
        public ulong LiarId;
        public string Word = "";
        public string Topic = "";
        public ulong CurrentPlayer;
        public string? CurrentQuestion;
        public LiarGameRound? Round;
        public Phase Phase = Phase.Joining;
        public TaskCompletionSource QuestionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> GuessCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Stop { get; } = new();
    }

    private static async Task CloseAsync(Session s)
    {
        if (s.Channel is null) return;
        await s.Channel.ModifyAsync(x => x.Name = s.Channel.Name.EndsWith("-종료") ? s.Channel.Name : $"{s.Channel.Name}-종료");
        await s.Channel.SendMessageAsync($"이 몰리 놀이터 채널은 **{CloseSeconds}초** 뒤 보기 전용으로 닫힙니다.", allowedMentions: AllowedMentions.None);
        await Task.Delay(TimeSpan.FromSeconds(CloseSeconds));
        var permissions = OverwritePermissions.InheritAll.Modify(sendMessages: PermValue.Deny, addReactions: PermValue.Deny, useApplicationCommands: PermValue.Deny);
        await s.Channel.AddPermissionOverwriteAsync(s.Channel.Guild.EveryoneRole, permissions);
        await s.Channel.SendMessageAsync("🔒 이 몰리 놀이터 채널은 닫혔습니다.", allowedMentions: AllowedMentions.None);
    }
}
