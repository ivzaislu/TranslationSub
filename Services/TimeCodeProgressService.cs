using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TranslationSub.Models;

namespace TranslationSub.Services;

/// <summary>
/// Reads the progress written by Lampac's TimeCode module (database/TimeCode.sql)
/// and reconciles the profile-scoped TranslationSub progress projection.
///
/// Lampac TimeCode is the authoritative source for watched progress. Shared
/// TranslationSubscription objects must never be mutated with profile state.
///
/// Current Lampac main stores TimeCode in typed columns (percent/identity/deleted)
/// and composes profile data areas as uid:profile. A legacy reader is retained so
/// an existing database is still readable before TimeCode performs its migration.
/// </summary>
public static class TimeCodeProgressService
{
    const double WatchedPercent = 60;
    const int MaxEpisodeScan = 400;
    const string DatabasePath = "database/TimeCode.sql";

    sealed class TimeCodeRow
    {
        public string Card { get; init; }
        public string Identity { get; init; }
        public double Percent { get; init; }
    }

    sealed class TimeCodeRows
    {
        public Dictionary<string, List<TimeCodeRow>> ByHash { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, List<TimeCodeRow>> ByIdentity { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int Count { get; set; }

        public void Add(string item, TimeCodeRow row)
        {
            Count++;

            if (!string.IsNullOrWhiteSpace(item))
            {
                if (!ByHash.TryGetValue(item, out var hashRows))
                {
                    hashRows = new List<TimeCodeRow>();
                    ByHash[item] = hashRows;
                }

                hashRows.Add(row);
            }

            if (!string.IsNullOrWhiteSpace(row.Identity))
            {
                if (!ByIdentity.TryGetValue(row.Identity, out var identityRows))
                {
                    identityRows = new List<TimeCodeRow>();
                    ByIdentity[row.Identity] = identityRows;
                }

                identityRows.Add(row);
            }
        }
    }

    public static int SyncUser(string uid, string profileId = null)
    {
        uid = (uid ?? string.Empty).Trim();
        profileId = ProfileProgressStore.NormalizeProfileId(profileId);
        string timeCodeUser = BuildTimeCodeUserId(uid, profileId);

        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(timeCodeUser) || !File.Exists(DatabasePath))
            return 0;

        try
        {
            var subscriptions = SubscriptionStore.Load()
                .Where(x => x.IsSerial && string.Equals(x.Uid, uid, StringComparison.Ordinal))
                .ToList();

            // main uses DataArea.Compose(uid, profile) => uid:profile.
            // If TimeCode has not migrated an older database yet, fall back once
            // to the previous uid_profile data-area name.
            var rows = LoadRows(timeCodeUser);
            if (rows.Count == 0)
            {
                string legacyUser = BuildLegacyTimeCodeUserId(uid, profileId);
                if (!string.IsNullOrWhiteSpace(legacyUser)
                    && !string.Equals(legacyUser, timeCodeUser, StringComparison.Ordinal))
                {
                    rows = LoadRows(legacyUser);
                }
            }

            // Reconciliation is allowed only after TimeCode was opened and read
            // successfully. An empty result is authoritative: this user/profile
            // currently has no persisted watched progress.
            var fallbackOwners = BuildFallbackOwners(subscriptions, rows);

            var watchedBySubscription = new Dictionary<string, int>(StringComparer.Ordinal);
            var progressCache = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var sub in subscriptions)
            {
                if (string.IsNullOrWhiteSpace(sub.Id))
                    continue;

                int season = Math.Max(1, sub.CurrentSeason.GetValueOrDefault(1));
                string cacheKey = BuildProgressCacheKey(sub, season);

                if (!progressCache.TryGetValue(cacheKey, out int watched))
                {
                    watched = rows.Count == 0
                        ? 0
                        : FindWatchedEpisode(sub, season, rows, fallbackOwners);
                    watched = Math.Max(0, watched);
                    progressCache[cacheKey] = watched;
                }

                watchedBySubscription[sub.Id] = watched;
            }

            // Only profile-scoped state changes here. Shared subscription metadata
            // (voice, sources, available episode, TMDB state) remains untouched.
            // Missing/zero TimeCode progress removes stale projection rows.
            return ProfileProgressStore.Reconcile(uid, profileId, watchedBySubscription);
        }
        catch
        {
            // TimeCode may be disabled, being migrated, or momentarily locked.
            // A read failure is not equivalent to zero progress, so preserve the
            // last successful projection and never break subscriptions/API.
            return 0;
        }
    }

    public static string BuildTimeCodeUserId(string uid, string profileId = null)
    {
        uid = (uid ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(uid))
            return string.Empty;

        profileId = ProfileProgressStore.NormalizeProfileId(profileId);

        // Keep this identical to current Modules/Sync/TimeCode/Controller.cs#getUserid.
        return DataArea.Compose(uid, profileId == "0" ? null : profileId);
    }

    static string BuildLegacyTimeCodeUserId(string uid, string profileId)
    {
        uid = (uid ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(uid))
            return string.Empty;

        profileId = ProfileProgressStore.NormalizeProfileId(profileId);
        return DataArea.Legacy(uid, profileId == "0" ? null : profileId);
    }

    static TimeCodeRows LoadRows(string user)
    {
        var result = new TimeCodeRows();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 2
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var columns = TableColumns(connection);
        bool typedSchema = columns.Contains("percent");

        using var command = connection.CreateCommand();
        command.CommandText = typedSchema
            ? "SELECT card, item, identity, percent, deleted FROM timecodes WHERE user = $user"
            : "SELECT card, item, NULL AS identity, data, 0 AS deleted FROM timecodes WHERE user = $user";
        command.Parameters.AddWithValue("$user", user);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bool deleted = !reader.IsDBNull(4) && Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture) != 0;
            if (deleted)
                continue;

            string item = reader.IsDBNull(1) ? null : reader.GetString(1);
            string identity = reader.IsDBNull(2) ? null : reader.GetString(2);

            // New native TimeCode rows can be identity-only and legitimately have
            // no legacy Lampa hash, so retain a row when either key is present.
            if (string.IsNullOrWhiteSpace(item) && string.IsNullOrWhiteSpace(identity))
                continue;

            double percent = typedSchema
                ? ReadTypedPercent(reader.IsDBNull(3) ? null : reader.GetValue(3))
                : ReadLegacyPercent(reader.IsDBNull(3) ? null : reader.GetString(3));

            result.Add(item, new TimeCodeRow
            {
                Card = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                Identity = identity,
                Percent = percent
            });
        }

        return result;
    }

    static HashSet<string> TableColumns(SqliteConnection connection)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(timecodes);";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
                result.Add(reader.GetString(1));
        }

        return result;
    }

    static double ReadTypedPercent(object value)
    {
        if (value == null || value == DBNull.Value)
            return 0;

        try
        {
            double percent = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Math.Max(0, Math.Min(100, percent));
        }
        catch
        {
            return 0;
        }
    }

    static double ReadLegacyPercent(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return 0;

        try
        {
            var value = JObject.Parse(data);
            return Math.Max(0, Math.Min(100, value.Value<double?>("percent") ?? 0));
        }
        catch
        {
            return 0;
        }
    }

    static Dictionary<string, HashSet<string>> BuildFallbackOwners(
        IReadOnlyCollection<TranslationSubscription> subscriptions,
        TimeCodeRows rows)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (subscriptions == null || subscriptions.Count == 0 || rows == null || rows.ByHash.Count == 0)
            return result;

        foreach (var sub in subscriptions)
        {
            if (sub == null || string.IsNullOrWhiteSpace(sub.Id))
                continue;

            string contentIdentity = ContentIdentityKey(sub);
            var titles = TitleCandidates(sub);
            if (string.IsNullOrWhiteSpace(contentIdentity) || titles.Count == 0)
                continue;

            int season = Math.Max(1, sub.CurrentSeason.GetValueOrDefault(1));
            foreach (string title in titles)
            {
                for (int episode = 1; episode <= MaxEpisodeScan; episode++)
                {
                    string hash = LampaHash(BuildEpisodeHashInput(season, episode, title));
                    if (!rows.ByHash.ContainsKey(hash))
                        continue;

                    if (!result.TryGetValue(hash, out var owners))
                    {
                        owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result[hash] = owners;
                    }

                    owners.Add(contentIdentity);
                }
            }
        }

        return result;
    }

    static int FindWatchedEpisode(
        TranslationSubscription sub,
        int season,
        TimeCodeRows rows,
        IReadOnlyDictionary<string, HashSet<string>> fallbackOwners)
    {
        var titles = TitleCandidates(sub);
        var cards = CardCandidates(sub);
        string contentIdentity = ContentIdentityKey(sub);
        int scanTo = EpisodeScanLimit(sub);

        int watched = 0;
        bool foundAnyTimelineRow = false;

        for (int episode = 1; episode <= scanTo; episode++)
        {
            bool episodeWatched = false;

            // Current TimeCode main resolves web hashes to canonical TMDB identities
            // and native clients can write identity-only rows. Prefer that exact key.
            string identity = BuildEpisodeIdentity(sub, season, episode);
            if (!string.IsNullOrWhiteSpace(identity)
                && rows.ByIdentity.TryGetValue(identity, out var identityRows))
            {
                foundAnyTimelineRow = true;
                episodeWatched = identityRows.Any(x => x.Percent >= WatchedPercent);
            }

            if (!episodeWatched)
            {
                foreach (string title in titles)
                {
                    string hash = LampaHash(BuildEpisodeHashInput(season, episode, title));
                    if (!rows.ByHash.TryGetValue(hash, out var matchingRows))
                        continue;

                    foundAnyTimelineRow = true;

                    bool exactWatched = matchingRows.Any(x =>
                        x.Percent >= WatchedPercent && cards.Contains(x.Card));

                    // TimeCode card ids can change when the same show is opened from a
                    // different Lampa source. Keep the source-independent hash fallback,
                    // but only if this hash maps to one logical subscribed work. This
                    // prevents equal titles from leaking watched progress into each other.
                    bool hashWatched = IsUnambiguousFallback(hash, contentIdentity, fallbackOwners)
                        && matchingRows.Any(x => x.Percent >= WatchedPercent);

                    if (exactWatched || hashWatched)
                    {
                        episodeWatched = true;
                        break;
                    }
                }
            }

            if (episodeWatched)
                watched = episode;
        }

        return foundAnyTimelineRow ? watched : -1;
    }

    static string BuildEpisodeIdentity(TranslationSubscription sub, int season, int episode)
    {
        string tmdb = (sub?.TmdbId ?? string.Empty).Trim();
        if (!long.TryParse(tmdb, NumberStyles.None, CultureInfo.InvariantCulture, out long tmdbId) || tmdbId <= 0)
            return null;

        return "tv-" + tmdbId.ToString(CultureInfo.InvariantCulture)
            + "-s" + season.ToString(CultureInfo.InvariantCulture)
            + "e" + episode.ToString(CultureInfo.InvariantCulture);
    }

    static bool IsUnambiguousFallback(
        string hash,
        string contentIdentity,
        IReadOnlyDictionary<string, HashSet<string>> fallbackOwners)
    {
        if (string.IsNullOrWhiteSpace(hash)
            || string.IsNullOrWhiteSpace(contentIdentity)
            || fallbackOwners == null
            || !fallbackOwners.TryGetValue(hash, out var owners)
            || owners == null
            || owners.Count != 1)
            return false;

        return owners.Contains(contentIdentity);
    }

    static int EpisodeScanLimit(TranslationSubscription sub)
    {
        int available = sub == null ? 0 : sub.LastEpisode.GetValueOrDefault(0);
        int target = sub == null ? 0 : sub.TmdbTargetSeasonEpisodes.GetValueOrDefault(0);
        int scanTo = Math.Max(32, Math.Max(available + 8, target + 3));
        return Math.Min(MaxEpisodeScan, scanTo);
    }

    static string ContentIdentityKey(TranslationSubscription sub)
    {
        if (sub == null)
            return string.Empty;

        string tmdb = (sub.TmdbId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(tmdb))
            return "tmdb:" + tmdb.ToLowerInvariant();

        string imdb = (sub.ImdbId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(imdb))
            return "imdb:" + imdb.ToLowerInvariant();

        string kp = (sub.KpId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(kp))
            return "kp:" + kp.ToLowerInvariant();

        string content = (sub.ContentId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(content))
            return "content:" + content.ToLowerInvariant();

        string id = (sub.Id ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(id) ? string.Empty : "subscription:" + id;
    }

    static HashSet<string> CardCandidates(TranslationSubscription sub)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCard(result, sub.TmdbId);
        AddCard(result, sub.ContentId);
        AddCard(result, sub.KpId);
        AddCard(result, sub.ImdbId);

        return result;
    }

    static void AddCard(HashSet<string> result, string id)
    {
        id = (id ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
            return;

        result.Add(id.EndsWith("_tv", StringComparison.OrdinalIgnoreCase) ? id : id + "_tv");
    }

    static List<string> TitleCandidates(TranslationSubscription sub)
    {
        var result = new List<string>(2);

        string original = (sub.OriginalTitle ?? string.Empty).Trim();
        string title = (sub.Title ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(original))
            result.Add(original);

        if (!string.IsNullOrWhiteSpace(title)
            && !result.Contains(title, StringComparer.Ordinal))
            result.Add(title);

        return result;
    }

    static string BuildProgressCacheKey(TranslationSubscription sub, int season)
        => string.Join("|",
            ContentIdentityKey(sub),
            season.ToString(CultureInfo.InvariantCulture));

    static string BuildEpisodeHashInput(int season, int episode, string title)
        => season.ToString(CultureInfo.InvariantCulture)
            + (season > 10 ? ":" : string.Empty)
            + episode.ToString(CultureInfo.InvariantCulture)
            + title;

    // Exact equivalent of Lampa.Utils.hash.
    static string LampaHash(string input)
    {
        input ??= string.Empty;
        int hash = 0;

        unchecked
        {
            foreach (char ch in input)
                hash = ((hash << 5) - hash) + ch;
        }

        long absolute = hash == int.MinValue ? 2147483648L : Math.Abs((long)hash);
        return absolute.ToString(CultureInfo.InvariantCulture);
    }
}
