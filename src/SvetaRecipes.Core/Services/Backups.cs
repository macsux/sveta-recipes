using Microsoft.Data.Sqlite;

namespace SvetaRecipes.Core.Services;

/// <summary>
/// Consistent copies of the database made with SQLite's online-backup API (safe while the app has it open), one per
/// day, pruned to the newest <c>keep</c>.
/// </summary>
public static class Backups
{
    public const int DefaultKeep = 30;

    /// <summary>Makes today's backup unless one already exists. Returns its path, or null when it was already there.</summary>
    public static string? Daily(string dbPath, string folder, int keep = DefaultKeep)
    {
        var target = Path.Combine(folder, $"recipes-{DateTime.Now:yyyy-MM-dd}.db");
        if (File.Exists(target)) return null;
        return Snapshot(dbPath, folder, keep, target);
    }

    /// <summary>Makes a backup now.</summary>
    public static string Snapshot(string dbPath, string folder, int keep = DefaultKeep, string? target = null)
    {
        Directory.CreateDirectory(folder);
        target ??= Path.Combine(folder, $"recipes-{DateTime.Now:yyyy-MM-dd-HHmmss}.db");
        var temp = target + ".partial";
        using (var source = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False"))
        using (var dest = new SqliteConnection($"Data Source={temp};Pooling=False"))
        {
            source.Open();
            dest.Open();
            source.BackupDatabase(dest);
        }
        File.Move(temp, target, overwrite: true);
        Prune(folder, keep);
        return target;
    }

    /// <summary>Replaces the database with a backup. The app must reload afterwards.</summary>
    public static void Restore(string backupPath, string dbPath)
    {
        SqliteConnection.ClearAllPools();
        using var source = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        using var dest = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        source.Open();
        dest.Open();
        source.BackupDatabase(dest);
    }

    public static IEnumerable<FileInfo> List(string folder) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("recipes-*.db").OrderByDescending(f => f.LastWriteTimeUtc)
            : [];

    private static void Prune(string folder, int keep)
    {
        foreach (var old in List(folder).Skip(keep))
        {
            try { old.Delete(); }
            catch (IOException) { /* in use or locked; next run will retry */ }
        }
    }
}
