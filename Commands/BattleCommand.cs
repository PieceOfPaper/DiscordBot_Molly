using Discord;
using Discord.Interactions;
using Molly.Battle;
using Molly.Prediction;

namespace DiscordBot_Molly.Commands;

public sealed class BattleCommand : InteractionModuleBase<SocketInteractionContext>
{
    // 배틀 템포 조정값: 기획 테스트에서 이 값만 바꾸면 됩니다.
    private const int PreBattleWaitSeconds = 30;
    private const int TurnIntervalSeconds = 5;
    private const int ThreadCloseDelaySeconds = 10;

    [SlashCommand("배틀", "등록한 캐릭터로 같은 서버의 사용자에게 자동 전투를 신청합니다.")]
    public async Task StartAsync([Summary("상대", "배틀할 같은 서버의 사용자")] IUser opponent)
    {
        if (Context.Guild is null || Context.Channel is IThreadChannel) { await RespondAsync("배틀은 서버의 일반 텍스트 채널에서만 시작해주세요.", ephemeral: true); return; }
        if (opponent.Id == Context.User.Id || opponent.IsBot) { await RespondAsync("자기 자신이나 봇과는 배틀할 수 없어요.", ephemeral: true); return; }
        if (MobiRankBrowser.IsWarmingUp) { await RespondAsync(MobiRankBrowser.WarmingUpMessage, ephemeral: true); return; }
        if (!Program.instance.Battles.Current.IsUsable) { await RespondAsync("배틀 데이터가 아직 준비되지 않았어요. 시트 데이터와 마지막 갱신 상태를 확인해주세요.", ephemeral: true); return; }
        // 상대 확인은 REST 호출이라 서버가 바쁠 때 3초 응답 제한을 넘길 수 있으므로 먼저 응답을 지연합니다.
        // 세션 입장 전에 지연하므로 여기서 실패해도 배틀 슬롯이 "진행 중"으로 남지 않습니다.
        await DeferAsync(ephemeral: true);
        try { await Context.Client.Rest.GetGuildUserAsync(Context.Guild.Id, opponent.Id); }
        catch { await ModifyOriginalResponseAsync(x => x.Content = "상대는 현재 이 서버의 사용자여야 해요."); return; }
        await RunAsync("배틀", async ct =>
        {
            var a = await ResolveAsync(Context.User.Id, Context.Guild.Id, ct);
            var b = await ResolveAsync(opponent.Id, Context.Guild.Id, ct);
            return (a, b, CombatantName(Context.User, a), CombatantName(opponent, b));
        }, record: true);
    }

    /// <summary>모의배틀 상대 이름. 인게임에서 허수아비를 두고 스킬을 연습하는 것처럼 가상의 상대와 싸운다.</summary>
    public const string DummyName = "허수아비";

    [SlashCommand("모의배틀", "등록한 캐릭터로 고른 클래스의 허수아비와 연습 전투를 합니다. 전적에 남지 않습니다.")]
    public async Task PracticeAsync([Summary("클래스", "허수아비의 클래스"), Autocomplete(typeof(BattleClassAutocomplete))] string classId)
    {
        if (Context.Guild is null || Context.Channel is IThreadChannel) { await RespondAsync("모의배틀은 서버의 일반 텍스트 채널에서만 시작해주세요.", ephemeral: true); return; }
        if (MobiRankBrowser.IsWarmingUp) { await RespondAsync(MobiRankBrowser.WarmingUpMessage, ephemeral: true); return; }
        var data = Program.instance.Battles.Current;
        if (!data.IsUsable) { await RespondAsync("배틀 데이터가 아직 준비되지 않았어요. 시트 데이터와 마지막 갱신 상태를 확인해주세요.", ephemeral: true); return; }
        // 자동완성을 쓰지 않고 직접 입력한 값도 받을 수 있어 클래스 ID 또는 이름으로 찾는다.
        var dummyClassId = PracticeClassId(data, classId);
        if (dummyClassId is null)
        {
            var readyNames = data.BattleReadyClassIds.Select(id => data.Classes[id].Name).Order(StringComparer.Ordinal);
            await RespondAsync("배틀을 지원하는 클래스를 골라주세요. 현재 배틀 가능 클래스: " + string.Join(", ", readyNames), ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        await RunAsync("모의배틀", async ct =>
        {
            var a = await ResolveAsync(Context.User.Id, Context.Guild.Id, ct);
            var dummy = CreateDummy(a, dummyClassId);
            return (a, dummy, CombatantName(Context.User, a), DummyLabel(dummy));
        }, record: false);
    }

    /// <summary>모의배틀 클래스 옵션을 배틀 가능 클래스 ID로 바꾼다. ID·이름 모두 받고, 배틀을 지원하지 않으면 null.</summary>
    public static string? PracticeClassId(BattleDataSnapshot data, string input)
    {
        var value = input.Trim();
        var match = data.Classes.Values.FirstOrDefault(x => x.Id == value || x.Name == value);
        return match is not null && data.IsClassBattleReady(match.Id) ? match.Id : null;
    }

    /// <summary>허수아비는 신청자와 전투력·생활력·매력이 같고 클래스만 다르다. 전적을 남기지 않으므로 사용자 ID는 0이다.</summary>
    public static CharacterBattleSnapshot CreateDummy(CharacterBattleSnapshot challenger, string classId)
        => new(0, DummyName, classId, challenger.CombatPower, challenger.LifePower, challenger.CharmPower);

    private static string DummyLabel(CharacterBattleSnapshot dummy)
        => "🎯 " + DummyName + "(" + ClassEmojis.Label(dummy.ClassId, Program.instance.Battles.Current.Classes.TryGetValue(dummy.ClassId, out var battleClass) ? battleClass.Name : dummy.ClassId) + ")";

    /// <summary>
    /// 배틀·모의배틀 공통 진행: 서버별 배틀 슬롯 입장 → 참가자 준비 → 스레드 생성 → (배틀만) 예측 열기 → 준비 대기 → (배틀만) 예측 마감
    /// → 중계 → (배틀만) 전적 저장·예측 정산 → 스레드 보관. 호출 전에 DeferAsync로 응답을 지연해 둔다.
    /// 예측 오류는 로그만 남기고 배틀 진행을 막지 않는다. 정산하지 못한 예측은 무효(증표 변동 없음)로 처리한다.
    /// </summary>
    private async Task RunAsync(string title, Func<CancellationToken, Task<(CharacterBattleSnapshot A, CharacterBattleSnapshot B, string ALabel, string BLabel)>> prepare, bool record)
    {
        if (!Program.instance.BattleSessions.TryEnter(Context.Guild.Id, out var session)) { await ModifyOriginalResponseAsync(x => x.Content = "이 서버에서는 이미 배틀이 진행 중이에요."); return; }
        IThreadChannel? thread = null;
        string? aLabel = null;
        string? bLabel = null;
        OpenPrediction? prediction = null;
        var predictionDone = false;
        var cancelReason = PredictionCancelReason.Error;
        try
        {
            var (a, b, preparedALabel, preparedBLabel) = await prepare(session.CancellationToken);
            session.CancellationToken.ThrowIfCancellationRequested();
            aLabel = preparedALabel;
            bLabel = preparedBLabel;
            thread = await GameThreads.CreateAsync(Context.Channel, title + "-" + DateTimeOffset.Now.ToString("yyyyMMddHHmm"));
            await ModifyOriginalResponseAsync(x => x.Content = title + " 스레드 <#" + thread.Id + ">에서 자동전투를 시작합니다.");
            // 예측은 배틀에만 연다. 예측 ID는 배틀 스레드 ID다.
            if (record) prediction = await OpenPredictionAsync(thread.Id, Context.Guild.Id, a, b, session.CancellationToken);
            var prepText = "⚔️ " + aLabel + " vs " + bLabel + (record ? "" : "\n-# 모의배틀은 전적에 남지 않아요.") + "\n**" + PreBattleWaitSeconds + "초 뒤 전투를 시작합니다. 준비하세요!**";
            if (prediction is not null)
            {
                prediction.Message = await SendWithComponentsAsync(thread, prepText + BattlePredictionMessages.Intro(prediction.Rules),
                    BattlePredictionMessages.Buttons(prediction.Id, a.CharacterName, b.CharacterName, prediction.Rules));
            }
            else await SendAsync(thread, prepText, AllowedMentions.All);
            await Task.Delay(TimeSpan.FromSeconds(PreBattleWaitSeconds), session.CancellationToken);
            // 예측을 마감한 뒤에 전투를 계산하므로 베팅하는 동안에는 결과가 정해져 있지 않다.
            var lockText = prediction is null ? "" : await LockPredictionAsync(prediction, a.CharacterName, b.CharacterName);
            await SendAsync(thread, "전투를 시작합니다!" + lockText);
            var result = new BattleEngine().Simulate(a, b, Program.instance.Battles.Current, new SystemBattleRandom());
            foreach (var turn in BattleLog.Format(result.Events, a.CharacterName))
            {
                await Task.Delay(TimeSpan.FromSeconds(TurnIntervalSeconds), session.CancellationToken);
                foreach (var blocks in BattleLog.SplitForDiscord(turn)) await SendBlocksAsync(thread, blocks);
            }
            var winner = result.Outcome == BattleOutcome.FighterAWin ? aLabel : result.Outcome == BattleOutcome.FighterBWin ? bLabel : "무승부";
            await SendAsync(thread, string.Format("🏁 전투 종료: **{0}**\n{1} {2:N0}/{3:N0} HP · {4} {5:N0}/{6:N0} HP", winner, aLabel, result.FighterAHp, result.FighterAMaxHp, bLabel, result.FighterBHp, result.FighterBMaxHp));
            // 끝까지 중계한 배틀만 전적에 남긴다. 강제 종료된 배틀은 여기까지 오지 않는다. 저장 실패는 이미 끝난 배틀 결과를 바꾸지 않는다.
            // 모의배틀(허수아비)은 전적에 남기지 않는다.
            if (record) await RecordAsync(a, b, result.Outcome);
            if (prediction is not null)
            {
                predictionDone = true;
                await ResolvePredictionAsync(thread, prediction, result.Outcome switch
                {
                    BattleOutcome.FighterAWin => PredictionSide.Challenger,
                    BattleOutcome.FighterBWin => PredictionSide.Opponent,
                    _ => null,
                });
            }
        }
        catch (OperationCanceledException) when (session.IsStopRequested)
        {
            cancelReason = PredictionCancelReason.Stopped;
            if (thread is null)
                await ModifyOriginalResponseAsync(x => x.Content = title + " 시작 전에 강제 종료되었어요.");
            else
                await SendAsync(thread, "🛑 전투가 강제 종료되었습니다." + (aLabel is null || bLabel is null ? "" : "\n⚔️ **" + aLabel + " vs " + bLabel + "**"));
        }
        catch (OperationCanceledException) when (thread is null)
        {
            await ModifyOriginalResponseAsync(x => x.Content = "캐릭터 랭킹 조회 시간이 초과되어 " + title + "을 시작하지 못했어요. 잠시 후 다시 시도해주세요.");
        }
        catch (InvalidDataException ex) { if (thread is null) await ModifyOriginalResponseAsync(x => x.Content = ex.Message); else await SendAsync(thread, title + "을 시작할 수 없어요: " + ex.Message); }
        catch (Exception ex) { Console.WriteLine("[" + title + "] 진행 실패: " + ex.GetType().Name); if (thread is null) await ModifyOriginalResponseAsync(x => x.Content = title + "을 시작하지 못했어요. 잠시 후 다시 시도해주세요."); else await SendAsync(thread, title + " 진행 중 오류가 발생해 중단했어요."); }
        finally
        {
            if (thread is not null)
            {
                // 끝까지 가지 못한 배틀의 예측은 무효다. 증표는 정산할 때만 움직이므로 되돌릴 것이 없다.
                if (prediction is not null && !predictionDone) await CancelPredictionAsync(thread, prediction, cancelReason);
                try { await SendAsync(thread, "⏳ 전투 기록은 " + ThreadCloseDelaySeconds + "초 뒤에 잠기고 보관됩니다."); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(ThreadCloseDelaySeconds));
                try { await thread.ModifyAsync(x => { x.Locked = true; x.Archived = true; }); } catch { }
            }
            Program.instance.BattleSessions.Leave(Context.Guild.Id, session);
        }
    }

    /// <summary>진행 중인 배틀의 예측. 준비 메시지는 마감·무효 때 버튼을 없애는 데 쓴다.</summary>
    private sealed class OpenPrediction(ulong id, PredictionRules rules)
    {
        public ulong Id { get; } = id;
        public PredictionRules Rules { get; } = rules;
        public IUserMessage? Message { get; set; }
        public bool ButtonsRemoved { get; set; }
    }

    /// <summary>예측을 연다. 규칙 시트는 짧게만 기다리고, 실패하면 마지막 정상 규칙을 쓴다. 열지 못하면 null(배틀만 진행).</summary>
    private static async Task<OpenPrediction?> OpenPredictionAsync(ulong predictionId, ulong guildId, CharacterBattleSnapshot a, CharacterBattleSnapshot b, CancellationToken ct)
    {
        try
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await Program.instance.TokenRules.EnsureFreshAsync(TimeSpan.FromMinutes(10), timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            var rules = PredictionRules.From(Program.instance.TokenRules.Current);
            await Program.instance.Predictions.OpenAsync(predictionId, guildId, a.DiscordUserId, b.DiscordUserId,
                a.CharacterName, b.CharacterName, rules, DateTimeOffset.UtcNow, ct);
            return new OpenPrediction(predictionId, rules);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine("[배틀 예측] 예측을 열지 못해 배틀만 진행합니다: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>예측을 마감하고 버튼을 없앤 뒤 전투 시작 메시지에 붙일 비율 안내를 돌려준다. 실패하면 빈 문자열.</summary>
    private static async Task<string> LockPredictionAsync(OpenPrediction prediction, string challengerName, string opponentName)
    {
        string text;
        try
        {
            text = await Program.instance.Predictions.LockAsync(prediction.Id, DateTimeOffset.UtcNow) is { } locked
                ? BattlePredictionMessages.Locked(locked, challengerName, opponentName, prediction.Rules)
                : "";
        }
        catch (Exception ex)
        {
            Console.WriteLine("[배틀 예측] 마감 실패: " + ex.GetType().Name + ": " + ex.Message);
            text = "";
        }
        await RemoveButtonsAsync(prediction);
        return text;
    }

    private static async Task ResolvePredictionAsync(IMessageChannel thread, OpenPrediction prediction, PredictionSide? winner)
    {
        PredictionSettlement? settlement;
        try { settlement = await Program.instance.Predictions.ResolveAsync(prediction.Id, winner, DateTimeOffset.UtcNow); }
        catch (Exception ex)
        {
            Console.WriteLine("[배틀 예측] 정산 실패, 무효 처리합니다: " + ex.GetType().Name + ": " + ex.Message);
            await CancelPredictionAsync(thread, prediction, PredictionCancelReason.SettlementFailed);
            return;
        }
        await SendSettlementAsync(thread, settlement);
    }

    private static async Task CancelPredictionAsync(IMessageChannel thread, OpenPrediction prediction, PredictionCancelReason reason)
    {
        await RemoveButtonsAsync(prediction);
        PredictionSettlement? settlement;
        try { settlement = await Program.instance.Predictions.CancelAsync(prediction.Id, reason, DateTimeOffset.UtcNow); }
        catch (Exception ex)
        {
            // 무효 처리에 실패해도 증표는 움직이지 않았고, 다음 시작 때 정산되지 않은 예측으로 무효 처리된다.
            Console.WriteLine("[배틀 예측] 무효 처리 실패: " + ex.GetType().Name + ": " + ex.Message);
            return;
        }
        await SendSettlementAsync(thread, settlement);
    }

    private static async Task SendSettlementAsync(IMessageChannel thread, PredictionSettlement? settlement)
    {
        if (settlement is null || BattlePredictionMessages.Settlement(settlement) is not { } text) return;
        // 맞힌 사람은 이름만 보이게 하고 알림은 보내지 않는다.
        try { await SendAsync(thread, text, AllowedMentions.None); }
        catch (Exception ex) { Console.WriteLine("[배틀 예측] 정산 안내 전송 실패: " + ex.GetType().Name); }
    }

    private static async Task RemoveButtonsAsync(OpenPrediction prediction)
    {
        if (prediction.ButtonsRemoved || prediction.Message is null) return;
        prediction.ButtonsRemoved = true;
        try { await prediction.Message.ModifyAsync(x => x.Components = new ComponentBuilder().Build()); }
        catch (Exception ex) { Console.WriteLine("[배틀 예측] 버튼 제거 실패: " + ex.GetType().Name); }
    }

    private static async Task RecordAsync(CharacterBattleSnapshot a, CharacterBattleSnapshot b, BattleOutcome outcome)
    {
        var (aResult, bResult) = outcome switch
        {
            BattleOutcome.FighterAWin => (BattleRecordStore.Result.Win, BattleRecordStore.Result.Loss),
            BattleOutcome.FighterBWin => (BattleRecordStore.Result.Loss, BattleRecordStore.Result.Win),
            _ => (BattleRecordStore.Result.Draw, BattleRecordStore.Result.Draw)
        };
        try
        {
            await Program.instance.BattleRecords.RecordAsync(a.DiscordUserId, a.ClassId, aResult);
            await Program.instance.BattleRecords.RecordAsync(b.DiscordUserId, b.ClassId, bResult);
        }
        catch (Exception ex) { Console.WriteLine("[배틀] 전적 저장 실패: " + ex.GetType().Name); }
    }

    [SlashCommand("배틀종료", "현재 서버에서 진행 중인 배틀을 강제로 종료합니다.")]
    public async Task StopAsync()
    {
        if (Context.Guild is null) { await RespondAsync("배틀은 서버에서만 종료할 수 있어요.", ephemeral: true); return; }
        if (!Program.instance.BattleSessions.TryStop(Context.Guild.Id)) { await RespondAsync("이 서버에서 진행 중인 배틀이 없어요.", ephemeral: true); return; }
        await RespondAsync("🛑 배틀 강제 종료를 요청했어요. 전투 스레드에 종료 안내를 남긴 뒤 " + ThreadCloseDelaySeconds + "초 후 잠금·보관합니다.", ephemeral: true);
    }

    private static async Task<CharacterBattleSnapshot> ResolveAsync(ulong userId, ulong guildId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var saved = await Program.instance.RegisteredCharacters.LoadAsync(userId) ?? throw new InvalidDataException("참가자 모두 먼저 /캐릭터등록을 해야 해요.");
        if (saved.LastSyncedAtUtc is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(1) && saved is { ClassId: not null, CombatPower: not null, LifePower: not null, CharmPower: not null })
        {
            EnsureBattleReady(saved.CharacterName, saved.ClassId);
            return new CharacterBattleSnapshot(userId, saved.CharacterName, saved.ClassId, saved.CombatPower.Value, saved.LifePower.Value, saved.CharmPower.Value);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var rank = await MobiRankBrowser.GetRankBySearchAsync(4, saved.CharacterName, saved.Server, null, cts.Token, guildId: guildId) ?? throw new InvalidDataException(saved.CharacterName + " 캐릭터를 현재 종합 랭킹에서 찾지 못했어요. /캐릭터등록으로 확인해주세요.");
        var classId = Program.instance.Battles.Current.Classes.Values.SingleOrDefault(x => x.Name == rank.ClassName)?.Id ?? throw new InvalidDataException(rank.ClassName + " 클래스의 배틀 데이터가 없습니다.");
        if (rank.Combat is null || rank.Life is null || rank.Charm is null) throw new InvalidDataException("랭킹에서 배틀에 필요한 능력치를 읽지 못했어요. 잠시 후 다시 시도해주세요.");
        var updated = saved with { ClassId = classId, CombatPower = rank.Combat, LifePower = rank.Life, CharmPower = rank.Charm, LastSyncedAtUtc = DateTimeOffset.UtcNow };
        await Program.instance.RegisteredCharacters.SaveAsync(updated, cts.Token);
        EnsureBattleReady(updated.CharacterName, classId);
        return new CharacterBattleSnapshot(userId, updated.CharacterName, classId, rank.Combat.Value, rank.Life.Value, rank.Charm.Value);
    }

    /// <summary>스레드를 만들기 전에 시트 로딩 때 계산해 둔 배틀 가능 클래스로 판정해, 미지원 클래스는 신청자에게만 바로 안내한다.</summary>
    private static void EnsureBattleReady(string characterName, string classId)
    {
        var data = Program.instance.Battles.Current;
        if (data.IsClassBattleReady(classId)) return;
        var className = ClassEmojis.Label(classId, data.Classes.TryGetValue(classId, out var battleClass) ? battleClass.Name : classId);
        var readyNames = data.BattleReadyClassIds.Select(id => (Id: id, data.Classes[id].Name)).OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => ClassEmojis.Label(x.Id, x.Name)).ToArray();
        throw new InvalidDataException(characterName + " 캐릭터의 " + className + " 클래스는 아직 배틀을 지원하지 않아요." + (readyNames.Length > 0 ? " 현재 배틀 가능 클래스: " + string.Join(", ", readyNames) : ""));
    }

    private static string CombatantName(IUser user, CharacterBattleSnapshot character)
    {
        var className = ClassEmojis.Label(character.ClassId, Program.instance.Battles.Current.Classes.TryGetValue(character.ClassId, out var battleClass) ? battleClass.Name : character.ClassId);
        return user.Mention + "(" + character.CharacterName + " · " + className + ")";
    }

    private static async Task SendAsync(IMessageChannel channel, string text, AllowedMentions? allowedMentions = null)
    {
        try { await channel.SendMessageAsync(text, allowedMentions: allowedMentions); }
        catch { await channel.SendMessageAsync(text, allowedMentions: allowedMentions); }
    }
    private static async Task<IUserMessage> SendWithComponentsAsync(IMessageChannel channel, string text, MessageComponent components)
    {
        try { return await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.All, components: components); }
        catch { return await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.All, components: components); }
    }
    private static async Task SendBlocksAsync(IMessageChannel channel, IReadOnlyList<BattleLogBlock> blocks)
    {
        var embeds = blocks.Select(x =>
        {
            var embed = new EmbedBuilder().WithColor(ToneColor(x.Tone));
            if (x.Title is not null) embed.WithTitle(x.Title);
            if (x.Body.Length > 0) embed.WithDescription(x.Body);
            return embed.Build();
        }).ToArray();
        try { await channel.SendMessageAsync(embeds: embeds); }
        catch { await channel.SendMessageAsync(embeds: embeds); }
    }

    // A 파랑, B 빨강, 턴 시작 상태 효과 회색. 누구의 행동인지 임베드 왼쪽 색 띠만 보고도 구분한다.
    private static Color ToneColor(BattleLogTone tone) => tone switch
    {
        BattleLogTone.FighterA => new Color(0x5865F2),
        BattleLogTone.FighterB => new Color(0xED4245),
        _ => new Color(0x99AAB5)
    };
}

/// <summary>모의배틀 클래스 옵션 자동완성. 시트에서 배틀 가능한 클래스만, 입력한 글자가 이름에 들어간 것부터 보여준다.</summary>
public sealed class BattleClassAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var data = Program.instance.Battles.Current;
        var typed = autocompleteInteraction.Data.Current.Value?.ToString()?.Trim() ?? "";
        var suggestions = data.BattleReadyClassIds.Select(id => data.Classes[id])
            .Where(x => typed.Length == 0 || x.Name.Contains(typed, StringComparison.Ordinal))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Take(25)
            .Select(x => new AutocompleteResult(x.Name, x.Id));
        return Task.FromResult(AutocompletionResult.FromSuccess(suggestions));
    }
}
