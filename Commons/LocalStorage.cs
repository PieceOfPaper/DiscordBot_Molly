using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

public sealed class LocalStorage<T>
{
    private readonly string m_RootDir;
    public string rootDir =>  m_RootDir;
    
    private readonly JsonSerializerOptions m_JsonOptions;
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> m_Locks = new();

    public LocalStorage(string? baseDataDir = null)
    {
        // 1) 환경변수 우선, 2) 인자, 3) 실행 폴더
        var baseDir =
            Environment.GetEnvironmentVariable("MOLLY_DATA_DIR")
            ?? baseDataDir
            ?? AppContext.BaseDirectory;

        var dataType = typeof(T);
        var folderName = string.IsNullOrWhiteSpace(dataType.Namespace) ? 
            $"{dataType.Name}".ToLower() : 
            $"{dataType.Namespace.Replace('.', '_')}_{dataType.Name}".ToLower();
        
        m_RootDir = Path.Combine(baseDir, folderName);
        Directory.CreateDirectory(m_RootDir);

        m_JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task SaveAsync(ulong guildId, T settings, CancellationToken ct = default)
    {
        var gate = m_Locks.GetOrAdd(guildId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var finalPath = GetFilePath(guildId);
            var tmpPath = finalPath + $".tmp-{Guid.NewGuid():N}";

            // 디렉터리 존재 보장
            Directory.CreateDirectory(m_RootDir);

            await using (var fs = new FileStream(
                tmpPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(fs, settings, m_JsonOptions, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            // 같은 볼륨 내에서 Move(overwrite) → 거의 원자적 교체
            File.Move(tmpPath, finalPath, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T?> LoadAsync(ulong guildId, CancellationToken ct = default)
    {
        var gate = m_Locks.GetOrAdd(guildId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = GetFilePath(guildId);
            if (!File.Exists(path)) return default;

            await using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            return await JsonSerializer.DeserializeAsync<T>(fs, m_JsonOptions, ct)
                   .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetFilePath(ulong guildId) => Path.Combine(m_RootDir, $"{guildId}.json");

    public List<ulong> GetAllGuildIds()
    {
        var list = new List<ulong>();
        var filePaths = Directory.GetFiles(m_RootDir, "*.json");
        foreach (var filePath in filePaths)
        {
            ulong guildId = 0;
            var fileName =  Path.GetFileNameWithoutExtension(filePath);
            if (ulong.TryParse(fileName, NumberStyles.Number, CultureInfo.InvariantCulture, out guildId))
                list.Add(guildId);
        }
        return list;
    }
}
