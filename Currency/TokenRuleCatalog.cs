using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace Molly.Currency;

public interface ITokenRuleSource
{
    string CacheKey { get; }
    Task<string> FetchCsvAsync(CancellationToken ct);
}

/// <summary>
/// `증표규칙` 시트. `값` 열에 숫자·문자열이 섞이므로 gviz(열 자료형을 추론해 숫자 열의 문자열 셀을 비움)가 아니라
/// export CSV를 탭 gid로 읽는다.
/// </summary>
public sealed class GoogleSheetsTokenRuleSource : ITokenRuleSource
{
    public const string DefaultSheetId = "1750775172";
    private readonly HttpClient client;
    public Uri CsvUri { get; }
    public string CacheKey => CsvUri.AbsoluteUri;

    public GoogleSheetsTokenRuleSource(HttpClient client, string spreadsheetId, string sheetId = DefaultSheetId)
    {
        if (string.IsNullOrWhiteSpace(spreadsheetId) || spreadsheetId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Google Sheets 문서 ID를 확인하세요.", nameof(spreadsheetId));
        if (string.IsNullOrWhiteSpace(sheetId) || !sheetId.All(char.IsAsciiDigit))
            throw new ArgumentException("증표규칙 시트 gid를 확인하세요.", nameof(sheetId));
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

/// <summary>`증표규칙` 시트의 `자료형` 열 값.</summary>
public enum TokenRuleType
{
    /// <summary>`정수`: 64비트 정수.</summary>
    Integer,
    /// <summary>`실수`: 소수(비율 등).</summary>
    Decimal,
    /// <summary>`문자열`: 최소값·최대값을 쓰지 않는다.</summary>
    Text,
    /// <summary>`정수목록`: `|`로 구분한 정수들(예: 빠른 선택 금액 `10|50|100`). 최소값·최대값은 각 원소에 적용한다.</summary>
    IntegerList,
}

/// <summary>검증을 마친 규칙 한 줄. 활성화되지 않은 행은 목록에 넣지 않는다.</summary>
public sealed record TokenRule(string Id, TokenRuleType Type, string RawValue, long? Integer, decimal? Decimal, IReadOnlyList<long>? Integers);

/// <summary>
/// 유효한 규칙 스냅샷. 증표를 쓰는 기능은 자기 규칙 ID와 코드 기본값을 함께 넘겨 값을 꺼낸다.
/// 시트에 행이 없거나, 비활성화되었거나, 자료형이 다르면 기본값을 쓴다.
/// </summary>
public sealed class TokenRuleTable
{
    public static TokenRuleTable Empty { get; } = new([], DateTimeOffset.MinValue);
    private readonly Dictionary<string, TokenRule> rules;
    public IReadOnlyCollection<TokenRule> Rules => rules.Values;
    public DateTimeOffset LoadedAt { get; }

    internal TokenRuleTable(IEnumerable<TokenRule> rules, DateTimeOffset loadedAt)
    {
        this.rules = rules.ToDictionary(x => x.Id, StringComparer.Ordinal);
        LoadedAt = loadedAt;
    }

    public TokenRule? Find(string id) => rules.GetValueOrDefault(id);

    public long GetInteger(string id, long fallback) => Find(id) is { Type: TokenRuleType.Integer, Integer: { } value } ? value : fallback;

    public decimal GetDecimal(string id, decimal fallback) => Find(id) switch
    {
        { Type: TokenRuleType.Decimal, Decimal: { } value } => value,
        { Type: TokenRuleType.Integer, Integer: { } value } => value,
        _ => fallback,
    };

    public string GetText(string id, string fallback) => Find(id) is { Type: TokenRuleType.Text } rule ? rule.RawValue : fallback;

    public IReadOnlyList<long> GetIntegerList(string id, IReadOnlyList<long> fallback)
        => Find(id) is { Type: TokenRuleType.IntegerList, Integers: { } values } ? values : fallback;
}

public static partial class TokenRuleCsvReader
{
    public static readonly IReadOnlyList<string> Headers = ["ID", "활성화", "값", "자료형", "최소값", "최대값", "단위", "설명", "비고"];

    private static readonly IReadOnlyDictionary<string, TokenRuleType> s_Types = new Dictionary<string, TokenRuleType>(StringComparer.Ordinal)
    {
        ["정수"] = TokenRuleType.Integer,
        ["실수"] = TokenRuleType.Decimal,
        ["문자열"] = TokenRuleType.Text,
        ["정수목록"] = TokenRuleType.IntegerList,
    };

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// 한 행이라도 잘못되면 표 전체를 거부한다(호출한 쪽이 마지막 정상 스냅샷을 유지).
    /// 규칙 행이 하나도 없는 시트는 정상이다. 각 기능이 코드 기본값을 쓴다.
    /// </summary>
    public static TokenRuleTable Parse(string csv, DateTimeOffset loadedAt)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('﻿')))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        var headers = (parser.ReadFields() ?? throw new InvalidDataException("증표규칙 시트 헤더가 없습니다."))
            .Select(x => x.Trim()).ToArray();
        var required = new[] { "ID", "활성화", "값", "자료형", "최소값", "최대값" };
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length || required.Any(x => !headers.Contains(x)))
            throw new InvalidDataException("증표규칙 시트에 중복 헤더가 있거나 필수 헤더(ID·활성화·값·자료형·최소값·최대값)가 없습니다.");

        var rules = new List<TokenRule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.EndOfData)
        {
            var line = parser.LineNumber;
            var fields = parser.ReadFields()!;
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Length != headers.Length) throw new InvalidDataException($"증표규칙 CSV {line}행의 열 수가 헤더와 다릅니다.");
            var row = headers.Zip(fields, (header, value) => (header, value: value.Trim())).ToDictionary(x => x.header, x => x.value);
            var id = row["ID"];
            string Error(string message) => $"증표규칙 CSV {line}행({id}): {message}";

            if (!IdPattern().IsMatch(id)) throw new InvalidDataException(Error("ID는 영어 소문자로 시작하고 영어 소문자·숫자·밑줄만 쓸 수 있습니다."));
            if (!ids.Add(id)) throw new InvalidDataException(Error("ID가 중복됩니다."));
            bool enabled = row["활성화"].ToUpperInvariant() switch
            {
                "TRUE" => true,
                "FALSE" or "" => false,
                _ => throw new InvalidDataException(Error("활성화는 TRUE 또는 FALSE여야 합니다.")),
            };
            if (!s_Types.TryGetValue(row["자료형"], out var type))
                throw new InvalidDataException(Error($"자료형은 {string.Join("·", s_Types.Keys)} 중 하나여야 합니다."));

            // 비활성화 행도 형식은 검사한다. 켜는 순간 잘못된 값이 드러나지 않도록 미리 막는다.
            var rule = ParseValue(id, type, row["값"], row["최소값"], row["최대값"], Error);
            if (enabled) rules.Add(rule);
        }
        return new TokenRuleTable(rules, loadedAt);
    }

    private static TokenRule ParseValue(string id, TokenRuleType type, string value, string minText, string maxText, Func<string, string> error)
    {
        if (type == TokenRuleType.Text)
        {
            if (value.Length == 0) throw new InvalidDataException(error("값이 비어 있습니다."));
            if (minText.Length > 0 || maxText.Length > 0) throw new InvalidDataException(error("문자열 규칙에는 최소값·최대값을 쓰지 않습니다."));
            return new(id, type, value, null, null, null);
        }

        decimal? min = ParseBound(minText, "최소값", error);
        decimal? max = ParseBound(maxText, "최대값", error);
        if (min > max) throw new InvalidDataException(error("최소값이 최대값보다 큽니다."));
        void CheckRange(decimal number)
        {
            if (number < min || number > max)
                throw new InvalidDataException(error($"값 {number.ToString(CultureInfo.InvariantCulture)}이(가) 허용 범위({minText}~{maxText})를 벗어났습니다."));
        }

        switch (type)
        {
            case TokenRuleType.Integer:
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    throw new InvalidDataException(error("값이 정수가 아닙니다."));
                CheckRange(integer);
                return new(id, type, value, integer, null, null);
            case TokenRuleType.Decimal:
                if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    throw new InvalidDataException(error("값이 실수가 아닙니다."));
                CheckRange(number);
                return new(id, type, value, null, number, null);
            default:
                var items = value.Split('|', StringSplitOptions.TrimEntries);
                var list = new List<long>(items.Length);
                foreach (var item in items)
                {
                    if (!long.TryParse(item, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var element))
                        throw new InvalidDataException(error("정수목록 값은 |로 구분한 정수여야 합니다."));
                    CheckRange(element);
                    list.Add(element);
                }
                return new(id, type, value, null, null, list.AsReadOnly());
        }
    }

    private static decimal? ParseBound(string text, string name, Func<string, string> error)
    {
        if (text.Length == 0) return null;
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException(error($"{name}이(가) 숫자가 아닙니다."));
    }
}

/// <summary>
/// `증표규칙` 시트의 마지막 정상 스냅샷. 새 데이터가 잘못되면 기존 스냅샷을 유지하고, 한 번도 읽지 못했다면 빈 표(모든 기능이 코드 기본값 사용)다.
/// 증표를 쓰는 기능은 사용 직전 <see cref="EnsureFreshAsync"/>로 갱신한 뒤 <see cref="Current"/>에서 값을 꺼낸다.
/// </summary>
public sealed class TokenRuleCatalog
{
    private readonly ITokenRuleSource source;
    private readonly string cachePath;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TokenRuleTable current = TokenRuleTable.Empty;
    public TokenRuleTable Current => Volatile.Read(ref current);

    public TokenRuleCatalog(ITokenRuleSource source, string dataDirectory, Action<string>? log = null)
    {
        this.source = source;
        this.log = log ?? Console.WriteLine;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.CacheKey)));
        cachePath = Path.Combine(dataDirectory, "token-rules", key + ".csv");
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
                    var csv = await File.ReadAllTextAsync(cachePath, ct);
                    Volatile.Write(ref current, TokenRuleCsvReader.Parse(csv, File.GetLastWriteTimeUtc(cachePath)));
                    log($"[증표규칙] 로컬 캐시 {Current.Rules.Count}개 로딩 완료");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { log($"[증표규칙] 캐시 로딩 실패: {ex.Message}"); }
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
            var snapshot = TokenRuleCsvReader.Parse(csv, DateTimeOffset.UtcNow);
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
            log($"[증표규칙] 시트 갱신 완료: 활성 규칙 {snapshot.Rules.Count}개");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log($"[증표규칙] 갱신 실패, 기존 {Current.Rules.Count}개 유지: {ex.Message}");
            return false;
        }
    }
}
