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
