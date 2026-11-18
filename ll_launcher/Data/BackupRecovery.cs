using Microsoft.Data.Sqlite;

namespace ll_launcher.Data;

/// <summary>Restores a validated snapshot while preserving the original database and any recovery journals.</summary>
public static class BackupRecovery
{
    public static void Restore(string dataDirectory, string backupPath)
    {
        CompletePendingRestore(dataDirectory);
        var recoveryDirectory = Path.Combine(dataDirectory, "Recovery", Guid.NewGuid().ToString("N"));
        var stagingDirectory = Path.Combine(recoveryDirectory, "selected-backup");
        Directory.CreateDirectory(stagingDirectory);
        var staged = Path.Combine(stagingDirectory, "launcher.db");

        using (var source = Open(backupPath, SqliteOpenMode.ReadOnly))
        using (var destination = Open(staged, SqliteOpenMode.ReadWriteCreate))
        {
            using var check = source.CreateCommand();
            check.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt64(check.ExecuteScalar()) is < 1 or > LauncherRepository.SchemaVersion)
                throw new InvalidDataException("备份版本与当前程序不匹配。");
            source.BackupDatabase(destination);
        }

        // Validate the full application schema on the copy before touching the existing database.
        using (var verify = new LauncherRepository(stagingDirectory))
        {
            _ = verify.GetGroups();
            _ = verify.GetItems();
        }

        // Publish the verified copy before moving any live files. On restart its
        // presence lets us finish the same operation before SQLite opens the database.
        Directory.Move(recoveryDirectory, PendingDirectory(dataDirectory));
        CompletePendingRestore(dataDirectory);
    }

    private static string PendingDirectory(string dataDirectory) =>
        Path.Combine(dataDirectory, "Recovery", "pending-restore");

    internal static void CompletePendingRestore(string dataDirectory)
    {
        var pending = PendingDirectory(dataDirectory);
        if (!Directory.Exists(pending)) return;
        var live = Path.Combine(dataDirectory, "launcher.db");
        var staged = Path.Combine(pending, "selected-backup", "launcher.db");
        if (File.Exists(staged))
        {
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            {
                var file = live + suffix;
                if (!File.Exists(file)) continue;
                File.Move(file, Path.Combine(pending, Path.GetFileName(file)));
            }

            if (File.Exists(live))
                File.Replace(staged, live, Path.Combine(pending, "launcher.db"));
            else
                File.Move(staged, live);
        }
        else if (!File.Exists(live))
            throw new InvalidDataException("恢复未完成，找不到待恢复或已替换的数据库。");

        // A missing staged file means its atomic replacement already completed.
        // Keep the original database and its journals together in the final archive.
        Directory.Move(pending, Path.Combine(dataDirectory, "Recovery", Guid.NewGuid().ToString("N")));
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
