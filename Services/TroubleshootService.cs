using System.Collections.Generic;
using System.Linq;

namespace XinSpect.Services;

/// <summary>
/// 故障排查場景定義。
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
/// 簡易故障排查導引引擎，提供預設場景與檢查步驟。
/// </summary>
public static class TroubleshootService
{
    public static IReadOnlyList<TroubleshootScenario> Scenarios { get; } = new[]
    {
        new TroubleshootScenario
        {
            Id              = "slow",
            Title           = "電腦很慢",
            Icon            = "🐢",
            PossibleCauses  = new[]
            {
                "CPU 溫度過高導致降頻",
                "記憶體占用率過高",
                "背景程式佔據過多資源",
                "磁碟健康狀態不佳",
            },
            CheckItems      = new[]
            {
                "檢查 CPU 溫度是否超過 85°C",
                "檢查記憶體使用率是否超過 90%",
                "檢查是否有異常高 CPU 佔用的程式",
            },
            Suggestions     = new[]
            {
                "清理暫存檔與系統垃圾",
                "關閉不必要的背景程式",
                "確認散熱系統正常運作",
            },
            RelatedPageKey  = "ceiling",
            RelatedPageLabel = "前往「效能天花板」查看詳細分析",
        },
        new TroubleshootScenario
        {
            Id              = "game-lag",
            Title           = "遊戲卡頓",
            Icon            = "🎮",
            PossibleCauses  = new[]
            {
                "GPU 溫度過高導致降頻",
                "顯示記憶體不足",
                "顯卡驅動版本過舊",
                "CPU 瓶頸限制遊戲效能",
            },
            CheckItems      = new[]
            {
                "檢查 GPU 溫度是否超過 85°C",
                "檢查顯示記憶體使用狀況",
                "確認顯卡驅動是否為最新版",
            },
            Suggestions     = new[]
            {
                "更新顯卡驅動至最新版本",
                "降低遊戲畫質設定",
                "確保電源計畫設為高效能",
            },
            RelatedPageKey  = "ceiling",
            RelatedPageLabel = "前往「效能天花板」查看 GPU 分析",
        },
        new TroubleshootScenario
        {
            Id              = "bsod",
            Title           = "藍屏當機",
            Icon            = "💻",
            PossibleCauses  = new[]
            {
                "驅動程式衝突或損壞",
                "記憶體硬體故障",
                "系統檔案損壞",
                "過熱導致硬體保護性關機",
            },
            CheckItems      = new[]
            {
                "檢查最近的藍屏記錄與停止碼",
                "檢查是否有近期安裝的新驅動",
                "檢查記憶體健康狀態",
            },
            Suggestions     = new[]
            {
                "使用藍屏分析頁查看詳細停止碼解讀",
                "更新或回滾問題驅動",
                "執行 sfc /scannow 檢查系統檔案",
            },
            RelatedPageKey  = "bsod",
            RelatedPageLabel = "前往「藍屏分析」查看詳細記錄",
        },
        new TroubleshootScenario
        {
            Id              = "fan-noise",
            Title           = "風扇太吵",
            Icon            = "🌬️",
            PossibleCauses  = new[]
            {
                "散熱器積塵導致散熱不良",
                "CPU 或 GPU 溫度過高觸發風扇全速",
                "風扇軟體設定不當",
                "風扇軸承老化",
            },
            CheckItems      = new[]
            {
                "檢查 CPU 與 GPU 目前溫度",
                "檢查風扇轉速是否異常偏高",
                "確認散熱器是否有積塵",
            },
            Suggestions     = new[]
            {
                "清理散熱器與風扇積塵",
                "檢查並調整風扇曲線設定",
                "考慮更換散熱膏",
            },
            RelatedPageKey  = "health",
            RelatedPageLabel = "前往「健康報告」查看溫度與風扇狀態",
        },
        new TroubleshootScenario
        {
            Id              = "slow-boot",
            Title           = "開機很慢",
            Icon            = "⏳",
            PossibleCauses  = new[]
            {
                "開機自啟動程式過多",
                "磁碟讀取速度緩慢",
                "系統服務啟動超時",
                "BIOS/UEFI 設定不當",
            },
            CheckItems      = new[]
            {
                "檢查開機耗時是否超過 30 秒",
                "檢查開機自啟動項目數量",
                "確認系統磁碟是否為 SSD",
            },
            Suggestions     = new[]
            {
                "停用不必要的開機自啟動程式",
                "考慮升級至 SSD 作為系統磁碟",
                "檢查 BIOS 是否開啟快速啟動",
            },
            RelatedPageKey  = "boot",
            RelatedPageLabel = "前往「開機耗時」查看詳細分析",
        },
        new TroubleshootScenario
        {
            Id              = "network",
            Title           = "網路不穩",
            Icon            = "🌐",
            PossibleCauses  = new[]
            {
                "網路介面卡驅動問題",
                "DNS 解析緩慢或失敗",
                "網路設定錯誤",
                "路由器或數據機問題",
            },
            CheckItems      = new[]
            {
                "檢查網路介面卡狀態",
                "檢查 IP 位址與 DNS 設定",
                "測試網路延遲與丟包率",
            },
            Suggestions     = new[]
            {
                "重設網路介面卡",
                "嘗試更換 DNS 伺服器（8.8.8.8 或 1.1.1.1）",
                "更新網路介面卡驅動",
            },
            RelatedPageKey  = "network",
            RelatedPageLabel = "前往「網路」查看詳細網路資訊",
        },
    };

    /// <summary>
    /// 依據 ID 取得場景；找不到傳回 null。
    /// </summary>
    public static TroubleshootScenario? GetById(string id)
        => Scenarios.FirstOrDefault(s => s.Id == id);
}
