using System.Collections.Generic;

namespace TranslationSub.Models;

public class TranslationVariant
{
    public string source { get; set; }
    public string translation { get; set; }
    public string translation_id { get; set; }
    public int season { get; set; }
    public int episode { get; set; }
    public List<int> Episodes { get; set; } = new();
    public List<TranslationVariantSource> Sources { get; set; } = new();
}

public class TranslationVariantSource
{
    public string Source { get; set; }
    public string TranslationId { get; set; }
    public string TranslationName { get; set; }
}

public class TranslationVariantsResponse
{
    public List<TranslationVariant> Translations { get; set; } = new();
}
