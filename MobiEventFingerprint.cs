using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Web; // (없으면 아래 HtmlDecode 부분을 WebUtility.HtmlDecode 로 바꿔도 됩니다)

public static class MobiEventFingerprint
{
    private const string BaseListUrl =
        "https://mabinogimobile.nexon.com/News/Events?headlineId=2501&directionType=DEFAULT&pageno=1";

    // 프로젝트 전체에서 재사용되도록 static HttpClient
    private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static MobiEventFingerprint()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9");
    }

    /// <summary>
    /// 이벤트 목록 전체(1..N페이지)를 가벼운 HTTP로 긁어,
    /// 각 카드의 (threadId | 기간문구) 목록을 정규화해 SHA256 해시를 돌려준다.
    /// DOM/JS 실행 없음 → 매우 저렴.
    /// </summary>
    public static async Task<string?> ComputeAsync(CancellationToken ct = default, Action<string>? log = null)
    {
        string Log(string s) { (log ?? Console.WriteLine).Invoke($"[MobiEventFingerprint] {s}"); return s; }

        // 1) 1페이지 GET
        var page1 = await GetStringAsync(BaseListUrl, ct);
        if (page1 == null) return null;

        // 2) 1페이지에서 pagination 메타 추출
        var (totalCount, blockStartNo, blockStartKey) = ParsePaginationMeta(page1);
        // 1페이지에 표시되는 카드 수
        var page1Items = ParseThreadItems(page1);
        int perPage = Math.Max(1, page1Items.Count);
        int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / perPage));

        Log($"meta: totalCount={totalCount}, perPage={perPage}, totalPages={totalPages}");

        // 3) 모든 페이지 순회 수집
        var all = new List<(string id, string range)>();
        all.AddRange(page1Items);

        for (int p = 2; p <= totalPages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var url = BuildListUrl(p, blockStartNo, blockStartKey);
            var html = await GetStringAsync(url, ct);
            if (html == null) continue;

            // 각 페이지마다 pagination data가 갱신될 수 있으니 최신 값으로 갱신
            var (_, bNo, bKey) = ParsePaginationMeta(html);
            if (!string.IsNullOrEmpty(bNo)) blockStartNo = bNo;
            if (!string.IsNullOrEmpty(bKey)) blockStartKey = bKey;

            all.AddRange(ParseThreadItems(html));
        }

        // 4) (id|range) 정규화 → 정렬 → 해시
        var normalized = all
            .Select(t => $"{t.id}|{NormalizeRange(t.range)}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var joined = string.Join("\n", normalized);
        var hash = Sha256Hex(joined);

        Log($"fingerprint={hash} (items={normalized.Length})");
        return hash;
    }

    /// <summary>이전 해시와 비교해 변경 여부만 반환.</summary>
    public static async Task<bool?> IsChangedAsync(string? previousHash, CancellationToken ct = default, Action<string>? log = null)
    {
        var current = await ComputeAsync(ct, log);
        if (current == null) return null;              // 네트워크/파싱 실패
        if (string.IsNullOrEmpty(previousHash)) return true;
        return !StringComparer.Ordinal.Equals(previousHash, current);
    }

    // --------------------- 내부 유틸 ---------------------

    private static async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    private static (int totalCount, string blockStartNo, string blockStartKey) ParsePaginationMeta(string html)
    {
        // <div class="pagination" data-pagingtype="thread" data-blockstartno="1" data-blockstartkey="253402300799,9223..." data-totalcount="17">
        var div = Regex.Match(html,
            @"<div\s+class=""pagination""[^>]*data-pagingtype=""thread""[^>]*>",
            RegexOptions.IgnoreCase);

        if (!div.Success) return (0, "", "");

        string blockStartNo = GetAttr(div.Value, "data-blockstartno");
        string blockStartKey = GetAttr(div.Value, "data-blockstartkey");
        int totalCount = int.TryParse(GetAttr(div.Value, "data-totalcount"), out var t) ? t : 0;

        return (totalCount, blockStartNo, blockStartKey);

        static string GetAttr(string tag, string name)
            => Regex.Match(tag, name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value;
    }

    private static string BuildListUrl(int pageNo, string blockStartNo, string blockStartKey)
    {
        var ub = new UriBuilder("https://mabinogimobile.nexon.com/News/Events");
        var q = new StringBuilder();
        q.Append("headlineId=2501&directionType=DEFAULT");
        q.Append("&pageno=").Append(pageNo);
        if (!string.IsNullOrEmpty(blockStartNo))
            q.Append("&blockStartNo=").Append(Uri.EscapeDataString(blockStartNo));
        if (!string.IsNullOrEmpty(blockStartKey))
            q.Append("&blockStartKey=").Append(Uri.EscapeDataString(blockStartKey));
        ub.Query = q.ToString();
        return ub.Uri.ToString();
    }

    private static readonly Regex LiRegex = new(
        @"<li[^>]*data-threadid=""(?<id>\d+)""[^>]*>(?<inner>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static List<(string id, string range)> ParseThreadItems(string html)
    {
        var list = new List<(string id, string range)>();

        foreach (Match m in LiRegex.Matches(html))
        {
            var id = m.Groups["id"].Value;
            var inner = m.Groups["inner"].Value;

            var text = StripTags(inner);
            var range = ExtractRangeLine(text);

            list.Add((id, range));
        }
        return list;
    }

    // 태그 제거 + 엔티티 디코드 + 공백 정규화
    private static string StripTags(string s)
    {
        s = Regex.Replace(s, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    // 기간 라인 후보: '~' / '까지' / '별도 안내 시까지' / '상시'
    private static string ExtractRangeLine(string text)
    {
        var lines = Regex.Split(text, @"\r?\n|(?<=\))\s+|  "); // 줄/큰 덩어리 단위로
        foreach (var raw in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (raw.Contains("~") || raw.Contains("까지") || raw.Contains("별도 안내 시까지") || raw.Contains("상시"))
                return raw;
        }
        return "";
    }

    private static string NormalizeRange(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace(" ", "");
        s = s.Replace("오전", "AM").Replace("오후", "PM"); // 언어 흔들려도 동일한 결과
        s = s.Replace("점검후", "점검후"); // unify
        return s;
    }

    private static string Sha256Hex(string s)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
