namespace XinSpect;


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
    [SpecRef("Intel SDM Vol.4, IA32_FEATURE_CONTROL (MSR 0x3A)：Lock bit0、VMX-in-SMX bit1、VMX-outside-SMX bit2")]
    public static FeatureControlDecode DecodeFeatureControl(ulong raw)
    {
        bool lockBit = (raw & 0x1) != 0;
        return new(lockBit, (raw & 0x2) != 0, (raw & 0x4) != 0, lockBit ? ChipsetSecurityVerdict.Protected : ChipsetSecurityVerdict.Unprotected);
    }

    /// <summary>DEBUG_INTERFACE：ENABLE 且未 LOCK＝除錯埠對外開放；DEBUG_OCCURRED 代表自上次清除後曾有除錯事件發生（鑑識線索）。</summary>
    [SpecRef("Intel SDM Vol.4, IA32_DEBUG_INTERFACE (MSR 0xC80)：ENABLE bit0、LOCK bit30、DEBUG_OCCURRED bit31")]
    public static DebugInterfaceDecode DecodeDebugInterface(ulong raw)
    {
        bool enable = (raw & 0x1) != 0;
        bool lockBit = (raw & (1UL << 30)) != 0;
        bool occurred = (raw & (1UL << 31)) != 0;
        var verdict = enable && !lockBit ? ChipsetSecurityVerdict.Unprotected : ChipsetSecurityVerdict.Protected;
        return new(enable, lockBit, occurred, verdict);
    }
}
