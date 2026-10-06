namespace XinSpect;

/// <summary>應用程式介面語言。</summary>
public enum AppLanguage
{
    /// <summary>繁體中文（預設，原文無損）。</summary>
    Traditional = 0,
    /// <summary>简体中文（LCMapStringEx 轉換＋詞組表）。</summary>
    Simplified = 1,
    /// <summary>English（翻譯表查找；未翻譯的字串回退繁體中文）。</summary>
    English = 2,
}
