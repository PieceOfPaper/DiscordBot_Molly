using System.Net;
using Molly.Runes;
using Molly.Quiz;

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
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
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
var round = new QuizRound("두 룬", 100, TimeSpan.FromSeconds(30));
Check(round.Submit(1, 99, "두 룬") == false, "문제 이전 메시지 무시");
Check(round.Submit(1, 101, " 두룬 "), "정답 선착순 처리");
Check(round.Submit(2, 102, "두 룬") == false, "한 문제 한 명만 정답 처리");
round.Close();
var normalized = QuizQuestions.NormalizeAnswer("두 룬");
Check(normalized == "두룬", "퀴즈 정답 정규화");

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
}
finally { Directory.Delete(dir, recursive: true); }
using var http = new HttpClient(new StubHandler());
var httpSource = new GoogleSheetsRuneSource(http);
try { await httpSource.FetchCsvAsync(default); throw new Exception("HTML 허용"); }
catch (InvalidDataException) { Console.WriteLine("PASS HTTP 로그인 HTML 거부"); }
await QuizFlowTests.RunAsync();
Console.WriteLine("모든 오프라인 데이터·퀴즈 테스트 통과");

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
sealed class StubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<html>Login</html>", System.Text.Encoding.UTF8, "text/html") });
}
