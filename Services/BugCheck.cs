namespace XinSpect;

/// <summary>常見藍屏停止代碼（BugCheck）對照表：代碼→名稱與白話原因提示。</summary>
internal static class BugCheck
{
    // (名稱, 常見原因提示)
    private static readonly Dictionary<uint, (string, string)> Table = new()
    {
        [0x0000000A] = ("IRQL_NOT_LESS_OR_EQUAL", "驅動程式以錯誤的 IRQL 存取記憶體，多為驅動程式或記憶體問題。"),
        [0x0000001A] = ("MEMORY_MANAGEMENT", "記憶體管理錯誤，常見於記憶體模組不穩或超頻過度。"),
        [0x0000001E] = ("KMODE_EXCEPTION_NOT_HANDLED", "核心模式例外未處理，通常為驅動程式錯誤。"),
        [0x00000019] = ("BAD_POOL_HEADER", "核心集區記憶體損毀，多由驅動程式或記憶體造成。"),
        [0x00000024] = ("NTFS_FILE_SYSTEM", "NTFS 檔案系統錯誤，建議以 chkdsk 檢查磁碟。"),
        [0x0000003B] = ("SYSTEM_SERVICE_EXCEPTION", "系統服務例外，常見於驅動程式或系統檔損毀。"),
        [0x00000050] = ("PAGE_FAULT_IN_NONPAGED_AREA", "存取無效記憶體，多為記憶體故障或驅動程式問題。"),
        [0x0000007E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "系統執行緒例外未處理，通常指向特定驅動程式。"),
        [0x0000007F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "核心模式陷阱，常見於硬體故障或超頻不穩。"),
        [0x0000009F] = ("DRIVER_POWER_STATE_FAILURE", "驅動程式電源狀態轉換失敗，多見於睡眠／喚醒。"),
        [0x000000C2] = ("BAD_POOL_CALLER", "驅動程式錯誤地配置／釋放集區記憶體。"),
        [0x000000C5] = ("DRIVER_CORRUPTED_EXPOOL", "驅動程式損毀了系統集區，常為記憶體或驅動程式問題。"),
        [0x000000D1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "驅動程式以錯誤 IRQL 存取分頁記憶體，肇事者通常是某個驅動程式。"),
        [0x000000EF] = ("CRITICAL_PROCESS_DIED", "關鍵系統行程終止，多為系統檔損毀，可執行 sfc /scannow。"),
        [0x000000F4] = ("CRITICAL_OBJECT_TERMINATION", "關鍵系統物件終止，常與磁碟或系統檔有關。"),
        [0x00000116] = ("VIDEO_TDR_ERROR", "顯示驅動程式逾時未回應（TDR），多為顯示卡驅動或過熱／超頻。"),
        [0x00000117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "顯示卡逾時未回應，建議更新或回退顯示驅動。"),
        [0x00000119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "顯示排程器內部錯誤，多為顯示驅動問題。"),
        [0x00000124] = ("WHEA_UNCORRECTABLE_ERROR", "硬體層級無法修正的錯誤，常見於 CPU／記憶體故障或超頻不穩。"),
        [0x00000133] = ("DPC_WATCHDOG_VIOLATION", "DPC 監視逾時，常見於過舊的驅動程式（如 SSD／晶片組）。"),
        [0x0000013A] = ("KERNEL_MODE_HEAP_CORRUPTION", "核心堆積損毀，多為驅動程式問題。"),
        [0x00000139] = ("KERNEL_SECURITY_CHECK_FAILURE", "核心偵測到資料結構損毀，常見於驅動程式或記憶體。"),
        [0x0000014F] = ("PDC_WATCHDOG_TIMEOUT", "連線待命監視逾時，多與電源／驅動程式有關。"),
        [0x000000BE] = ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "驅動程式嘗試寫入唯讀記憶體。"),
        [0x0000004A] = ("IRQL_GT_ZERO_AT_SYSTEM_SERVICE", "返回使用者模式時 IRQL 過高，多為驅動程式錯誤。"),
    };

    public static (string, string) Lookup(uint code) =>
        Table.TryGetValue(code, out var v) ? v : ("（未收錄的停止代碼）", "可上網以此停止代碼查詢，或以 WinDbg 進一步分析傾印檔。");
}
