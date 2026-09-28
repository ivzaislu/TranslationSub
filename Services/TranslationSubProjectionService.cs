using System;
using System.Collections.Generic;
using System.Linq;
using TranslationSub.Models;

namespace TranslationSub.Services;

/// <summary>
/// Composes shared subscription metadata with profile-scoped watched progress.
/// It never mutates or synthesizes profile fields on TranslationSubscription.
/// TimeCode/ProfileProgressStore is the only source for watched progress.
/// </summary>
public static class TranslationSubProjectionService
{
    public static List<TranslationSubProfileProjection> ForProfile(string uid, string profileId)
    {
        uid = (uid ?? string.Empty).Trim();
        profileId = ProfileProgressStore.NormalizeProfileId(profileId);

        if (string.IsNullOrWhiteSpace(uid))
            return new List<TranslationSubProfileProjection>();

        var watched = ProfileProgressStore.LoadWatchedBySubscription(uid, profileId);
        return SubscriptionStore.Load()
            .Where(x => x != null && string.Equals(x.Uid, uid, StringComparison.Ordinal))
            .Select(sub => new TranslationSubProfileProjection
            {
                Subscription = sub,
                WatchedEpisode = !string.IsNullOrWhiteSpace(sub.Id)
                    && watched.TryGetValue(sub.Id, out int stored)
                        ? Math.Max(0, stored)
                        : 0
            })
            .ToList();
    }
}
