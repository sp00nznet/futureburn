namespace Futureburn.Core.Tools;

// DVD-Video stores audio / subpicture language as a two-letter ISO 639-1 code.
// MKV (and most container metadata) tags streams with three-letter ISO 639-2
// codes — "eng", "fra", "deu". This maps the common ones down to two letters.

public static class IsoLanguage
{
    // ISO 639-2/B and 639-2/T → ISO 639-1, for the languages a home user is
    // realistically going to have on a disc. Unknown codes fall back below.
    private static readonly Dictionary<string, string> ThreeToTwo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["fra"] = "fr", ["fre"] = "fr", ["deu"] = "de", ["ger"] = "de",
        ["spa"] = "es", ["ita"] = "it", ["por"] = "pt", ["nld"] = "nl", ["dut"] = "nl",
        ["rus"] = "ru", ["jpn"] = "ja", ["zho"] = "zh", ["chi"] = "zh", ["kor"] = "ko",
        ["ara"] = "ar", ["hin"] = "hi", ["swe"] = "sv", ["nor"] = "no", ["dan"] = "da",
        ["fin"] = "fi", ["pol"] = "pl", ["ces"] = "cs", ["cze"] = "cs", ["ell"] = "el",
        ["gre"] = "el", ["heb"] = "he", ["tur"] = "tr", ["tha"] = "th", ["vie"] = "vi",
        ["hun"] = "hu", ["ron"] = "ro", ["rum"] = "ro", ["ukr"] = "uk", ["ind"] = "id",
    };

    /// <summary>
    /// Best-effort two-letter language code for a DVD <c>lang</c> attribute.
    /// Accepts a 3-letter ISO 639-2 code, an already-2-letter code, or null;
    /// falls back to <paramref name="fallback"/> (default "en") when unknown.
    /// </summary>
    public static string To2Letter(string? code, string fallback = "en")
    {
        if (string.IsNullOrWhiteSpace(code)) return fallback;
        code = code.Trim();

        if (code.Length == 2)
            return code.ToLowerInvariant();

        if (code.Length == 3 && ThreeToTwo.TryGetValue(code, out var two))
            return two;

        // "und" (undetermined) and anything else we don't recognize.
        return fallback;
    }
}
