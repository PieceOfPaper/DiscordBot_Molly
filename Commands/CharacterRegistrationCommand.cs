using System.Collections.Concurrent;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Molly.Battle;

namespace DiscordBot_Molly.Commands;

public sealed class CharacterRegistrationCommand : InteractionModuleBase<SocketInteractionContext>
{
    private const int RankingTimeoutMilliseconds = 180_000;
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(5);
    /// <summary>전적 삭제 확인을 기다리는 캐릭터 변경. 버튼 ID의 토큰으로 찾는다. 봇이 다시 시작되면 사라지고, 그때 누른 버튼은 만료 안내를 받는다.</summary>
    private static readonly ConcurrentDictionary<string, PendingRegistration> Pending = new(StringComparer.Ordinal);
    private sealed record PendingRegistration(ulong UserId, RegisteredCharacter Character, string ServerName, DateTimeOffset ExpiresAt);

    [SlashCommand("캐릭터등록", "종합 랭킹에 있는 내 캐릭터를 등록합니다.")]
    public async Task RegisterAsync(
        [Summary("서버", "캐릭터가 있는 서버")] MobiServer server,
        [Summary("캐릭터이름", "등록할 캐릭터 이름")] string characterName)
    {
        var normalizedName = characterName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            await RespondAsync("캐릭터 이름은 필수로 입력해주세요.", ephemeral: true);
            return;
        }

        if (normalizedName.Length > 12)
        {
            await RespondAsync("캐릭터 이름은 12자 이하로 입력해주세요.", ephemeral: true);
            return;
        }

        if (MobiRankBrowser.IsWarmingUp)
        {
            await RespondAsync(MobiRankBrowser.WarmingUpMessage, ephemeral: true);
            return;
        }

        var guildId = Context.Guild?.Id ?? 0;
        if (MobiRankBrowser.IsFullRunning(guildId))
        {
            await RespondAsync("종합 랭킹 검색이 진행 중이에요. 잠시 후 다시 시도해주세요.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ModifyOriginalResponseAsync(message => message.Content = "🔎 종합 랭킹에서 캐릭터를 찾고 있어요...");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(RankingTimeoutMilliseconds));
        try
        {
            var rank = await MobiRankBrowser.GetRankBySearchAsync(4, normalizedName, server, null, cts.Token, guildId: guildId);
            if (rank is null)
            {
                await ModifyOriginalResponseAsync(message => message.Content =
                    $"[{server}] 서버의 `{normalizedName}`은(는) 종합 랭킹에 없는 캐릭터입니다.");
                return;
            }

            var battleClass = Program.instance.Battles.Current.Classes.Values
                .SingleOrDefault(x => string.Equals(x.Name, rank.ClassName, StringComparison.Ordinal));
            var character = new RegisteredCharacter(
                Context.User.Id, server, normalizedName, DateTimeOffset.UtcNow,
                battleClass?.Id, rank.Combat ?? rank.Power, rank.Life, rank.Charm, DateTimeOffset.UtcNow);
            // 이미 등록한 것과 다른 캐릭터로 바꾸면 전적이 사라지므로, 지울 전적이 있을 때만 본인에게 확인을 받는다.
            var existing = await Program.instance.RegisteredCharacters.LoadAsync(Context.User.Id, cts.Token);
            if (existing is not null && (existing.Server != server || existing.CharacterName != normalizedName))
            {
                var records = await Program.instance.BattleRecords.LoadAsync(Context.User.Id, cts.Token);
                if (records.Count > 0)
                {
                    foreach (var (key, stale) in Pending) if (stale.ExpiresAt <= DateTimeOffset.UtcNow) Pending.TryRemove(key, out _);
                    var token = Guid.NewGuid().ToString("N");
                    Pending[token] = new PendingRegistration(Context.User.Id, character, rank.ServerName, DateTimeOffset.UtcNow + ConfirmTimeout);
                    await ModifyOriginalResponseAsync(message =>
                    {
                        message.Content = ConfirmMessage(existing, rank.ServerName, normalizedName, records);
                        message.Components = new ComponentBuilder()
                            .WithButton("전적 삭제하고 등록", "charreg:confirm:" + token, ButtonStyle.Danger)
                            .WithButton("취소", "charreg:cancel:" + token, ButtonStyle.Secondary)
                            .Build();
                    });
                    return;
                }
            }
            await Program.instance.RegisteredCharacters.SaveAsync(character, cts.Token);
            await ModifyOriginalResponseAsync(message => message.Content =
                $"✅ [{rank.ServerName}] 서버의 `{normalizedName}` 캐릭터를 등록했어요.");
        }
        catch (TaskCanceledException)
        {
            await ModifyOriginalResponseAsync(message => message.Content = "종합 랭킹 검색 시간이 초과되었어요. 잠시 후 다시 시도해주세요.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[캐릭터등록] 종합 랭킹 조회 또는 저장 실패: {ex}");
            await ModifyOriginalResponseAsync(message => message.Content = "캐릭터 등록 중 오류가 발생했어요. 잠시 후 다시 시도해주세요.");
        }
    }

    /// <summary>캐릭터를 바꾸면 이전 전적이 모두 삭제된다는 확인 문구. 테스트에서 문구를 검사할 수 있도록 공개한다.</summary>
    public static string ConfirmMessage(RegisteredCharacter existing, string newServerName, string newCharacterName, IReadOnlyList<BattleClassRecord> records)
        => $"⚠️ 이미 [{existing.Server}] `{existing.CharacterName}` 캐릭터가 등록되어 있어요.\n" +
           $"[{newServerName}] `{newCharacterName}`(으)로 바꾸면 지금까지의 배틀 전적(클래스 {records.Count}개, {records.Sum(x => x.Total)}전)이 **모두 삭제**돼요. 계속할까요?\n" +
           $"({(int)ConfirmTimeout.TotalMinutes}분 안에 선택해주세요.)";

    [ComponentInteraction("charreg:confirm:*")]
    public async Task ConfirmAsync(string token)
    {
        if (Context.Interaction is not SocketMessageComponent component) return;
        if (!Pending.TryGetValue(token, out var pending) || pending.UserId != Context.User.Id || !Pending.TryRemove(token, out _))
        {
            await component.UpdateAsync(message => { message.Content = "이미 처리했거나 만료된 요청이에요. 다시 `/캐릭터등록`을 해주세요."; message.Components = new ComponentBuilder().Build(); });
            return;
        }
        if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await component.UpdateAsync(message => { message.Content = "확인 시간이 지났어요. 기존 캐릭터와 전적은 그대로예요. 바꾸려면 다시 `/캐릭터등록`을 해주세요."; message.Components = new ComponentBuilder().Build(); });
            return;
        }
        // 버튼을 먼저 없애 두 번 누르는 것을 막는다.
        await component.UpdateAsync(message => { message.Content = "⏳ 이전 전적을 삭제하고 캐릭터를 등록하고 있어요..."; message.Components = new ComponentBuilder().Build(); });
        try
        {
            await Program.instance.BattleRecords.DeleteAllAsync(pending.UserId);
            await Program.instance.RegisteredCharacters.SaveAsync(pending.Character with { RegisteredAtUtc = DateTimeOffset.UtcNow });
            await ModifyOriginalResponseAsync(message => message.Content =
                $"✅ 이전 전적을 삭제하고 [{pending.ServerName}] 서버의 `{pending.Character.CharacterName}` 캐릭터를 등록했어요.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[캐릭터등록] 전적 삭제 또는 저장 실패: {ex}");
            await ModifyOriginalResponseAsync(message => message.Content = "캐릭터 등록 중 오류가 발생했어요. 잠시 후 다시 `/캐릭터등록`을 해주세요.");
        }
    }

    [ComponentInteraction("charreg:cancel:*")]
    public async Task CancelAsync(string token)
    {
        if (Context.Interaction is not SocketMessageComponent component) return;
        if (Pending.TryGetValue(token, out var pending) && pending.UserId == Context.User.Id) Pending.TryRemove(token, out _);
        await component.UpdateAsync(message => { message.Content = "등록을 취소했어요. 기존 캐릭터와 전적은 그대로예요."; message.Components = new ComponentBuilder().Build(); });
    }
}
