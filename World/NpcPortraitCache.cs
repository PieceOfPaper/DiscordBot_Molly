using System.Text.Json;
using Molly.Unpack;

namespace Molly.World;

/// <summary>
/// NPC·지역 데이터의 NPC 프로필 이미지를 게임 데이터 저장소에서 받아 디스크에 보관합니다. `/이미지퀴즈` NPC 종목이 씁니다.
/// - <see cref="UnpackDataCatalogs.RunAsync"/>의 보관소 목록에서 NPC·지역 보관소 뒤에 둡니다. 이미지는 NPC·지역 데이터를 만든 커밋 기준으로 받습니다.
/// - 이미지 폴더마다 트리를 한 번 조회해(요청 1회) 파일별 blob SHA를 받고, 디스크에 없는 SHA만 받습니다. 파일 이름이 SHA라 내용이 같으면 다시 받지 않습니다.
/// - 필요 없어진 파일은 지웁니다. 일부를 받지 못하면 받은 것만 쓰고, 이전에 받아 둔 같은 NPC 이미지는 유지한 채 다시 시도합니다.
/// </summary>
public sealed class NpcPortraitCache : IUnpackDataCatalog
{
    public const string AssetRoot = "Patch/AssetPackages/Assets/PatchableAssets";
    private const int ManifestFormat = 1;
    private const int MaxImageBytes = 1024 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly UnpackRepoClient m_Client;
    private readonly UnpackDataCatalog<WorldTable> m_World;
    private readonly string m_Directory;
    private readonly string m_ManifestPath;
    private readonly Action<string> m_Log;
    private readonly SemaphoreSlim m_Gate = new(1, 1);
    // 에셋 경로 → blob SHA. 디스크에 파일이 있는 것만 담습니다.
    private IReadOnlyDictionary<string, string> m_Images = new Dictionary<string, string>(StringComparer.Ordinal);
    // 이번 실행에서 확인을 마친 NPC·지역 데이터 커밋. 같으면 요청하지 않습니다.
    private string? m_Synced;

    public NpcPortraitCache(UnpackRepoClient client, UnpackDataCatalog<WorldTable> world, string dataDirectory, Action<string>? log = null)
    {
        m_Client = client;
        m_World = world;
        m_Directory = Path.Combine(dataDirectory, "unpack", "npc-portraits");
        m_ManifestPath = Path.Combine(m_Directory, "manifest.json");
        m_Log = log ?? (msg => Console.WriteLine($"[게임데이터:{Name}] {msg}"));
    }

    public string Name => "NPC 이미지";
    public int Count => Volatile.Read(ref m_Images).Count;

    /// <summary>받아 둔 이미지 파일 경로. 없으면 null입니다. assetPath는 <see cref="NpcData.ProfileImage"/>입니다.</summary>
    public string? GetPath(string assetPath) =>
        Volatile.Read(ref m_Images).TryGetValue(assetPath, out var sha) ? Path.Combine(m_Directory, sha + ".png") : null;

    /// <summary>디스크의 목록만 읽습니다(요청 0회). 파일이 없어진 항목은 뺍니다.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(m_ManifestPath)) return;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(m_ManifestPath, ct));
                if (document.RootElement.GetProperty("format").GetInt32() != ManifestFormat) throw new InvalidDataException("목록 형식이 다릅니다.");
                var images = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var item in document.RootElement.GetProperty("images").EnumerateObject())
                    if (item.Value.GetString() is { Length: 40 } sha && File.Exists(Path.Combine(m_Directory, sha + ".png")))
                        images[item.Name] = sha;
                Volatile.Write(ref m_Images, images);
                m_Log($"캐시 로딩: {images.Count}개");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { m_Log($"캐시 목록 로딩 실패, 저장소에서 다시 받습니다: {ex.Message}"); }
        }
        finally { m_Gate.Release(); }
    }

    public async Task<UnpackRefreshResult> RefreshAsync(string commit, CancellationToken ct)
    {
        await m_Gate.WaitAsync(ct);
        try { return await RefreshLockedAsync(commit, ct); }
        finally { m_Gate.Release(); }
    }

    private async Task<UnpackRefreshResult> RefreshLockedAsync(string commit, CancellationToken ct)
    {
        // NPC·지역 데이터가 없으면 받을 목록도 없습니다. 그 보관소가 이미 실패를 기록하고 다시 시도합니다.
        if (m_World.Current is not { } world) return new UnpackRefreshResult(UnpackRepoStatus.Success);
        // NPC 목록과 같은 버전의 이미지를 받습니다(버전 커밋에 파일이 없어 최신 커밋으로 만든 경우 포함).
        commit = world.Commit;
        if (m_Synced == commit) return new UnpackRefreshResult(UnpackRepoStatus.Success);

        var previous = Volatile.Read(ref m_Images);
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        UnpackRefreshResult? failure = null;
        int downloaded = 0, missing = 0;
        var needed = world.Data.Npcs.Select(x => x.ProfileImage).OfType<string>().Distinct(StringComparer.Ordinal);
        foreach (var folder in needed.GroupBy(DirectoryOf, StringComparer.Ordinal))
        {
            var tree = await m_Client.GetTreeAsync($"{AssetRoot}/{folder.Key}", commit, ct);
            if (!tree.IsSuccess)
            {
                failure ??= Failed(tree.Status, $"{folder.Key} 목록 확인 실패({tree.Status}): {tree.Detail}", tree.RetryAfter);
                KeepPrevious(folder, previous, next);
                continue;
            }
            var blobs = tree.Value!.Where(x => x.Type == "blob").ToDictionary(x => x.Name, x => x.Sha, StringComparer.Ordinal);
            foreach (var image in folder)
            {
                if (!blobs.TryGetValue(image[(image.LastIndexOf('/') + 1)..], out var sha)) { missing++; continue; }
                var path = Path.Combine(m_Directory, sha + ".png");
                if (!File.Exists(path))
                {
                    var file = await m_Client.GetFileAsync($"{AssetRoot}/{image}", commit, ct);
                    if (!file.IsSuccess)
                    {
                        failure ??= Failed(file.Status, $"{image} 받기 실패({file.Status}): {file.Detail}", file.RetryAfter);
                        if (previous.TryGetValue(image, out var old)) next[image] = old;
                        continue;
                    }
                    if (file.Value!.Length > MaxImageBytes || !file.Value.AsSpan().StartsWith(PngSignature))
                    {
                        m_Log($"{image}가 PNG가 아니거나 너무 커서 쓰지 않습니다.");
                        continue;
                    }
                    try { await WriteAtomicAsync(path, file.Value, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        failure ??= Failed(UnpackRepoStatus.Unavailable, $"{image} 저장 실패: {ex.Message}");
                        if (previous.TryGetValue(image, out var old)) next[image] = old;
                        continue;
                    }
                    downloaded++;
                }
                next[image] = sha;
            }
        }

        try { await SaveManifestAsync(commit, next, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return failure ?? Failed(UnpackRepoStatus.Unavailable, $"목록 저장 실패: {ex.Message}"); }
        Volatile.Write(ref m_Images, next);
        var removed = Prune(next.Values.ToHashSet(StringComparer.Ordinal));
        var changed = downloaded > 0 || removed > 0 || next.Count != previous.Count;
        if (failure is not null) return failure;
        m_Synced = commit;
        m_Log($"확인 완료: {next.Count}개(새로 받음 {downloaded}, 지움 {removed}, 저장소에 없음 {missing}) (커밋 {commit[..7]})");
        return new UnpackRefreshResult(UnpackRepoStatus.Success, Changed: changed);
    }

    private static string DirectoryOf(string assetPath) => assetPath[..Math.Max(0, assetPath.LastIndexOf('/'))];

    private static void KeepPrevious(IEnumerable<string> images, IReadOnlyDictionary<string, string> previous, Dictionary<string, string> next)
    {
        foreach (var image in images)
            if (previous.TryGetValue(image, out var sha)) next[image] = sha;
    }

    private int Prune(IReadOnlySet<string> keep)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(m_Directory, "*.png"))
        {
            if (keep.Contains(Path.GetFileNameWithoutExtension(file))) continue;
            try { File.Delete(file); removed++; }
            catch (Exception ex) { m_Log($"쓰지 않는 이미지 삭제 실패: {ex.Message}"); }
        }
        return removed;
    }

    private async Task SaveManifestAsync(string commit, IReadOnlyDictionary<string, string> images, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", ManifestFormat);
            writer.WriteString("commit", commit);
            writer.WriteStartObject("images");
            foreach (var (image, sha) in images.OrderBy(x => x.Key, StringComparer.Ordinal)) writer.WriteString(image, sha);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        await WriteAtomicAsync(m_ManifestPath, buffer.ToArray(), ct);
    }

    private async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(m_Directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    void IUnpackDataCatalog.LogStopped(string reason) => m_Log(reason + (Count == 0 ? " 받아 둔 이미지가 없어 NPC 이미지 문제는 낼 수 없습니다." : " 받아 둔 이미지를 계속 사용합니다."));

    UnpackRefreshResult IUnpackDataCatalog.Fail(UnpackRepoStatus status, string detail, TimeSpan? retryAfter) => Failed(status, detail, retryAfter);

    private UnpackRefreshResult Failed(UnpackRepoStatus status, string detail, TimeSpan? retryAfter = null)
    {
        m_Log($"{detail} — 받아 둔 이미지 {Count}개 유지");
        return new UnpackRefreshResult(status, Detail: detail, RetryAfter: retryAfter);
    }
}
