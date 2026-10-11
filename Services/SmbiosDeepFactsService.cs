using System.Globalization;

namespace XinSpect;

/// <summary>
/// SMBIOS 表層深化的<b>事實</b>產出（Vol2 SB 域）：Type 0–3 全欄位（SB-001）、Type 9 插槽（SB-002）、
/// Type 17 記憶體裝置（SB-003）、Type 42 管理控制器（SB-004）、Type 11 OEM 字串（SB-005）、
/// Type 39 電源供應（SB-006）、M.2／U.2 槽位對照（SB-015）。
/// </summary>
/// <remarks>
/// <para>與 <see cref="SmbiosFacts"/> 的分工：那份是「驗機」用的判讀列；這份是<b>事實鍵</b>，
/// 給快照／CLI／對帳用，兩者讀同一批 <see cref="SmbiosStruct"/>，不各自重讀表。</para>
/// <para><b>逐欄位移出處</b>：dmidecode 3.x `dmidecode.c`（2026-10-11 抓取核對）——Type 9：Designation@0x04、
/// Type@0x05、Bus Width@0x06、Current Usage@0x07；Type 17：Size@0x0C（0x7FFF→Extended@0x1C）、
/// Locator@0x10、Bank@0x11、Type@0x12、Speed@0x15、Manufacturer@0x17、Serial@0x18、Part@0x1A、
/// Rank@0x1B(bits 3:0)、Configured@0x20；Type 39：Group@0x04、Location@0x05…Revision@0x0B、
/// MaxPower@0x0C(word)、Characteristics@0x0E(word)；Type 42：Interface Type@0x04（SMBIOS < 3.2）。</para>
/// <para><b>誠實界線</b>：序號一律<b>不進事實值</b>（只在「有無」計數裡出現）——序號屬 L1，
/// 要看得去資產頁；OEM 字串可能夾帶識別碼，整條標 Sensitive；Type 42 的型別相依中段無長度欄
/// （dmidecode 自己也說 "no easy way to decode"），故只出介面型別。</para>
/// </remarks>
public static class SmbiosDeepFactsService
{
    private const string Category = "SMBIOS";

    /// <summary>M.2／U.2 的插槽型別碼（SMBIOS 規格：0x14–0x17 M.2、0x1F／0x20／0x24／0x25 SFF-8639 U.2）。</summary>
    private static readonly byte[] M2SlotCodes = [0x14, 0x15, 0x16, 0x17, 0x1F, 0x20, 0x24, 0x25];

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<IReadOnlyList<SmbiosStruct>>? structsProbe = null)
    {
        IReadOnlyList<SmbiosStruct> structs;
        try { structs = (structsProbe ?? (() => new SmbiosService().Structs))(); }
        catch (Exception ex)
        {
            return Na(at, "SMBIOS 表讀取失敗：" + ex.Message, FactAvailability.ReadError);
        }

        if (structs.Count == 0)
            return Na(at, "SMBIOS 表不可用或無結構（GetSystemFirmwareTable('RSMB') 未回表）", FactAvailability.NotSupported);

        var facts = new List<HardwareFact>();
        var byType = structs.GroupBy(s => s.Type).ToDictionary(g => g.Key, g => g.ToList());

        // ── SB-001 Type 0–3 全欄位 ────────────────────────────────────────
        int typeCount = byType.Keys.Count(t => t <= 3);
        facts.Add(new HardwareFact("sb.type03.count", Category, "Type 0–3 到齊數",
            $"{typeCount}/4", "張", "SMBIOS Type 0/1/2/3", FactTrustLevel.Measured, false, at, typeCount));
        facts.Add(new HardwareFact("sb.type03", Category, "BIOS／系統／主機板／機箱",
            Type03Summary(byType), "", "SMBIOS Type 0/1/2/3", FactTrustLevel.Measured, false, at));

        // ── SB-002 Type 9 插槽 ───────────────────────────────────────────
        var slots = byType.TryGetValue(9, out var s9) ? s9.Where(s => s.Length >= 0x0C).ToList() : [];
        facts.Add(new HardwareFact("sb.slot.count", Category, "系統插槽數",
            slots.Count.ToString(CultureInfo.InvariantCulture), "個", "SMBIOS Type 9", FactTrustLevel.Measured, false, at, slots.Count));
        facts.Add(new HardwareFact("sb.slot.summary", Category, "系統插槽明細",
            slots.Count > 0
                ? string.Join("；", slots.Take(12).Select(s =>
                    $"{Str(s, 0x04)}（{SmbiosService.SlotTypeName(s.ByteAt(0x05))}／{SmbiosService.SlotWidthName(s.ByteAt(0x06))}／{SmbiosService.SlotUsageName(s.ByteAt(0x07))}）"))
                : "（SMBIOS 未列任何插槽——不代表沒有，部分主機板 Type 9 留空）",
            "", "SMBIOS Type 9", FactTrustLevel.Measured, false, at));

        // ── SB-003 Type 17 記憶體裝置 ────────────────────────────────────
        var dimms = byType.TryGetValue(17, out var s17) ? s17.Where(s => s.Length >= 0x15).ToList() : [];
        int withSerial = dimms.Count(s => s.Length > 0x18 && s.ByteAt(0x18) > 0 && Str(s, 0x18).Length > 0 && !SmbiosService.IsPlaceholder(Str(s, 0x18)));
        facts.Add(new HardwareFact("sb.dim.count", Category, "記憶體裝置數",
            dimms.Count.ToString(CultureInfo.InvariantCulture), "條", "SMBIOS Type 17", FactTrustLevel.Measured, false, at, dimms.Count));
        facts.Add(new HardwareFact("sb.dim.serial.count", Category, "回報序號的記憶體條數",
            withSerial.ToString(CultureInfo.InvariantCulture), "條", "SMBIOS Type 17 位移 0x18（只計有無，序號內容不入事實值）",
            FactTrustLevel.Measured, false, at, withSerial));
        facts.Add(new HardwareFact("sb.dim.summary", Category, "記憶體裝置明細",
            dimms.Count > 0
                ? string.Join("；", dimms.Select(DescribeDimm))
                : "（SMBIOS 未列任何記憶體裝置）",
            "", "SMBIOS Type 17（序號依 L1 原則不列入）", FactTrustLevel.Measured, false, at));

        // ── SB-004 Type 42 管理控制器 ────────────────────────────────────
        var mctl = byType.TryGetValue(42, out var s42) ? s42.Where(s => s.Length >= 0x05).ToList() : [];
        if (mctl.Count > 0)
            facts.Add(new HardwareFact("sb.mctl", Category, "管理控制器主機介面",
                string.Join("；", mctl.Select(s => $"{MctlTypeName(s.ByteAt(0x04))}（0x{s.ByteAt(0x04):X2}）"))
                    + "；型別相依中段無長度欄（dmidecode 亦註明不可靠），故只出介面型別",
                "", "SMBIOS Type 42 位移 0x04", FactTrustLevel.Measured, false, at, (long)mctl.Count));
        else
            facts.Add(new HardwareFact("sb.mctl", Category, "管理控制器主機介面", "", "",
                "SMBIOS Type 42", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "本機 SMBIOS 無 Type 42（無 BMC／管理控制器）"));

        // ── SB-005 Type 11 OEM 字串（可能夾帶識別碼→Sensitive） ──────────
        var oem = byType.TryGetValue(11, out var s11) ? s11.SelectMany(s => s.Strings).Where(x => x.Length > 0).ToList() : [];
        facts.Add(new HardwareFact("sb.oem.count", Category, "OEM 字串數",
            oem.Count.ToString(CultureInfo.InvariantCulture), "條", "SMBIOS Type 11", FactTrustLevel.Measured, false, at, oem.Count));
        facts.Add(new HardwareFact("sb.oem.list", Category, "OEM 字串",
            oem.Count > 0 ? string.Join(" | ", oem) : "（無）", "",
            "SMBIOS Type 11（可能夾帶機器識別碼，標為敏感）", FactTrustLevel.Measured, true, at));

        // ── SB-006 Type 39 電源供應 ──────────────────────────────────────
        var psus = byType.TryGetValue(39, out var s39) ? s39.Where(s => s.Length >= 0x10).ToList() : [];
        if (psus.Count > 0)
            facts.Add(new HardwareFact("sb.psu", Category, "電源供應器",
                string.Join("；", psus.Select(DescribePsu)), "",
                "SMBIOS Type 39（序號依 L1 原則不列入）", FactTrustLevel.Measured, false, at, (long)psus.Count));
        else
            facts.Add(new HardwareFact("sb.psu", Category, "電源供應器", "", "",
                "SMBIOS Type 39", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "本機 SMBIOS 無 Type 39（多半只有整機廠填，DIY 與伺服器常缺）"));

        // ── SB-015 M.2／U.2 槽位對照 ─────────────────────────────────────
        var m2 = slots.Where(s => M2SlotCodes.Contains(s.ByteAt(0x05))).ToList();
        facts.Add(new HardwareFact("sb.m2slot.count", Category, "M.2／U.2 槽位數",
            m2.Count.ToString(CultureInfo.InvariantCulture), "個", "SMBIOS Type 9 插槽型別碼（0x14–0x17、0x1F/0x20/0x24/0x25）",
            FactTrustLevel.Measured, false, at, m2.Count));
        facts.Add(new HardwareFact("sb.m2slot.summary", Category, "M.2／U.2 槽位明細",
            m2.Count > 0
                ? string.Join("；", m2.Select(s => $"{Str(s, 0x04)}（{SmbiosService.SlotTypeName(s.ByteAt(0x05))}／{SmbiosService.SlotUsageName(s.ByteAt(0x07))}）"))
                : "（未偵測到——部分主機板不在 Type 9 列出 M.2，缺列不代表沒有）",
            "", "SMBIOS Type 9 插槽型別碼", FactTrustLevel.Measured, false, at));

        return facts;
    }

    private static string Type03Summary(IReadOnlyDictionary<byte, List<SmbiosStruct>> byType)
    {
        var parts = new List<string>();
        if (byType.TryGetValue(0, out var b) && b[0].Length >= 0x09)
            parts.Add($"BIOS {Str(b[0], 0x04)} {Str(b[0], 0x05)}（{Str(b[0], 0x08)}）");
        if (byType.TryGetValue(1, out var sys) && sys[0].Length >= 0x08)
            parts.Add($"系統 {Str(sys[0], 0x04)} {Str(sys[0], 0x05)}");
        if (byType.TryGetValue(2, out var brd) && brd[0].Length >= 0x07)
            parts.Add($"主機板 {Str(brd[0], 0x04)} {Str(brd[0], 0x05)} {Str(brd[0], 0x06)}");
        if (byType.TryGetValue(3, out var ch) && ch[0].Length >= 0x06)
        {
            byte types = ch[0].ByteAt(0x05);
            string name = SmbiosService.ChassisTypeName((byte)(types & 0x7F));
            string lockText = (types & 0x80) != 0 ? "，機箱鎖存在" : "";
            parts.Add($"機箱 {name}{lockText}");
        }
        return parts.Count > 0 ? string.Join("；", parts) : "（Type 0–3 皆未提供）";
    }

    private static string DescribeDimm(SmbiosStruct s)
    {
        string locator = Str(s, 0x10);
        string bank = s.Length > 0x11 ? Str(s, 0x11) : "—";
        string size = FormatSize(s);
        string type = SmbiosService.MemoryTypeName(s.ByteAt(0x12));
        string speed = s.WordAt(0x15) != 0 ? $"{s.WordAt(0x15)} MT/s" : "速率未報";
        string configured = s.Length >= 0x22 && s.WordAt(0x20) != 0 ? $"，實跑 {s.WordAt(0x20)}" : "";
        string rank = s.Length > 0x1B && (s.ByteAt(0x1B) & 0x0F) != 0 ? $"，{s.ByteAt(0x1B) & 0x0F} rank" : "";
        return $"{locator}／{bank}：{size} {type} {speed}{configured}{rank}";
    }

    /// <summary>Type 17 位移 0x0C 的容量字：bit15=0 表示 MB、=1 表示 KB；0x7FFF 表示改看 0x1C 的擴充容量（MB）。</summary>
    [SpecRef("SMBIOS Specification, Memory Device (Type 17)：Size WORD@0x0C（bit15 0＝MB、1＝KB；0x7FFF＝改用 Extended Size DWORD@0x1C，單位 MB）。位移核對 dmidecode 3.x")]
    internal static string FormatSize(SmbiosStruct s)
    {
        ushort raw = s.WordAt(0x0C);
        if (raw == 0) return "未安裝";
        if (raw == 0x7FFF)
        {
            if (s.Length >= 0x20) return $"{s.DwordAt(0x1C)} MB";
            return "擴充容量欄不存在";
        }
        return (raw & 0x8000) != 0 ? $"{raw & 0x7FFF} KB" : $"{raw} MB";
    }

    private static string DescribePsu(SmbiosStruct s)
    {
        ushort chars = s.WordAt(0x0E);
        ushort power = s.WordAt(0x0C);
        string powerText = (power & 0x8000) != 0 ? "功率未回報" : $"{power} W";
        string present = (chars & 0x0002) != 0 ? "存在" : "不在位";
        string plugged = (chars & 0x0004) == 0 ? "，已插電" : "，未插電";
        return $"{Str(s, 0x06)}（{Str(s, 0x07)}）{powerText}，{present}{plugged}";
    }

    private static string MctlTypeName(byte code) => code switch
    {
        0x02 => "KCS",
        0x03 => "8250 UART",
        0x04 => "16450 UART",
        0x05 => "16550 UART",
        0x06 => "16650 UART",
        0x07 => "16750 UART",
        0x08 => "16850 UART",
        0x40 => "Network",
        0xF0 => "OEM",
        <= 0x3F => "MCTP",
        _ => "規格外"
    };

    private static string Str(SmbiosStruct s, int offset)
    {
        string? v = offset < s.Length ? s.GetString(s.ByteAt(offset)) : null;
        return v is null || v.Length == 0 ? "—" : v + SmbiosService.PlaceholderNote(v);
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason, FactAvailability availability)
    {
        string[] keys =
        [
            "sb.type03.count", "sb.type03", "sb.slot.count", "sb.slot.summary",
            "sb.dim.count", "sb.dim.serial.count", "sb.dim.summary",
            "sb.mctl", "sb.oem.count", "sb.oem.list", "sb.psu",
            "sb.m2slot.count", "sb.m2slot.summary",
        ];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", "SMBIOS",
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
