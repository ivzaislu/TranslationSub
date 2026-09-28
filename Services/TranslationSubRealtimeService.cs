using Shared;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TranslationSub.Services;

public static class TranslationSubRealtimeService
{
    sealed class Registration
    {
        public string Uid { get; init; }
        public string ProfileId { get; init; }
    }

    static readonly ConcurrentDictionary<string, Registration> registrations = new(StringComparer.Ordinal);
    static long revision;

    public static void Register(string connectionId, string uid, string profileId)
    {
        connectionId = (connectionId ?? string.Empty).Trim();
        uid = (uid ?? string.Empty).Trim();
        if (connectionId.Length == 0 || uid.Length == 0)
            return;

        registrations[connectionId] = new Registration
        {
            Uid = uid,
            ProfileId = NormalizeProfileId(profileId)
        };
    }

    public static void Unregister(string connectionId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId))
            registrations.TryRemove(connectionId, out _);
    }

    public static async Task PublishProfile(string uid, string profileId, string reason)
    {
        uid = (uid ?? string.Empty).Trim();
        if (uid.Length == 0 || Startup.Nws == null)
            return;

        string normalizedProfile = NormalizeProfileId(profileId);
        long nextRevision = Interlocked.Increment(ref revision);
        var targets = registrations
            .Where(x => string.Equals(x.Value.Uid, uid, StringComparison.Ordinal)
                && string.Equals(x.Value.ProfileId, normalizedProfile, StringComparison.Ordinal))
            .Select(x => x.Key)
            .ToArray();

        foreach (string connectionId in targets)
        {
            try
            {
                await Startup.Nws.SendAsync(
                    connectionId,
                    "TranslationSubChanged",
                    nextRevision,
                    reason ?? "state"
                ).ConfigureAwait(false);
            }
            catch
            {
                // A disconnected NWS client must never break TimeCode/scheduler work.
            }
        }
    }

    public static async Task PublishUid(string uid, string reason)
    {
        uid = (uid ?? string.Empty).Trim();
        if (uid.Length == 0 || Startup.Nws == null)
            return;

        long nextRevision = Interlocked.Increment(ref revision);
        var targets = registrations
            .Where(x => string.Equals(x.Value.Uid, uid, StringComparison.Ordinal))
            .Select(x => x.Key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (string connectionId in targets)
        {
            try
            {
                await Startup.Nws.SendAsync(
                    connectionId,
                    "TranslationSubChanged",
                    nextRevision,
                    reason ?? "state"
                ).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort delivery; reconnecting clients refresh their snapshot.
            }
        }
    }

    static string NormalizeProfileId(string profileId)
    {
        string value = (profileId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) || value == "0" ? "0" : value;
    }
}
