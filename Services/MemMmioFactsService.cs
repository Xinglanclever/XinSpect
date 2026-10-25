using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 實體位址空間的「MMIO 洞」盤點（U10-MM-0052，鍵 <c>mem.mmio.total</c>）。
///
/// <para>
/// <b>題目是「實體位址空間裡有多少不是 RAM」，而 Windows 上這件事有一條零特權、唯讀的既有通路：</b>
/// 登錄 <c>HKLM\HARDWARE\RESOURCEMAP\System Resources\Physical Memory</c> 的 <c>.Translated</c> 值
/// （型別 REG_RESOURCE_LIST）——那是<b>開機時由韌體記憶體映射（BIOS 時代的 E820／UEFI 的
/// GetMemoryMap）建立、核心翻譯後留下的實體記憶體區段清單</b>。
/// Windows 上沒有直接讀 E820 的通路，這份映射就是它在這個作業系統裡的對應物。
/// </para>
///
/// <para>
/// <b>解析的一個實作細節（在真機上逐位元組核對過）：</b>這個值的型別是 REG_RESOURCE_LIST（8），
/// .NET 的 <see cref="Microsoft.Win32.RegistryKey.GetValue"/> 對這個型別<b>回 null</b>
/// （<c>FirmwareSettingsReachFactsService</c> 的探針因此只能驗「機碼存在」、讀不到內容）——
/// 所以這裡走 <c>RegQueryValueEx</c>。CM_PARTIAL_RESOURCE_DESCRIPTOR 在本值的編碼是
/// <b>20 位元組一步</b>（cmreslist.h 以 4 位元組對齊打包：Type(1)＋ShareDisposition(1)＋Flags(2)＋
/// Start(8)＋Length(4)），不是 x64 自然對齊的 24——本機實測 100 bytes＝20 表頭＋4×20 逐段合理
/// （0x1000–0xA0000、0x100000–0x4A66A000、…、4 GiB 以上），24 步長解出來是垃圾。
/// </para>
///
/// <para>
/// <b>定義（寫進事實本身，不藏在實作裡）：</b>「MMIO 洞」＝<b>合併重疊後的 RAM 區段之間、
/// 從 0 到最高 RAM 端點為止</b>的未佔用位址空間。洞裡有裝置的 MMIO 視窗、快閃視窗、ACPI 保留區，
/// <b>本服務不逐段歸因</b>；最高 RAM 端點<b>以上</b>的空間不計（沒有上界可言，計了就是編造）。
/// 因此這個合計<b>不是「所有 MMIO 裝置視窗的大小總和」</b>——那是逐裝置的問題（pci.res.*），
/// 兩者不得互相代言。
/// </para>
///
/// <para>
/// <b>與 sb.memmap.* 的交叉界線：</b>SMBIOS Type 19 是<b>韌體宣告</b>的映射範圍
/// （<see cref="SmbiosMemoryMapFactsService"/>），這裡是<b>OS 開機時拿到的記憶體映射</b>的剩餘面。
/// 兩個來源各說各的、粒度不同，<b>並列而不互判</b>——把它們的差額解釋成「誰錯了」是沒有出處的推論。
/// </para>
/// </summary>
public static class MemMmioFactsService
{
    private const string Category = "記憶體";
    private const string Source =
        "登錄 HKLM\\HARDWARE\\RESOURCEMAP\\System Resources\\Physical Memory（.Translated，REG_RESOURCE_LIST，唯讀）"
        + "＝開機記憶體映射（E820 的 Windows 對應物）";

    public const string TotalKey = "mem.mmio.total";

    /// <summary>
    /// 逐洞明細的<strong>家族前綴</strong>。名稱刻意不含 <c>Key</c>——掃描器把
    /// <c>const string ...Key... = "..."</c> 當成一把鍵，而前綴不是鍵（同
    /// <see cref="SmbiosMemoryMapFactsService"/> 的 RangePrefix 那個講究）。
    /// <b>成員由機器決定</b>（這台機器有幾個洞是韌體映射的事實），動態家族申報見
    /// <see cref="FactKeyDynamicCatalog"/>。
    /// </summary>
    private const string HolePrefix = "mem.mmio.hole.";

    /// <summary>一個 RAM 區段（OS 記憶體映射裡的「這段是記憶體」宣告）。</summary>
    public sealed record PhysRange(ulong Start, ulong Length);

    /// <summary><c>null</c>＝讀取或解析失敗；空清單＝解析成功但沒有任何記憶體描述元（不同的斷言）。</summary>
    public delegate IReadOnlyList<PhysRange>? MemoryMapProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, MemoryMapProbe? probe = null)
    {
        IReadOnlyList<PhysRange>? ranges;
        try { ranges = (probe ?? new MemoryMapProbe(ReadOsPhysicalMemory))(); }
        catch { ranges = null; }

        if (ranges is null)
            return [new HardwareFact(TotalKey, Category, "MMIO 位址空間總量", "", "GB", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "**實體記憶體資源映射讀取失敗**（登錄值不存在、型別不符或解析失敗）。"
                + "**「讀不到」與「沒有洞」是不同的斷言**——把前者說成後者，"
                + "會讓一次讀取失敗看起來像「這台機器沒有 MMIO」")];

        if (ranges.Count == 0)
            return [new HardwareFact(TotalKey, Category, "MMIO 位址空間總量", "", "GB", Source,
                FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                "映射解析成功但沒有任何記憶體描述元——**這是解析後的答案，不是讀取失敗**；不推測")];

        var holes = ComputeHoles(ranges);
        var facts = new List<HardwareFact>(holes.Count + 1) { TotalFact(at, holes) };
        for (int i = 0; i < holes.Count; i++) facts.Add(HoleFact(at, i, holes[i]));
        return facts;
    }

    /// <summary>一個洞：<paramref name="Start"/> 到 <paramref name="End"/>（<b>不含</b>端點，與區段同口徑）。</summary>
    public sealed record MmioHole(ulong Start, ulong End)
    {
        public ulong Size => End - Start;
    }

    /// <summary>
    /// 合併重疊的 RAM 區段後，取 0 到最高 RAM 端點之間的間隙。
    /// <b>端點以上的空間不計</b>——那沒有上界，計了就是編造（界線寫在總計事實的文字裡）。
    /// </summary>
    internal static IReadOnlyList<MmioHole> ComputeHoles(IReadOnlyList<PhysRange> ranges)
    {
        var merged = new List<(ulong Start, ulong End)>();
        foreach (var r in ranges)
        {
            if (r.Length == 0) continue;                       // 長度 0 不佔空間，不是洞的邊界
            ulong end = r.Start + r.Length;                    // 溢位讓它繞回——下一行的 Max 保底不會產生負洞
            if (end <= r.Start) end = ulong.MaxValue;
            merged.Add((r.Start, end));
        }
        merged.Sort((a, b) => a.Start.CompareTo(b.Start));

        var holes = new List<MmioHole>();
        ulong cursor = 0;
        foreach (var (start, end) in merged)
        {
            if (start > cursor) holes.Add(new MmioHole(cursor, start));
            if (end > cursor) cursor = end;
        }
        return holes;
    }

    private static HardwareFact TotalFact(DateTimeOffset at, IReadOnlyList<MmioHole> holes)
    {
        long bytes = holes.Sum(h => (long)Math.Min(h.Size, (ulong)long.MaxValue));
        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
        return new HardwareFact(TotalKey, Category, "MMIO 位址空間總量",
            $"{holes.Count} 個洞、合計 {gb:0.###} GB＝{bytes.ToString("#,0", CultureInfo.InvariantCulture)} bytes。"
            + "**定義**：OS 開機記憶體映射裡、最高 RAM 端點以下的未佔用位址空間——"
            + "洞裡有裝置 MMIO 視窗、快閃視窗與 ACPI 保留區，**本項不逐段歸因**；"
            + "最高 RAM 端點以上**不計**（無上界）。"
            + "它**不是**「所有 MMIO 裝置視窗的總和」（那是逐裝置的事，pci.res.*）；"
            + "與 sb.memmap.*（韌體 Type 19 的映射宣告）是兩個來源，**並列不互判**",
            "GB", Source, FactTrustLevel.Derived, false, at, Math.Round(gb, 3),
            FactAvailability.Present, null);
    }

    private static HardwareFact HoleFact(DateTimeOffset at, int index, MmioHole hole)
    {
        string key = HolePrefix + index.ToString(CultureInfo.InvariantCulture);
        double gb = hole.Size / 1024.0 / 1024.0 / 1024.0;
        string size = hole.Size >= (1UL << 30) ? $"{gb:0.###} GB"
            : hole.Size >= (1UL << 20) ? $"{hole.Size >> 20} MiB" : $"{hole.Size >> 10} KiB";
        return new HardwareFact(key, Category, $"MMIO 洞 {index}",
            $"0x{hole.Start:X}–0x{hole.End:X}（不含端點）＝{size}——內容不歸因："
            + "可能是裝置 MMIO、快閃視窗或保留區，本服務不從洞的位置反推用途",
            "GB", Source, FactTrustLevel.Derived, false, at, Math.Round(gb, 3),
            FactAvailability.Present, null);
    }

    // ── 真實讀取路徑（唯一一份；測試用合成 byte[]，不碰登錄）────────────────

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int access, out IntPtr hKeyOut);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(IntPtr hKey, string name, IntPtr reserved, out int type,
        byte[]? data, ref int cbData);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    private const int ErrorSuccess = 0;
    private const int KeyRead = 0x20019;
    private const int RegResourceListType = 8;
    private static readonly IntPtr HkeyLocalMachine = new(unchecked(-2147483646)); // HKEY_LOCAL_MACHINE

    /// <summary>
    /// 讀 <c>.Translated</c> 並解析成 RAM 區段。<c>null</c>＝讀取失敗（機碼不在、查詢失敗、型別不符、長度不夠）。
    /// <b>不走 <see cref="Microsoft.Win32.RegistryKey.GetValue"/>：它對 REG_RESOURCE_LIST 回 null（.NET 不支援這個型別）。</b>
    /// </summary>
    internal static IReadOnlyList<PhysRange>? ReadOsPhysicalMemory()
    {
        if (RegOpenKeyEx(HkeyLocalMachine, @"HARDWARE\RESOURCEMAP\System Resources\Physical Memory",
                0, KeyRead, out IntPtr hKey) != ErrorSuccess)
            return null;
        try
        {
            int size = 0, type = 0;
            if (RegQueryValueEx(hKey, ".Translated", IntPtr.Zero, out type, null, ref size) != ErrorSuccess
                || type != RegResourceListType || size <= 0)
                return null;
            var buf = new byte[size];
            if (RegQueryValueEx(hKey, ".Translated", IntPtr.Zero, out type, buf, ref size) != ErrorSuccess
                || type != RegResourceListType)
                return null;
            return ParseResourceList(buf);
        }
        finally { RegCloseKey(hKey); }
    }

    /// <summary>
    /// CM_RESOURCE_LIST → RAM 區段（純函式）。描述元步長 <b>20 位元組</b>（4 位元組對齊打包，
    /// 真機核對過）；只收 Type 3（CmResourceTypeMemory）與 Type 7（CmResourceTypeMemoryLarge），
    /// 旗標不解讀、其餘型別跳過。結構越界即停——**損壞的映射不猜補**。
    /// </summary>
    [SpecRef("WDK cmreslist.h：CM_RESOURCE_LIST { DWORD Count; CM_FULL_RESOURCE_DESCRIPTOR List[1]; }，"
        + "CM_FULL_RESOURCE_DESCRIPTOR { INTERFACE_TYPE(4) + BusNumber(4) + CM_PARTIAL_RESOURCE_LIST "
        + "{ Version(2) + Revision(2) + Count(4) + CM_PARTIAL_RESOURCE_DESCRIPTOR PartialDescriptors[1]; } }；"
        + "描述元 { UCHAR Type; UCHAR ShareDisposition; USHORT Flags; union { LARGE_INTEGER Start; ULONG Length; } }＝20 bytes（pshpack4 對齊）。"
        + "Type 3＝CmResourceTypeMemory、Type 7＝CmResourceTypeMemoryLarge")]
    internal static IReadOnlyList<PhysRange>? ParseResourceList(byte[] data)
    {
        if (data.Length < 4) return null;
        uint fullCount = BitConverter.ToUInt32(data, 0);
        if (fullCount == 0 || fullCount > 64) return null;

        var ranges = new List<PhysRange>();
        int off = 4;
        for (int f = 0; f < fullCount; f++)
        {
            // 每個 full descriptor 的表頭＝InterfaceType(4)＋BusNumber(4)＋Version(2)＋Revision(2)＋Count(4)＝16。
            if (off + 16 > data.Length) return ranges.Count > 0 ? ranges : null;
            uint descCount = BitConverter.ToUInt32(data, off + 12);
            int p = off + 16;
            for (int i = 0; i < descCount && p + 20 <= data.Length; i++, p += 20)
            {
                byte type = data[p];
                if (type is not (3 or 7)) continue;
                ulong start = BitConverter.ToUInt64(data, p + 4);
                uint length = BitConverter.ToUInt32(data, p + 12);
                ranges.Add(new PhysRange(start, length));
            }
            off = p;
        }
        return ranges;
    }
}
