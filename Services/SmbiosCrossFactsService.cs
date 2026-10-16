using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// SMBIOS 陳述與作業系統視圖的對照（ULTIMATE-1000 階段二 P2-033）。
///
/// <para>
/// <b>這一項是整套設計的收束點。</b>前面每一項都在做「多來源並列」——
/// Type 4 的核心數與 CPUID 並列、Type 43 的 TPM 與 TBS 並列、
/// Type 1 的機型與 WMI 並列。這一項把它們<b>逐欄位攤開</b>，
/// 讓「哪裡不一致」變成可查詢的事實。
/// </para>
///
/// <para>
/// <b>界線，而這是本項最重要的一句話：</b>
/// <b>不一致不是錯誤。</b>SMBIOS 是主機板廠在出廠時填的，WMI 是 OS 從各個驅動
/// 與韌體介面蒐集來的，兩者本來就可能不同——不同版本、不同時機、不同來源。
/// 本服務<b>只陳述兩邊各說了什麼</b>，不判斷哪一邊對，也不給「一致率」這種
/// 會被讀成評分的數字。
/// </para>
///
/// <para>
/// <b>但「無法比較」與「不一致」必須分開。</b>一邊沒有值時是「無從對照」，
/// 不是「不一致」——把前者算進後者會製造假的落差。
/// </para>
/// </summary>
public static class SmbiosCrossFactsService
{
    private const string Category = "SMBIOS×OS";
    private const string Source = "SMBIOS 表列 × WMI Win32_BIOS／Win32_ComputerSystemProduct／Win32_BaseBoard／Win32_Processor";

    public const string BiosKey = "sb.cross.bios";
    public const string SystemKey = "sb.cross.system";
    public const string BoardKey = "sb.cross.board";
    public const string CpuCountKey = "sb.cross.cpu.count";
    public const string ComparableKey = "sb.cross.comparable";

    /// <summary>WMI 那一邊的值——由呼叫端注入，這樣測試不必真的碰 WMI。</summary>
    public sealed record OsView(
        string? BiosVendor, string? BiosVersion,
        string? SystemVendor, string? SystemProduct,
        string? BoardVendor, string? BoardProduct,
        int? ProcessorCount);

    /// <summary>
    /// 從 WMI 取作業系統視圖。<b>每一項都各自 try/catch</b>——
    /// 一個查詢失敗不該讓其他三項也變成「沒有值」，
    /// 因為那會把「查不到」誤報成「不一致」。
    /// </summary>
    public static OsView ReadOsView()
    {
        string? biosVendor = null, biosVersion = null;
        string? sysVendor = null, sysProduct = null;
        string? boardVendor = null, boardProduct = null;
        int? cpuCount = null;

        try
        {
            using var s = new System.Management.ManagementObjectSearcher("root\\CIMV2",
                "SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (System.Management.ManagementObject o in s.Get())
                using (o)
                {
                    biosVendor = Str(o, "Manufacturer");
                    biosVersion = Str(o, "SMBIOSBIOSVersion");
                    break;
                }
        }
        catch (Exception ex) { Diag.Swallow("對照用 BIOS 查詢", ex, "BIOS 那一邊沒有值"); }

        try
        {
            using var s = new System.Management.ManagementObjectSearcher("root\\CIMV2",
                "SELECT Vendor, Name FROM Win32_ComputerSystemProduct");
            foreach (System.Management.ManagementObject o in s.Get())
                using (o)
                {
                    sysVendor = Str(o, "Vendor");
                    sysProduct = Str(o, "Name");
                    break;
                }
        }
        catch (Exception ex) { Diag.Swallow("對照用系統查詢", ex, "系統那一邊沒有值"); }

        try
        {
            using var s = new System.Management.ManagementObjectSearcher("root\\CIMV2",
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (System.Management.ManagementObject o in s.Get())
                using (o)
                {
                    boardVendor = Str(o, "Manufacturer");
                    boardProduct = Str(o, "Product");
                    break;
                }
        }
        catch (Exception ex) { Diag.Swallow("對照用主機板查詢", ex, "主機板那一邊沒有值"); }

        try
        {
            cpuCount = Environment.ProcessorCount;
        }
        catch (Exception ex) { Diag.Swallow("對照用處理器數", ex, "處理器數讀不到"); }

        return new OsView(biosVendor, biosVersion, sysVendor, sysProduct,
            boardVendor, boardProduct, cpuCount);
    }

    private static string? Str(System.Management.ManagementObject o, string prop)
    {
        try
        {
            string? v = o[prop]?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, OsView os,
        Func<IReadOnlyList<SmbiosStruct>>? structsProbe = null)
    {
        IReadOnlyList<SmbiosStruct> structs;
        try { structs = (structsProbe ?? (() => new SmbiosService().Structs))(); }
        catch (Exception ex)
        {
            return Na(at, "SMBIOS 表讀取失敗：" + ex.Message, FactAvailability.ReadError);
        }

        if (structs.Count == 0)
            return Na(at, "SMBIOS 表不可用或無結構（GetSystemFirmwareTable('RSMB') 未回表）",
                FactAvailability.NotSupported);

        var byType = structs.GroupBy(s => s.Type).ToDictionary(g => g.Key, g => g.ToList());
        var t0 = First(byType, 0);
        var t1 = First(byType, 1);
        var t2 = First(byType, 2);

        var facts = new List<HardwareFact>
        {
            Compare(at, BiosKey, "BIOS 廠商與版本",
                t0 is null ? null : Str(t0, 0x04),
                t0 is null ? null : Str(t0, 0x05),
                os.BiosVendor, os.BiosVersion),

            Compare(at, SystemKey, "系統廠商與產品名",
                t1 is null ? null : Str(t1, 0x04),
                t1 is null ? null : Str(t1, 0x05),
                os.SystemVendor, os.SystemProduct),

            Compare(at, BoardKey, "主機板廠商與產品名",
                t2 is null ? null : Str(t2, 0x04),
                t2 is null ? null : Str(t2, 0x05),
                os.BoardVendor, os.BoardProduct),

            CpuCount(at, byType, os.ProcessorCount),
        };

        facts.Add(Summary(at, facts));
        return facts;
    }

    /// <summary>
    /// 一組兩欄位的對照。<b>三種結果要分清楚：</b>
    /// 一致／不一致／無從對照——第三種不能算進第二種。
    /// </summary>
    private static HardwareFact Compare(DateTimeOffset at, string key, string name,
        string? smbiosA, string? smbiosB, string? osA, string? osB)
    {
        bool hasSmbios = !string.IsNullOrWhiteSpace(smbiosA) || !string.IsNullOrWhiteSpace(smbiosB);
        bool hasOs = !string.IsNullOrWhiteSpace(osA) || !string.IsNullOrWhiteSpace(osB);

        if (!hasSmbios && !hasOs)
            return new HardwareFact(key, Category, name, "兩邊都沒有值", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "SMBIOS 與 WMI 都沒有提供這一組——無從對照");

        if (!hasSmbios || !hasOs)
        {
            string have = hasSmbios ? "SMBIOS" : "WMI";
            string value = hasSmbios
                ? $"SMBIOS：{Join(smbiosA, smbiosB)}（WMI 無值）"
                : $"WMI：{Join(osA, osB)}（SMBIOS 無值）";
            return new HardwareFact(key, Category, name, value, "", Source,
                FactTrustLevel.Measured, false, at, null, FactAvailability.Unknown,
                $"只有 {have} 那一邊有值——**這是「無從對照」而不是「不一致」**；"
                + "把它算成不一致會製造假的落差");
        }

        string sText = Join(smbiosA, smbiosB);
        string oText = Join(osA, osB);
        bool same = string.Equals(sText, oText, StringComparison.OrdinalIgnoreCase);

        return new HardwareFact(key, Category, name,
            same ? $"兩邊一致：{sText}" : $"SMBIOS：{sText}；WMI：{oText}",
            "", Source, FactTrustLevel.Measured, false, at, same ? 1 : 0,
            same ? FactAvailability.Present : FactAvailability.Unknown,
            same
                ? null
                : "兩邊不同——**那不是「哪一個錯了」**：SMBIOS 是主機板廠出廠時填的，"
                  + "WMI 是 OS 從驅動與韌體介面蒐集來的，版本與時機本來就可能不同");
    }

    private static HardwareFact CpuCount(DateTimeOffset at,
        IReadOnlyDictionary<byte, List<SmbiosStruct>> byType, int? osCount)
    {
        var procs = byType.TryGetValue(4, out var s4) ? s4.Where(s => s.Length >= 0x1A).ToList() : [];
        int? smbiosCount = procs.Count > 0 ? procs.Count : null;

        // Type 4 的「核心數」是每一顆的欄位；總執行緒數要把它們加起來。
        long? smbiosThreads = null;
        if (procs.Count > 0)
        {
            long sum = 0;
            bool any = false;
            foreach (var p in procs)
            {
                if (p.Length > 0x26 && p.WordAt(0x25) > 0) { sum += p.WordAt(0x25); any = true; }
                else if (p.Length > 0x24 && p.WordAt(0x23) > 0) { sum += p.WordAt(0x23); any = true; }
            }
            if (any) smbiosThreads = sum;
        }

        if (smbiosCount is null && osCount is null)
            return new HardwareFact(CpuCountKey, Category, "處理器數",
                "兩邊都沒有值", "", Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "SMBIOS 未列 Type 4 且 WMI 沒有處理器數——無從對照");

        if (smbiosCount is null || osCount is null)
        {
            string have = smbiosCount is not null ? "SMBIOS" : "WMI";
            string onlyOne = smbiosCount is not null
                ? $"SMBIOS：{smbiosCount} 顆（WMI 無值）"
                : $"WMI：{osCount} 顆（SMBIOS 無值）";
            return new HardwareFact(CpuCountKey, Category, "處理器數", onlyOne, "", Source,
                FactTrustLevel.Measured, false, at, null, FactAvailability.Unknown,
                $"只有 {have} 那一邊有值——這是「無從對照」而不是「不一致」");
        }

        // 「處理器數」與「邏輯處理器數」是兩個不同的量，不能直接比。
        // 所以這裡報的是「SMBIOS 宣告幾顆實體處理器、OS 看到幾個邏輯處理器」。
        string value = $"SMBIOS 宣告 {smbiosCount} 顆實體處理器"
            + (smbiosThreads is not null ? $"（合計 {smbiosThreads} 執行緒）" : "")
            + $"；WMI 回報 {osCount} 個邏輯處理器";

        bool plausible = smbiosThreads is null
                      || osCount == smbiosThreads
                      || osCount <= smbiosThreads;

        return new HardwareFact(CpuCountKey, Category, "處理器數", value, "", Source,
            FactTrustLevel.Measured, false, at, osCount, FactAvailability.Present,
            plausible
                ? "**這兩個數不是同一個量**：一個是實體處理器與它的執行緒，"
                  + "一個是 OS 看到的邏輯處理器數。兩個都要看，不能只留一個"
                : $"OS 看到的邏輯處理器（{osCount}）多於 SMBIOS 宣告的執行緒總數"
                  + $"（{smbiosThreads}）——那是**具體的落差**，可能是韌體沒有更新 Type 4，"
                  + "也可能是虛擬化層的呈現方式；本服務只指出落差，不判斷誰對");
    }

    private static HardwareFact Summary(DateTimeOffset at, IReadOnlyList<HardwareFact> parts)
    {
        int comparable = parts.Count(p => p.NumericValue is 1 or 0);
        int agreed = parts.Count(p => p.NumericValue is 1);
        int noBasis = parts.Count(p => p.UnavailableReason?.Contains("無從對照", StringComparison.Ordinal) == true);

        string value = $"可對照 {comparable} 組（一致 {agreed}、不一致 {comparable - agreed}）；"
                     + $"無從對照 {noBasis} 組";

        return new HardwareFact(ComparableKey, Category, "對照涵蓋",
            value, "組", Source, FactTrustLevel.Derived, false, at, comparable,
            comparable == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
            // 「不給一致率」是這個服務對外的政策宣告，與一致性無關——
            // 所以它必須在**每一個**分支裡都出現，否則那句話會隨著資料而消失。
            "本服務**不給一致率**——那個數字會被讀成評分，"
            + "而「兩邊不同」通常只是來源與時機不同，不是誰錯了。"
            + (comparable == 0
                ? "（目前沒有可對照的組）"
                : agreed == comparable
                    ? "另外：所有可對照的欄位兩邊都一致——**但那不代表兩邊是同一個來源**："
                      + "有可能是 WMI 直接轉述 SMBIOS，那時「一致」不提供額外的確認"
                    : $"目前 {comparable} 組可對照中有 {comparable - agreed} 組不一致，"
                      + "那些落差要看上面各自的說明，而不是看這裡的數字"));
    }

    private static SmbiosStruct? First(IReadOnlyDictionary<byte, List<SmbiosStruct>> byType, byte type)
        => byType.TryGetValue(type, out var list) && list.Count > 0 ? list[0] : null;

    private static string Join(string? a, string? b)
    {
        var parts = new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim());
        return string.Join(" ", parts);
    }

    private static string Str(SmbiosStruct s, int offset)
    {
        if (offset >= s.Length) return "";
        string? v = s.GetString(s.ByteAt(offset));
        return v is null || v.Length == 0 || SmbiosService.IsPlaceholder(v) ? "" : v;
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [BiosKey, SystemKey, BoardKey, CpuCountKey, ComparableKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
