namespace XinSpect;

/// <summary>
/// 記憶體攻擊面的誠實聲明（V7 WP22／A36 的第一層）：<b>Rowhammer 未施測</b>——
/// 施測可能損壞記憶體內容（V7 §14 風險表明列），需專用機器與明確同意，本工具不做；
/// TRR 啟用狀態在 usermode 無法誠實取得，不猜。ECC 保護現況已在驗機頁 R-MEM-05
/// （SMBIOS 陣列宣告 vs 模組位寬交叉）。這筆事實存在的理由：讓「為什麼工具不測」
/// 有明文答案，而不是沉默。
/// </summary>
public static class MemoryAttackSurfaceService
{
    private const string Category = "記憶體";

    public static HardwareFact Collect(DateTimeOffset at) =>
        new("mem.rowhammer", Category, "Rowhammer／記憶體攻擊面",
            "未施測——Rowhammer 測試可能損壞記憶體內容（需專用機器與明確同意）；TRR 狀態 usermode 無法誠實取得，不猜。" +
            "ECC 保護現況見驗機頁 R-MEM-05；冷開機殘留僅標風險不掃描。",
            "", "本專案誠實界線聲明（V7 §14／§12.5）", FactTrustLevel.Derived, false, at);
}
