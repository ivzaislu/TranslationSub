using Microsoft.Data.Sqlite;
using System;
using System.Globalization;
using System.IO;

namespace TranslationSub.Services;

internal static class TranslationSubDatabase
{
    public const string DatabasePath = "database/translationsub.db";
    public static object SyncRoot { get; } = new();

    static bool initialized;

    public static SqliteConnection Open()
    {
        EnsureInitialized();
        var connection = CreateConnection();
        connection.Open();
        Configure(connection);
        return connection;
    }

    public static string DateTimeText(DateTime value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    public static object DateTimeValue(DateTime? value)
        => value.HasValue ? DateTimeText(value.Value) : DBNull.Value;

    public static DateTime ReadDateTime(SqliteDataReader reader, string column, DateTime fallback)
    {
        var value = ReadNullableDateTime(reader, column);
        return value ?? fallback;
    }

    public static DateTime? ReadNullableDateTime(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            return null;

        string text = reader.GetString(ordinal);
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            return value;
        return null;
    }

    public static string ReadNullableString(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static int? ReadNullableInt(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    static SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        return new SqliteConnection(builder.ToString());
    }

    static void EnsureInitialized()
    {
        if (initialized)
            return;

        lock (SyncRoot)
        {
            if (initialized)
                return;

            string directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using (var connection = CreateConnection())
            {
                connection.Open();
                Configure(connection);

                using var command = connection.CreateCommand();
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS settings (
    uid TEXT NOT NULL PRIMARY KEY,
    check_interval_hours INTEGER NOT NULL,
    sources_json TEXT NOT NULL DEFAULT '[]',
    use_tmdb_schedule INTEGER NOT NULL,
    tmdb_refresh_hours INTEGER NOT NULL,
    ended_refresh_days INTEGER NOT NULL,
    new_season_mode TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS subscriptions (
    id TEXT NOT NULL PRIMARY KEY,
    uid TEXT NOT NULL,
    content_id TEXT NULL,
    title TEXT NULL,
    original_title TEXT NULL,
    kp_id TEXT NULL,
    imdb_id TEXT NULL,
    tmdb_id TEXT NULL,
    poster TEXT NULL,
    year INTEGER NULL,
    is_serial INTEGER NOT NULL,
    source TEXT NULL,
    translation_id TEXT NULL,
    translation_name TEXT NULL,
    current_season INTEGER NULL,
    last_season INTEGER NULL,
    last_episode INTEGER NULL,
    sources_json TEXT NOT NULL DEFAULT '[]',
    created_at TEXT NOT NULL,
    last_checked_at TEXT NULL,
    tmdb_status TEXT NULL,
    tmdb_last_season INTEGER NULL,
    tmdb_last_episode INTEGER NULL,
    tmdb_last_air_date TEXT NULL,
    tmdb_next_season INTEGER NULL,
    tmdb_next_episode INTEGER NULL,
    tmdb_next_air_date TEXT NULL,
    tmdb_target_season_episodes INTEGER NULL,
    tmdb_last_synced_at TEXT NULL,
    schedule_state TEXT NULL,
    tmdb_new_season_available INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_translationsub_subscriptions_uid
    ON subscriptions(uid);

CREATE TABLE IF NOT EXISTS profile_progress (
    uid TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    watched_episode INTEGER NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (uid, profile_id, subscription_id)
);
CREATE INDEX IF NOT EXISTS idx_translationsub_profile_progress_subscription
    ON profile_progress(subscription_id);
";
                command.ExecuteNonQuery();
            }

            initialized = true;
            RestrictDatabasePermissions();
            RemoveLegacyJsonFiles();
        }
    }

    static void Configure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
PRAGMA busy_timeout = 5000;
PRAGMA foreign_keys = ON;
PRAGMA synchronous = NORMAL;
";
        command.ExecuteNonQuery();
    }

    static void RestrictDatabasePermissions()
    {
        if (OperatingSystem.IsWindows() || !File.Exists(DatabasePath))
            return;

        try
        {
            File.SetUnixFileMode(DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Filesystem may not expose Unix permissions (for example some mounted volumes).
        }
    }

    static void RemoveLegacyJsonFiles()
    {
        string legacyDirectory = "database/translationsub";
        string[] legacyFiles =
        {
            Path.Combine(legacyDirectory, "settings.json"),
            Path.Combine(legacyDirectory, "subscriptions.json"),
            Path.Combine(legacyDirectory, "profile-progress.json")
        };

        foreach (string file in legacyFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
                // Legacy development data is not part of the current storage contract.
            }
        }

        try
        {
            if (Directory.Exists(legacyDirectory)
                && Directory.GetFileSystemEntries(legacyDirectory).Length == 0)
                Directory.Delete(legacyDirectory);
        }
        catch
        {
            // Leave a non-empty or locked legacy directory untouched.
        }
    }
}
