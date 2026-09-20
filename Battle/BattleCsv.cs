using Microsoft.VisualBasic.FileIO;
using System.Text;

namespace Molly.Battle;

internal static class BattleCsv
{
    public static IReadOnlyList<Dictionary<string, string>> Read(string csv, string sheetName)
    {
        if (string.IsNullOrWhiteSpace(csv) || csv.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{sheetName} 시트가 CSV 데이터를 반환하지 않았습니다.");
        using var reader = new TextFieldParser(new StringReader(csv)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        reader.SetDelimiters(",");
        var headers = reader.ReadFields()?.Select(x => x.Trim().TrimStart('\uFEFF')).ToArray()
            ?? throw new InvalidDataException($"{sheetName} 시트의 헤더가 없습니다.");
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
            throw new InvalidDataException($"{sheetName} 시트의 헤더가 비어 있거나 중복되었습니다.");
        var rows = new List<Dictionary<string, string>>();
        while (!reader.EndOfData)
        {
            var fields = reader.ReadFields();
            if (fields is null || fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Length != headers.Length) throw new InvalidDataException($"{sheetName} 시트의 행 열 수가 헤더와 다릅니다.");
            rows.Add(headers.Select((h, i) => (h, Value: fields[i].Trim())).ToDictionary(x => x.h, x => x.Value, StringComparer.Ordinal));
        }
        return rows;
    }

    public static string Required(this Dictionary<string, string> row, string header, string sheet, int rowNumber)
    {
        if (!row.TryGetValue(header, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{sheet} 시트 {rowNumber}행의 필수 값 '{header}'이(가) 비어 있습니다.");
        return value;
    }

    public static void Headers(IReadOnlyList<Dictionary<string, string>> rows, string sheet, params string[] required)
    {
        IEnumerable<string> actual = rows.Count == 0 ? Array.Empty<string>() : rows[0].Keys;
        var missing = required.Where(x => !actual.Contains(x, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"{sheet} 시트에 필수 헤더가 없습니다: {string.Join(", ", missing)}");
    }

    public static bool Bool(string value, string sheet, int row, string field) => value.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" or "예" => true,
        "FALSE" or "0" or "아니오" => false,
        _ => throw new InvalidDataException($"{sheet} 시트 {row}행의 {field} 값 '{value}'은 TRUE 또는 FALSE여야 합니다.")
    };

    public static int Int(string value, string sheet, int row, string field, int minimum = 0)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum) throw new InvalidDataException($"{sheet} 시트 {row}행의 {field} 값이 허용 범위를 벗어났습니다.");
        return parsed;
    }

    public static double Double(string value, string sheet, int row, string field, double minimum = 0, double maximum = double.MaxValue)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
            throw new InvalidDataException($"{sheet} 시트 {row}행의 {field} 값이 허용 범위를 벗어났습니다.");
        return parsed;
    }
}
