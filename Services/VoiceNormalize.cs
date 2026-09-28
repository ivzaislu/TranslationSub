using System.Text.RegularExpressions;

namespace TranslationSub.Services;

public static class VoiceNormalize
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.ToLowerInvariant();
        value = Regex.Replace(value, "[^a-zа-яё0-9]+", " ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\s+", " ").Trim();

        return value;
    }
}
