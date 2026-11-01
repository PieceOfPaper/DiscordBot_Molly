using System.Security.Cryptography;
using System.Text.Json;
using Molly.Unpack;

namespace Molly.SkillQuiz;

/// <summary>스킬 퀴즈에 필요한 아이콘만 blob SHA 이름으로 영구 보관합니다. 128px를 우선하며, 없으면 asset 정의의 가장 큰 PNG를 씁니다.</summary>
public sealed class SkillIconCache : IUnpackDataCatalog
{
    public const string AssetRoot = "Patch/AssetPackages/Assets/PatchableAssets/Icons/CommonIcon/Skill_Icon_Battle";
    private readonly UnpackRepoClient client; private readonly UnpackDataCatalog<SkillQuizTable> data; private readonly string dir; private readonly Action<string> log;
    private IReadOnlyDictionary<string, string> files = new Dictionary<string, string>(); private string? synced;
    public SkillIconCache(UnpackRepoClient client, UnpackDataCatalog<SkillQuizTable> data, string root, Action<string>? log = null)
    { this.client = client; this.data = data; dir = Path.Combine(root, "unpack", "skill-icons"); this.log = log ?? (x => Console.WriteLine("[게임데이터:스킬 아이콘] " + x)); }
    public string Name => "스킬 아이콘"; public int Count => files.Count;
    public string? GetPath(string icon) => files.TryGetValue(icon, out var sha) ? Path.Combine(dir, sha + ".png") : null;
    public Task InitializeAsync(CancellationToken ct = default) { return LoadAsync(ct); }
    private async Task LoadAsync(CancellationToken ct) { var manifest = Path.Combine(dir, "manifest.json"); if (!File.Exists(manifest)) return; try { using var d = JsonDocument.Parse(await File.ReadAllBytesAsync(manifest, ct)); var next = new Dictionary<string,string>(); foreach (var p in d.RootElement.GetProperty("images").EnumerateObject()) if (p.Value.GetString() is { Length: 64 } sha && File.Exists(Path.Combine(dir, sha + ".png"))) next[p.Name] = sha; files = next; } catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { log("캐시 목록 로딩 실패: " + ex.Message); } }
    public async Task<UnpackRefreshResult> RefreshAsync(string commit, CancellationToken ct)
    {
        if (data.Current is not { } snapshot || synced == snapshot.Commit) return new(UnpackRepoStatus.Success);
        var previous = files; var next = new Dictionary<string,string>(); int downloaded = 0;
        foreach (var icon in snapshot.Data.Items.Select(x => x.IconPath).Distinct(StringComparer.Ordinal))
        {
            var bytes = await ReadIconAsync(icon, snapshot.Commit, ct);
            if (bytes is null) { if (previous.TryGetValue(icon, out var old)) next[icon] = old; continue; }
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); var path = Path.Combine(dir, sha + ".png");
            if (!File.Exists(path)) { Directory.CreateDirectory(dir); await File.WriteAllBytesAsync(path, bytes, ct); downloaded++; }
            next[icon] = sha;
        }
        try { await SaveAsync(snapshot.Commit, next, ct); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Fail(UnpackRepoStatus.Unavailable, ex.Message); }
        files = next; synced = snapshot.Commit; log($"확인 완료: {next.Count}개(새로 받음 {downloaded}) (커밋 {snapshot.Commit[..7]})"); return new(UnpackRepoStatus.Success, downloaded > 0 || next.Count != previous.Count);
    }
    private async Task<byte[]?> ReadIconAsync(string icon, string commit, CancellationToken ct)
    {
        // Skill.Icon이 .asset이면 CommonIconAsset JSON 안의 PNG 리소스를 찾는다. ID/파일명 조합을 하지 않는다.
        var path = icon.StartsWith("Patch/", StringComparison.Ordinal) ? icon : AssetRoot + "/" + icon.TrimStart('/');
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return await FetchPng(path, commit, ct);
        var asset = await client.GetFileAsync(path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json", commit, ct);
        if (!asset.IsSuccess) return null;
        try { using var doc = JsonDocument.Parse(asset.Value!); var resources = FindPng(doc.RootElement).Distinct(StringComparer.Ordinal).ToArray();
            // JSON의 128 크기 경로를 우선하고, 없으면 정의 순서의 마지막(보통 최대) PNG를 대체로 쓴다.
            var preferred = resources.FirstOrDefault(x => x.Contains("128", StringComparison.Ordinal));
            return await FetchPng(preferred ?? resources.LastOrDefault() ?? "", commit, ct); }
        catch (JsonException) { return null; }
    }
    private static IEnumerable<string> FindPng(JsonElement e) { if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s && s.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) yield return s; else if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) foreach (var x in FindPng(p.Value)) yield return x; else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) foreach (var y in FindPng(x)) yield return y; }
    private async Task<byte[]?> FetchPng(string path, string commit, CancellationToken ct) { if (path.Length == 0) return null; var p = path.StartsWith("Patch/", StringComparison.Ordinal) ? path : AssetRoot + "/" + path.TrimStart('/'); var r = await client.GetFileAsync(p, commit, ct); return r.IsSuccess && r.Value!.Length > 8 && r.Value.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10}) ? r.Value! : null; }
    private async Task SaveAsync(string commit, IReadOnlyDictionary<string,string> map, CancellationToken ct) { Directory.CreateDirectory(dir); using var s = new MemoryStream(); using (var w = new Utf8JsonWriter(s)) { w.WriteStartObject(); w.WriteString("commit", commit); w.WriteStartObject("images"); foreach (var x in map) w.WriteString(x.Key,x.Value); w.WriteEndObject(); w.WriteEndObject(); } await File.WriteAllBytesAsync(Path.Combine(dir,"manifest.json"),s.ToArray(),ct); }
    void IUnpackDataCatalog.LogStopped(string reason) => log(reason + (Count == 0 ? " 받아 둔 이미지가 없어 스킬 아이콘 문제는 낼 수 없습니다." : " 받아 둔 이미지를 계속 사용합니다."));
    UnpackRefreshResult IUnpackDataCatalog.Fail(UnpackRepoStatus status, string detail, TimeSpan? retryAfter) => Fail(status, detail, retryAfter);
    private UnpackRefreshResult Fail(UnpackRepoStatus s,string d,TimeSpan? r=null) { log($"{d} — 받아 둔 이미지 {Count}개 유지"); return new(s, Detail:d, RetryAfter:r); }
}
