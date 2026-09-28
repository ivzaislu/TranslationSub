using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace TranslationSub.Models;

public class TranslationSubSnapshot
{
    [JsonProperty("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.Now;

    [JsonProperty("profileId")]
    public string ProfileId { get; set; }

    [JsonProperty("badge")]
    public TranslationSubBadgeSnapshot Badge { get; set; } = new();

    [JsonProperty("subscriptions")]
    public List<TranslationSubSubscriptionSnapshot> Subscriptions { get; set; } = new();

    [JsonProperty("updates")]
    public List<TranslationSubSubscriptionSnapshot> Updates { get; set; } = new();
}

public class TranslationSubBadgeSnapshot
{
    [JsonProperty("count")]
    public int Count { get; set; }
}

public class TranslationSubSubscriptionSnapshot
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("contentId")]
    public string ContentId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("originalTitle")]
    public string OriginalTitle { get; set; }

    [JsonProperty("kpId")]
    public string KpId { get; set; }

    [JsonProperty("imdbId")]
    public string ImdbId { get; set; }

    [JsonProperty("tmdbId")]
    public string TmdbId { get; set; }

    [JsonProperty("poster")]
    public string Poster { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("isSerial")]
    public bool IsSerial { get; set; }

    [JsonProperty("navigation")]
    public TranslationSubNavigationSnapshot Navigation { get; set; }

    [JsonProperty("season")]
    public int Season { get; set; }

    [JsonProperty("watchedEpisode")]
    public int WatchedEpisode { get; set; }

    [JsonProperty("availableEpisode")]
    public int AvailableEpisode { get; set; }

    [JsonProperty("hasNewEpisodes")]
    public bool HasNewEpisodes { get; set; }

    [JsonProperty("fromEpisode")]
    public int FromEpisode { get; set; }

    [JsonProperty("toEpisode")]
    public int ToEpisode { get; set; }

    [JsonProperty("newCount")]
    public int NewCount { get; set; }

    [JsonProperty("progressPercent")]
    public int ProgressPercent { get; set; }

    [JsonProperty("source")]
    public string Source { get; set; }

    [JsonProperty("sources")]
    public List<TranslationSubSourceSnapshot> Sources { get; set; } = new();

    [JsonProperty("translationId")]
    public string TranslationId { get; set; }

    [JsonProperty("translationName")]
    public string TranslationName { get; set; }

    [JsonProperty("lastCheckedAt")]
    public DateTime? LastCheckedAt { get; set; }

    [JsonProperty("schedule")]
    public TranslationSubScheduleSnapshot Schedule { get; set; }

    [JsonProperty("tmdb")]
    public TranslationSubTmdbSnapshot Tmdb { get; set; }

    // Presentation-ready strings are produced by the backend so clients do not
    // reconstruct progress ranges, source aggregation or TMDB facts from domain fields.
    [JsonProperty("display")]
    public TranslationSubDisplaySnapshot Display { get; set; } = new();
}

public class TranslationSubNavigationSnapshot
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("method")]
    public string Method { get; set; }
}

public class TranslationSubSourceSnapshot
{
    [JsonProperty("source")]
    public string Source { get; set; }

    [JsonProperty("translationId")]
    public string TranslationId { get; set; }

    [JsonProperty("translationName")]
    public string TranslationName { get; set; }
}

public class TranslationSubScheduleSnapshot
{
    [JsonProperty("code")]
    public string Code { get; set; }

    [JsonProperty("text")]
    public string Text { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }
}

public class TranslationSubTmdbSnapshot
{
    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("lastSeason")]
    public int? LastSeason { get; set; }

    [JsonProperty("lastEpisode")]
    public int? LastEpisode { get; set; }

    [JsonProperty("lastAirDate")]
    public DateTime? LastAirDate { get; set; }

    [JsonProperty("nextSeason")]
    public int? NextSeason { get; set; }

    [JsonProperty("nextEpisode")]
    public int? NextEpisode { get; set; }

    [JsonProperty("nextAirDate")]
    public DateTime? NextAirDate { get; set; }

    [JsonProperty("targetSeasonEpisodes")]
    public int? TargetSeasonEpisodes { get; set; }

    [JsonProperty("lastSyncedAt")]
    public DateTime? LastSyncedAt { get; set; }

    [JsonProperty("newSeasonAvailable")]
    public bool NewSeasonAvailable { get; set; }
}

public class TranslationSubDisplaySnapshot
{
    [JsonProperty("season")]
    public string Season { get; set; }

    [JsonProperty("watched")]
    public string Watched { get; set; }

    [JsonProperty("available")]
    public string Available { get; set; }

    [JsonProperty("progress")]
    public string Progress { get; set; }

    [JsonProperty("source")]
    public string Source { get; set; }

    [JsonProperty("sourceLabels")]
    public List<string> SourceLabels { get; set; } = new();

    [JsonProperty("newBadge")]
    public string NewBadge { get; set; }

    [JsonProperty("noticeTime")]
    public string NoticeTime { get; set; }

    [JsonProperty("noticeRange")]
    public string NoticeRange { get; set; }

    [JsonProperty("tmdbFacts")]
    public string TmdbFacts { get; set; }

    [JsonProperty("tmdbNext")]
    public string TmdbNext { get; set; }

    [JsonProperty("tmdbTitle")]
    public string TmdbTitle { get; set; }
}
