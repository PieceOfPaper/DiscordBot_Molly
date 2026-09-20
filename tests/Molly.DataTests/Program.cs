using System.Net;
using Molly.Runes;
using Molly.Quiz;
using Molly.Nunchi;
using Molly.Messages;
using Molly.LiarGame;
using Molly.Lottery;
using Molly.Battle;

const string header = "시즌,등급,분류,클래스,이름,효과\r\n";
const string valid = header + "2,전설,무기,전사,테스트,효과";
if (args.Length == 2 && args[0] == "--csv")
{
    var table = RuneCsvReader.Parse(await File.ReadAllTextAsync(args[1]), DateTimeOffset.UtcNow);
    Console.WriteLine($"실제 CSV: {table.Items.Count}개, 경고 {table.Warnings.Count}개");
    foreach (var warning in table.Warnings) Console.WriteLine(warning);
    return;
}
if (args.SequenceEqual(new[] { "--live" }))
{
    using var client = new HttpClient();
    var table = RuneCsvReader.Parse(await new GoogleSheetsRuneSource(client).FetchCsvAsync(default), DateTimeOffset.UtcNow);
    Console.WriteLine($"실제 Google Sheets HTTP 로딩: {table.Items.Count}개, 경고 {table.Warnings.Count}개");
    return;
}
if (args.SequenceEqual(new[] { "--battle-live" }))
{
    using var client = new HttpClient();
    var source = new GoogleSheetsBattleSource(client, GoogleSheetsRuneSource.DefaultSpreadsheetId);
    var snapshot = BattleCatalog.Parse(await source.FetchAsync(default), DateTimeOffset.UtcNow);
    Console.WriteLine($"실제 배틀 시트: 클래스 {snapshot.Classes.Count}개, 스킬 {snapshot.Skills.Count}개");
    return;
}
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
Check((int)MobiServer.몰리 == 8, "몰리 서버 ID는 공식 랭킹 선택값 8");
Check(NunchiTargetParser.ParseMentions("<@12345678901234567> <@!23456789012345678> <@12345678901234567>").SequenceEqual(new ulong[] { 12345678901234567, 23456789012345678 }), "눈치게임 대상자 멘션을 중복 없이 읽기");
var nunchi = new NunchiRound([1, 2, 3, 4, 5]);
Check(nunchi.Submit(1, "1", DateTimeOffset.UnixEpoch) is null && nunchi.Submit(2, "2", DateTimeOffset.UnixEpoch.AddSeconds(1)) is null &&
      nunchi.Submit(3, "3", DateTimeOffset.UnixEpoch.AddSeconds(2)) is null && nunchi.Submit(4, "4", DateTimeOffset.UnixEpoch.AddSeconds(3)) is { CaughtUserIds: var missing } && missing.SequenceEqual(new ulong[] { 5 }), "눈치게임은 마지막 숫자를 외칠 차례의 미호출자를 처리");
var nunchiLast = new NunchiRound([1, 2, 3]);
Check(nunchiLast.Submit(1, "1", DateTimeOffset.UnixEpoch) is null && nunchiLast.Submit(2, "1", DateTimeOffset.UnixEpoch.AddMilliseconds(900)) is { CaughtUserIds: var rapid } && rapid.Order().SequenceEqual(new ulong[] { 1, 2 }), "눈치게임 마지막 숫자 1초 내 중복은 모두 처리");
var nunchiWrong = new NunchiRound([1, 2, 3]);
Check(nunchiWrong.Submit(1, "2", DateTimeOffset.UnixEpoch) is { CaughtUserIds: var wrong } && wrong.SequenceEqual(new ulong[] { 1 }), "눈치게임 순서 밖 숫자는 호출자를 처리");
var cancelNunchi = new NunchiGameService();
Check(cancelNunchi.TryStart(77, 88, [1, 2], TimeSpan.FromMinutes(1), _ => Task.CompletedTask) &&
      !cancelNunchi.TryStart(77, 99, [1, 2], TimeSpan.FromMinutes(1), _ => Task.CompletedTask), "눈치게임은 길드당 하나만 시작");
cancelNunchi.CancelChannel(88);
Check(cancelNunchi.TryStart(77, 99, [1, 2], TimeSpan.FromMinutes(1), _ => Task.CompletedTask), "눈치게임 채널 삭제 시 진행 상태 정리");
var lotteryRound = new LotteryRound([1, 2, 3], 2, new Random(1));
var lotteryFirst = lotteryRound.Draw(1);
Check((lotteryFirst is LotteryDrawResult.Winner or LotteryDrawResult.NotWinner) && lotteryRound.Draw(1) == LotteryDrawResult.AlreadyDrawn &&
      lotteryRound.Draw(4) == LotteryDrawResult.NotParticipant && lotteryRound.RevealRemaining().Count == 2 && lotteryRound.AllRevealed,
    "당첨뽑기는 참가자별 한 번만 결과를 공개하고 미참가자를 거부");
var concurrentLottery = new LotteryRound([1, 2], 1, new Random(2));
var acceptedDraws = 0;
Parallel.For(0, 50, _ =>
{
    if (concurrentLottery.Draw(1) is LotteryDrawResult.Winner or LotteryDrawResult.NotWinner) Interlocked.Increment(ref acceptedDraws);
});
Check(acceptedDraws == 1, "당첨뽑기 동시 버튼 입력은 한 번만 결과를 공개");
var lotteryService = new LotteryService();
var lotteryAnnouncements = 0;
Check(lotteryService.TryStart(88, 100, [1, 2], 1, TimeSpan.FromMinutes(1), _ => { Interlocked.Increment(ref lotteryAnnouncements); return Task.CompletedTask; }) &&
      !lotteryService.TryStart(88, 101, [3], 1, TimeSpan.FromMinutes(1), _ => Task.CompletedTask), "당첨뽑기는 길드당 하나만 시작");
Check(await lotteryService.EndAsync(88, "테스트 종료") && lotteryAnnouncements == 1 && !await lotteryService.EndAsync(88, "중복 종료"), "당첨뽑기 종료는 남은 결과를 한 번만 공개");
var liarVotes = new LiarGameRound([1, 2, 3]);
Check(liarVotes.VoteAnswer(1, true) && liarVotes.VoteAnswer(2, false) && !liarVotes.VoteAnswer(1, false) &&
      liarVotes.MissingAnswers.SequenceEqual(new ulong[] { 3 }), "라이어게임 O/X 투표는 참가자 한 명당 한 번과 미투표자를 처리");
Check(liarVotes.Accuse(1, 3) && liarVotes.Accuse(2, 3) && liarVotes.Accuse(3, 1) && liarVotes.Decide(3).LiarFound,
    "라이어게임 최다 지목 라이어 판정");
var liarTie = new LiarGameRound([1, 2, 3, 4]);
Check(liarTie.Accuse(1, 2) && liarTie.Accuse(2, 1) && liarTie.Accuse(3, 2) && liarTie.Accuse(4, 1) && !liarTie.Decide(3).LiarFound,
    "라이어게임 지목 동률은 라이어 승리");
var liarNoVote = new LiarGameRound([1, 2, 3]);
Check(!liarNoVote.Decide(1).LiarFound, "라이어게임 지목자가 없으면 라이어 승리");
void Reject(string csv, string name)
{
    try { RuneCsvReader.Parse(csv, DateTimeOffset.UtcNow); }
    catch (Exception ex) when (ex is InvalidDataException or Microsoft.VisualBasic.FileIO.MalformedLineException)
    { Console.WriteLine("PASS " + name); return; }
    throw new Exception(name);
}
var parsed = RuneCsvReader.Parse("\uFEFF이름,효과,분류,등급,시즌,클래스\r\n테스트,\"첫 줄, \"\"인용\"\"\r\n둘째 줄\",무기,전설,2,전사", DateTimeOffset.UtcNow);
Check(parsed.Items[0].Effect == "첫 줄, \"인용\"\r\n둘째 줄" && parsed.Items[0].Class == "전사", "열 재배치·BOM·쉼표·따옴표·여러 줄 효과·클래스");
Reject("<html>로그인</html>", "HTML 거부");
Reject(header, "빈 테이블 거부");
Reject(valid + "\n2,전설,무기,전사,테스트,다른 효과", "중복 키 거부");
Reject(header + "시즌,전설,무기,전사,이름,효과", "잘못된 시즌 거부");
Reject(header + "2,전설,오타,전사,이름,효과", "분류 오타 거부");
Reject(header + "2,희귀,무기,전사,이름,효과", "허용하지 않은 등급 거부");
Reject("시즌,등급,분류,이름,효과\n2,전설,무기,이름,효과", "클래스 헤더 누락 거부");
Reject(valid + ",여분", "열 수 불일치 거부");
Reject(header + "2,전설,무기,이름,\"닫히지 않음", "깨진 CSV 거부");
var partial = RuneCsvReader.Parse(valid + "\n2,전설,방어구,전사,두 영웅,", DateTimeOffset.UtcNow);
Check(partial.Items.Count == 1 && partial.Warnings.Count == 1, "미작성 효과 제외와 경고");
var seasons = RuneCsvReader.Parse(valid + "\n1,전설,무기,전사,테스트,이전 효과", DateTimeOffset.UtcNow);
Check(seasons.ByKey.Count == 2, "시즌별 동일 이름 분리");

var searchTable = RuneCsvReader.Parse(header + "2,전설,무기,전사,거대한 분노,효과\n2,전설,방어구,궁수,분노의 힘,효과\n1,신화,장신구,,분노,효과\n2,전설,무기,전사,평온,분노가 증가한다", DateTimeOffset.UtcNow);
Check(RuneSearch.Find(searchTable, " 분노 ").Count == 3, "이름 부분 일치 전체 검색 및 앞뒤 공백 제거");
Check(RuneSearch.Find(searchTable, "분노".Normalize(System.Text.NormalizationForm.FormD)).Count == 3, "한글 유니코드 정규화 검색");
Check(RuneSearch.Find(searchTable, "없는이름").Count == 0, "검색 결과 없음");
foreach (var empty in new string?[] { null, "", " ", "\t\r\n", "　" })
{
    try { RuneSearch.Find(searchTable, empty); throw new Exception("빈 검색어 허용"); }
    catch (ArgumentException) { }
}
Console.WriteLine("PASS 누락·빈 문자열·공백 검색어 거부");
var resultText = RuneSearch.Format(RuneSearch.Find(searchTable, "분노"));
Check(resultText.Contains("3개") && resultText.Contains("시즌 1 · 신화 · 장신구") && resultText.Contains("· 전사") && !resultText.Contains("평온"), "검색 결과 상세 정보와 클래스·이름만 검색");
var longText = new string('가', 1899) + "😀" + new string('나', 4000) + "\n마지막";
var messages = RuneSearch.SplitMessages(longText);
Check(messages.All(x => x.Length is > 0 and <= 1900 && !char.IsHighSurrogate(x[^1])) && string.Concat(messages) == longText, "긴 결과 누락 없이 분할 및 이모지 보존");
var quizPool = RuneCsvReader.Parse(header +
    "2,전설,무기,전사,첫 룬,효과 1\n2,전설,방어구,궁수,두 룬,효과 2\n2,신화,장신구,마법사,장신구 룬,효과 3\n2,신화,장신구,,클래스 없음,효과 4\n1,신화,장신구,마법사,이전 시즌,효과 5", DateTimeOffset.UtcNow);
var picked = QuizQuestions.Pick(QuizTopic.Season2RuneEffect, quizPool.Items, 2);
Check(picked.Count == 2 && picked.All(x => x.Answer is "첫 룬" or "두 룬" or "장신구 룬" or "클래스 없음"), "시즌2 유효 룬만 출제");
Check(QuizQuestions.NormalizeAnswer(" 첫  룬 ") == QuizQuestions.NormalizeAnswer("첫룬"), "퀴즈 정답 띄어쓰기 무시");
Check(QuizQuestions.NormalizeAnswer("ABC") == QuizQuestions.NormalizeAnswer("abc"), "퀴즈 정답 영문 대소문자 무시");
Check(QuizQuestions.Pick(QuizTopic.Season2RuneEffect, quizPool.Items, 99).Count == 4, "출제 가능 수만큼 자동 조정");
var accessoryEffect = QuizQuestions.Pick(QuizTopic.Season2AccessoryRuneEffect, quizPool.Items, 99);
Check(accessoryEffect.Count == 2 && accessoryEffect.All(x => x.Answer is "장신구 룬" or "클래스 없음"), "시즌2 장신구 룬 효과로 이름 출제");
var nonAccessoryEffect = QuizQuestions.Pick(QuizTopic.Season2NonAccessoryRuneEffect, quizPool.Items, 99);
Check(nonAccessoryEffect.Count == 2 && nonAccessoryEffect.All(x => x.Answer is "첫 룬" or "두 룬"), "시즌2 장신구를 제외한 룬 효과로 이름 출제");
var accessoryClass = QuizQuestions.Pick(QuizTopic.Season2AccessoryRuneNameClass, quizPool.Items, 99);
Check(accessoryClass.Count == 1 && accessoryClass[0] == new QuizQuestion("장신구 룬", "마법사"), "클래스가 있는 시즌2 장신구 룬 이름으로 클래스 출제");
var plusName = RuneCsvReader.Parse(header + "2,전설,무기,전사,폭염+,효과", DateTimeOffset.UtcNow);
Check(QuizQuestions.Pick(QuizTopic.Season2RuneEffect, plusName.Items, 1)[0].Answer == "폭염", "정답 표시에서 플러스 제거");
var trueFalsePool = RuneCsvReader.Parse(header +
    "2,전설,무기,전사,무기 하나,무기 효과 하나\n2,전설,무기,전사,무기 둘,무기 효과 둘\n2,전설,방어구,궁수,방어구 하나,방어구 효과 하나\n2,전설,방어구,궁수,방어구 둘,방어구 효과 둘\n2,전설,장신구,마법사,장신구 하나,장신구 효과 하나", DateTimeOffset.UtcNow);
var trueFalseQuestions = TrueFalseQuestions.Pick(TrueFalseTopic.Season2WeaponRune, trueFalsePool.Items, 99);
Check(trueFalseQuestions.Count == 2 && trueFalseQuestions.All(q => q.Prompt.Contains("무기") && !q.Prompt.Contains("방어구 효과")), "진혹거퀴즈는 같은 분류의 룬 효과만 출제");
var reactionRound = new TrueFalseRound(true, TimeSpan.FromSeconds(30));
reactionRound.Submit(1, true);
reactionRound.Submit(2, false);
reactionRound.Submit(3, true);
Check(reactionRound.Submit(3, false) == TrueFalseChoiceResult.AlreadySelected && reactionRound.CloseAndGetWinners().SequenceEqual(new ulong[] { 1, 3 }), "진혹거퀴즈는 첫 버튼 선택 하나만 채점");
var concurrentReactionRound = new TrueFalseRound(false, TimeSpan.FromSeconds(30));
Parallel.For(1, 51, userId => concurrentReactionRound.Submit((ulong)userId, false));
Check(concurrentReactionRound.CloseAndGetWinners().Count == 50, "진혹거퀴즈 동시 정답 버튼을 모두 채점");
var round = new QuizRound("두 룬", 100, TimeSpan.FromSeconds(30));
Check(round.Submit(1, 99, "두 룬") == false, "문제 이전 메시지 무시");
Check(round.Submit(1, 101, " 두룬 "), "정답 선착순 처리");
Check(round.Submit(2, 102, "두 룬") == false, "한 문제 한 명만 정답 처리");
round.Close();
var normalized = QuizQuestions.NormalizeAnswer("두 룬");
Check(normalized == "두룬", "퀴즈 정답 정규화");

var consonantCsv = new ConsonantCsvData(
    "이름,지역\n알 수 없는 NPC,\n글리니스,던바튼",
    "이름,분류,대륙\n던바튼,마을,울라\n티르 코네일,마을,울라",
    "이름,계열\n전사,전사\n마법사,마법");
var consonantTable = ConsonantCsvReader.Parse(consonantCsv, DateTimeOffset.UtcNow);
Check(consonantTable.Npcs.Count == 2 && consonantTable.Regions.Count == 2 && consonantTable.Classes.Count == 2, "NPC·지역·클래스 CSV 검증");
var npcQuestions = QuizQuestions.PickConsonant(ConsonantTopic.Npc, quizPool.Items, consonantTable, 10);
Check(npcQuestions.Count == 2 && npcQuestions.Any(q => q.Prompt.Contains("종류: **NPC**") && q.Prompt.Contains("지역: 불명")) && npcQuestions.All(q => q.Prompt.Contains("자음:")), "NPC 자음·지역 힌트와 불명 처리");
var regionQuestions = QuizQuestions.PickConsonant(ConsonantTopic.Region, quizPool.Items, consonantTable, 10);
Check(regionQuestions.All(q => q.Prompt.Contains("종류: **지역**") && q.Prompt.Contains("분류: 마을")), "지역 자음·분류 힌트");
var classQuestions = QuizQuestions.PickConsonant(ConsonantTopic.Class, quizPool.Items, consonantTable, 10);
Check(classQuestions.All(q => q.Prompt.Contains("종류: **클래스**") && !q.Prompt.Contains("💡 힌트")), "클래스 자음과 힌트 없음");
var runeQuestions = QuizQuestions.PickConsonant(ConsonantTopic.Season2WeaponRune, quizPool.Items, consonantTable, 10);
Check(runeQuestions.Count == 1 && runeQuestions[0].Prompt.Contains("무기룬"), "시즌2 무기룬 자음 힌트");
var mixedQuestions = QuizQuestions.PickConsonant(ConsonantTopic.NpcClassRegion, quizPool.Items, consonantTable, 99);
Check(mixedQuestions.Count == 6 && mixedQuestions.Select(q => q.Prompt).Any(x => x.Contains("NPC")) && mixedQuestions.Select(q => q.Prompt).Any(x => x.Contains("클래스")) && mixedQuestions.Select(q => q.Prompt).Any(x => x.Contains("지역")), "NPC·클래스·지역 혼합 출제");
RejectConsonants(new ConsonantCsvData("이름,지역\n중복,던바튼\n중복,반호르", consonantCsv.Regions, consonantCsv.Classes), "NPC 이름 중복 거부");

var bottleCsv = "ID,메시지\n1,첫 번째 쪽지\n2,\"둘째 줄 첫 문장\n둘째 줄 두 번째 문장\"";
var bottleTable = MessageBottleCsvReader.Parse(bottleCsv, DateTimeOffset.UtcNow);
Check(bottleTable.Items.Count == 2 && bottleTable.Items[1].Message == "둘째 줄 첫 문장\n둘째 줄 두 번째 문장", "병 속에 든 쪽지 CSV와 줄바꿈 검증");
RejectMessageBottle("ID,메시지\n1,첫 쪽지\n1,다른 쪽지", "병 속에 든 쪽지 ID 중복 거부");
RejectMessageBottle("ID,메시지\n1,", "병 속에 든 쪽지 빈 메시지 거부");

var dir = Path.Combine(Path.GetTempPath(), "molly-data-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var source = new FakeSource(valid);
    var catalog = new RuneCatalog(source, dir, _ => { });
    await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => catalog.EnsureFreshAsync(TimeSpan.FromMinutes(10))));
    Check(source.Calls == 1, "동시 최초 요청은 한 번만 갱신");
    await catalog.EnsureFreshAsync(TimeSpan.FromMinutes(10));
    Check(source.Calls == 1, "유효 캐시는 갱신 생략");
    var freshCache = Directory.GetFiles(Path.Combine(dir, "runes"), "*.csv").Single();
    File.SetLastWriteTimeUtc(freshCache, DateTime.UtcNow.AddMinutes(-11));
    source.Fail = true;
    var stale = new RuneCatalog(source, dir, _ => { });
    await stale.InitializeAsync();
    source.Fail = false;
    var beforeRefresh = source.Calls;
    await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => stale.EnsureFreshAsync(TimeSpan.FromMinutes(10))));
    Check(source.Calls == beforeRefresh + 1, "만료 캐시 동시 요청도 한 번만 갱신");
    var original = catalog.Current;
    source.Csv = header;
    Check(!await catalog.RefreshAsync() && ReferenceEquals(original, catalog.Current), "검증 실패 시 스냅샷 유지");
    source.Fail = true;
    Check(!await catalog.RefreshAsync() && ReferenceEquals(original, catalog.Current), "통신 실패 시 스냅샷 유지");
    var restarted = new RuneCatalog(source, dir, _ => { });
    await restarted.InitializeAsync();
    Check(restarted.Current.Items.Count == 1, "재시작 후 오프라인 캐시 복구");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try { await catalog.RefreshAsync(cts.Token); throw new Exception("취소 미전파"); }
    catch (OperationCanceledException) { Console.WriteLine("PASS 취소 전파"); }
    source.Fail = false;
    source.Csv = valid.Replace("테스트", "새 룬");
    await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => catalog.RefreshAsync()));
    Check(source.MaximumConcurrent == 1 && catalog.Current.Items[0].Name == "새 룬", "동시 갱신 직렬화 및 전체 교체");
    Check(original.Items[0].Name == "테스트", "기존 독자 스냅샷 불변");
    var cache = Directory.GetFiles(Path.Combine(dir, "runes"), "*.csv").Single();
    await File.WriteAllTextAsync(cache, "broken");
    source.Fail = true;
    var broken = new RuneCatalog(source, dir, _ => { });
    await broken.InitializeAsync();
    Check(broken.Current.Items.Count == 0, "손상 캐시와 통신 실패 시 안전한 빈 상태");
    var blockedDir = Path.Combine(dir, "file");
    await File.WriteAllTextAsync(blockedDir, "not a directory");
    source.Fail = false;
    var unwritable = new RuneCatalog(source, blockedDir, _ => { });
    Check(!await unwritable.RefreshAsync() && unwritable.Current.Items.Count == 0, "저장 실패 시 새 데이터 미공개");

    var consonantSource = new FakeConsonantSource(consonantCsv);
    var consonantCatalog = new ConsonantCatalog(consonantSource, dir, _ => { });
    await consonantCatalog.InitializeAsync();
    var consonantOriginal = consonantCatalog.Current;
    consonantSource.Csv = new ConsonantCsvData("이름,지역\n중복,던바튼\n중복,반호르", consonantCsv.Regions, consonantCsv.Classes);
    Check(!await consonantCatalog.RefreshAsync() && ReferenceEquals(consonantOriginal, consonantCatalog.Current), "자음퀴즈 시트 검증 실패 시 정상 스냅샷 유지");

    var bottleSource = new FakeMessageBottleSource(bottleCsv);
    var bottleCatalog = new MessageBottleCatalog(bottleSource, dir, _ => { });
    await bottleCatalog.InitializeAsync();
    var bottleOriginal = bottleCatalog.Current;
    bottleSource.Csv = "ID,메시지\n1,첫 쪽지\n1,다른 쪽지";
    Check(!await bottleCatalog.RefreshAsync() && ReferenceEquals(bottleOriginal, bottleCatalog.Current), "병 속에 든 쪽지 검증 실패 시 정상 스냅샷 유지");
}
finally { Directory.Delete(dir, recursive: true); }
var storageDir = Path.Combine(Path.GetTempPath(), "molly-storage-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var legacyDir = Path.Combine(storageDir, "eventexpirealertsetting");
    Directory.CreateDirectory(legacyDir);
    await File.WriteAllTextAsync(Path.Combine(legacyDir, "123.json"), "{\"enabled\":true,\"channelId\":456,\"hoursBefore\":12,\"lastAlertAtKst\":null}");
    var settingsStore = new EventExpireAlertSettingStore(storageDir);
    await settingsStore.InitializeAsync();
    var migrated = await settingsStore.LoadAsync(123);
    Check(migrated is { Enabled: true, ChannelId: 456, HoursBefore: 12 } && !File.Exists(Path.Combine(legacyDir, "123.json")), "기존 이벤트 알림 JSON을 SQLite로 이전 후 삭제");
    await settingsStore.SaveAsync(789, new EventExpireAlertSetting { Enabled = false, ChannelId = 987, HoursBefore = 24 });
    Check((await settingsStore.GetAllGuildIdsAsync()).SequenceEqual(new ulong[] { 123, 789 }) && (await settingsStore.LoadAsync(789))?.ChannelId == 987, "SQLite 이벤트 알림 설정 저장·조회");
    var characterStore = new RegisteredCharacterStore(databasePath: Path.Combine(storageDir, "database", "molly.sqlite"));
    await characterStore.InitializeAsync();
    await characterStore.SaveAsync(new RegisteredCharacter(100, MobiServer.몰리, "첫캐릭터", DateTimeOffset.UnixEpoch));
    await characterStore.SaveAsync(new RegisteredCharacter(100, MobiServer.칼릭스, "바꾼캐릭터", DateTimeOffset.UnixEpoch.AddDays(1)));
    var registeredCharacter = await characterStore.LoadAsync(100);
    Check(registeredCharacter is { Server: MobiServer.칼릭스, CharacterName: "바꾼캐릭터" }, "캐릭터 등록은 길드와 무관하게 Discord 사용자별로 저장·갱신");
    await File.WriteAllTextAsync(Path.Combine(legacyDir, "999.json"), "{broken");
    await settingsStore.InitializeAsync();
    Check(File.Exists(Path.Combine(legacyDir, "999.json")), "손상된 기존 JSON은 삭제하지 않고 다음 이전을 위해 유지");
}
finally { Directory.Delete(storageDir, recursive: true); }
using var http = new HttpClient(new StubHandler());
var httpSource = new GoogleSheetsRuneSource(http);
try { await httpSource.FetchCsvAsync(default); throw new Exception("HTML 허용"); }
catch (InvalidDataException) { Console.WriteLine("PASS HTTP 로그인 HTML 거부"); }
await QuizFlowTests.RunAsync();
var battleRules = new Dictionary<string, BattleRule>
{
    ["base_max_hp"] = new("base_max_hp", "전투능력치", "number", "100", ""), ["base_attack"] = new("base_attack", "전투능력치", "number", "40", ""), ["base_defense"] = new("base_defense", "전투능력치", "number", "0", ""),
    ["power_scale_exponent"] = new("power_scale_exponent", "전투능력치", "number", "0.5", ""), ["power_scale_min"] = new("power_scale_min", "전투능력치", "number", "0.75", ""), ["power_scale_max"] = new("power_scale_max", "전투능력치", "number", "1.25", ""),
    ["defense_coefficient"] = new("defense_coefficient", "피해", "number", "1", ""), ["damage_variance_min"] = new("damage_variance_min", "피해", "number", "1", ""), ["damage_variance_max"] = new("damage_variance_max", "피해", "number", "1", ""), ["fixed_damage_scale"] = new("fixed_damage_scale", "피해", "number", "1", ""), ["base_critical_chance"] = new("base_critical_chance", "치명타", "number", "0", ""), ["critical_damage_multiplier"] = new("critical_damage_multiplier", "치명타", "number", "1.5", ""),
    ["max_major_actions"] = new("max_major_actions", "종료", "integer", "16", ""), ["draw_hp_ratio_threshold"] = new("draw_hp_ratio_threshold", "종료", "number", "0.05", ""), ["break_gauge_maximum"] = new("break_gauge_maximum", "브레이크", "integer", "1", ""), ["break_duration_turns"] = new("break_duration_turns", "브레이크", "integer", "2", ""), ["target_release_evasion_chance"] = new("target_release_evasion_chance", "상태효과", "number", "1", "")
    , ["normal_attack_multiplier"] = new("normal_attack_multiplier", "피해", "number", "1.3", ""), ["skill_damage_min_multiplier"] = new("skill_damage_min_multiplier", "피해", "number", "1.7", ""), ["skill_damage_max_multiplier"] = new("skill_damage_max_multiplier", "피해", "number", "2.1", ""), ["ultimate_damage_multiplier"] = new("ultimate_damage_multiplier", "피해", "number", "2.4", ""), ["minimum_skill_cooldown"] = new("minimum_skill_cooldown", "행동", "integer", "2", ""), ["skill_heal_ratio"] = new("skill_heal_ratio", "회복", "number", "0.06", ""), ["max_surprise_events_per_actor"] = new("max_surprise_events_per_actor", "돌발", "integer", "2", ""), ["surprise_event_global_cooldown"] = new("surprise_event_global_cooldown", "돌발", "integer", "2", ""), ["life_surprise_hp_ratio_threshold"] = new("life_surprise_hp_ratio_threshold", "돌발", "number", "0.5", ""), ["life_surprise_heal_ratio"] = new("life_surprise_heal_ratio", "돌발", "number", "0.08", ""), ["charm_surprise_damage_multiplier"] = new("charm_surprise_damage_multiplier", "돌발", "number", "1.3", ""), ["life_surprise_base_chance"] = new("life_surprise_base_chance", "돌발", "number", "0.2", ""), ["life_surprise_stat_reference"] = new("life_surprise_stat_reference", "돌발", "number", "100000", ""), ["life_surprise_stat_coefficient"] = new("life_surprise_stat_coefficient", "돌발", "number", "0.1", ""), ["life_surprise_max_chance"] = new("life_surprise_max_chance", "돌발", "number", "0.35", ""), ["charm_surprise_base_chance"] = new("charm_surprise_base_chance", "돌발", "number", "0.2", ""), ["charm_surprise_stat_reference"] = new("charm_surprise_stat_reference", "돌발", "number", "100000", ""), ["charm_surprise_stat_coefficient"] = new("charm_surprise_stat_coefficient", "돌발", "number", "0.1", ""), ["charm_surprise_max_chance"] = new("charm_surprise_max_chance", "돌발", "number", "0.35", ""), ["additional_hit_chance"] = new("additional_hit_chance", "추가타", "number", "0", ""), ["additional_hit_damage_ratio"] = new("additional_hit_damage_ratio", "추가타", "number", "0.35", "")
};
var battleSnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["test"] = new("test", "테스트", Array.Empty<string>()) }, Skills = new Dictionary<string, BattleSkill>(), LoadedAt = DateTimeOffset.UtcNow };
var fixedRandom = new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(0.5d, 200)));
var battle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), battleSnapshot, fixedRandom);
Check(battle.Outcome == BattleOutcome.FighterAWin && battle.MajorActions == 3 && battle.Events.Count(x => x.Type == "DamageDealt") == 3, "배틀 엔진은 고정 난수에서 동일한 일반 공격 결과를 생성");
var battleSessions = new BattleSessions();
Check(battleSessions.TryEnter(1, out var firstSession) && !battleSessions.TryEnter(1, out _) && battleSessions.TryStop(1) && firstSession.IsStopRequested && firstSession.CancellationToken.IsCancellationRequested,
    "배틀 세션은 길드당 하나만 진행하고 강제 종료 신호를 전달");
battleSessions.Leave(1, firstSession);
Check(battleSessions.TryEnter(1, out var nextSession), "종료된 배틀 세션은 같은 길드에서 새 배틀을 시작할 수 있다");
battleSessions.Leave(1, nextSession);
var impactRules = battleRules.ToDictionary(x => x.Key, x => x.Value);
impactRules["base_attack"] = new("base_attack", "전투능력치", "number", "20", "");
impactRules["base_critical_chance"] = new("base_critical_chance", "치명타", "number", "1", "");
impactRules["additional_hit_chance"] = new("additional_hit_chance", "추가타", "number", "1", "");
var strikeEffect = new BattleEffect("strike_damage", 1, "피해", "상대", 0, 2, 1, 0, null, 0, null, null, null, null, null, null, null, null);
var strike = new BattleSkill("strike", "시험 일격", "일반", null, true, 2, 0, 1, 1, [strikeEffect]);
var impactSnapshot = new BattleDataSnapshot { Rules = impactRules, Classes = new Dictionary<string, BattleClass> { ["test"] = new("test", "테스트", ["strike"]) }, Skills = new Dictionary<string, BattleSkill> { ["strike"] = strike }, LoadedAt = DateTimeOffset.UtcNow };
var impactBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), impactSnapshot, new FixedBattleRandom(new[] { 0d, .9d, 0d, .5d, 0d, 0d }.Concat(Enumerable.Repeat(.5d, 100))));
var firstTwoHits = impactBattle.Events.Where(x => x.Type == "DamageDealt" && x.Actor == "A").Take(2).ToArray();
Check(impactBattle.Events.Any(x => x.Type == "CriticalHit") && impactBattle.Events.Any(x => x.Type == "AdditionalHit") && firstTwoHits.Length == 2 && firstTwoHits.Sum(x => x.Amount ?? 0) < 80, "효과 행의 다단 피해를 타수만큼 분배하고 치명타·추가타를 처리");
var statusStrike = new BattleSkill("status_strike", "현기증 일격", "일반", null, true, 2, 0, 1, 1,
    [new BattleEffect("apply_dizziness", 1, "상태효과", "상대", 0, 1, 1, 3, "dizziness", 1, null, null, null, null, null, null, null, null), new BattleEffect("refresh_dizziness", 2, "상태효과", "상대", 0, 1, 1, 3, "dizziness", 1, null, null, null, null, null, null, null, null), new BattleEffect("dizzy_damage", 3, "피해", "상대", 0, 1, 1, 0, null, 0, null, null, null, null, null, null, null, null)]);
var statusBaseSnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["test"] = new("test", "테스트", ["status_strike"]) }, Skills = new Dictionary<string, BattleSkill> { ["status_strike"] = statusStrike }, LoadedAt = DateTimeOffset.UtcNow };
var statusSnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = statusBaseSnapshot.Classes, Skills = statusBaseSnapshot.Skills, Statuses = new Dictionary<string, BattleStatus> { ["dizziness"] = new("dizziness", "현기증", "받는피해증가", .15, "받는 피해가 15% 증가") }, LoadedAt = DateTimeOffset.UtcNow };
var statusRandom = new[] { 0d, .5d, .5d, .5d, .5d }.Concat(Enumerable.Repeat(.5d, 100));
var baselineDamage = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), statusBaseSnapshot, new FixedBattleRandom(statusRandom)).Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
var statusBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), statusSnapshot, new FixedBattleRandom(statusRandom));
var statusDamage = statusBattle.Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
Check(statusBattle.Events.Count(x => x.Type == "StatusApplied" && x.Actor == "B" && x.Detail == "현기증" && x.Amount == 3) == 1 && statusDamage > baselineDamage, "상태효과 시트의 받는 피해 증가와 중복 적용 로그 억제를 처리");
var fixedStrike = new BattleSkill("fixed_strike", "고정 피해", "일반", null, true, 4, 0, 1, 1, [new BattleEffect("fixed", 1, "피해", "상대", 50, 1, 1, 0, null, 0, null, null, null, null, null, null, null, null)]);
var fixedSnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["fixed"] = new("fixed", "고정", ["fixed_strike"]) }, Skills = new Dictionary<string, BattleSkill> { ["fixed_strike"] = fixedStrike }, LoadedAt = DateTimeOffset.UtcNow };
var fixedBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "fixed", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "fixed", 100, 0, 0), fixedSnapshot, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100))));
Check(fixedBattle.Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount == 50, "고정값이 있는 피해 효과는 시트의 표시 수치를 사용한다");
var feignEvade = new BattleSkill("feign_evade", "죽은 척", "일반", null, true, 5, 0, 1, 1, [new BattleEffect("release", 1, "대상해제", "자신", 0, 1, 1, 1, "feign_release", 1, null, null, null, null, null, null, null, null)]);
var evadeSnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["feign"] = new("feign", "죽은 척", ["feign_evade"]), ["target"] = new("target", "대상", Array.Empty<string>()) }, Skills = new Dictionary<string, BattleSkill> { ["feign_evade"] = feignEvade }, Statuses = new Dictionary<string, BattleStatus> { ["feign_release"] = new("feign_release", "죽은 척", "대상해제", 0, "다음 공격 회피") }, LoadedAt = DateTimeOffset.UtcNow };
var evadeBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "feign", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "target", 100, 0, 0), evadeSnapshot, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100))));
Check(evadeBattle.Events.Any(x => x.Type == "AttackEvaded" && x.Actor == "B" && x.Target == "A"), "대상 해제 상태는 다음 상대 공격을 회피 판정한다");
var heat = new BattleResource("performance_heat", "공연의 열기", "중첩", 5, 0, 0, "가산");
var finaleStart = new BattleSkill("test_finale", "피날레", "궁극기", null, true, 0, 0, 1, 1, [new BattleEffect("heat", 1, "자원설정", "자신", 5, 1, 1, 0, "performance_heat", 0, null, null, null, null, null, null, null, null)]);
var finaleFinish = new BattleSkill("test_finale_finish", "피날레: 마무리", "파생", "test_finale", true, 0, 0, 1, 1, [new BattleEffect("heat_damage", 1, "추가피해", "상대", 250, 1, 1, 0, null, 0, null, null, null, null, null, null, "performance_heat", "소모중첩배율")], "performance_heat", "전부");
var finaleSnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["finale"] = new("finale", "피날레", ["test_finale"]) }, Skills = new Dictionary<string, BattleSkill> { ["test_finale"] = finaleStart, ["test_finale_finish"] = finaleFinish }, Resources = new Dictionary<string, BattleResource> { ["performance_heat"] = heat }, Derivations = [new BattleDerivation("finish", "test_finale", "test_finale_finish", "조건", 0, 1, "자원보유", "performance_heat>0", false, "재사용 시", 100)], LoadedAt = DateTimeOffset.UtcNow };
var finaleBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "finale", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "finale", 100, 0, 0), finaleSnapshot, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100))));
Check(finaleBattle.Events.Any(x => x.Type == "AdditionalDamage" && x.Actor == "A" && x.Amount == 1250) && finaleBattle.Events.Any(x => x.Type == "ResourceChanged" && x.Actor == "A" && x.Detail == "공연의 열기 -5 (현재 0)"), "피날레 마무리는 열기 중첩으로 추가 피해를 계산한 뒤 모두 소모한다");
var breakSkill = new BattleSkill("break_skill", "브레이크 일격", "일반", null, true, 4, 0, 1, 1, [new BattleEffect("break", 1, "브레이크피해", "상대", 1, 1, 1, 0, null, 0, null, null, null, null, null, null, null, null)]);
var breakSnapshot = new BattleDataSnapshot
{
    Rules = battleRules,
    Classes = new Dictionary<string, BattleClass> { ["breaker"] = new("breaker", "브레이커", ["break_skill"]), ["target"] = new("target", "대상", Array.Empty<string>()) },
    Skills = new Dictionary<string, BattleSkill> { ["break_skill"] = breakSkill },
    Statuses = new Dictionary<string, BattleStatus> { ["break_broken"] = new("break_broken", "브레이크", "브레이크|받는피해증가", .25, "행동 불가 및 받는 피해 증가"), ["break_immunity"] = new("break_immunity", "브레이크 면역", "브레이크면역", 0, "브레이크를 막음") },
    LoadedAt = DateTimeOffset.UtcNow
};
var breakBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "breaker", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "target", 100, 0, 0), breakSnapshot, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100))));
Check(breakBattle.Events.Any(x => x.Type == "BreakActivated" && x.Target == "B") && breakBattle.Events.Any(x => x.Type == "BreakActionLost" && x.Target == "B"), "브레이크 게이지 완성 시 상대 행동을 취소한다");
var wardSkill = new BattleSkill("ward", "브레이크 방어", "일반", null, true, 5, 0, 1, 1, [new BattleEffect("ward", 1, "브레이크면역", "자신", 0, 1, 1, 2, "break_immunity", 1, null, null, null, null, null, null, null, null)]);
var immunitySnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["breaker"] = new("breaker", "브레이커", ["break_skill"]), ["ward"] = new("ward", "방어", ["ward"]) }, Skills = new Dictionary<string, BattleSkill> { ["break_skill"] = breakSkill, ["ward"] = wardSkill }, Statuses = breakSnapshot.Statuses, LoadedAt = DateTimeOffset.UtcNow };
var immunityBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "breaker", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "ward", 100, 0, 0), immunitySnapshot, new FixedBattleRandom(new[] { .9d }.Concat(Enumerable.Repeat(.5d, 100))));
Check(immunityBattle.Events.Any(x => x.Type == "BreakImmune" && x.Target == "B") && !immunityBattle.Events.Any(x => x.Type == "BreakActivated" && x.Target == "B"), "브레이크 면역은 게이지 피해를 막는다");
var dotSkill = new BattleSkill("dot_skill", "두려움의 선율", "일반", null, true, 4, 0, 1, 1, [new BattleEffect("fear", 1, "지속피해", "상대", 0, 2, 1, 2, "fear", 1, "두려움", null, null, null, null, null, null, null)]);
var dotSnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["dot"] = new("dot", "지속", ["dot_skill"]), ["target"] = new("target", "대상", Array.Empty<string>()) }, Skills = new Dictionary<string, BattleSkill> { ["dot_skill"] = dotSkill }, Statuses = new Dictionary<string, BattleStatus> { ["fear"] = new("fear", "두려움", "없음", 0, "턴마다 피해") }, LoadedAt = DateTimeOffset.UtcNow };
var dotBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "dot", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "target", 100, 0, 0), dotSnapshot, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100))));
Check(dotBattle.Events.Any(x => x.Type == "StatusDamage" && x.Actor == "A" && x.Target == "B" && x.Detail == "두려움"), "지속피해 상태는 대상 턴 시작에 피해를 준다");
var melodyResourcesForStatus = new Dictionary<string, BattleResource> { ["bard_valor"] = new("bard_valor", "용맹 악상", "악상", 1, 0, 0, "상호배타") };
var tunedStrike = new BattleSkill("tuned_strike", "정밀 연주", "일반", null, true, 6, 0, 1, 1,
    [new BattleEffect("tuned", 1, "악상피해증가", "자신", 0, 1, 1, 2, "fine_tuned", 1, null, null, null, null, null, null, null, null), new BattleEffect("melody", 2, "자원설정", "자신", 1, 1, 1, 0, "bard_valor", 1, null, null, null, null, null, null, null, null), new BattleEffect("tuned_damage", 3, "피해", "상대", 0, 1, 1, 0, null, 0, null, "자신", "자원보유", "bard_valor", ">=", "1", null, null)]);
var tunedBase = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["tuned"] = new("tuned", "정밀", ["tuned_strike"]) }, Skills = new Dictionary<string, BattleSkill> { ["tuned_strike"] = tunedStrike }, Resources = melodyResourcesForStatus, LoadedAt = DateTimeOffset.UtcNow };
var tunedSnapshot = new BattleDataSnapshot { Rules = battleRules, Classes = tunedBase.Classes, Skills = tunedBase.Skills, Resources = melodyResourcesForStatus, Statuses = new Dictionary<string, BattleStatus> { ["fine_tuned"] = new("fine_tuned", "정밀 조율", "악상피해증가", .1, "악상 스킬 피해 증가") }, LoadedAt = DateTimeOffset.UtcNow };
var tunedBaseline = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "tuned", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "tuned", 100, 0, 0), tunedBase, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100)))).Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
var tunedDamage = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "tuned", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "tuned", 100, 0, 0), tunedSnapshot, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100)))).Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
Check(tunedDamage > tunedBaseline, "정밀 조율은 활성 악상이 적용된 스킬 피해만 높인다");
var rangePrep = new BattleSkill("range_prep", "원거리 전환", "일반", null, true, 6, 0, 1, 1, [new BattleEffect("range", 1, "기본공격변경", "자신", 0, 1, 1, 2, "basic_attack_ranged", 1, null, null, null, null, null, null, null, null)]);
var rangeBase = new BattleDataSnapshot { Rules = battleRules, Classes = new Dictionary<string, BattleClass> { ["range"] = new("range", "원거리", ["range_prep"]) }, Skills = new Dictionary<string, BattleSkill> { ["range_prep"] = rangePrep }, LoadedAt = DateTimeOffset.UtcNow };
var rangeStatus = new BattleDataSnapshot { Rules = battleRules, Classes = rangeBase.Classes, Skills = rangeBase.Skills, Statuses = new Dictionary<string, BattleStatus> { ["basic_attack_ranged"] = new("basic_attack_ranged", "원거리 기본 공격", "기본공격피해증가", .2, "원거리 기본 공격 피해 증가") }, LoadedAt = DateTimeOffset.UtcNow };
var rangeBaseline = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "range", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "range", 100, 0, 0), rangeBase, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100)))).Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
var rangeDamage = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "range", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "range", 100, 0, 0), rangeStatus, new FixedBattleRandom(new[] { 0d }.Concat(Enumerable.Repeat(.5d, 100)))).Events.First(x => x.Type == "DamageDealt" && x.Actor == "A").Amount ?? 0;
Check(rangeDamage > rangeBaseline, "원거리 기본 공격 상태는 다음 일반 공격을 강화한다");
var parentSkill = new BattleSkill("parent", "부모 스킬", "일반", null, true, 1, 0, 1, 1, Array.Empty<BattleEffect>());
var childSkill = new BattleSkill("child", "파생 스킬", "파생", "parent", true, 1, 0, 1, 1, [new BattleEffect("child_damage", 1, "피해", "상대", 0, 1, 1, 0, null, 0, null, null, null, null, null, null, null, null)]);
var derivationSnapshot = new BattleDataSnapshot
{
    Rules = impactRules,
    Classes = new Dictionary<string, BattleClass> { ["test"] = new("test", "테스트", ["parent"]) },
    Skills = new Dictionary<string, BattleSkill> { ["parent"] = parentSkill, ["child"] = childSkill },
    Derivations = [new BattleDerivation("parent_child", "parent", "child", "무작위", 1, 1, null, null, false, "즉시", 1)],
    LoadedAt = DateTimeOffset.UtcNow
};
var derivationBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), derivationSnapshot, new FixedBattleRandom(Enumerable.Repeat(0d, 100)));
var derivedHeaderIndex = derivationBattle.Events.ToList().FindIndex(x => x.Type == "SkillUsed" && x.Detail == "파생 스킬");
var derivedCriticalIndex = derivationBattle.Events.ToList().FindIndex(x => x.Type == "CriticalHit");
Check(derivedHeaderIndex >= 0 && derivedCriticalIndex > derivedHeaderIndex, "무작위 파생 스킬의 제목은 치명타·피해 효과보다 먼저 기록");
var resourceDefinitions = new Dictionary<string, BattleResource>
{
    ["focus"] = new("focus", "집중", "자원", 3, 0, 0, "가산"),
    ["grace"] = new("grace", "우아", "태세", 1, 0, 0, "상호배타"),
    ["passion"] = new("passion", "정열", "태세", 1, 0, 0, "상호배타")
};
var resourceParent = new BattleSkill("resource_parent", "집중 준비", "일반", null, true, 3, 0, 1, 1,
    [new BattleEffect("focus_gain", 1, "자원증가", "자신", 1, 1, 1, 0, "focus", 0, null, null, null, null, null, null, null, null)]);
var resourceChild = new BattleSkill("resource_child", "집중 일격", "파생", "resource_parent", true, 0, 0, 1, 1,
    [new BattleEffect("focus_damage", 1, "피해", "상대", 0, 1, 1, 0, null, 0, null, null, null, null, null, null, null, null)]);
var stanceSkill = new BattleSkill("stance", "태세 전환", "일반", null, true, 4, 0, 1, 1,
    [new BattleEffect("grace_set", 1, "자원설정", "자신", 1, 1, 1, 0, "grace", 0, null, null, null, null, null, null, null, null), new BattleEffect("passion_set", 2, "자원설정", "자신", 1, 1, 1, 0, "passion", 0, null, null, null, null, null, null, null, null)]);
var resourceSnapshot = new BattleDataSnapshot
{
    Rules = impactRules,
    Classes = new Dictionary<string, BattleClass> { ["test"] = new("test", "테스트", ["resource_parent", "stance"]) },
    Skills = new Dictionary<string, BattleSkill> { ["resource_parent"] = resourceParent, ["resource_child"] = resourceChild, ["stance"] = stanceSkill },
    Resources = resourceDefinitions,
    Derivations = [new BattleDerivation("focus_child", "resource_parent", "resource_child", "조건", 0, 1, "자원보유", "focus>=1", false, "즉시", 100)],
    LoadedAt = DateTimeOffset.UtcNow
};
var resourceBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), resourceSnapshot, new FixedBattleRandom(Enumerable.Repeat(0d, 100)));
var resourceEvents = resourceBattle.Events.ToList();
Check(resourceEvents.Any(x => x.Type == "ResourceChanged" && x.Detail == "집중 +1 (현재 1)") && resourceEvents.Any(x => x.Type == "DerivedSkillUsed" && x.Detail == "집중 일격"), "자원 증감과 자원 조건 즉시 파생을 처리");
var feign = new BattleSkill("feign", "죽은 척 하기", "일반", null, true, 6, 0, 1, 1,
    [new BattleEffect("feign_state", 1, "받는피해감소", "자신", 0, 1, 1, 1, "feign_state", 1, null, null, null, null, null, null, null, null)]);
var rising = new BattleSkill("rising", "라이징 윈드밀", "파생", "feign", true, 0, 0, 1, 1,
    [new BattleEffect("rising_damage", 1, "피해", "상대", 0, 1, 1, 0, null, 0, null, null, null, null, null, null, null, null)]);
var feignSnapshot = new BattleDataSnapshot
{
    Rules = impactRules,
    Classes = new Dictionary<string, BattleClass> { ["test"] = new("test", "테스트", ["feign"]) },
    Skills = new Dictionary<string, BattleSkill> { ["feign"] = feign, ["rising"] = rising },
    Derivations = [new BattleDerivation("feign_expire", "feign", "rising", "조건", 0, 1, "상태효과보유", "feign_state", false, "상태만료 시", 100)],
    LoadedAt = DateTimeOffset.UtcNow
};
var feignBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), feignSnapshot, new FixedBattleRandom(Enumerable.Repeat(0d, 100)));
var feignEvents = feignBattle.Events.ToList();
var stateExpiredIndex = feignEvents.FindIndex(x => x.Type == "StatusExpired" && x.Actor == "A" && x.Detail == "feign_state");
var risingIndex = feignEvents.FindIndex(x => x.Type == "SkillUsed" && x.Actor == "A" && x.Detail == "라이징 윈드밀");
Check(stateExpiredIndex >= 0 && risingIndex > stateExpiredIndex, "상태 만료 파생은 다음 행동을 라이징 윈드밀로 교체");
var heldFeign = feign with { Id = "held_feign", Effects = [new BattleEffect("held_feign_state", 1, "받는피해감소", "자신", 0, 1, 1, 2, "held_feign_state", 1, null, null, null, null, null, null, null, null)] };
var reuseRising = rising with { ParentSkillId = "held_feign" };
var reuseSnapshot = new BattleDataSnapshot
{
    Rules = impactRules,
    Classes = new Dictionary<string, BattleClass> { ["test"] = new("test", "테스트", ["held_feign"]) },
    Skills = new Dictionary<string, BattleSkill> { ["held_feign"] = heldFeign, ["rising"] = reuseRising },
    Derivations = [new BattleDerivation("held_feign_reuse", "held_feign", "rising", "조건", 0, 1, "상태효과보유", "held_feign_state", false, "재사용 시", 100)],
    LoadedAt = DateTimeOffset.UtcNow
};
var reuseBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), reuseSnapshot, new FixedBattleRandom(Enumerable.Repeat(0d, 100)));
Check(reuseBattle.Events.Any(x => x.Type == "SkillUsed" && x.Actor == "A" && x.Detail == "라이징 윈드밀"), "유지 중인 상태의 재사용 파생은 쿨다운과 관계없이 실행");
var melodyResources = new Dictionary<string, BattleResource>
{
    ["bard_valor"] = new("bard_valor", "용맹 악상", "악상", 1, 0, 0, "상호배타"),
    ["bard_hope"] = new("bard_hope", "희망 악상", "악상", 1, 0, 0, "상호배타")
};
var melody = new BattleSkill("melody", "멜로디 쇼크", "일반", null, true, 4, 0, 1, 1,
    [new BattleEffect("valor", 1, "자원설정", "자신", 1, 1, 1, 0, "bard_valor", 1, null, "자신", "분류자원미보유", "악상", "미보유", "0", null, null), new BattleEffect("hope_blocked", 2, "자원설정", "자신", 1, 1, 1, 0, "bard_hope", 1, null, "자신", "분류자원미보유", "악상", "미보유", "0", null, null)]);
var bardTale = new BattleSkill("bard_tale", "바즈 테일", "일반", null, true, 2, 0, 1, 1,
    [new BattleEffect("hope", 1, "자원설정", "자신", 1, 1, 1, 0, "bard_hope", 1, null, "자신", "분류자원미보유", "악상", "미보유", "0", null, null),
     new BattleEffect("tale_damage", 2, "피해", "상대", 0, 1, 1, 0, null, 0, null, null, null, null, null, null, null, null),
     new BattleEffect("tale_heal", 3, "회복", "자신", 0, 2, 1, 0, null, 0, null, null, null, null, null, null, null, null)]);
var valorSong = new BattleSkill("valor_song", "용맹의 찬가", "파생", "bard_tale", true, 0, 0, 1, 1, Array.Empty<BattleEffect>());
var hopeSong = new BattleSkill("hope_song", "희망의 송가", "파생", "bard_tale", true, 0, 0, 1, 1, Array.Empty<BattleEffect>());
var melodySnapshot = new BattleDataSnapshot
{
    Rules = impactRules,
    Classes = new Dictionary<string, BattleClass> { ["test"] = new("test", "테스트", ["melody", "bard_tale"]) },
    Skills = new Dictionary<string, BattleSkill> { ["melody"] = melody, ["bard_tale"] = bardTale, ["valor_song"] = valorSong, ["hope_song"] = hopeSong },
    Resources = melodyResources,
    Derivations = [new BattleDerivation("valor_path", "bard_tale", "valor_song", "대체", 0, 1, "자원보유", "bard_valor", false, "즉시", 100), new BattleDerivation("hope_path", "bard_tale", "hope_song", "대체", 0, 1, "자원보유", "bard_hope", false, "즉시", 100)],
    LoadedAt = DateTimeOffset.UtcNow
};
var melodyBattle = new BattleEngine().Simulate(new CharacterBattleSnapshot(1, "A", "test", 100, 0, 0), new CharacterBattleSnapshot(2, "B", "test", 100, 0, 0), melodySnapshot, new FixedBattleRandom(Enumerable.Repeat(0d, 100)));
Check(melodyBattle.Events.Any(x => x.Type == "ResourceChanged" && x.Detail == "용맹 악상 +1 (현재 1)") && !melodyBattle.Events.Any(x => x.Type == "ResourceChanged" && x.Detail?.Contains("희망 악상 +1") == true) && melodyBattle.Events.Any(x => x.Type == "SkillUsed" && x.Actor == "A" && x.Detail == "용맹의 찬가") && !melodyBattle.Events.Any(x => x.Type == "SkillUsed" && x.Actor == "A" && x.Detail == "바즈 테일") && melodyBattle.Events.Any(x => x.Type == "DamageDealt" && x.Actor == "A"), "악상 생성은 비어 있을 때만 실행되고 바즈 테일을 실제 연주곡으로 대체");
Console.WriteLine("모든 오프라인 데이터·퀴즈 테스트 통과");

void RejectConsonants(ConsonantCsvData csv, string name)
{
    try { ConsonantCsvReader.Parse(csv, DateTimeOffset.UtcNow); }
    catch (InvalidDataException) { Console.WriteLine("PASS " + name); return; }
    throw new Exception(name);
}

void RejectMessageBottle(string csv, string name)
{
    try { MessageBottleCsvReader.Parse(csv, DateTimeOffset.UtcNow); }
    catch (InvalidDataException) { Console.WriteLine("PASS " + name); return; }
    throw new Exception(name);
}

sealed class FakeSource(string csv) : IRuneSource
{
    public string CacheKey => "test-source";
    public string Csv { get; set; } = csv;
    public bool Fail { get; set; }
    public int Calls;
    public int MaximumConcurrent { get; private set; }
    private int active;
    public async Task<string> FetchCsvAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref active));
        try
        {
            await Task.Delay(10, ct);
            if (Fail) throw new HttpRequestException("offline");
            return Csv;
        }
        finally { Interlocked.Decrement(ref active); }
    }
}
sealed class FixedBattleRandom(IEnumerable<double> values) : IBattleRandom
{
    private readonly Queue<double> values = new(values);
    public double NextDouble() => values.Count == 0 ? 0.5 : values.Dequeue();
}
sealed class StubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<html>Login</html>", System.Text.Encoding.UTF8, "text/html") });
}
sealed class FakeConsonantSource(ConsonantCsvData csv) : IConsonantSource
{
    public string CacheKey => "test-consonant-source";
    public ConsonantCsvData Csv { get; set; } = csv;
    public Task<ConsonantCsvData> FetchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Csv);
    }
}
sealed class FakeMessageBottleSource(string csv) : IMessageBottleSource
{
    public string CacheKey => "test-message-bottle-source";
    public string Csv { get; set; } = csv;
    public Task<string> FetchCsvAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Csv);
    }
}
