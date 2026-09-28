using Discord;
using DiscordBot_Molly.Commands;
using Microsoft.Data.Sqlite;
using Molly.Attendance;
using Molly.Currency;

/// <summary>출석일 계산(오전 6시 KST), 출석 기록·증표 지급의 원자성과 중복 방지, 출석부 등록·해제·갱신·복구, 명단 표시를 검사한다.</summary>
internal static class AttendanceTests
{
    private const ulong Guild = 100, OtherGuild = 200, User = 10, Other = 11;

    // 2026-09-28 06:00 KST
    private static readonly DateTimeOffset s_Morning = new(2026, 9, 27, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly s_Day = new(2026, 9, 28);

    public static async Task RunAsync()
    {
        ClockTests();
        RendererTests();
        var directory = Path.Combine(Path.GetTempPath(), "molly-attendance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var databasePath = Path.Combine(directory, "molly.sqlite");
            var store = new AttendanceStore(databasePath);
            await store.InitializeAsync();
            await store.InitializeAsync();
            var tokens = new MollyTokenStore(databasePath);
            await tokens.InitializeAsync();
            await StoreTestsAsync(store, tokens);
            await ServiceTestsAsync(store, tokens);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void ClockTests()
    {
        var kst = TimeSpan.FromHours(9);
        Assert(AttendanceClock.LogicalDate(new DateTimeOffset(2026, 9, 29, 2, 0, 0, kst)) == s_Day, "새벽 2시는 전날 출석일");
        Assert(AttendanceClock.LogicalDate(new DateTimeOffset(2026, 9, 29, 5, 59, 59, kst)) == s_Day, "오전 5시 59분까지 전날 출석일");
        Assert(AttendanceClock.LogicalDate(new DateTimeOffset(2026, 9, 29, 6, 0, 0, kst)) == s_Day.AddDays(1), "오전 6시부터 새 출석일");
        Assert(AttendanceClock.LogicalDate(s_Morning) == s_Day, "UTC 시각도 KST 기준으로 계산");
        Assert(AttendanceClock.NextResetUtc(new DateTimeOffset(2026, 9, 29, 2, 0, 0, kst)) == new DateTimeOffset(2026, 9, 29, 6, 0, 0, kst)
            && AttendanceClock.NextResetUtc(new DateTimeOffset(2026, 9, 29, 6, 0, 0, kst)) == new DateTimeOffset(2026, 9, 30, 6, 0, 0, kst),
            "다음 초기화는 다가오는 오전 6시(KST)");
    }

    private static void RendererTests()
    {
        // 2026-09-28 실제 증표규칙 시트 내용
        var rules = TokenRuleCsvReader.Parse("ID,활성화,값,자료형,최소값,최대값,단위,설명,비고\n" +
            "attendance_reward_amount,TRUE,50,정수,1,1000,개,출석 버튼 1회 지급량,밸런싱 중\n", DateTimeOffset.UtcNow);
        Assert(rules.GetInteger(AttendanceService.RewardRuleId, 0) == 50, "증표규칙 시트의 출석 지급량을 규칙 ID로 읽음");
        Assert(AttendanceBoardRenderer.Title(s_Day) == "📅 2026년 9월 28일 월요일 출석부", "출석부 제목은 날짜와 요일");
        var empty = AttendanceBoardRenderer.BuildEmbed(new(s_Day, [], 50, true));
        Assert(empty.Description.Contains("출석 인원: 0명") && empty.Description.Contains("아직 출석한 사람이 없어요") && empty.Description.Contains("마물 퇴치 증표 50개")
            && empty.Footer!.Value.Text.Contains("오전 6시"), "빈 출석부는 인원 0명·보상·초기화 시각 안내");

        var names = Enumerable.Range(1, 400).Select(i => $"아주아주긴이름을가진모험가{i:000}").ToList();
        var listed = AttendanceBoardRenderer.FormatNames(names);
        var embed = AttendanceBoardRenderer.BuildEmbed(new(s_Day, names, 50, true));
        Assert(listed.Length <= AttendanceBoardRenderer.MaxNamesLength + 20 && listed.EndsWith("명") && listed.Contains("… 외 ")
            && embed.Description.Contains("출석 인원: 400명") && embed.Description.Length <= 4096, "명단이 길면 앞쪽만 보여주고 '외 N명'으로 줄이되 전체 인원은 유지");
        Assert(AttendanceBoardRenderer.FormatNames(["*별*"]) == "1. \\*별\\*", "이름의 서식 문자는 이스케이프");

        var active = AttendanceBoardRenderer.BuildComponents(new(s_Day, [], 50, true));
        var inactive = AttendanceBoardRenderer.BuildComponents(new(s_Day, [], 0, false));
        static ButtonComponent Button(MessageComponent c) => (ButtonComponent)((ActionRowComponent)c.Components.First()).Components.First();
        Assert(Button(active).CustomId == "attendance:check_in" && !Button(active).IsDisabled && Button(inactive).IsDisabled,
            "출석 버튼은 고정 ID이고, 사용하지 않는 출석부에서는 꺼짐");
        Assert(AttendanceBoardRenderer.BuildEmbed(new(s_Day, [], 0, false)).Description.Contains("더 이상 사용하지 않아요"), "꺼진 출석부 안내");

        Assert(AttendanceCommand.Message(new(AttendanceClickStatus.CheckedIn, 50, 120)).StartsWith("출석했습니다! 마물 퇴치 증표 50개를 받았습니다."),
            "출석 응답은 증표 풀네임과 지급량");
        Assert(AttendanceCommand.Message(new(AttendanceClickStatus.AlreadyCheckedIn)) == "오늘은 이미 출석했습니다. 다음 출석은 오전 6시부터 가능합니다.",
            "이미 출석한 사용자 안내");
    }

    private static async Task StoreTestsAsync(AttendanceStore store, MollyTokenStore tokens)
    {
        var first = await store.CheckInAsync(Guild, s_Day, User, "종잇장", 50, s_Morning);
        Assert(first is { Status: AttendanceCheckInStatus.CheckedIn, RewardAmount: 50, Balance: 50 } && await tokens.GetBalanceAsync(Guild, User) == 50,
            "출석하면 증표 지급");
        var again = await store.CheckInAsync(Guild, s_Day, User, "종잇장", 50, s_Morning);
        Assert(again.Status == AttendanceCheckInStatus.AlreadyCheckedIn && await tokens.GetBalanceAsync(Guild, User) == 50, "같은 출석일에는 다시 지급하지 않음");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.CheckInAsync(Guild, s_Day, Other, "핏피", 50, s_Morning))));
        Assert(results.Count(x => x.Status == AttendanceCheckInStatus.CheckedIn) == 1 && await tokens.GetBalanceAsync(Guild, Other) == 50,
            "동시에 여러 번 눌러도 한 번만 출석·지급");

        Assert((await store.CheckInAsync(Guild, s_Day.AddDays(1), User, "종잇장", 30, s_Morning.AddDays(1))).Balance == 80, "다음 출석일에는 다시 출석 가능");
        Assert((await store.CheckInAsync(OtherGuild, s_Day, User, "종잇장", 50, s_Morning)).Status == AttendanceCheckInStatus.CheckedIn
            && await tokens.GetBalanceAsync(OtherGuild, User) == 50 && await tokens.GetBalanceAsync(Guild, User) == 80, "서버마다 따로 출석·지급");

        var entries = await store.GetEntriesAsync(Guild, s_Day);
        Assert(entries.Select(x => (x.DisplayName, x.RewardAmount)).SequenceEqual([("종잇장", 50L), ("핏피", 50L)]), "명단은 출석 순서대로 실제 지급량과 함께 기록");
        var ledger = await tokens.GetTransactionsAsync(Guild, User);
        Assert(ledger.All(x => x.ReasonType == TokenReasons.AttendanceReward) && ledger.Select(x => x.ReferenceId).SequenceEqual(["attendance:2026-09-29", "attendance:2026-09-28"]),
            "원장에 출석 보상 사유와 출석일 참조 ID 기록");

        // 지급이 실패하면(같은 참조 ID에 다른 금액이 이미 있음) 출석 기록도 남기지 않는다.
        const ulong broken = 12;
        await tokens.ApplyAsync(Guild, new(broken, 30, TokenReasons.AttendanceReward, "attendance:2026-09-28"));
        try { await store.CheckInAsync(Guild, s_Day, broken, "고장", 50, s_Morning); throw new Exception("지급 실패가 전달되지 않음"); }
        catch (InvalidOperationException) { }
        Assert((await store.GetEntriesAsync(Guild, s_Day)).All(x => x.UserId != broken) && await tokens.GetBalanceAsync(Guild, broken) == 30,
            "증표 지급이 실패하면 출석 기록도 함께 취소");
    }

    private sealed class FakeGateway : IAttendanceBoardGateway
    {
        private ulong m_NextId = 1000;
        public readonly Dictionary<ulong, (ulong Channel, AttendanceBoard Board)> Messages = new();
        public int Sends, Edits;
        public bool ChannelGone;

        public Task<ulong> SendAsync(ulong channelId, AttendanceBoard board, CancellationToken ct)
        {
            if (ChannelGone) throw new AttendanceChannelUnavailableException("gone");
            Sends++;
            var id = ++m_NextId;
            Messages[id] = (channelId, board);
            return Task.FromResult(id);
        }

        public Task<bool> EditAsync(ulong channelId, ulong messageId, AttendanceBoard board, CancellationToken ct)
        {
            if (ChannelGone) throw new AttendanceChannelUnavailableException("gone");
            if (!Messages.ContainsKey(messageId)) return Task.FromResult(false);
            Edits++;
            Messages[messageId] = (channelId, board);
            return Task.FromResult(true);
        }
    }

    private static async Task ServiceTestsAsync(AttendanceStore store, MollyTokenStore tokens)
    {
        const ulong guild = 300, channelA = 1, channelB = 2;
        var now = s_Morning;
        var gateway = new FakeGateway();
        var service = new AttendanceService(store, gateway, _ => Task.FromResult(50L), () => now, _ => { }, TimeSpan.FromMilliseconds(200));

        Assert(await service.RegisterAsync(guild, channelA) == AttendanceRegisterResult.Created && gateway.Sends == 1, "출석부 등록 시 메시지 생성");
        var setting = (await store.GetSettingAsync(guild))!;
        Assert(setting is { Enabled: true, ChannelId: channelA } && setting.CurrentDate == s_Day, "등록한 채널·메시지·출석일 저장");

        Assert((await service.CheckInAsync(guild, 999, User, "종잇장")).Status == AttendanceClickStatus.InactiveBoard, "다른 메시지의 버튼은 받지 않음");
        var click = await service.CheckInAsync(guild, setting.MessageId, User, "종잇장");
        Assert(click is { Status: AttendanceClickStatus.CheckedIn, RewardAmount: 50, Balance: 50 }, "활성 출석부 버튼으로 출석");
        Assert((await service.CheckInAsync(guild, setting.MessageId, User, "종잇장")).Status == AttendanceClickStatus.AlreadyCheckedIn, "두 번째 클릭은 이미 출석");

        // 버튼이 몰려도 메시지 수정은 한 번으로 모은다.
        var editsBefore = gateway.Edits;
        var refreshes = new List<Task>();
        foreach (var user in new ulong[] { 21, 22, 23, 24 })
        {
            await service.CheckInAsync(guild, setting.MessageId, user, "사용자" + user);
            refreshes.Add(service.RequestRefresh(guild));
        }
        await Task.WhenAll(refreshes);
        Assert(gateway.Edits == editsBefore + 1 && gateway.Messages[setting.MessageId].Board.Names.Count == 5, "짧은 간격의 명단 갱신은 한 번으로 모아 최신 명단 반영");

        // 같은 채널 재등록: 새 메시지로 바꾸고 옛 버튼은 끈다. 오늘 기록은 이어진다.
        Assert(await service.RegisterAsync(guild, channelA) == AttendanceRegisterResult.Reposted, "같은 채널 재등록은 새 메시지로 교체");
        var reposted = (await store.GetSettingAsync(guild))!;
        Assert(reposted.MessageId != setting.MessageId && !gateway.Messages[setting.MessageId].Board.Active
            && gateway.Messages[reposted.MessageId].Board.Names.Count == 5, "옛 출석부 버튼은 꺼지고 새 출석부에 오늘 명단 유지");
        Assert((await service.CheckInAsync(guild, setting.MessageId, 30, "늦은사람")).Status == AttendanceClickStatus.InactiveBoard, "옛 출석부 버튼으로는 출석 불가");

        Assert(await service.RegisterAsync(guild, channelB) == AttendanceRegisterResult.Moved, "다른 채널 재등록은 이동");
        var moved = (await store.GetSettingAsync(guild))!;
        Assert(moved.ChannelId == channelB && gateway.Messages[moved.MessageId].Channel == channelB && !gateway.Messages[reposted.MessageId].Board.Active,
            "이동한 채널에 새 출석부, 이전 채널 출석부는 꺼짐");

        // 오전 6시가 지나면 같은 메시지를 새 날짜와 빈 명단으로 바꾼다.
        now = s_Morning.AddDays(1);
        var sends = gateway.Sends;
        await service.RefreshAllAsync();
        var nextDay = (await store.GetSettingAsync(guild))!;
        Assert(nextDay.MessageId == moved.MessageId && nextDay.CurrentDate == s_Day.AddDays(1) && gateway.Sends == sends
            && gateway.Messages[moved.MessageId].Board is { Names.Count: 0, Active: true } && gateway.Messages[moved.MessageId].Board.Date == s_Day.AddDays(1),
            "날짜가 바뀌면 같은 메시지를 새 날짜·빈 명단으로 수정");
        Assert((await service.CheckInAsync(guild, moved.MessageId, User, "종잇장")).Balance == 100, "새 날짜에는 다시 출석");

        // 메시지가 지워지면 같은 채널에 새로 만들고 당일 기록은 유지한다.
        gateway.Messages.Remove(moved.MessageId);
        await service.RefreshAsync(guild);
        var recreated = (await store.GetSettingAsync(guild))!;
        Assert(recreated.MessageId != moved.MessageId && recreated.Enabled && gateway.Messages[recreated.MessageId] is { Channel: channelB, Board.Names.Count: 1 },
            "지워진 출석부는 같은 채널에 다시 만들고 당일 명단 유지");

        await service.UnregisterAsync(guild);
        Assert(!(await store.GetSettingAsync(guild))!.Enabled && !gateway.Messages[recreated.MessageId].Board.Active, "해제하면 출석부를 끄고 버튼 비활성화");
        Assert(!await service.UnregisterAsync(guild), "활성 출석부가 없으면 해제할 것이 없음");
        Assert((await service.CheckInAsync(guild, recreated.MessageId, 40, "해제후")).Status == AttendanceClickStatus.InactiveBoard
            && await tokens.GetBalanceAsync(guild, User) == 100, "해제한 출석부로는 출석 불가, 받은 증표는 유지");

        // 채널에 접근할 수 없으면 비활성화한다.
        await service.RegisterAsync(guild, channelA);
        gateway.ChannelGone = true;
        await service.RefreshAsync(guild);
        Assert(!(await store.GetSettingAsync(guild))!.Enabled, "채널이 사라지면 출석부 비활성화");
        gateway.ChannelGone = false;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
