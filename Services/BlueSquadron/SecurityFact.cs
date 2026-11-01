using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>安全發現的嚴重等級。</summary>
public enum SecuritySeverity { Good, Advisory, Warning, Critical }

/// <summary>單一安全發現（一項可量測的安全事實及其判定）。</summary>
public sealed record SecurityFinding(
    string Id,                // e.g. "kernel.hvci-off"
    string Category,          // e.g. "核心完整性"
    string Title,             // e.g. "HVCI 未啟用"
    string Detail,            // 完整說明
    SecuritySeverity Severity,
    string? Recommendation,   // null if Good
    int Weight = 0,           // 這一項在該防線裡的最大扣分（用於算覆蓋率）
    bool Assessed = true      // false = 讀不到／不適用，不算分也不算缺口
);

/// <summary>單一防線的評分與其下的發現清單。</summary>
public sealed record SecurityCategoryScore(
    string Id,                // e.g. "dma"
    string Name,              // e.g. "DMA 與記憶體保護"
    int Score,                // 0-100 within category（只算已評估項目；0 = 全未評估）
    int Weight,               // percentage weight in total (all weights sum to 100)
    SecuritySeverity Severity,
    IReadOnlyList<SecurityFinding> Findings,
    int AssessedWeight = 0,   // 這一條防線「量到的分數點數」（項目權重之和；0 = 全未評估）
    int TotalWeight = 100     // 這一條防線「滿分點數」（已評估＋未評估的項目權重之和）
);

/// <summary>完整安全態勢評估結果。</summary>
public sealed record SecurityPosture(
    int TotalScore,           // 0-100 weighted composite（只算已評估項目）
    string Verdict,           // 一行繁中結論
    IReadOnlyList<SecurityCategoryScore> Categories,
    IReadOnlyList<HardeningRecommendation> Recommendations,
    DateTimeOffset AssessedAt,
    int CoveragePercent = 100, // 實際量到的權重佔比；未評估項目不計分
    bool HasScore = true       // false = 覆蓋率太低，不該顯示總分
);

/// <summary>一條可執行的強化建議。</summary>
public sealed record HardeningRecommendation(
    string Id,                // e.g. "enable-hvci"
    string Title,
    SecuritySeverity Priority,
    string Description,
    string Impact,
    string Difficulty          // "簡單" / "中等" / "進階"
);

/// <summary>一筆即時威脅事件（由 Bridge 推送或輪詢取得）。</summary>
public sealed record ThreatEvent(
    DateTimeOffset Timestamp,
    SecuritySeverity Severity,
    string DefenseLine,       // 所屬防線 id
    string Title,
    string Detail
);

/// <summary>六大防線的即時狀態摘要。</summary>
public sealed class DefenseLineStatus : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    private bool _active;
    public bool Active { get => _active; set => SetProperty(ref _active, value); }

    private int _score;
    public int Score { get => _score; set => SetProperty(ref _score, value); }

    private SecuritySeverity _severity;
    public SecuritySeverity Severity { get => _severity; set => SetProperty(ref _severity, value); }

    private string _summary = "尚未評估";
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }
}
