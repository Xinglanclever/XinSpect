namespace XinSpect;


/// <summary>讀 PCI 設定空間 DWORD 的可注入接縫；讓三態事實產生邏輯脫離硬體可單測。留根基：之後換自家驅動只換這一層實作。</summary>
public interface IPciConfigReader
{
    bool Available { get; }
    string? UnavailableReason { get; }
    uint? ReadDword(byte bus, byte device, byte function, uint register);
}

/// <summary>以現有已簽章 WinRing0 讀 PCI 設定空間；載不進或缺 PCI 能力時 Available=false 並帶原因。Phase 3 會換成自家驅動後端。</summary>
public sealed class WinRing0PciConfigReader : IPciConfigReader, IDisposable
{
    private readonly WinRing0Bridge _bridge = WinRing0Bridge.Create();
    public bool Available => _bridge.Available && _bridge.PciAvailable;
    public string? UnavailableReason => Available ? null
        : string.IsNullOrEmpty(_bridge.Error) ? "缺 ring0：WinRing0 未提供 PCI 設定空間讀取" : _bridge.Error;
    public uint? ReadDword(byte bus, byte device, byte function, uint register)
        => _bridge.ReadPciConfig(bus, device, function, register);
    public void Dispose() => _bridge.Dispose();
}

public static class ChipsetSecurityService
{
    private const string Category = "韌體安全";

    /// <summary>讀晶片組安全暫存器並產生三態事實：讀不到就標 Availability+原因，讀到才解碼下裁決。</summary>
    public static IReadOnlyList<HardwareFact> Collect(IPciConfigReader reader, DateTimeOffset at) =>
    [
        BuildFact(reader, at, "chipset.bios_cntl", "BIOS 寫入保護", 0, 31, 0, 0xDC,
            raw => BiosCntlText(ChipsetSecurity.DecodeBiosCntl(raw))),
        BuildFact(reader, at, "chipset.smramc", "SMRAM 鎖定", 0, 0, 0, 0x88,
            raw => SmramcText(ChipsetSecurity.DecodeSmramc(raw))),
        BuildHfsFact(reader, at),
    ];

    // HFS 自成一格：先驗 0:16.0 是否 Intel HECI（低 16 位 vendor=0x8086），避免把別的裝置的 0x40 當 HFSTS1 誤讀。
    private static HardwareFact BuildHfsFact(IPciConfigReader reader, DateTimeOffset at)
    {
        const string key = "chipset.me_hfs", name = "ME 狀態 (HFSTS1)", source = "PCI 0:16.0+0x40";
        if (!reader.Available)
            return Unavailable(key, name, source, at, FactAvailability.InsufficientPrivilege,
                reader.UnavailableReason ?? "缺 ring0：特權讀取未就緒");
        uint? vendorDevice = reader.ReadDword(0, 22, 0, 0x00);
        if (vendorDevice is null)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, "PCI 設定空間讀取失敗");
        if (vendorDevice.Value == 0xFFFFFFFF || (vendorDevice.Value & 0xFFFF) != 0x8086)
            return Unavailable(key, name, source, at, FactAvailability.NotApplicable, "0:16.0 非 Intel HECI（ME 不在此處或已停用）");
        uint? raw = reader.ReadDword(0, 22, 0, 0x40);
        if (raw is null)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, "HFSTS1 讀取失敗");
        return new HardwareFact(key, Category, name, HfsText(ChipsetSecurity.DecodeHfs(raw.Value)), "", source,
            FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact BuildFact(IPciConfigReader reader, DateTimeOffset at,
        string key, string name, byte bus, byte dev, byte fn, uint reg, Func<uint, string> decode)
    {
        string source = $"PCI {bus:X}:{dev:X2}.{fn}+0x{reg:X2}";
        if (!reader.Available)
            return Unavailable(key, name, source, at, FactAvailability.InsufficientPrivilege,
                reader.UnavailableReason ?? "缺 ring0：特權讀取未就緒");
        uint? raw = reader.ReadDword(bus, dev, fn, reg);
        if (raw is null)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, "PCI 設定空間讀取失敗");
        if (raw.Value == 0xFFFFFFFF)
            return Unavailable(key, name, source, at, FactAvailability.NotApplicable, "裝置無回應（可能非 Intel 平台或無此功能）");
        return new HardwareFact(key, Category, name, decode(raw.Value), "", source, FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);

    private static string BiosCntlText(BiosCntlDecode d) => d.Verdict switch
    {
        ChipsetSecurityVerdict.SmmProtected => "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS",
        ChipsetSecurityVerdict.Protected => "有鎖保護：BLE=1，開啟寫入會觸發 SMI",
        _ => $"未保護：BLE=0，任何 ring0 皆可寫 BIOS（BIOSWE={(d.BiosWe ? 1 : 0)}）",
    };

    private static string SmramcText(SmramcDecode d) => d.Verdict switch
    {
        ChipsetSecurityVerdict.Protected => d.DOpen
            ? "已鎖：D_LCK=1，但 D_OPEN=1——鎖定下對外開放，此組合硬體不應出現"
            : "已鎖：D_LCK=1，SMRAM 設定鎖定",
        _ => d.DOpen ? "SMRAM 對外開放：D_OPEN=1 且未鎖" : "未鎖：D_LCK=0",
    };

    private static string HfsText(HfsDecode d) => d.OperationMode switch
    {
        MeOperationMode.Normal => $"正常運作（working_state={d.WorkingState}，fw_init={(d.FwInitComplete ? "完成" : "未完成")}）",
        MeOperationMode.Debug => "除錯模式",
        MeOperationMode.SoftDisable => "軟性停用（HAP / soft-disable）",
        MeOperationMode.OverrideJumper => "安全覆寫（Mfg 跳線）",
        MeOperationMode.OverrideMei => "安全覆寫（MEI 訊息）",
        _ => $"其他 operation_mode=0x{d.OperationModeRaw:X}（未定義值，不臆測）",
    };
}
