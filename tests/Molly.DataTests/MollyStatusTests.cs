using Discord;
using Microsoft.Data.Sqlite;
using Molly.Crafting;
using Molly.HaeyeonMarket;
using Molly.KeywordMarket;
using Molly.Market;
using Molly.MobiLife;
using Molly.OfficialNews;
using Molly.Status;

/// <summary>
/// /몰리상태: 수집 상태 저장·판정(정상·준비 중·최근 확인 실패·확인 지연·수집 불가·사용 안 함), 서버별 전송 상태(복구·채널 변경),
/// 채널 권한 판정, 응답 문구·분할, 재시작 시 기록 유지·진행 중 초기화, 조회 시 외부 호출 없음을 검사한다.
/// </summary>
internal static class MollyStatusTests
{
    // 2026-10-29 20:15 KST
    private static readonly DateTimeOffset s_Now = new(2026, 10, 29, 11, 15, 0, TimeSpan.Zero);
    private const ulong Guild = 100, OtherGuild = 200, ChNews = 11, ChMarket = 12, ChOther = 13;

    public static async Task RunAsync()
    {
        JudgeTests();
        DeliveryViewTests();
        PermissionTests();
        ErrorKindTests();
        ReportTests();
        CommandTests();
        var directory = Path.Combine(Path.GetTempPath(), "molly-status-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await StoreTestsAsync(Path.Combine(directory, "store.sqlite"));
            await TrackerTestsAsync(directory);
            await MonitorRecordingTestsAsync(Path.Combine(directory, "monitor.sqlite"));
            await NewsDeliveryTestsAsync(Path.Combine(directory, "news.sqlite"));
            await GatherTestsAsync(Path.Combine(directory, "gather.sqlite"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static JobStatusRecord Success(DateTimeOffset at) => new("job", at, at, true, null, 0);
    private static JobStatusRecord Failure(DateTimeOffset at, DateTimeOffset? lastSuccess, int failures = 1, string kind = StatusErrorKinds.Timeout) =>
        new("job", at, lastSuccess, false, kind, failures);

    private static void JudgeTests()
    {
        var news = StatusJobs.NewsSchedule;
        var market = StatusJobs.MarketHourlySchedule;
        var started = s_Now.AddHours(-2);
        CollectionState Judge(JobStatusInput input, JobSchedule schedule, DateTimeOffset? processStart = null, DateTimeOffset? now = null) =>
            CollectionStatusJudge.Judge(input, schedule, processStart ?? started, now ?? s_Now).State;

        Assert(Judge(new(Success(s_Now.AddMinutes(-1)), null, true), news) == CollectionState.Ok,
            "새 글이 없어도 최근 확인이 성공했으면 정상");
        Assert(Judge(new(null, null, false), news, processStart: s_Now.AddMinutes(-1)) == CollectionState.Preparing,
            "최초 실행 직후 기록이 없으면 준비 중");
        Assert(Judge(new(null, s_Now.AddMinutes(-1), false), news, processStart: s_Now.AddMinutes(-2)) == CollectionState.Preparing,
            "이번 실행의 첫 확인이 진행 중이면 준비 중");
        Assert(Judge(new(null, null, false), news, processStart: s_Now.AddMinutes(-30)) == CollectionState.Delayed,
            "첫 확인 허용 시간이 지났는데 기록이 없으면 확인 지연");
        Assert(Judge(new(Failure(s_Now.AddMinutes(-1), null), null, false), news) == CollectionState.Unavailable,
            "실패했고 정상 데이터가 없으면 수집 불가");
        Assert(Judge(new(Failure(s_Now.AddMinutes(-1), s_Now.AddMinutes(-11), 2), null, true), news) == CollectionState.RecentFailure,
            "이전 정상 데이터가 있는 상태의 실패는 최근 확인 실패");
        Assert(Judge(new(Success(s_Now.AddMinutes(-19)), null, true), news) == CollectionState.Ok
               && Judge(new(Success(s_Now.AddMinutes(-21)), null, true), news) == CollectionState.Delayed,
            "공식 소식은 마지막 확인 뒤 20분(주기 10분 + 한 주기)이 지나면 확인 지연");
        var lastHour = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero); // 19:00 KST
        Assert(Judge(new(Success(lastHour), null, true), market, now: lastHour.AddMinutes(69)) == CollectionState.Ok
               && Judge(new(Success(lastHour), null, true), market, now: lastHour.AddMinutes(71)) == CollectionState.Delayed,
            "시세는 다음 정각 뒤 재시도 간격(10분)이 지나면 확인 지연(공홈과 다른 기준)");
        Assert(Judge(new(Success(lastHour.AddHours(1)), null, true), market, now: lastHour.AddMinutes(71)) == CollectionState.Ok,
            "실패 뒤 다음 시도가 성공하면 정상으로 복구");
        Assert(Judge(new(Success(s_Now.AddMinutes(-30)), s_Now.AddMinutes(-15), true), news) == CollectionState.Delayed
               && Judge(new(Success(s_Now.AddMinutes(-30)), s_Now.AddMinutes(-5), true), news) == CollectionState.Ok,
            "진행 중인 작업도 최대 진행 시간(10분)을 넘기면 확인 지연");
        Assert(Judge(new(Success(s_Now.AddMinutes(-5)), null, true), news, processStart: s_Now.AddMinutes(-1)) == CollectionState.Ok,
            "재시작 직후라도 이전 실행의 성공이 기한 안이면 정상");
        Assert(Judge(new(Failure(s_Now.AddHours(-3), s_Now.AddHours(-4)), null, true), news, processStart: s_Now.AddMinutes(-1)) == CollectionState.Preparing,
            "재시작 직후 이전 기록이 오래됐으면 준비 중");
        Assert(Judge(new(Failure(s_Now.AddMinutes(-1), null), null, false, new JobDisabled(true, "키 없음")), market) == CollectionState.Disabled,
            "설정으로 수집하지 않으면 실패 기록보다 사용 안 함이 우선");
        Assert(CollectionStatusJudge.IsProblem(new(CollectionState.Disabled, null, new JobDisabled(true, "키 없음")))
               && !CollectionStatusJudge.IsProblem(new(CollectionState.Disabled, null, new JobDisabled(false, "꺼 둠")))
               && !CollectionStatusJudge.IsProblem(new(CollectionState.Preparing, null)),
            "필수 설정 누락만 문제로 보고 의도적 비활성화·준비 중은 문제가 아님");

        var merged = JobStatusRecord.Merge("job", Failure(s_Now.AddMinutes(-20), null), s_Now.AddMinutes(-5));
        Assert(merged is { LastSucceeded: true, ConsecutiveFailures: 0 } && merged.LastSuccessAt == s_Now.AddMinutes(-5),
            "기존 저장소의 더 최근 성공 시각을 병합하면 이전 실패는 해제");
        merged = JobStatusRecord.Merge("job", Failure(s_Now.AddMinutes(-1), null), s_Now.AddMinutes(-5));
        Assert(merged is { LastSucceeded: false } && merged.LastSuccessAt == s_Now.AddMinutes(-5),
            "성공 뒤의 실패는 유지하고 마지막 성공 시각만 채움");
        Assert(JobStatusRecord.Merge("job", null, s_Now) is { LastSucceeded: true } && JobStatusRecord.Merge("job", null, null) is null,
            "이 기능 전의 수집 기록도 마지막 성공으로 사용");
    }

    private static void DeliveryViewTests()
    {
        Assert(DeliveryView.For(null, ChNews) == DeliveryView.None, "전송 기록이 없으면 실패가 아님");
        var record = new AlertDeliveryRecord(Guild, "k", s_Now.AddHours(-2), ChNews, s_Now.AddHours(-1), ChNews, StatusErrorKinds.Permission);
        Assert(DeliveryView.For(record, ChNews) is { HasUnresolvedFailure: true, ErrorKind: StatusErrorKinds.Permission },
            "과거 성공이 그 뒤의 전송 실패를 가리지 않음");
        Assert(!DeliveryView.For(record with { LastSuccessAt = s_Now }, ChNews).HasUnresolvedFailure, "실패 뒤 정상 전송이면 복구");
        Assert(DeliveryView.For(record, ChOther) == DeliveryView.None, "채널을 바꾸면 이전 채널의 성공·실패를 현재 설정 결과로 보지 않음");
        Assert(!DeliveryView.For(record, ChNews, settingSince: s_Now.AddMinutes(-30)).HasUnresolvedFailure
               && DeliveryView.For(record, ChNews, settingSince: s_Now.AddHours(-3)).HasUnresolvedFailure,
            "해제 후 같은 채널로 다시 등록하면 등록 전 실패는 현재 설정의 문제로 보지 않음");
    }

    private static void PermissionTests()
    {
        ChannelPermissions Perms(params ChannelPermission[] permissions) => new(permissions.Aggregate(0UL, (all, p) => all | (ulong)p));
        var all = Perms(ChannelPermission.ViewChannel, ChannelPermission.SendMessages, ChannelPermission.EmbedLinks, ChannelPermission.AttachFiles,
            ChannelPermission.SendMessagesInThreads);
        Assert(AlertChannelRequirements.Check(all, true, false) == ChannelCheck.Ok, "필요 권한이 모두 있으면 설정 정상");
        Assert(AlertChannelRequirements.Missing(Perms(ChannelPermission.ViewChannel, ChannelPermission.EmbedLinks, ChannelPermission.AttachFiles), true, false)
               .SequenceEqual(["메시지 보내기"]), "메시지 보내기 권한 부족");
        Assert(AlertChannelRequirements.Missing(Perms(ChannelPermission.ViewChannel, ChannelPermission.SendMessages, ChannelPermission.AttachFiles), true, false)
               .SequenceEqual(["링크 첨부"]), "링크 첨부 권한 부족");
        var noAttach = Perms(ChannelPermission.ViewChannel, ChannelPermission.SendMessages, ChannelPermission.EmbedLinks);
        Assert(AlertChannelRequirements.Missing(noAttach, true, false).SequenceEqual(["파일 첨부"]) && AlertChannelRequirements.Missing(noAttach, false, false).Count == 0,
            "파일 첨부는 기본 이미지를 첨부하는 공식 소식에서만 필요");
        Assert(AlertChannelRequirements.Missing(noAttach, false, true).SequenceEqual(["스레드에서 메시지 보내기"]),
            "스레드는 스레드에서 메시지 보내기 권한을 확인");
    }

    private static void ErrorKindTests()
    {
        Assert(StatusErrorKinds.ClassifyCollection(new TaskCanceledException()) == StatusErrorKinds.Timeout
               && StatusErrorKinds.ClassifyCollection(new MobiEventFetchException("HTTP 403 (https://mabinogimobile.nexon.com/News/Notice?secret=1)")) == StatusErrorKinds.HttpStatus
               && StatusErrorKinds.ClassifyCollection(new OfficialNewsFetchException("1페이지에서 목록 구조를 찾지 못함")) == StatusErrorKinds.Parse
               && StatusErrorKinds.ClassifyCollection(new HttpRequestException("x")) == StatusErrorKinds.Network,
            "수집 예외를 분류 코드로 바꿈");
        Assert(StatusErrorKinds.FromNewsDeliveryCode("discord_403_50013") == StatusErrorKinds.Permission
               && StatusErrorKinds.FromNewsDeliveryCode("discord_403_50001") == StatusErrorKinds.ChannelMissing
               && StatusErrorKinds.FromNewsDeliveryCode("channel_not_found") == StatusErrorKinds.ChannelMissing
               && StatusErrorKinds.FromNewsDeliveryCode("retry_exhausted:timeout") == StatusErrorKinds.Timeout
               && StatusErrorKinds.FromNewsDeliveryCode("discord_500") == StatusErrorKinds.DiscordError,
            "공식 소식 발송 오류 코드를 전송 분류로 바꿈");
        Assert(StatusErrorKinds.ClassifyDelivery(new AlertDeliveryException(StatusErrorKinds.ChannelMissing, "x")) == StatusErrorKinds.ChannelMissing
               && StatusErrorKinds.Describe(StatusErrorKinds.Permission) == "메시지 전송 권한 부족"
               && StatusErrorKinds.Describe(StatusErrorKinds.Auth) == "API 인증 설정 확인 필요"
               && StatusErrorKinds.Describe("이상한값") == "알 수 없는 오류",
            "전송 예외 분류와 운영자용 짧은 문구");
    }

    private static CollectionLine Line(string section, string name, CollectionState state, JobStatusRecord? record = null, JobDisabled? disabled = null, bool used = false) =>
        new(section, name, new JobStatusView(state, record, disabled), used);

    private static void ReportTests()
    {
        var ok = Success(s_Now.AddMinutes(-1));
        var allOk = MollyStatusReport.Build(new StatusReportInput(s_Now, s_Now.AddHours(-2),
            [Line("공홈 수집", "공지", CollectionState.Ok, ok), Line("공홈 수집", "업데이트", CollectionState.Ok, ok), Line("시세 수집", "해연", CollectionState.Ok, ok)],
            [new AlertEntry("공지", null, null, ChannelCheck.NotRegistered, null), new AlertEntry("해연 시세", null, null, ChannelCheck.NotRegistered, null)]));
        var text = string.Join("\n", allOk.Pages);
        Assert(allOk.Summary == StatusSummary.Ok && allOk.Title.EndsWith("정상") && text.Contains("확인 시각: 2026-10-29 20:15 KST")
               && text.Contains("이번 실행 시작: 2026-10-29 18:15 KST") && text.Contains("🟢 공지·업데이트 · 마지막 성공 20:14")
               && text.Contains("공지·해연 시세 → 미등록") && !text.Contains("[확인할 내용]"),
            "모두 정상이고 미등록뿐이면 정상(미등록은 오류가 아님), 같은 상태는 한 줄로 묶음");

        var yesterday = s_Now.AddDays(-1);
        var input = new StatusReportInput(s_Now, s_Now.AddHours(-2),
        [
            Line("공홈 수집", "공지", CollectionState.Ok, ok),
            Line("공홈 수집", "에린 노트", CollectionState.RecentFailure, Failure(s_Now.AddMinutes(-1), s_Now.AddMinutes(-6), 2)),
            Line("공홈 수집", "이벤트 마감용 목록", CollectionState.Preparing, Success(yesterday)),
            new CollectionLine("공홈 수집", "업데이트", null, true),
            Line("시세 수집", "해연", CollectionState.Disabled, null, new JobDisabled(true, "모비라이프 API 키가 설정되지 않음"), used: true),
            Line("시세 수집", "상자", CollectionState.Disabled, null, new JobDisabled(true, "모비라이프 API 키가 설정되지 않음")),
        ],
        [
            new AlertEntry("공지", ChNews, null, ChannelCheck.Ok, DeliveryView.None),
            new AlertEntry("업데이트", ChNews, null, ChannelCheck.Ok, DeliveryView.None),
            new AlertEntry("이벤트", ChOther, null, new ChannelCheck(ChannelCheckKind.MissingPermissions, ["링크 첨부"]), new DeliveryView(yesterday, null, null)),
            new AlertEntry("에린 노트", null, null, ChannelCheck.NotRegistered, null),
            new AlertEntry("이벤트 마감", ChOther, "24시간 전", ChannelCheck.Ok, DeliveryView.None),
            new AlertEntry("해연 시세", ChMarket, "장비 10% / 재료 20%", ChannelCheck.Ok,
                DeliveryView.For(s_Now.AddDays(-2), s_Now.AddMinutes(-15), StatusErrorKinds.Permission)),
            AlertEntry.Failed("상자 시세"),
            new AlertEntry("패키지 시세", null, null, ChannelCheck.ChannelUnavailable, null),
        ], RecordFailureCount: 1, LastRecordFailureAt: s_Now.AddMinutes(-3));
        var report = MollyStatusReport.Build(input);
        text = string.Join("\n", report.Pages);
        Assert(report.Summary == StatusSummary.NeedsAttention && report.Title.EndsWith("일부 확인 필요")
               && text.Contains("원인: 에린 노트 최근 확인 실패, 업데이트 상태 조회 실패, 해연 필수 설정 누락, 이벤트 채널 권한 부족, 상자 시세 설정 조회 실패 외"),
            "문제가 있으면 일부 확인 필요와 짧은 원인 목록");
        Assert(text.Contains("🟡 에린 노트 · 최근 확인 실패") && text.Contains("└ 마지막 시도 20:14 / 마지막 성공 20:09 · 연속 실패 2회")
               && text.Contains("에린 노트: 응답 시간 초과. 이전 정상 데이터를 유지하고 다음 확인에서 다시 시도해요."),
            "문제 항목은 마지막 시도·성공과 원인을 덧붙임");
        Assert(text.Contains("⏳ 이벤트 마감용 목록 · 준비 중") && text.Contains("└ 마지막 성공 10-28 20:15"),
            "준비 중이면 과거 성공 시각을 날짜와 함께 표시");
        Assert(text.Contains("⚠️ 업데이트 · 상태를 불러오지 못함") && text.Contains("🟢 공지 · 마지막 성공") && text.Contains("상자 시세 → 설정을 불러오지 못함"),
            "한 항목 조회가 실패해도 나머지 항목은 표시");
        Assert(text.Contains("⚪ 해연·상자 · 사용 안 함 — 모비라이프 API 키가 설정되지 않음 (필수 설정 누락)")
               && report.Reasons.Contains("해연 필수 설정 누락") && !report.Reasons.Contains("상자 필수 설정 누락"),
            "필수 설정 누락은 이 서버가 쓰는 알림만 원인으로 올림");
        Assert(text.Contains($"공지·업데이트 → <#{ChNews}> · 설정 정상") && text.Contains($"이벤트 → <#{ChOther}> · 링크 첨부 권한 필요")
               && text.Contains($"이벤트 마감 → <#{ChOther}> · 24시간 전 · 설정 정상") && text.Contains($"해연 시세 → <#{ChMarket}> · 장비 10% / 재료 20% · 설정 정상")
               && text.Contains("에린 노트·패키지 시세 → 미등록"),
            "같은 채널·같은 결과는 묶고 마감 시간·등락률은 생략하지 않음");
        Assert(text.Contains("공지·업데이트·이벤트 마감 · 전송 기록 없음") && text.Contains("이벤트 · 마지막 전송 10-28 20:15")
               && text.Contains("해연 시세 · 🔴 전송 실패 20:00 (메시지 전송 권한 부족) · 마지막 성공 10-27 20:15")
               && report.Reasons.Contains("해연 시세 전송 실패"),
            "전송 기록 없음은 오류가 아니고, 공통 수집과 별개로 이 서버의 전송 실패를 표시");
        Assert(report.Reasons.Contains("상태 기록 실패") && text.Contains("상태 기록에 1번 실패"), "상태 기록 실패를 드러냄");

        var preparing = MollyStatusReport.Build(new StatusReportInput(s_Now, s_Now.AddMinutes(-1),
            [Line("공홈 수집", "공지", CollectionState.Preparing)], [new AlertEntry("공지", null, null, ChannelCheck.NotRegistered, null)]));
        Assert(preparing.Summary == StatusSummary.Preparing && preparing.Title.EndsWith("준비 중"), "문제 없이 첫 확인을 기다리면 준비 중");

        var many = Enumerable.Range(0, 120).Select(i => Line("공홈 수집", $"항목{i}", CollectionState.Unavailable, Failure(s_Now.AddMinutes(-i), null))).ToList();
        var long_ = MollyStatusReport.Build(new StatusReportInput(s_Now, s_Now.AddHours(-2), many, []));
        Assert(long_.Pages.Count > 1 && long_.Pages.All(p => p.Length <= MollyStatusReport.MaxPageLength) && long_.Title.Length <= 256,
            "길면 Embed 설명 제한 안에서 여러 메시지로 나눔");
        Assert(!text.Contains("http") && !long_.Pages.Any(p => p.Contains("Exception")), "응답에는 주소·예외 원문을 넣지 않음");
    }

    private static void CommandTests()
    {
        var method = typeof(DiscordBot_Molly.Commands.MollyStatusCommand).GetMethod(nameof(DiscordBot_Molly.Commands.MollyStatusCommand.StatusAsync))!;
        var permission = method.GetCustomAttributes(typeof(Discord.Interactions.DefaultMemberPermissionsAttribute), false)
            .Cast<Discord.Interactions.DefaultMemberPermissionsAttribute>().SingleOrDefault();
        Assert(permission?.Permissions == GuildPermission.ManageGuild && method.GetParameters().Length == 0,
            "/몰리상태는 옵션이 없고 명령 등록에 서버 관리 권한을 요구");
    }

    private static async Task StoreTestsAsync(string databasePath)
    {
        // 기존 표·데이터가 있는 DB에 새 표만 추가한다.
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE event_expire_alert_settings (guild_id INTEGER PRIMARY KEY, enabled INTEGER NOT NULL, channel_id INTEGER NOT NULL, hours_before INTEGER NOT NULL, last_alert_at_kst TEXT NULL); INSERT INTO event_expire_alert_settings VALUES (1, 1, 2, 24, NULL);";
            await command.ExecuteNonQueryAsync();
        }
        var store = new MollyStatusStore(databasePath);
        await store.InitializeAsync();
        await store.InitializeAsync();
        var settings = new EventExpireAlertSettingStore(databasePath: databasePath);
        Assert((await settings.LoadAsync(1))?.HoursBefore == 24, "상태 표를 만들어도 기존 설정은 그대로");

        var t0 = s_Now.AddMinutes(-30);
        await store.RecordJobAsync("job", t0, true, null);
        await store.RecordJobAsync("job", t0.AddMinutes(10), false, StatusErrorKinds.Timeout);
        await store.RecordJobAsync("job", t0.AddMinutes(20), false, StatusErrorKinds.Parse);
        var record = await store.GetJobAsync("job");
        Assert(record is { LastSucceeded: false, ConsecutiveFailures: 2, LastErrorKind: StatusErrorKinds.Parse } && record.LastSuccessAt == t0,
            "실패가 이어지면 연속 실패 횟수가 늘고 마지막 성공은 유지");
        await store.RecordJobAsync("job", t0.AddMinutes(5), true, null);
        Assert((await store.GetJobAsync("job"))!.LastAttemptAt == t0.AddMinutes(20), "늦게 끝난 이전 시도가 최신 결과를 덮어쓰지 않음");
        await store.RecordJobAsync("job", t0.AddMinutes(30), true, null);
        Assert(await store.GetJobAsync("job") is { LastSucceeded: true, ConsecutiveFailures: 0, LastErrorKind: null }, "실패 후 성공하면 실패 표시와 연속 실패 해제");
        Assert(await store.GetJobAsync("없음") is null, "기록이 없는 작업은 null");

        await store.RecordDeliveryAsync(Guild, "k", ChMarket, t0, true, null);
        await store.RecordDeliveryAsync(Guild, "k", ChMarket, t0.AddMinutes(10), false, StatusErrorKinds.Permission);
        await store.RecordDeliveryAsync(Guild, "k", ChMarket, t0.AddMinutes(5), true, null);
        var delivery = await store.GetDeliveryAsync(Guild, "k");
        Assert(delivery is { LastSuccessAt: { } s, LastFailureAt: { } f, LastErrorKind: StatusErrorKinds.Permission } && s == t0.AddMinutes(5) && f == t0.AddMinutes(10)
               && DeliveryView.For(delivery, ChMarket).HasUnresolvedFailure,
            "전송 성공·실패는 각자 최신 값만 남고 실패가 더 늦으면 미해결");
        Assert(await store.GetDeliveryAsync(OtherGuild, "k") is null, "서버별로 따로 저장");
    }

    private static async Task TrackerTestsAsync(string directory)
    {
        var databasePath = Path.Combine(directory, "tracker.sqlite");
        var store = new MollyStatusStore(databasePath);
        await store.InitializeAsync();
        var now = s_Now;
        var tracker = new MollyStatusTracker(store, now.AddHours(-1), () => now, _ => { });
        var run = tracker.Begin("job");
        Assert(tracker.RunningSince("job") == now, "시작하면 진행 중으로 표시");
        now = now.AddMinutes(1);
        await tracker.CompleteAsync(run, false, StatusErrorKinds.Timeout);
        Assert(tracker.RunningSince("job") is null && (await store.GetJobAsync("job"))?.LastAttemptAt == s_Now, "끝나면 진행 표시를 지우고 시작 시각으로 결과 저장");

        var stuck = tracker.Begin("job");
        var restarted = new MollyStatusTracker(new MollyStatusStore(databasePath), now, () => now, _ => { });
        Assert(restarted.RunningSince("job") is null && (await restarted.Store.GetJobAsync("job")) is { LastSucceeded: false, LastErrorKind: StatusErrorKinds.Timeout },
            "재시작하면 진행 중 상태는 초기화되고 마지막 결과는 유지");
        tracker.Abandon(stuck);

        var older = tracker.Begin("overlap");
        var newer = tracker.Begin("overlap");
        tracker.Abandon(older);
        Assert(tracker.RunningSince("overlap") == newer.StartedAt, "먼저 시작한 시도가 끝나도 새 시도의 진행 표시는 유지");

        // 디렉터리 경로를 DB로 열면 기록이 실패한다. 예외를 던지지 않고 실패 횟수만 센다.
        var logs = new List<string>();
        var broken = new MollyStatusTracker(new MollyStatusStore(directory), now, () => now, logs.Add);
        await broken.CompleteAsync(broken.Begin("job"), true, null);
        await broken.RecordDeliveryAsync(Guild, "k", ChMarket, true);
        Assert(broken.RecordFailures.Count == 2 && broken.RecordFailures.LastAt == now && logs.Count == 2,
            "상태 기록 실패는 작업을 실패시키지 않고 로그와 실패 횟수로 남김");
    }

    private static async Task MonitorRecordingTestsAsync(string databasePath)
    {
        var statusStore = new MollyStatusStore(databasePath);
        await statusStore.InitializeAsync();
        var now = s_Now;
        var tracker = new MollyStatusTracker(statusStore, now.AddHours(-1), () => now, _ => { });
        var store = new KeywordMarketStore(KeywordMarketRules.Box.Id, databasePath);
        await store.InitializeAsync();
        await store.SetChannelAsync(Guild, ChMarket, now);
        await store.SetChannelAsync(OtherGuild, ChOther, now);
        var source = new CountingPriceSource();
        var sender = new FailingKeywordSender { FailChannel = ChOther };
        var monitor = new KeywordMarketMonitor(KeywordMarketRules.Box, source, store, sender, () => now, _ => { }, tracker);
        var jobId = StatusJobs.Market(KeywordMarketRules.Box.Id);

        source.Fail = MarketPriceSearchResult.Fail("모비라이프 데이터를 지금 가져올 수 없습니다.", StatusErrorKinds.Auth);
        Assert(!(await monitor.CollectAsync(default)).Success && await statusStore.GetJobAsync(jobId) is { LastSucceeded: false, LastErrorKind: StatusErrorKinds.Auth },
            "시세 조회 실패는 분류와 함께 기록");

        source.Fail = null;
        source.Prices = [new MarketPrice(1, "테스트 상자", "아이템", 1000, 100, false, now)];
        now = now.AddMinutes(10);
        Assert((await monitor.CollectAsync(default)).Success && await statusStore.GetJobAsync(jobId) is { LastSucceeded: true, ConsecutiveFailures: 0 },
            "첫 수집(알림 없음) 성공도 정상으로 기록");
        Assert(await statusStore.GetDeliveryAsync(Guild, StatusAlerts.Market("box")) is null, "보낼 알림이 없으면 전송 기록 없음");

        source.Prices = [new MarketPrice(1, "테스트 상자", "아이템", 1000, 100, false, now), new MarketPrice(2, "새 상자", "아이템", 500, 100, false, now)];
        now = now.AddHours(1);
        Assert((await monitor.CollectAsync(default)).Success, "새 아이템 알림 회차 수집 성공");
        var ok = await statusStore.GetDeliveryAsync(Guild, StatusAlerts.Market("box"));
        var failed = await statusStore.GetDeliveryAsync(OtherGuild, StatusAlerts.Market("box"));
        Assert(await statusStore.GetJobAsync(jobId) is { LastSucceeded: true } && ok is { LastSuccessAt: not null, LastFailureAt: null }
               && failed is { LastSuccessAt: null, LastFailureChannelId: ChOther, LastErrorKind: StatusErrorKinds.Permission },
            "공통 수집은 정상이어도 특정 서버 전송 실패는 그 서버에만 기록");
    }

    private static async Task NewsDeliveryTestsAsync(string databasePath)
    {
        var store = new OfficialNewsStore(databasePath);
        await store.InitializeAsync();
        var now = s_Now.AddHours(-1);
        var sender = new FakeNewsSender();
        var worker = new OfficialNewsDeliveryWorker(store, sender, () => now, _ => { });
        await store.SetSubscriptionAsync(Guild, "notice", ChNews, now);
        Assert(await store.GetDeliveryHealthAsync(Guild, "notice", ChNews) == (null, null, null), "보낸 적이 없으면 공식 소식 전송 기록 없음");

        async Task Receive(long postId)
        {
            await store.ReceiveAsync(new NewsEvent(NewsBoards.EventIdFor("notice", postId), "notice", postId.ToString(), "제목", "https://example.invalid", null, null, now), now);
            await worker.ProcessDueAsync(default);
        }

        sender.Fail = new NewsDeliveryException("discord_403_50013", true, "Missing Permissions");
        await Receive(1);
        var health = await store.GetDeliveryHealthAsync(Guild, "notice", ChNews);
        Assert(health.LastSentAt is null && health.LastFailureAt == now && StatusErrorKinds.FromNewsDeliveryCode(health.LastErrorCode) == StatusErrorKinds.Permission,
            "공식 소식 영구 실패를 발송 작업 표에서 읽음");

        sender.Fail = new NewsDeliveryException("discord_503", false, "unavailable");
        now = now.AddMinutes(1);
        await Receive(2);
        health = await store.GetDeliveryHealthAsync(Guild, "notice", ChNews);
        Assert(health.LastFailureAt == now && health.LastErrorCode == "discord_503", "재시도 대기 중인 실패도 미해결 실패로 봄");

        sender.Fail = null;
        now = now.AddMinutes(5);
        await worker.ProcessDueAsync(default);
        health = await store.GetDeliveryHealthAsync(Guild, "notice", ChNews);
        Assert(!DeliveryView.For(health.LastSentAt, health.LastFailureAt, health.LastErrorCode).HasUnresolvedFailure,
            "같은 작업의 재시도 성공으로 복구");

        await store.SetSubscriptionAsync(Guild, "notice", ChOther, now);
        Assert(await store.GetDeliveryHealthAsync(Guild, "notice", ChOther) == (null, null, null), "채널을 바꾸면 이전 채널의 기록을 현재 설정 결과로 보지 않음");
    }

    private static async Task GatherTestsAsync(string databasePath)
    {
        var now = s_Now;
        var statusStore = new MollyStatusStore(databasePath);
        await statusStore.InitializeAsync();
        var tracker = new MollyStatusTracker(statusStore, now.AddHours(-1), () => now, _ => { });
        var newsStore = new OfficialNewsStore(databasePath);
        await newsStore.InitializeAsync();
        var pages = new CountingPageSource();
        var newsSender = new FakeNewsSender();
        var worker = new OfficialNewsDeliveryWorker(newsStore, newsSender, () => now, _ => { });
        var news = new OfficialNewsService(newsStore, worker, new OfficialNewsCollector(newsStore, pages, () => { }, () => now, _ => { }, tracker), newsSender, true);
        var events = new MobiEventService(pages, null, () => now.UtcDateTime, log: _ => { }) { Status = tracker };
        var eventSettings = new EventExpireAlertSettingStore(databasePath: databasePath);
        await eventSettings.InitializeAsync();
        var http = new CountingHandler();
        using var mobiLife = new MobiLifeApiClient(new MobiLifeOptions { ApiKey = "test-key" }, http);
        var source = new CountingPriceSource();
        var recipeCalls = 0;
        var haeyeonStore = new HaeyeonMarketStore(databasePath);
        await haeyeonStore.InitializeAsync();
        var haeyeon = new HaeyeonMarketMonitor(_ => { recipeCalls++; return Task.FromResult<IReadOnlyList<CraftingRecipe>>([]); }, source, haeyeonStore,
            new NullHaeyeonSender(), () => now, _ => { }, tracker);
        var boxStore = new KeywordMarketStore("box", databasePath);
        await boxStore.InitializeAsync();
        var packageStore = new KeywordMarketStore("package", databasePath);
        await packageStore.InitializeAsync();
        var box = new KeywordMarketMonitor(KeywordMarketRules.Box, source, boxStore, new FailingKeywordSender(), () => now, _ => { }, tracker);
        var package = new KeywordMarketMonitor(KeywordMarketRules.Package, source, packageStore, new FailingKeywordSender(), () => now, _ => { }, tracker);
        var service = new MollyStatusService(tracker, news, events, eventSettings, mobiLife, haeyeon, box, package, () => now, _ => { });

        // 공지: 기준 있음·최근 성공, 에린 노트: 기준 있음·최근 실패, 이벤트 마감: 등록·채널 삭제, 해연: 등록·권한 부족, 상자: 등록 후 해제.
        await newsStore.BaselineBoardAsync("notice", [1], now.AddMinutes(-3));
        await statusStore.RecordJobAsync(StatusJobs.News(NewsBoards.Devnote), now.AddMinutes(-1), false, StatusErrorKinds.Timeout);
        await newsStore.BaselineBoardAsync("devnote", [1], now.AddMinutes(-11));
        await newsStore.SetSubscriptionAsync(Guild, "notice", ChNews, now);
        await eventSettings.SaveAsync(Guild, new EventExpireAlertSetting { Enabled = true, ChannelId = ChOther, HoursBefore = 12 });
        await haeyeonStore.SetChannelAsync(Guild, ChMarket, now, 15, null);
        await boxStore.SetChannelAsync(Guild, ChMarket, now);
        await statusStore.RecordDeliveryAsync(Guild, StatusAlerts.Market("box"), ChMarket, now.AddMinutes(-5), false, StatusErrorKinds.Permission);
        await boxStore.RemoveChannelAsync(Guild);
        await packageStore.SetChannelAsync(Guild, ChNews, now.AddMinutes(-10));
        await statusStore.RecordDeliveryAsync(Guild, StatusAlerts.Market("package"), ChNews, now.AddMinutes(-20), false, StatusErrorKinds.Permission);
        await statusStore.RecordDeliveryAsync(Guild, StatusAlerts.Market("package"), ChNews, now.AddMinutes(-5), false, StatusErrorKinds.ChannelMissing);

        var checks = new List<(ulong ChannelId, bool NeedsAttach)>();
        Task<ChannelCheck> Check(ulong channelId, bool needsAttach)
        {
            checks.Add((channelId, needsAttach));
            return Task.FromResult(channelId switch
            {
                ChNews => ChannelCheck.Ok,
                ChOther => ChannelCheck.ChannelUnavailable,
                _ => new ChannelCheck(ChannelCheckKind.MissingPermissions, ["메시지 보내기"]),
            });
        }

        var input = await service.GatherAsync(Guild, Check);
        var report = MollyStatusReport.Build(input);
        var text = string.Join("\n", report.Pages);
        Assert(pages.Requests == 0 && source.Searches == 0 && http.Requests == 0 && recipeCalls == 0,
            "상태 조회는 공식 홈페이지·시세 API·제작 시트를 호출하지 않음");
        Assert(checks.Contains((ChNews, true)) && checks.Contains((ChNews, false)) && checks.Contains((ChOther, false)) && checks.Contains((ChMarket, false)) && checks.Count == 4,
            "등록된 알림만 실제 전송 방식에 맞는 권한으로 채널 확인");
        Assert(State(input, "공지") == CollectionState.Ok && State(input, "에린 노트") == CollectionState.RecentFailure
               && State(input, "업데이트") == CollectionState.Delayed && State(input, "해연") == CollectionState.Delayed,
            "기존 게시판 상태와 상태 표를 합쳐 작업별로 판정(이번 실행에 확인 기록이 없으면 지연)");
        Assert(text.Contains($"공지 → <#{ChNews}> · 설정 정상") && text.Contains("이벤트 마감 → 등록 채널을 확인할 수 없음 · 12시간 전")
               && text.Contains($"해연 시세 → <#{ChMarket}> · 장비 15% / 재료 20% · 메시지 보내기 권한 필요") && text.Contains("상자 시세 → 미등록")
               && !report.Reasons.Any(r => r.StartsWith("상자 시세"))
               && text.Contains("패키지 시세 · 🔴 전송 실패 20:10 (채널 접근 불가)") && report.Reasons.Contains("패키지 시세 전송 실패"),
            "서버 설정·권한을 표시하고, 해제한 알림이나 현재 설정 등록 전의 실패는 현재 문제로 표시하지 않음");

        await eventSettings.SaveAsync(Guild, new EventExpireAlertSetting());
        input = await service.GatherAsync(Guild, Check);
        Assert(input.Collections.Single(x => x.Name == "이벤트 마감용 목록").View is { State: CollectionState.Disabled, Disabled.MissingSetting: false },
            "마감 알림을 켠 서버가 없으면 이벤트 목록은 사용 안 함(의도적)");

        using var disabledClient = new MobiLifeApiClient(new MobiLifeOptions { Enabled = false, ApiKey = "test-key" });
        using var noKeyClient = new MobiLifeApiClient(new MobiLifeOptions());
        var disabled = await new MollyStatusService(tracker, new OfficialNewsService(newsStore, worker, news.Collector, newsSender, false), events, eventSettings,
            disabledClient, haeyeon, box, package, () => now, _ => { }).GatherAsync(Guild, Check);
        var noKey = await new MollyStatusService(tracker, news, events, eventSettings, noKeyClient, haeyeon, box, package, () => now, _ => { }).GatherAsync(Guild, Check);
        Assert(disabled.Collections.Single(x => x.Name == "공지").View is { State: CollectionState.Disabled, Disabled.MissingSetting: false }
               && disabled.Collections.Single(x => x.Name == "해연").View is { State: CollectionState.Disabled, Disabled.MissingSetting: false }
               && noKey.Collections.Single(x => x.Name == "해연").View is { State: CollectionState.Disabled, Disabled.MissingSetting: true },
            "의도적인 비활성화와 필수 설정 누락을 구분");

        var failingInput = await service.GatherAsync(Guild, (_, _) => throw new InvalidOperationException("boom"));
        Assert(failingInput.Alerts.Single(x => x.Name == "공지").Check.Kind == ChannelCheckKind.CheckFailed && failingInput.Collections.Count == 8,
            "채널 확인이 실패해도 나머지 상태는 모음");
    }

    private static CollectionState? State(StatusReportInput input, string name) => input.Collections.Single(x => x.Name == name).View?.State;

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class CountingPriceSource : IMarketPriceSource
    {
        public int Searches;
        public MarketPriceSearchResult? Fail { get; set; }
        public IReadOnlyList<MarketPrice> Prices { get; set; } = [];
        public string Attribution => "테스트 제공";

        public Task<MarketPriceSearchResult> SearchAsync(string keyword, string? category, CancellationToken ct)
        {
            Searches++;
            return Task.FromResult(Fail ?? new MarketPriceSearchResult(Prices));
        }
    }

    private sealed class FailingKeywordSender : IKeywordAlertSender
    {
        public ulong? FailChannel { get; init; }

        public Task SendAsync(KeywordMonitorChannel channel, IReadOnlyList<Embed> embeds, CancellationToken ct) =>
            channel.ChannelId == FailChannel ? throw new AlertDeliveryException(StatusErrorKinds.Permission, "권한 없음") : Task.CompletedTask;
    }

    private sealed class NullHaeyeonSender : IHaeyeonAlertSender
    {
        public Task SendAsync(HaeyeonMonitorChannel channel, IReadOnlyList<Embed> embeds, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingPageSource : IMobiEventPageSource
    {
        public int Requests;
        public string Name => "Counting";

        public Task<string> FetchPageAsync(string url, CancellationToken ct)
        {
            Requests++;
            throw new MobiEventFetchException("HTTP 500");
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }

    private sealed class FakeNewsSender : IOfficialNewsSender
    {
        private ulong m_NextId = 1000;
        public bool IsReady => true;
        public Exception? Fail { get; set; }

        public Task<ulong> SendAsync(ulong channelId, NewsMessage message, CancellationToken ct) =>
            Fail is { } error ? throw error : Task.FromResult(Interlocked.Increment(ref m_NextId));
    }
}
