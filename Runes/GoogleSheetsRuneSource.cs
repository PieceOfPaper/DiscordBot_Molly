namespace Molly.Runes;

// 게임이나 Discord 명령은 이 공급 경로 대신 RuneCatalog의 스냅샷을 사용합니다.
public interface IRuneSource
{
    string CacheKey { get; }
    Task<string> FetchCsvAsync(CancellationToken ct);
}

public sealed class GoogleSheetsRuneSource : IRuneSource
{
    public const string DefaultSpreadsheetId = "19kRuVIlZ1LEhU5lixEjRqYvZKbdGnXk0tjY6qznSRf0";
    private readonly HttpClient client;
    public Uri CsvUri { get; }
    public string CacheKey => CsvUri.AbsoluteUri;

    public GoogleSheetsRuneSource(HttpClient client, string spreadsheetId = DefaultSpreadsheetId, string sheetId = "0")
    {
        if (string.IsNullOrWhiteSpace(spreadsheetId) || spreadsheetId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Google Sheets 문서 ID를 확인하세요.", nameof(spreadsheetId));
        if (!uint.TryParse(sheetId, out _)) throw new ArgumentException("Google Sheets 탭 ID(gid)를 확인하세요.", nameof(sheetId));
        this.client = client;
        CsvUri = new Uri($"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=csv&gid={sheetId}");
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
