using System;
using System.Collections.Generic;

namespace TranslationSub.Models;

/// <summary>
/// Shared user subscription metadata. Profile-specific watched progress is kept
/// exclusively in SubscriptionProfileProgress/ProfileProgressStore and is never
/// persisted on this shared object.
/// </summary>
public class TranslationSubscription
{
    public string Id { get; set; }
    public string Uid { get; set; }
    public string ContentId { get; set; }
    public string Title { get; set; }
    public string OriginalTitle { get; set; }
    public string KpId { get; set; }
    public string ImdbId { get; set; }
    public string TmdbId { get; set; }
    public string Poster { get; set; }
    public int? Year { get; set; }
    public bool IsSerial { get; set; } = true;
    public string Source { get; set; }
    public string TranslationId { get; set; }
    public string TranslationName { get; set; }
    public int? CurrentSeason { get; set; }
    public int? LastSeason { get; set; }
    public int? LastEpisode { get; set; }
    public List<TranslationSubscriptionSource> Sources { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastCheckedAt { get; set; }

    public string TmdbStatus { get; set; }
    public int? TmdbLastSeason { get; set; }
    public int? TmdbLastEpisode { get; set; }
    public DateTime? TmdbLastAirDate { get; set; }
    public int? TmdbNextSeason { get; set; }
    public int? TmdbNextEpisode { get; set; }
    public DateTime? TmdbNextAirDate { get; set; }
    public int? TmdbTargetSeasonEpisodes { get; set; }
    public DateTime? TmdbLastSyncedAt { get; set; }
    public string ScheduleState { get; set; }
    public bool TmdbNewSeasonAvailable { get; set; }
}

public class TranslationSubscriptionSource
{
    public string Source { get; set; }
    public string TranslationId { get; set; }
    public string TranslationName { get; set; }
}
