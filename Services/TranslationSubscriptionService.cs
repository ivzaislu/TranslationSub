using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TranslationSub.Models;

namespace TranslationSub.Services;

public static class TranslationSubscriptionService
{
    static Timer timer;
    static readonly SemaphoreSlim tickGate = new(1, 1);

    public static void Start()
    {
        Stop();
        timer = new Timer(async _ => await Tick(), null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMinutes(Math.Max(5, ModInit.conf?.check_interval_minutes ?? 15)));
    }

    public static void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public static async Task Tick(string uid = null, HashSet<string> selectedSources = null, bool force = false)
    {
        force = force || !string.IsNullOrWhiteSpace(uid);

        bool entered = false;
        try
        {
            if (force)
            {
                await tickGate.WaitAsync().ConfigureAwait(false);
                entered = true;
            }
            else
            {
                entered = await tickGate.WaitAsync(0).ConfigureAwait(false);
                if (!entered)
                    return;
            }

            DateTime now = DateTime.Now;
            var snapshot = SubscriptionStore.Load();
            // Remember terminal series before polling: another season/voice might
            // still have an old active status for the same user's series.
            var terminalSubscriptions = snapshot
                .Where(x => x != null && x.IsSerial && IsEndedStatus(x.TmdbStatus))
                .ToList();
            using var storeBatch = SubscriptionStore.BeginBatch();
            var settingsCache = new Dictionary<string, TranslationUserSettings>(StringComparer.Ordinal);
            var tmdbCache = new Dictionary<string, TmdbScheduleSnapshot>(StringComparer.OrdinalIgnoreCase);
            var variantCache = new Dictionary<string, TranslationVariantsResponse>(StringComparer.Ordinal);

            foreach (var sub in snapshot)
            {
                if (!string.IsNullOrWhiteSpace(uid) && !string.Equals(sub.Uid, uid, StringComparison.Ordinal))
                    continue;

                try
                {
                    // Already-ended/cancelled series require no more TMDB or
                    // balancer requests, including during a forced check.
                    if (terminalSubscriptions.Any(x => SameSeries(x, sub)))
                        continue;

                    string settingsKey = sub.Uid?.Trim();
                    if (string.IsNullOrWhiteSpace(settingsKey))
                        continue;

                    if (!settingsCache.TryGetValue(settingsKey, out var settings))
                    {
                        settings = TranslationSettingsStore.Get(settingsKey);
                        settingsCache[settingsKey] = settings;
                    }

                    bool hasTmdbIdentity = sub.IsSerial
                        && (IsTmdbId(sub.TmdbId) || IsImdbId(sub.ImdbId));
                    bool smartTmdb = settings.UseTmdbSchedule && hasTmdbIdentity;
                    TmdbScheduleSnapshot tmdb = null;

                    // TMDB status checks remain active even when smart scheduling
                    // is off: terminal series must still be automatically removed.
                    if (hasTmdbIdentity)
                    {
                        string identityKey = TmdbCacheKey(sub);
                        if (!string.IsNullOrWhiteSpace(identityKey)
                            && tmdbCache.TryGetValue(identityKey, out tmdb))
                        {
                            if (!IsEndedStatus(tmdb.Status))
                            {
                                ApplyTmdbState(sub.Id, tmdb, settings, now);
                                CopyTmdbState(sub, tmdb, settings, now);
                                HandleNewSeason(sub, tmdb, settings, now);
                            }
                            else
                                UpdateStatusOnly(sub, tmdb);
                        }
                        else if (force || ShouldRefreshTmdb(sub, settings, now))
                        {
                            tmdb = await TmdbScheduleService.Get(sub.TmdbId, sub.ImdbId).ConfigureAwait(false);
                            if (tmdb != null)
                            {
                                tmdbCache["tmdb:" + tmdb.TmdbId] = tmdb;
                                if (IsImdbId(tmdb.ImdbId))
                                    tmdbCache["imdb:" + tmdb.ImdbId.ToLowerInvariant()] = tmdb;
                                if (!string.IsNullOrWhiteSpace(identityKey))
                                    tmdbCache[identityKey] = tmdb;

                                if (!IsEndedStatus(tmdb.Status))
                                {
                                    ApplyTmdbState(sub.Id, tmdb, settings, now);
                                    CopyTmdbState(sub, tmdb, settings, now);
                                    HandleNewSeason(sub, tmdb, settings, now);
                                }
                                else
                                    UpdateStatusOnly(sub, tmdb);
                            }
                        }

                        if (IsEndedStatus(tmdb?.Status))
                        {
                            terminalSubscriptions.Add(sub);
                            // TMDB may resolve a corrected ID from IMDb; cover
                            // sibling subscriptions that already use that ID.
                            if (!string.IsNullOrWhiteSpace(tmdb.TmdbId))
                            {
                                terminalSubscriptions.Add(new TranslationSubscription
                                {
                                    Uid = sub.Uid,
                                    IsSerial = true,
                                    TmdbId = tmdb.TmdbId,
                                    ImdbId = tmdb.ImdbId
                                });
                            }
                            continue;
                        }

                        if (tmdb == null)
                            tmdb = SnapshotFromStored(sub);
                    }

                    int season = sub.IsSerial ? sub.CurrentSeason.GetValueOrDefault(1) : 0;
                    if (sub.IsSerial && season <= 0)
                        season = 1;

                    int expectedAired = smartTmdb ? ExpectedAiredEpisode(sub, tmdb, season, now) : 0;

                    if (!force)
                    {
                        if (smartTmdb && tmdb != null)
                        {
                            if (!ShouldPollBalancersSmart(sub, tmdb, settings, season, expectedAired, now, out string state))
                            {
                                SetScheduleState(sub.Id, state, expectedAired);
                                continue;
                            }
                        }
                        else if (!IntervalDue(sub.LastCheckedAt, settings.CheckIntervalHours, now))
                        {
                            SetScheduleState(sub.Id, smartTmdb ? "tmdb_unavailable" : "interval_wait", expectedAired);
                            continue;
                        }
                    }

                    HashSet<string> sources = ResolveSources(settings, selectedSources);
                    if (sources.Count == 0)
                    {
                        SetScheduleState(sub.Id, "sources_disabled", expectedAired);
                        continue;
                    }

                    long.TryParse(sub.KpId, out long kp);
                    var query = new TranslationMetadataQuery
                    {
                        Uid = sub.Uid,
                        ContentId = sub.ContentId,
                        TmdbId = sub.TmdbId,
                        ImdbId = sub.ImdbId,
                        KpId = kp,
                        Title = sub.Title,
                        OriginalTitle = sub.OriginalTitle,
                        Year = sub.Year.GetValueOrDefault(0),
                        IsSerial = sub.IsSerial,
                        Season = season,
                        Sources = sources
                    };

                    string cacheKey = BuildVariantCacheKey(query);
                    if (!variantCache.TryGetValue(cacheKey, out var response))
                    {
                        response = await LampacMetadataService.GetVariants(query).ConfigureAwait(false);
                        variantCache[cacheKey] = response;
                    }

                    var matches = response.Translations.Where(x =>
                        (!sub.IsSerial || x.season == season) &&
                        (
                            (!string.IsNullOrWhiteSpace(sub.TranslationId) && x.translation_id == sub.TranslationId) ||
                            (!string.IsNullOrWhiteSpace(sub.TranslationName) &&
                             VoiceNormalize.Normalize(x.translation) == VoiceNormalize.Normalize(sub.TranslationName))
                        )
                    ).ToList();

                    int latestEpisode = matches.Select(x => x.episode).DefaultIfEmpty(0).Max();
                    if (!sub.IsSerial && matches.Count > 0)
                        latestEpisode = Math.Max(latestEpisode, 1);
                    if (smartTmdb && expectedAired > 0)
                        latestEpisode = Math.Min(latestEpisode, expectedAired);

                    var best = matches.OrderByDescending(x => x.episode).FirstOrDefault();
                    var foundSources = best?.Sources?
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Source))
                        .Select(x => new TranslationSubscriptionSource
                        {
                            Source = x.Source,
                            TranslationId = x.TranslationId,
                            TranslationName = x.TranslationName
                        })
                        .ToList();

                    string subscriptionId = sub.Id;
                    bool isSerial = sub.IsSerial;

                    SubscriptionStore.Mutate(list =>
                    {
                        var current = list.FirstOrDefault(x => x.Id == subscriptionId);
                        if (current == null)
                            return;

                        if (foundSources != null && foundSources.Count > 0)
                        {
                            current.Sources = foundSources;
                            current.Source = foundSources.Count > 1 ? "multi" : foundSources[0].Source;
                        }

                        if (latestEpisode > current.LastEpisode.GetValueOrDefault(0))
                        {
                            current.LastEpisode = latestEpisode;
                            current.LastSeason = isSerial ? season : 0;
                        }

                        if (smartTmdb && expectedAired > 0 && current.LastEpisode.GetValueOrDefault(0) > expectedAired)
                            current.LastEpisode = expectedAired;

                        current.LastCheckedAt = now;
                        current.ScheduleState = ResolveAfterCheckState(current, tmdb, settings, expectedAired, now);
                    });
                }
                catch
                {
                    // One broken subscription or balancer must not stop the queue.
                }
            }

            if (terminalSubscriptions.Count > 0)
            {
                // SubscriptionStore cleans profile_progress for every deletion
                // in the SAME SQLite transaction. Lampac TimeCode is untouched.
                SubscriptionStore.Mutate(list => list.RemoveAll(sub =>
                    sub != null && sub.IsSerial
                    && terminalSubscriptions.Any(terminal => SameSeries(terminal, sub))));
            }
        }
        catch
        {
            // Timer callback must never terminate Lampac on storage/network failures.
        }
        finally
        {
            if (entered)
                tickGate.Release();
        }
    }

    static HashSet<string> ResolveSources(TranslationUserSettings settings, HashSet<string> selectedSources)
    {
        IEnumerable<string> values = selectedSources ?? settings?.Sources ?? Enumerable.Empty<string>();
        return values
            .Select(TranslationSettingsStore.NormalizeSourceId)
            .Where(x => x != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    static string BuildVariantCacheKey(TranslationMetadataQuery query)
    {
        string sources = string.Join(",", (query.Sources ?? new HashSet<string>())
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        return string.Join("|",
            query.Uid ?? string.Empty,
            query.ContentId ?? string.Empty,
            query.TmdbId ?? string.Empty,
            query.KpId,
            query.ImdbId ?? string.Empty,
            query.Title ?? string.Empty,
            query.OriginalTitle ?? string.Empty,
            query.Year,
            query.IsSerial ? 1 : 0,
            query.Season,
            sources);
    }

    static bool IsTmdbId(string value)
        => long.TryParse(value, out long id) && id > 0;

    static bool IsImdbId(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(value.Substring(2), out long id)
            && id > 0;
    }

    static string TmdbCacheKey(TranslationSubscription sub)
    {
        if (IsImdbId(sub.ImdbId))
            return "imdb:" + sub.ImdbId.Trim().ToLowerInvariant();
        if (IsTmdbId(sub.TmdbId))
            return "tmdb:" + sub.TmdbId.Trim();
        return string.Empty;
    }

    static void UpdateStatusOnly(TranslationSubscription sub, TmdbScheduleSnapshot tmdb)
    {
        string subscriptionId = sub.Id;
        SubscriptionStore.Mutate(list =>
        {
            var current = list.FirstOrDefault(x => x.Id == subscriptionId);
            if (current == null)
                return;

            current.TmdbStatus = tmdb.Status;
            current.TmdbLastSyncedAt = tmdb.SyncedAt;
            if (!string.IsNullOrWhiteSpace(tmdb.TmdbId))
                current.TmdbId = tmdb.TmdbId;
            if (!string.IsNullOrWhiteSpace(tmdb.ImdbId))
                current.ImdbId = tmdb.ImdbId;
        });

        sub.TmdbStatus = tmdb.Status;
        sub.TmdbLastSyncedAt = tmdb.SyncedAt;
        if (!string.IsNullOrWhiteSpace(tmdb.TmdbId))
            sub.TmdbId = tmdb.TmdbId;
        if (!string.IsNullOrWhiteSpace(tmdb.ImdbId))
            sub.ImdbId = tmdb.ImdbId;
    }

    static bool ShouldRefreshTmdb(TranslationSubscription sub, TranslationUserSettings settings, DateTime now)
    {
        // Terminal series are deleted, never scheduled for another TMDB poll.
        if (IsEndedStatus(sub.TmdbStatus))
            return false;
        if (!sub.TmdbLastSyncedAt.HasValue)
            return true;

        if (settings.UseTmdbSchedule && sub.TmdbNextAirDate.HasValue && sub.TmdbNextAirDate.Value.Date <= now.Date)
        {
            int lastSeason = sub.TmdbLastSeason.GetValueOrDefault(0);
            int lastEpisode = sub.TmdbLastEpisode.GetValueOrDefault(0);
            int nextSeason = sub.TmdbNextSeason.GetValueOrDefault(0);
            int nextEpisode = sub.TmdbNextEpisode.GetValueOrDefault(0);
            if (nextSeason > lastSeason || (nextSeason == lastSeason && nextEpisode > lastEpisode))
                return true;
        }

        TimeSpan interval = TimeSpan.FromHours(Math.Max(6, settings.TmdbRefreshHours));
        return now - sub.TmdbLastSyncedAt.Value >= interval;
    }

    static bool IntervalDue(DateTime? last, int hours, DateTime now)
        => !last.HasValue || now - last.Value >= TimeSpan.FromHours(Math.Max(1, Math.Min(24, hours)));

    static void ApplyTmdbState(string subscriptionId, TmdbScheduleSnapshot tmdb, TranslationUserSettings settings, DateTime now)
    {
        SubscriptionStore.Mutate(list =>
        {
            var current = list.FirstOrDefault(x => x.Id == subscriptionId);
            if (current == null)
                return;

            int season = current.CurrentSeason.GetValueOrDefault(1);
            int expected = ExpectedAiredEpisode(tmdb, season, now);

            current.TmdbId = tmdb.TmdbId;
            if (string.IsNullOrWhiteSpace(current.ImdbId) && IsImdbId(tmdb.ImdbId))
                current.ImdbId = tmdb.ImdbId;
            current.TmdbStatus = tmdb.Status;
            current.TmdbLastSeason = tmdb.LastSeason > 0 ? tmdb.LastSeason : null;
            current.TmdbLastEpisode = tmdb.LastEpisode > 0 ? tmdb.LastEpisode : null;
            current.TmdbLastAirDate = tmdb.LastAirDate;
            current.TmdbNextSeason = tmdb.NextSeason > 0 ? tmdb.NextSeason : null;
            current.TmdbNextEpisode = tmdb.NextEpisode > 0 ? tmdb.NextEpisode : null;
            current.TmdbNextAirDate = tmdb.NextAirDate;
            current.TmdbTargetSeasonEpisodes = expected > 0 ? expected : null;
            current.TmdbLastSyncedAt = tmdb.SyncedAt;

            if (expected > 0 && current.LastEpisode.GetValueOrDefault(0) > expected)
                current.LastEpisode = expected;

            current.TmdbNewSeasonAvailable = NewSeasonStarted(tmdb, season, now)
                && string.Equals(settings.NewSeasonMode, "notify", StringComparison.OrdinalIgnoreCase);
        });
    }

    static void CopyTmdbState(TranslationSubscription sub, TmdbScheduleSnapshot tmdb, TranslationUserSettings settings, DateTime now)
    {
        int season = sub.CurrentSeason.GetValueOrDefault(1);
        int expected = ExpectedAiredEpisode(tmdb, season, now);

        sub.TmdbId = tmdb.TmdbId;
        if (string.IsNullOrWhiteSpace(sub.ImdbId) && IsImdbId(tmdb.ImdbId))
            sub.ImdbId = tmdb.ImdbId;
        sub.TmdbStatus = tmdb.Status;
        sub.TmdbLastSeason = tmdb.LastSeason > 0 ? tmdb.LastSeason : null;
        sub.TmdbLastEpisode = tmdb.LastEpisode > 0 ? tmdb.LastEpisode : null;
        sub.TmdbLastAirDate = tmdb.LastAirDate;
        sub.TmdbNextSeason = tmdb.NextSeason > 0 ? tmdb.NextSeason : null;
        sub.TmdbNextEpisode = tmdb.NextEpisode > 0 ? tmdb.NextEpisode : null;
        sub.TmdbNextAirDate = tmdb.NextAirDate;
        sub.TmdbTargetSeasonEpisodes = expected > 0 ? expected : null;
        sub.TmdbLastSyncedAt = tmdb.SyncedAt;
        sub.TmdbNewSeasonAvailable = NewSeasonStarted(tmdb, season, now)
            && string.Equals(settings.NewSeasonMode, "notify", StringComparison.OrdinalIgnoreCase);

        if (expected > 0 && sub.LastEpisode.GetValueOrDefault(0) > expected)
            sub.LastEpisode = expected;
    }

    static TmdbScheduleSnapshot SnapshotFromStored(TranslationSubscription sub)
    {
        if (!sub.TmdbLastSyncedAt.HasValue)
            return null;

        return new TmdbScheduleSnapshot
        {
            TmdbId = sub.TmdbId,
            ImdbId = sub.ImdbId,
            Status = sub.TmdbStatus,
            LastSeason = sub.TmdbLastSeason.GetValueOrDefault(0),
            LastEpisode = sub.TmdbLastEpisode.GetValueOrDefault(0),
            LastAirDate = sub.TmdbLastAirDate,
            NextSeason = sub.TmdbNextSeason.GetValueOrDefault(0),
            NextEpisode = sub.TmdbNextEpisode.GetValueOrDefault(0),
            NextAirDate = sub.TmdbNextAirDate,
            SyncedAt = sub.TmdbLastSyncedAt.Value
        };
    }

    static int ExpectedAiredEpisode(TranslationSubscription sub, TmdbScheduleSnapshot tmdb, int season, DateTime now)
    {
        if (tmdb == null)
            return sub.TmdbTargetSeasonEpisodes.GetValueOrDefault(0);

        int value = ExpectedAiredEpisode(tmdb, season, now);
        if (value <= 0)
            value = sub.TmdbTargetSeasonEpisodes.GetValueOrDefault(0);
        return value;
    }

    static int ExpectedAiredEpisode(TmdbScheduleSnapshot tmdb, int season, DateTime now)
    {
        if (tmdb == null || season <= 0)
            return 0;

        int dueNext = 0;
        if (tmdb.NextSeason == season && tmdb.NextAirDate.HasValue && tmdb.NextAirDate.Value.Date <= now.Date)
            dueNext = Math.Max(1, tmdb.NextEpisode);

        if (tmdb.LastSeason > season)
            return tmdb.SeasonEpisodeCounts.TryGetValue(season, out int count) ? Math.Max(0, count) : dueNext;
        if (tmdb.LastSeason == season)
            return Math.Max(Math.Max(0, tmdb.LastEpisode), dueNext);
        return dueNext;
    }

    static bool ShouldPollBalancersSmart(
        TranslationSubscription sub,
        TmdbScheduleSnapshot tmdb,
        TranslationUserSettings settings,
        int season,
        int expectedAired,
        DateTime now,
        out string state)
    {
        int available = sub.LastEpisode.GetValueOrDefault(0);
        if (expectedAired > available)
        {
            state = "active_dubbing";
            return IntervalDue(sub.LastCheckedAt, settings.CheckIntervalHours, now);
        }
        if (tmdb.NextAirDate.HasValue && tmdb.NextAirDate.Value.Date > now.Date)
        {
            state = "waiting_air";
            return false;
        }
        if (IsEndedStatus(tmdb.Status))
        {
            state = "ended";
            return false;
        }
        if (NewSeasonStarted(tmdb, season, now))
        {
            state = string.Equals(settings.NewSeasonMode, "notify", StringComparison.OrdinalIgnoreCase)
                ? "new_season"
                : "season_complete";
            return false;
        }
        state = "waiting_tmdb";
        return false;
    }

    static string ResolveAfterCheckState(
        TranslationSubscription current,
        TmdbScheduleSnapshot tmdb,
        TranslationUserSettings settings,
        int expectedAired,
        DateTime now)
    {
        if (tmdb == null || !settings.UseTmdbSchedule)
            return "interval_wait";

        int available = current.LastEpisode.GetValueOrDefault(0);
        if (expectedAired > available)
            return "active_dubbing";
        if (tmdb.NextAirDate.HasValue && tmdb.NextAirDate.Value.Date > now.Date)
            return "waiting_air";
        if (IsEndedStatus(tmdb.Status))
            return "ended";
        if (NewSeasonStarted(tmdb, current.CurrentSeason.GetValueOrDefault(1), now))
            return string.Equals(settings.NewSeasonMode, "notify", StringComparison.OrdinalIgnoreCase)
                ? "new_season"
                : "season_complete";
        return "waiting_tmdb";
    }

    static bool NewSeasonStarted(TmdbScheduleSnapshot tmdb, int currentSeason, DateTime now)
    {
        if (tmdb == null)
            return false;
        if (tmdb.LastSeason > currentSeason && tmdb.LastAirDate.HasValue && tmdb.LastAirDate.Value.Date <= now.Date)
            return true;
        return tmdb.NextSeason > currentSeason
            && tmdb.NextAirDate.HasValue
            && tmdb.NextAirDate.Value.Date <= now.Date;
    }

    internal static bool IsEndedStatus(string status)
        => string.Equals(status, "Ended", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase);

    static bool SameSeries(TranslationSubscription left, TranslationSubscription right)
    {
        if (left == null || right == null || !left.IsSerial || !right.IsSerial
            || !string.Equals(left.Uid, right.Uid, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(left.Id)
            && string.Equals(left.Id, right.Id, StringComparison.Ordinal))
            return true;

        // Match only explicit content identifiers; titles are not unique.
        return SameId(left.TmdbId, right.TmdbId)
            || SameId(left.ImdbId, right.ImdbId, ignoreCase: true)
            || SameId(left.KpId, right.KpId)
            || SameId(left.ContentId, right.ContentId);
    }

    static bool SameId(string left, string right, bool ignoreCase = false)
        => !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(),
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static void SetScheduleState(string subscriptionId, string state, int expectedAired)
    {
        SubscriptionStore.MutateIfChanged(list =>
        {
            var current = list.FirstOrDefault(x => x.Id == subscriptionId);
            if (current == null)
                return false;

            bool changed = false;
            if (!string.Equals(current.ScheduleState, state, StringComparison.Ordinal))
            {
                current.ScheduleState = state;
                changed = true;
            }
            if (expectedAired > 0 && current.TmdbTargetSeasonEpisodes.GetValueOrDefault(0) != expectedAired)
            {
                current.TmdbTargetSeasonEpisodes = expectedAired;
                changed = true;
            }
            return changed;
        });
    }

    static void HandleNewSeason(TranslationSubscription sub, TmdbScheduleSnapshot tmdb, TranslationUserSettings settings, DateTime now)
    {
        if (!sub.IsSerial || tmdb == null)
            return;

        int currentSeason = sub.CurrentSeason.GetValueOrDefault(1);
        if (!NewSeasonStarted(tmdb, currentSeason, now))
            return;

        string mode = settings.NewSeasonMode ?? "auto";
        if (!string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))
            return;

        int newSeason = tmdb.LastSeason > currentSeason ? tmdb.LastSeason : tmdb.NextSeason;
        if (newSeason <= currentSeason)
            return;

        int expected = ExpectedAiredEpisode(tmdb, newSeason, now);
        string normalizedVoice = VoiceNormalize.Normalize(sub.TranslationName);

        SubscriptionStore.MutateIfChanged(list =>
        {
            bool sourceExists = list.Any(x => x != null
                && string.Equals(x.Id, sub.Id, StringComparison.Ordinal)
                && string.Equals(x.Uid, sub.Uid, StringComparison.Ordinal));
            if (!sourceExists)
                return false;

            bool exists = list.Any(x => x != null
                && !string.Equals(x.Id, sub.Id, StringComparison.Ordinal)
                && string.Equals(x.Uid, sub.Uid, StringComparison.Ordinal)
                && x.ContentId == sub.ContentId
                && x.CurrentSeason.GetValueOrDefault(1) == newSeason
                && (
                    (!string.IsNullOrWhiteSpace(sub.TranslationId) && x.TranslationId == sub.TranslationId)
                    || (!string.IsNullOrWhiteSpace(normalizedVoice)
                        && VoiceNormalize.Normalize(x.TranslationName) == normalizedVoice)
                ));

            if (!exists)
            {
                list.Add(new TranslationSubscription
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Uid = sub.Uid,
                    ContentId = sub.ContentId,
                    Title = sub.Title,
                    OriginalTitle = sub.OriginalTitle,
                    KpId = sub.KpId,
                    ImdbId = sub.ImdbId,
                    TmdbId = sub.TmdbId,
                    Poster = sub.Poster,
                    Year = sub.Year,
                    IsSerial = true,
                    Source = sub.Source,
                    TranslationId = sub.TranslationId,
                    TranslationName = sub.TranslationName,
                    CurrentSeason = newSeason,
                    LastSeason = newSeason,
                    LastEpisode = 0,
                    Sources = sub.Sources?.Select(x => new TranslationSubscriptionSource
                    {
                        Source = x.Source,
                        TranslationId = x.TranslationId,
                        TranslationName = x.TranslationName
                    }).ToList() ?? new List<TranslationSubscriptionSource>(),
                    CreatedAt = now,
                    TmdbStatus = tmdb.Status,
                    TmdbLastSeason = tmdb.LastSeason > 0 ? tmdb.LastSeason : null,
                    TmdbLastEpisode = tmdb.LastEpisode > 0 ? tmdb.LastEpisode : null,
                    TmdbLastAirDate = tmdb.LastAirDate,
                    TmdbNextSeason = tmdb.NextSeason > 0 ? tmdb.NextSeason : null,
                    TmdbNextEpisode = tmdb.NextEpisode > 0 ? tmdb.NextEpisode : null,
                    TmdbNextAirDate = tmdb.NextAirDate,
                    TmdbTargetSeasonEpisodes = expected > 0 ? expected : null,
                    TmdbLastSyncedAt = tmdb.SyncedAt,
                    ScheduleState = "active_dubbing",
                    TmdbNewSeasonAvailable = false
                });
            }

            return list.RemoveAll(x => x != null
                && string.Equals(x.Id, sub.Id, StringComparison.Ordinal)
                && string.Equals(x.Uid, sub.Uid, StringComparison.Ordinal)) > 0;
        });
    }
}
