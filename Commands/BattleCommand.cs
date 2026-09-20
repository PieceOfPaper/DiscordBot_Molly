using Discord;
using Discord.Interactions;
using Molly.Battle;

namespace DiscordBot_Molly.Commands;

public sealed class BattleCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("배틀", "등록한 캐릭터로 같은 서버의 사용자에게 자동 전투를 신청합니다.")]
    public async Task StartAsync([Summary("상대", "배틀할 같은 서버의 사용자")] IUser opponent)
    {
        if (Context.Guild is null || Context.Channel is IThreadChannel) { await RespondAsync("배틀은 서버의 일반 텍스트 채널에서만 시작해주세요.", ephemeral: true); return; }
        if (opponent.Id == Context.User.Id || opponent.IsBot) { await RespondAsync("자기 자신이나 봇과는 배틀할 수 없어요.", ephemeral: true); return; }
        try { await Context.Client.Rest.GetGuildUserAsync(Context.Guild.Id, opponent.Id); }
        catch { await RespondAsync("상대는 현재 이 서버의 사용자여야 해요.", ephemeral: true); return; }
        if (!Program.instance.Battles.Current.IsUsable) { await RespondAsync("배틀 데이터가 아직 준비되지 않았어요. 시트 데이터와 마지막 갱신 상태를 확인해주세요.", ephemeral: true); return; }
        if (!Program.instance.BattleSessions.TryEnter(Context.Guild.Id)) { await RespondAsync("이 서버에서는 이미 배틀이 진행 중이에요.", ephemeral: true); return; }
        await DeferAsync(ephemeral: true);
        IThreadChannel? thread = null;
        try
        {
            var a = await ResolveAsync(Context.User.Id);
            var b = await ResolveAsync(opponent.Id);
            thread = await GameThreads.CreateAsync(Context.Channel, "배틀-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmm"));
            await ModifyOriginalResponseAsync(x => x.Content = "배틀 스레드 <#" + thread.Id + ">에서 자동전투를 시작합니다.");
            var result = new BattleEngine().Simulate(a, b, Program.instance.Battles.Current, new SystemBattleRandom());
            await SendAsync(thread, "⚔️ **" + a.CharacterName + "** vs **" + b.CharacterName + "**\n전투를 시작합니다!");
            foreach (var line in Format(result.Events)) { await Task.Delay(1500); await SendAsync(thread, line); }
            var winner = result.Outcome == BattleOutcome.FighterAWin ? a.CharacterName : result.Outcome == BattleOutcome.FighterBWin ? b.CharacterName : "무승부";
            await SendAsync(thread, string.Format("🏁 전투 종료: **{0}**\n{1} {2:N0}/{3:N0} HP · {4} {5:N0}/{6:N0} HP", winner, a.CharacterName, result.FighterAHp, result.FighterAMaxHp, b.CharacterName, result.FighterBHp, result.FighterBMaxHp));
        }
        catch (InvalidDataException ex) { if (thread is null) await ModifyOriginalResponseAsync(x => x.Content = ex.Message); else await SendAsync(thread, "배틀을 시작할 수 없어요: " + ex.Message); }
        catch (Exception ex) { Console.WriteLine("[배틀] 진행 실패: " + ex.GetType().Name); if (thread is null) await ModifyOriginalResponseAsync(x => x.Content = "배틀을 시작하지 못했어요. 잠시 후 다시 시도해주세요."); else await SendAsync(thread, "배틀 진행 중 오류가 발생해 중단했어요."); }
        finally { if (thread is not null) try { await thread.ModifyAsync(x => { x.Locked = true; x.Archived = true; }); } catch { } Program.instance.BattleSessions.Leave(Context.Guild.Id); }
    }

    private static async Task<CharacterBattleSnapshot> ResolveAsync(ulong userId)
    {
        var saved = await Program.instance.RegisteredCharacters.LoadAsync(userId) ?? throw new InvalidDataException("두 참가자 모두 먼저 /캐릭터등록을 해야 해요.");
        if (saved.LastSyncedAtUtc is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(1) && saved is { ClassId: not null, CombatPower: not null, LifePower: not null, CharmPower: not null })
            return new CharacterBattleSnapshot(userId, saved.CharacterName, saved.ClassId, saved.CombatPower.Value, saved.LifePower.Value, saved.CharmPower.Value);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var rank = await MobiRankBrowser.GetRankBySearchAsync(4, saved.CharacterName, saved.Server, null, cts.Token) ?? throw new InvalidDataException(saved.CharacterName + " 캐릭터를 현재 종합 랭킹에서 찾지 못했어요. /캐릭터등록으로 확인해주세요.");
        var classId = Program.instance.Battles.Current.Classes.Values.SingleOrDefault(x => x.Name == rank.ClassName)?.Id ?? throw new InvalidDataException(rank.ClassName + " 클래스의 배틀 데이터가 없습니다.");
        if (rank.Combat is null || rank.Life is null || rank.Charm is null) throw new InvalidDataException("랭킹에서 배틀에 필요한 능력치를 읽지 못했어요. 잠시 후 다시 시도해주세요.");
        var updated = saved with { ClassId = classId, CombatPower = rank.Combat, LifePower = rank.Life, CharmPower = rank.Charm, LastSyncedAtUtc = DateTimeOffset.UtcNow };
        await Program.instance.RegisteredCharacters.SaveAsync(updated, cts.Token);
        return new CharacterBattleSnapshot(userId, updated.CharacterName, classId, rank.Combat.Value, rank.Life.Value, rank.Charm.Value);
    }

    private static async Task SendAsync(IMessageChannel channel, string text) { try { await channel.SendMessageAsync(text); } catch { await channel.SendMessageAsync(text); } }
    private static IEnumerable<string> Format(IEnumerable<BattleEvent> events)
    {
        var current = new List<string>();
        var hasActionHeader = false;
        var pendingCritical = false;
        foreach (var x in events)
        {
            if (x.Type is "BattleStarted" or "BattleEnded") continue;
            if (x.Type == "TurnStarted")
            {
                if (current.Count > 0) { yield return string.Join("\n", current); current.Clear(); hasActionHeader = false; }
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
            if (x.Type == "CriticalHit") { pendingCritical = true; continue; }
            var text = x.Type switch
            {
                "DamageDealt" => pendingCritical ? "💥 **치명타!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 치명타 피해를 입혔습니다!" : x.Target + "에게 " + x.Amount?.ToString("N0") + "의 피해를 입혔습니다!", "AdditionalHit" => "⚡ **추가타!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 추가 피해!", "AdditionalDamage" => "✨ " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 추가 피해를 입혔습니다!", "StatusDamage" => "🌒 **" + x.Detail + "!** " + x.Target + "에게 " + x.Amount?.ToString("N0") + "의 지속 피해를 입혔습니다!", "BreakGaugeChanged" => x.Target + "의 브레이크 게이지가 " + x.Amount + "/" + x.Detail + "이 되었습니다.", "BreakActivated" => "💢 " + x.Target + "이(가) **브레이크** 상태에 빠졌습니다!", "BreakActionLost" => "💢 " + x.Target + "은(는) 브레이크로 행동하지 못했습니다!", "BreakImmune" => "🛡️ " + x.Target + "은(는) 브레이크를 버텨냈습니다!", "ResourceChanged" => x.Actor + "의 " + x.Detail, "HealApplied" => x.Actor + "의 HP가 " + x.Amount?.ToString("N0") + " 회복되었습니다!", "StatusApplied" => x.Actor + "에게 **" + x.Detail + "** 상태가 적용되었습니다!" + (x.Amount is > 0 ? " (" + x.Amount + "턴)" : ""), "StatusExpired" => x.Actor + "의 **" + x.Detail + "** 상태가 풀렸습니다.", "HpStatus" => x.Actor + "은 " + x.Detail, "CharacterDefeated" => x.Target + "이(가) 쓰러졌습니다!", _ => null
            };
            pendingCritical = false;
            if (text is not null) current.Add((hasActionHeader ? "　↳ " : "") + text);
        }
        if (current.Count > 0) yield return string.Join("\n", current);
    }
}
