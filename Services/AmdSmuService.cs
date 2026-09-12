using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>AMD SMU 遙測結果。</summary>
public sealed class AmdSmuTelemetry
{
    public bool Success { get; init; }
    public string Status { get; init; } = "";
    public string PlatformName { get; init; } = "";
    public double? PackagePowerW { get; init; }
    public double? PackageTempC { get; init; }
    public IReadOnlyList<AmdCoreTelemetry> Cores { get; init; } = [];
}

/// <summary>單一核心的遙測。</summary>
public sealed class AmdCoreTelemetry
{
    public int CoreIndex { get; init; }
    public double? FrequencyMHz { get; init; }
    public double? VoltageV { get; init; }
}

/// <summary>
/// AMD SMU 唯讀存取：透過 SMN（System Management Network）讀 Ryzen 的遙測資料。
/// </summary>
/// <remarks>
/// <para>
/// <b>第一版只做唯讀。</b>寫入（PBO／Curve Optimizer）風險高，之後再議。
/// </para>
/// <para>
/// SMN 是 AMD Zen 系列存取 SMU 的通道。作法是透過 PCI 設定空間的 index/data 埠：
/// 常見位置是 bus 0 / dev 0 / fn 0 的 offset 0xB8（index）與 0xBC（data），
/// 部分平台是 0x60 / 0x64。流程：寫 SMN 位址到 index 埠 → 讀 data 埠得到值。
/// </para>
/// <para>
/// <b>平台白名單制</b>：Zen 各家族的 SMU 信箱位址不同。只填已確認的位址，
/// 不確定的留空、讓服務回報「尚未收錄」。
/// </para>
/// <para>
/// <b>誠實界線</b>：本機是 Intel（X299），無法實測 AMD 路徑。只能驗證
/// 「非 AMD 平台正確降級」與純函式單元測試；實際讀值需要 AMD 機器。
/// </para>
/// </remarks>
public sealed class AmdSmuService
{
    /// <summary>PCI 設定空間讀寫介面（可注入假實作以利測試）。</summary>
    public interface IPciAccess
    {
        uint? ReadPci(byte bus, byte dev, byte fn, uint reg);
        bool WritePci(byte bus, byte dev, byte fn, uint reg, uint value);
        string CpuVendor();
    }

    /// <summary>走真實 WinRing0Bridge 的實作。</summary>
    public sealed class WinRing0PciAccess : IPciAccess
    {
        public uint? ReadPci(byte bus, byte dev, byte fn, uint reg)
        {
            using var b = WinRing0Bridge.Create();
            return b.PciAvailable ? b.ReadPciConfig(bus, dev, fn, reg) : null;
        }

        public bool WritePci(byte bus, byte dev, byte fn, uint reg, uint value)
        {
            using var b = WinRing0Bridge.Create();
            return b.PciWriteAvailable && b.WritePciConfig(bus, dev, fn, reg, value);
        }

        public string CpuVendor()
        {
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
            Span<byte> buf = stackalloc byte[12];
            System.Runtime.InteropServices.MemoryMarshal.Write(buf, ebx);
            System.Runtime.InteropServices.MemoryMarshal.Write(buf.Slice(4), edx);
            System.Runtime.InteropServices.MemoryMarshal.Write(buf.Slice(8), ecx);
            return System.Text.Encoding.ASCII.GetString(buf);
        }
    }

    private const string AmdVendor = "AuthenticAMD";

    // SMN index/data 埠組：不同平台可能用不同的
    private static readonly (uint Index, uint Data)[] SmnPorts =
    [
        (0xB8, 0xBC),
        (0x60, 0x64),
    ];

    private readonly IPciAccess _pci;
    private (uint Index, uint Data)? _smnPort;
    private bool _isAmd;
    private bool _probed;

    public AmdSmuService(IPciAccess? pci = null)
        => _pci = pci ?? new WinRing0PciAccess();

    /// <summary>是否為 AMD 平台。</summary>
    public bool IsAmdPlatform()
    {
        if (!_probed) Probe();
        return _isAmd;
    }

    /// <summary>讀取一個 SMN 位址。失敗回 null。</summary>
    public uint? SmnRead(uint address)
    {
        if (!_probed) Probe();
        if (_smnPort is not { } port) return null;

        if (!_pci.WritePci(0, 0, 0, port.Index, address)) return null;
        return _pci.ReadPci(0, 0, 0, port.Data);
    }

    /// <summary>讀取遙測資料。非 AMD 平台或 SMN 不可存取時回傳降級結果。</summary>
    public AmdSmuTelemetry ReadTelemetry()
    {
        if (!IsAmdPlatform())
            return new AmdSmuTelemetry { Status = "此功能需要 AMD 處理器。" };

        if (_smnPort is null)
            return new AmdSmuTelemetry { Status = "SMN 不可存取（兩組埠都失敗）。可能需要系統管理員權限或 WinRing0 未載入。" };

        // 嘗試讀取 THM_TCON_CUR_TMP (SMN 0x00059800) — 封裝溫度
        // 這是多數 Zen 家族共用的位址（來源：ryzen_smu/zenpower）
        var tempRaw = SmnRead(0x00059800);
        double? tempC = null;
        if (IsPlausible(tempRaw))
        {
            // bit 31:21 = 溫度（單位 0.125°C），bit 19 = range select (+49°C offset)
            uint tv = tempRaw!.Value;
            int rawTemp = (int)(tv >> 21);
            bool rangeAdj = (tv & (1u << 19)) != 0;
            double t = rawTemp * 0.125;
            if (rangeAdj) t -= 49.0;
            if (t > 0 && t < 130) tempC = t;
        }

        return new AmdSmuTelemetry
        {
            Success = tempC.HasValue,
            Status = tempC.HasValue
                ? $"已讀取封裝溫度。逐核頻率與電壓需要平台專屬的 PM table 位址，尚未收錄。"
                : "SMN 位址 0x00059800（封裝溫度）讀取失敗或值不合理。此家族可能需要不同的位址。",
            PlatformName = "AMD Zen 系列",
            PackageTempC = tempC,
            // 逐核頻率/電壓需要 PM table（位址因家族而異），
            // 第一版不捏造——等有 AMD 機器實測再填。
            Cores = [],
        };
    }

    /// <summary>合理性檢查：0 與 0xFFFFFFFF 視為無效。</summary>
    internal static bool IsPlausible(uint? value)
        => value is { } v && v != 0 && v != uint.MaxValue;

    private void Probe()
    {
        _probed = true;
        _isAmd = _pci.CpuVendor().Contains("AMD", StringComparison.OrdinalIgnoreCase);
        if (!_isAmd) return;

        // 探測哪一組 SMN 埠可用：讀 bus 0 / dev 0 / fn 0 的 vendor ID，
        // 然後試每組 index/data 看讀回的值是否合理
        foreach (var port in SmnPorts)
        {
            // 寫一個已知的 SMN 位址（0x00050000 = SMU 版本，大多數 Zen 平台都有）
            if (!_pci.WritePci(0, 0, 0, port.Index, 0x00050000)) continue;
            var val = _pci.ReadPci(0, 0, 0, port.Data);
            if (IsPlausible(val))
            {
                _smnPort = port;
                return;
            }
        }
    }
}
