using Microsoft.Data.Sqlite;

public static class MollySqlite
{
    private static int s_ProviderInitialized;

    // 시스템 SQLite 공급자는 프로세스에서 한 번만 지정합니다. 저장소 생성자에서 먼저 호출하세요.
    public static void EnsureProvider()
    {
        if (Interlocked.Exchange(ref s_ProviderInitialized, 1) != 0) return;
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        SQLitePCL.raw.FreezeProvider();
    }
}

// 기존 DB를 이어 쓰기 위한 스키마 확인·열 추가 도우미.
public static class MollySqliteSchema
{
    public static async Task<bool> TableExistsAsync(SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", table);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    // 이전 DB에 없는 열을 추가합니다. 이미 있으면 아무것도 하지 않습니다.
    public static async Task AddColumnIfMissingAsync(SqliteConnection connection, SqliteTransaction? transaction, string table, string column, string definition, CancellationToken ct)
    {
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column;";
            info.Parameters.AddWithValue("$column", column);
            if (await info.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return;
        }
        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
