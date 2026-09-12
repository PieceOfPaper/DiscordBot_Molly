using System.Security.Cryptography;
using System.Text;

namespace Molly.Runes;

public sealed class RuneCatalog
{
    private readonly IRuneSource source;
    private readonly string cachePath;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private RuneTable current = RuneTable.Empty;
    public RuneTable Current => Volatile.Read(ref current);

    public RuneCatalog(IRuneSource source, string dataDirectory, Action<string>? log = null)
    {
        this.source = source;
        this.log = log ?? Console.WriteLine;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.CacheKey)));
        cachePath = Path.Combine(dataDirectory, "runes", key + ".csv");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Current.Items.Count == 0 && File.Exists(cachePath))
            {
                try
                {
                    var csv = await File.ReadAllTextAsync(cachePath, ct);
                    var snapshot = RuneCsvReader.Parse(csv, File.GetLastWriteTimeUtc(cachePath));
                    Volatile.Write(ref current, snapshot);
                    log($"[룬] 로컬 캐시 {snapshot.Items.Count}개 로딩 완료");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { log($"[룬] 캐시 로딩 실패: {ex.Message}"); }
            }
        }
        finally { gate.Release(); }
        await RefreshAsync(ct);
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var csv = await source.FetchCsvAsync(ct);
            var snapshot = RuneCsvReader.Parse(csv, DateTimeOffset.UtcNow);
            // 디스크 저장까지 성공한 경우에만 새 스냅샷을 공개합니다.
            await SaveCacheAsync(csv, ct);
            Volatile.Write(ref current, snapshot);
            log($"[룬] 시트 갱신 완료: {snapshot.Items.Count}개");
            foreach (var warning in snapshot.Warnings) log($"[룬] {warning}");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log($"[룬] 갱신 실패, 기존 {Current.Items.Count}개 유지: {ex.Message}");
            return false;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> EnsureFreshAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        if (maxAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxAge));
        var loadedAt = Current.LoadedAt;
        if (loadedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - loadedAt < maxAge)
            return true;
        return await RefreshAsync(ct);
    }

    private async Task SaveCacheAsync(string csv, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, csv, Encoding.UTF8, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

}
