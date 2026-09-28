using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TranslationSub.Services;

public class TranslationUserSettings
{
    public string Uid { get; set; }
    public int CheckIntervalHours { get; set; } = 1;
    public List<string> Sources { get; set; } = new();
    public bool UseTmdbSchedule { get; set; } = true;
    public int TmdbRefreshHours { get; set; } = 24;
    public int EndedRefreshDays { get; set; } = 7;
    public string NewSeasonMode { get; set; } = "auto";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public static class TranslationSettingsStore
{
    static readonly string[] allowedNewSeasonModes = { "auto", "notify", "off" };
    static readonly Regex sourceIdRegex = new("^[a-z0-9][a-z0-9._:/-]{0,119}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static TranslationUserSettings LoadUnsafe(string uid)
    {
        using var connection = TranslationSubDatabase.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT uid, check_interval_hours, sources_json, use_tmdb_schedule,
       tmdb_refresh_hours, ended_refresh_days, new_season_mode, updated_at
FROM settings
WHERE uid = $uid
LIMIT 1;";
        command.Parameters.AddWithValue("$uid", uid);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new TranslationUserSettings
        {
            Uid = reader.GetString(reader.GetOrdinal("uid")),
            CheckIntervalHours = reader.GetInt32(reader.GetOrdinal("check_interval_hours")),
            Sources = ReadSources(reader.GetString(reader.GetOrdinal("sources_json"))),
            UseTmdbSchedule = reader.GetInt32(reader.GetOrdinal("use_tmdb_schedule")) != 0,
            TmdbRefreshHours = reader.GetInt32(reader.GetOrdinal("tmdb_refresh_hours")),
            EndedRefreshDays = reader.GetInt32(reader.GetOrdinal("ended_refresh_days")),
            NewSeasonMode = reader.GetString(reader.GetOrdinal("new_season_mode")),
            UpdatedAt = TranslationSubDatabase.ReadDateTime(reader, "updated_at", DateTime.Now)
        };
    }

    static void SaveUnsafe(TranslationUserSettings value)
    {
        using var connection = TranslationSubDatabase.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO settings (
    uid, check_interval_hours, sources_json, use_tmdb_schedule,
    tmdb_refresh_hours, ended_refresh_days, new_season_mode, updated_at
) VALUES (
    $uid, $check_interval_hours, $sources_json, $use_tmdb_schedule,
    $tmdb_refresh_hours, $ended_refresh_days, $new_season_mode, $updated_at
)
ON CONFLICT(uid) DO UPDATE SET
    check_interval_hours = excluded.check_interval_hours,
    sources_json = excluded.sources_json,
    use_tmdb_schedule = excluded.use_tmdb_schedule,
    tmdb_refresh_hours = excluded.tmdb_refresh_hours,
    ended_refresh_days = excluded.ended_refresh_days,
    new_season_mode = excluded.new_season_mode,
    updated_at = excluded.updated_at;";

        command.Parameters.AddWithValue("$uid", value.Uid);
        command.Parameters.AddWithValue("$check_interval_hours", value.CheckIntervalHours);
        command.Parameters.AddWithValue("$sources_json", JsonConvert.SerializeObject(value.Sources ?? new List<string>()));
        command.Parameters.AddWithValue("$use_tmdb_schedule", value.UseTmdbSchedule ? 1 : 0);
        command.Parameters.AddWithValue("$tmdb_refresh_hours", value.TmdbRefreshHours);
        command.Parameters.AddWithValue("$ended_refresh_days", value.EndedRefreshDays);
        command.Parameters.AddWithValue("$new_season_mode", value.NewSeasonMode);
        command.Parameters.AddWithValue("$updated_at", TranslationSubDatabase.DateTimeText(value.UpdatedAt));
        command.ExecuteNonQuery();
    }

    static List<string> ReadSources(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<List<string>>(json ?? "[]") ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    static TranslationUserSettings Normalize(TranslationUserSettings value, string uid = null, bool touchUpdatedAt = false)
    {
        value ??= new TranslationUserSettings();
        value.Uid = string.IsNullOrWhiteSpace(value.Uid) ? uid?.Trim() : value.Uid.Trim();
        value.CheckIntervalHours = Math.Max(1, Math.Min(24, value.CheckIntervalHours));
        value.TmdbRefreshHours = Math.Max(6, Math.Min(168, value.TmdbRefreshHours <= 0 ? 24 : value.TmdbRefreshHours));
        value.EndedRefreshDays = Math.Max(1, Math.Min(90, value.EndedRefreshDays <= 0 ? 7 : value.EndedRefreshDays));

        value.NewSeasonMode = string.IsNullOrWhiteSpace(value.NewSeasonMode)
            ? "auto"
            : value.NewSeasonMode.Trim().ToLowerInvariant();
        if (!allowedNewSeasonModes.Contains(value.NewSeasonMode))
            value.NewSeasonMode = "auto";

        value.Sources = (value.Sources ?? new List<string>())
            .Select(NormalizeSourceId)
            .Where(x => x != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (touchUpdatedAt)
            value.UpdatedAt = DateTime.Now;

        return value;
    }

    public static string NormalizeSourceId(string value)
    {
        string id = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id) || !sourceIdRegex.IsMatch(id))
            return null;
        return id;
    }

    static TranslationUserSettings Clone(TranslationUserSettings value)
    {
        return new TranslationUserSettings
        {
            Uid = value.Uid,
            CheckIntervalHours = value.CheckIntervalHours,
            Sources = value.Sources?.ToList() ?? new List<string>(),
            UseTmdbSchedule = value.UseTmdbSchedule,
            TmdbRefreshHours = value.TmdbRefreshHours,
            EndedRefreshDays = value.EndedRefreshDays,
            NewSeasonMode = value.NewSeasonMode,
            UpdatedAt = value.UpdatedAt
        };
    }

    public static TranslationUserSettings Get(string uid)
    {
        uid = string.IsNullOrWhiteSpace(uid) ? null : uid.Trim();

        lock (TranslationSubDatabase.SyncRoot)
        {
            var current = uid == null ? null : LoadUnsafe(uid);
            if (current == null)
                return Normalize(new TranslationUserSettings { Uid = uid }, uid);

            return Normalize(Clone(current), uid);
        }
    }

    public static TranslationUserSettings Set(
        string uid,
        int checkIntervalHours,
        IEnumerable<string> sources,
        bool? useTmdbSchedule = null,
        int? tmdbRefreshHours = null,
        int? endedRefreshDays = null,
        string newSeasonMode = null)
    {
        uid = string.IsNullOrWhiteSpace(uid) ? null : uid.Trim();
        if (uid == null)
            throw new ArgumentException("uid is required", nameof(uid));

        lock (TranslationSubDatabase.SyncRoot)
        {
            var current = LoadUnsafe(uid) ?? new TranslationUserSettings { Uid = uid };

            current.CheckIntervalHours = checkIntervalHours;
            current.Sources = sources?.ToList() ?? new List<string>();
            if (useTmdbSchedule.HasValue) current.UseTmdbSchedule = useTmdbSchedule.Value;
            if (tmdbRefreshHours.HasValue) current.TmdbRefreshHours = tmdbRefreshHours.Value;
            if (endedRefreshDays.HasValue) current.EndedRefreshDays = endedRefreshDays.Value;
            if (!string.IsNullOrWhiteSpace(newSeasonMode)) current.NewSeasonMode = newSeasonMode;

            Normalize(current, uid, true);
            SaveUnsafe(current);
            return Clone(current);
        }
    }

    /// <summary>
    /// Canonical UI options/defaults for server-owned policy settings. Clients
    /// render this schema and mirror canonical values, but do not define policy.
    /// </summary>
    public static object UiSchema()
    {
        var intervalValues = Enumerable.Range(1, 24)
            .ToDictionary(x => x.ToString(), HourLabel, StringComparer.Ordinal);

        return new
        {
            checkIntervalHours = new
            {
                defaultValue = 1,
                values = intervalValues
            },
            useTmdbSchedule = new
            {
                defaultValue = true
            },
            tmdbRefreshHours = new
            {
                defaultValue = 24,
                values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["6"] = "6 часов",
                    ["12"] = "12 часов",
                    ["24"] = "24 часа",
                    ["48"] = "2 дня",
                    ["72"] = "3 дня",
                    ["168"] = "7 дней"
                }
            },
            endedRefreshDays = new
            {
                defaultValue = 7,
                values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["7"] = "7 дней",
                    ["14"] = "14 дней",
                    ["30"] = "30 дней",
                    ["60"] = "60 дней"
                }
            },
            newSeasonMode = new
            {
                defaultValue = "auto",
                values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["auto"] = "Автоматически продолжать",
                    ["notify"] = "Только показать новый сезон",
                    ["off"] = "Не отслеживать новые сезоны"
                }
            }
        };
    }

    static string HourLabel(int value)
    {
        int mod10 = value % 10;
        int mod100 = value % 100;
        string suffix = mod10 == 1 && mod100 != 11
            ? "час"
            : mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)
                ? "часа"
                : "часов";
        return value + " " + suffix;
    }
}
