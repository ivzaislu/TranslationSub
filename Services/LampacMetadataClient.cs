using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Models.Base;
using Shared.Services;
using Shared.Services.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TranslationSub.Models;

namespace TranslationSub.Services;

internal sealed class LampacSourceDescriptor
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Url { get; init; }
}

internal static class LampacMetadataClient
{
    const int MaxMetadataDepth = 4;
    const int MaxMetadataPages = 64;

    // Compatibility fallback for online modules that ignore rjson=true.
    // Normal metadata traversal uses the common Lampac JSON template contract.
    static readonly Regex onlineElementRegex = new(
        @"<(?<tag>[a-zA-Z0-9]+)\b(?<attrs>[^>]*videos__(?:item|button)[^>]*)>(?<inner>.*?)</\k<tag>>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    static readonly Regex attributeRegex = new(
        @"(?<name>[\w:-]+)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex tagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    static readonly Regex seasonTextRegex = new(
        @"(?:season|сезон)\D{0,8}(?<n>\d{1,3})|(?<n2>\d{1,3})\D{0,8}(?:season|сезон)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<IReadOnlyList<LampacVoiceMetadata>> ReadAsync(TranslationMetadataQuery query, HttpContext httpContext = null)
    {
        if (query == null || query.Sources == null || query.Sources.Count == 0)
            return Array.Empty<LampacVoiceMetadata>();

        // Do not call /lite/events here. TranslationSub resolves only the user's
        // selected Lampac modules and then requests metadata from those routes.
        // This avoids OnlineApi.checkSearch() fan-out across unrelated balancers.
        var selected = LampacSourceRegistry.ResolveSelected(query.Sources);
        if (selected.Count == 0)
        {
            Serilog.Log.Warning(
                "TranslationSub metadata has no resolvable selected balancers. Selected={Selected}",
                string.Join(",", query.Sources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
            return Array.Empty<LampacVoiceMetadata>();
        }

        // Match Lampac's per-balancer failure isolation: one broken source must not
        // turn a successful response from every other source into an empty result.
        var values = await Task.WhenAll(selected.Select(async source =>
        {
            try
            {
                return await ReadSourceAsync(source, query, httpContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "TranslationSub metadata source failed. Source={Source}", source.Id);
                return new List<LampacVoiceMetadata>();
            }
        })).ConfigureAwait(false);

        return values
            .SelectMany(x => x)
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.VoiceName))
            .OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Season)
            .ThenBy(x => x.VoiceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string TmdbSourceId(TranslationMetadataQuery query)
    {
        if (query != null && long.TryParse(query.TmdbId, out long tmdbId) && tmdbId > 0)
            return tmdbId.ToString();

        return query?.ContentId?.Trim();
    }

    static async Task<List<LampacVoiceMetadata>> ReadSourceAsync(LampacSourceDescriptor source, TranslationMetadataQuery query, HttpContext httpContext)
    {
        var rows = new List<MetadataRow>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Uri trustedExternalOrigin = TrustedExternalOrigin(source.Url, httpContext);

        await TraverseAsync(
            source,
            query,
            BuildSourceRequest(source.Url, query),
            null,
            null,
            query.Season,
            0,
            visited,
            rows,
            httpContext,
            trustedExternalOrigin).ConfigureAwait(false);

        if (rows.Count == 0)
            Serilog.Log.Warning("TranslationSub metadata source produced no episode rows. Source={Source}; Season={Season}", source.Id, query.Season);

        return rows
            .Where(x => x.Episode > 0)
            .Where(x => !query.IsSerial || query.Season <= 0 || x.Season == query.Season)
            .GroupBy(x => new
            {
                season = x.Season,
                voice = string.IsNullOrWhiteSpace(x.VoiceId)
                    ? StableMetadataVoiceId(x.VoiceName, source.Id)
                    : x.VoiceId
            })
            .Select(group => new LampacVoiceMetadata
            {
                Source = source.Id,
                VoiceId = group.Key.voice,
                VoiceName = group.Select(x => x.VoiceName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                Season = group.Key.season,
                Episodes = group.Select(x => x.Episode).Where(x => x > 0).Distinct().OrderBy(x => x).ToList()
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.VoiceName) && x.Episodes.Count > 0)
            .ToList();
    }

    static string BuildSourceRequest(string sourceUrl, TranslationMetadataQuery query)
    {
        string url = ForceMetadataJson(sourceUrl);

        string sourceId = TmdbSourceId(query);
        if (!string.IsNullOrWhiteSpace(sourceId))
            url = AppendQuery(url, "id", sourceId);
        if (query.KpId > 0)
            url = AppendQuery(url, "kinopoisk_id", query.KpId.ToString());
        if (long.TryParse(query.TmdbId, out long tmdbId) && tmdbId > 0)
            url = AppendQuery(url, "tmdb_id", tmdbId.ToString());
        if (!string.IsNullOrWhiteSpace(query.ImdbId))
            url = AppendQuery(url, "imdb_id", query.ImdbId);
        if (!string.IsNullOrWhiteSpace(query.Title))
            url = AppendQuery(url, "title", query.Title);
        if (!string.IsNullOrWhiteSpace(query.OriginalTitle))
            url = AppendQuery(url, "original_title", query.OriginalTitle);
        if (query.Year > 0)
            url = AppendQuery(url, "year", query.Year.ToString());

        url = AppendQuery(url, "serial", query.IsSerial ? "1" : "0");
        url = AppendQuery(url, "source", "tmdb");
        return url;
    }

    static async Task TraverseAsync(
        LampacSourceDescriptor source,
        TranslationMetadataQuery query,
        string url,
        string inheritedVoiceName,
        string inheritedVoiceId,
        int inheritedSeason,
        int depth,
        HashSet<string> visited,
        List<MetadataRow> rows,
        HttpContext httpContext,
        Uri trustedExternalOrigin)
    {
        if (depth > MaxMetadataDepth || visited.Count >= MaxMetadataPages || string.IsNullOrWhiteSpace(url))
            return;

        url = ForceMetadataJson(url);
        if (!visited.Add(url.Trim()))
            return;

        string body = await GetAsync(url, query.Uid, httpContext, trustedExternalOrigin).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            Serilog.Log.Warning(
                "TranslationSub metadata HTTP returned no body. Source={Source}; Depth={Depth}; Route={Route}",
                source.Id, depth, SafeRoute(url));
            return;
        }

        var nodes = ParseMetadataNodes(body);
        if (nodes.Count == 0)
        {
            Serilog.Log.Warning(
                "TranslationSub metadata parser found no nodes. Source={Source}; Depth={Depth}; Route={Route}; Length={Length}",
                source.Id, depth, SafeRoute(url), body.Length);
            return;
        }

        var voices = nodes.Where(x => x.Kind == "voice" || x.Kind == "button").ToList();
        var activeVoice = voices.FirstOrDefault(x => x.Active);
        if (activeVoice == null && string.IsNullOrWhiteSpace(inheritedVoiceName))
            activeVoice = voices.FirstOrDefault();

        string pageVoiceName = inheritedVoiceName;
        string pageVoiceId = inheritedVoiceId;
        if (activeVoice != null)
        {
            pageVoiceName = activeVoice.VoiceName ?? activeVoice.Text ?? pageVoiceName;
            pageVoiceId = activeVoice.VoiceId ?? pageVoiceId;
        }
        if (!string.IsNullOrWhiteSpace(pageVoiceName) && string.IsNullOrWhiteSpace(pageVoiceId))
            pageVoiceId = StableMetadataVoiceId(pageVoiceName, source.Id);

        var follow = new List<FollowNode>();
        int terminalOrdinal = 0;

        foreach (var node in nodes)
        {
            string voiceName = !string.IsNullOrWhiteSpace(node.VoiceName) ? node.VoiceName : pageVoiceName;
            string voiceId = !string.IsNullOrWhiteSpace(node.VoiceId) ? node.VoiceId : pageVoiceId;
            int season = node.Season > 0 ? node.Season : inheritedSeason;

            if (node.Kind == "voice" || node.Kind == "button")
            {
                voiceName = node.VoiceName ?? node.Text ?? voiceName;
                if (string.IsNullOrWhiteSpace(voiceId))
                    voiceId = StableMetadataVoiceId(voiceName, source.Id);

                // EpisodeTpl.data already describes the current voice. When Lampac
                // marks no voice active, the first voice is the page voice fallback.
                if (!string.IsNullOrWhiteSpace(node.Url) && !ReferenceEquals(node, activeVoice))
                {
                    follow.Add(new FollowNode
                    {
                        Url = ResolveMetadataLink(url, node.Url),
                        VoiceName = voiceName,
                        VoiceId = voiceId,
                        Season = season
                    });
                }
                continue;
            }

            bool terminal = node.Episode > 0
                || node.Kind is "episode" or "movie"
                || string.Equals(node.Method, "play", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Method, "call", StringComparison.OrdinalIgnoreCase);

            if (terminal)
            {
                terminalOrdinal++;
                int episode = query.IsSerial
                    ? (node.Episode > 0 ? node.Episode : terminalOrdinal)
                    : 1;

                if (!query.IsSerial)
                {
                    season = 0;
                    if (string.IsNullOrWhiteSpace(voiceName))
                        voiceName = node.Text;
                }
                else if (season <= 0 && query.Season > 0)
                {
                    season = query.Season;
                }

                if (string.IsNullOrWhiteSpace(voiceName))
                    voiceName = "Неизвестно";
                if (string.IsNullOrWhiteSpace(voiceId))
                    voiceId = StableMetadataVoiceId(voiceName, source.Id);

                rows.Add(new MetadataRow
                {
                    VoiceName = voiceName.Trim(),
                    VoiceId = voiceId,
                    Season = season,
                    Episode = episode
                });

                // Playback boundary: terminal items are recorded, never followed.
                continue;
            }

            if (node.Similar || string.IsNullOrWhiteSpace(node.Url) || !IsMetadataNavigation(node))
                continue;

            int nextSeason = node.Season > 0 ? node.Season : SeasonFromText(node.Text);
            if (nextSeason <= 0)
                nextSeason = season;

            if (query.IsSerial && query.Season > 0 && nextSeason > 0 && nextSeason != query.Season)
                continue;

            follow.Add(new FollowNode
            {
                Url = ResolveMetadataLink(url, node.Url),
                VoiceName = voiceName,
                VoiceId = voiceId,
                Season = nextSeason
            });
        }

        foreach (var next in follow.Where(x => !string.IsNullOrWhiteSpace(x.Url)))
        {
            if (visited.Count >= MaxMetadataPages)
                break;

            await TraverseAsync(
                source,
                query,
                next.Url,
                next.VoiceName,
                next.VoiceId,
                next.Season,
                depth + 1,
                visited,
                rows,
                httpContext,
                trustedExternalOrigin).ConfigureAwait(false);
        }
    }

    static bool IsMetadataNavigation(OnlineNode node)
    {
        if (node == null)
            return false;
        if (node.Kind is "season" or "voice" or "button")
            return true;
        return string.Equals(node.Method, "link", StringComparison.OrdinalIgnoreCase);
    }

    static List<OnlineNode> ParseMetadataNodes(string body)
    {
        var result = new List<OnlineNode>();
        if (string.IsNullOrWhiteSpace(body))
            return result;

        // rjson=true is the primary protocol because all common Lampac templates
        // serialize season/voice/episode/movie metadata through this shape.
        try
        {
            JToken root = JToken.Parse(body);
            AddJsonNodes(root, null, null, 0, null, result);
            if (result.Count > 0)
                return result;
        }
        catch
        {
        }

        return ParseHtmlNodes(body);
    }

    static void AddJsonNodes(
        JToken token,
        string inheritedVoiceName,
        string inheritedVoiceId,
        int inheritedSeason,
        string contextualKind,
        List<OnlineNode> result)
    {
        if (token == null)
            return;

        if (token is JArray array)
        {
            foreach (var child in array)
                AddJsonNodes(child, inheritedVoiceName, inheritedVoiceId, inheritedSeason, contextualKind, result);
            return;
        }

        if (token is not JObject obj)
            return;

        string responseType = NormalizeNodeKind(ReadJsonString(obj, "type"));
        if (responseType != null && (obj["data"] != null || obj["voice"] != null))
        {
            AddJsonNodes(obj["voice"], inheritedVoiceName, inheritedVoiceId, inheritedSeason, "voice", result);
            AddJsonNodes(obj["data"], inheritedVoiceName, inheritedVoiceId, inheritedSeason, responseType, result);
            return;
        }

        string kind = NormalizeNodeKind(contextualKind) ?? responseType;
        if (kind == null)
        {
            foreach (var property in obj.Properties())
            {
                string childKind = property.Name.Equals("voice", StringComparison.OrdinalIgnoreCase)
                    ? "voice"
                    : null;
                AddJsonNodes(property.Value, inheritedVoiceName, inheritedVoiceId, inheritedSeason, childKind, result);
            }
            return;
        }

        string text = ReadJsonString(obj, "name", "title", "translate", "text");
        string voiceName = ReadJsonString(obj, "voice_name", "voiceName", "details", "translation", "voice") ?? inheritedVoiceName;
        string voiceId = ReadJsonString(obj, "voice_id", "voiceId", "translation_id", "translationId") ?? inheritedVoiceId;
        int season = ReadJsonInt(obj, "s", "season", "season_number");
        int episode = ReadJsonInt(obj, "e", "episode", "episode_number");

        if (kind == "season" && season <= 0)
            season = ReadJsonInt(obj, "id");
        if (season <= 0)
            season = inheritedSeason;

        if (kind == "voice")
        {
            voiceName = text ?? voiceName;
            if (string.IsNullOrWhiteSpace(voiceId))
                voiceId = VoiceNormalize.Normalize(voiceName);
        }
        else if (kind == "movie" && string.IsNullOrWhiteSpace(voiceName))
        {
            voiceName = ReadJsonString(obj, "voice_name", "details", "translate") ?? text;
        }

        result.Add(new OnlineNode
        {
            Kind = kind,
            Text = text,
            Url = ReadJsonString(obj, "url", "link"),
            Method = ReadJsonString(obj, "method"),
            Season = season,
            Episode = episode,
            VoiceName = voiceName,
            VoiceId = voiceId,
            Similar = ReadJsonBool(obj, "similar"),
            Active = ReadJsonBool(obj, "active")
        });
    }

    static string NormalizeNodeKind(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string kind = value.Trim().ToLowerInvariant();
        return kind is "season" or "voice" or "episode" or "movie" ? kind : null;
    }

    static List<OnlineNode> ParseHtmlNodes(string html)
    {
        var result = new List<OnlineNode>();
        if (string.IsNullOrWhiteSpace(html))
            return result;

        foreach (Match match in onlineElementRegex.Matches(html))
        {
            var attrs = ParseAttributes(match.Groups["attrs"].Value);
            string classes = ReadAttribute(attrs, "class") ?? string.Empty;
            string kind = classes.IndexOf("videos__button", StringComparison.OrdinalIgnoreCase) >= 0
                ? "button"
                : "item";

            JObject data = null;
            string rawJson = ReadAttribute(attrs, "data-json");
            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                try
                {
                    data = JObject.Parse(WebUtility.HtmlDecode(rawJson));
                }
                catch
                {
                }
            }

            string text = WebUtility.HtmlDecode(tagRegex.Replace(match.Groups["inner"].Value, " "));
            text = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

            int season = ReadInt(ReadAttribute(attrs, "s"));
            if (season <= 0)
                season = ReadJsonInt(data, "s", "season", "season_number");

            int episode = ReadInt(ReadAttribute(attrs, "e"));
            if (episode <= 0)
                episode = ReadJsonInt(data, "e", "episode", "episode_number");

            string voiceName = ReadJsonString(data, "voice_name", "voiceName", "details", "translation", "voice");
            string voiceId = ReadJsonString(data, "voice_id", "voiceId", "translation_id", "translationId");
            if (kind == "button" && string.IsNullOrWhiteSpace(voiceName))
                voiceName = text;

            string semanticKind = NormalizeNodeKind(ReadJsonString(data, "type")) ?? kind;

            result.Add(new OnlineNode
            {
                Kind = semanticKind,
                Text = text,
                Url = ReadJsonString(data, "url", "link") ?? ReadAttribute(attrs, "href"),
                Method = ReadJsonString(data, "method"),
                Season = season,
                Episode = episode,
                VoiceName = voiceName,
                VoiceId = voiceId,
                Similar = ReadJsonBool(data, "similar"),
                Active = HasCssClass(classes, "active") || ReadJsonBool(data, "active")
            });
        }

        return result;
    }

    static Dictionary<string, string> ParseAttributes(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in attributeRegex.Matches(raw ?? string.Empty))
        {
            string value = match.Groups["dq"].Success
                ? match.Groups["dq"].Value
                : match.Groups["sq"].Value;
            result[match.Groups["name"].Value] = value;
        }
        return result;
    }

    static bool HasCssClass(string classes, string name)
        => (classes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    static string ResolveMetadataLink(string currentUrl, string nextUrl)
    {
        if (string.IsNullOrWhiteSpace(nextUrl))
            return null;

        nextUrl = WebUtility.HtmlDecode(nextUrl.Trim());

        if (TryCreateHttpUri(nextUrl, out var absoluteNext))
            return ForceMetadataJson(absoluteNext.ToString());

        if (nextUrl.StartsWith("//", StringComparison.Ordinal))
        {
            string scheme = TryCreateHttpUri(currentUrl, out var currentAbsolute)
                ? currentAbsolute.Scheme
                : Uri.UriSchemeHttp;
            return ForceMetadataJson(scheme + ":" + nextUrl);
        }

        if (nextUrl.StartsWith('/'))
        {
            if (TryCreateHttpUri(currentUrl, out var currentAbsolute))
                return ForceMetadataJson(new Uri(currentAbsolute, nextUrl).ToString());

            return ForceMetadataJson(nextUrl);
        }

        try
        {
            Uri current = TryCreateHttpUri(currentUrl, out var absoluteCurrent)
                ? absoluteCurrent
                : new Uri(new Uri("http://lampac.local/"), currentUrl.TrimStart('/'));
            Uri resolved = new Uri(current, nextUrl);
            string value = resolved.Host.Equals("lampac.local", StringComparison.OrdinalIgnoreCase)
                ? resolved.PathAndQuery
                : resolved.ToString();
            return ForceMetadataJson(value);
        }
        catch
        {
            return ForceMetadataJson(nextUrl);
        }
    }

    static int SeasonFromText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        Match match = seasonTextRegex.Match(value);
        if (!match.Success)
            return 0;

        string raw = match.Groups["n"].Success ? match.Groups["n"].Value : match.Groups["n2"].Value;
        return int.TryParse(raw, out int season) && season > 0 ? season : 0;
    }

    static string StableMetadataVoiceId(string voiceName, string sourceId)
    {
        string normalized = VoiceNormalize.Normalize(voiceName);
        if (!string.IsNullOrWhiteSpace(normalized)
            && !string.Equals(voiceName, "Неизвестно", StringComparison.OrdinalIgnoreCase))
            return normalized;
        return (sourceId ?? "lampac") + ":default";
    }

    static string ReadAttribute(Dictionary<string, string> attrs, string name)
        => attrs != null && attrs.TryGetValue(name, out var value) ? value : null;

    static int ReadInt(string value)
        => int.TryParse(value, out int parsed) && parsed > 0 ? parsed : 0;

    static int ReadJsonInt(JObject value, params string[] names)
    {
        if (value == null)
            return 0;

        foreach (string name in names)
        {
            JToken token = value[name];
            if (token != null && int.TryParse(token.ToString(), out int parsed) && parsed > 0)
                return parsed;
        }
        return 0;
    }

    static string ReadJsonString(JObject value, params string[] names)
    {
        if (value == null)
            return null;

        foreach (string name in names)
        {
            JToken token = value[name];
            if (token == null || token.Type is JTokenType.Object or JTokenType.Array or JTokenType.Null)
                continue;

            string text = token.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }
        return null;
    }

    static bool ReadJsonBool(JObject value, string name)
    {
        if (value == null || value[name] == null)
            return false;

        JToken token = value[name];
        if (token.Type == JTokenType.Boolean)
            return token.Value<bool>();
        return bool.TryParse(token.ToString(), out bool result) && result;
    }

    static Uri TrustedExternalOrigin(string sourceUrl, HttpContext httpContext)
    {
        Uri uri = ResolveRequestUri(sourceUrl, httpContext, out bool localRoute);
        return uri != null && !localRoute ? uri : null;
    }

    static bool SameOrigin(Uri uri, Uri trustedOrigin)
    {
        if (uri == null || trustedOrigin == null)
            return false;

        return uri.Scheme.Equals(trustedOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(trustedOrigin.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == trustedOrigin.Port;
    }

    static async Task<string> GetAsync(string pathOrUrl, string uid, HttpContext httpContext, Uri trustedExternalOrigin)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            return null;

        try
        {
            // Resolve the target before attaching Lampac identity. Local routes may
            // receive the native request identity; an explicitly configured remote
            // source may receive it only on its original origin.
            Uri uri = ResolveRequestUri(pathOrUrl, httpContext, out bool localRoute);
            if (uri == null)
                return null;

            bool externalSource = trustedExternalOrigin != null;
            if (externalSource && (localRoute || !SameOrigin(uri, trustedExternalOrigin)))
            {
                Serilog.Log.Warning(
                    "TranslationSub blocked external metadata origin escape. Route={Route}",
                    SafeRoute(pathOrUrl));
                return null;
            }

            if (localRoute || SameOrigin(uri, trustedExternalOrigin))
            {
                if (httpContext != null)
                    pathOrUrl = AccsDbInvk.Args(pathOrUrl, httpContext);
                else if (!string.IsNullOrWhiteSpace(uid) && !HasQueryKey(pathOrUrl, "uid"))
                    pathOrUrl = AppendQuery(pathOrUrl, "uid", uid.Trim());

                uri = ResolveRequestUri(pathOrUrl, httpContext, out localRoute);
                if (uri == null)
                    return null;
            }

            // Re-check after identity/query decoration so an external source can
            // never become a privileged local Lampac route or change origin.
            if (externalSource && (localRoute || !SameOrigin(uri, trustedExternalOrigin)))
            {
                Serilog.Log.Warning(
                    "TranslationSub blocked external metadata origin escape after identity decoration. Route={Route}",
                    SafeRoute(pathOrUrl));
                return null;
            }

            // External metadata is intentionally fetched without automatic redirects.
            // This prevents a configured/compromised remote source from redirecting
            // the server to loopback, private services, or another origin.
            if (externalSource)
                return await GetExternalNoRedirectAsync(uri).ConfigureAwait(false);

            IReadOnlyList<HeadersModel> headers = null;
            if (localRoute)
            {
                headers = HeadersModel.Init(
                    ("xhost", LocalRequestHost(httpContext)),
                    ("xscheme", LocalRequestScheme(httpContext)),
                    ("lcrqpasswd", CoreInit.rootPasswd)
                );
            }

            return await Http.Get(
                uri.ToString(),
                timeoutSeconds: 30,
                headers: headers,
                statusCodeOK: true,
                weblog: false
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "TranslationSub metadata HTTP failed. Route={Route}", SafeRoute(pathOrUrl));
            return null;
        }
    }

    static async Task<string> GetExternalNoRedirectAsync(Uri uri)
    {
        HttpClientHandler handler = Http.HandlerOrNull(uri.ToString(), null);
        if (handler != null)
            handler.AllowAutoRedirect = false;

        HttpClient client = FriendlyHttp.MessageClient(
            "base",
            handler,
            out bool disposeHttpClient,
            allowAutoRedirect: false);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            Http.DefaultRequestHeaders(uri.ToString(), request, null, null, null);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
                return null;

            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (disposeHttpClient)
                client.Dispose();
        }
    }

    public static string AppendQuery(string pathOrUrl, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl) || string.IsNullOrWhiteSpace(key) || HasQueryKey(pathOrUrl, key))
            return pathOrUrl;

        int fragmentIndex = pathOrUrl.IndexOf('#');
        string fragment = fragmentIndex >= 0 ? pathOrUrl[fragmentIndex..] : string.Empty;
        string route = fragmentIndex >= 0 ? pathOrUrl[..fragmentIndex] : pathOrUrl;
        string separator = route.Contains('?') ? "&" : "?";

        return route + separator
            + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value ?? string.Empty)
            + fragment;
    }

    static string ForceMetadataJson(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            return pathOrUrl;

        int fragmentIndex = pathOrUrl.IndexOf('#');
        string fragment = fragmentIndex >= 0 ? pathOrUrl[fragmentIndex..] : string.Empty;
        string value = fragmentIndex >= 0 ? pathOrUrl[..fragmentIndex] : pathOrUrl;

        int queryIndex = value.IndexOf('?');
        if (queryIndex < 0)
            return value + "?rjson=true" + fragment;

        string prefix = value[..(queryIndex + 1)];
        string query = value[(queryIndex + 1)..];
        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool replaced = false;

        for (int i = 0; i < pairs.Count; i++)
        {
            string rawName = pairs[i].Split('=', 2)[0];
            string name = rawName;
            try { name = Uri.UnescapeDataString(rawName); } catch { }

            if (!name.Equals("rjson", StringComparison.OrdinalIgnoreCase))
                continue;

            pairs[i] = "rjson=true";
            replaced = true;
        }

        if (!replaced)
            pairs.Add("rjson=true");

        return prefix + string.Join("&", pairs) + fragment;
    }

    static bool HasQueryKey(string pathOrUrl, string key)
    {
        int queryIndex = pathOrUrl?.IndexOf('?') ?? -1;
        if (queryIndex < 0 || queryIndex >= pathOrUrl.Length - 1)
            return false;

        string query = pathOrUrl[(queryIndex + 1)..];
        int fragmentIndex = query.IndexOf('#');
        if (fragmentIndex >= 0)
            query = query[..fragmentIndex];

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = pair.Split('=', 2)[0];
            try { name = Uri.UnescapeDataString(name); } catch { }
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool TryCreateHttpUri(string value, out Uri uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    static Uri ResolveRequestUri(string pathOrUrl, HttpContext httpContext, out bool localRoute)
    {
        localRoute = true;
        string path = pathOrUrl?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            string scheme = LocalRequestScheme(httpContext);
            if (TryCreateHttpUri(scheme + ":" + path, out var schemeRelative))
            {
                if (!IsLocalAddress(schemeRelative, httpContext))
                {
                    localRoute = false;
                    return schemeRelative;
                }

                path = schemeRelative.PathAndQuery;
            }
        }
        else if (TryCreateHttpUri(path, out var absolute))
        {
            if (!IsLocalAddress(absolute, httpContext))
            {
                localRoute = false;
                return absolute;
            }

            path = absolute.PathAndQuery;
        }

        if (!path.StartsWith('/'))
            path = "/" + path;

        var listen = CoreInit.conf?.listen;
        if (listen == null || listen.port <= 0)
            return null;

        // Lampac's own internal online requests always use listen.localhost:listen.port.
        string localHost = string.IsNullOrWhiteSpace(listen.localhost)
            ? "127.0.0.1"
            : listen.localhost.Trim();

        if (localHost.Contains(':') && !localHost.StartsWith('['))
            localHost = $"[{localHost}]";

        return new Uri(new Uri($"http://{localHost}:{listen.port}/"), path.TrimStart('/'));
    }

    static string LocalRequestHost(HttpContext httpContext)
    {
        // Native OnlineApi.checkSearch sends its current controller host in xhost.
        if (httpContext != null)
            return CoreInit.Host(httpContext);

        var listen = CoreInit.conf?.listen;
        if (listen == null)
            return null;

        if (!string.IsNullOrWhiteSpace(listen.host))
            return listen.host.Trim();

        string localHost = string.IsNullOrWhiteSpace(listen.localhost)
            ? "127.0.0.1"
            : listen.localhost.Trim();

        if (localHost.Contains(':') && !localHost.StartsWith('['))
            localHost = $"[{localHost}]";

        return listen.port > 0 ? $"http://{localHost}:{listen.port}" : $"http://{localHost}";
    }

    static string LocalRequestScheme(HttpContext httpContext)
    {
        if (!string.IsNullOrWhiteSpace(httpContext?.Request?.Scheme))
            return httpContext.Request.Scheme;

        string scheme = CoreInit.conf?.listen?.scheme;
        return string.IsNullOrWhiteSpace(scheme) ? "http" : scheme.Trim();
    }

    static bool IsLocalAddress(Uri uri, HttpContext httpContext)
    {
        if (uri == null)
            return false;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // SeasonTpl/VoiceTpl can render the effective Lampac host from listen.host
        // or xhost, which may differ from the raw Request.Host.
        if (httpContext != null && HostMatches(uri, CoreInit.Host(httpContext)))
            return true;

        if (MatchesRequestAuthority(uri, httpContext))
            return true;

        var listen = CoreInit.conf?.listen;
        if (HostMatches(uri, listen?.localhost) || HostMatches(uri, listen?.host))
            return true;

        string listenIp = listen?.ip;
        if (!string.IsNullOrWhiteSpace(listenIp)
            && !listenIp.Equals("any", StringComparison.OrdinalIgnoreCase)
            && !listenIp.Equals("broadcast", StringComparison.OrdinalIgnoreCase)
            && listenIp != "0.0.0.0"
            && listenIp != "::"
            && HostMatches(uri, listenIp))
        {
            return true;
        }

        return false;
    }

    static bool MatchesRequestAuthority(Uri uri, HttpContext httpContext)
    {
        if (uri == null || httpContext?.Request == null)
            return false;

        var requestHost = httpContext.Request.Host;
        if (string.IsNullOrWhiteSpace(requestHost.Host)
            || !uri.Host.Equals(requestHost.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!requestHost.Port.HasValue)
            return uri.IsDefaultPort;

        return uri.Port == requestHost.Port.Value;
    }

    static bool HostMatches(Uri uri, string configuredHost)
    {
        if (uri == null || string.IsNullOrWhiteSpace(configuredHost))
            return false;

        string value = configuredHost.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value.TrimStart('/');

        if (Uri.TryCreate(value, UriKind.Absolute, out var configured))
            return uri.Host.Equals(configured.Host, StringComparison.OrdinalIgnoreCase)
                && (!configured.IsDefaultPort ? uri.Port == configured.Port : true);

        string host = configuredHost.Trim().Trim('[', ']');
        int colon = host.LastIndexOf(':');
        int configuredPort = -1;
        if (colon > 0 && host.IndexOf(':') == colon)
        {
            int.TryParse(host[(colon + 1)..], out configuredPort);
            host = host[..colon];
        }

        if (!uri.Host.Equals(host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            return false;

        return configuredPort <= 0 || uri.Port == configuredPort;
    }

    static string SafeRoute(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            return string.Empty;

        if (TryCreateHttpUri(pathOrUrl, out var absolute))
            return absolute.AbsolutePath;

        if (pathOrUrl.StartsWith("//", StringComparison.Ordinal)
            && TryCreateHttpUri("http:" + pathOrUrl, out var schemeRelative))
        {
            return schemeRelative.AbsolutePath;
        }

        int query = pathOrUrl.IndexOf('?');
        return query >= 0 ? pathOrUrl[..query] : pathOrUrl;
    }

    sealed class OnlineNode
    {
        public string Kind { get; init; }
        public string Text { get; init; }
        public string Url { get; init; }
        public string Method { get; init; }
        public int Season { get; init; }
        public int Episode { get; init; }
        public string VoiceName { get; init; }
        public string VoiceId { get; init; }
        public bool Similar { get; init; }
        public bool Active { get; init; }
    }

    sealed class FollowNode
    {
        public string Url { get; init; }
        public string VoiceName { get; init; }
        public string VoiceId { get; init; }
        public int Season { get; init; }
    }

    sealed class MetadataRow
    {
        public string VoiceName { get; init; }
        public string VoiceId { get; init; }
        public int Season { get; init; }
        public int Episode { get; init; }
    }
}
