public static class MollyDataPaths
{
    private static string? s_ConfiguredDatabasePath;

    public static string RootDirectory =>
        Environment.GetEnvironmentVariable("MOLLY_DATA_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "data");

    public static string DatabasePath =>
        s_ConfiguredDatabasePath
        ?? Environment.GetEnvironmentVariable("MollyDatabase__Path")
        ?? Path.Combine(RootDirectory, "database", "molly.sqlite");

    public static void Configure(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var configuredPath = configuration["MollyDatabase:Path"];
        s_ConfiguredDatabasePath = string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath;
    }
}
