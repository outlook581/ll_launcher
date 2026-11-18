using ll_launcher.Data;

internal static class RecoveryInterruptionTests
{
    public static void Resume(string directory)
    {
        var source = Path.Combine(directory, "source");
        string backup;
        using (var repository = new LauncherRepository(source))
        {
            repository.SetSetting("profile", "restored");
            backup = repository.CreateBackup();
        }

        string[] suffixes = ["-journal", "-wal", "-shm"];
        byte[] original = [12, 34, 56];
        // Stop after each rename, including the main-file replacement just
        // before final archiving. Opening the repository must finish the restore.
        for (int completed = 0; completed <= suffixes.Length + 1; completed++)
        {
            var data = Path.Combine(directory, completed.ToString());
            var recovery = Path.Combine(data, "Recovery");
            var pending = Path.Combine(recovery, "pending-restore");
            var staging = Path.Combine(pending, "selected-backup");
            Directory.CreateDirectory(staging);
            var staged = Path.Combine(staging, "launcher.db");
            var live = Path.Combine(data, "launcher.db");
            File.Copy(backup, staged);
            File.WriteAllBytes(live, original);
            for (int i = 0; i < suffixes.Length; i++)
            {
                var journal = live + suffixes[i];
                File.WriteAllText(journal, suffixes[i]);
                if (i < completed) File.Move(journal, Path.Combine(pending, Path.GetFileName(journal)));
            }
            if (completed > suffixes.Length)
                File.Replace(staged, live, Path.Combine(pending, "launcher.db"));

            using (var reopened = new LauncherRepository(data))
            {
                if (reopened.GetSetting("profile") != "restored")
                    throw new InvalidOperationException("Selected backup was not restored.");
                reopened.SetSetting("profile", "updated");
            }
            using (var reopened = new LauncherRepository(data))
            {
                if (reopened.GetSetting("profile") != "updated")
                    throw new InvalidOperationException("Completed restore was applied more than once.");
            }
            var archive = Directory.GetDirectories(recovery).Single();
            if (archive == pending || !File.ReadAllBytes(Path.Combine(archive, "launcher.db")).SequenceEqual(original))
                throw new InvalidOperationException("Original database was not archived.");
            foreach (var suffix in suffixes)
                if (File.ReadAllText(Path.Combine(archive, "launcher.db" + suffix)) != suffix)
                    throw new InvalidOperationException("Original journal was not archived: " + suffix);
        }
    }
}
