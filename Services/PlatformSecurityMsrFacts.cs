namespace XinSpect;

/// <summary>IA32_FEATURE_CONTROL（MSR 0x3A，Intel SDM Vol.4）解碼：bit0 Lock、bit1 VMX-in-SMX、bit2 VMX-outside-SMX。</summary>
public readonly record struct FeatureControlDecode(bool Lock, bool VmxInSmx, bool VmxOutsideSmx, ChipsetSecurityVerdict Verdict);

/// <summary>IA32_DEBUG_INTERFACE（MSR 0xC80）解碼：bit0 ENABLE、bit30 LOCK、bit31 DEBUG_OCCURRED。</summary>
public readonly record struct DebugInterfaceDecode(bool Enable, bool Lock, bool DebugOccurred, ChipsetSecurityVerdict Verdict);

/// <summary>
/// 平台安全 MSR 的純解碼器。位元定義取自 Intel SDM Vol.4（穩定、跨世代）；
/// 只解有定義的位元，其餘不臆測。實際讀取由服務層經接縫取得後餵進來。
/// </summary>
public static class PlatformSecurity
{
    /// <summary>FEATURE_CONTROL：Lock=0 時 BIOS 的 VMX/SMX 啟用設定可被任何 ring0 改寫；raw=0 時「未實作」與「未啟用」無法區分，由呼叫端註明。</summary>
    public static FeatureControlDecode DecodeFeatureControl(ulong raw)
    {
        bool lockBit = (raw & 0x1) != 0;
        return new(lockBit, (raw & 0x2) != 0, (raw & 0x4) != 0, lockBit ? ChipsetSecurityVerdict.Protected : ChipsetSecurityVerdict.Unprotected);
    }

    /// <summary>DEBUG_INTERFACE：ENABLE 且未 LOCK＝除錯埠對外開放；DEBUG_OCCURRED 代表自上次清除後曾有除錯事件發生（鑑識線索）。</summary>
    public static DebugInterfaceDecode DecodeDebugInterface(ulong raw)
    {
        bool enable = (raw & 0x1) != 0;
        bool lockBit = (raw & (1UL << 30)) != 0;
        bool occurred = (raw & (1UL << 31)) != 0;
        var verdict = enable && !lockBit ? ChipsetSecurityVerdict.Unprotected : ChipsetSecurityVerdict.Protected;
        return new(enable, lockBit, occurred, verdict);
    }
}

/// <summary>核心特權 MSR 讀取的可注入接縫；真實後端走 WinRing0，測試注入假件。</summary>
public interface IKernelMsrReader
{
    bool Available { get; }
    string? UnavailableReason { get; }
    /// <summary>服務本機的後端名（如「WinRing0」）；未標示者回 null。</summary>
    string? BackendName => null;
    /// <summary>讀 MSR；失敗或不支援回 null（上層標 ReadError，不以 0 頂替）。</summary>
    ulong? ReadMsr(uint index);

    /// <summary>最近一次 ReadMsr 失敗的細節（null＝尚無失敗或無細節）；預設無，驅動後端實作以帶出 NTSTATUS 級原因。</summary>
    string? LastFailReason => null;
}

/// <summary>以現有已簽章 WinRing0 讀 MSR；失敗回 null 帶原因。驅動就緒後可換自家驅動後端，只換這一層。</summary>
public sealed class WinRing0KernelMsrReader : IKernelMsrReader, IDisposable
{
    private readonly WinRing0Bridge _bridge = WinRing0Bridge.Create();
    public bool Available => _bridge.Available;
    public string? BackendName => "WinRing0";
    public string? UnavailableReason => Available ? null
        : string.IsNullOrEmpty(_bridge.Error) ? "缺 ring0：WinRing0 未提供 MSR 讀取" : _bridge.Error;

    public ulong? ReadMsr(uint index)
    {
        try { return _bridge.ReadMsrPair64(index); }
        catch { return null; } // 部分平台對未實作 MSR 會丟例外——如實回 null 由上層標三態
    }

    public void Dispose() => _bridge.Dispose();
}

/// <summary>
/// 平台安全 MSR 三態事實：IA32_FEATURE_CONTROL（0x3A，VT-x/SMX 啟用鎖）與 IA32_DEBUG_INTERFACE
/// （0xC80，矽除錯埠啟用／鎖定／曾發生除錯事件）。讀不到（缺 ring0、平台未實作）一律三態標原因；
/// raw=0 的「未實作 vs 未啟用」歧義在文字裡明說，不冒判決。
/// </summary>
public static class PlatformSecurityMsrService
{
    private const string Category = "韌體安全";

    public static IReadOnlyList<HardwareFact> Collect(IKernelMsrReader reader, DateTimeOffset at)
    {
        return
        [
            BuildFact(reader, at, "platform.feature_control", "VT-x 特性控制 (IA32_FEATURE_CONTROL)", 0x3A,
                raw => FeatureControlText(raw)),
            BuildFact(reader, at, "platform.debug_interface", "矽除錯埠 (IA32_DEBUG_INTERFACE)", 0xC80,
                raw => DebugInterfaceText(raw)),
        ];
    }

    private static HardwareFact BuildFact(IKernelMsrReader reader, DateTimeOffset at,
        string key, string name, uint msr, Func<ulong, string> decode)
    {
        string source = $"MSR 0x{msr:X}";
        if (!reader.Available)
            return Unavailable(key, name, source, at, FactAvailability.InsufficientPrivilege,
                reader.UnavailableReason ?? "缺 ring0：特權讀取未就緒");
        ulong? raw = reader.ReadMsr(msr);
        if (raw is null)
            return Unavailable(key, name, source, at, FactAvailability.ReadError,
                reader.LastFailReason ?? $"MSR 0x{msr:X} 讀取失敗（此平台可能未實作）");
        return new HardwareFact(key, Category, name, decode(raw.Value), "", source, FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);

    private static string FeatureControlText(ulong raw)
    {
        var d = PlatformSecurity.DecodeFeatureControl(raw);
        var text = d.Verdict == ChipsetSecurityVerdict.Protected
            ? "已鎖定（Lock=1）：VMX/SMX 啟用設定不可再改"
            : "未鎖定（Lock=0）：任何 ring0 可改寫 VMX/SMX 啟用設定";
        text += $"；VMX outside SMX={(d.VmxOutsideSmx ? 1 : 0)}、VMX in SMX={(d.VmxInSmx ? 1 : 0)}";
        if (raw == 0) text += "（全 0：未實作與未啟用無法區分，僅報位元值）";
        return text;
    }

    private static string DebugInterfaceText(ulong raw)
    {
        var d = PlatformSecurity.DecodeDebugInterface(raw);
        var text = d.Verdict == ChipsetSecurityVerdict.Unprotected
            ? "除錯埠啟用中且未鎖（ENABLE=1、LOCK=0）：外部除錯連線可行"
            : d.Lock ? "已鎖定（LOCK=1）：重置前除錯埠不可再啟用"
            : "除錯埠未啟用（ENABLE=0）";
        if (d.DebugOccurred) text += "；曾偵測到除錯事件發生（DEBUG_OCCURRED=1）——自上次清除後有除錯連線紀錄";
        return text;
    }
}
