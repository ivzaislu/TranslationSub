using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TranslationSub.Models;

namespace TranslationSub.Services;

public static class LampacMetadataService
{
    public static async Task<TranslationVariantsResponse> GetVariants(TranslationMetadataQuery query, HttpContext httpContext = null)
    {
        if (ModInit.conf?.enable != true || query == null || query.Sources?.Count == 0)
            return new TranslationVariantsResponse();

        IReadOnlyList<LampacVoiceMetadata> metadata;
        try
        {
            metadata = await LampacMetadataClient.ReadAsync(query, httpContext).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "TranslationSub metadata variants failed");
            metadata = Array.Empty<LampacVoiceMetadata>();
        }

        if (metadata == null || metadata.Count == 0)
        {
            Serilog.Log.Warning(
                "TranslationSub metadata returned zero voices. Sources={Sources}; Season={Season}; Serial={Serial}",
                string.Join(",", query.Sources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                query.Season,
                query.IsSerial);
            metadata = Array.Empty<LampacVoiceMetadata>();
        }

        var raw = metadata
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.VoiceName))
            .Where(x => !query.IsSerial || query.Season <= 0 || x.Season == query.Season)
            .Select(x =>
            {
                var episodes = (x.Episodes ?? new List<int>())
                    .Where(e => e > 0)
                    .Distinct()
                    .OrderBy(e => e)
                    .ToList();

                return new TranslationVariant
                {
                    source = x.Source,
                    translation = x.VoiceName,
                    translation_id = SourceVoiceId(x.VoiceId, x.VoiceName, x.Source),
                    season = x.Season,
                    episode = episodes.DefaultIfEmpty(query.IsSerial ? 0 : 1).Max(),
                    Episodes = episodes
                };
            })
            .ToList();

        if (metadata.Count > 0 && raw.Count == 0)
        {
            Serilog.Log.Warning(
                "TranslationSub metadata voices were filtered to zero variants. Metadata={MetadataCount}; Season={Season}; Serial={Serial}",
                metadata.Count,
                query.Season,
                query.IsSerial);
        }

        var combined = raw
            .GroupBy(x => new
            {
                x.season,
                voice = CombinedVoiceId(x.translation, x.source)
            })
            .Select(group =>
            {
                var episodes = group
                    .SelectMany(x => x.Episodes ?? new List<int>())
                    .Where(e => e > 0)
                    .Distinct()
                    .OrderBy(e => e)
                    .ToList();

                var best = group
                    .OrderByDescending(x => x.episode)
                    .ThenBy(x => x.source, StringComparer.OrdinalIgnoreCase)
                    .First();

                var sources = group
                    .GroupBy(x => x.source ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.episode).First())
                    .Select(x => new TranslationVariantSource
                    {
                        Source = x.source,
                        TranslationId = x.translation_id,
                        TranslationName = x.translation
                    })
                    .OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new TranslationVariant
                {
                    source = sources.Count > 1 ? "multi" : best.source,
                    translation = best.translation,
                    translation_id = group.Key.voice,
                    season = group.Key.season,
                    episode = episodes.DefaultIfEmpty(group.Max(x => x.episode)).Max(),
                    Episodes = episodes,
                    Sources = sources
                };
            })
            .OrderBy(x => x.season)
            .ThenBy(x => x.translation, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TranslationVariantsResponse
        {
            Translations = combined
        };
    }

    static string SourceVoiceId(string sourceId, string voiceName, string source)
    {
        if (!string.IsNullOrWhiteSpace(sourceId))
            return sourceId.Trim();

        string normalized = VoiceNormalize.Normalize(voiceName);
        if (!string.IsNullOrWhiteSpace(normalized)
            && !string.Equals(voiceName, "Неизвестно", StringComparison.OrdinalIgnoreCase))
            return normalized;

        return (source ?? "lampac") + ":default";
    }

    static string CombinedVoiceId(string voiceName, string source)
    {
        string normalized = VoiceNormalize.Normalize(voiceName);
        if (!string.IsNullOrWhiteSpace(normalized)
            && !string.Equals(voiceName, "Неизвестно", StringComparison.OrdinalIgnoreCase))
            return normalized;

        return (source ?? "lampac") + ":default";
    }
}
