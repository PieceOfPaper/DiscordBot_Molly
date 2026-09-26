using Discord;
using Discord.Interactions;
using Molly.Battle;

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
        if (!Program.instance.BattleSessions.TryEnter(Context.Guild.Id, out var session)) { await ModifyOriginalResponseAsync(x => x.Content = "이 서버에서는 이미 배틀이 진행 중이에요."); return; }
        IThreadChannel? thread = null;
        string? aLabel = null;
        string? bLabel = null;
        try
        {
            var a = await ResolveAsync(Context.User.Id, Context.Guild.Id, session.CancellationToken);
            var b = await ResolveAsync(opponent.Id, Context.Guild.Id, session.CancellationToken);
            session.CancellationToken.ThrowIfCancellationRequested();
            aLabel = CombatantName(Context.User, a);
            bLabel = CombatantName(opponent, b);
            thread = await GameThreads.CreateAsync(Context.Channel, "배틀-" + DateTimeOffset.Now.ToString("yyyyMMddHHmm"));
            await ModifyOriginalResponseAsync(x => x.Content = "배틀 스레드 <#" + thread.Id + ">에서 자동전투를 시작합니다.");
            await SendAsync(thread, "⚔️ " + aLabel + " vs " + bLabel + "\n**" + PreBattleWaitSeconds + "초 뒤 전투를 시작합니다. 준비하세요!**", AllowedMentions.All);
            await Task.Delay(TimeSpan.FromSeconds(PreBattleWaitSeconds), session.CancellationToken);
            await SendAsync(thread, "전투를 시작합니다!");
            var result = new BattleEngine().Simulate(a, b, Program.instance.Battles.Current, new SystemBattleRandom());
            foreach (var line in Format(result.Events)) { await Task.Delay(TimeSpan.FromSeconds(TurnIntervalSeconds), session.CancellationToken); await SendAsync(thread, line); }
            var winner = result.Outcome == BattleOutcome.FighterAWin ? aLabel : result.Outcome == BattleOutcome.FighterBWin ? bLabel : "무승부";
            await SendAsync(thread, string.Format("🏁 전투 종료: **{0}**\n{1} {2:N0}/{3:N0} HP · {4} {5:N0}/{6:N0} HP", winner, aLabel, result.FighterAHp, result.FighterAMaxHp, bLabel, result.FighterBHp, result.FighterBMaxHp));
        }
        catch (OperationCanceledException) when (session.IsStopRequested)
        {
            if (thread is null)
                await ModifyOriginalResponseAsync(x => x.Content = "배틀 시작 전에 강제 종료되었어요.");
            else
                await SendAsync(thread, "🛑 전투가 강제 종료되었습니다." + (aLabel is null || bLabel is null ? "" : "\n⚔️ **" + aLabel + " vs " + bLabel + "**"));
        }
        catch (OperationCanceledException) when (thread is null)
        {
            await ModifyOriginalResponseAsync(x => x.Content = "캐릭터 랭킹 조회 시간이 초과되어 배틀을 시작하지 못했어요. 잠시 후 다시 시도해주세요.");
        }
        catch (InvalidDataException ex) { if (thread is null) await ModifyOriginalResponseAsync(x => x.Content = ex.Message); else await SendAsync(thread, "배틀을 시작할 수 없어요: " + ex.Message); }
        catch (Exception ex) { Console.WriteLine("[배틀] 진행 실패: " + ex.GetType().Name); if (thread is null) await ModifyOriginalResponseAsync(x => x.Content = "배틀을 시작하지 못했어요. 잠시 후 다시 시도해주세요."); else await SendAsync(thread, "배틀 진행 중 오류가 발생해 중단했어요."); }
        finally
        {
            if (thread is not null)
            {
                try { await SendAsync(thread, "⏳ 전투 기록은 " + ThreadCloseDelaySeconds + "초 뒤에 잠기고 보관됩니다."); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(ThreadCloseDelaySeconds));
                try { await thread.ModifyAsync(x => { x.Locked = true; x.Archived = true; }); } catch { }
            }
            Program.instance.BattleSessions.Leave(Context.Guild.Id, session);
        }
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
        var saved = await Program.instance.RegisteredCharacters.LoadAsync(userId) ?? throw new InvalidDataException("두 참가자 모두 먼저 /캐릭터등록을 해야 해요.");
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
        var className = data.Classes.TryGetValue(classId, out var battleClass) ? battleClass.Name : classId;
        var readyNames = data.BattleReadyClassIds.Select(id => data.Classes[id].Name).Order(StringComparer.Ordinal).ToArray();
        throw new InvalidDataException(characterName + " 캐릭터의 " + className + " 클래스는 아직 배틀을 지원하지 않아요." + (readyNames.Length > 0 ? " 현재 배틀 가능 클래스: " + string.Join(", ", readyNames) : ""));
    }

    private static string CombatantName(IUser user, CharacterBattleSnapshot character)
    {
        var className = Program.instance.Battles.Current.Classes.TryGetValue(character.ClassId, out var battleClass) ? battleClass.Name : character.ClassId;
        return user.Mention + "(" + character.CharacterName + " · " + className + ")";
    }

    private static async Task SendAsync(IMessageChannel channel, string text, AllowedMentions? allowedMentions = null)
    {
        try { await channel.SendMessageAsync(text, allowedMentions: allowedMentions); }
        catch { await channel.SendMessageAsync(text, allowedMentions: allowedMentions); }
    }
    /// <summary>전투 이벤트를 행동 단위 Discord 메시지로 묶는다. 테스트에서 로그 문구를 검사할 수 있도록 공개한다.</summary>
    public static IEnumerable<string> Format(IEnumerable<BattleEvent> events)
    {
        var current = new List<string>();
        var hasActionHeader = false;
        var hasTurnStatusHeader = false;
        var pendingCritical = false;
        // 턴 시작 상태 효과 묶음의 제목은 그 턴의 주인이다. 지속 피해 이벤트의 Actor는 피해를 건 쪽이라 제목에 쓰면 상대의 상태처럼 보인다.
        string? turnOwner = null;
        foreach (var x in events)
        {
            if (x.Type is "BattleStarted" or "BattleEnded") continue;
            if (x.Type == "TurnStarted")
            {
                turnOwner = x.Actor;
                if (current.Count > 0) { yield return string.Join("\n", current); current.Clear(); hasActionHeader = false; hasTurnStatusHeader = false; }
                continue;
            }
            if (x.Type == "SurpriseEventTriggered") { current.Add("✨ " + x.Actor + "의 **" + x.Detail + "**!"); continue; }
            if (x.Type is "NormalAttackUsed" or "SkillUsed" or "DerivedSkillUsed")
            {
                var heading = x.Type == "NormalAttackUsed" ? x.Actor + "의 일반 공격!" : x.Actor + "이(가) **" + x.Detail + "**을(를) 사용합니다!";
                current.Add(x.Type == "DerivedSkillUsed" || hasActionHeader ? "　↳ " + heading : heading);
                hasActionHeader = true;
                continue;
            }
            if (x.Type == "LifeSkillUsed")
            {
                var heading = "🌿 " + x.Actor + "의 생활스킬 **" + x.Detail + "**!";
                current.Add(hasActionHeader ? "　↳ " + heading : heading);
                hasActionHeader = true;
                continue;
            }
            if (x.Type == "CriticalHit") { pendingCritical = true; continue; }
            var text = x.Type switch
            {
                "LifeSkillNarration" => x.Detail, "LifeSkillEffect" => (pendingCritical ? "💥 **치명타!** " : "") + x.Detail, "StatusCleansed" => "🌿 " + x.Actor + "의 **" + x.Detail + "** 상태가 사라졌습니다.", "DamageDealt" => pendingCritical ? "💥 **치명타!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 치명타 피해를 입혔습니다!" : x.Target + "에게 " + x.Amount?.ToString("N0") + "의 피해를 입혔습니다!", "AttackEvaded" => "💨 " + x.Target + "은(는) 상대의 시야에서 벗어나 공격을 흘려냈습니다!", "ShieldAbsorbed" => "🛡️ " + x.Target + "의 **" + x.Detail + "**이(가) " + x.Amount?.ToString("N0") + "의 피해를 흡수했습니다!", "AdditionalHit" => "⚡ **추가타!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 추가 피해!", "AdditionalDamage" => "✨ " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 추가 피해를 입혔습니다!", "StatusDamage" => "🌒 **" + x.Detail + "!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 지속 피해를 입혔습니다!", "BreakGaugeChanged" => x.Target + "의 브레이크 게이지가 " + x.Amount + "/" + x.Detail + "이 되었습니다.", "BreakActivated" => "💢 " + x.Target + "이(가) **브레이크** 상태에 빠졌습니다!", "BreakActionLost" => "💢 " + x.Target + "은(는) 브레이크로 행동하지 못했습니다!", "BreakImmune" => "🛡️ " + x.Target + "은(는) 브레이크를 버텨냈습니다!", "CooldownReduced" => x.Actor + "의 스킬 쿨다운이 " + x.Detail + "턴씩 감소했습니다.", "ResourceChanged" => x.Actor + "의 " + x.Detail, "HealApplied" => x.Actor + "의 HP가 " + x.Amount?.ToString("N0") + " 회복되었습니다!", "StatusApplied" => x.Actor + "에게 **" + x.Detail + "** 상태가 적용되었습니다!" + (x.Amount is > 0 ? " (" + x.Amount + "턴)" : ""), "StatusExpired" => x.Actor + "의 **" + x.Detail + "** 상태가 풀렸습니다.", "StatusConsumed" => x.Actor + "의 **" + x.Detail + "** 상태가 공격에 소모되었습니다.", "StatusHeal" => "💚 **" + x.Detail + "!** " + x.Target + "의 HP가 " + x.Amount?.ToString("N0") + " 회복되었습니다!", "HpStatus" => x.Actor + "은 " + x.Detail, "CharacterDefeated" => x.Target + "이(가) 쓰러졌습니다!", _ => null
            };
            pendingCritical = false;
            var isTurnStatus = !hasActionHeader && (x.Type is "StatusDamage" or "StatusHeal" or "StatusExpired" or "HpStatus" or "BreakActionLost");
            if (isTurnStatus && !hasTurnStatusHeader) { current.Add("⏳ **" + (turnOwner ?? x.Actor) + "의 상태 효과**"); hasTurnStatusHeader = true; }
            if (text is not null) current.Add((hasActionHeader || isTurnStatus ? "　↳ " : "") + text);
        }
        if (current.Count > 0) yield return string.Join("\n", current);
    }
}
