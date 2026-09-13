using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Molly.Quiz;

public sealed record ConsonantCsvData(string Npcs, string Regions, string Classes);

public interface IConsonantSource
{
    string CacheKey { get; }
    Task<ConsonantCsvData> FetchAsync(CancellationToken ct);
}

public sealed class GoogleSheetsConsonantSource : IConsonantSource
{
    private readonly HttpClient client;
    private readonly Uri npcUri;
    private readonly Uri regionUri;
    private readonly Uri classUri;
    public string CacheKey => $"{npcUri}|{regionUri}|{classUri}";

    public GoogleSheetsConsonantSource(HttpClient client, string spreadsheetId)
    {
        if (string.IsNullOrWhiteSpace(spreadsheetId) || spreadsheetId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Google Sheets 문서 ID를 확인하세요.", nameof(spreadsheetId));
        this.client = client;
        npcUri = CreateUri(spreadsheetId, "NPC");
        regionUri = CreateUri(spreadsheetId, "지역");
        classUri = CreateUri(spreadsheetId, "클래스");
    }

    public async Task<ConsonantCsvData> FetchAsync(CancellationToken ct)
        => new(await FetchCsvAsync(npcUri, ct), await FetchCsvAsync(regionUri, ct), await FetchCsvAsync(classUri, ct));

    private static Uri CreateUri(string spreadsheetId, string sheetName)
        => new($"https://docs.google.com/spreadsheets/d/{spreadsheetId}/gviz/tq?tqx=out:csv&sheet={Uri.EscapeDataString(sheetName)}");

    private async Task<string> FetchCsvAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(uri, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("Google Sheets가 CSV 대신 HTML을 반환했습니다. 링크 공개 읽기 권한을 확인하세요.");
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }
}

public sealed record NpcData(string Name, string Region);
public sealed record RegionData(string Name, string Category);
public sealed record ClassData(string Name);

public sealed class ConsonantTable
{
    public IReadOnlyList<NpcData> Npcs { get; }
    public IReadOnlyList<RegionData> Regions { get; }
    public IReadOnlyList<ClassData> Classes { get; }
    public DateTimeOffset LoadedAt { get; }

    internal ConsonantTable(IEnumerable<NpcData> npcs, IEnumerable<RegionData> regions, IEnumerable<ClassData> classes, DateTimeOffset loadedAt)
    {
        Npcs = Array.AsReadOnly(npcs.ToArray());
        Regions = Array.AsReadOnly(regions.ToArray());
        Classes = Array.AsReadOnly(classes.ToArray());
        LoadedAt = loadedAt;
    }

    public static ConsonantTable Empty { get; } = new([], [], [], DateTimeOffset.MinValue);
}

public static class ConsonantCsvReader
{
    public static ConsonantTable Parse(ConsonantCsvData csv, DateTimeOffset loadedAt)
    {
        var npcs = Read(csv.Npcs, ["이름", "지역"], "NPC", row => new NpcData(row["이름"], row["지역"]));
        var regions = Read(csv.Regions, ["이름", "분류", "대륙"], "지역", row => new RegionData(row["이름"], row["분류"]));
        var classes = Read(csv.Classes, ["이름", "계열"], "클래스", row => new ClassData(row["이름"]));
        if (npcs.Count == 0 || regions.Count == 0 || classes.Count == 0)
            throw new InvalidDataException("NPC·지역·클래스 시트에는 각각 한 개 이상의 출제 가능한 행이 필요합니다.");
        return new(npcs, regions, classes, loadedAt);
    }

    private static List<T> Read<T>(string csv, string[] required, string tableName, Func<Dictionary<string, string>, T> create)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        var headers = (parser.ReadFields() ?? throw new InvalidDataException($"{tableName} 테이블 헤더가 없습니다.")).Select(x => x.Trim()).ToArray();
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length || required.Any(x => !headers.Contains(x)))
            throw new InvalidDataException($"{tableName} 테이블에 중복 헤더가 있거나 필수 헤더({string.Join('·', required)})가 없습니다.");
        var result = new List<T>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.EndOfData)
        {
            var line = parser.LineNumber;
            var fields = parser.ReadFields()!;
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Length != headers.Length) throw new InvalidDataException($"{tableName} CSV {line}행의 열 수가 헤더와 다릅니다.");
            var row = headers.Zip(fields, (header, value) => (header, value: value.Trim())).ToDictionary(x => x.header, x => x.value);
            if (row["이름"].Length == 0 || !names.Add(row["이름"]))
                throw new InvalidDataException($"{tableName} CSV {line}행의 이름이 비어 있거나 중복됩니다.");
            if (tableName == "지역" && row["분류"].Length == 0)
                throw new InvalidDataException($"지역 CSV {line}행의 분류가 비어 있습니다.");
            result.Add(create(row));
        }
        return result;
    }
}

public sealed class ConsonantCatalog
{
    private readonly IConsonantSource source;
    private readonly string cachePath;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConsonantTable current = ConsonantTable.Empty;
    public ConsonantTable Current => Volatile.Read(ref current);

    public ConsonantCatalog(IConsonantSource source, string dataDirectory, Action<string>? log = null)
    {
        this.source = source;
        this.log = log ?? Console.WriteLine;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.CacheKey)));
        cachePath = Path.Combine(dataDirectory, "consonants", key + ".json");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Current.LoadedAt == DateTimeOffset.MinValue && File.Exists(cachePath))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<ConsonantCsvData>(await File.ReadAllTextAsync(cachePath, ct));
                    if (cached is null) throw new InvalidDataException("자음퀴즈 캐시가 비어 있습니다.");
                    Volatile.Write(ref current, ConsonantCsvReader.Parse(cached, File.GetLastWriteTimeUtc(cachePath)));
                    log($"[자음퀴즈] 로컬 캐시 로딩 완료");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { log($"[자음퀴즈] 캐시 로딩 실패: {ex.Message}"); }
            }
        }
        finally { gate.Release(); }
        await RefreshAsync(ct);
    }

    public async Task<bool> EnsureFreshAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        if (maxAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxAge));
        await gate.WaitAsync(ct);
        try
        {
            return Current.LoadedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - Current.LoadedAt < maxAge
                || await RefreshLockedAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await RefreshLockedAsync(ct); }
        finally { gate.Release(); }
    }

    private async Task<bool> RefreshLockedAsync(CancellationToken ct)
    {
        try
        {
            var csv = await source.FetchAsync(ct);
            var snapshot = ConsonantCsvReader.Parse(csv, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(csv), Encoding.UTF8, ct);
                File.Move(temporary, cachePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Volatile.Write(ref current, snapshot);
            log($"[자음퀴즈] 시트 갱신 완료: NPC {snapshot.Npcs.Count}개, 지역 {snapshot.Regions.Count}개, 클래스 {snapshot.Classes.Count}개");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log($"[자음퀴즈] 갱신 실패, 기존 데이터 유지: {ex.Message}"); return false; }
    }
}
