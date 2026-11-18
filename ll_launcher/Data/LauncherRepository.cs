using Microsoft.Data.Sqlite;
using ll_launcher.Models;
using System.Globalization;
using System.IO;

namespace ll_launcher.Data;

/// <summary>
/// Local, synchronous storage. Every user mutation is one short transaction.
/// Database files are never silently replaced after an open or integrity failure.
/// </summary>
public sealed class LauncherRepository : IDisposable
{
    internal const int SchemaVersion = 2;
    // Empty after backup. A missing revision identifies a database from an earlier release.
    private const string BackupRevisionKey = "backup.pendingRevision";
    private readonly Dictionary<long, string> _shortcutCache = new();
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly string _backupDirectory;
    private bool _disposed;

    public string DatabasePath { get; }

    public long DefaultGroupId => Read(() => Convert.ToInt64(
        Scalar(null, "SELECT id FROM launcher_groups WHERE is_default = 1;"), CultureInfo.InvariantCulture));

    public LauncherRepository(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        BackupRecovery.CompletePendingRestore(directory);
        DatabasePath = Path.Combine(directory, "launcher.db");
        _backupDirectory = Path.Combine(directory, "Backups");
        bool existed = File.Exists(DatabasePath);
        if (existed && new FileInfo(DatabasePath).Length == 0)
            throw new InvalidDataException("数据库文件为空，已保留原文件。请从备份恢复，不能自动覆盖创建。");

        _connection = OpenConnection(DatabasePath, existed ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate);
        try
        {
            _connection.Open();
            Execute(null, "PRAGMA busy_timeout = 5000;");
            if (existed) CheckIntegrity(_connection);
            int version = Convert.ToInt32(Scalar(null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
                throw new InvalidDataException("数据库来自较新版本的启动器，请使用相应版本打开。原文件已保留。");
            if (version < 0)
                throw new InvalidDataException("数据库版本号无效，已保留原文件。");
            if (version == 0 && Convert.ToInt64(Scalar(null,
                    "SELECT COUNT(*) FROM sqlite_master WHERE name NOT GLOB 'sqlite_*';"),
                    CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("无法识别现有数据库结构，已保留原文件，不会自动覆盖。");
            if (version > 0) ValidateSchema(version);

            Execute(null, "PRAGMA foreign_keys = ON;");
            Execute(null, "PRAGMA journal_mode = DELETE;");
            Execute(null, "PRAGMA synchronous = FULL;");
            if (version < SchemaVersion)
            {
                // Any migration of an existing valid database must have a verified backup first.
                if (existed) CreateBackupCore();
                InitializeSchema(version);
            }
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public List<LauncherGroup> GetGroups() => Read(() =>
    {
        using var command = Command(null, "SELECT id, name, icon_key, sort_order FROM launcher_groups ORDER BY sort_order, id;");
        using var reader = command.ExecuteReader();
        var result = new List<LauncherGroup>();
        while (reader.Read())
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return result;
    });

    public List<LauncherItem> GetItems() => Read(() =>
    {
        using var command = Command(null, """
            SELECT i.id, i.group_id, i.name, i.target, i.arguments, i.working_directory,
                   i.kind, i.is_favorite, i.sort_order
            FROM launcher_items i JOIN launcher_groups g ON g.id = i.group_id
            ORDER BY g.sort_order, g.id, i.sort_order, i.id;
            """);
        using var reader = command.ExecuteReader();
        var result = new List<LauncherItem>();
        while (reader.Read())
            result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.GetInt32(8)));
        return result;
    });

    public long SaveGroup(LauncherGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        RequireText(group.Name, nameof(group.Name));
        RequireText(group.IconKey, nameof(group.IconKey));
        if (group.Id < 0) throw new ArgumentOutOfRangeException(nameof(group.Id));
        return WriteContent(transaction =>
        {
            var order = GroupIds(transaction);
            long id = group.Id;
            long defaultId = Convert.ToInt64(Scalar(transaction,
                "SELECT id FROM launcher_groups WHERE is_default = 1;"), CultureInfo.InvariantCulture);
            if (id == defaultId && group.Name.Trim() != "其他")
                throw new InvalidOperationException("默认“其他”分组不能重命名。");
            if (id != defaultId && group.Name.Trim() == "其他")
                throw new InvalidOperationException("“其他”是保留的默认分组名称。");
            if (id == 0)
            {
                Execute(transaction, "INSERT INTO launcher_groups(name, icon_key, sort_order) VALUES ($name, $icon, 0);",
                    ("$name", group.Name.Trim()), ("$icon", group.IconKey));
                id = Convert.ToInt64(Scalar(transaction, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
            }
            else
            {
                RequireExisting(order.Remove(id), "分组不存在。");
                Execute(transaction, "UPDATE launcher_groups SET name = $name, icon_key = $icon WHERE id = $id;",
                    ("$name", group.Name.Trim()), ("$icon", group.IconKey), ("$id", id));
            }
            order.Insert(Math.Clamp(group.SortOrder, 0, order.Count), id);
            ApplyOrder(transaction, "launcher_groups", order);
            return id;
        });
    }

    public long SaveItem(LauncherItem item, ShortcutSnapshot? shortcut = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireText(item.Name, nameof(item.Name));
        RequireText(item.Target, nameof(item.Target));
        RequireText(item.Kind, nameof(item.Kind));
        if (item.Id < 0) throw new ArgumentOutOfRangeException(nameof(item.Id));
        return WriteContent(transaction =>
        {
            long id = item.Id;
            long? oldGroup = id == 0 ? null : ItemGroup(transaction, id);
            if (oldGroup is not null && oldGroup != item.GroupId)
            {
                var previous = ItemIds(transaction, oldGroup.Value);
                previous.Remove(id);
                ApplyOrder(transaction, "launcher_items", previous);
            }
            var order = ItemIds(transaction, item.GroupId);
            order.Remove(id);
            var parameters = new (string, object)[]
            {
                ("$group", item.GroupId), ("$name", item.Name.Trim()), ("$target", item.Target),
                ("$arguments", item.Arguments ?? ""), ("$working", item.WorkingDirectory ?? ""),
                ("$kind", item.Kind), ("$favorite", item.IsFavorite ? 1 : 0)
            };
            if (id == 0)
            {
                Execute(transaction, """
                    INSERT INTO launcher_items(group_id, name, target, arguments, working_directory, kind, is_favorite, sort_order)
                    VALUES ($group, $name, $target, $arguments, $working, $kind, $favorite, 0);
                    """, parameters);
                id = Convert.ToInt64(Scalar(transaction, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
            }
            else
            {
                Execute(transaction, """
                    UPDATE launcher_items SET group_id = $group, name = $name, target = $target,
                        arguments = $arguments, working_directory = $working, kind = $kind, is_favorite = $favorite
                    WHERE id = $id;
                    """, [.. parameters, ("$id", id)]);
            }
            order.Insert(Math.Clamp(item.SortOrder, 0, order.Count), id);
            ApplyOrder(transaction, "launcher_items", order);
            if (shortcut != null)
            {
                if (item.Kind != ShortcutSnapshot.Kind || !ShortcutSnapshot.IsLinkPath(item.Target))
                    throw new ArgumentException("快捷方式目标无效。");
                StoreSnapshot(transaction, id, item.Target, shortcut);
            }
            else if (item.Kind != ShortcutSnapshot.Kind)
            {
                if (item.Id != 0 && Execute(transaction, "DELETE FROM launcher_shortcuts WHERE item_id=$id;", ("$id", id)) > 0)
                    ClearShortcutCache(id);
            }
            else if (Scalar(transaction, "SELECT 1 FROM launcher_shortcuts WHERE item_id=$id;", ("$id", id)) == null)
                throw new InvalidDataException("缺少快捷方式内容，请重新选择文件。");
            if (shortcut != null) ClearShortcutCache(id);
            return id;
        });
    }

    private void StoreSnapshot(SqliteTransaction transaction, long id, string source, ShortcutSnapshot snapshot) =>
        Execute(transaction, """
            INSERT INTO launcher_shortcuts(item_id,contents,zone,extension) VALUES($id,$contents,$zone,$extension)
            ON CONFLICT(item_id) DO UPDATE SET contents=excluded.contents, zone=excluded.zone, extension=excluded.extension;
            """, ("$id", id), ("$contents", snapshot.Contents), ("$zone", (object?)snapshot.Zone ?? DBNull.Value),
            ("$extension", Path.GetExtension(Environment.ExpandEnvironmentVariables(source)).ToLowerInvariant()));

    public bool CaptureExistingShortcut(LauncherItem item, ShortcutSnapshot snapshot) => Write(transaction =>
    {
        if (item.Kind is ShortcutSnapshot.Kind or "folder" || !ShortcutSnapshot.IsLinkPath(item.Target)) return false;
        // A user may replace or delete a legacy entry while its source is being read.
        int updated = Execute(transaction, """
            UPDATE launcher_items SET kind=$shortcut WHERE id=$id AND target=$target AND kind=$previous;
            """, ("$shortcut", ShortcutSnapshot.Kind), ("$id", item.Id), ("$target", item.Target), ("$previous", item.Kind));
        if (updated == 0) return false;
        StoreSnapshot(transaction, item.Id, item.Target, snapshot);
        MarkContentChanged(transaction);
        return true;
    });

    public string LaunchTarget(LauncherItem item)
    {
        if (item.Kind != ShortcutSnapshot.Kind) return item.Target;
        return Read(() =>
        {
            if (_shortcutCache.TryGetValue(item.Id, out var cached) && File.Exists(cached)) return cached;
            using var cmd = Command(null, "SELECT contents, zone, extension FROM launcher_shortcuts WHERE item_id=$id;", ("$id", item.Id));
            using var row = cmd.ExecuteReader();
            if (!row.Read()) throw new InvalidDataException("找不到已保存的快捷方式，请重新选择文件。");
            var snapshot = new ShortcutSnapshot((byte[])row[0], row.IsDBNull(1) ? null : (byte[])row[1]);
            var extension = row.GetString(2);
            if (extension is not (".lnk" or ".url")) throw new InvalidDataException("快捷方式类型无效。");
            var cache = ShortcutCachePath(item.Id, extension);
            snapshot.WriteCache(cache);
            _shortcutCache[item.Id] = cache;
            return cache;
        });
    }

    private string ShortcutCachePath(long id, string extension) =>
        Path.Combine(Path.GetDirectoryName(DatabasePath)!, "ShortcutCache", id + extension);

    private void ClearShortcutCache(long id)
    {
        _shortcutCache.Remove(id);
        foreach (var extension in new[] { ".lnk", ".url" })
        {
            var path = ShortcutCachePath(id, extension);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void DeleteItem(long id) => WriteContent(transaction =>
    {
        long group = ItemGroup(transaction, id);
        Execute(transaction, "DELETE FROM launcher_items WHERE id = $id;", ("$id", id));
        ClearShortcutCache(id);
        ApplyOrder(transaction, "launcher_items", ItemIds(transaction, group));
        return 0;
    });

    public void DeleteGroup(long id) => WriteContent(transaction =>
    {
        long other = Convert.ToInt64(Scalar(transaction, "SELECT id FROM launcher_groups WHERE is_default = 1;"), CultureInfo.InvariantCulture);
        if (id == other) throw new InvalidOperationException("默认“其他”分组不能删除。");
        var groups = GroupIds(transaction);
        RequireExisting(groups.Remove(id), "分组不存在。");
        var order = ItemIds(transaction, other);
        order.AddRange(ItemIds(transaction, id));
        Execute(transaction, "UPDATE launcher_items SET group_id = $other WHERE group_id = $id;", ("$other", other), ("$id", id));
        ApplyOrder(transaction, "launcher_items", order);
        Execute(transaction, "DELETE FROM launcher_groups WHERE id = $id;", ("$id", id));
        ApplyOrder(transaction, "launcher_groups", groups);
        return 0;
    });

    public void SetFavorite(long id, bool favorite) => WriteContent(transaction =>
    {
        RequireExisting(Execute(transaction, "UPDATE launcher_items SET is_favorite = $favorite WHERE id = $id;",
            ("$favorite", favorite ? 1 : 0), ("$id", id)) == 1, "启动项不存在。");
        return 0;
    });

    public void MoveItem(long id, long groupId) => Write(transaction =>
    {
        long previousGroup = ItemGroup(transaction, id);
        if (previousGroup == groupId) return 0;
        var next = ItemIds(transaction, groupId);
        Execute(transaction, "UPDATE launcher_items SET group_id = $group WHERE id = $id;", ("$group", groupId), ("$id", id));
        next.Add(id);
        ApplyOrder(transaction, "launcher_items", next);
        ApplyOrder(transaction, "launcher_items", ItemIds(transaction, previousGroup));
        MarkContentChanged(transaction);
        return 0;
    });

    public void ReorderItems(long groupId, IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        WriteContent(transaction =>
        {
            RequireExisting(GroupIds(transaction).Contains(groupId), "分组不存在。");
            ValidatePermutation(ItemIds(transaction, groupId), ids);
            ApplyOrder(transaction, "launcher_items", ids);
            return 0;
        });
    }

    public void ReorderGroups(IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        WriteContent(transaction =>
        {
            ValidatePermutation(GroupIds(transaction), ids);
            ApplyOrder(transaction, "launcher_groups", ids);
            return 0;
        });
    }

    public string GetSetting(string key, string defaultValue = "")
    {
        RequireText(key, nameof(key));
        return Read(() => Scalar(null, "SELECT value FROM launcher_settings WHERE key = $key;", ("$key", key)) as string ?? defaultValue);
    }

    public void SetSetting(string key, string value)
    {
        RequireText(key, nameof(key));
        ArgumentNullException.ThrowIfNull(value);
        Write(transaction =>
        {
            Execute(transaction, "INSERT INTO launcher_settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                ("$key", key), ("$value", value));
            return 0;
        });
    }

    public void SetSettings(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (key, value) in values)
        {
            RequireText(key, nameof(key));
            ArgumentNullException.ThrowIfNull(value);
        }
        Write(transaction =>
        {
            foreach (var (key, value) in values)
                Execute(transaction, "INSERT INTO launcher_settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                    ("$key", key), ("$value", value));
            return 0;
        });
    }

    public string CreateBackup() => Read(() => BackupCurrentData(onlyIfPending: false)!);

    public string? CreateBackupIfPending() => Read(() => BackupCurrentData(onlyIfPending: true));

    private string? BackupCurrentData(bool onlyIfPending)
    {
        var revision = Scalar(null, "SELECT value FROM launcher_settings WHERE key = $key;", ("$key", BackupRevisionKey));
        if (onlyIfPending && revision is "") return null;

        var path = CreateBackupCore();
        // An edit committed by another connection during the copy still needs its own backup.
        Execute(null, """
            INSERT INTO launcher_settings(key, value) VALUES ($key, '')
            ON CONFLICT(key) DO UPDATE SET value = '' WHERE value = $revision;
            """, ("$key", BackupRevisionKey), ("$revision", revision ?? DBNull.Value));
        return path;
    }

    private void InitializeSchema(int previousVersion)
    {
        using var transaction = _connection.BeginTransaction();
        if (previousVersion == 0) Execute(transaction, """
            CREATE TABLE launcher_groups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL CHECK(length(trim(name)) > 0),
                icon_key TEXT NOT NULL,
                sort_order INTEGER NOT NULL CHECK(sort_order >= 0),
                is_default INTEGER NOT NULL DEFAULT 0 CHECK(is_default IN (0,1))
            );
            CREATE UNIQUE INDEX ux_groups_default ON launcher_groups(is_default) WHERE is_default = 1;
            CREATE TABLE launcher_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id INTEGER NOT NULL REFERENCES launcher_groups(id) ON DELETE RESTRICT,
                name TEXT NOT NULL CHECK(length(trim(name)) > 0),
                target TEXT NOT NULL CHECK(length(trim(target)) > 0),
                arguments TEXT NOT NULL DEFAULT '',
                working_directory TEXT NOT NULL DEFAULT '',
                kind TEXT NOT NULL,
                is_favorite INTEGER NOT NULL DEFAULT 0 CHECK(is_favorite IN (0,1)),
                sort_order INTEGER NOT NULL CHECK(sort_order >= 0)
            );
            CREATE INDEX ix_items_group_order ON launcher_items(group_id, sort_order, id);
            CREATE TABLE launcher_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO launcher_settings(key, value) VALUES ($backupKey, '');
            INSERT INTO launcher_groups(name, icon_key, sort_order, is_default) VALUES ('其他', 'folder', 0, 1);
            """, ("$backupKey", BackupRevisionKey));
        Execute(transaction, """
            CREATE TABLE launcher_shortcuts (
                item_id INTEGER PRIMARY KEY REFERENCES launcher_items(id) ON DELETE CASCADE,
                contents BLOB NOT NULL,
                zone BLOB,
                extension TEXT NOT NULL CHECK(extension IN ('.lnk','.url'))
            );
            """);
        Execute(transaction, $"PRAGMA user_version = {SchemaVersion};");
        transaction.Commit();
    }

    private void ValidateSchema(int version)
    {
        // Read the expected columns before allowing writes to an existing database.
        using (var command = Command(null, "SELECT id, name, icon_key, sort_order, is_default FROM launcher_groups LIMIT 0;"))
        using (command.ExecuteReader()) { }
        using (var command = Command(null, "SELECT id, group_id, name, target, arguments, working_directory, kind, is_favorite, sort_order FROM launcher_items LIMIT 0;"))
        using (command.ExecuteReader()) { }
        using (var command = Command(null, "SELECT key, value FROM launcher_settings LIMIT 0;"))
        using (command.ExecuteReader()) { }
        if (version == 1)
        {
            if (Scalar(null, "SELECT 1 FROM sqlite_master WHERE name = 'launcher_shortcuts' COLLATE NOCASE;") != null)
                throw new InvalidDataException("旧版数据库已含有同名的快捷方式对象，无法升级。原文件已保留。");
        }
        else
        {
            using var shortcuts = Command(null, "SELECT item_id, contents, zone, extension FROM launcher_shortcuts LIMIT 0;");
            using var rows = shortcuts.ExecuteReader();
        }
        if (Convert.ToInt64(Scalar(null, "SELECT COUNT(*) FROM launcher_groups WHERE is_default = 1;"), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("数据库缺少默认分组，请从备份恢复。原文件已保留。");
        using var check = Command(null, "PRAGMA foreign_key_check;");
        using var reader = check.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("数据库存在不完整的分组关联，请从备份恢复。原文件已保留。");
    }

    private string CreateBackupCore()
    {
        Directory.CreateDirectory(_backupDirectory);
        string filename = $"launcher-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.db";
        string destinationPath = Path.Combine(_backupDirectory, filename);
        string temporaryPath = destinationPath + ".tmp";
        try
        {
            using (var destination = OpenConnection(temporaryPath, SqliteOpenMode.ReadWriteCreate))
            {
                destination.Open();
                _connection.BackupDatabase(destination);
                CheckIntegrity(destination);
            }
            File.Move(temporaryPath, destinationPath);
        }
        catch
        {
            // Retain the source and previously completed backups on every failure path.
            try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        foreach (var old in Directory.EnumerateFiles(_backupDirectory, "launcher-*.db")
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(5))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return destinationPath;
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
        DefaultTimeout = 5
    }.ToString());

    private static void CheckIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        using var reader = command.ExecuteReader();
        bool found = false;
        while (reader.Read())
        {
            found = true;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("数据库完整性检查失败，已保留原文件。请从备份恢复。");
        }
        if (!found) throw new InvalidDataException("无法检查数据库完整性，已保留原文件。");
    }

    private T Read<T>(Func<T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return action();
        }
    }

    private void MarkContentChanged(SqliteTransaction transaction) =>
        Execute(transaction, """
            INSERT INTO launcher_settings(key, value) VALUES ($key, $revision)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """, ("$key", BackupRevisionKey), ("$revision", Guid.NewGuid().ToString("N")));

    private T WriteContent<T>(Func<SqliteTransaction, T> action) => Write(transaction =>
    {
        var result = action(transaction);
        MarkContentChanged(transaction);
        return result;
    });

    private T Write<T>(Func<SqliteTransaction, T> action) => Read(() =>
    {
        using var transaction = _connection.BeginTransaction();
        T result = action(transaction);
        transaction.Commit();
        return result;
    });

    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private int Execute(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private object? Scalar(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    private List<long> Ids(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private List<long> GroupIds(SqliteTransaction transaction) => Ids(transaction, "SELECT id FROM launcher_groups ORDER BY sort_order, id;");
    private List<long> ItemIds(SqliteTransaction transaction, long groupId) => Ids(transaction,
        "SELECT id FROM launcher_items WHERE group_id = $group ORDER BY sort_order, id;", ("$group", groupId));

    private long ItemGroup(SqliteTransaction transaction, long id)
    {
        object? value = Scalar(transaction, "SELECT group_id FROM launcher_items WHERE id = $id;", ("$id", id));
        RequireExisting(value is not null, "启动项不存在。");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private void ApplyOrder(SqliteTransaction transaction, string table, IReadOnlyList<long> ids)
    {
        // Table names only come from fixed internal literals; all external data is parameterized.
        for (int index = 0; index < ids.Count; index++)
            Execute(transaction, $"UPDATE {table} SET sort_order = $order WHERE id = $id;", ("$order", index), ("$id", ids[index]));
    }

    private static void ValidatePermutation(IReadOnlyList<long> existing, IReadOnlyList<long> proposed)
    {
        if (existing.Count != proposed.Count || proposed.Distinct().Count() != proposed.Count || !existing.ToHashSet().SetEquals(proposed))
            throw new ArgumentException("排序必须恰好包含全部待排序记录，且不能重复。");
    }

    private static void RequireText(string value, string name) => ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
    private static void RequireExisting(bool condition, string message)
    {
        if (!condition) throw new KeyNotFoundException(message);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _connection.Dispose();
            _disposed = true;
        }
    }
}
