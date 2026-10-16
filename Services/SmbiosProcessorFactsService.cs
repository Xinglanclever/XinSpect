using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// SMBIOS Type 4（處理器）與 Type 7（快取）的事實產出
/// （ULTIMATE-1000 階段二 P2-020）。
///
/// <para>
/// <b>為什麼這一項值得獨立做：</b>Type 4 的「核心數／執行緒數」與
/// CPUID 讀到的、OS 看到的<b>是三個獨立來源</b>。
/// 三者的差異本身就是重要的事實——韌體宣告 8 核而 OS 只看到 4 核，
/// 那通常意味著某種遮蔽或組態限制，而<b>那不是「哪一個錯了」，是三個都要並列</b>。
/// </para>
///
/// <para>
/// <b>界線：這裡只陳述 SMBIOS 說了什麼。</b>本服務不做「哪個數才是對的」的判斷，
/// 也不把 Type 4 的值當成處理器的真實規格——它是<b>主機板廠填的欄位</b>，
/// 而那個欄位歷史上常常填錯（尤其是組裝機的工程樣品與後期 BIOS）。
/// </para>
/// </summary>
public static class SmbiosProcessorFactsService
{
    private const string Category = "SMBIOS";
    private const string Source4 = "SMBIOS Type 4";
    private const string Source7 = "SMBIOS Type 7";

    public const string ProcCountKey = "sb.type4.count";
    public const string ProcKey = "sb.type4";
    public const string CacheKey = "sb.type7.summary";
    public const string CacheCountKey = "sb.type7.count";

    /// <summary>
    /// 收集 Type 4／7 事實。<paramref name="structsProbe"/> 為 null 時走真實的
    /// <see cref="SmbiosService"/>；測試注入假結構。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
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
        var facts = new List<HardwareFact>();

        // ── Type 4 處理器 ────────────────────────────────────────────────
        var procs = byType.TryGetValue(4, out var s4) ? s4.Where(s => s.Length >= 0x1A).ToList() : [];
        facts.Add(new HardwareFact(ProcCountKey, Category, "SMBIOS 宣告的處理器數",
            procs.Count.ToString(CultureInfo.InvariantCulture), "顆", Source4,
            FactTrustLevel.Measured, false, at, procs.Count,
            procs.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
            procs.Count == 0
                ? "SMBIOS 未列任何 Type 4——有些虛擬機與精簡韌體不填這一項；"
                  + "那不代表沒有處理器，只代表這個來源沒有陳述"
                : null));

        facts.Add(procs.Count > 0
            ? new HardwareFact(ProcKey, Category, "處理器明細", DescribeProcessors(procs), "",
                Source4 + "（Socket Designation@0x04、Manufacturer@0x07、Family@0x06、"
                + "Max Speed@0x14、Core Count@0x23、Thread Count@0x25）",
                FactTrustLevel.Measured, false, at, null, FactAvailability.Present,
                "這是主機板廠填的欄位，不是 CPUID 讀到的規格——"
                + "與 OS／CPUID 的差異是三個來源並列的事實，不是哪一個錯了")
            : new HardwareFact(ProcKey, Category, "處理器明細", "", "", Source4,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有 Type 4 可描述"));

        // ── Type 7 快取 ──────────────────────────────────────────────────
        var caches = byType.TryGetValue(7, out var s7) ? s7.Where(s => s.Length >= 0x13).ToList() : [];
        facts.Add(new HardwareFact(CacheCountKey, Category, "SMBIOS 宣告的快取數",
            caches.Count.ToString(CultureInfo.InvariantCulture), "個", Source7,
            FactTrustLevel.Measured, false, at, caches.Count,
            caches.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
            caches.Count == 0
                ? "SMBIOS 未列任何 Type 7——快取資訊通常另有 CPUID 來源，這裡缺席不影響判讀"
                : null));

        facts.Add(caches.Count > 0
            ? new HardwareFact(CacheKey, Category, "快取明細", DescribeCaches(caches), "",
                Source7 + "（Socket Designation@0x04、Cache Configuration@0x05、"
                + "Max Size@0x07、Installed Size@0x09、Associativity@0x0C、Line Size@0x0D）",
                FactTrustLevel.Measured, false, at, null, FactAvailability.Present,
                "快取大小的高位是粒度旗標（bit15 為 1 時單位是 64 KiB），"
                + "解讀時已依規格換算——這一項是本服務自己算的，不是原始值")
            : new HardwareFact(CacheKey, Category, "快取明細", "", "", Source7,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有 Type 7 可描述"));

        return facts;
    }

    /// <summary>每顆處理器一行；欄位讀不到就略過該欄而不是填 0。</summary>
    private static string DescribeProcessors(IReadOnlyList<SmbiosStruct> procs)
    {
        var parts = new List<string>();
        for (int i = 0; i < procs.Count; i++)
        {
            var s = procs[i];
            var fields = new List<string>();

            string? socket = Str(s, 0x04);
            if (socket.Length > 0) fields.Add($"插槽 {socket}");

            string vendor = $"{Str(s, 0x07)} {Str(s, 0x08)}".Trim();
            if (vendor.Length > 0) fields.Add(vendor);

            // Family 是位元組代碼（0x01 其他、0x02 未知、0xB3 起是規格列舉）；
            // 取不到有意義的名字時如實報代碼，不猜。
            byte family = s.Length > 0x06 ? s.ByteAt(0x06) : (byte)0;
            fields.Add($"家族 0x{family:X2}");

            // 核心數與執行緒數是 u16@0x23／@0x25（SMBIOS 2.5+ 才有；前面的版本欄位較短）。
            if (s.Length > 0x24)
            {
                ushort cores = s.WordAt(0x23);
                if (cores > 0) fields.Add($"{cores} 核");
            }
            if (s.Length > 0x26)
            {
                ushort threads = s.WordAt(0x25);
                if (threads > 0) fields.Add($"{threads} 執行緒");
            }

            if (s.Length > 0x16)
            {
                ushort maxSpeed = s.WordAt(0x14);
                if (maxSpeed > 0) fields.Add($"標示最高 {maxSpeed} MHz");
            }

            parts.Add($"[{i}] " + string.Join("／", fields));
        }
        return string.Join("；", parts);
    }

    /// <summary>
    /// 每筆快取一行。大小欄位的高位是粒度旗標：
    /// <b>bit15 = 1 時單位是 64 KiB，否則單位是 1 KiB</b>——
    /// 直接把它當 KiB 讀會讓大快取少算 64 倍。
    /// </summary>
    private static string DescribeCaches(IReadOnlyList<SmbiosStruct> caches)
    {
        var parts = new List<string>();
        for (int i = 0; i < caches.Count; i++)
        {
            var s = caches[i];
            var fields = new List<string>();

            string? socket = Str(s, 0x04);
            if (socket.Length > 0) fields.Add(socket);

            ushort config = s.WordAt(0x05);
            fields.Add($"L{(config & 0x07) + 1}");

            long installed = DecodeCacheSize(s.WordAt(0x09));
            fields.Add(installed > 0 ? $"{installed / 1024} KiB" : "大小未提供");

            if (s.Length > 0x0D)
            {
                byte assoc = s.ByteAt(0x0C);
                if (assoc > 0) fields.Add($"{AssociativityName(assoc)}關聯");

                ushort line = s.ByteAt(0x0D);
                if (line > 0) fields.Add($"行 {line} 位元組");
            }

            parts.Add($"[{i}] " + string.Join("／", fields));
        }
        return string.Join("；", parts);
    }

    /// <summary>
    /// 解快取大小：bit15 為粒度旗標——<b>1 表示單位是 64 KiB，0 表示單位是 1 KiB</b>。
    /// 回傳位元組數；0 代表未提供。
    /// </summary>
    internal static long DecodeCacheSize(ushort raw)
    {
        if (raw == 0) return 0;
        int granularity = (raw & 0x8000) != 0 ? 64 : 1;
        int value = raw & 0x7FFF;
        return (long)value * granularity * 1024;
    }

    private static string AssociativityName(byte code) => code switch
    {
        0x01 => "其他",
        0x02 => "未知",
        0x03 => "直接對映",
        0x04 => "2 路",
        0x05 => "4 路",
        0x06 => "全關聯",
        0x07 => "8 路",
        0x08 => "16 路",
        0x09 => "12 路",
        0x0A => "24 路",
        0x0B => "32 路",
        0x0C => "48 路",
        0x0D => "64 路",
        0x0E => "96 路",
        0x0F => "128 路",
        _ => $"0x{code:X2}",
    };

    /// <summary>依 1 起始索引取字串；佔位字串（To Be Filled By O.E.M. 等）視為未提供。</summary>
    private static string Str(SmbiosStruct s, int offset)
    {
        if (offset >= s.Length) return "";
        string? v = s.GetString(s.ByteAt(offset));
        return v is null || v.Length == 0 || SmbiosService.IsPlaceholder(v) ? "" : v;
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason, FactAvailability availability)
    {
        string[] keys = [ProcCountKey, ProcKey, CacheCountKey, CacheKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", "SMBIOS",
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
