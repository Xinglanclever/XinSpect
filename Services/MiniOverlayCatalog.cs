namespace XinSpect;

/// <summary>懸浮小窗的一列讀值：鍵、標籤、對應的設定開關、所屬群組。</summary>
/// <param name="Key">穩定識別碼（測試與選單順序都用它）。</param>
/// <param name="Label">顯示名稱（繁中；英文模式另有對照）。</param>
/// <param name="SettingsProperty"><see cref="SettingsService"/> 上的布林屬性名稱——選單與測試都靠它對帳。</param>
/// <param name="Group">分組（處理器／顯示卡／記憶體／其他），只用來排選單。</param>
public sealed record MiniOverlayItem(string Key, string Label, string SettingsProperty, string Group);

/// <summary>
/// 懸浮小窗的顯示項目目錄與風格定義。<b>純資料＋純函式</b>，不碰 UI，所以可以單獨測試。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要有目錄：</b>項目開關原本是六個手寫的勾選項，加一列就要改三處（設定欄位、XAML、
/// 齒輪選單），漏一處的症狀是「選單打開了、畫面沒出現」或相反。現在順序、標籤與設定屬性只有
/// 這一份清單，XAML 的每一列與選單的每一項都指回同一個鍵；測試再驗每個
/// <see cref="MiniOverlayItem.SettingsProperty"/> 都真的存在於 <see cref="SettingsService"/>。
/// </para>
/// <para>
/// <b>預設全部開啟</b>（使用者 2026-10-27 的要求）：想看少一點是自己關，不是預設藏起來。
/// 讀不到的值一律顯示「—」，不補 0——那是這個專案一貫的界線。
/// </para>
/// </remarks>
public static class MiniOverlayCatalog
{
    /// <summary>曦覽風格：沿用主題色與一般字型。</summary>
    public const int StyleXinSpect = 0;

    /// <summary>微星 Afterburner／RTSS 風格：黑底、等寬數字、標籤與數值分色的密集 OSD。</summary>
    public const int StyleAfterburner = 1;

    /// <summary>所有可顯示的項目（順序即畫面與選單的順序）。</summary>
    public static IReadOnlyList<MiniOverlayItem> Items { get; } =
    [
        new("cpu", "CPU", nameof(SettingsService.MiniShowCpu), "處理器"),
        new("cpuPower", "CPU 功耗", nameof(SettingsService.MiniShowCpuPower), "處理器"),
        new("cpuVolt", "CPU 電壓", nameof(SettingsService.MiniShowCpuVolt), "處理器"),
        new("clock", "頻率", nameof(SettingsService.MiniShowClock), "處理器"),
        new("vrm", "VRM 溫度", nameof(SettingsService.MiniShowVrm), "處理器"),
        new("gpu", "GPU", nameof(SettingsService.MiniShowGpu), "顯示卡"),
        new("gpuClock", "GPU 時脈", nameof(SettingsService.MiniShowGpuClock), "顯示卡"),
        new("gpuVram", "顯示記憶體", nameof(SettingsService.MiniShowGpuVram), "顯示卡"),
        new("gpuFan", "GPU 風扇", nameof(SettingsService.MiniShowGpuFan), "顯示卡"),
        new("gpuPower", "GPU 功耗", nameof(SettingsService.MiniShowGpuPower), "顯示卡"),
        new("mem", "記憶體", nameof(SettingsService.MiniShowMem), "記憶體"),
        new("memUsage", "記憶體用量", nameof(SettingsService.MiniShowMemUsage), "記憶體"),
        new("fps", "FPS", nameof(SettingsService.MiniShowFps), "遊戲"),
        new("low1", "1% Low", nameof(SettingsService.MiniShowLow1), "遊戲"),
        new("low01", "0.1% Low", nameof(SettingsService.MiniShowLow01), "遊戲"),
    ];

    /// <summary>把任意整數收斂成已知的兩種風格（設定檔被手改成 7 也不會出現半套樣式）。</summary>
    public static int NormalizeStyle(int style) => style == StyleAfterburner ? StyleAfterburner : StyleXinSpect;

    /// <summary>風格名稱（給設定頁與選單顯示）。</summary>
    public static string StyleName(int style) => NormalizeStyle(style) switch
    {
        StyleAfterburner => "微星 Afterburner（OSD）",
        _ => "曦覽（XinSpect）",
    };

    /// <summary>風格的說明：兩者差在哪裡、相同在哪裡。</summary>
    public static string StyleDescription(int style) => NormalizeStyle(style) switch
    {
        StyleAfterburner =>
            "黑底、等寬數字、標籤與數值分色的密集排版（RTSS／Afterburner 那種 OSD）。"
            + "只換外觀，讀值來源與資料項開關完全不變。",
        _ =>
            "沿用曦覽主題色與一般字型，與主視窗一致的圓角卡片。"
            + "只換外觀，讀值來源與資料項開關完全不變。",
    };
}
