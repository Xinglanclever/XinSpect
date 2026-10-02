namespace XinSpect;

/// <summary>
/// XsRegProbe 驅動契約的 managed 鏡像（與 XsRegProbe/Driver/XrpContract.h 同源）。
/// 唯一真源是那份 .h；本鏡像供 usermode 組 IOCTL 與做允許清單對帳，
/// <see cref="XsRegProbeContractTests.驅動標頭與managed契約鏡像一致"/> 逐項解析 .h 鎖住兩邊不漂移。
/// 加一條允許範圍＝改 .h 與本檔各一處（測試會強制兩邊一致），派遣碼不動。
/// </summary>
public static class XsRegProbeContract
{
    public const string DevicePath = "\\\\.\\XsRegProbe";

    public const uint Magic = 0x31505258;          // 'XRP1'
    public const uint IoctlVersion = 1;
    public const uint FeatureMsrRead = 0x00000001;
    public const uint FeatureMmioRead = 0x00000002;

    // CTL_CODE(0x8338, 0x800/0x801/0x802, METHOD_BUFFERED, FILE_READ_DATA)
    public const uint IoctlQueryInfo = 0x83386000;
    public const uint IoctlReadMsrList = 0x83386004;
    public const uint IoctlReadMmio = 0x83386008;

    public const int MsrMaxBatch = 64;
    public const int MmioMax = 4096;

    /// <summary>逐列 MSR 允許清單（與 .h 的 g_MsrAllow 逐筆一致）。</summary>
    public static readonly IReadOnlyList<uint> MsrAllow =
    [
        0x10, 0x34, 0x48, 0x8B, 0xC1, 0xCE,
        0x186, 0x198, 0x19C, 0x1A2, 0x1AA, 0x1AD, 0x1AE, 0x1B1, 0x1FC,
        0x38F,
        0x606, 0x610, 0x611, 0x613, 0x614, 0x619, 0x620, 0x621, 0x638, 0x639, 0x64F, 0x690,
        0x770, 0x771,
        0xC8D, 0xC8E, 0xC8F,
        0x3A, 0xC80,
        0xE7, 0xE8,
    ];

    /// <summary>MSR 範圍允許（含端點）：MCA 銀行。</summary>
    public static readonly (uint Lo, uint Hi) MsrRangeAllow = (0x400, 0x4FF);

    /// <summary>MMIO 範圍允許（含端點）：SPIBAR 與 ECAM。</summary>
    public static readonly (ulong Lo, ulong Hi)[] MmioAllow =
    [
        (0xFED10000UL, 0xFED10087UL),
        (0xE0000000UL, 0xE7FFFFFFUL),
    ];

    public static bool MsrAllowed(uint msr) =>
        MsrAllow.Contains(msr) ||
        (msr >= MsrRangeAllow.Lo && msr <= MsrRangeAllow.Hi);

    public static bool MmioRangeAllowed(ulong physicalAddress, int length)
    {
        if (length <= 0 || length > MmioMax) return false;
        ulong last = physicalAddress + (ulong)length - 1;
        if (last < physicalAddress) return false;
        return MmioAllow.Any(r => physicalAddress >= r.Lo && last <= r.Hi);
    }
}
