using System.Net;
using System.Text;
using System.Text.RegularExpressions;

public record MobiEventResult(
    string eventName, string url, string thumbnailUrl, DateTime start, DateTime end, bool isPerma
);

// 이벤트 목록 한 페이지의 카드. range는 사이트에 표시된 기간 문구 원문입니다.
public sealed record MobiEventCard(string ThreadId, string Title, string Url, string Range, string ThumbnailUrl);

public sealed record MobiEventListPage(
    int TotalCount, string BlockStartNo, string BlockStartKey, bool HasPagination, IReadOnlyList<MobiEventCard> Cards);

/// <summary>
/// 이벤트 목록 HTML(서버 렌더링 결과)을 해석합니다. 네트워크·브라우저와 무관한 순수 함수라
/// HTTP 수집과 Playwright 대체 수집이 같은 파서를 공유하고, 오프라인 테스트로 고정할 수 있습니다.
/// </summary>
public static class MobiEventParser
{
    public const string SiteOrigin = "https://mabinogimobile.nexon.com";
    // 진행중 이벤트 목록(headlineId=2501)
    public const string FirstPageUrl = SiteOrigin + "/News/Events?headlineId=2501&directionType=DEFAULT&pageno=1";

    private static readonly Regex s_PaginationRegex = new(
        @"<div\b[^>]*\bdata-pagingtype=""thread""[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_CardRegex = new(
        @"<li\b[^>]*\bdata-threadid=""(?<id>\d+)""[^>]*>(?<inner>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex s_TitleRegex = new(
        @"<a\b[^>]*\bclass=""[^""]*\btitle\b[^""]*""[^>]*>(?<t>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex s_DateRegex = new(
        @"<div\b[^>]*\bclass=""[^""]*\bdate\b[^""]*""[^>]*>(?<d>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex s_ImgSrcRegex = new(
        @"<img\b[^>]*\b(?:data-src|src)=""(?<src>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static MobiEventListPage ParseListPage(string html)
    {
        var pagination = s_PaginationRegex.Match(html);
        var totalCount = 0;
        string blockStartNo = "", blockStartKey = "";
        if (pagination.Success)
        {
            totalCount = int.TryParse(GetAttr(pagination.Value, "data-totalcount"), out var total) ? total : 0;
            blockStartNo = GetAttr(pagination.Value, "data-blockstartno");
            blockStartKey = GetAttr(pagination.Value, "data-blockstartkey");
        }

        var cards = new List<MobiEventCard>();
        foreach (Match m in s_CardRegex.Matches(html))
        {
            var id = m.Groups["id"].Value;
            var inner = m.Groups["inner"].Value;

            var titleMatch = s_TitleRegex.Match(inner);
            var title = titleMatch.Success ? StripTags(titleMatch.Groups["t"].Value) : "";
            if (title.Length == 0) continue;

            var dateMatch = s_DateRegex.Match(inner);
            var range = dateMatch.Success ? StripTags(dateMatch.Groups["d"].Value) : FindRangeText(StripTags(inner));

            var thumbnail = "";
            var img = s_ImgSrcRegex.Match(inner);
            if (img.Success)
            {
                var src = WebUtility.HtmlDecode(img.Groups["src"].Value);
                thumbnail = Uri.TryCreate(new Uri(SiteOrigin), src, out var abs) ? abs.AbsoluteUri : src;
            }

            cards.Add(new MobiEventCard(id, title, $"{SiteOrigin}/News/Events/{id}", range, thumbnail));
        }

        return new MobiEventListPage(totalCount, blockStartNo, blockStartKey, pagination.Success, cards);
    }

    // 사이트의 페이지 링크(?pageno=N&blockStartNo=..&blockStartKey=..)와 같은 형식으로 목록 URL을 만듭니다.
    public static string BuildListUrl(int pageNo, string blockStartNo, string blockStartKey)
    {
        var q = new StringBuilder("headlineId=2501&directionType=DEFAULT&pageno=").Append(pageNo);
        if (pageNo > 1)
        {
            if (!string.IsNullOrEmpty(blockStartNo))
                q.Append("&blockStartNo=").Append(Uri.EscapeDataString(blockStartNo));
            if (!string.IsNullOrEmpty(blockStartKey))
                q.Append("&blockStartKey=").Append(Uri.EscapeDataString(blockStartKey));
        }
        return $"{SiteOrigin}/News/Events?{q}";
    }

    public static bool TryParseRange(string range, out DateTime startDateTime, out DateTime endDateTime, out bool isPerma)
    {
        isPerma = false;
        startDateTime = DateTime.MinValue;
        endDateTime = DateTime.MaxValue;

        if (string.IsNullOrWhiteSpace(range))
            return false;

        // 상시/무기한 (공백 변형 대응)
        var normalizedRange = Regex.Replace(range, @"\s+", "");
        if (normalizedRange.Contains("별도안내시까지") || normalizedRange.Contains("상시"))
        {
            var startSide = range.Split('~').FirstOrDefault() ?? "";
            if (!TryParseKoreanDateTime(startSide, defaultHour: 0, defaultMinute: 0, out startDateTime))
                startDateTime = DateTime.MinValue;
            isPerma = true;
            endDateTime = DateTime.MaxValue;
            return true;
        }

        // 일반: "시작 ~ 종료"
        var parts = range.Split('~');
        if (parts.Length < 2)
            return false;

        var startSideStr = parts[0].Trim();
        var endSideStr = parts[1].Trim().Replace("까지", "").Trim();

        // 시작: 시간 없으면 00:00, '점검 후'면 06:00 가정
        if (!TryParseKoreanDateTime(startSideStr,
                defaultHour: startSideStr.Contains("점검 후") ? 6 : 0,
                defaultMinute: 0,
                out startDateTime))
            return false;

        // 종료: 시간 없으면 23:59
        return TryParseKoreanDateTime(endSideStr, defaultHour: 23, defaultMinute: 59, out endDateTime);
    }

    public static bool TryParseKoreanDateTime(string src, int defaultHour, int defaultMinute, out DateTime dateTime)
    {
        // 예: 2025.09.11(목) 오전 6시  /  2025.09.24(수) 오후 11시 59분  /  2025.9.24(수)
        dateTime = DateTime.MinValue;

        var d = Regex.Match(src, @"(?<y>\d{4})\.(?<m>\d{1,2})\.(?<d>\d{1,2})");
        if (!d.Success) return false;

        int y = int.Parse(d.Groups["y"].Value);
        int mo = int.Parse(d.Groups["m"].Value);
        int da = int.Parse(d.Groups["d"].Value);

        int h = defaultHour;
        int min = defaultMinute;

        var t = Regex.Match(src, @"(?<ampm>오전|오후)?\s*(?<h>\d{1,2})\s*시(?:\s*(?<min>\d{1,2})\s*분)?");
        if (t.Success)
        {
            h = int.Parse(t.Groups["h"].Value);
            if (t.Groups["min"].Success) min = int.Parse(t.Groups["min"].Value);
            var ampm = t.Groups["ampm"].Success ? t.Groups["ampm"].Value : null;

            if (ampm == "오전" && h == 12) h = 0; // 오전 12 = 00
            else if (ampm == "오후" && h != 12) h += 12; // 오후 1~11
        }

        if (mo is < 1 or > 12 || da < 1 || da > DateTime.DaysInMonth(y, mo) || h is < 0 or > 23 || min is < 0 or > 59)
            return false;

        dateTime = new DateTime(y, mo, da, h, min, 0, DateTimeKind.Unspecified);
        return true;
    }

    private static string GetAttr(string tag, string name)
        => Regex.Match(tag, name + @"=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;

    // 태그 제거 + 엔티티 디코드 + 공백 정규화
    private static string StripTags(string s)
    {
        s = Regex.Replace(s, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // .date 영역이 없는 레이아웃 대비: 기간 문구로 보이는 첫 구간을 찾습니다.
    private static string FindRangeText(string text)
    {
        var m = Regex.Match(text, @"\d{4}\.\d{1,2}\.\d{1,2}.*?(?:까지|상시|$)");
        return m.Success ? m.Value.Trim() : "";
    }
}
