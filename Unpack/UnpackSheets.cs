using System.Text.Json;

namespace Molly.Unpack;

/// <summary>
/// 게임 데이터 저장소의 시트 파일(<c>Patch/Sheetdata/client/&lt;시트&gt;.json</c>) 읽기.
/// 파일 구조와 시트 사이 참조는 저장소의 <c>Docs/SheetRelations.md</c>를 따릅니다.
/// </summary>
public static class UnpackSheets
{
    // 본 서비스 시트 묶음. 모든 시트가 한 커밋으로 함께 올라가므로 이 폴더의 마지막 커밋이 데이터 버전입니다.
    public const string ClientDirectory = "Patch/Sheetdata/client";

    public static string PathOf(string sheet) => $"{ClientDirectory}/{sheet}.json";

    /// <summary><c>{"sheet": 이름, "rows": [...]}</c> 형식인지 확인하고 문서를 돌려줍니다. 호출자가 Dispose합니다.</summary>
    public static JsonDocument Parse(string sheet, ReadOnlyMemory<byte> json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"{sheet} 시트 JSON 해석 실패: {ex.Message}", ex); }
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sheet", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != sheet ||
            !root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new InvalidDataException($"{sheet} 시트 형식이 아닙니다(sheet·rows 확인).");
        }
        return document;
    }

    public static JsonElement.ArrayEnumerator Rows(JsonDocument sheet) => sheet.RootElement.GetProperty("rows").EnumerateArray();

    /// <summary>
    /// 다른 시트를 가리키는 Id 값. 참조가 없으면 변환기가 <c>0</c>(숫자)이나 <c>"None"</c>으로 남기므로 null로 돌려줍니다.
    /// </summary>
    public static string? Reference(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } id && id != "None"
            ? id
            : null;

    public static string Text(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public static double? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.String when double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public static double? Number(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) ? Number(value) : null;
}

/// <summary>필요한 시트를 받아 문서로 돌려줍니다. 실패하면 <see cref="UnpackDataException"/>을 던집니다.</summary>
public delegate Task<JsonDocument> UnpackSheetFetcher(string sheet, CancellationToken ct);

public sealed class UnpackDataException(UnpackRepoStatus status, string message) : Exception(message)
{
    public UnpackRepoStatus Status { get; } = status;
}
