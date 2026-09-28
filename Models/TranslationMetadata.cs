using System.Collections.Generic;

namespace TranslationSub.Models;

public sealed class TranslationMetadataQuery
{
    public string Uid { get; set; }
    public string ContentId { get; set; }
    public string TmdbId { get; set; }
    public string ImdbId { get; set; }
    public long KpId { get; set; }
    public string Title { get; set; }
    public string OriginalTitle { get; set; }
    public int Year { get; set; }
    public bool IsSerial { get; set; }
    public int Season { get; set; }
    public HashSet<string> Sources { get; set; }
}

public sealed class LampacSourceOption
{
    public string id { get; set; }
    public string name { get; set; }
}

internal sealed class LampacVoiceMetadata
{
    public string Source { get; init; }
    public string VoiceId { get; init; }
    public string VoiceName { get; init; }
    public int Season { get; init; }
    public List<int> Episodes { get; init; } = new();
}
