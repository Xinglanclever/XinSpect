namespace XinSpect;

/// <summary>
/// SMBIOS 結構 → 型別化事實。
/// </summary>
/// <remarks>
/// 位移全部照 SMBIOS 規格寫死並在註解與 <see cref="VerifyFact.Method"/> 裡標明，
/// 這樣測試（與日後看程式的人）核對得動。既有的 <c>SmbiosService</c> 產出的是給人看的字串列；
/// 這裡產出的是給規則做算術的數值，兩者刻意分開——把格式化過的字串再解析回數字是這類程式
/// 最常見的爛法。
/// <para>
/// 每個欄位在取用前都先確認結構長度夠：SMBIOS 2.3 的 Type 17 只到 +0x1A，
/// 擴充容量（+0x1C）與實際運行速度（+0x20）是後來的版本才加的。長度不夠就當那個事實不存在，
/// 不要讀出界，也不要拿 0 充數。
/// </para>
/// </remarks>
public static class SmbiosFacts
{
    // Type 17（Memory Device）欄位位移
    private const int DimmSize = 0x0C;          // word：bit15 0＝MB、1＝KB；0x7FFF＝改看擴充容量
    private const int DimmSpeed = 0x15;         // word：標稱速度 MT/s
    private const int DimmManufacturer = 0x17;  // 字串索引
    private const int DimmSerial = 0x18;        // 字串索引
    private const int DimmPart = 0x1A;          // 字串索引
    private const int DimmExtendedSize = 0x1C;  // dword：MB（SMBIOS 2.7+）
    private const int DimmConfigured = 0x20;    // word：實際運行速度 MT/s（SMBIOS 2.7+）

    // Type 16（Physical Memory Array）欄位位移
    private const int ArrayMaxCapacity = 0x07;  // dword：KB
    private const int ArrayDeviceCount = 0x0D;  // word：插槽數

    public static List<VerifyFact> From(IEnumerable<SmbiosStruct> structs, DateTime now)
    {
        var list = new List<VerifyFact>();
        var all = structs as IList<SmbiosStruct> ?? structs.ToList();

        // 只算真的插了模組的（容量為 0 的 Type 17 是空插槽）
        var dimms = all.Where(s => s.Type == 17 && s.Length > DimmPart && SizeMiB(s) > 0).ToList();
        if (dimms.Count > 0)
        {
            list.Add(Number(FactId.DimmCount, dimms.Count, "", "SMBIOS Type 17 逐筆計數（容量非 0）", now));
            list.Add(Strings(FactId.DimmManufacturers, dimms, DimmManufacturer, now));
            list.Add(Strings(FactId.DimmSerials, dimms, DimmSerial, now));
            list.Add(Strings(FactId.DimmPartNumbers, dimms, DimmPart, now));
            list.Add(Number(FactId.DimmSizeTotalMiB, dimms.Sum(SizeMiB), "MiB",
                $"SMBIOS Type 17 +0x{DimmSize:X2}／+0x{DimmExtendedSize:X2} 加總", now));

            // 一台機器可能插著速度不同的模組；取最高的當「標稱」，因為那才是被降下來的那一邊。
            ushort rated = dimms.Max(d => d.WordAt(DimmSpeed));
            if (rated > 0)
                list.Add(Number(FactId.DimmSpeedMts, rated, "MT/s", $"SMBIOS Type 17 +0x{DimmSpeed:X2}", now));

            var withConfigured = dimms.Where(d => d.Length > DimmConfigured + 1).ToList();
            if (withConfigured.Count > 0)
            {
                ushort cur = withConfigured.Max(d => d.WordAt(DimmConfigured));
                if (cur > 0)
                    list.Add(Number(FactId.DimmConfiguredMts, cur, "MT/s",
                        $"SMBIOS Type 17 +0x{DimmConfigured:X2}", now));
            }
        }

        var array = all.FirstOrDefault(s => s.Type == 16 && s.Length > ArrayDeviceCount + 1);
        if (array is not null)
        {
            list.Add(Number(FactId.ArrayMaxCapacityMiB, array.DwordAt(ArrayMaxCapacity) / 1024.0, "MiB",
                $"SMBIOS Type 16 +0x{ArrayMaxCapacity:X2}（單位 KB）", now));
            list.Add(Number(FactId.ArraySlotCount, array.WordAt(ArrayDeviceCount), "",
                $"SMBIOS Type 16 +0x{ArrayDeviceCount:X2}", now));
        }
        return list;
    }

    /// <summary>
    /// Type 17 +0x0C 的容量：bit15 為 0 時單位是 MB、為 1 時是 KB；
    /// 值為 0x7FFF 代表容量放在 +0x1C 的擴充欄位（單位 MB）；0 或 0xFFFF 代表空插槽／未知。
    /// </summary>
    private static double SizeMiB(SmbiosStruct d)
    {
        if (d.Length <= DimmSize + 1) return 0;
        ushort raw = d.WordAt(DimmSize);
        if (raw == 0 || raw == 0xFFFF) return 0;
        if (raw == 0x7FFF) return d.Length > DimmExtendedSize + 3 ? d.DwordAt(DimmExtendedSize) : 0;
        return (raw & 0x8000) != 0 ? (raw & 0x7FFF) / 1024.0 : raw;
    }

    /// <summary>逐條模組的字串以豎線相連；讀不到的那一條寫「—」，位置仍保留，條數才對得起模組數。</summary>
    private static VerifyFact Strings(FactId id, List<SmbiosStruct> dimms, int offset, DateTime now)
    {
        var parts = dimms.Select(d =>
        {
            string s = (d.GetString(d.ByteAt(offset)) ?? "").Trim();
            return s.Length > 0 ? s : "—";
        });
        return new(id, FactCatalog.Name(id), string.Join("|", parts), null, "",
            FactSource.Smbios, $"SMBIOS Type 17 +0x{offset:X2}（字串索引）", false,
            FactTrust.FirmwareReported, now);
    }

    private static VerifyFact Number(FactId id, double n, string unit, string method, DateTime now)
        => new(id, FactCatalog.Name(id),
            n.ToString("0.##") + (unit.Length > 0 ? " " + unit : ""), n, unit,
            FactSource.Smbios, method, false, FactTrust.FirmwareReported, now);
}
