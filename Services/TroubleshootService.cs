using System.Collections.Generic;
using System.Linq;

namespace XinSpect.Services;

/// <summary>
/// 描述一個疑難排解情境：使用者的症狀、可能原因、檢查項目、建議動作，
/// 以及可連結至深入分析的相關頁面。
/// </summary>
public sealed class TroubleshootScenario
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required string[] PossibleCauses { get; init; }
    public required string[] CheckItems { get; init; }
    public required string[] Suggestions { get; init; }
    public required string RelatedPageKey { get; init; }
    public required string RelatedPageLabel { get; init; }
}

/// <summary>
/// 疑難排解精靈引擎：提供預定義的常見問題情境與對應的診斷建議。
/// </summary>
public static class TroubleshootService
{
    private static readonly TroubleshootScenario[] _scenarios =
    [
        new TroubleshootScenario
        {
            Id              = "slow",
            Title           = "電腦很慢",
            Icon            = "\U0001F422",  // 🐢
            PossibleCauses  = ["CPU 使用率長期偏高", "記憶體不足或大量分頁", "背景程式佔用過多資源", "散熱不良導致降頻"],
            CheckItems      = ["CPU 溫度與使用率", "記憶體佔用百分比", "磁碟佇列長度", "是否有異常高負載的程序"],
            Suggestions     = ["關閉不必要的背景程式", "確認散熱器與風扇正常運作", "檢查是否需要擴充記憶體", "前往效能天花板頁面查看瓶頸分析"],
            RelatedPageKey  = "ceiling",
            RelatedPageLabel = "效能天花板",
        },
        new TroubleshootScenario
        {
            Id              = "game-lag",
            Title           = "遊戲卡頓",
            Icon            = "\U0001F3AE",  // 🎮
            PossibleCauses  = ["GPU 溫度過高而降頻", "顯示記憶體不足", "顯示卡驅動程式過舊或異常", "遊戲畫質設定超出硬體能力"],
            CheckItems      = ["GPU 溫度與時脈", "VRAM 使用量", "顯示卡驅動程式版本", "遊戲內 FPS 與畫格時間"],
            Suggestions     = ["更新顯示卡驅動程式至最新穩定版", "降低遊戲畫質或解析度", "清理顯示卡散熱器的灰塵", "前往效能天花板頁面確認 GPU 瓶頸"],
            RelatedPageKey  = "ceiling",
            RelatedPageLabel = "效能天花板",
        },
        new TroubleshootScenario
        {
            Id              = "bsod",
            Title           = "藍屏當機",
            Icon            = "\U0001F4A5",  // 💥
            PossibleCauses  = ["驅動程式不相容或損壞", "記憶體硬體故障", "磁碟檔案系統錯誤", "系統核心元件異常"],
            CheckItems      = ["最近安裝或更新的驅動程式", "藍屏錯誤代碼（Bug Check Code）", "記憶體診斷結果", "事件檢視器中的關鍵錯誤"],
            Suggestions     = ["記錄藍屏錯誤代碼並查詢對應原因", "執行 Windows 記憶體診斷工具", "回復最近更新的驅動程式", "前往藍屏分析頁面檢視 Minidump 詳情"],
            RelatedPageKey  = "bsod",
            RelatedPageLabel = "藍屏分析",
        },
        new TroubleshootScenario
        {
            Id              = "fan-noise",
            Title           = "風扇太吵",
            Icon            = "\U0001F32C️",  // 🌬️
            PossibleCauses  = ["散熱膏老化導致導熱效率下降", "機殼內部積塵嚴重", "風扇轉速曲線設定過於激進", "高負載工作持續運行"],
            CheckItems      = ["CPU 與 GPU 溫度", "風扇轉速（RPM）", "機殼內部清潔狀況", "目前系統負載"],
            Suggestions     = ["清潔機殼內部與風扇葉片的灰塵", "考慮重新塗抹散熱膏", "調整 BIOS 或軟體中的風扇曲線", "前往健康狀態頁面監控溫度與轉速"],
            RelatedPageKey  = "health",
            RelatedPageLabel = "健康狀態",
        },
        new TroubleshootScenario
        {
            Id              = "slow-boot",
            Title           = "開機很慢",
            Icon            = "⏳",  // ⏳
            PossibleCauses  = ["啟動項目過多", "系統碟速度不足或空間不夠", "BIOS / UEFI 設定不當", "驅動程式初始化緩慢"],
            CheckItems      = ["開機時間量測數據", "啟動項目清單與影響程度", "系統碟類型（SSD 或 HDD）與可用空間", "BIOS 開機模式（UEFI / CSM）"],
            Suggestions     = ["停用不必要的啟動項目", "確認系統碟為 SSD 且有足夠可用空間", "啟用 UEFI 快速開機", "前往開機時間頁面查看詳細開機階段分析"],
            RelatedPageKey  = "boot",
            RelatedPageLabel = "開機時間",
        },
        new TroubleshootScenario
        {
            Id              = "network",
            Title           = "網路不穩",
            Icon            = "\U0001F4E1",  // 📡
            PossibleCauses  = ["網路介面卡驅動程式異常", "DNS 解析緩慢或失敗", "Wi-Fi 訊號干擾或距離過遠", "網路線材接觸不良"],
            CheckItems      = ["網路介面卡狀態與連線速度", "延遲（Ping）與封包遺失率", "DNS 解析時間", "Wi-Fi 訊號強度"],
            Suggestions     = ["重新啟動網路介面卡或路由器", "更換 DNS 伺服器（如 1.1.1.1 或 8.8.8.8）", "改用有線連線以排除 Wi-Fi 問題", "前往網路頁面查看介面卡與連線詳情"],
            RelatedPageKey  = "network",
            RelatedPageLabel = "網路",
        },
    ];

    /// <summary>所有預定義的疑難排解情境。</summary>
    public static IReadOnlyList<TroubleshootScenario> Scenarios { get; } = _scenarios;

    /// <summary>依識別碼取得情境，找不到則傳回 <c>null</c>。</summary>
    public static TroubleshootScenario? GetById(string id)
        => _scenarios.FirstOrDefault(s => s.Id == id);
}
