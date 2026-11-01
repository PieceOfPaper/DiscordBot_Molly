namespace Molly.Runes;

public static class RuneSearch
{
    /// <summary>
    /// 지정 시즌 AND 지정 부위(null이면 전체) AND 검색식에 맞는 룬. 검색어마다 이름 또는 검색용 효과 문장(<see cref="RuneData.SearchEffect"/>)의
    /// 한 줄에 부분 일치하면 맞은 것으로 봅니다. 검색어 하나라도 이름에 든 룬을 앞에 두고, 각 묶음 안은 표의 순서를 유지합니다.
    /// </summary>
    public static IReadOnlyList<RuneData> Find(RuneTable table, RuneQuery query, int season, RuneCategory? category = null)
    {
        var matches = new List<(RuneData Rune, bool NameHit)>();
        foreach (var rune in table.Items)
        {
            if (rune.Season != season || (category is { } c && rune.Category != c)) continue;
            var name = RuneQuery.Normalize(rune.Name);
            var segments = rune.SearchEffect.Split(RuneEffectText.SearchBoundary).Select(RuneQuery.Normalize).ToArray();
            if (query.Matches(name, segments)) matches.Add((rune, query.AnyTermIn(name)));
        }
        // OrderBy는 안정 정렬이라 같은 묶음 안의 기존 순서가 유지됩니다.
        return matches.OrderBy(x => x.NameHit ? 0 : 1).Select(x => x.Rune).ToArray();
    }

    /// <summary>검색 결과에 함께 보여주는 적용 조건. 예: '시즌 2 · 전체 부위'</summary>
    public static string Conditions(int season, RuneCategory? category) =>
        $"시즌 {season} · {(category is { } c ? c.Display() : "전체 부위")}";

    public static string Format(IReadOnlyList<RuneData> results, string header = "")
        => (header.Length > 0 ? header + "\n" : "") + $"룬 검색 결과: {results.Count}개\n\n" + string.Join("\n\n", results.Select(r =>
            $"{r.Name} · 시즌 {r.Season} · {r.Grade.Display()} · {r.Category.Display()}" +
            (string.IsNullOrWhiteSpace(r.Class) ? string.Empty : $" · {r.Class}") +
            (r.TranscendLevels > 0 ? $" · 수치 {RuneNames.TranscendLegend(r.TranscendLevels)}" : string.Empty) +
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
