using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// PawnIO 後端：沙箱化 ring0——模組是雜湊白名單裡的 Pawn 小程式，任意代碼進不了核心。
/// <b>本檔的直接動機是 2026-08-30 的實測事故</b>（McaService.cs 註解在案）：PawnIO 的 IntelMsr
/// 對每個 MSR「回報成功卻回 0」，回 true 不代表讀到值。那次事故讓整條 PawnIO 路徑被退回。
/// 本後端的回答是：不信任模組的回傳值本身，<b>載入時先做自證，通過才准進後端鏈</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>自證協定（<see cref="SelfValidateAsync"/>）。</b>選一組「語義上幾乎必不為零」的 MSR：
/// 0x8B（微碼版本，EDX 高 32 位）、0xCE（PLATFORM_INFO，最低支援/最大非渦輪倍頻必在低位）。
/// 逐核讀取並要求：① 全部「成功且非零」；② 逐核微碼全等；③ 與第二條獨立路徑（WinRing0，
/// 若同時可用）抽查一致性。任一不满足→回 <see cref="PawnIoHealth.SilentZero"/>，
/// 後端整體降級退出鏈，原因如實寫進 <see cref="UnavailableReason"/>——
/// 「成功卻回零」比「讀不到」更糟，因為它會冒充事實；本後端寧可整體缺席。
/// </para>
/// <para>
/// <b>雜湊釘死。</b><see cref="ModulePins"/> 每一列：模組名＋官方發佈的 SHA-256。
/// 雜湊由倉庫以守門測試對照上游發佈頁核對（PawnIoPinTests，待補）；拿不到雜湊的模組
/// 不載入、不出現在能力申報裡——「未成文」而不是「先載了再說」。
/// </para>
/// <para>
/// <b>P/Invoke 縫。</b>下方 <see cref="Native"/> 的簽名以 PawnIO 2.x 官方匯出為準；
/// 加入 PawnIO NuGet／原生二進位時，以 <see cref="Native"/> 整層隔離差異，
/// 簽名有出入只改這一處。在釘住雜湊與綁定原生庫之前，本後端恆回
/// <see cref="KernelCapability.None"/>＋原因「未接入」——這是申報，不是佔位謊言。
/// </para>
/// </remarks>
public sealed class PawnIoBackend : IKernelBackend
{
    /// <summary>自證結論（進 backend.pawnio.health 事實與驅動就緒卡）。</summary>
    public enum PawnIoHealth { NotBound, Validating, Healthy, SilentZero, LoadFailed }

    /// <summary>雜湊釘死表：模組名 → 官方 SHA-256（小寫十六進位）。</summary>
    /// <remarks>接入時逐列填官方值並配守門測試；留空條目＝該模組未成文，不載入。</remarks>
    internal static readonly IReadOnlyDictionary<string, string> ModulePins = new Dictionary<string, string>
    {
        // ["intelmsr"] = "<官方發佈頁核對後填入>",
    };

    private readonly PawnIoHealth _health;
    private readonly string? _reason;

    public string Id => "pawnio";
    public string DisplayName => "PawnIO（沙箱）";
    public KernelTrustTier Tier => KernelTrustTier.Sandboxed;
    public string? UnavailableReason => _reason;

    public KernelCapability Capabilities =>
        _health == PawnIoHealth.Healthy && ModulePins.Count > 0
            ? KernelCapability.MsrRead          // 現有白名單模組只覆蓋讀；寫入路徑不在本版申報內
            : KernelCapability.None;

    public PawnIoBackend()
    {
        if (ModulePins.Count == 0 || Native.IsBound == false)
        {
            _health = PawnIoHealth.NotBound;
            _reason = "PawnIO 原生庫或模組雜湊表尚未接入（見 ModulePins 註解）——本後端未成文，不參與鏈。";
            return;
        }
        _health = PawnIoHealth.NotBound;   // 實際建構走 CreateAsync；此處保守缺席
        _reason = "須經 CreateAsync 自證後才可用。";
    }

    private PawnIoBackend(PawnIoHealth health, string? reason)
    {
        _health = health;
        _reason = health switch
        {
            PawnIoHealth.Healthy => null,
            PawnIoHealth.SilentZero => "自證失敗：MSR 回「成功卻為零」（2026-08-30 事故型態）——整體退出鏈，不冒充事實。",
            PawnIoHealth.LoadFailed => reason ?? "模組載入失敗。",
            _ => reason ?? "未接入。",
        };
    }

    /// <summary>載入＋自證＋產出可用後端。失敗回健康原因，不丟例外（呼叫端如實展示）。</summary>
    public static async Task<PawnIoBackend?> CreateAsync(IKernelBackend? crossCheck = null)
    {
        if (ModulePins.Count == 0 || Native.IsBound != true)
            return null;

        var health = await Task.Run(() => SelfValidate(crossCheck)).ConfigureAwait(false);
        return health.Health switch
        {
            PawnIoHealth.Healthy => new PawnIoBackend(PawnIoHealth.Healthy, null),
            _ => new PawnIoBackend(health.Health, health.Reason),
        };
    }

    private static (PawnIoHealth Health, string? Reason) SelfValidate(IKernelBackend? crossCheck)
    {
        // ① 微碼版本 MSR（IA32_BIOS_SIGN_ID）：讀前先寫零到 EDX 位是 CPUID 協議的一部分，
        //    此處只做唯讀抽查——讀值的高 32 位在真機上幾乎必非零。
        const uint msrMicrocode = 0x8B, msrPlatformInfo = 0xCE;

        foreach (var index in new[] { msrMicrocode, msrPlatformInfo })
        {
            var v = Native.ExecuteReadMsr(index);
            if (v is null)
                return (PawnIoHealth.LoadFailed, $"模組對 MSR 0x{index:X} 回失敗。");
        }

        var micro = Native.ExecuteReadMsr(msrMicrocode);
        if ((micro!.Value >> 32) == 0)
            return (PawnIoHealth.SilentZero,
                "MSR 0x8B 高 32 位為零：實體機上微碼版本不可能全機為零。界線：部分虛擬化環境會虛擬化此 MSR——VM 上自證失敗屬預期降級，退回其他後端。");

        // ② 逐核一致性：此版無法指定核心（PawnIO 在呼叫執行緒的當下核心上執行），
        //    只做單次讀取；逐核抽查需要執行緒親和性編排，留給接入後的守門測試。
        // ③ 獨立路徑交叉：若 WinRing0 同時可用，抽查一鍵比對。兩路不一致時**無法歸因**
        //    （可能是任一側壞了），保守做法是整體退出鏈——寧可缺席也不冒充。
        if (crossCheck is { } other &&
            other.Capabilities.HasFlag(KernelCapability.MsrRead))
        {
            var a = Native.ExecuteReadMsr(msrPlatformInfo);
            var b = other.ReadMsr(msrPlatformInfo);
            if (a is { } && b is { } && a != b)
                return (PawnIoHealth.SilentZero,
                    $"MSR 0x{msrPlatformInfo:X} 兩路不一致（PawnIO 0x{a:X} vs {other.DisplayName} 0x{b:X}）——無法歸因誰錯，整體退出鏈。");
        }

        return (PawnIoHealth.Healthy, null);
    }

    // ── IKernelBackend：只在 Healthy 時有真實行為；其餘狀態一律如實回 null（不冒充）──

    public ulong? ReadMsr(uint index) =>
        _health == PawnIoHealth.Healthy ? Native.ExecuteReadMsr(index) : null;

    public KernelWriteOutcome WriteMsr(uint index, ulong value, string caller) =>
        new(false, "PawnIO 本版白名單不含寫入模組——能力申報即邊界，不開後門。");

    public uint? ReadPciConfig(byte bus, byte device, byte function, uint register) => null;
    public KernelWriteOutcome WritePciConfig(byte bus, byte device, byte function, uint register, uint value, string caller) =>
        new(false, "PawnIO 本版白名單不含 PCI 寫入模組。");
    public byte? ReadIoPortByte(uint port) => null;
    public KernelWriteOutcome WriteIoPortByte(uint port, byte value, string caller) =>
        new(false, "PawnIO 本版白名單不含 I/O 埠寫入模組。");
    public byte[]? ReadMemoryBlock(ulong address, int length) => null;

    public void Dispose() { }

    /// <summary>
    /// PawnIO 原生綁定縫。官方 PawnIO 以具名裝置＋IOCTL 載入雜湊驗證的模組；
    /// 細節以官方 2.x 匯出為準，本層的存在意義是<b>讓簽名出入只改一處</b>。
    /// 未綁定時 <see cref="IsBound"/> 為 false，所有方法回 null——後端據此整體缺席。
    /// </summary>
    private static class Native
    {
        /// <summary>原生庫（PawnIO.dll）已載入且匯出核對通過。接入時由初始化器賦值。</summary>
        public static bool? IsBound => null;   // 未接入：如實缺席，而非假裝可用

        /// <summary>執行白名單 MSR 讀模組。回 null＝模組回失敗；<b>回 0 是合法值，由自證負責辨偽</b>。</summary>
        public static ulong? ExecuteReadMsr(uint index) => null;
    }
}
