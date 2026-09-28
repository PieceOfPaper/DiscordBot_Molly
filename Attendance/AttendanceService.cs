using System.Collections.Concurrent;

namespace Molly.Attendance;

/// <summary>출석부 채널이 삭제되었거나 봇이 접근할 수 없음. 출석부를 비활성화한다.</summary>
public sealed class AttendanceChannelUnavailableException(string message) : Exception(message);

/// <summary>출석부 메시지를 보내고 고치는 Discord 호출. 테스트에서 대신할 수 있게 분리한다.</summary>
public interface IAttendanceBoardGateway
{
    /// <summary>새 출석부 메시지를 보내고 메시지 ID를 돌려준다.</summary>
    Task<ulong> SendAsync(ulong channelId, AttendanceBoard board, CancellationToken ct);

    /// <summary>기존 출석부 메시지를 고친다. 메시지가 지워졌으면 false.</summary>
    Task<bool> EditAsync(ulong channelId, ulong messageId, AttendanceBoard board, CancellationToken ct);
}

public enum AttendanceRegisterResult { Created, Moved, Reposted }

public enum AttendanceClickStatus { CheckedIn, AlreadyCheckedIn, InactiveBoard }

public sealed record AttendanceClickResult(AttendanceClickStatus Status, long RewardAmount = 0, long Balance = 0);

/// <summary>
/// 출석부 등록·해제, 출석 버튼 처리, 명단 메시지 갱신, 매일 오전 6시 초기화를 맡는다.
/// 메시지는 DB를 기준으로 다시 그리므로, 메시지 수정에 실패해도 출석 기록과 지급은 되돌리지 않는다.
/// </summary>
public sealed class AttendanceService
{
    public const string RewardRuleId = "attendance_reward_amount";
    public const long DefaultReward = 50;

    private readonly AttendanceStore m_Store;
    private readonly IAttendanceBoardGateway m_Gateway;
    private readonly Func<CancellationToken, Task<long>> m_Reward;
    private readonly Func<DateTimeOffset> m_UtcNow;
    private readonly Action<string> m_Log;
    private readonly TimeSpan m_RefreshDelay;
    private readonly SemaphoreSlim m_Gate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, byte> m_ScheduledRefreshes = new();

    /// <param name="reward">지금 적용할 출석 지급량. 증표규칙 시트 값을 읽는다.</param>
    /// <param name="refreshDelay">버튼이 몰릴 때 메시지 수정을 한 번으로 모으는 대기 시간.</param>
    public AttendanceService(AttendanceStore store, IAttendanceBoardGateway gateway, Func<CancellationToken, Task<long>> reward,
        Func<DateTimeOffset>? utcNow = null, Action<string>? log = null, TimeSpan? refreshDelay = null)
    {
        m_Store = store;
        m_Gateway = gateway;
        m_Reward = reward;
        m_UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        m_Log = message => (log ?? Console.WriteLine).Invoke("[출석] " + message);
        m_RefreshDelay = refreshDelay ?? TimeSpan.FromSeconds(1.5);
    }

    public AttendanceStore Store => m_Store;

    /// <summary>
    /// 이 채널에 새 출석부를 만든다. 이미 활성 출석부가 있으면 옛 메시지의 버튼을 끄고 새 메시지로 바꾼다.
    /// 같은 날의 출석 기록은 그대로 이어진다. 새 메시지를 보내지 못하면 기존 출석부를 그대로 둔다.
    /// </summary>
    public async Task<AttendanceRegisterResult> RegisterAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var old = await m_Store.GetSettingAsync(guildId, ct).ConfigureAwait(false);
            var board = await BuildBoardAsync(guildId, ct).ConfigureAwait(false);
            var messageId = await m_Gateway.SendAsync(channelId, board, ct).ConfigureAwait(false);
            await m_Store.SaveSettingAsync(new(guildId, channelId, messageId, board.Date, true), ct).ConfigureAwait(false);

            if (old is not { Enabled: true }) return AttendanceRegisterResult.Created;
            await DeactivateMessageAsync(old, ct).ConfigureAwait(false);
            return old.ChannelId == channelId ? AttendanceRegisterResult.Reposted : AttendanceRegisterResult.Moved;
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>출석부를 끄고 메시지의 버튼을 비활성화한다. 활성 출석부가 없으면 false. 출석 기록과 증표는 남는다.</summary>
    public async Task<bool> UnregisterAsync(ulong guildId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var setting = await m_Store.GetSettingAsync(guildId, ct).ConfigureAwait(false);
            if (setting is not { Enabled: true }) return false;
            await m_Store.SaveSettingAsync(setting with { Enabled = false }, ct).ConfigureAwait(false);
            await DeactivateMessageAsync(setting, ct).ConfigureAwait(false);
            return true;
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>
    /// 출석 버튼 처리. 지금 활성인 출석부 메시지의 버튼만 받는다. 날짜는 누른 시각으로 계산하므로 오전 6시 갱신이 늦어도 새 날짜로 출석한다.
    /// 명단 메시지 수정은 호출한 쪽이 <see cref="RequestRefresh"/>로 요청한다.
    /// </summary>
    public async Task<AttendanceClickResult> CheckInAsync(ulong guildId, ulong messageId, ulong userId, string displayName, CancellationToken ct = default)
    {
        var setting = await m_Store.GetSettingAsync(guildId, ct).ConfigureAwait(false);
        if (setting is not { Enabled: true } || setting.MessageId != messageId) return new(AttendanceClickStatus.InactiveBoard);

        var now = m_UtcNow();
        var reward = await m_Reward(ct).ConfigureAwait(false);
        var result = await m_Store.CheckInAsync(guildId, AttendanceClock.LogicalDate(now), userId, displayName, reward, now, ct).ConfigureAwait(false);
        return result.Status == AttendanceCheckInStatus.CheckedIn
            ? new(AttendanceClickStatus.CheckedIn, result.RewardAmount, result.Balance)
            : new(AttendanceClickStatus.AlreadyCheckedIn);
    }

    /// <summary>
    /// 명단 메시지 수정을 예약한다. 대기 시간 안에 들어온 요청은 한 번의 수정으로 모은다.
    /// 대기가 끝난 뒤 들어온 요청은 새로 예약되므로 마지막 출석까지 반드시 반영된다.
    /// </summary>
    public Task RequestRefresh(ulong guildId)
    {
        if (!m_ScheduledRefreshes.TryAdd(guildId, 0)) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try { await Task.Delay(m_RefreshDelay).ConfigureAwait(false); }
            finally { m_ScheduledRefreshes.TryRemove(guildId, out _); }
            await RefreshAsync(guildId).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// DB 기준으로 출석부 메시지를 다시 그린다. 날짜가 바뀌었으면 새 날짜와 빈 명단으로 바꾼다.
    /// 메시지가 지워졌으면 같은 채널에 새로 만들고, 채널에 접근할 수 없으면 출석부를 비활성화한다.
    /// </summary>
    public async Task RefreshAsync(ulong guildId, CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var setting = await m_Store.GetSettingAsync(guildId, ct).ConfigureAwait(false);
            if (setting is not { Enabled: true }) return;
            var board = await BuildBoardAsync(guildId, ct).ConfigureAwait(false);
            try
            {
                var messageId = setting.MessageId;
                if (!await m_Gateway.EditAsync(setting.ChannelId, messageId, board, ct).ConfigureAwait(false))
                {
                    messageId = await m_Gateway.SendAsync(setting.ChannelId, board, ct).ConfigureAwait(false);
                    m_Log($"서버 {guildId}: 출석부 메시지가 없어 같은 채널에 다시 만들었습니다.");
                }
                if (messageId != setting.MessageId || board.Date != setting.CurrentDate)
                    await m_Store.SaveSettingAsync(setting with { MessageId = messageId, CurrentDate = board.Date }, ct).ConfigureAwait(false);
            }
            catch (AttendanceChannelUnavailableException ex)
            {
                await m_Store.SaveSettingAsync(setting with { Enabled = false }, ct).ConfigureAwait(false);
                m_Log($"서버 {guildId}: 출석부 채널에 접근할 수 없어 비활성화했습니다. /출석부등록으로 다시 등록해야 합니다. ({ex.Message})");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { m_Log($"서버 {guildId}: 출석부 갱신 실패: {ex.GetType().Name}: {ex.Message}"); }
        finally { m_Gate.Release(); }
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var setting in await m_Store.GetEnabledSettingsAsync(ct).ConfigureAwait(false))
            await RefreshAsync(setting.GuildId, ct).ConfigureAwait(false);
    }

    /// <summary>시작할 때 모든 출석부를 오늘 기준으로 맞추고(재시작·메시지 삭제 복구), 이후 매일 오전 6시에 새 날짜로 바꾼다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAllAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var next = AttendanceClock.NextResetUtc(m_UtcNow());
                // Task.Delay가 시계보다 조금 일찍 깨어날 수 있어 초기화 시각이 지날 때까지 다시 기다린다.
                for (var remaining = next - m_UtcNow(); remaining > TimeSpan.Zero; remaining = next - m_UtcNow())
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                m_Log("오전 6시 출석부 초기화");
                await RefreshAllAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<AttendanceBoard> BuildBoardAsync(ulong guildId, CancellationToken ct)
    {
        var date = AttendanceClock.LogicalDate(m_UtcNow());
        var entries = await m_Store.GetEntriesAsync(guildId, date, ct).ConfigureAwait(false);
        var reward = await m_Reward(ct).ConfigureAwait(false);
        return new(date, entries.Select(x => x.DisplayName).ToList(), reward, Active: true);
    }

    // 옛 메시지의 버튼을 끈다. 이미 지워졌거나 접근할 수 없어도 새 출석부 등록·해제는 계속한다.
    private async Task DeactivateMessageAsync(AttendanceSetting setting, CancellationToken ct)
    {
        try
        {
            await m_Gateway.EditAsync(setting.ChannelId, setting.MessageId,
                new AttendanceBoard(setting.CurrentDate, [], 0, Active: false), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { m_Log($"서버 {setting.GuildId}: 이전 출석부 버튼을 끄지 못했습니다: {ex.GetType().Name}: {ex.Message}"); }
    }
}
