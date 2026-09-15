using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Molly.Messages;

public sealed record MessageBottleEntry(string Id, string Message);

public interface IMessageBottleSource
{
    string CacheKey { get; }
    Task<string> FetchCsvAsync(CancellationToken ct);
}

public sealed class GoogleSheetsMessageBottleSource : IMessageBottleSource
{
    public const string DefaultSheetName = "병 속에 든 쪽지";
    private readonly HttpClient client;
    public Uri CsvUri { get; }
    public string CacheKey => CsvUri.AbsoluteUri;

    public GoogleSheetsMessageBottleSource(HttpClient client, string spreadsheetId, string sheetName = DefaultSheetName)
    {
        if (string.IsNullOrWhiteSpace(spreadsheetId) || spreadsheetId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Google Sheets 문서 ID를 확인하세요.", nameof(spreadsheetId));
        if (string.IsNullOrWhiteSpace(sheetName)) throw new ArgumentException("쪽지 시트 이름을 확인하세요.", nameof(sheetName));
        this.client = client;
        CsvUri = new Uri($"https://docs.google.com/spreadsheets/d/{spreadsheetId}/gviz/tq?tqx=out:csv&sheet={Uri.EscapeDataString(sheetName)}");
    }

    public async Task<string> FetchCsvAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(CsvUri, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("Google Sheets가 CSV 대신 HTML을 반환했습니다. 링크 공개 읽기 권한을 확인하세요.");
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }
}

public sealed class MessageBottleTable
{
    public static MessageBottleTable Empty { get; } = new([], DateTimeOffset.MinValue);
    public IReadOnlyList<MessageBottleEntry> Items { get; }
    public DateTimeOffset LoadedAt { get; }

    internal MessageBottleTable(IEnumerable<MessageBottleEntry> items, DateTimeOffset loadedAt)
    {
        Items = Array.AsReadOnly(items.ToArray());
        LoadedAt = loadedAt;
    }
}

public static class MessageBottleCsvReader
{
    public static MessageBottleTable Parse(string csv, DateTimeOffset loadedAt)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        var headers = (parser.ReadFields() ?? throw new InvalidDataException("병 속에 든 쪽지 테이블 헤더가 없습니다."))
            .Select(x => x.Trim()).ToArray();
        var required = new[] { "ID", "메시지" };
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length || required.Any(x => !headers.Contains(x)))
            throw new InvalidDataException("병 속에 든 쪽지 테이블에 중복 헤더가 있거나 필수 헤더(ID·메시지)가 없습니다.");

        var entries = new List<MessageBottleEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.EndOfData)
        {
            var line = parser.LineNumber;
            var fields = parser.ReadFields()!;
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Length != headers.Length) throw new InvalidDataException($"병 속에 든 쪽지 CSV {line}행의 열 수가 헤더와 다릅니다.");
            var row = headers.Zip(fields, (header, value) => (header, value: value.Trim())).ToDictionary(x => x.header, x => x.value);
            if (row["ID"].Length == 0 || !ids.Add(row["ID"]))
                throw new InvalidDataException($"병 속에 든 쪽지 CSV {line}행의 ID가 비어 있거나 중복됩니다.");
            if (row["메시지"].Length == 0)
                throw new InvalidDataException($"병 속에 든 쪽지 CSV {line}행의 메시지가 비어 있습니다.");
            if (row["메시지"].Length > 3500)
                throw new InvalidDataException($"병 속에 든 쪽지 CSV {line}행의 메시지는 3500자 이하여야 합니다.");
            entries.Add(new MessageBottleEntry(row["ID"], row["메시지"]));
        }
        if (entries.Count == 0) throw new InvalidDataException("사용 가능한 병 속에 든 쪽지가 없어 기존 데이터를 유지합니다.");
        return new MessageBottleTable(entries, loadedAt);
    }
}

public sealed class MessageBottleCatalog
{
    private readonly IMessageBottleSource source;
    private readonly string cachePath;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MessageBottleTable current = MessageBottleTable.Empty;
    public MessageBottleTable Current => Volatile.Read(ref current);

    public MessageBottleCatalog(IMessageBottleSource source, string dataDirectory, Action<string>? log = null)
    {
        this.source = source;
        this.log = log ?? Console.WriteLine;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.CacheKey)));
        cachePath = Path.Combine(dataDirectory, "message-bottles", key + ".csv");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Current.Items.Count == 0 && File.Exists(cachePath))
            {
                try
                {
                    var csv = await File.ReadAllTextAsync(cachePath, ct);
                    Volatile.Write(ref current, MessageBottleCsvReader.Parse(csv, File.GetLastWriteTimeUtc(cachePath)));
                    log($"[병 속에 든 쪽지] 로컬 캐시 {Current.Items.Count}개 로딩 완료");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { log($"[병 속에 든 쪽지] 캐시 로딩 실패: {ex.Message}"); }
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
            var csv = await source.FetchCsvAsync(ct);
            var snapshot = MessageBottleCsvReader.Parse(csv, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, csv, Encoding.UTF8, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, cachePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Volatile.Write(ref current, snapshot);
            log($"[병 속에 든 쪽지] 시트 갱신 완료: {snapshot.Items.Count}개");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log($"[병 속에 든 쪽지] 갱신 실패, 기존 {Current.Items.Count}개 유지: {ex.Message}");
            return false;
        }
    }
}
