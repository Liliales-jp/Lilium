using Microsoft.Data.Sqlite;

namespace Lilium;

public sealed class SettingsStore
{
    private readonly string _connectionString;
    public event EventHandler? QuickAccessChanged;

    public SettingsStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var connection = Open();
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version is < 0 or > 1)
                throw new InvalidDataException(L10n.Get("SettingsStore_001"));
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS quick_access (path TEXT PRIMARY KEY);
                """;
            command.ExecuteNonQuery();
            VerifyColumns(command, "settings", ["key", "value"]);
            VerifyColumns(command, "quick_access", ["path"]);
            command.CommandText = "PRAGMA user_version=1";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            SqliteConnection.ClearPool(connection);
            throw;
        }
    }

    private static void VerifyColumns(SqliteCommand command, string table, string[] expected)
    {
        command.CommandText = $"PRAGMA table_info({table})"; // table names are internal constants.
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
            if (!string.Equals(reader.GetString(2), "TEXT", StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(5) != (names.Count == 1 ? 1 : 0) ||
                (table == "settings" && names.Count == 2 && reader.GetInt32(3) != 1))
                throw new InvalidDataException(L10n.Get("SettingsStore_002") + table);
        }
        if (!names.SequenceEqual(expected)) throw new InvalidDataException(L10n.Get("SettingsStore_003") + table);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch
        {
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            throw;
        }
    }

    public string Get(string key, string fallback) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key); return cmd.ExecuteScalar() as string ?? fallback; }
    public void Set(string key, string value) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value"; cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", value); cmd.ExecuteNonQuery(); }

    public void SetMany(params (string Key, string Value)[] values)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var (key, value) in values)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<string> GetQuickAccess() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT path FROM quick_access ORDER BY path COLLATE NOCASE"; using var reader = cmd.ExecuteReader(); var paths = new List<string>(); while (reader.Read()) paths.Add(reader.GetString(0)); return paths; }
    public void AddQuickAccess(string path)
    {
        bool changed;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO quick_access(path) VALUES($path)";
            cmd.Parameters.AddWithValue("$path", path);
            changed = cmd.ExecuteNonQuery() > 0;
        }
        if (changed) QuickAccessChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveQuickAccess(string path)
    {
        bool changed;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM quick_access WHERE path=$path";
            cmd.Parameters.AddWithValue("$path", path);
            changed = cmd.ExecuteNonQuery() > 0;
        }
        if (changed) QuickAccessChanged?.Invoke(this, EventArgs.Empty);
    }

}
