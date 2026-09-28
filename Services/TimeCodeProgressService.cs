using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TranslationSub.Models;

namespace TranslationSub.Services;

/// <summary>
/// Reads the progress written by Lampac's TimeCode module (database/TimeCode.sql)
/// and reconciles the profile-scoped TranslationSub progress projection.
///
/// Lampac TimeCode is the authoritative source for watched progress. Shared
/// TranslationSubscription objects must never be mutated with profile state.
/// </summary>
public static class TimeCodeProgressService
{
    const int WatchedPercent = 60;
    const int MaxEpisodeScan = 400;
    const string DatabasePath = "database/TimeCode.sql";

    sealed class TimeCodeRow
    {
        public string Card { get; init; }
        public int Percent { get; init; }
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

            // Reconciliation is allowed only after TimeCode was opened and read
            // successfully. An empty result is authoritative: this user/profile
            // currently has no persisted watched progress.
            var rows = LoadRows(timeCodeUser);
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
        string value = (uid ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        profileId = ProfileProgressStore.NormalizeProfileId(profileId);
        if (profileId != "0")
            value += "_" + profileId;

        // Keep this identical to Modules/Sync/TimeCode/Controller.cs#getUserid.
        return Regex.Replace(value, "[^a-z0-9\\-_\\.]+", string.Empty, RegexOptions.IgnoreCase);
    }

    static Dictionary<string, List<TimeCodeRow>> LoadRows(string user)
    {
        var result = new Dictionary<string, List<TimeCodeRow>>(StringComparer.Ordinal);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 2
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT card, item, data FROM timecodes WHERE user = $user";
        command.Parameters.AddWithValue("$user", user);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string item = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(item))
                continue;

            string data = reader.IsDBNull(2) ? null : reader.GetString(2);
            int percent = ReadPercent(data);

            if (!result.TryGetValue(item, out var list))
            {
                list = new List<TimeCodeRow>();
                result[item] = list;
            }

            list.Add(new TimeCodeRow
            {
                Card = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                Percent = percent
            });
        }

        return result;
    }

    static int ReadPercent(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return 0;

        try
        {
            var value = JObject.Parse(data);
            return Math.Max(0, Math.Min(100, value.Value<int?>("percent") ?? 0));
        }
        catch
        {
            return 0;
        }
    }

    static Dictionary<string, HashSet<string>> BuildFallbackOwners(
        IReadOnlyCollection<TranslationSubscription> subscriptions,
        IReadOnlyDictionary<string, List<TimeCodeRow>> rows)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (subscriptions == null || subscriptions.Count == 0 || rows == null || rows.Count == 0)
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
                    if (!rows.ContainsKey(hash))
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
        Dictionary<string, List<TimeCodeRow>> rows,
        IReadOnlyDictionary<string, HashSet<string>> fallbackOwners)
    {
        var titles = TitleCandidates(sub);
        if (titles.Count == 0)
            return -1;

        var cards = CardCandidates(sub);
        string contentIdentity = ContentIdentityKey(sub);
        int scanTo = EpisodeScanLimit(sub);

        int watched = 0;
        bool foundAnyTimelineRow = false;

        for (int episode = 1; episode <= scanTo; episode++)
        {
            bool episodeWatched = false;

            foreach (string title in titles)
            {
                string hash = LampaHash(BuildEpisodeHashInput(season, episode, title));
                if (!rows.TryGetValue(hash, out var matchingRows))
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

            if (episodeWatched)
                watched = episode;
        }

        return foundAnyTimelineRow ? watched : -1;
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
