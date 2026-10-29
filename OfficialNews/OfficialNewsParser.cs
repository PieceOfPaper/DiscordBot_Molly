using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Molly.OfficialNews;

/// <summary>목록의 글 하나. Category는 말머리 원문(이벤트 게시판은 진행중·종료 같은 상태), ThumbnailUrl은 목록·상단 배너에 보이는 이미지다.</summary>
public sealed record NewsListCard(long PostId, string Title, string? Category, string? ThumbnailUrl);

public sealed record NewsListPage(int TotalCount, string BlockStartNo, string BlockStartKey, bool HasPagination, IReadOnlyList<NewsListCard> Cards);

/// <summary>
/// 공식 소식 목록 HTML(서버 렌더링 결과)을 해석한다. 공지·업데이트·이벤트·에린 노트 목록은 같은 구조라 한 파서를 쓴다.
/// 네트워크와 무관한 순수 함수라 오프라인 테스트로 고정한다. 차단·글로벌 페이지처럼 목록이 없으면 HasPagination=false·Cards 0개다.
/// </summary>
public static partial class OfficialNewsParser
{
    public static NewsListPage ParseListPage(string html)
    {
        var pagination = PaginationRegex().Match(html);
        var totalCount = 0;
        string blockStartNo = "", blockStartKey = "";
        if (pagination.Success)
        {
            totalCount = int.TryParse(GetAttr(pagination.Value, "data-totalcount"), out var total) ? total : 0;
            blockStartNo = GetAttr(pagination.Value, "data-blockstartno");
            blockStartKey = GetAttr(pagination.Value, "data-blockstartkey");
        }

        // 상단 고정 배너는 목록과 별도로 data-threadid 없이 내려온다. 같은 글이 목록에 있을 때 이미지만 보충한다.
        var bannerImages = new Dictionary<long, string>();
        foreach (Match m in BannerRegex().Matches(html))
        {
            var inner = m.Groups["inner"].Value;
            var link = BannerLinkRegex().Match(inner);
            if (link.Success && long.TryParse(link.Groups["id"].Value, out var id) && FindThumbnail(inner) is { } image)
                bannerImages.TryAdd(id, image);
        }

        // 같은 글이 PC·모바일 카드로 두 번 올 수 있다. 처음 순서를 지키고 빠진 분류·이미지만 채운다.
        var cards = new Dictionary<long, NewsListCard>();
        foreach (Match m in CardRegex().Matches(html))
        {
            if (!long.TryParse(m.Groups["id"].Value, out var id)) continue;
            var inner = m.Groups["inner"].Value;
            var title = TextOf(TitleRegex().Match(inner));
            if (title is null) continue;
            var category = TextOf(CategoryRegex().Match(inner));
            var thumbnail = FindThumbnail(inner);
            cards[id] = cards.TryGetValue(id, out var previous)
                ? previous with { Category = previous.Category ?? category, ThumbnailUrl = previous.ThumbnailUrl ?? thumbnail }
                : new NewsListCard(id, title, category, thumbnail);
        }

        var list = cards.Values
            .Select(c => c.ThumbnailUrl is null && bannerImages.TryGetValue(c.PostId, out var image) ? c with { ThumbnailUrl = image } : c)
            .ToList();
        return new NewsListPage(totalCount, blockStartNo, blockStartKey, pagination.Success, list);
    }

    // 사이트의 페이지 링크(?pageno=N&blockStartNo=..&blockStartKey=..)와 같은 형식으로 목록 URL을 만든다.
    public static string BuildListUrl(NewsBoard board, int pageNo, string blockStartNo, string blockStartKey)
    {
        if (pageNo <= 1) return board.ListUrl;
        var q = new StringBuilder("pageno=").Append(pageNo);
        if (!string.IsNullOrEmpty(blockStartNo)) q.Append("&blockStartNo=").Append(Uri.EscapeDataString(blockStartNo));
        if (!string.IsNullOrEmpty(blockStartKey)) q.Append("&blockStartKey=").Append(Uri.EscapeDataString(blockStartKey));
        return $"{board.ListUrl}?{q}";
    }

    // 목록 카드의 썸네일 영역만 본다. 작성자 프로필 이미지는 썸네일이 아니다.
    private static string? FindThumbnail(string inner)
    {
        var thumbnail = ThumbnailRegex().Match(inner);
        if (!thumbnail.Success) return null;
        var style = BackgroundImageRegex().Match(WebUtility.HtmlDecode(GetAttr(thumbnail.Groups["tag"].Value, "style")));
        if (style.Success && Absolute(style.Groups["url"].Value) is { } fromStyle) return fromStyle;
        foreach (Match img in ImgRegex().Matches(thumbnail.Groups["inner"].Value))
        {
            var tag = img.Value;
            if (Absolute(GetAttr(tag, "data-src")) is { } dataSrc) return dataSrc;
            if (Absolute(GetAttr(tag, "src")) is { } src) return src;
        }
        return null;
    }

    private static string? Absolute(string value)
    {
        value = WebUtility.HtmlDecode(value).Trim();
        if (value.Length == 0 || value.Any(char.IsControl)) return null;
        return Uri.TryCreate(new Uri(NewsBoards.Origin), value, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri
            : null;
    }

    private static string? TextOf(Match match)
    {
        if (!match.Success) return null;
        var text = WhitespaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(match.Groups["t"].Value, " ")), " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string GetAttr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{Regex.Escape(name)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value : "";
    }

    [GeneratedRegex(@"<div\b[^>]*\bdata-pagingtype=""thread""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex PaginationRegex();

    [GeneratedRegex(@"<li\b[^>]*\bdata-threadid=""(?<id>[0-9]+)""[^>]*>(?<inner>.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CardRegex();

    [GeneratedRegex(@"<li\b[^>]*\bclass=""[^""]*\bbanner_item\b[^""]*""[^>]*>(?<inner>.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BannerRegex();

    [GeneratedRegex(@"Thread\.link\(\s*(?<id>[0-9]+)|/News/(?:Notice|Update|Events|Devnote)/(?<id>[0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BannerLinkRegex();

    [GeneratedRegex(@"<a\b[^>]*\bclass=""[^""]*\btitle\b[^""]*""[^>]*>(?<t>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<div\b[^>]*\bclass=""[^""]*\btype\b[^""]*""[^>]*>(?<t>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CategoryRegex();

    [GeneratedRegex(@"<div\b(?<tag>[^>]*\bclass=""[^""]*\bthumbnail\b[^""]*""[^>]*)>(?<inner>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ThumbnailRegex();

    [GeneratedRegex(@"background-image\s*:\s*url\(\s*(?:'(?<url>[^']*)'|""(?<url>[^""]*)""|(?<url>[^)]*))\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundImageRegex();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
