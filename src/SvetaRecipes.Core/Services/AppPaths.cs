namespace SvetaRecipes.Core.Services;

public static class AppPaths
{
    /// <summary>Set to point the app at another database (development, or a copy).</summary>
    public const string DbOverrideVariable = "SVETA_RECIPES_DB";

    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SvetaRecipes");

    public static string Database =>
        Environment.GetEnvironmentVariable(DbOverrideVariable) is { Length: > 0 } p ? p : Path.Combine(DataFolder, "recipes.db");

    /// <summary>Default backup folder: in Documents, so it is visible to her and follows a OneDrive-redirected Documents.</summary>
    public static string DefaultBackupFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sveta Recipes Backups");

    public static string ExportsFolder => Path.Combine(DataFolder, "exports");

    /// <summary>Imported data shipped next to the program; copied into place on the very first start.</summary>
    public static string SeedDatabase => Path.Combine(AppContext.BaseDirectory, "seed", "recipes.db");

    /// <summary>First start on a new PC: take the shipped data instead of an empty database. Never overwrites.</summary>
    public static void SeedIfMissing(string database)
    {
        if (File.Exists(database) || !File.Exists(SeedDatabase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);
        File.Copy(SeedDatabase, database);
    }
}
