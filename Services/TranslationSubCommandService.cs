using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TranslationSub.Models;

namespace TranslationSub.Services;

/// <summary>
/// Server-authoritative TranslationSub commands. The client sends intent only;
/// identity, selected sources, voice metadata and available episodes are
/// re-resolved on the backend before persistence.
/// </summary>
public static class TranslationSubCommandService
{
    public static async Task<TranslationSubCommandResult> SubscribeAsync(
        string uid,
        TranslationSubSubscribeIntent intent,
        HttpContext httpContext = null)
    {
        uid = (uid ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(uid))
            return Fail("uid_required");
        if (intent?.Card == null)
            return Fail("card_required");

        var content = await ContentIdentityService.ResolveAsync(intent.Card, uid, httpContext).ConfigureAwait(false);
        if (content == null || !content.IsSerial)
            return Fail("not_serial");
        if (string.IsNullOrWhiteSpace(content.ContentId) && string.IsNullOrWhiteSpace(content.Title))
            return Fail("identity_unresolved");

        var selectedSources = (TranslationSettingsStore.Get(uid).Sources ?? new List<string>())
            .Select(TranslationSettingsStore.NormalizeSourceId)
            .Where(x => x != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedSources.Count == 0)
            return Fail("sources_disabled");

        long.TryParse(content.KpId, out long kp);
        var variants = await LampacMetadataService.GetVariants(new TranslationMetadataQuery
        {
            Uid = uid,
            ContentId = content.ContentId,
            TmdbId = content.TmdbId,
            ImdbId = content.ImdbId,
            KpId = kp,
            Title = content.Title,
            OriginalTitle = content.OriginalTitle,
            Year = content.Year.GetValueOrDefault(0),
            IsSerial = true,
            Season = Math.Max(1, content.Season),
            Sources = selectedSources
        }, httpContext).ConfigureAwait(false);

        var variant = FindVariant(variants?.Translations, intent.VoiceId, intent.VoiceName, content.Season);
        if (variant == null)
            return Fail("voice_not_found");

        var sources = VariantSources(variant);
        if (sources.Count == 0)
            return Fail("voice_sources_not_found");

        TmdbScheduleSnapshot tmdb = null;
        if (!string.IsNullOrWhiteSpace(content.TmdbId) || !string.IsNullOrWhiteSpace(content.ImdbId))
        {
            try
            {
                tmdb = await TmdbScheduleService.Get(content.TmdbId, content.ImdbId).ConfigureAwait(false);
            }
            catch
            {
                tmdb = null;
            }
        }

        string subscriptionId = null;
        int season = Math.Max(1, content.Season);
        int latestEpisode = Math.Max(0, variant.episode);

        SubscriptionStore.Mutate(list =>
        {
            var existing = list.FirstOrDefault(x =>
                x != null
                && string.Equals(x.Uid, uid, StringComparison.Ordinal)
                && TranslationSubContentStateService.SameContent(x, content)
                && Math.Max(1, x.CurrentSeason.GetValueOrDefault(1)) == season
                && VoiceMatches(x, variant));

            if (existing != null)
            {
                subscriptionId = existing.Id;
                return;
            }

            var sub = new TranslationSubscription
            {
                Id = Guid.NewGuid().ToString("N"),
                Uid = uid,
                ContentId = content.ContentId,
                Title = content.Title,
                OriginalTitle = content.OriginalTitle,
                KpId = content.KpId,
                ImdbId = content.ImdbId,
                TmdbId = content.TmdbId,
                Poster = content.Poster,
                Year = content.Year,
                IsSerial = true,
                Source = sources.Count > 1 ? "multi" : sources[0].Source,
                TranslationId = variant.translation_id,
                TranslationName = variant.translation,
                CurrentSeason = season,
                LastSeason = season,
                LastEpisode = latestEpisode,
                Sources = sources,
                CreatedAt = DateTime.Now,
                LastCheckedAt = DateTime.Now
            };

            ApplyTmdb(sub, tmdb, season);
            list.Add(sub);
            subscriptionId = sub.Id;
        });

        return new TranslationSubCommandResult
        {
            Success = !string.IsNullOrWhiteSpace(subscriptionId),
            Error = string.IsNullOrWhiteSpace(subscriptionId) ? "subscription_not_created" : null,
            SubscriptionId = subscriptionId
        };
    }

    public static TranslationSubCommandResult Unsubscribe(string uid, string subscriptionId)
    {
        uid = (uid ?? string.Empty).Trim();
        subscriptionId = (subscriptionId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(uid))
            return Fail("uid_required");
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return Fail("subscription_id_required");

        bool removed = false;
        SubscriptionStore.Mutate(list =>
        {
            removed = list.RemoveAll(x => x != null
                && string.Equals(x.Uid, uid, StringComparison.Ordinal)
                && string.Equals(x.Id, subscriptionId, StringComparison.Ordinal)) > 0;
        });

        if (removed)
            ProfileProgressStore.RemoveSubscription(uid, subscriptionId);

        return new TranslationSubCommandResult
        {
            Success = removed,
            Error = removed ? null : "subscription_not_found",
            SubscriptionId = subscriptionId
        };
    }

    static TranslationVariant FindVariant(
        IEnumerable<TranslationVariant> variants,
        string voiceId,
        string voiceName,
        int season)
    {
        voiceId = (voiceId ?? string.Empty).Trim();
        string normalizedName = VoiceNormalize.Normalize(voiceName);
        season = Math.Max(1, season);

        return (variants ?? Enumerable.Empty<TranslationVariant>())
            .Where(x => x != null && x.season == season)
            .FirstOrDefault(x =>
            {
                if (!string.IsNullOrWhiteSpace(voiceId)
                    && string.Equals((x.translation_id ?? string.Empty).Trim(), voiceId, StringComparison.Ordinal))
                    return true;

                string candidate = VoiceNormalize.Normalize(x.translation);
                return !string.IsNullOrWhiteSpace(normalizedName)
                    && !string.IsNullOrWhiteSpace(candidate)
                    && string.Equals(candidate, normalizedName, StringComparison.Ordinal);
            });
    }

    static bool VoiceMatches(TranslationSubscription subscription, TranslationVariant variant)
    {
        string leftId = (subscription?.TranslationId ?? string.Empty).Trim();
        string rightId = (variant?.translation_id ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(leftId)
            && !string.IsNullOrWhiteSpace(rightId)
            && string.Equals(leftId, rightId, StringComparison.Ordinal))
            return true;

        string leftName = VoiceNormalize.Normalize(subscription?.TranslationName);
        string rightName = VoiceNormalize.Normalize(variant?.translation);
        return !string.IsNullOrWhiteSpace(leftName)
            && !string.IsNullOrWhiteSpace(rightName)
            && string.Equals(leftName, rightName, StringComparison.Ordinal);
    }

    static List<TranslationSubscriptionSource> VariantSources(TranslationVariant variant)
    {
        if (variant?.Sources != null && variant.Sources.Count > 0)
        {
            return variant.Sources
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Source))
                .Select(x => new TranslationSubscriptionSource
                {
                    Source = TranslationSettingsStore.NormalizeSourceId(x.Source) ?? x.Source,
                    TranslationId = x.TranslationId,
                    TranslationName = x.TranslationName
                })
                .GroupBy(x => x.Source ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
        }

        string source = TranslationSettingsStore.NormalizeSourceId(variant?.source) ?? variant?.source;
        if (string.IsNullOrWhiteSpace(source))
            return new List<TranslationSubscriptionSource>();

        return new List<TranslationSubscriptionSource>
        {
            new()
            {
                Source = source,
                TranslationId = variant.translation_id,
                TranslationName = variant.translation
            }
        };
    }

    static void ApplyTmdb(TranslationSubscription sub, TmdbScheduleSnapshot tmdb, int season)
    {
        if (sub == null || tmdb == null)
            return;

        if (!string.IsNullOrWhiteSpace(tmdb.TmdbId))
        {
            sub.TmdbId = tmdb.TmdbId;
            sub.ContentId = tmdb.TmdbId;
        }
        if (!string.IsNullOrWhiteSpace(tmdb.ImdbId))
            sub.ImdbId = tmdb.ImdbId;

        sub.TmdbStatus = tmdb.Status;
        sub.TmdbLastSeason = tmdb.LastSeason > 0 ? tmdb.LastSeason : null;
        sub.TmdbLastEpisode = tmdb.LastEpisode > 0 ? tmdb.LastEpisode : null;
        sub.TmdbLastAirDate = tmdb.LastAirDate;
        sub.TmdbNextSeason = tmdb.NextSeason > 0 ? tmdb.NextSeason : null;
        sub.TmdbNextEpisode = tmdb.NextEpisode > 0 ? tmdb.NextEpisode : null;
        sub.TmdbNextAirDate = tmdb.NextAirDate;
        sub.TmdbLastSyncedAt = tmdb.SyncedAt;

        int target = 0;
        if (tmdb.LastSeason == season)
            target = Math.Max(target, tmdb.LastEpisode);
        if (tmdb.NextSeason == season && tmdb.NextAirDate.HasValue && tmdb.NextAirDate.Value.Date <= DateTime.Now.Date)
            target = Math.Max(target, tmdb.NextEpisode);
        if (target > 0)
            sub.TmdbTargetSeasonEpisodes = target;
    }

    static TranslationSubCommandResult Fail(string error)
        => new()
        {
            Success = false,
            Error = error
        };
}
