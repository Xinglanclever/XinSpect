namespace XinSpect;

/// <summary>Localizes catalogued product prose while leaving unknown/provider text byte-for-byte intact.</summary>
internal static class ProductTextProjection
{
    internal static string LocalizeKnown(string text)
        => LocalizeKnown(text, LanguageService.IsEnglish, LanguageService.IsSimplified);

    internal static string LocalizeKnown(string text, bool english, bool simplified)
    {
        if (string.IsNullOrEmpty(text)) return text;
        bool known = EnglishStrings.Lookup(text) is not null
            || UiTextTranslation.HasAnchoredTemplateMatch(text);
        return known
            ? UiTextTranslation.KnownDisplayText(text, english, simplified)
            : text;
    }
}
