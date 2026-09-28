using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TranslationSub.Models;

namespace TranslationSub.Services;

/// <summary>
/// Builds the canonical profile-aware TranslationSub read model consumed by
/// thin clients. All progress/update/schedule/navigation/display decisions belong
/// here, not in JavaScript.
/// </summary>
public static class TranslationSubSnapshotService
{
    static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    public static TranslationSubSnapshot Build(string uid, string profileId)
    {
        profileId = ProfileProgressStore.NormalizeProfileId(profileId);
        var subscriptions = TranslationSubProjectionService.ForProfile(uid, profileId)
            .Select(ToSnapshot)
            .OrderByDescending(x => x.HasNewEpisodes)
            .ThenBy(x => x.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.TranslationName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var updates = subscriptions
            .Where(x => x.HasNewEpisodes)
            .ToList();

        return new TranslationSubSnapshot
        {
            GeneratedAt = DateTime.Now,
            ProfileId = profileId,
            Badge = new TranslationSubBadgeSnapshot
            {
                Count = updates.Count
            },
            Subscriptions = subscriptions,
            Updates = updates
        };
    }

    static TranslationSubSubscriptionSnapshot ToSnapshot(TranslationSubProfileProjection projection)
    {
        var sub = projection?.Subscription;
        if (sub == null)
            return new TranslationSubSubscriptionSnapshot();

        int watched = Math.Max(0, projection.WatchedEpisode);
        int available = Math.Max(0, sub.LastEpisode.GetValueOrDefault(0));
        bool hasNew = available > watched;
        int season = Math.Max(1, sub.LastSeason ?? sub.CurrentSeason ?? 1);
        int from = hasNew ? watched + 1 : 0;
        int to = hasNew ? available : 0;
        int newCount = Math.Max(0, available - watched);
        int progressPercent = available > 0
            ? Math.Max(0, Math.Min(100, (int)Math.Round(watched * 100d / available)))
            : 0;
        var schedule = BuildSchedule(sub, available);
        var sources = (sub.Sources ?? new List<TranslationSubscriptionSource>())
            .Where(x => x != null)
            .Select(x => new TranslationSubSourceSnapshot
            {
                Source = x.Source,
                TranslationId = x.TranslationId,
                TranslationName = x.TranslationName
            })
            .ToList();

        return new TranslationSubSubscriptionSnapshot
        {
            Id = sub.Id,
            ContentId = sub.ContentId,
            Title = sub.Title,
            OriginalTitle = sub.OriginalTitle,
            KpId = sub.KpId,
            ImdbId = sub.ImdbId,
            TmdbId = sub.TmdbId,
            Poster = sub.Poster,
            Year = sub.Year,
            IsSerial = sub.IsSerial,
            Navigation = BuildNavigation(sub),
            Season = season,
            WatchedEpisode = watched,
            AvailableEpisode = available,
            HasNewEpisodes = hasNew,
            FromEpisode = from,
            ToEpisode = to,
            NewCount = newCount,
            ProgressPercent = progressPercent,
            Source = sub.Source,
            Sources = sources,
            TranslationId = sub.TranslationId,
            TranslationName = sub.TranslationName,
            LastCheckedAt = sub.LastCheckedAt,
            Schedule = schedule,
            Tmdb = new TranslationSubTmdbSnapshot
            {
                Status = sub.TmdbStatus,
                LastSeason = sub.TmdbLastSeason,
                LastEpisode = sub.TmdbLastEpisode,
                LastAirDate = sub.TmdbLastAirDate,
                NextSeason = sub.TmdbNextSeason,
                NextEpisode = sub.TmdbNextEpisode,
                NextAirDate = sub.TmdbNextAirDate,
                TargetSeasonEpisodes = sub.TmdbTargetSeasonEpisodes,
                LastSyncedAt = sub.TmdbLastSyncedAt,
                NewSeasonAvailable = sub.TmdbNewSeasonAvailable
            },
            Display = BuildDisplay(
                sub,
                season,
                watched,
                available,
                hasNew,
                from,
                to,
                newCount,
                sources)
        };
    }

    static TranslationSubNavigationSnapshot BuildNavigation(TranslationSubscription sub)
    {
        if (sub == null)
            return null;

        string id = (sub.TmdbId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            string fallback = (sub.ContentId ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fallback) && fallback.All(char.IsDigit))
                id = fallback;
        }

        if (string.IsNullOrWhiteSpace(id))
            return null;

        return new TranslationSubNavigationSnapshot
        {
            Id = id,
            Method = sub.IsSerial ? "tv" : "movie"
        };
    }

    static TranslationSubDisplaySnapshot BuildDisplay(
        TranslationSubscription sub,
        int season,
        int watched,
        int available,
        bool hasNew,
        int from,
        int to,
        int newCount,
        List<TranslationSubSourceSnapshot> sources)
    {
        var labels = (sources ?? new List<TranslationSubSourceSnapshot>())
            .Select(x => (x?.Source ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (labels.Count == 0 && !string.IsNullOrWhiteSpace(sub?.Source))
            labels.Add(sub.Source.Trim());

        string progress = hasNew
            ? "Можно смотреть E" + from + (to > from ? "–E" + to : string.Empty)
            : "Новых серий пока нет";

        string voice = string.IsNullOrWhiteSpace(sub?.TranslationName)
            ? "Озвучка"
            : sub.TranslationName.Trim();

        int aired = Math.Max(0, sub?.TmdbTargetSeasonEpisodes.GetValueOrDefault(0) ?? 0);
        string tmdbFacts = aired > 0
            ? "TMDB · вышло S" + season + "E" + aired
            : !string.IsNullOrWhiteSpace(sub?.TmdbStatus)
                ? "TMDB · " + sub.TmdbStatus.Trim()
                : null;

        string tmdbNext = null;
        int nextSeason = Math.Max(0, sub?.TmdbNextSeason.GetValueOrDefault(0) ?? 0);
        int nextEpisode = Math.Max(0, sub?.TmdbNextEpisode.GetValueOrDefault(0) ?? 0);
        if (sub?.TmdbNextAirDate != null && nextSeason > 0 && nextEpisode > 0)
        {
            string date = sub.TmdbNextAirDate.Value.ToString("d MMM", RuCulture).Replace(".", string.Empty);
            tmdbNext = "Следующая S" + nextSeason + "E" + nextEpisode + " · " + date;
        }

        return new TranslationSubDisplaySnapshot
        {
            Season = "S" + season,
            Watched = "Просмотрено E" + watched,
            Available = "В озвучке E" + available,
            Progress = progress,
            Source = string.Join(", ", labels),
            SourceLabels = labels,
            NewBadge = newCount > 0 ? newCount + " НОВЫХ" : null,
            NoticeTime = newCount > 0 ? newCount + " новых" : "S" + season,
            NoticeRange = hasNew
                ? "S" + season + " · просмотрено E" + watched + " · доступны E" + from + (to > from ? "–E" + to : string.Empty)
                : "S" + season + " · " + voice,
            TmdbFacts = tmdbFacts,
            TmdbNext = tmdbNext,
            TmdbTitle = sub?.TmdbLastSyncedAt != null
                ? "TMDB обновлён: " + sub.TmdbLastSyncedAt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : null
        };
    }

    static TranslationSubScheduleSnapshot BuildSchedule(TranslationSubscription sub, int available)
    {
        int aired = Math.Max(0, sub.TmdbTargetSeasonEpisodes.GetValueOrDefault(0));
        int tmdbLastSeason = Math.Max(0, sub.TmdbLastSeason.GetValueOrDefault(0));
        int nextSeason = Math.Max(0, sub.TmdbNextSeason.GetValueOrDefault(0));
        string code = (sub.ScheduleState ?? string.Empty).Trim();

        if (sub.TmdbNewSeasonAvailable)
        {
            int season = Math.Max(tmdbLastSeason, nextSeason);
            return Schedule(code, season > 0
                ? "Новый сезон S" + season + " уже начался"
                : "Новый сезон уже начался", "new-season");
        }

        if (code == "active_dubbing")
        {
            if (aired > available)
            {
                string text = "Ждём озвучку E" + (available + 1);
                if (aired > available + 1)
                    text += "–E" + aired;
                return Schedule(code, text, "active");
            }

            return Schedule(code, "Ждём обновление озвучки", "active");
        }

        return code switch
        {
            "waiting_air" => Schedule(code, "Балансеры спят до выхода следующей серии", "sleep"),
            "ended" => Schedule(code, "Сериал завершён · балансеры спят", "sleep"),
            "new_season" => Schedule(code, "Новый сезон уже начался", "new-season"),
            "season_complete" => Schedule(code, "Сезон завершён · ждём TMDB", "sleep"),
            "waiting_tmdb" => Schedule(code, "Озвучка догнала вышедшие серии", "ok"),
            "tmdb_unavailable" => Schedule(code, "TMDB временно недоступен · используется резервный интервал", "warn"),
            "sources_disabled" => Schedule(code, "Балансеры отключены", "warn"),
            "interval_wait" => Schedule(code, "Умное расписание TMDB отключено", "plain"),
            _ when aired > 0 => Schedule(code, "TMDB отслеживает сезон", "plain"),
            _ => null
        };
    }

    static TranslationSubScheduleSnapshot Schedule(string code, string text, string type)
        => new()
        {
            Code = code,
            Text = text,
            Type = type
        };
}
