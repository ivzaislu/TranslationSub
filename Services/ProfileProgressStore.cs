using System;
using System.Collections.Generic;
using TranslationSub.Models;

namespace TranslationSub.Services;

/// <summary>
/// Durable profile-scoped projection of Lampac TimeCode progress.
/// This store is not the source of truth: TimeCodeProgressService reconciles it
/// from database/TimeCode.sql whenever Lampac commits progress.
/// </summary>
public static class ProfileProgressStore
{
    public static string NormalizeProfileId(string profileId)
    {
        profileId = (profileId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(profileId) ? "0" : profileId;
    }

    public static List<SubscriptionProfileProgress> Load(string uid, string profileId)
    {
        uid = (uid ?? string.Empty).Trim();
        profileId = NormalizeProfileId(profileId);

        if (string.IsNullOrWhiteSpace(uid))
            return new List<SubscriptionProfileProgress>();

        lock (TranslationSubDatabase.SyncRoot)
        {
            using var connection = TranslationSubDatabase.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT uid, profile_id, subscription_id, watched_episode, updated_at
FROM profile_progress
WHERE uid = $uid AND profile_id = $profile_id
ORDER BY updated_at DESC;";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$profile_id", profileId);

            var result = new List<SubscriptionProfileProgress>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SubscriptionProfileProgress
                {
                    Uid = reader.GetString(reader.GetOrdinal("uid")),
                    ProfileId = reader.GetString(reader.GetOrdinal("profile_id")),
                    SubscriptionId = reader.GetString(reader.GetOrdinal("subscription_id")),
                    WatchedEpisode = Math.Max(0, reader.GetInt32(reader.GetOrdinal("watched_episode"))),
                    UpdatedAt = TranslationSubDatabase.ReadDateTime(reader, "updated_at", DateTime.Now)
                });
            }

            return result;
        }
    }

    public static Dictionary<string, int> LoadWatchedBySubscription(string uid, string profileId)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in Load(uid, profileId))
        {
            if (item == null || string.IsNullOrWhiteSpace(item.SubscriptionId) || result.ContainsKey(item.SubscriptionId))
                continue;
            result[item.SubscriptionId] = Math.Max(0, item.WatchedEpisode);
        }
        return result;
    }

    public static int Upsert(string uid, string profileId, IReadOnlyDictionary<string, int> watchedBySubscription)
    {
        uid = (uid ?? string.Empty).Trim();
        profileId = NormalizeProfileId(profileId);

        if (string.IsNullOrWhiteSpace(uid) || watchedBySubscription == null || watchedBySubscription.Count == 0)
            return 0;

        int changed = 0;
        DateTime now = DateTime.Now;

        lock (TranslationSubDatabase.SyncRoot)
        {
            using var connection = TranslationSubDatabase.Open();
            using var transaction = connection.BeginTransaction();

            var existing = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = @"
SELECT subscription_id, watched_episode
FROM profile_progress
WHERE uid = $uid AND profile_id = $profile_id;";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$profile_id", profileId);

                using var reader = read.ExecuteReader();
                while (reader.Read())
                    existing[reader.GetString(0)] = Math.Max(0, reader.GetInt32(1));
            }

            foreach (var pair in watchedBySubscription)
            {
                string subscriptionId = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(subscriptionId))
                    continue;

                int watched = Math.Max(0, pair.Value);
                bool exists = existing.TryGetValue(subscriptionId, out int previous);
                if (exists && previous == watched)
                    continue;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO profile_progress (
    uid, profile_id, subscription_id, watched_episode, updated_at
) VALUES (
    $uid, $profile_id, $subscription_id, $watched_episode, $updated_at
)
ON CONFLICT(uid, profile_id, subscription_id) DO UPDATE SET
    watched_episode = excluded.watched_episode,
    updated_at = excluded.updated_at;";
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$profile_id", profileId);
                command.Parameters.AddWithValue("$subscription_id", subscriptionId);
                command.Parameters.AddWithValue("$watched_episode", watched);
                command.Parameters.AddWithValue("$updated_at", TranslationSubDatabase.DateTimeText(now));
                command.ExecuteNonQuery();

                if (exists || watched > 0)
                    changed++;
            }

            transaction.Commit();
        }

        return changed;
    }

    /// <summary>
    /// Replaces the profile projection with the successfully read TimeCode state.
    /// Missing or zero entries are represented by the absence of a row; the read
    /// projection already treats an absent row as watched episode 0.
    /// </summary>
    public static int Reconcile(string uid, string profileId, IReadOnlyDictionary<string, int> watchedBySubscription)
    {
        uid = (uid ?? string.Empty).Trim();
        profileId = NormalizeProfileId(profileId);

        if (string.IsNullOrWhiteSpace(uid) || watchedBySubscription == null)
            return 0;

        var desired = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in watchedBySubscription)
        {
            string subscriptionId = (pair.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(subscriptionId))
                continue;
            desired[subscriptionId] = Math.Max(0, pair.Value);
        }

        int changed = 0;
        DateTime now = DateTime.Now;

        lock (TranslationSubDatabase.SyncRoot)
        {
            using var connection = TranslationSubDatabase.Open();
            using var transaction = connection.BeginTransaction();

            var existing = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = @"
SELECT subscription_id, watched_episode
FROM profile_progress
WHERE uid = $uid AND profile_id = $profile_id;";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$profile_id", profileId);

                using var reader = read.ExecuteReader();
                while (reader.Read())
                    existing[reader.GetString(0)] = Math.Max(0, reader.GetInt32(1));
            }

            foreach (var pair in existing)
            {
                if (desired.TryGetValue(pair.Key, out int watched) && watched > 0)
                    continue;

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = @"
DELETE FROM profile_progress
WHERE uid = $uid AND profile_id = $profile_id AND subscription_id = $subscription_id;";
                delete.Parameters.AddWithValue("$uid", uid);
                delete.Parameters.AddWithValue("$profile_id", profileId);
                delete.Parameters.AddWithValue("$subscription_id", pair.Key);
                changed += delete.ExecuteNonQuery();
            }

            foreach (var pair in desired)
            {
                int watched = Math.Max(0, pair.Value);
                if (watched <= 0)
                    continue;

                bool exists = existing.TryGetValue(pair.Key, out int previous);
                if (exists && previous == watched)
                    continue;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO profile_progress (
    uid, profile_id, subscription_id, watched_episode, updated_at
) VALUES (
    $uid, $profile_id, $subscription_id, $watched_episode, $updated_at
)
ON CONFLICT(uid, profile_id, subscription_id) DO UPDATE SET
    watched_episode = excluded.watched_episode,
    updated_at = excluded.updated_at;";
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$profile_id", profileId);
                command.Parameters.AddWithValue("$subscription_id", pair.Key);
                command.Parameters.AddWithValue("$watched_episode", watched);
                command.Parameters.AddWithValue("$updated_at", TranslationSubDatabase.DateTimeText(now));
                command.ExecuteNonQuery();
                changed++;
            }

            transaction.Commit();
        }

        return changed;
    }

    public static void RemoveSubscription(string uid, string subscriptionId)
    {
        uid = (uid ?? string.Empty).Trim();
        subscriptionId = (subscriptionId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(subscriptionId))
            return;

        // Batched subscription deletions clean their profile progress in the same
        // SQLite transaction when SubscriptionStore flushes the final state.
        if (SubscriptionStore.HasActiveBatch)
            return;

        lock (TranslationSubDatabase.SyncRoot)
        {
            using var connection = TranslationSubDatabase.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
DELETE FROM profile_progress
WHERE uid = $uid AND subscription_id = $subscription_id;";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$subscription_id", subscriptionId);
            command.ExecuteNonQuery();
        }
    }
}
