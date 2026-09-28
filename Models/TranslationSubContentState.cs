using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace TranslationSub.Models;

public class TranslationSubResolvedContent
{
    [JsonProperty("contentId")]
    public string ContentId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("originalTitle")]
    public string OriginalTitle { get; set; }

    [JsonProperty("tmdbId")]
    public string TmdbId { get; set; }

    [JsonProperty("imdbId")]
    public string ImdbId { get; set; }

    [JsonProperty("kpId")]
    public string KpId { get; set; }

    [JsonProperty("poster")]
    public string Poster { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("isSerial")]
    public bool IsSerial { get; set; }

    [JsonProperty("season")]
    public int Season { get; set; }
}

public class TranslationSubContentState
{
    [JsonProperty("eligible")]
    public bool Eligible { get; set; }

    [JsonProperty("reason")]
    public string Reason { get; set; }

    [JsonProperty("content")]
    public TranslationSubResolvedContent Content { get; set; }

    [JsonProperty("button")]
    public TranslationSubContentButtonState Button { get; set; } = new();

    [JsonProperty("voices")]
    public List<TranslationSubVoiceState> Voices { get; set; } = new();
}

public class TranslationSubContentButtonState
{
    [JsonProperty("subscribed")]
    public bool Subscribed { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }
}

public class TranslationSubVoiceState
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("latestEpisode")]
    public int LatestEpisode { get; set; }

    [JsonProperty("subscribed")]
    public bool Subscribed { get; set; }

    [JsonProperty("subscriptionId")]
    public string SubscriptionId { get; set; }

    [JsonProperty("action")]
    public string Action { get; set; }

    [JsonProperty("subtitle")]
    public string Subtitle { get; set; }

    [JsonProperty("sources")]
    public List<TranslationSubSourceSnapshot> Sources { get; set; } = new();
}

public class TranslationSubSubscribeIntent
{
    [JsonProperty("card")]
    public JObject Card { get; set; }

    [JsonProperty("voiceId")]
    public string VoiceId { get; set; }

    [JsonProperty("voiceName")]
    public string VoiceName { get; set; }
}

public class TranslationSubCommandResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("error")]
    public string Error { get; set; }

    [JsonProperty("subscriptionId")]
    public string SubscriptionId { get; set; }

    [JsonProperty("snapshot")]
    public TranslationSubSnapshot Snapshot { get; set; }
}
