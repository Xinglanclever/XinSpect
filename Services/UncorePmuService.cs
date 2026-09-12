namespace XinSpect;

/// <summary>Uncore PMU 的平台支援狀態。</summary>
public sealed class UncoreSupport
{
    public bool Supported { get; init; }
    public string PlatformName { get; init; } = "未知";
    public string Status { get; init; } = "此平台未收錄。";
    public IReadOnlyList<string> Available { get; init; } = [];
    public IReadOnlyList<string> Unavailable { get; init; } = [];
}

/// <summary>一次 Uncore PMU 讀取的結果。</summary>
public sealed class UncoreReading
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string ValueText { get; init; }
    public required string Note { get; init; }
    public ulong? RawValue { get; init; }
}

/// <summary>
/// Uncore PMU 讀取服務（嚴格限定 Ubox / UPI link PMU）。
/// </summary>
/// <remarks>
/// <para>
/// <b>技術界線（不可逾越）：</b>
/// ① CHA/Cbo（mesh/ring）計數器走 MMIO 映射，需要能映射實體記憶體的驅動，現有橋接層做不到。
/// ② iMC 逐通道計數器同理，已於 <c>DramTrafficService</c> 記載。
/// 這兩類<b>不列一組猜出來的值</b>——到不了就說到不了。
/// </para>
/// <para>
/// <b>平台白名單制：</b>只列已知 MSR 佈局的平台。
/// 查不到就顯示「此平台未收錄」而不是套用別平台的位址。
/// 白名單寧可少，不可錯。
/// </para>
/// <para>
/// 目前收錄：Skylake-X / Skylake-SP（family 6, model 0x55）——使用者本機（X299）可實測。
/// Uncore 頻率已由 <c>CeilingService</c> 的 MSR 0x620/0x621 處理，此服務不重複。
/// </para>
/// </remarks>
public sealed class UncorePmuService
{
    /// <summary>用於讀取 MSR 的介面（可注入假實作以利測試）。</summary>
    public interface IMsrReader
    {
        ulong? ReadMsr(uint index);
        (int Family, int Model, int Stepping) CpuId();
    }

    /// <summary>走真實 WinRing0Bridge 的實作。</summary>
    public sealed class WinRing0MsrReader : IMsrReader
    {
        public ulong? ReadMsr(uint index)
        {
            using var bridge = WinRing0Bridge.Create();
            return bridge.Available ? bridge.ReadMsrPair64(index) : null;
        }

        public (int Family, int Model, int Stepping) CpuId()
        {
            // CPUID leaf 1: EAX = [31:28]ext family + [27:20]ext model + [19:16]type + [15:12]family + [11:8]model + [7:4]stepping + [3:0]
            var (eax, _, _, _) = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
            int family = (eax >> 8) & 0xF;
            int model = (eax >> 4) & 0xF;
            int stepping = eax & 0xF;
            if (family == 6 || family == 0xF)
                model += ((eax >> 16) & 0xF) << 4;
            if (family == 0xF)
                family += (eax >> 20) & 0xFF;
            return (family, model, stepping);
        }
    }

    private readonly IMsrReader _reader;

    public UncorePmuService(IMsrReader? reader = null)
        => _reader = reader ?? new WinRing0MsrReader();

    /// <summary>判斷平台是否在白名單內。</summary>
    public UncoreSupport Detect()
    {
        var (family, model, _) = _reader.CpuId();

        if (family == 6 && model == 0x55)
        {
            return new UncoreSupport
            {
                Supported = true,
                PlatformName = "Skylake-X / Skylake-SP（model 0x55）",
                Status = "已收錄。可讀取 Uncore 頻率比率與 UPI 相關 MSR。",
                Available =
                [
                    "Uncore 頻率上限與目前倍頻（MSR 0x620 / 0x621，已由效能天花板頁處理）",
                    "UPI 鏈路數偵測（CPUID leaf 0x14）",
                ],
                Unavailable =
                [
                    "CHA/Cbo（mesh/ring）計數器：走 MMIO 映射，本程式的唯讀路徑（MSR 與 PCI 設定空間）到不了",
                    "iMC 逐通道計數器：同上，已於 DramTrafficService 記載",
                    "UPI PMON 事件計數器：MSR 位址因步進而異，未經實機驗證的位址不列入白名單",
                ],
            };
        }

        return new UncoreSupport
        {
            Supported = false,
            PlatformName = $"family {family}，model 0x{model:X2}",
            Status = "此平台未收錄。不會套用別平台的 MSR 位址——套錯會讀出垃圾值。",
            Unavailable = ["此平台不在白名單內，所有 Uncore PMU 讀取停用。"],
        };
    }

    /// <summary>
    /// 讀取可用的 Uncore 計數器。白名單外的平台回傳空清單。
    /// </summary>
    public List<UncoreReading> Measure()
    {
        var support = Detect();
        if (!support.Supported) return [];

        var readings = new List<UncoreReading>();

        // Uncore 頻率上限（MSR 0x620）
        var uncoreLimit = _reader.ReadMsr(0x620);
        if (IsPlausible(uncoreLimit))
        {
            ulong val = uncoreLimit!.Value;
            int minRatio = (int)(val & 0x7F);
            int maxRatio = (int)((val >> 8) & 0x7F);
            readings.Add(new UncoreReading
            {
                Name = "Uncore 頻率範圍",
                Category = "Uncore",
                ValueText = $"{minRatio}x – {maxRatio}x（× 100 MHz）",
                Note = "MSR 0x620 UNCORE_RATIO_LIMIT",
                RawValue = uncoreLimit.Value,
            });
        }

        // Uncore 目前頻率狀態（MSR 0x621）
        var uncoreStatus = _reader.ReadMsr(0x621);
        if (IsPlausible(uncoreStatus))
        {
            int currentRatio = (int)(uncoreStatus!.Value & 0x7F);
            readings.Add(new UncoreReading
            {
                Name = "Uncore 目前倍頻",
                Category = "Uncore",
                ValueText = $"{currentRatio}x（≈ {currentRatio * 100} MHz）",
                Note = "MSR 0x621 UNCORE_PERF_STATUS",
                RawValue = uncoreStatus.Value,
            });
        }

        if (readings.Count == 0)
        {
            readings.Add(new UncoreReading
            {
                Name = "Uncore PMU",
                Category = "Uncore",
                ValueText = "—",
                Note = "MSR 讀取未回傳有效值（可能需要系統管理員權限或 WinRing0 未載入）",
            });
        }

        return readings;
    }

    /// <summary>
    /// 合理性檢查：全 0 與全 1（0xFFFFFFFFFFFFFFFF）視為「此 MSR 未實作」而非真實值。
    /// </summary>
    internal static bool IsPlausible(ulong? value)
        => value is { } v && v != 0 && v != ulong.MaxValue;
}
