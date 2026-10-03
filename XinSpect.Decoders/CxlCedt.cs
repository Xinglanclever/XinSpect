namespace XinSpect;

/// <summary>一個 CXL 固定記憶體窗口（CFMWS）的解碼結果。</summary>
public sealed record CedtCfmws(uint HwSuppVer, ulong BaseHpa, ulong WindowSize, uint InterleaveWays);

/// <summary>
/// ACPI CEDT（CXL Early Discovery Table）的純解碼器：只解 CFMWS（Type 0）的關鍵欄位。
/// 佈局依 CXL 規格手算：表頭 36 bytes；記錄頭＝Type u8@0＋Reserved u8@1＋RecordLength u16@2＋
/// Reserved 3@4＋HwSuppVer u8@7；CFMWS 內 BaseHPA u64@16（記錄相對）＋WindowSize u64@24＋
/// InterleaveWays u32@32。記錄長度不足或越界如實停（截斷不猜補）。
/// </summary>
public static class CedtDecoder
{
    /// <summary>解出表中全部 CFMWS 記錄。非 CFMWS 記錄跳過；長度異常即停。</summary>
    [SpecRef("CXL Specification r3.0, Table 9-22（CXL Fixed Memory Window Structure）：Type 0、Record Length u16@2、Hw Supp Ver u8@7、Base HPA u64@16、Window Size u64@24、Interleave Ways u32@32；表頭 36 bytes（ACPI 通用表頭）")]
    public static IReadOnlyList<CedtCfmws> DecodeCfmws(byte[] cedt)
    {
        var windows = new List<CedtCfmws>();
        int off = 36; // ACPI 表頭
        while (off + 4 <= cedt.Length)
        {
            byte type = cedt[off];
            int recordLength = cedt[off + 2] | (cedt[off + 3] << 8);
            if (recordLength < 4 || off + recordLength > cedt.Length) break; // 截斷——不猜補
            if (type == 0 && recordLength >= 36)
            {
                uint hwSuppVer = cedt[off + 7];
                ulong baseHpa = BitConverter.ToUInt64(cedt, off + 16);
                ulong windowSize = BitConverter.ToUInt64(cedt, off + 24);
                uint ways = BitConverter.ToUInt32(cedt, off + 32);
                windows.Add(new CedtCfmws(hwSuppVer, baseHpa, windowSize, ways));
            }
            off += recordLength;
        }
        return windows;
    }
}
