using System.Text;

namespace Molly.Runes;

public static class RuneSearch
{
    public static IReadOnlyList<RuneData> Find(RuneTable table, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("검색할 룬 이름을 입력해주세요.", nameof(keyword));
        var query = RemoveWhitespace(keyword.Normalize(NormalizationForm.FormC));
        return table.Items.Where(r => RemoveWhitespace(r.Name.Normalize(NormalizationForm.FormC))
            .Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    // 룬 이름 표기가 띄어쓰기까지 정확히 일치해야만 검색되는 것을 막기 위해
    // 비교 전 모든 공백을 제거합니다.
    private static string RemoveWhitespace(string value)
        => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    public static string Format(IReadOnlyList<RuneData> results)
        => $"룬 검색 결과: {results.Count}개\n\n" + string.Join("\n\n", results.Select(r =>
            $"{r.Name} · 시즌 {r.Season} · {r.Grade} · {r.Category}" +
            (string.IsNullOrWhiteSpace(r.Class) ? string.Empty : $" · {r.Class}") +
            $"\n{r.Effect}"));

    public static IReadOnlyList<string> SplitMessages(string text)
    {
        const int limit = 1900;
        var chunks = new List<string>();
        while (text.Length > limit)
        {
            var end = text.LastIndexOf('\n', limit - 1, limit);
            if (end <= 0) end = limit;
            else end++; // 줄바꿈도 보존합니다.
            if (char.IsHighSurrogate(text[end - 1])) end--;
            chunks.Add(text[..end]);
            text = text[end..];
        }
        if (text.Length > 0) chunks.Add(text);
        return chunks;
    }
}
