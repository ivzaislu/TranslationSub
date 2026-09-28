using Newtonsoft.Json.Linq;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace TranslationSub.Services;

public class TmdbScheduleSnapshot
{
    public string TmdbId { get; set; }
    public string ImdbId { get; set; }
    public string Status { get; set; }
    public int LastSeason { get; set; }
    public int LastEpisode { get; set; }
    public DateTime? LastAirDate { get; set; }
    public int NextSeason { get; set; }
    public int NextEpisode { get; set; }
    public DateTime? NextAirDate { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.Now;
    public Dictionary<int, int> SeasonEpisodeCounts { get; set; } = new();
}

public static class TmdbScheduleService
{
    static readonly object cacheLock = new();
    static readonly Dictionary<string, (TmdbScheduleSnapshot value, DateTime fetchedAt)> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan cacheLifetime = TimeSpan.FromMinutes(30);
    static readonly TimeSpan slowFailureThreshold = TimeSpan.FromSeconds(10);
    static readonly TimeSpan slowFailureBackoff = TimeSpan.FromSeconds(5);
    const int slowFailureTripCount = 3;
    static int consecutiveSlowFailures;
    static DateTime slowFailureBackoffUntilUtc;

    public static async Task<TmdbScheduleSnapshot> Get(string tmdbId, string expectedImdbId = null)
    {
        expectedImdbId = NormalizeImdb(expectedImdbId);
        TmdbScheduleSnapshot result = null;

        if (long.TryParse(tmdbId, out long id) && id > 0)
            result = await GetById(id).ConfigureAwait(false);

        // A CUB card may expose its own numeric card.id. If we accidentally
        // received it as TmdbId, external_ids lets us reject it safely.
        if (result != null && !string.IsNullOrWhiteSpace(expectedImdbId))
        {
            string actualImdb = NormalizeImdb(result.ImdbId);
            if (!string.IsNullOrWhiteSpace(actualImdb)
                && !string.Equals(actualImdb, expectedImdbId, StringComparison.OrdinalIgnoreCase))
                result = null;
        }

        if (result == null && !string.IsNullOrWhiteSpace(expectedImdbId))
        {
            long resolvedId = await FindTvIdByImdb(expectedImdbId).ConfigureAwait(false);
            if (resolvedId > 0)
                result = await GetById(resolvedId).ConfigureAwait(false);
        }

        return result;
    }

    static async Task<TmdbScheduleSnapshot> GetById(long id)
    {
        string normalizedId = id.ToString();
        lock (cacheLock)
        {
            if (cache.TryGetValue(normalizedId, out var cached)
                && DateTime.Now - cached.fetchedAt < cacheLifetime)
                return cached.value;
        }

        if (SlowFailureBackoffActive())
            return null;

        string host = (ModInit.conf?.tmdb_apihost ?? "https://api.themoviedb.org/3").TrimEnd('/');
        string key = ModInit.conf?.tmdb_apikey;
        if (string.IsNullOrWhiteSpace(key))
            return null;

        DateTime startedAtUtc = DateTime.UtcNow;
        try
        {
            string url = $"{host}/tv/{id}?api_key={Uri.EscapeDataString(key)}&language=ru-RU&append_to_response=external_ids";
            string json = await Http.Get(url, timeoutSeconds: 12).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                RecordSlowFailure(startedAtUtc);
                return null;
            }

            var root = JObject.Parse(json);
            var result = new TmdbScheduleSnapshot
            {
                TmdbId = normalizedId,
                ImdbId = root["external_ids"]?.Value<string>("imdb_id"),
                Status = root.Value<string>("status") ?? string.Empty,
                SyncedAt = DateTime.Now
            };

            var last = root["last_episode_to_air"] as JObject;
            if (last != null)
            {
                result.LastSeason = last.Value<int?>("season_number") ?? 0;
                result.LastEpisode = last.Value<int?>("episode_number") ?? 0;
                result.LastAirDate = ParseDate(last.Value<string>("air_date"));
            }
            else
            {
                result.LastAirDate = ParseDate(root.Value<string>("last_air_date"));
            }

            var next = root["next_episode_to_air"] as JObject;
            if (next != null)
            {
                result.NextSeason = next.Value<int?>("season_number") ?? 0;
                result.NextEpisode = next.Value<int?>("episode_number") ?? 0;
                result.NextAirDate = ParseDate(next.Value<string>("air_date"));
            }

            var seasons = root["seasons"] as JArray;
            if (seasons != null)
            {
                foreach (var token in seasons)
                {
                    var season = token as JObject;
                    if (season == null)
                        continue;

                    int number = season.Value<int?>("season_number") ?? 0;
                    int count = season.Value<int?>("episode_count") ?? 0;
                    if (number > 0 && count >= 0)
                        result.SeasonEpisodeCounts[number] = count;
                }
            }

            lock (cacheLock)
                cache[normalizedId] = (result, DateTime.Now);

            RecordSuccess();
            return result;
        }
        catch
        {
            RecordSlowFailure(startedAtUtc);
            return null;
        }
    }

    static async Task<long> FindTvIdByImdb(string imdbId)
    {
        if (SlowFailureBackoffActive())
            return 0;

        string host = (ModInit.conf?.tmdb_apihost ?? "https://api.themoviedb.org/3").TrimEnd('/');
        string key = ModInit.conf?.tmdb_apikey;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(imdbId))
            return 0;

        DateTime startedAtUtc = DateTime.UtcNow;
        try
        {
            string url = $"{host}/find/{Uri.EscapeDataString(imdbId)}?api_key={Uri.EscapeDataString(key)}&external_source=imdb_id&language=ru-RU";
            string json = await Http.Get(url, timeoutSeconds: 12).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                RecordSlowFailure(startedAtUtc);
                return 0;
            }

            var root = JObject.Parse(json);
            var tv = root["tv_results"] as JArray;
            RecordSuccess();
            if (tv == null || tv.Count == 0)
                return 0;

            return tv[0]?.Value<long?>("id") ?? 0;
        }
        catch
        {
            RecordSlowFailure(startedAtUtc);
            return 0;
        }
    }

    static bool SlowFailureBackoffActive()
    {
        lock (cacheLock)
        {
            if (slowFailureBackoffUntilUtc > DateTime.UtcNow)
                return true;

            if (slowFailureBackoffUntilUtc != default)
                slowFailureBackoffUntilUtc = default;

            return false;
        }
    }

    static void RecordSlowFailure(DateTime startedAtUtc)
    {
        if (DateTime.UtcNow - startedAtUtc < slowFailureThreshold)
            return;

        lock (cacheLock)
        {
            consecutiveSlowFailures++;
            if (consecutiveSlowFailures < slowFailureTripCount)
                return;

            consecutiveSlowFailures = 0;
            slowFailureBackoffUntilUtc = DateTime.UtcNow + slowFailureBackoff;
        }
    }

    static void RecordSuccess()
    {
        lock (cacheLock)
        {
            consecutiveSlowFailures = 0;
            slowFailureBackoffUntilUtc = default;
        }
    }

    static string NormalizeImdb(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (!value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return value.ToLowerInvariant();
    }

    static DateTime? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out DateTime exact))
            return exact.Date;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
            return parsed.Date;

        return null;
    }
}
