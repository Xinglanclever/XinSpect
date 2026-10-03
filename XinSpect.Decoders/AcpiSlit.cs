namespace XinSpect;

/// <summary>
/// ACPI SLIT（System Locality Distance Information Table）的純解碼器：N×N 節點距離矩陣。
/// 表頭 36 bytes；Number of System Localities（u64 LE）@36；其後 N² 個位元組為距離矩陣
/// （row-major，對角線 10、異地距離 ＞10 且 ≥ 本地距離——由韌體宣告）。宣告節點數與
/// 實際資料不符（截斷）時如實回空清單，不猜補。
/// </summary>
public static class SlitDecoder
{
    [SpecRef("ACPI Spec r6.5, Table 5-43（SLIT）：Header 36 bytes＋Number of System Localities u64@36＋距離矩陣 N² bytes（row-major）")]
    public static IReadOnlyList<IReadOnlyList<int>> DecodeMatrix(byte[] slit)
    {
        if (slit.Length < 44) return [];
        ulong nodes = BitConverter.ToUInt64(slit, 36);
        if (nodes is 0 or > 1024) return [];
        if (slit.Length < 44 + (int)(nodes * nodes)) return []; // 宣告與資料不符——截斷不猜

        var matrix = new List<IReadOnlyList<int>>((int)nodes);
        for (uint row = 0; row < nodes; row++)
        {
            var r = new List<int>((int)nodes);
            for (uint col = 0; col < nodes; col++)
                r.Add(slit[44 + (int)(row * nodes + col)]);
            matrix.Add(r);
        }
        return matrix;
    }
}
