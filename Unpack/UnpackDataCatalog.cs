using System.Text.Json;

namespace Molly.Unpack;

/// <summary>
/// 게임 데이터 저장소의 시트로 기능 하나가 쓰는 데이터를 만드는 규칙.
/// 원본 시트를 그대로 들고 있지 않고, 기능에 필요한 값만 골라 작은 데이터로 바꿉니다.
/// </summary>
public interface IUnpackDataset<T> where T : class
{
    // 로그와 사용자 안내에 쓰는 이름(예: "룬").
    string Name { get; }
    // 캐시 파일 이름(예: "runes").
    string CacheName { get; }
    /// <summary>필요한 시트를 받아 데이터를 만듭니다. 검증에 실패하면 InvalidDataException을 던집니다.</summary>
    Task<T> BuildAsync(UnpackSheetFetcher sheets, CancellationToken ct);
    void Write(Utf8JsonWriter writer, T data);
    T Read(JsonElement data);
    string Describe(T data);
}

/// <summary>데이터와 그 데이터를 만든 저장소 버전.</summary>
public sealed record UnpackSnapshot<T>(T Data, string Commit, string PatchDisplay, string AppVersion, DateTimeOffset? GeneratedAt, DateTimeOffset BuiltAt)
    where T : class;

public sealed record UnpackRefreshResult(UnpackRepoStatus Status, bool Changed = false, string? Detail = null, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Status == UnpackRepoStatus.Success;
}

/// <summary>
/// 게임 데이터 저장소에서 만든 데이터 하나의 공용 보관소.
/// - 시작 시 디스크 캐시를 읽고, 이후 <see cref="UnpackDataCatalogs.RunAsync"/>가 주기적으로 시트 폴더의 마지막 커밋만 확인합니다(모든 데이터 합쳐 요청 1회).
/// - 봇이 시작된 뒤 첫 확인은 커밋과 상관없이 항상 다시 만들어 변환 규칙이 바뀐 코드가 바로 반영되게 합니다.
///   캐시는 그 사이와 저장소에 접근하지 못할 때 쓰는 마지막 정상 데이터입니다.
/// - 이후에는 커밋이 바뀌었을 때만 필요한 시트를 받아 다시 만들고, 검증·캐시 저장까지 성공해야 교체합니다. 실패하면 마지막 정상 데이터를 유지합니다.
/// - 캐시도 없고 저장소에도 접근하지 못하면 <see cref="Data"/>가 null이며, 명령은 <see cref="UnpackDataMessages.NoData"/>로 응답합니다.
/// </summary>
public sealed class UnpackDataCatalog<T> : IUnpackDataCatalog where T : class
{
    private const int CacheFormat = 1;

    private readonly UnpackRepoClient m_Client;
    private readonly IUnpackDataset<T> m_Dataset;
    private readonly string m_CachePath;
    private readonly Action<string> m_Log;
    private readonly Func<DateTimeOffset> m_Now;
    private readonly SemaphoreSlim m_Gate = new(1, 1);
    private UnpackSnapshot<T>? m_Current;
    // 이번 실행에서 저장소로 직접 만든 데이터인지. 캐시에서 읽은 데이터는 이전 코드의 변환 규칙으로 만들었을 수 있습니다.
    private bool m_BuiltThisRun;

    public UnpackDataCatalog(UnpackRepoClient client, IUnpackDataset<T> dataset, string dataDirectory, Action<string>? log = null, Func<DateTimeOffset>? now = null)
    {
        m_Client = client;
        m_Dataset = dataset;
        m_CachePath = Path.Combine(dataDirectory, "unpack", dataset.CacheName + ".json");
        m_Log = log ?? (msg => Console.WriteLine($"[게임데이터:{dataset.Name}] {msg}"));
        m_Now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name => m_Dataset.Name;
    public UnpackSnapshot<T>? Current => Volatile.Read(ref m_Current);
    // 명령은 이 값이 null이면 데이터가 없다고 안내합니다.
    public T? Data => Current?.Data;
    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>디스크 캐시만 읽습니다. 저장소 확인은 <see cref="UnpackDataCatalogs.RunAsync"/>가 합니다.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await m_Gate.WaitAsync(ct);
        try
        {
            if (Current is not null || !File.Exists(m_CachePath)) return;
            try
            {
                var snapshot = ReadCache(await File.ReadAllBytesAsync(m_CachePath, ct));
                Volatile.Write(ref m_Current, snapshot);
                m_Log($"캐시 로딩: {m_Dataset.Describe(snapshot.Data)} (커밋 {snapshot.Commit[..7]}, 패치 {Display(snapshot.PatchDisplay)})");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { m_Log($"캐시 로딩 실패, 저장소에서 다시 받습니다: {ex.Message}"); }
        }
        finally { m_Gate.Release(); }
    }

    /// <summary>시트 폴더의 마지막 커밋을 확인하고, 바뀌었거나 데이터가 없을 때만 다시 만듭니다.</summary>
    public async Task<UnpackRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var head = await m_Client.GetLatestCommitForPathAsync(UnpackSheets.ClientDirectory, ct);
        if (!head.IsSuccess) return Failed(head.Status, $"시트 커밋 확인 실패({head.Status}): {head.Detail}", head.RetryAfter);
        return await RefreshAsync(head.Value!, ct);
    }

    /// <summary>이미 확인한 시트 폴더 커밋으로 갱신합니다. 여러 데이터가 커밋 확인 한 번을 함께 씁니다(<see cref="UnpackDataCatalogs.RunAsync"/>).</summary>
    public async Task<UnpackRefreshResult> RefreshAsync(string commit, CancellationToken ct)
    {
        await m_Gate.WaitAsync(ct);
        try { return await RefreshLockedAsync(commit, ct); }
        finally { m_Gate.Release(); }
    }

    /// <summary>이 데이터 하나만 주기적으로 확인합니다. 봇은 <see cref="UnpackDataCatalogs.RunAsync"/>로 모든 데이터를 함께 확인합니다.</summary>
    public Task RunAsync(CancellationToken ct) => UnpackDataCatalogs.RunAsync(m_Client, [this], ct);

    void IUnpackDataCatalog.LogStopped(string reason) => m_Log(reason + (Current is null ? " 데이터가 없어 관련 명령은 사용할 수 없습니다." : " 마지막 데이터를 계속 사용합니다."));

    UnpackRefreshResult IUnpackDataCatalog.Fail(UnpackRepoStatus status, string detail, TimeSpan? retryAfter) => Failed(status, detail, retryAfter);

    private async Task<UnpackRefreshResult> RefreshLockedAsync(string commit, CancellationToken ct)
    {
        var current = Current;
        if (current is not null && current.Commit == commit && m_BuiltThisRun)
        {
            LastCheckedAt = m_Now();
            return new UnpackRefreshResult(UnpackRepoStatus.Success);
        }

        // 표시용 패치 정보. 읽지 못해도 데이터는 만듭니다.
        var version = await m_Client.GetVersionAsync(commit, ct);
        T data;
        try
        {
            data = await m_Dataset.BuildAsync(async (sheet, token) =>
            {
                var file = await m_Client.GetFileAsync(UnpackSheets.PathOf(sheet), commit, token);
                if (!file.IsSuccess) throw new UnpackDataException(file.Status, $"{sheet} 시트 받기 실패({file.Status}): {file.Detail}");
                return UnpackSheets.Parse(sheet, file.Value!);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnpackDataException ex) { return Failed(ex.Status, ex.Message); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Failed(UnpackRepoStatus.InvalidResponse, $"데이터 검증 실패: {ex.Message}");
        }

        var snapshot = new UnpackSnapshot<T>(data, commit, version.Value?.PatchDisplay ?? "", version.Value?.AppVersion ?? "", version.Value?.GeneratedAt, m_Now());
        try { await SaveCacheAsync(snapshot, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Failed(UnpackRepoStatus.Unavailable, $"캐시 저장 실패: {ex.Message}"); }
        // 디스크 저장까지 성공한 경우에만 새 데이터를 공개합니다.
        Volatile.Write(ref m_Current, snapshot);
        m_BuiltThisRun = true;
        LastCheckedAt = snapshot.BuiltAt;
        m_Log($"갱신 완료: {m_Dataset.Describe(data)} (커밋 {commit[..7]}, 패치 {Display(snapshot.PatchDisplay)})");
        return new UnpackRefreshResult(UnpackRepoStatus.Success, Changed: true);
    }

    private UnpackRefreshResult Failed(UnpackRepoStatus status, string detail, TimeSpan? retryAfter = null)
    {
        m_Log(Current is null ? $"{detail} — 데이터 없음" : $"{detail} — 기존 데이터 유지(커밋 {Current.Commit[..7]})");
        return new UnpackRefreshResult(status, Detail: detail, RetryAfter: retryAfter);
    }

    private UnpackSnapshot<T> ReadCache(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("format").GetInt32() != CacheFormat) throw new InvalidDataException("캐시 형식이 다릅니다.");
        var snapshot = new UnpackSnapshot<T>(
            m_Dataset.Read(root.GetProperty("data")),
            root.GetProperty("commit").GetString() ?? throw new InvalidDataException("캐시에 커밋이 없습니다."),
            root.GetProperty("patch").GetString() ?? "",
            root.GetProperty("appVersion").GetString() ?? "",
            root.GetProperty("generatedAt").ValueKind == JsonValueKind.Null ? null : root.GetProperty("generatedAt").GetDateTimeOffset(),
            root.GetProperty("builtAt").GetDateTimeOffset());
        if (snapshot.Commit.Length != 40) throw new InvalidDataException("캐시의 커밋 형식이 다릅니다.");
        return snapshot;
    }

    private async Task SaveCacheAsync(UnpackSnapshot<T> snapshot, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", CacheFormat);
            writer.WriteString("commit", snapshot.Commit);
            writer.WriteString("patch", snapshot.PatchDisplay);
            writer.WriteString("appVersion", snapshot.AppVersion);
            if (snapshot.GeneratedAt is { } generatedAt) writer.WriteString("generatedAt", generatedAt);
            else writer.WriteNull("generatedAt");
            writer.WriteString("builtAt", snapshot.BuiltAt);
            writer.WritePropertyName("data");
            m_Dataset.Write(writer, snapshot.Data);
            writer.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(m_CachePath)!);
        var temporary = m_CachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, buffer.ToArray(), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, m_CachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Display(string patch) => patch.Length > 0 ? patch : "알 수 없음";
}

/// <summary>커밋 확인을 함께 쓰는 데이터 보관소. <see cref="UnpackDataCatalog{T}"/>가 구현합니다.</summary>
public interface IUnpackDataCatalog
{
    string Name { get; }
    Task<UnpackRefreshResult> RefreshAsync(string commit, CancellationToken ct);
    // 확인을 멈출 때 데이터별 로그. 데이터 유무에 따라 안내를 덧붙입니다.
    void LogStopped(string reason);
    // 커밋 확인 자체가 실패했을 때 데이터별 로그를 남기고 결과를 돌려줍니다.
    UnpackRefreshResult Fail(UnpackRepoStatus status, string detail, TimeSpan? retryAfter);
}

/// <summary>
/// 여러 데이터 보관소의 주기 확인. 데이터 수와 상관없이 확인 1회당 시트 폴더 커밋 요청은 1회이고,
/// 같은 커밋으로 각 데이터를 차례로 갱신합니다(바뀐 데이터만 시트를 받습니다).
/// </summary>
public static class UnpackDataCatalogs
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    public static async Task RunAsync(UnpackRepoClient client, IReadOnlyList<IUnpackDataCatalog> catalogs, CancellationToken ct)
    {
        if (!client.IsConfigured)
        {
            foreach (var catalog in catalogs)
                catalog.LogStopped($"저장소를 읽지 않습니다({client.DescribeState()}).");
            return;
        }
        try
        {
            while (true)
            {
                var results = await RefreshAllAsync(client, catalogs, ct);
                if (results.Any(x => x.Status is UnpackRepoStatus.Unauthorized or UnpackRepoStatus.NotConfigured or UnpackRepoStatus.Disabled))
                {
                    foreach (var catalog in catalogs)
                        catalog.LogStopped("저장소 접근이 막혀 재시작 전까지 확인을 멈춥니다.");
                    return;
                }
                var delay = results.All(x => x.IsSuccess) ? CheckInterval
                    : results.Select(x => x.RetryAfter).OfType<TimeSpan>().Append(RetryInterval).Max();
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Console.WriteLine($"[게임데이터] 주기 확인 중단: {ex}"); }
    }

    /// <summary>시트 폴더 커밋을 한 번 확인하고 모든 데이터를 그 커밋으로 갱신합니다.</summary>
    public static async Task<IReadOnlyList<UnpackRefreshResult>> RefreshAllAsync(UnpackRepoClient client, IReadOnlyList<IUnpackDataCatalog> catalogs, CancellationToken ct)
    {
        var head = await client.GetLatestCommitForPathAsync(UnpackSheets.ClientDirectory, ct);
        if (!head.IsSuccess)
            return catalogs.Select(x => x.Fail(head.Status, $"시트 커밋 확인 실패({head.Status}): {head.Detail}", head.RetryAfter)).ToArray();
        var results = new List<UnpackRefreshResult>();
        foreach (var catalog in catalogs)
            results.Add(await catalog.RefreshAsync(head.Value!, ct));
        return results;
    }
}

public static class UnpackDataMessages
{
    /// <summary>
    /// 게임 데이터 저장소의 데이터를 쓰는 명령이 데이터를 하나도 갖고 있지 않을 때(캐시 없음 + 저장소 접근 실패) 보내는 안내.
    /// </summary>
    public static string NoData(string dataName) =>
        $"{dataName} 데이터가 없어서 지금은 이 명령을 사용할 수 없어요. 게임 데이터를 아직 불러오지 못했어요. 잠시 후 다시 시도해주세요.";
}
