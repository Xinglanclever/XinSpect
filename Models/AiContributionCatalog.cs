namespace XinSpect;

/// <summary>「關於」頁顯示的協力排名。名次是明確提供的順序，不推導 Token 數量。</summary>
public readonly record struct AiContributionRank(int Rank, string Name)
{
    public string RankLabel => $"#{Rank}";
}

/// <summary>
/// AI 協力家族與型號排名。專案沒有保存跨工作階段的供應商／型號用量，
/// 因此此目錄只記錄名次，不建立或暗示 Token 用量比例。
/// </summary>
public static class AiContributionCatalog
{
    public const string UsageAvailabilityNote =
        "目前沒有按供應商與型號彙總的歷史 Token 數據。名次依指定順序排列，不代表消耗量或比例。";

    public static IReadOnlyList<AiContributionRank> Providers { get; } =
    [
        new(1, "Claude"),
        new(2, "ChatGPT"),
        new(3, "GLM"),
        new(4, "DeepSeek"),
        new(5, "Qwen"),
        new(6, "Gemini"),
    ];

    public static IReadOnlyList<AiContributionRank> Models { get; } =
    [
        new(1, "Claude Opus 5"),
        new(2, "ChatGPT 6.1 Sol"),
        new(3, "GLM 5.3 Flash"),
        new(4, "Claude Opus 4.6"),
        new(5, "ChatGPT 5.6 Sol"),
        new(6, "Claude Opus 4.8"),
        new(7, "DeepSeek V4.1 Flash"),
        new(8, "Qwen 3.8 Flash"),
        new(9, "GLM 5.3"),
    ];
}
