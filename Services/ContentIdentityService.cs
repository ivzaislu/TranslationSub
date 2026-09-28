using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Models.Base;
using Shared.Services;
using Shared.Services.Utilities;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using TranslationSub.Models;

namespace TranslationSub.Services;

/// <summary>
/// Converts flexible Lampa full-card payloads into a canonical TranslationSub
/// content identity. The browser may provide hints, but serial/season/content
/// decisions belong to the backend.
/// </summary>
public static class ContentIdentityService
{
    public static async Task<TranslationSubResolvedContent> ResolveAsync(
        JObject payload,
        string uid = null,
        HttpContext httpContext = null)
    {
        payload ??= new JObject();

        JObject source = payload["payload"] as JObject ?? payload;
        JObject card = source["movie"] as JObject
            ?? source["card"] as JObject
            ?? source["data"] as JObject
            ?? source;

        string cardSource = Lower(Value(source, "source") ?? Value(card, "source", "card_source"));
        string explicitTmdbId = Value(card, "tmdb_id", "tmdbId");
        string cardId = Value(card, "id") ?? Value(source, "id");
        string tmdbId = explicitTmdbId;

        if (string.IsNullOrWhiteSpace(tmdbId)
            && (string.IsNullOrWhiteSpace(cardSource)
                || cardSource == "tmdb"
                || cardSource == "themoviedb"))
            tmdbId = cardId;

        var externalIds = card["external_ids"] as JObject;
        string kpId = Value(card, "kinopoisk_id", "kp_id", "kpId")
            ?? Value(externalIds, "kinopoisk_id", "kp_id");
        string imdbId = Value(card, "imdb_id", "imdbId")
            ?? Value(externalIds, "imdb_id");
        string title = Value(card, "title", "name") ?? Value(source, "title");
        string originalTitle = Value(card, "original_title", "original_name");
        string date = Value(card, "first_air_date", "release_date");
        string yearText = !string.IsNullOrWhiteSpace(date) && date.Length >= 4
            ? date.Substring(0, 4)
            : Value(card, "year");
        int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year);

        string poster = Value(card, "poster_path", "poster", "img", "image");
        bool isSerial = DetectSerial(source, card);
        int seasonHint = IntValue(source, "season");
        if (seasonHint <= 0)
            seasonHint = IntValue(card, "season");

        int season = isSerial ? LatestAiredSeason(card, seasonHint) : 0;

        // This mirrors the last frontend implementation before the backend move:
        // GET /externalids?id=<tmdb/content>&serial=1&imdb_id=...&kinopoisk_id=...
        // plus Lampac identity arguments (uid/account/nws) for the current request.
        if (isSerial
            && (string.IsNullOrWhiteSpace(tmdbId)
                || string.IsNullOrWhiteSpace(kpId)
                || string.IsNullOrWhiteSpace(imdbId)))
        {
            string externalId = FirstNonEmpty(tmdbId, kpId, imdbId, cardId, title);

            try
            {
                var ids = await ResolveExternalIds(
                    externalId,
                    imdbId,
                    kpId,
                    uid,
                    httpContext).ConfigureAwait(false);

                if (ids != null)
                {
                    string resolvedKp = Value(ids, "kinopoisk_id", "kp_id", "kpId");
                    string resolvedImdb = Value(ids, "imdb_id", "imdbId");
                    string resolvedTmdb = Value(ids, "tmdb_id", "tmdbId");

                    if (!string.IsNullOrWhiteSpace(resolvedKp))
                        kpId = resolvedKp;
                    if (!string.IsNullOrWhiteSpace(resolvedImdb))
                        imdbId = resolvedImdb;
                    if (!string.IsNullOrWhiteSpace(resolvedTmdb))
                        tmdbId = resolvedTmdb;
                }
            }
            catch
            {
                // External-id enrichment is optional; keep the card usable when unavailable.
            }
        }

        if (isSerial && (!string.IsNullOrWhiteSpace(tmdbId) || !string.IsNullOrWhiteSpace(imdbId)))
        {
            try
            {
                var tmdb = await TmdbScheduleService.Get(tmdbId, imdbId).ConfigureAwait(false);
                if (tmdb != null)
                {
                    if (!string.IsNullOrWhiteSpace(tmdb.TmdbId))
                        tmdbId = tmdb.TmdbId;
                    if (!string.IsNullOrWhiteSpace(tmdb.ImdbId))
                        imdbId = tmdb.ImdbId;
                    if (tmdb.LastSeason > 0)
                        season = tmdb.LastSeason;
                }
            }
            catch
            {
                // Identity resolution must keep working when TMDB is unavailable.
            }
        }

        string contentId = Value(card, "content_id", "contentId");
        if (!string.IsNullOrWhiteSpace(tmdbId))
            contentId = tmdbId;
        else if (string.IsNullOrWhiteSpace(contentId))
            contentId = FirstNonEmpty(kpId, imdbId, cardId, title);

        return new TranslationSubResolvedContent
        {
            ContentId = Clean(contentId),
            Title = Clean(title),
            OriginalTitle = Clean(originalTitle),
            TmdbId = Clean(tmdbId),
            ImdbId = Clean(imdbId),
            KpId = Clean(kpId),
            Poster = Clean(poster),
            Year = year > 0 ? year : null,
            IsSerial = isSerial,
            Season = isSerial ? Math.Max(1, season) : 0
        };
    }

    static async Task<JObject> ResolveExternalIds(
        string id,
        string imdbId,
        string kpId,
        string uid,
        HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var listen = CoreInit.conf?.listen;
        if (listen == null || listen.port <= 0)
            return null;

        string route = "/externalids";
        route = LampacMetadataClient.AppendQuery(route, "id", id.Trim());
        route = LampacMetadataClient.AppendQuery(route, "serial", "1");
        if (!string.IsNullOrWhiteSpace(imdbId))
            route = LampacMetadataClient.AppendQuery(route, "imdb_id", imdbId.Trim());
        if (!string.IsNullOrWhiteSpace(kpId))
            route = LampacMetadataClient.AppendQuery(route, "kinopoisk_id", kpId.Trim());

        // The old browser request sent uid/account_email/nws_id. For interactive
        // backend calls use Lampac's native request identity expansion; keep uid
        // as a fallback for callers without HttpContext.
        if (httpContext != null)
            route = AccsDbInvk.Args(route, httpContext);
        else if (!string.IsNullOrWhiteSpace(uid))
            route = LampacMetadataClient.AppendQuery(route, "uid", uid.Trim());

        string localHost = string.IsNullOrWhiteSpace(listen.localhost)
            ? "127.0.0.1"
            : listen.localhost.Trim();
        if (localHost.Contains(':') && !localHost.StartsWith('['))
            localHost = $"[{localHost}]";

        var uri = new Uri(new Uri($"http://{localHost}:{listen.port}/"), route.TrimStart('/'));

        string xhost;
        if (httpContext != null)
            xhost = CoreInit.Host(httpContext);
        else if (!string.IsNullOrWhiteSpace(listen.host))
            xhost = listen.host.Trim();
        else
            xhost = $"http://{localHost}:{listen.port}";

        string xscheme = !string.IsNullOrWhiteSpace(httpContext?.Request?.Scheme)
            ? httpContext.Request.Scheme
            : (string.IsNullOrWhiteSpace(listen.scheme) ? "http" : listen.scheme.Trim());

        var headers = HeadersModel.Init(
            ("xhost", xhost),
            ("xscheme", xscheme),
            ("lcrqpasswd", CoreInit.rootPasswd)
        );

        string body = await Http.Get(
            uri.ToString(),
            timeoutSeconds: 15,
            headers: headers,
            statusCodeOK: true,
            weblog: false
        ).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JObject.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    static bool DetectSerial(JObject source, JObject card)
    {
        string method = Lower(Value(source, "method") ?? Value(card, "method"));
        string mediaType = Lower(Value(source, "media_type", "type") ?? Value(card, "media_type", "type"));

        bool explicitMovie = method == "movie" || method == "film"
            || mediaType == "movie" || mediaType == "film";
        bool explicitTv = method == "tv" || method == "serial"
            || mediaType == "tv" || mediaType == "serial" || mediaType == "series";

        if (explicitTv)
            return true;

        if (Truthy(source["serial"]) || Truthy(card["serial"])
            || Truthy(source["is_serial"]) || Truthy(card["is_serial"])
            || Truthy(card["isSerial"]))
            return true;

        if (IntValue(source, "season") > 0 || IntValue(card, "season") > 0)
            return true;

        if (HasSeasonData(card))
            return true;

        if (!string.IsNullOrWhiteSpace(Value(card, "original_name"))
            && string.IsNullOrWhiteSpace(Value(card, "original_title")))
            return true;

        if (explicitMovie)
            return false;

        if (!string.IsNullOrWhiteSpace(Value(card, "release_date", "original_title")))
            return false;

        return false;
    }

    static bool HasSeasonData(JObject card)
    {
        if (IntValue(card, "number_of_seasons", "seasons_count") > 0)
            return true;
        if (IntValue(card, "number_of_episodes", "episodes_count") > 0)
            return true;
        if (!string.IsNullOrWhiteSpace(Value(card, "first_air_date", "last_air_date")))
            return true;
        if (card["last_episode_to_air"] is JObject || card["next_episode_to_air"] is JObject)
            return true;
        if (card["episode_run_time"] is JArray runtime && runtime.Count > 0)
            return true;

        if (card["seasons"] is JArray seasons)
        {
            foreach (var token in seasons.OfType<JObject>())
            {
                if (IntValue(token, "season_number", "number") > 0)
                    return true;
            }
        }

        return false;
    }

    static int LatestAiredSeason(JObject card, int seasonHint)
    {
        if (card["last_episode_to_air"] is JObject lastEpisode)
        {
            int season = IntValue(lastEpisode, "season_number", "season");
            if (season > 0)
                return season;
        }

        int bestAired = 0;
        int bestKnown = 0;

        if (card["seasons"] is JArray seasons)
        {
            foreach (var item in seasons.OfType<JObject>())
            {
                int number = IntValue(item, "season_number", "number");
                if (number <= 0)
                    continue;

                bestKnown = Math.Max(bestKnown, number);
                if (DateHasAired(Value(item, "air_date")))
                    bestAired = Math.Max(bestAired, number);
            }
        }

        if (bestAired > 0)
            return bestAired;

        if (card["next_episode_to_air"] is JObject nextEpisode)
        {
            int nextSeason = IntValue(nextEpisode, "season_number", "season");
            int nextNumber = IntValue(nextEpisode, "episode_number", "episode");
            if (nextSeason > 0)
            {
                if (nextNumber > 1)
                    return nextSeason;
                if (nextSeason > 1)
                    return nextSeason - 1;
            }
        }

        if (bestKnown > 0)
            return bestKnown;

        int total = IntValue(card, "number_of_seasons", "seasons_count");
        if (total > 0)
            return total;

        if (seasonHint > 0)
            return seasonHint;

        return 1;
    }

    static bool DateHasAired(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset date)
            && date <= DateTimeOffset.Now;
    }

    static bool Truthy(JToken value)
    {
        if (value == null || value.Type == JTokenType.Null)
            return false;
        if (value.Type == JTokenType.Boolean)
            return value.Value<bool>();
        if (value.Type == JTokenType.Integer)
            return value.Value<long>() == 1;

        string text = value.ToString().Trim();
        return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    static int IntValue(JObject value, params string[] names)
    {
        string text = Value(value, names);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    static string Value(JObject value, params string[] names)
    {
        if (value == null || names == null)
            return null;

        foreach (string name in names)
        {
            var token = value[name];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Object || token.Type == JTokenType.Array)
                continue;

            string text = token.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    static string FirstNonEmpty(params string[] values)
        => values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    static string Clean(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static string Lower(string value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();
}
