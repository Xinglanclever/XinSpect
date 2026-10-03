namespace XinSpect;

/// <summary>TME_ACTIVATE（MSR 0x982）的解碼結果。</summary>
public sealed record TmeActivateState(bool Enabled, string Algorithm);

/// <summary>SGX EPC 區域（CPUID leaf 0x12 subleaf ≥1 的解碼結果）。</summary>
public sealed record SgxEpcRegion(bool Valid, ulong Base, ulong Size);

/// <summary>
/// 記憶體加密的純解碼器：TME（Total Memory Encryption）啟用狀態與 SGX EPC 區域佈局。
/// </summary>
public static class MemoryEncryptionDecoder
{
    /// <summary>解 TME_ACTIVATE：bits[3:0]＝TME 啟用、bits[7:4]＝加密演算法（0＝AES-XTS-128、1＝AES-XTS-256）。</summary>
    [SpecRef("Intel SDM Vol.3, Table 2-49（MSR 0x982 TME_ACTIVATE）：bits[3:0] TME_ENABLE、bits[7:4] TME_ALGORITHM（0 AES-XTS-128、1 AES-XTS-256）；CPUID leaf 7 ECX bit25＝TME 支援、bit12＝MKTME")]
    public static TmeActivateState DecodeTmeActivate(ulong raw)
    {
        bool enabled = (raw & 0xF) != 0;
        uint alg = (uint)((raw >> 4) & 0xF);
        string algText = alg switch
        {
            0 => "AES-XTS-128",
            1 => "AES-XTS-256",
            var other => $"演算法 {other}（未收錄）",
        };
        return new TmeActivateState(enabled, algText);
    }

    /// <summary>解 CPUID leaf 0x12 subleaf≥1 的 EPC 區域：EAX bit0＝有效、base＝EBX:EAX、size＝EDX:ECX（皆 u64 大位組合）。</summary>
    [SpecRef("Intel SDM Vol.3D, Table 41-3（CPUID leaf 0x12, SGX EPC Enumeration）：EAX bit0＝標記有效、base＝EBX:EAX、size＝EDX:ECX")]
    public static SgxEpcRegion DecodeEpcRegion(uint eax, uint ebx, uint ecx, uint edx)
    {
        bool valid = (eax & 1) != 0;
        ulong baseAddr = ((ulong)ebx << 32) | eax & 0xFFFF_FFFFu;
        ulong size = ((ulong)edx << 32) | ecx & 0xFFFF_FFFFu;
        return new SgxEpcRegion(valid, baseAddr & 0xFFFF_FFFF_FFFF_F000ul, size);
    }
}
