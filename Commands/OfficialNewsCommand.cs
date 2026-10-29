using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Molly.OfficialNews;

namespace DiscordBot_Molly.Commands;

/// <summary>
/// 공식 소식 게시판별 알림과 점검 알림의 채널 등록·확인·해제·테스트. 다섯 알림은 서로 독립적이고 같은 채널을 함께 써도 된다.
/// 점검 알림은 공지 게시판 안의 별도 알림 종류라 공지 목록 수집을 함께 쓰며, `/공지알림`은 점검 글을 보내지 않는다.
/// </summary>
public sealed class OfficialNewsCommand : InteractionModuleBase<SocketInteractionContext>
{
    public const string Register = "등록", Check = "확인", Unregister = "해제", Test = "테스트";
    private const string ActionSummary = "등록: 이 채널로 받기 · 확인: 현재 설정 · 해제: 끄기 · 테스트: 최근 글 하나 출력 (기본 확인)";

    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("공지알림", "공식 공지 새 글(점검 제외)을 받을 채널을 등록·확인·해제합니다.")]
    public Task NoticeAsync([Summary("작업", ActionSummary)][Choice(Register, Register)][Choice(Check, Check)][Choice(Unregister, Unregister)][Choice(Test, Test)] string action = Check)
        => RunAsync(NewsBoards.Notice, action);

    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("점검알림", "공식 점검 공지의 등록·제목 변경·완료를 받을 채널을 등록·확인·해제합니다.")]
    public Task MaintenanceAsync([Summary("작업", "등록: 이 채널로 받기 · 확인: 현재 설정 · 해제: 끄기 · 테스트: 예시 알림 출력 (기본 확인)")][Choice(Register, Register)][Choice(Check, Check)][Choice(Unregister, Unregister)][Choice(Test, Test)] string action = Check)
        => RunAsync(NewsBoards.Maintenance, action);

    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("업데이트알림", "공식 업데이트 새 글을 받을 채널을 등록·확인·해제합니다.")]
    public Task UpdateAsync([Summary("작업", ActionSummary)][Choice(Register, Register)][Choice(Check, Check)][Choice(Unregister, Unregister)][Choice(Test, Test)] string action = Check)
        => RunAsync(NewsBoards.Update, action);

    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("이벤트알림", "공식 이벤트 새 글을 받을 채널을 등록·확인·해제합니다. (이벤트 마감 알림과 별개)")]
    public Task EventsAsync([Summary("작업", ActionSummary)][Choice(Register, Register)][Choice(Check, Check)][Choice(Unregister, Unregister)][Choice(Test, Test)] string action = Check)
        => RunAsync(NewsBoards.Events, action);

    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    [SlashCommand("에린노트알림", "공식 에린 노트(개발자노트·CM노트) 새 글을 받을 채널을 등록·확인·해제합니다.")]
    public Task DevnoteAsync([Summary("작업", ActionSummary)][Choice(Register, Register)][Choice(Check, Check)][Choice(Unregister, Unregister)][Choice(Test, Test)] string action = Check)
        => RunAsync(NewsBoards.Devnote, action);

    private async Task RunAsync(NewsBoard board, string action)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("DM에서는 사용할 수 없어요. 알림을 받을 서버 채널에서 실행해주세요.", ephemeral: true);
            return;
        }
        if (!AlertCommandAccess.IsManager(Context.User))
        {
            await RespondAsync(AlertCommandAccess.DeniedMessage, ephemeral: true);
            return;
        }
        if (action == Register && !await CheckChannelAsync()) return;

        await DeferAsync(ephemeral: true);
        try
        {
            var service = Program.instance.OfficialNews;
            if (action == Test)
            {
                await (board == NewsBoards.Maintenance ? TestMaintenanceAsync(service) : TestAsync(service, board));
                return;
            }
            var message = action switch
            {
                Register => await RegisterAsync(service, board),
                Unregister => await UnregisterAsync(service, board),
                _ => await CheckAsync(service, board),
            };
            await FollowupAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[공식소식] /{board.CommandName} {action} 실패: {ex.GetType().Name}: {ex.Message}");
            await FollowupAsync("설정을 처리하지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
        }
    }

    private async Task<bool> CheckChannelAsync()
    {
        // 일반 텍스트·공지 채널만 받는다. 스레드·음성 채널의 채팅·포럼은 첫 버전에서 제외한다.
        if (Context.Channel is not SocketTextChannel channel || channel is SocketThreadChannel or SocketVoiceChannel)
        {
            await RespondAsync("공식 소식 알림은 서버의 일반 텍스트 채널이나 공지 채널에서만 등록할 수 있어요.", ephemeral: true);
            return false;
        }
        var permissions = Context.Guild.CurrentUser.GetPermissions(channel);
        // 썸네일이 없는 글은 기본 이미지를 파일로 첨부해 보내므로 파일 첨부 권한도 필요하다.
        if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks || !permissions.AttachFiles)
        {
            await RespondAsync("이 채널에서 몰리가 채널 보기·메시지 보내기·링크 첨부·파일 첨부 권한을 갖고 있지 않아요. 채널 권한을 확인해주세요.", ephemeral: true);
            return false;
        }
        return true;
    }

    private async Task<string> RegisterAsync(OfficialNewsService service, NewsBoard board)
    {
        var channelId = Context.Channel.Id;
        var previous = await service.Store.SetSubscriptionAsync(Context.Guild.Id, board.Id, channelId, DateTimeOffset.UtcNow);
        var text = new StringBuilder();
        if (previous is { Enabled: true } && previous.ChannelId == channelId)
            text.AppendLine($"**{board.Label}** 알림은 이미 이 채널(<#{channelId}>)로 받고 있어요.");
        else if (previous is { Enabled: true })
        {
            text.AppendLine($"**{board.Label}** 알림 채널을 <#{previous.ChannelId}>에서 이 채널(<#{channelId}>)로 바꿨어요.");
            text.AppendLine("이전 채널로 보내려던 대기 중인 알림은 보내지 않고, 다음 새 글부터 이 채널로 보내요.");
        }
        else if (board == NewsBoards.Maintenance)
        {
            text.AppendLine($"앞으로 감지하는 **{board.Label}** 공지의 새 글·제목 변경·완료를 이 채널(<#{channelId}>)로 보내요.");
            text.AppendLine("등록 전에 올라온 점검 공지와 지난 변경은 보내지 않아요.");
        }
        else
        {
            text.AppendLine($"앞으로 올라오는 **{board.Label}** 새 글을 이 채널(<#{channelId}>)로 보내요.");
            text.AppendLine("이미 올라온 글은 다시 보내지 않아요.");
        }
        if (board == NewsBoards.Notice)
            text.AppendLine("-# 점검 공지는 보내지 않아요. 점검 등록·변경·완료는 `/점검알림`으로 따로 받을 수 있어요.");
        if (board == NewsBoards.Events)
            text.AppendLine("-# 이벤트 마감 알림(`/이벤트마감알림`)과는 따로 설정돼요. 같은 채널에 함께 등록해도 돼요.");
        await AppendCollectorWarningAsync(service, board, text);
        return text.ToString().TrimEnd();
    }

    // 목록 첫 페이지를 지금 읽어 그중 한 글을 실제 알림과 같은 Embed로 보낸다. 수집 상태·발송 기록은 바꾸지 않는다.
    // 등록돼 있으면 등록 채널로 보내 권한까지 확인하고, 아니면 실행한 사람에게만 미리보기로 보여준다.
    private async Task TestAsync(OfficialNewsService service, NewsBoard board)
    {
        NewsListPage page;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try { page = await service.Collector.FetchLatestAsync(board, cts.Token); }
            catch (Exception ex)
            {
                Console.WriteLine($"[공식소식] /{board.CommandName} 테스트 목록 확인 실패: {ex.GetType().Name}: {ex.Message}");
                await FollowupAsync($"공식 홈페이지의 {board.Name} 목록을 읽지 못했어요. 잠시 후 다시 시도해주세요.", ephemeral: true);
                return;
            }
        }
        // 공지 알림은 점검 글을 보내지 않으므로 테스트에서도 고르지 않는다(모두 점검 글이면 그대로 쓴다).
        var cards = MaintenanceRules.Tracks(board) && page.Cards.Any(c => !MaintenanceRules.IsMaintenancePost(c.Title, c.Category))
            ? page.Cards.Where(c => !MaintenanceRules.IsMaintenancePost(c.Title, c.Category)).ToList()
            : page.Cards;
        var card = cards[Random.Shared.Next(cards.Count)];
        var message = OfficialNewsMessages.Build(board, OfficialNewsCollector.ToEvent(board, card, DateTimeOffset.UtcNow), isTest: true);
        await SendTestAsync(service, board, message, $"첫 페이지 글 {cards.Count}개 중 하나예요.");
    }

    // 실제 감지 기록과 무관한 가상 예시(새 점검·제목 변경·완료 중 하나)를 보낸다. 공식 홈페이지를 읽지 않고 수집 기준·이벤트·발송 기록을 바꾸지 않는다.
    private async Task TestMaintenanceAsync(OfficialNewsService service)
    {
        string[] kinds = [MaintenanceEventKinds.Created, MaintenanceEventKinds.TitleChanged, MaintenanceEventKinds.Completed];
        var message = MaintenanceMessages.Build(MaintenanceMessages.Example(kinds[Random.Shared.Next(kinds.Length)], DateTimeOffset.UtcNow), isTest: true);
        await SendTestAsync(service, NewsBoards.Maintenance, message, "새 점검·제목 변경·완료 예시 중 하나예요.");
    }

    private async Task SendTestAsync(OfficialNewsService service, NewsBoard board, NewsMessage message, string note)
    {
        var subscription = await service.Store.GetSubscriptionAsync(Context.Guild.Id, board.Id);
        if (subscription is not { Enabled: true })
        {
            var preview = $"**{board.Label}** 알림이 등록돼 있지 않아 여기에만 미리 보여드려요. {note}";
            if (message.AttachmentPath is { } path)
                await FollowupWithFileAsync(path, text: preview, embed: message.Embed, ephemeral: true, allowedMentions: AllowedMentions.None);
            else
                await FollowupAsync(preview, embed: message.Embed, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(OfficialNewsDeliveryWorker.SendTimeout);
            await service.Sender.SendAsync(subscription.ChannelId, message, cts.Token);
            await FollowupAsync($"<#{subscription.ChannelId}> 채널로 테스트 알림을 보냈어요. {note}", ephemeral: true);
        }
        catch (Exception ex) when (ex is NewsDeliveryException or OperationCanceledException or Discord.Net.HttpException)
        {
            var reason = ex is NewsDeliveryException { Permanent: true } ? "채널이 없거나 몰리의 권한(채널 보기·메시지 보내기·링크 첨부·파일 첨부)이 부족해요" : "잠시 Discord에 보내지 못했어요";
            await FollowupAsync($"<#{subscription.ChannelId}> 채널로 테스트 알림을 보내지 못했어요. {reason}.", ephemeral: true);
        }
    }

    private async Task<string> UnregisterAsync(OfficialNewsService service, NewsBoard board)
    {
        var removed = await service.Store.DisableSubscriptionAsync(Context.Guild.Id, board.Id, DateTimeOffset.UtcNow);
        if (removed is null) return $"이 서버에는 등록된 **{board.Label}** 알림이 없어요.";
        var text = $"<#{removed.ChannelId}> 채널의 **{board.Label}** 알림을 해제했어요. 아직 보내지 않은 알림도 보내지 않아요.";
        if (board == NewsBoards.Events) text += "\n-# 이벤트 마감 알림은 그대로예요. 끄려면 `/이벤트마감알림 작업:해제`를 사용하세요.";
        return text;
    }

    private async Task<string> CheckAsync(OfficialNewsService service, NewsBoard board)
    {
        var subscription = await service.Store.GetSubscriptionAsync(Context.Guild.Id, board.Id);
        var text = new StringBuilder();
        if (subscription is not { Enabled: true })
        {
            text.AppendLine($"**{board.Label}** 알림이 꺼져 있어요.");
            text.AppendLine($"받을 채널에서 `/{board.CommandName} 작업:{Register}`을 실행하면 {(board == NewsBoards.Maintenance ? "점검 공지의 등록·제목 변경·완료를" : "새 글을")} 알려드려요.");
        }
        else
        {
            var summary = await service.Store.GetDeliverySummaryAsync(Context.Guild.Id, board.Id);
            text.AppendLine(board == NewsBoards.Maintenance
                ? $"**{board.Label}** 공지의 새 글·제목 변경·완료를 <#{subscription.ChannelId}> 채널로 보내고 있어요."
                : $"**{board.Label}** 새 글을 <#{subscription.ChannelId}> 채널로 보내고 있어요.");
            text.AppendLine($"- 등록·변경: {Kst(subscription.UpdatedAt)}");
            text.AppendLine(summary.LastSentAt is { } lastSent ? $"- 마지막 발송: {Kst(lastSent)}" : "- 아직 보낸 알림이 없어요.");
            if (summary.Waiting > 0) text.AppendLine($"- 보내는 중인 알림: {summary.Waiting}개");
            if (summary.Failed > 0) text.AppendLine($"- ⚠️ 보내지 못한 알림: {summary.Failed}개 (채널 삭제·권한 변경 등). 채널 권한을 확인하고 다시 등록해주세요.");
            var state = await service.Store.GetBoardStateAsync(board.SourceBoard);
            if (state?.LastSuccessAt is { } lastSuccess) text.AppendLine($"- 공식 홈페이지 마지막 확인: {Kst(lastSuccess)}");
        }
        await AppendCollectorWarningAsync(service, board, text);
        return text.ToString().TrimEnd();
    }

    private static async Task AppendCollectorWarningAsync(OfficialNewsService service, NewsBoard board, StringBuilder text)
    {
        if (!service.IsCollecting)
        {
            text.AppendLine("-# ⚠️ 지금은 몰리가 공식 소식을 확인하지 않고 있어요. 설정은 저장되며, 확인이 켜진 뒤 올라오는 새 글부터 보내요.");
            return;
        }
        var state = await service.Store.GetBoardStateAsync(board.SourceBoard);
        if (state?.LastSuccessAt is { } lastSuccess && DateTimeOffset.UtcNow - lastSuccess > OfficialNewsService.StaleAfter)
            text.AppendLine($"-# ⚠️ {Kst(lastSuccess)} 이후 공식 홈페이지 목록을 확인하지 못하고 있어요. 확인이 되면 그동안 올라온 글도 보내요.");
    }

    private static string Kst(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, MobiTime.timezone).ToString("yyyy-MM-dd HH:mm");
}
