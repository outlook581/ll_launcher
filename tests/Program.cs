using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ll_launcher.Data;
using ll_launcher.Models;
using ll_launcher.Native;

if (args is ["--crash-write", var database])
{
    using var connection = Connect(database);
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA cache_size=10; BEGIN IMMEDIATE; UPDATE launcher_items SET target='UNCOMMITTED';";
    command.ExecuteNonQuery();
    command.CommandText = "INSERT INTO launcher_settings(key,value) VALUES ($key, $value);";
    command.Parameters.Add("$key", SqliteType.Text);
    command.Parameters.AddWithValue("$value", new string('X', 16_384));
    for (int i = 0; i < 128; i++)
    {
        command.Parameters["$key"].Value = "uncommitted-" + i;
        command.ExecuteNonQuery();
    }
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    return;
}

var testRoot = Path.Combine(Path.GetTempPath(), "ll-launcher-storage-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int passed = 0;
var tests = new List<(string Name, Action<string> Test)>
{
    ("CRUD, parameters, favorites and persistence", Crud),
    ("Group and item ordering; delete-group migration", OrderingAndMigration),
    ("Foreign-key failure rolls back prior order changes", ForeignKeyRollback),
    ("Mid-operation failures roll back reorder and settings", MidOperationRollback),
    ("Verified online backup, retention and restore", BackupAndRestore),
    ("Pending content backup survives restart and backup failure", PendingBackup),
    ("Interrupted restore resumes before opening the live database", RecoveryInterruptionTests.Resume),
    ("Schema initialization backs up existing database", UpgradeBackup),
    ("Portable shortcut survives removal, cache loss and backup restore", ShortcutBackup),
    ("Internet shortcuts accept Windows INI quoting", QuotedInternetShortcut),
    ("Version one database and backup upgrade without changing groups", LegacyBackup),
    ("Corrupt, empty, unknown and newer databases are preserved", PreserveInvalidFiles),
    ("Process termination recovers an uncommitted transaction", CrashRecovery)
};
if (OperatingSystem.IsWindows())
    tests.Add(("Saved Windows shortcut resolves its target after the original is deleted", SavedShortcutTarget));
foreach (var (name, test) in tests)
{
    string directory = Path.Combine(testRoot, passed.ToString("D2"));
    Directory.CreateDirectory(directory);
    try
    {
        test(directory);
        Console.WriteLine("PASS " + name);
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("FAIL " + name + Environment.NewLine + error);
        Console.Error.WriteLine("Artifacts: " + testRoot);
        Environment.ExitCode = 1;
        return;
    }
}
Console.WriteLine($"All {passed} storage tests passed. Artifacts: {testRoot}");

static LauncherItem Item(long group, string name, int order = 0) =>
    new(0, group, name, @"C:\应用程序\" + name + ".exe", "--profile \"中文 配置\"", @"C:\工作目录", "application", false, order);

static void Crud(string directory)
{
    long id;
    long group;
    using (var repository = new LauncherRepository(directory))
    {
        Equal("其他", repository.GetGroups().Single().Name);
        group = repository.SaveGroup(new(0, "自定义 ' 分组", "folder", 6));
        id = repository.SaveItem(Item(group, "first"));
        var original = repository.GetItems().Single();
        Equal("--profile \"中文 配置\"", original.Arguments);
        repository.SaveItem(original with { Name = "renamed", Target = "https://example.com/?q=';DROP TABLE launcher_groups;--" });
        repository.SetFavorite(id, true);
        Equal(true, repository.GetItems().Single().IsFavorite);
        repository.SetFavorite(id, false);
        repository.SetSetting("view", "list");
        repository.SetSettings(new Dictionary<string, string> { ["x"] = "100", ["y"] = "200" });
        Equal("fallback", repository.GetSetting("missing", "fallback"));
    }
    using (var repository = new LauncherRepository(directory))
    {
        Equal("renamed", repository.GetItems().Single().Name);
        Equal("list", repository.GetSetting("view"));
        Equal("200", repository.GetSetting("y"));
        repository.SaveGroup(repository.GetGroups().Single(g => g.Id == group) with { Name = "改名", IconKey = "code" });
        Equal("改名", repository.GetGroups().Single(g => g.Id == group).Name);
        repository.DeleteItem(id);
        Equal(0, repository.GetItems().Count);
    }
}

static void OrderingAndMigration(string directory)
{
    using var repository = new LauncherRepository(directory);
    long development = repository.SaveGroup(new(0, "开发", "code", 0));
    long office = repository.SaveGroup(new(0, "办公", "briefcase", 1));
    long other = repository.DefaultGroupId;
    long a = repository.SaveItem(Item(development, "A", 0));
    long b = repository.SaveItem(Item(development, "B", 1));
    long c = repository.SaveItem(Item(development, "C", 2));
    repository.ReorderItems(development, [c, a, b]);
    Sequence([c, a, b], repository.GetItems().Where(i => i.GroupId == development).Select(i => i.Id));
    Sequence([0, 1, 2], repository.GetItems().Where(i => i.GroupId == development).Select(i => i.SortOrder));
    Throws<ArgumentException>(() => repository.ReorderItems(development, [a, a, c]));
    Throws<ArgumentException>(() => repository.ReorderItems(development, [a]));
    repository.MoveItem(a, office);
    Sequence([c, b], repository.GetItems().Where(i => i.GroupId == development).Select(i => i.Id));
    Sequence([0, 1], repository.GetItems().Where(i => i.GroupId == development).Select(i => i.SortOrder));
    repository.SetFavorite(c, true);
    long existing = repository.SaveItem(Item(other, "existing"));
    repository.DeleteGroup(development);
    Sequence([existing, c, b], repository.GetItems().Where(i => i.GroupId == other).Select(i => i.Id));
    Equal(true, repository.GetItems().Single(i => i.Id == c).IsFavorite);
    Equal(office, repository.GetItems().Single(i => i.Id == a).GroupId);
    Throws<InvalidOperationException>(() => repository.DeleteGroup(other));
    var reversed = repository.GetGroups().Select(g => g.Id).Reverse().ToList();
    repository.ReorderGroups(reversed);
    Sequence(reversed, repository.GetGroups().Select(g => g.Id));
    Sequence(Enumerable.Range(0, reversed.Count), repository.GetGroups().Select(g => g.SortOrder));
    Throws<ArgumentException>(() => repository.ReorderGroups([other]));
}

static void ForeignKeyRollback(string directory)
{
    using var repository = new LauncherRepository(directory);
    long group = repository.GetGroups()[0].Id;
    repository.SaveItem(Item(group, "A", 0));
    long b = repository.SaveItem(Item(group, "B", 1));
    repository.SaveItem(Item(group, "C", 2));
    var snapshot = repository.GetItems();
    // SaveItem first changes sibling ordering. A later FK failure must roll it all back.
    Throws<SqliteException>(() => repository.SaveItem(snapshot.Single(i => i.Id == b) with { GroupId = long.MaxValue, Name = "should rollback" }));
    Sequence(snapshot, repository.GetItems());
    Throws<SqliteException>(() => repository.MoveItem(b, long.MaxValue));
    Sequence(snapshot, repository.GetItems());
}

static void MidOperationRollback(string directory)
{
    long group;
    long a;
    long b;
    long c;
    using (var repository = new LauncherRepository(directory))
    {
        group = repository.GetGroups()[0].Id;
        a = repository.SaveItem(Item(group, "A", 0));
        b = repository.SaveItem(Item(group, "B", 1));
        c = repository.SaveItem(Item(group, "C", 2));
        repository.SetSetting("good", "before");
    }
    using (var connection = Connect(Path.Combine(directory, "launcher.db")))
    using (var command = connection.CreateCommand())
    {
        command.CommandText = $"""
            CREATE TRIGGER injected_order_failure BEFORE UPDATE OF sort_order ON launcher_items
            WHEN NEW.id = {b} BEGIN SELECT RAISE(ABORT, 'injected order failure'); END;
            CREATE TRIGGER injected_settings_failure BEFORE INSERT ON launcher_settings
            WHEN NEW.key = 'bad' BEGIN SELECT RAISE(ABORT, 'injected settings failure'); END;
            """;
        command.ExecuteNonQuery();
    }
    using (var repository = new LauncherRepository(directory))
    {
        Throws<SqliteException>(() => repository.ReorderItems(group, [c, b, a]));
        Sequence([a, b, c], repository.GetItems().Select(i => i.Id));
        Sequence([0, 1, 2], repository.GetItems().Select(i => i.SortOrder));
        Throws<SqliteException>(() => repository.SetSettings(new Dictionary<string, string> { ["good"] = "after", ["bad"] = "fail" }));
        Equal("before", repository.GetSetting("good"));
        Equal("missing", repository.GetSetting("bad", "missing"));
    }
}

static void BackupAndRestore(string directory)
{
    string backup;
    long id;
    using (var repository = new LauncherRepository(directory))
    {
        id = repository.SaveItem(Item(repository.GetGroups()[0].Id, "original"));
        repository.SetSetting("theme", "dark");
        for (int i = 0; i < 6; i++) repository.CreateBackup();
        backup = repository.CreateBackup();
        Equal(5, Directory.GetFiles(Path.Combine(directory, "Backups"), "*.db").Length);
        Equal(0, Directory.GetFiles(Path.Combine(directory, "Backups"), "*.tmp").Length);
        repository.DeleteItem(id);
        repository.SetSetting("theme", "changed");
    }
    string restoreDirectory = Path.Combine(directory, "restored");
    Directory.CreateDirectory(restoreDirectory);
    string live = Path.Combine(restoreDirectory, "launcher.db");
    byte[] broken = [0x12, 0x34, 0x56];
    File.WriteAllBytes(live, broken);
    BackupRecovery.Restore(restoreDirectory, backup);
    string preserved = Directory.GetFiles(Path.Combine(restoreDirectory, "Recovery"), "launcher.db", SearchOption.AllDirectories).Single();
    Equal(Convert.ToHexString(broken), Convert.ToHexString(File.ReadAllBytes(preserved)));
    using var restored = new LauncherRepository(restoreDirectory);
    Equal(id, restored.GetItems().Single().Id);
    Equal("original", restored.GetItems().Single().Name);
    Equal("dark", restored.GetSetting("theme"));
    using var raw = Connect(Path.Combine(restoreDirectory, "launcher.db"));
    using var command = raw.CreateCommand();
    command.CommandText = "PRAGMA integrity_check;";
    Equal("ok", (string)command.ExecuteScalar()!);
}

static void PendingBackup(string directory)
{
    long id;
    using (var repository = new LauncherRepository(directory))
    {
        repository.SetSetting("ui.skin", "cloud");
        repository.SetSettings(new Dictionary<string, string> { ["window.left"] = "200" });
        Throws<SqliteException>(() => repository.SaveItem(Item(long.MaxValue, "invalid group")));
        Equal<string?>(null, repository.CreateBackupIfPending());
        id = repository.SaveItem(Item(repository.DefaultGroupId, "saved before exit"));
    }

    var backups = Path.Combine(directory, "Backups");
    File.WriteAllText(backups, "block backup directory creation");
    using (var reopened = new LauncherRepository(directory))
        Throws<IOException>(() => reopened.CreateBackupIfPending());
    File.Delete(backups);

    using (var reopened = new LauncherRepository(directory))
    {
        var backup = reopened.CreateBackupIfPending() ?? throw new InvalidOperationException("Pending backup was lost on restart.");
        using var saved = Connect(backup);
        using var command = saved.CreateCommand();
        command.CommandText = "SELECT name FROM launcher_items;";
        Equal("saved before exit", (string?)command.ExecuteScalar());
    }
    using (var reopened = new LauncherRepository(directory))
    {
        Equal<string?>(null, reopened.CreateBackupIfPending());
        reopened.SetFavorite(id, true);
        reopened.CreateBackup();
        Equal<string?>(null, reopened.CreateBackupIfPending());
    }

    // A v2 database from before durable backup tracking needs one initial snapshot.
    using (var connection = Connect(Path.Combine(directory, "launcher.db")))
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "DELETE FROM launcher_settings WHERE key = 'backup.pendingRevision';";
        Equal(1, command.ExecuteNonQuery());
    }
    using var legacy = new LauncherRepository(directory);
    Equal(true, File.Exists(legacy.CreateBackupIfPending()));
    Equal<string?>(null, legacy.CreateBackupIfPending());
}

static void UpgradeBackup(string directory)
{
    string path = Path.Combine(directory, "launcher.db");
    using (var connection = Connect(path))
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "VACUUM;";
        command.ExecuteNonQuery();
    }
    using var repository = new LauncherRepository(directory);
    Equal(1, repository.GetGroups().Count);
    var backups = Directory.GetFiles(Path.Combine(directory, "Backups"), "*.db");
    Equal(1, backups.Length);
    using var backup = Connect(backups[0]);
    using var check = backup.CreateCommand();
    check.CommandText = "PRAGMA user_version;";
    Equal(0L, (long)check.ExecuteScalar()!);
}

static void ShortcutBackup(string directory)
{
    var source = Path.Combine(directory, "文档.url");
    const string address = "https://example.com/资源?topic=a=b&view=all#资料";
    File.WriteAllText(source, $"[Other]\r\nURL=https://example.com/wrong\r\n[InternetShortcut]\r\nURL={address}\r\n",
        System.Text.Encoding.Unicode);
    const string zone = "[ZoneTransfer]\r\nZoneId=3\r\n";
    if (OperatingSystem.IsWindows()) File.WriteAllText(source + ":Zone.Identifier", zone);
    var contents = File.ReadAllBytes(source);
    string backup;
    using (var repo = new LauncherRepository(directory))
    {
        var item = Item(repo.DefaultGroupId, "文档") with { Target = source, Kind = ShortcutSnapshot.Kind };
        repo.SaveItem(item, ShortcutSnapshot.Read(source));
        File.Delete(source);
        item = repo.GetItems().Single();
        var cached = repo.LaunchTarget(item);
        Sequence(contents, File.ReadAllBytes(cached));
        Equal(address, ShortcutSnapshot.ReadInternetTarget(cached));
        File.Delete(cached);
        Sequence(contents, File.ReadAllBytes(repo.LaunchTarget(item)));
        repo.SaveItem(item with { Name = "网页" });
        backup = repo.CreateBackup();
        repo.DeleteItem(item.Id);
    }
    var restored = Path.Combine(directory, "relocated");
    BackupRecovery.Restore(restored, backup);
    using var reopened = new LauncherRepository(restored);
    var saved = reopened.GetItems().Single();
    Equal("网页", saved.Name);
    var path = reopened.LaunchTarget(saved);
    Equal(true, path.StartsWith(restored + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    Sequence(contents, File.ReadAllBytes(path));
    Equal(address, ShortcutSnapshot.ReadInternetTarget(path));
    if (OperatingSystem.IsWindows()) Equal(zone, File.ReadAllText(path + ":Zone.Identifier"));
    reopened.DeleteItem(saved.Id);
    Equal(false, File.Exists(path));
}

static void QuotedInternetShortcut(string directory)
{
    string shortcut = Path.Combine(directory, "资源平台.url");
    const string address = "https://example.com/资源?topic=a=b#资料";
    foreach (char quote in new[] { '"', '\'' })
    {
        File.WriteAllText(shortcut, $"[InternetShortcut]\r\nURL={quote}{address}{quote}\r\n");
        Equal(address, ShortcutSnapshot.ReadInternetTarget(shortcut));
    }
}

static void SavedShortcutTarget(string directory)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var source = Path.Combine(directory, "桌面快捷方式.lnk");
    var target = Path.Combine(directory, "程序 文件.exe");
    File.WriteAllBytes(target, []);
    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
    try
    {
        dynamic shortcut = shell.CreateShortcut(source);
        try
        {
            shortcut.TargetPath = target;
            shortcut.Arguments = "--profile \"默认 配置\"";
            shortcut.Save();
        }
        finally { Marshal.ReleaseComObject(shortcut); }
    }
    finally { Marshal.ReleaseComObject(shell); }

    using (var repository = new LauncherRepository(directory))
    {
        var item = Item(repository.DefaultGroupId, "程序") with { Target = source, Kind = ShortcutSnapshot.Kind };
        repository.SaveItem(item, ShortcutSnapshot.Read(source));
    }
    File.Delete(source);
    using var reopened = new LauncherRepository(directory);
    var cached = reopened.LaunchTarget(reopened.GetItems().Single());
    Equal(target, ShellShortcut.GetFileSystemTarget(cached));
    File.Delete(target);
    Equal<string?>(null, ShellShortcut.GetFileSystemTarget(cached));
}

static void LegacyBackup(string directory)
{
    string path;
    using (var old = new LauncherRepository(directory))
    {
        old.SaveGroup(new(0, "我的办公", "briefcase", 0));
        old.SaveItem(Item(old.DefaultGroupId, "快捷方式") with { Target = Path.Combine(directory, "old.lnk"), IsFavorite = true });
        path = old.DatabasePath;
    }
    using (var conn = Connect(path))
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "DROP TABLE launcher_shortcuts; UPDATE launcher_items SET sort_order=999; PRAGMA user_version=1;";
        cmd.ExecuteNonQuery();
    }
    using (var upgraded = new LauncherRepository(directory))
    {
        Equal("我的办公,其他", string.Join(',', upgraded.GetGroups().Select(g => g.Name)));
        var legacy = upgraded.GetItems().Single();
        var snapshot = new ShortcutSnapshot([1,2,3], null);
        Equal(false, upgraded.CaptureExistingShortcut(legacy with { Target = Path.Combine(directory, "changed.lnk") }, snapshot));
        Equal(true, upgraded.CaptureExistingShortcut(legacy, snapshot));
        Equal(false, upgraded.CaptureExistingShortcut(legacy, snapshot));
        Equal(legacy with { Kind = ShortcutSnapshot.Kind }, upgraded.GetItems().Single());
    }
    var backup = Directory.GetFiles(Path.Combine(directory, "Backups"), "*.db").Single();
    var restored = Path.Combine(directory, "restored");
    BackupRecovery.Restore(restored, backup);
    using var reopened = new LauncherRepository(restored);
    Equal("我的办公,其他", string.Join(',', reopened.GetGroups().Select(g => g.Name)));
}

static void PreserveInvalidFiles(string directory)
{
    foreach (string kind in new[] { "corrupt", "empty", "unknown", "view-only", "negative", "invalid-v1", "conflicting-v1", "invalid-v2", "newer" })
    {
        string folder = Path.Combine(directory, kind);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "launcher.db");
        if (kind == "corrupt") File.WriteAllText(path, "this is a damaged database");
        else if (kind == "empty") File.WriteAllBytes(path, []);
        else
        {
            if (kind is "conflicting-v1" or "invalid-v2")
            {
                using var valid = new LauncherRepository(folder);
            }
            using var connection = Connect(path);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteNonQuery();
            command.CommandText = kind switch
            {
                "unknown" => "CREATE TABLE sqliteSettings(value TEXT);",
                "view-only" => "CREATE VIEW unrelated AS SELECT 1;",
                "negative" => "PRAGMA user_version=-1;",
                "invalid-v1" => "CREATE TABLE unrelated(value TEXT); PRAGMA user_version=1;",
                "conflicting-v1" => "PRAGMA user_version=1;",
                "invalid-v2" => "DROP TABLE launcher_shortcuts;",
                _ => "PRAGMA user_version=999;"
            };
            command.ExecuteNonQuery();
        }
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        bool failed = false;
        try { using var repository = new LauncherRepository(folder); }
        catch (Exception error) when (error is InvalidDataException or SqliteException) { failed = true; }
        Equal(true, failed);
        Sequence(before, SHA256.HashData(File.ReadAllBytes(path)));
        Equal(false, Directory.Exists(Path.Combine(folder, "Backups")));
    }
}

static void CrashRecovery(string directory)
{
    string path;
    List<LauncherItem> before;
    using (var repository = new LauncherRepository(directory))
    {
        repository.SaveItem(Item(repository.GetGroups()[0].Id, "committed"));
        repository.SetSetting("committed", "keep me");
        path = repository.DatabasePath;
        before = repository.GetItems();
    }
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate test executable.");
    var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("--crash-write");
    start.ArgumentList.Add(path);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start crash-test child.");
    try
    {
        var ready = child.StandardOutput.ReadLineAsync();
        if (!ready.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Crash-test child did not reach an open write transaction.");
        Equal("READY", ready.Result);
        Equal(true, File.Exists(path + "-journal"));
        Equal(true, new FileInfo(path + "-journal").Length > 0);
        child.Kill(entireProcessTree: true);
        if (!child.WaitForExit(10_000)) throw new TimeoutException("Crash-test child did not exit.");
    }
    finally
    {
        if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(10_000); }
    }
    using var recovered = new LauncherRepository(directory);
    Sequence(before, recovered.GetItems());
    Equal("keep me", recovered.GetSetting("committed"));
    Equal("absent", recovered.GetSetting("uncommitted-0", "absent"));
    Equal("absent", recovered.GetSetting("uncommitted-127", "absent"));
    recovered.SetSetting("after-recovery", "writable");
    Equal("writable", recovered.GetSetting("after-recovery"));
}

static SqliteConnection Connect(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    connection.Open();
    return connection;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected <{expected}>; got <{actual}>.");
}

static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Sequences differ.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected exception " + typeof(T).Name);
}
