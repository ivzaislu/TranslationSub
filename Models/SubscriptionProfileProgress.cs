using System;

namespace TranslationSub.Models;

/// <summary>
/// Profile-scoped watched progress for a shared TranslationSub subscription.
/// Lampac TimeCode remains the authoritative source; this model is a durable
/// projection/cache used by TranslationSub read models and realtime invalidation.
/// </summary>
public class SubscriptionProfileProgress
{
    public string Uid { get; set; }
    public string ProfileId { get; set; } = "0";
    public string SubscriptionId { get; set; }
    public int WatchedEpisode { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Read-only composition of shared subscription metadata and one profile's
/// watched progress. Profile state never gets written back into the shared
/// TranslationSubscription object.
/// </summary>
public sealed class TranslationSubProfileProjection
{
    public TranslationSubscription Subscription { get; init; }
    public int WatchedEpisode { get; init; }
}
