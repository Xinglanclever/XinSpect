using System.Collections.Generic;

namespace XinSpect;

/// <summary>
/// 英文翻譯表：繁中原文 → 英文。覆蓋導覽標題與高頻 UI 字串；
/// 未收錄的字串在英語模式下回退繁中原文（漸進式翻譯，不影響功能）。
/// 新增條目時同步更新 Tests/LanguageServiceTests.cs 的覆蓋檢查。
/// </summary>
public static class EnglishStrings
{
    private static readonly Dictionary<string, string> Map = new()
    {
        // ── 導覽標題 ──
        ["我的電腦"] = "My PC",
        ["總覽"] = "Overview",
        ["AI 評價"] = "AI Review",
        ["瓶頸診斷"] = "Bottleneck Diagnosis",
        ["處理器"] = "Processor",
        ["NPU 檢測"] = "NPU Detection",
        ["記憶體"] = "Memory",
        ["主機板"] = "Motherboard",
        ["顯示卡"] = "Graphics Card",
        ["儲存裝置"] = "Storage",
        ["網路"] = "Network",
        ["感測器"] = "Sensors",
        ["歷史回放"] = "History Replay",
        ["證據實驗室"] = "Evidence Lab",
        ["韌體安全"] = "Firmware Security",
        ["健康"] = "Health",
        ["效能"] = "Benchmark",
        ["深測中心"] = "Deep Bench",
        ["效能天花板"] = "Performance Ceiling",
        ["算力圖"] = "Compute Chart",
        ["繪圖管線測試"] = "Graphics Pipeline Test",
        ["防護"] = "Protection",
        ["超頻"] = "Overclocking",
        ["顯示卡超頻"] = "GPU Overclocking",
        ["系統風扇"] = "System Fans",
        ["工具箱"] = "Toolbox",
        ["實用工具"] = "Utilities",
        ["瀏覽器"] = "Browser",
        ["終端機"] = "Terminal",
        ["進階驅動分析"] = "Advanced Driver Analysis",
        ["作業系統分析"] = "OS Analysis",
        ["設定"] = "Settings",
        ["關於"] = "About",
        ["螢幕色域"] = "Screen Gamut",
        ["連接埠占用"] = "Port Usage",
        ["Hosts 編輯器"] = "Hosts Editor",
        ["藍屏分析"] = "BSOD Analysis",
        ["垃圾清理"] = "Cleanup",
        ["電池分析"] = "Battery",
        ["右鍵選單"] = "Context Menu",
        ["網速測試"] = "Speed Test",
        ["記憶體整理"] = "Memory Cleaner",
        ["開機啟動項"] = "Startup Items",
        ["驅動稽核"] = "Driver Audit",
        ["DNS 切換"] = "DNS Switch",
        ["大檔掃描"] = "Large File Scan",
        ["運算穩定性壓測"] = "Stress Test",
        ["幀時間監測"] = "Frametime Monitor",
        ["DPC 延遲"] = "DPC Latency",
        ["執行緒遷移"] = "Thread Migration",
        ["PCIe 鏈路"] = "PCIe Link",
        ["Windows 授權"] = "Windows License",
        ["網卡進階屬性"] = "NIC Advanced Properties",
        ["睡眠與喚醒"] = "Sleep & Wake",
        ["顯示鏈路"] = "Display Link",
        ["NVMe 電源狀態"] = "NVMe Power States",
        ["USB 鏈路"] = "USB Link",
        ["系統引導修復"] = "Boot Repair",
        ["硬體檢測"] = "Hardware Test",

        // ── 通用 UI ──
        ["溫度"] = "Temperature",
        ["頻率"] = "Frequency",
        ["電壓"] = "Voltage",
        ["風扇"] = "Fan",
        ["負載"] = "Load",
        ["容量"] = "Capacity",
        ["速率"] = "Speed",
        ["版本"] = "Version",
        ["授權"] = "License",
        ["狀態"] = "Status",
        ["健康度"] = "Health",
        ["重新擷取"] = "Refresh",
        ["匯出報告"] = "Export Report",
        ["迷你"] = "Mini",
        ["搜尋"] = "Search",
        ["設定模式"] = "Settings Mode",
        ["就緒"] = "Ready",
        ["每秒更新中"] = "Updating every second",

        // ── 日期 ──
        ["西元紀年"] = "Gregorian",
        ["公元纪年"] = "Gregorian",
        ["民國紀年"] = "Minguo (ROC)",
        ["中華黃帝紀元"] = "Yellow Emperor Era",
        ["宣統紀年〔大清〕"] = "Xuantong Era (Qing)",
        ["哆啦A夢紀元〔惡搞〕"] = "Doraemon Era (Fun)",
    };

    /// <summary>查英文翻譯；未收錄回 null（呼叫方回退繁中原文）。</summary>
    public static string? Lookup(string zh) => Map.TryGetValue(zh, out var en) ? en : null;

    /// <summary>目前收錄條目數。</summary>
    public static int Count => Map.Count;
}
