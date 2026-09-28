using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TranslationSub.Models;

namespace TranslationSub.Services;

public static class SubscriptionStore
{
    static readonly AsyncLocal<MutationBatch> ambientBatch = new();

    internal static bool HasActiveBatch => ambientBatch.Value != null;

    sealed class MutationBatch
    {
        public int Depth { get; set; } = 1;
        public List<Action<List<TranslationSubscription>>> Operations { get; } = new();
    }

    sealed class RemovedSubscription
    {
        public string Id { get; init; }
        public string Uid { get; init; }
    }

    sealed class SubscriptionSnapshot
    {
        public Dictionary<string, string> StateById { get; init; }
        public Dictionary<string, string> UidById { get; init; }
    }

    sealed class MutationBatchScope : IDisposable
    {
        readonly MutationBatch batch;
        bool disposed;

        public MutationBatchScope(MutationBatch batch)
        {
            this.batch = batch;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            if (batch == null || --batch.Depth > 0)
                return;

            ambientBatch.Value = null;
            FlushBatch(batch);
        }
    }

    static List<TranslationSubscription> LoadUnsafe()
    {
        using var connection = TranslationSubDatabase.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, uid, content_id, title, original_title, kp_id, imdb_id, tmdb_id,
       poster, year, is_serial, source, translation_id, translation_name,
       current_season, last_season, last_episode, sources_json, created_at,
       last_checked_at, tmdb_status, tmdb_last_season, tmdb_last_episode,
       tmdb_last_air_date, tmdb_next_season, tmdb_next_episode, tmdb_next_air_date,
       tmdb_target_season_episodes, tmdb_last_synced_at, schedule_state,
       tmdb_new_season_available
FROM subscriptions
ORDER BY rowid;";

        var result = new List<TranslationSubscription>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TranslationSubscription
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                Uid = reader.GetString(reader.GetOrdinal("uid")),
                ContentId = TranslationSubDatabase.ReadNullableString(reader, "content_id"),
                Title = TranslationSubDatabase.ReadNullableString(reader, "title"),
                OriginalTitle = TranslationSubDatabase.ReadNullableString(reader, "original_title"),
                KpId = TranslationSubDatabase.ReadNullableString(reader, "kp_id"),
                ImdbId = TranslationSubDatabase.ReadNullableString(reader, "imdb_id"),
                TmdbId = TranslationSubDatabase.ReadNullableString(reader, "tmdb_id"),
                Poster = TranslationSubDatabase.ReadNullableString(reader, "poster"),
                Year = TranslationSubDatabase.ReadNullableInt(reader, "year"),
                IsSerial = reader.GetInt32(reader.GetOrdinal("is_serial")) != 0,
                Source = TranslationSubDatabase.ReadNullableString(reader, "source"),
                TranslationId = TranslationSubDatabase.ReadNullableString(reader, "translation_id"),
                TranslationName = TranslationSubDatabase.ReadNullableString(reader, "translation_name"),
                CurrentSeason = TranslationSubDatabase.ReadNullableInt(reader, "current_season"),
                LastSeason = TranslationSubDatabase.ReadNullableInt(reader, "last_season"),
                LastEpisode = TranslationSubDatabase.ReadNullableInt(reader, "last_episode"),
                Sources = ReadSources(reader.GetString(reader.GetOrdinal("sources_json"))),
                CreatedAt = TranslationSubDatabase.ReadDateTime(reader, "created_at", DateTime.Now),
                LastCheckedAt = TranslationSubDatabase.ReadNullableDateTime(reader, "last_checked_at"),
                TmdbStatus = TranslationSubDatabase.ReadNullableString(reader, "tmdb_status"),
                TmdbLastSeason = TranslationSubDatabase.ReadNullableInt(reader, "tmdb_last_season"),
                TmdbLastEpisode = TranslationSubDatabase.ReadNullableInt(reader, "tmdb_last_episode"),
                TmdbLastAirDate = TranslationSubDatabase.ReadNullableDateTime(reader, "tmdb_last_air_date"),
                TmdbNextSeason = TranslationSubDatabase.ReadNullableInt(reader, "tmdb_next_season"),
                TmdbNextEpisode = TranslationSubDatabase.ReadNullableInt(reader, "tmdb_next_episode"),
                TmdbNextAirDate = TranslationSubDatabase.ReadNullableDateTime(reader, "tmdb_next_air_date"),
                TmdbTargetSeasonEpisodes = TranslationSubDatabase.ReadNullableInt(reader, "tmdb_target_season_episodes"),
                TmdbLastSyncedAt = TranslationSubDatabase.ReadNullableDateTime(reader, "tmdb_last_synced_at"),
                ScheduleState = TranslationSubDatabase.ReadNullableString(reader, "schedule_state"),
                TmdbNewSeasonAvailable = reader.GetInt32(reader.GetOrdinal("tmdb_new_season_available")) != 0
            });
        }

        return result;
    }

    static List<TranslationSubscriptionSource> ReadSources(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<List<TranslationSubscriptionSource>>(json ?? "[]")
                ?? new List<TranslationSubscriptionSource>();
        }
        catch
        {
            return new List<TranslationSubscriptionSource>();
        }
    }

    static Dictionary<string, TranslationSubscription> PersistableById(IEnumerable<TranslationSubscription> list)
    {
        var result = new Dictionary<string, TranslationSubscription>(StringComparer.Ordinal);
        foreach (var item in list ?? Enumerable.Empty<TranslationSubscription>())
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id))
                continue;

            if (!result.TryAdd(item.Id, item))
                throw new InvalidOperationException($"Duplicate TranslationSub subscription id '{item.Id}'.");
        }
        return result;
    }

    static SubscriptionSnapshot CaptureSnapshot(IEnumerable<TranslationSubscription> list)
    {
        var items = PersistableById(list);
        return new SubscriptionSnapshot
        {
            StateById = items.ToDictionary(
                pair => pair.Key,
                pair => JsonConvert.SerializeObject(pair.Value, Formatting.None),
                StringComparer.Ordinal),
            UidById = items.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Uid ?? string.Empty,
                StringComparer.Ordinal)
        };
    }

    static bool SameSnapshot(SubscriptionSnapshot left, SubscriptionSnapshot right)
    {
        if (left?.StateById == null || right?.StateById == null)
            return false;
        if (left.StateById.Count != right.StateById.Count)
            return false;

        foreach (var pair in left.StateById)
        {
            if (!right.StateById.TryGetValue(pair.Key, out string state)
                || !string.Equals(pair.Value, state, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    static List<RemovedSubscription> RemovedSubscriptions(
        SubscriptionSnapshot before,
        SubscriptionSnapshot after)
    {
        if (before?.UidById == null)
            return new List<RemovedSubscription>();

        var afterOwners = after?.UidById ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return before.UidById
            .Where(pair => !afterOwners.TryGetValue(pair.Key, out string currentUid)
                || !string.Equals(currentUid, pair.Value, StringComparison.Ordinal))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new RemovedSubscription
            {
                Id = pair.Key,
                Uid = pair.Value
            })
            .ToList();
    }

    static void BindSubscriptionParameters(SqliteCommand command, TranslationSubscription item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$uid", (object)item.Uid ?? DBNull.Value);
        command.Parameters.AddWithValue("$content_id", (object)item.ContentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object)item.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$original_title", (object)item.OriginalTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$kp_id", (object)item.KpId ?? DBNull.Value);
        command.Parameters.AddWithValue("$imdb_id", (object)item.ImdbId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdb_id", (object)item.TmdbId ?? DBNull.Value);
        command.Parameters.AddWithValue("$poster", (object)item.Poster ?? DBNull.Value);
        command.Parameters.AddWithValue("$year", (object)item.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_serial", item.IsSerial ? 1 : 0);
        command.Parameters.AddWithValue("$source", (object)item.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("$translation_id", (object)item.TranslationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$translation_name", (object)item.TranslationName ?? DBNull.Value);
        command.Parameters.AddWithValue("$current_season", (object)item.CurrentSeason ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_season", (object)item.LastSeason ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_episode", (object)item.LastEpisode ?? DBNull.Value);
        command.Parameters.AddWithValue("$sources_json", JsonConvert.SerializeObject(item.Sources ?? new List<TranslationSubscriptionSource>()));
        command.Parameters.AddWithValue("$created_at", TranslationSubDatabase.DateTimeText(item.CreatedAt));
        command.Parameters.AddWithValue("$last_checked_at", TranslationSubDatabase.DateTimeValue(item.LastCheckedAt));
        command.Parameters.AddWithValue("$tmdb_status", (object)item.TmdbStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdb_last_season", (object)item.TmdbLastSeason ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdb_last_episode", (object)item.TmdbLastEpisode ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdb_last_air_date", TranslationSubDatabase.DateTimeValue(item.TmdbLastAirDate));
        command.Parameters.AddWithValue("$tmdb_next_season", (object)item.TmdbNextSeason ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdb_next_episode", (object)item.TmdbNextEpisode ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdb_next_air_date", TranslationSubDatabase.DateTimeValue(item.TmdbNextAirDate));
        command.Parameters.AddWithValue("$tmdb_target_season_episodes", (object)item.TmdbTargetSeasonEpisodes ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdb_last_synced_at", TranslationSubDatabase.DateTimeValue(item.TmdbLastSyncedAt));
        command.Parameters.AddWithValue("$schedule_state", (object)item.ScheduleState ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdb_new_season_available", item.TmdbNewSeasonAvailable ? 1 : 0);
    }

    static void InsertSubscription(SqliteConnection connection, SqliteTransaction transaction, TranslationSubscription item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO subscriptions (
    id, uid, content_id, title, original_title, kp_id, imdb_id, tmdb_id,
    poster, year, is_serial, source, translation_id, translation_name,
    current_season, last_season, last_episode, sources_json, created_at,
    last_checked_at, tmdb_status, tmdb_last_season, tmdb_last_episode,
    tmdb_last_air_date, tmdb_next_season, tmdb_next_episode, tmdb_next_air_date,
    tmdb_target_season_episodes, tmdb_last_synced_at, schedule_state,
    tmdb_new_season_available
) VALUES (
    $id, $uid, $content_id, $title, $original_title, $kp_id, $imdb_id, $tmdb_id,
    $poster, $year, $is_serial, $source, $translation_id, $translation_name,
    $current_season, $last_season, $last_episode, $sources_json, $created_at,
    $last_checked_at, $tmdb_status, $tmdb_last_season, $tmdb_last_episode,
    $tmdb_last_air_date, $tmdb_next_season, $tmdb_next_episode, $tmdb_next_air_date,
    $tmdb_target_season_episodes, $tmdb_last_synced_at, $schedule_state,
    $tmdb_new_season_available
);";
        BindSubscriptionParameters(command, item);
        command.ExecuteNonQuery();
    }

    static void UpdateSubscription(SqliteConnection connection, SqliteTransaction transaction, TranslationSubscription item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
UPDATE subscriptions SET
    uid = $uid,
    content_id = $content_id,
    title = $title,
    original_title = $original_title,
    kp_id = $kp_id,
    imdb_id = $imdb_id,
    tmdb_id = $tmdb_id,
    poster = $poster,
    year = $year,
    is_serial = $is_serial,
    source = $source,
    translation_id = $translation_id,
    translation_name = $translation_name,
    current_season = $current_season,
    last_season = $last_season,
    last_episode = $last_episode,
    sources_json = $sources_json,
    created_at = $created_at,
    last_checked_at = $last_checked_at,
    tmdb_status = $tmdb_status,
    tmdb_last_season = $tmdb_last_season,
    tmdb_last_episode = $tmdb_last_episode,
    tmdb_last_air_date = $tmdb_last_air_date,
    tmdb_next_season = $tmdb_next_season,
    tmdb_next_episode = $tmdb_next_episode,
    tmdb_next_air_date = $tmdb_next_air_date,
    tmdb_target_season_episodes = $tmdb_target_season_episodes,
    tmdb_last_synced_at = $tmdb_last_synced_at,
    schedule_state = $schedule_state,
    tmdb_new_season_available = $tmdb_new_season_available
WHERE id = $id;";
        BindSubscriptionParameters(command, item);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"TranslationSub subscription '{item.Id}' disappeared during update.");
    }

    static void SaveUnsafe(
        List<TranslationSubscription> list,
        SubscriptionSnapshot before,
        SubscriptionSnapshot after)
    {
        var afterItems = PersistableById(list);
        var beforeStates = before?.StateById ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var afterStates = after?.StateById ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var removedSubscriptions = RemovedSubscriptions(before, after);
        var deletedIds = beforeStates.Keys
            .Where(id => !afterItems.ContainsKey(id))
            .ToArray();
        var inserted = afterItems
            .Where(pair => !beforeStates.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        var updated = afterItems
            .Where(pair => beforeStates.TryGetValue(pair.Key, out string oldState)
                && afterStates.TryGetValue(pair.Key, out string newState)
                && !string.Equals(oldState, newState, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .ToArray();

        using var connection = TranslationSubDatabase.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var removed in removedSubscriptions)
        {
            using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = @"
DELETE FROM profile_progress
WHERE uid = $uid AND subscription_id = $subscription_id;";
            cleanup.Parameters.AddWithValue("$uid", removed.Uid);
            cleanup.Parameters.AddWithValue("$subscription_id", removed.Id);
            cleanup.ExecuteNonQuery();
        }

        foreach (string id in deletedIds)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM subscriptions WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        foreach (var item in updated)
            UpdateSubscription(connection, transaction, item);

        foreach (var item in inserted)
            InsertSubscription(connection, transaction, item);

        transaction.Commit();
    }

    public static IDisposable BeginBatch()
    {
        var batch = ambientBatch.Value;
        if (batch != null)
        {
            batch.Depth++;
            return new MutationBatchScope(batch);
        }

        batch = new MutationBatch();
        ambientBatch.Value = batch;
        return new MutationBatchScope(batch);
    }

    public static List<TranslationSubscription> Load()
    {
        lock (TranslationSubDatabase.SyncRoot)
            return LoadUnsafe();
    }

    public static void Mutate(Action<List<TranslationSubscription>> action)
    {
        if (action == null)
            return;

        var batch = ambientBatch.Value;
        if (batch != null)
        {
            batch.Operations.Add(action);
            return;
        }

        string[] changedUids;
        lock (TranslationSubDatabase.SyncRoot)
        {
            var list = LoadUnsafe();
            var before = CaptureSnapshot(list);
            var beforeShared = SharedStateByUid(list);
            action(list);
            var after = CaptureSnapshot(list);

            if (SameSnapshot(before, after))
                return;

            var afterShared = SharedStateByUid(list);
            SaveUnsafe(list, before, after);
            changedUids = ChangedUids(beforeShared, afterShared);
        }

        PublishSharedChanges(changedUids);
    }

    public static bool MutateIfChanged(Func<List<TranslationSubscription>, bool> action)
    {
        if (action == null)
            return false;

        var batch = ambientBatch.Value;
        if (batch != null)
        {
            batch.Operations.Add(list => action(list));
            return true;
        }

        string[] changedUids;
        lock (TranslationSubDatabase.SyncRoot)
        {
            var list = LoadUnsafe();
            var before = CaptureSnapshot(list);
            var beforeShared = SharedStateByUid(list);
            if (!action(list))
                return false;

            var after = CaptureSnapshot(list);
            if (SameSnapshot(before, after))
                return false;

            var afterShared = SharedStateByUid(list);
            SaveUnsafe(list, before, after);
            changedUids = ChangedUids(beforeShared, afterShared);
        }

        PublishSharedChanges(changedUids);
        return true;
    }

    static void FlushBatch(MutationBatch batch)
    {
        if (batch?.Operations == null || batch.Operations.Count == 0)
            return;

        string[] changedUids;
        lock (TranslationSubDatabase.SyncRoot)
        {
            var list = LoadUnsafe();
            var before = CaptureSnapshot(list);
            var beforeShared = SharedStateByUid(list);

            foreach (var operation in batch.Operations)
            {
                try
                {
                    operation?.Invoke(list);
                }
                catch
                {
                    // Scheduler mutations are isolated per subscription. One stale
                    // or invalid operation must not discard the rest of the batch.
                }
            }

            var after = CaptureSnapshot(list);
            if (SameSnapshot(before, after))
                return;

            var afterShared = SharedStateByUid(list);
            SaveUnsafe(list, before, after);
            changedUids = ChangedUids(beforeShared, afterShared);
        }

        PublishSharedChanges(changedUids);
    }

    static Dictionary<string, string> SharedStateByUid(IEnumerable<TranslationSubscription> list)
    {
        return (list ?? Enumerable.Empty<TranslationSubscription>())
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Uid))
            .GroupBy(x => x.Uid.Trim(), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => JsonConvert.SerializeObject(group
                    .OrderBy(x => x.Id, StringComparer.Ordinal)
                    .Select(x => new
                    {
                        x.Id,
                        x.ContentId,
                        x.Title,
                        x.OriginalTitle,
                        x.KpId,
                        x.ImdbId,
                        x.TmdbId,
                        x.Poster,
                        x.Year,
                        x.IsSerial,
                        x.Source,
                        x.TranslationId,
                        x.TranslationName,
                        x.CurrentSeason,
                        x.LastSeason,
                        x.LastEpisode,
                        Sources = (x.Sources ?? new List<TranslationSubscriptionSource>())
                            .Where(source => source != null)
                            .OrderBy(source => source.Source, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(source => source.TranslationId, StringComparer.Ordinal)
                            .Select(source => new
                            {
                                source.Source,
                                source.TranslationId,
                                source.TranslationName
                            }),
                        x.TmdbStatus,
                        x.TmdbLastSeason,
                        x.TmdbLastEpisode,
                        x.TmdbLastAirDate,
                        x.TmdbNextSeason,
                        x.TmdbNextEpisode,
                        x.TmdbNextAirDate,
                        x.TmdbTargetSeasonEpisodes,
                        x.ScheduleState,
                        x.TmdbNewSeasonAvailable
                    })),
                StringComparer.Ordinal);
    }

    static string[] ChangedUids(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var keys = new HashSet<string>(before.Keys, StringComparer.Ordinal);
        keys.UnionWith(after.Keys);

        return keys
            .Where(uid => !before.TryGetValue(uid, out string oldState)
                || !after.TryGetValue(uid, out string newState)
                || !string.Equals(oldState, newState, StringComparison.Ordinal))
            .ToArray();
    }

    static void PublishSharedChanges(IEnumerable<string> uids)
    {
        foreach (string uid in uids ?? Enumerable.Empty<string>())
            _ = TranslationSubRealtimeService.PublishUid(uid, "subscription");
    }
}
