using System.Buffers.Binary;
using System.Text;

namespace XinSpect;

/// <summary>核心傾印檔頭（PAGEDU64）解出的欄位。</summary>
public sealed record KernelDumpHeader(
    string Kind,
    int MajorVersion,
    int BuildNumber,
    uint BugCheckCode,
    ulong Parameter1,
    ulong Parameter2,
    ulong Parameter3,
    ulong Parameter4);

/// <summary>
/// Windows 核心傾印（Minidump）檔頭解碼器（Vol 2 批次 B／FS 之後的 P0-04 RC-001）。純位元組解析。
/// <para>
/// <b>版面已以真檔驗證</b>（本機 C:\Windows\Minidump 五個真實傾印，2026-10-11）：
/// 位移 0 起為 ASCII <c>PAGEDU64</c>（x64 核心傾印）；<c>MajorVersion</c> u32 @8、<c>MinorVersion</c>
/// （組建號，如 26100）u32 @12、<b>BugCheck 代碼 u32 @0x38</b>、四個參數 u64 @0x40／0x48／0x50／0x58。
/// 代碼→名稱知識表來源＝Microsoft Learn「Bug Check Code Reference」（2026-10-11 抓取）。
/// </para>
/// <para>
/// <b>界線</b>：<b>解碼不是診斷</b>——只回答「這台機器藍過幾次、錯誤代碼是什麼」，不宣稱根因；
/// 未收錄的代碼如實標「未收錄」＋原碼十六進位，不猜名稱；x86 傾印（<c>PAGEDUMP</c>）版面不同且未對照，不解碼；
/// 參數可能是核心位址，匯出時視為敏感值預設遮蔽。
/// </para>
/// </summary>
public static class KernelDumpDecoder
{
    /// <summary>解碼所需的最小位元組數（到第四個參數尾端）。</summary>
    public const int HeaderBytes = 0x60;

    /// <summary>
    /// 解析傾印檔頭。x64 核心傾印（PAGEDU64）回欄位；其他簽名回 null（呼叫端如實標示不猜）。
    /// </summary>
    [SpecRef("Windows 核心傾印 PAGEDU64 檔頭：簽名 ASCII \"PAGEDU64\"（位移 0）、MajorVersion u32@8、"
        + "MinorVersion（組建號）u32@12、BugCheckCode u32@0x38、四個參數 u64@0x40/0x48/0x50/0x58——"
        + "位移以本機 C:\\Windows\\Minidump 五個真實傾印（2026-10-11）逐欄驗證（0x124 WHEA、0xA IRQL、0xCE 驅動卸載）")]
    public static KernelDumpHeader? Parse(byte[]? head)
    {
        if (head is null || head.Length < HeaderBytes) return null;
        if (Encoding.ASCII.GetString(head, 0, 8) != "PAGEDU64") return null;
        return new KernelDumpHeader(
            "PAGEDU64",
            BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(12, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x38, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x40, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x48, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x50, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x58, 8)));
    }

    /// <summary>
    /// bugcheck 代碼→符號名知識表（Microsoft Learn「Bug Check Code Reference」收錄子集；未收錄回 null 不猜）。
    /// </summary>
    [SpecRef("Microsoft Learn「Bug Check Code Reference」（windows-hardware/drivers/debugger/bug-check-code-reference2，"
        + "2026-10-11 抓取）：代碼與符號名逐條照錄；本表是子集，未收錄者呼叫端如實標「未收錄」＋原碼，不猜")]
    public static string? CodeName(uint code) => BugCheckTable.TryGetValue(code, out string? name) ? name : null;

    /// <summary>知識表收錄條數（供申報與測試比對）。</summary>
    [SpecRef("計數即上表條目數；條目逐條出自 Microsoft Learn Bug Check Code Reference（2026-10-11）")]
    public static int TableCount() => BugCheckTable.Count;

    private static readonly IReadOnlyDictionary<uint, string> BugCheckTable = new Dictionary<uint, string>
    {
        [0x0A] = "IRQL_NOT_LESS_OR_EQUAL",
        [0x1A] = "MEMORY_MANAGEMENT",
        [0x1E] = "KMODE_EXCEPTION_NOT_HANDLED",
        [0x24] = "NTFS_FILE_SYSTEM",
        [0x2E] = "DATA_BUS_ERROR",
        [0x34] = "CACHE_MANAGER",
        [0x3B] = "SYSTEM_SERVICE_EXCEPTION",
        [0x3F] = "NO_MORE_SYSTEM_PTES",
        [0x50] = "PAGE_FAULT_IN_NONPAGED_AREA",
        [0x51] = "REGISTRY_ERROR",
        [0x74] = "BAD_SYSTEM_CONFIG_INFO",
        [0x79] = "MISMATCHED_HAL",
        [0x7A] = "KERNEL_DATA_INPAGE_ERROR",
        [0x7B] = "INACCESSIBLE_BOOT_DEVICE",
        [0x7E] = "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
        [0x7F] = "UNEXPECTED_KERNEL_MODE_TRAP",
        [0x80] = "NMI_HARDWARE_FAILURE",
        [0x9C] = "MACHINE_CHECK_EXCEPTION",
        [0x9F] = "DRIVER_POWER_STATE_FAILURE",
        [0xA0] = "INTERNAL_POWER_ERROR",
        [0xA5] = "ACPI_BIOS_ERROR",
        [0xC1] = "SPECIAL_POOL_DETECTED_MEMORY_CORRUPTION",
        [0xC2] = "BAD_POOL_CALLER",
        [0xC4] = "DRIVER_VERIFIER_DETECTED_VIOLATION",
        [0xC5] = "DRIVER_CORRUPTED_EXPOOL",
        [0xCE] = "DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS",
        [0xD1] = "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
        [0xD5] = "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL",
        [0xE2] = "MANUALLY_INITIATED_CRASH",
        [0xEA] = "THREAD_STUCK_IN_DEVICE_DRIVER",
        [0xEF] = "CRITICAL_PROCESS_DIED",
        [0xF3] = "DISORDERLY_SHUTDOWN",
        [0xF4] = "CRITICAL_OBJECT_TERMINATION",
        [0xF5] = "FLTMGR_FILE_SYSTEM",
        [0xF7] = "DRIVER_OVERRAN_STACK_BUFFER",
        [0xFC] = "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY",
        [0x101] = "CLOCK_WATCHDOG_TIMEOUT",
        [0x109] = "CRITICAL_STRUCTURE_CORRUPTION",
        [0x10E] = "VIDEO_MEMORY_MANAGEMENT_INTERNAL",
        [0x116] = "VIDEO_TDR_FAILURE",
        [0x117] = "VIDEO_TDR_TIMEOUT_DETECTED",
        [0x119] = "VIDEO_SCHEDULER_INTERNAL_ERROR",
        [0x122] = "WHEA_INTERNAL_ERROR",
        [0x124] = "WHEA_UNCORRECTABLE_ERROR",
        [0x133] = "DPC_WATCHDOG_VIOLATION",
        [0x139] = "KERNEL_SECURITY_CHECK_FAILURE",
        [0x13A] = "KERNEL_MODE_HEAP_CORRUPTION",
        [0x13D] = "CRITICAL_INITIALIZATION_FAILURE",
        [0x140] = "STORAGE_DEVICE_ABNORMALITY_DETECTED",
        [0x145] = "SECURE_BOOT_VIOLATION",
        [0x147] = "ABNORMAL_RESET_DETECTED",
        [0x14C] = "FATAL_ABNORMAL_RESET_ERROR",
        [0x154] = "UNEXPECTED_STORE_EXCEPTION",
        [0x155] = "OS_DATA_TAMPERING",
        [0x178] = "ELAM_DRIVER_DETECTED_FATAL_ERROR",
        [0x1CA] = "SYNTHETIC_WATCHDOG_TIMEOUT",
        [0x1CF] = "HARDWARE_WATCHDOG_TIMEOUT",
        [0x1D5] = "DRIVER_PNP_WATCHDOG",
        [0x1DB] = "IPI_WATCHDOG_TIMEOUT",
        [0x1EA] = "SECURE_PCI_CONFIG_SPACE_ACCESS_VIOLATION",
        [0xC000021A] = "WINLOGON_FATAL_ERROR",
        [0xDEADDEAD] = "MANUALLY_INITIATED_CRASH1",
    };
}
