using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TranslationSub.Models;

namespace TranslationSub.Services;

/// <summary>
/// Backend facade for a Lampa content card. It resolves identity, selected
/// Lampac sources, voice aggregation and existing subscriptions and returns a
/// presentation-ready state. JS must not repeat these decisions.
/// </summary>
public static class TranslationSubContentStateService
{
    public static async Task<TranslationSubContentState> BuildAsync(
        string uid,
        JObject payload,
        HttpContext httpContext = null)
    {
        uid = (uid ?? string.Empty).Trim();

        bool includeVoices = payload?.Value<bool?>("includeVoices") ?? true;
        var content = await ContentIdentityService.ResolveAsync(payload, uid, httpContext).ConfigureAwait(false);
        var result = new TranslationSubContentState
        {
            Content = content
        };

        if (content == null || !content.IsSerial)
        {
            result.Eligible = false;
            result.Reason = "not_serial";
            return result;
        }

        if (string.IsNullOrWhiteSpace(content.ContentId) && string.IsNullOrWhiteSpace(content.Title))
        {
            result.Eligible = false;
            result.Reason = "identity_unresolved";
            return result;
        }

        result.Eligible = true;

        // Subscription identity is shared user metadata. Profile progress is not
        // needed to answer whether a voice subscription exists for this content.
        var subscriptions = SubscriptionStore.Load()
            .Where(x => x != null && string.Equals(x.Uid, uid, StringComparison.Ordinal))
            .ToList();
        bool anySubscription = subscriptions.Any(x => SameContent(x, content));
        result.Button = new TranslationSubContentButtonState
        {
            Subscribed = anySubscription,
            Title = anySubscription ? "Озвучки · подписка активна" : "Подписки на озвучки"
        };

        // Card rendering only needs eligibility + active button state. Avoid
        // touching balancers until the user actually opens the voice selector.
        if (!includeVoices)
            return result;

        var selectedSources = (TranslationSettingsStore.Get(uid).Sources ?? new List<string>())
            .Select(TranslationSettingsStore.NormalizeSourceId)
            .Where(x => x != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selectedSources.Count == 0)
        {
            result.Reason = "sources_disabled";
            return result;
        }

        long.TryParse(content.KpId, out long kp);
        var response = await LampacMetadataService.GetVariants(new TranslationMetadataQuery
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

        var voices = (response?.Translations ?? new List<TranslationVariant>())
            .Where(x => x != null && x.season == Math.Max(1, content.Season))
            .Select(variant => BuildVoiceState(variant, FindExisting(subscriptions, content, variant)))
            .OrderByDescending(x => x.Subscribed)
            .ThenBy(x => x.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.Voices = voices;
        if (voices.Count == 0)
            result.Reason = "voices_not_found";

        return result;
    }

    static TranslationSubVoiceState BuildVoiceState(
        TranslationVariant variant,
        TranslationSubscription existing)
    {
        string name = !string.IsNullOrWhiteSpace(variant.translation)
            ? variant.translation.Trim()
            : "Озвучка " + (variant.translation_id ?? string.Empty);
        int latest = Math.Max(0, variant.episode);
        var sources = VariantSources(variant);
        string sourceSummary = string.Join(", ", sources
            .Select(x => x.Source)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        string subtitle;
        if (existing != null)
            subtitle = "Подписка активна · нажмите, чтобы отписаться";
        else if (latest > 0)
            subtitle = "Доступно до " + latest + " серии";
        else
            subtitle = "Нажмите, чтобы подписаться";

        if (!string.IsNullOrWhiteSpace(sourceSummary))
            subtitle += " · " + sourceSummary;

        return new TranslationSubVoiceState
        {
            Id = variant.translation_id,
            Name = name,
            LatestEpisode = latest,
            Subscribed = existing != null,
            SubscriptionId = existing?.Id,
            Action = existing != null ? "unsubscribe" : "subscribe",
            Subtitle = subtitle,
            Sources = sources
        };
    }

    static List<TranslationSubSourceSnapshot> VariantSources(TranslationVariant variant)
    {
        if (variant?.Sources != null && variant.Sources.Count > 0)
        {
            return variant.Sources
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Source))
                .Select(x => new TranslationSubSourceSnapshot
                {
                    Source = x.Source,
                    TranslationId = x.TranslationId,
                    TranslationName = x.TranslationName
                })
                .ToList();
        }

        if (variant == null || string.IsNullOrWhiteSpace(variant.source))
            return new List<TranslationSubSourceSnapshot>();

        return new List<TranslationSubSourceSnapshot>
        {
            new()
            {
                Source = variant.source,
                TranslationId = variant.translation_id,
                TranslationName = variant.translation
            }
        };
    }

    static TranslationSubscription FindExisting(
        IEnumerable<TranslationSubscription> subscriptions,
        TranslationSubResolvedContent content,
        TranslationVariant variant)
    {
        int season = Math.Max(1, content.Season);
        string voiceId = (variant?.translation_id ?? string.Empty).Trim();
        string voiceName = VoiceNormalize.Normalize(variant?.translation);

        return (subscriptions ?? Enumerable.Empty<TranslationSubscription>())
            .FirstOrDefault(item =>
            {
                if (item == null || !SameContent(item, content))
                    return false;
                if (Math.Max(1, item.CurrentSeason.GetValueOrDefault(1)) != season)
                    return false;

                string itemId = (item.TranslationId ?? string.Empty).Trim();
                string itemName = VoiceNormalize.Normalize(item.TranslationName);
                return (!string.IsNullOrWhiteSpace(voiceId)
                        && !string.IsNullOrWhiteSpace(itemId)
                        && string.Equals(voiceId, itemId, StringComparison.Ordinal))
                    || (!string.IsNullOrWhiteSpace(voiceName)
                        && !string.IsNullOrWhiteSpace(itemName)
                        && string.Equals(voiceName, itemName, StringComparison.Ordinal));
            });
    }

    public static bool SameContent(TranslationSubscription item, TranslationSubResolvedContent content)
    {
        if (item == null || content == null)
            return false;

        if (Same(item.ContentId, content.ContentId))
            return true;
        if (Same(item.TmdbId, content.TmdbId))
            return true;
        if (Same(item.ImdbId, content.ImdbId, ignoreCase: true))
            return true;
        return Same(item.KpId, content.KpId);
    }

    static bool Same(string left, string right, bool ignoreCase = false)
    {
        left = (left ?? string.Empty).Trim();
        right = (right ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(left, right,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
