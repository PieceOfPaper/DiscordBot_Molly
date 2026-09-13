using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace Molly.Runes;

public sealed record RuneKey(int Season, string Grade, string Category, string Name);

public sealed record RuneData(int Season, string Grade, string Category, string Class, string Name, string Effect)
{
    public RuneKey Key => new(Season, Grade, Category, Name);
}

public sealed class RuneTable
{
    public IReadOnlyList<RuneData> Items { get; }
    public IReadOnlyDictionary<RuneKey, RuneData> ByKey { get; }
    public IReadOnlyList<string> Warnings { get; }
    public DateTimeOffset LoadedAt { get; }

    internal RuneTable(IEnumerable<RuneData> items, IEnumerable<string> warnings, DateTimeOffset loadedAt)
    {
        Items = Array.AsReadOnly(items.ToArray());
        ByKey = new ReadOnlyDictionary<RuneKey, RuneData>(Items.ToDictionary(x => x.Key));
        Warnings = Array.AsReadOnly(warnings.ToArray());
        LoadedAt = loadedAt;
    }

    public static RuneTable Empty { get; } = new([], [], DateTimeOffset.MinValue);
}

public static class RuneCsvReader
{
    public static RuneTable Parse(string csv, DateTimeOffset loadedAt)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidDataException("룬 테이블 헤더가 없습니다.");
        headers = headers.Select(x => x.Trim()).ToArray();
        string[] required = ["시즌", "등급", "분류", "클래스", "이름", "효과"];
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length || required.Any(x => !headers.Contains(x)))
            throw new InvalidDataException("룬 테이블에 중복 헤더가 있거나 필수 헤더(시즌·등급·분류·클래스·이름·효과)가 없습니다.");
        var indexes = required.Select(x => Array.IndexOf(headers, x)).ToArray();
        var items = new List<RuneData>();
        var warnings = new List<string>();
        var keys = new HashSet<RuneKey>();
        while (!parser.EndOfData)
        {
            var line = parser.LineNumber;
            var fields = parser.ReadFields()!;
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Length != headers.Length)
                throw new InvalidDataException($"룬 CSV {line}행의 열 수가 헤더와 다릅니다.");
            var values = indexes.Select(i => fields[i].Trim()).ToArray();
            if (!int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var season) || season < 1)
                throw new InvalidDataException($"룬 CSV {line}행의 시즌은 양의 정수여야 합니다.");
            if (values[1] is not ("신화" or "전설") || values[2] is not ("무기" or "방어구" or "앰블럼" or "장신구") || values[4].Length == 0)
                throw new InvalidDataException($"룬 CSV {line}행의 등급·분류·이름을 확인하세요.");
            var item = new RuneData(season, values[1], values[2], values[3], values[4], values[5]);
            if (!keys.Add(item.Key))
                throw new InvalidDataException($"룬 CSV {line}행에 같은 시즌·등급·분류·이름이 중복됩니다.");
            if (item.Effect.Length == 0)
            {
                warnings.Add($"{line}행 '{item.Name}': 효과가 비어 있어 제외했습니다.");
                continue;
            }
            items.Add(item);
        }
        if (items.Count == 0) throw new InvalidDataException("사용 가능한 룬이 없어 기존 데이터를 유지합니다.");
        return new RuneTable(items, warnings, loadedAt);
    }
}
