using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Molly.Crafting;

// 제작 시트는 여러 기능이 함께 쓰는 공용 레시피 데이터입니다. 기능별 조건(예: 해연 아이템만)은 사용하는 쪽에서 거릅니다.
public sealed record CraftingIngredient(string Name, int Quantity);

// Category는 시트의 분류(예: 무기·방어구·장신구)이며, 분류 열이 없거나 비어 있으면 빈 문자열입니다.
public sealed record CraftingRecipe(string Name, IReadOnlyList<CraftingIngredient> Ingredients, string Category = "");

public interface ICraftingSource
{
    string CacheKey { get; }
    Task<string> FetchCsvAsync(CancellationToken ct);
}

public sealed class GoogleSheetsCraftingSource : ICraftingSource
{
    public const string DefaultSheetName = "제작";
    private readonly HttpClient client;
    public Uri CsvUri { get; }
    public string CacheKey => CsvUri.AbsoluteUri;

    public GoogleSheetsCraftingSource(HttpClient client, string spreadsheetId, string sheetName = DefaultSheetName)
    {
        if (string.IsNullOrWhiteSpace(spreadsheetId) || spreadsheetId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Google Sheets 문서 ID를 확인하세요.", nameof(spreadsheetId));
        if (string.IsNullOrWhiteSpace(sheetName)) throw new ArgumentException("제작 시트 이름을 확인하세요.", nameof(sheetName));
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

public sealed class CraftingTable
{
    public static CraftingTable Empty { get; } = new([], DateTimeOffset.MinValue);
    public IReadOnlyList<CraftingRecipe> Items { get; }
    public IReadOnlyDictionary<string, CraftingRecipe> ByName { get; }
    public DateTimeOffset LoadedAt { get; }

    internal CraftingTable(IEnumerable<CraftingRecipe> items, DateTimeOffset loadedAt)
    {
        Items = Array.AsReadOnly(items.ToArray());
        ByName = Items.ToDictionary(x => x.Name, StringComparer.Ordinal);
        LoadedAt = loadedAt;
    }
}

public static class CraftingCsvReader
{
    public const string NameHeader = "이름";
    public const string CategoryHeader = "분류";
    public const string IngredientHeaderPrefix = "재료";

    // 헤더: 이름, (선택) 분류, 재료1..재료N. 재료 셀은 "재료명/수량"이며 빈 셀은 건너뜁니다.
    public static CraftingTable Parse(string csv, DateTimeOffset loadedAt)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('﻿')))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        var headers = (parser.ReadFields() ?? throw new InvalidDataException("제작 테이블 헤더가 없습니다."))
            .Select(x => x.Trim()).ToArray();
        var nameIndex = Array.IndexOf(headers, NameHeader);
        // 분류 열이 추가되기 전의 로컬 캐시도 읽을 수 있도록 분류는 선택 헤더입니다.
        var categoryIndex = Array.IndexOf(headers, CategoryHeader);
        var ingredientIndexes = headers.Select((header, index) => (header, index))
            .Where(x => x.header.StartsWith(IngredientHeaderPrefix, StringComparison.Ordinal)).Select(x => x.index).ToArray();
        if (nameIndex < 0 || ingredientIndexes.Length == 0 || headers.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Count() != headers.Count(x => x.Length > 0))
            throw new InvalidDataException("제작 테이블에 중복 헤더가 있거나 필수 헤더(이름·재료N)가 없습니다.");

        var recipes = new List<CraftingRecipe>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.EndOfData)
        {
            var line = parser.LineNumber;
            var fields = parser.ReadFields()!;
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            string Field(int index) => index < fields.Length ? fields[index].Trim() : "";
            var name = Field(nameIndex);
            if (name.Length == 0 || !names.Add(name))
                throw new InvalidDataException($"제작 CSV {line}행의 이름이 비어 있거나 중복됩니다.");

            var ingredients = new List<CraftingIngredient>();
            foreach (var index in ingredientIndexes)
            {
                var cell = Field(index);
                if (cell.Length == 0) continue;
                var slash = cell.LastIndexOf('/');
                if (slash <= 0 || !int.TryParse(cell[(slash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                    throw new InvalidDataException($"제작 CSV {line}행 '{name}'의 재료 '{cell}'는 '재료명/수량(양의 정수)' 형식이어야 합니다.");
                var ingredientName = cell[..slash].Trim();
                if (ingredients.Any(x => x.Name == ingredientName))
                    throw new InvalidDataException($"제작 CSV {line}행 '{name}'에 재료 '{ingredientName}'가 중복됩니다.");
                ingredients.Add(new CraftingIngredient(ingredientName, quantity));
            }
            if (ingredients.Count == 0)
                throw new InvalidDataException($"제작 CSV {line}행 '{name}'에 재료가 없습니다.");
            recipes.Add(new CraftingRecipe(name, ingredients.AsReadOnly(), categoryIndex < 0 ? "" : Field(categoryIndex)));
        }
        if (recipes.Count == 0) throw new InvalidDataException("사용 가능한 제작 레시피가 없어 기존 데이터를 유지합니다.");
        return new CraftingTable(recipes, loadedAt);
    }
}

/// <summary>
/// 제작 시트 스냅샷. 새 데이터는 검증을 통과해야 교체하고, 실패하면 마지막 정상본(메모리·로컬 캐시)을 유지합니다.
/// </summary>
public sealed class CraftingCatalog
{
    private readonly ICraftingSource source;
    private readonly string cachePath;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CraftingTable current = CraftingTable.Empty;
    public CraftingTable Current => Volatile.Read(ref current);

    public CraftingCatalog(ICraftingSource source, string dataDirectory, Action<string>? log = null)
    {
        this.source = source;
        this.log = log ?? Console.WriteLine;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.CacheKey)));
        cachePath = Path.Combine(dataDirectory, "crafting", key + ".csv");
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
                    Volatile.Write(ref current, CraftingCsvReader.Parse(csv, File.GetLastWriteTimeUtc(cachePath)));
                    log($"[제작] 로컬 캐시 {Current.Items.Count}개 로딩 완료");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { log($"[제작] 캐시 로딩 실패: {ex.Message}"); }
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
            var snapshot = CraftingCsvReader.Parse(csv, DateTimeOffset.UtcNow);
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
            log($"[제작] 시트 갱신 완료: {snapshot.Items.Count}개");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log($"[제작] 갱신 실패, 기존 {Current.Items.Count}개 유지: {ex.Message}");
            return false;
        }
    }
}
