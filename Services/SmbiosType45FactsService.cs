using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// SMBIOS Type 45（Firmware Inventory Information，SMBIOS 3.5+ 新增）的事實產出（U10-SB-0161）。
///
/// <para>
/// <b>這是全新的解碼</b>：既有的 SMBIOS 服務解到 Type 43 為止，Type 45 的「逐韌體組件清單」
/// 沒有任何一支碰過。版面核對的出處是 <b>dmidecode 的 Type 45 解碼（§7.46）</b>，
/// 與 DMTF DSP0134 3.5.0 的 Table 132——欄位位移逐欄抄自那份實作，不憑記憶：
/// </para>
/// <list type="bullet">
///   <item>0x04 組件名、0x05 韌體版本（字串索引）；0x06 版本格式、0x08 韌體 ID 格式（位元組）——
///     <b>dmidecode 也沒有解這兩個列舉</b>，本服務同樣只帶原始十六進位、不解讀（收錄表沒有出處不硬編）。</item>
///   <item>0x07 韌體 ID、0x09 發行日、0x0A 製造商、0x0B 最低支援版本（字串索引）；0x0C 映像大小（QWORD bytes，0＝未回報）。</item>
///   <item>0x14 特性（WORD）：bit0＝Updatable、bit1＝Write-Protect（§7.46.3）。</item>
///   <item>0x16 狀態（BYTE，§7.46.4：0x01 其他、0x02 未知、0x03 已停用、0x04 已啟用、0x05 不存在、
///     0x06 待機離線、0x07 待機備援、0x08 不可用離線）。</item>
///   <item>0x17 關聯組件數＋0x18 起逐個 WORD 控制代碼；最短需要 0x18。</item>
/// </list>
///
/// <para>
/// <b>版本閘：</b>Type 45 是 SMBIOS 3.5 才有的類型。<b>本機 SMBIOS 版本 &lt; 3.5＝NotApplicable</b>——
/// 那是環境事實，不是「韌體沒有韌體組件」的斷言。版本與結構來自<b>同一次</b>
/// <c>GetSystemFirmwareTable('RSMB')</c> 讀取（預設探針只建一次 <see cref="SmbiosService"/>），不讀第二份。
/// </para>
///
/// <para>
/// <b>與 pdev.fw.* 的對照（<c>sb.type45.cross</c>）：</b>Type 45 是<b>韌體端的組件自述</b>，
/// pdev.fw.version 是<b>周邊裝置的自述版本</b>（硬碟 FirmwareRevision）、pdev.fw.updatable 是
/// <b>裝置自述的支援旗標</b>。對照規則只有一條有出處：<b>版本字串完全相等（Ordinal、不去空白以外）</b>，
/// 不做模糊比對——「字串一樣」與「同一個組件」是兩個斷言，本服務只陳述前者。兩邊的缺席者都如實計數，
/// 不解釋原因（為什麼硬碟不在 Type 45 裡、為什麼 BMC 韌體不在 Win32_DiskDrive 裡——都是來源自己的粒度）。
/// </para>
/// </summary>
public static class SmbiosType45FactsService
{
    private const string Category = "SMBIOS";
    private const string Source = "SMBIOS Type 45（GetSystemFirmwareTable('RSMB')，唯讀）；SMBIOS 3.5+ §7.46";

    public const string ExistenceKey = "sb.type45";
    public const string CrossKey = "sb.type45.cross";

    /// <summary>逐組件明細的<strong>家族前綴</strong>——有幾個韌體組件是韌體的事實，成員編譯期列不完。</summary>
    private const string Type45ItemPrefix = "sb.type45.item.";

    /// <summary>SMBIOS 表的一次讀取：結構＋版本（同一份，不讀兩次）。</summary>
    public sealed record SmbiosTable(IReadOnlyList<SmbiosStruct> Structs, byte Major, byte Minor);

    public delegate SmbiosTable? TableProbe();
    public delegate PeripheralFirmwareFactsService.PeripheralInventory? InventoryProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        TableProbe? tableProbe = null, InventoryProbe? inventoryProbe = null)
    {
        SmbiosTable? table;
        try { table = (tableProbe ?? DefaultProbe)(); }
        catch (Exception ex)
        {
            return Both(ExistenceKey, CrossKey, at, FactAvailability.ReadError,
                "SMBIOS 表讀取失敗：" + ex.Message + "——「讀不到表」與「表裡沒有 Type 45」是不同的斷言");
        }

        if (table is null)
            return Both(ExistenceKey, CrossKey, at, FactAvailability.ReadError,
                "SMBIOS 表不可用（GetSystemFirmwareTable('RSMB') 未回表）——不推測");

        if (table.Major < 3 || (table.Major == 3 && table.Minor < 5))
            return Both(ExistenceKey, CrossKey, at, FactAvailability.NotApplicable,
                $"本機 SMBIOS 版本 {table.Major}.{table.Minor}，低於 Type 45 所需的 3.5——"
                + "**這是環境事實（NotApplicable），不是「這台機器沒有韌體組件」**");

        var entries = table.Structs.Where(s => s.Type == 45).Select(Decode).ToList();
        if (entries.Count == 0)
            return Both(ExistenceKey, CrossKey, at, FactAvailability.NotApplicable,
                $"SMBIOS {table.Major}.{table.Minor} 但未列 Type 45——**韌體沒有提供韌體組件清單**，"
                + "這是查證後的「沒有列」，不是讀取失敗");

        var facts = new List<HardwareFact>(entries.Count + 2)
        {
            new(ExistenceKey, Category, "韌體組件清單存在性",
                $"Type 45 條目 {entries.Count} 筆（SMBIOS {table.Major}.{table.Minor}）", "筆", Source,
                FactTrustLevel.Measured, false, at, entries.Count, FactAvailability.Present, null),
        };
        for (int i = 0; i < entries.Count; i++)
            facts.Add(ItemFact(at, i, entries[i]));
        facts.Add(CrossFact(at, entries, inventoryProbe));
        return facts;
    }

    /// <summary>解碼失敗（結構長度不足）的條目也要有鍵——「解不開」不是「不存在」。</summary>
    private static HardwareFact ItemFact(DateTimeOffset at, int index, FirmwareComponent e)
    {
        string key = Type45ItemPrefix + index.ToString(CultureInfo.InvariantCulture);
        if (e.Unusable)
            return new HardwareFact(key, Category, $"韌體組件 {index}", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                $"Type 45 結構長度 {e.RawLength} bytes，短於本解碼器所需的最小 0x18——如實拒解，不猜");

        var parts = new List<string>();
        if (e.ComponentName is { } n) parts.Add($"組件名 {n}");
        if (e.FirmwareVersion is { } v) parts.Add($"版本 {v}");
        if (e.FirmwareId is { } id) parts.Add($"韌體 ID {id}");
        if (e.ReleaseDate is { } r) parts.Add($"發行日 {r}");
        if (e.Manufacturer is { } m) parts.Add($"製造商 {m}");
        if (e.LowestSupportedVersion is { } l) parts.Add($"最低支援版本 {l}");
        parts.Add(e.ImageSizeBytes is { } sz && sz > 0 ? $"映像大小 {sz.ToString("#,0", CultureInfo.InvariantCulture)} bytes" : "映像大小未回報");
        parts.Add($"Updatable {Bit(e.Characteristics.HasFlag(FwCharacteristics.Updatable))}、Write-Protect {Bit(e.Characteristics.HasFlag(FwCharacteristics.WriteProtect))}");
        parts.Add($"狀態 {StateName(e.StateRaw)}");
        if (e.VersionFormatRaw is byte vf) parts.Add($"版本格式 0x{vf:X2}（未收錄對照表，不解讀）");
        if (e.AssociatedHandles.Count > 0)
        {
            string handles = string.Join("、0x", e.AssociatedHandles.Select(h => h.ToString("X4", CultureInfo.InvariantCulture)));
            parts.Add($"關聯組件 {e.AssociatedHandles.Count} 個（0x{handles}）");
        }
        parts.Add("**以上是韌體在表裡的自述**，不判斷新舊、不對照原廠清單");

        return new HardwareFact(key, Category, $"韌體組件 {index}",
            string.Join("；", parts), "", Source,
            FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact CrossFact(DateTimeOffset at, IReadOnlyList<FirmwareComponent> entries,
        InventoryProbe? inventoryProbe)
    {
        PeripheralFirmwareFactsService.PeripheralInventory? inv;
        try { inv = (inventoryProbe ?? new InventoryProbe(PeripheralFirmwareFactsService.ReadInventory))(); }
        catch { inv = null; }

        var versions = entries.Where(e => !e.Unusable && e.FirmwareVersion is { } v && v.Length > 0)
            .Select(e => e.FirmwareVersion!).ToList();
        int matched, t45Unmatched, diskUnrepresented;
        string pdevSide;
        if (inv is null)
        {
            matched = 0; t45Unmatched = versions.Count; diskUnrepresented = 0;
            pdevSide = "**周邊韌體盤點（pdev.fw.version 的來源）列舉失敗——對照面缺一半**，"
                + "只陳述 Type 45 那一半（交集無從計起，如實標）";
        }
        else
        {
            var diskFw = inv.Disks.Select(d => d.Firmware).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            matched = versions.Count(v => diskFw.Any(f => string.Equals(f, v, StringComparison.Ordinal)));
            t45Unmatched = versions.Count - matched;
            diskUnrepresented = diskFw.Count(f => !versions.Any(v => string.Equals(f, v, StringComparison.Ordinal)));
            pdevSide = $"周邊自述盤點（硬碟 {inv.Disks.Count} 顆：自述韌體版本 {diskFw.Count} 顆、"
                + $"不自述 {inv.Disks.Count - diskFw.Count} 顆——不自述者無從比對，如實計數）";
        }

        return new HardwareFact(CrossKey, Category, "Type 45 × 周邊韌體版本對照",
            $"Type 45 韌體組件 {entries.Count} 筆（其中自述版本 {versions.Count} 筆）× {pdevSide}"
            + $"：版本字串**完全相等**（Ordinal，不做模糊比對）者 {matched} 筆；"
            + $"Type 45 端無對應 {t45Unmatched} 筆、周邊端未出現在 Type 45 {diskUnrepresented} 筆——"
            + "**缺席不解釋**（兩個來源粒度不同：Type 45 列的是韌體組件，Win32_DiskDrive 列的是磁碟裝置）。"
            + "Type 45 的 Characteristics bit0（Updatable）與 pdev.fw.updatable 的支援旗標**同屬裝置自述面**，"
            + "兩者不互判；本對照只陳述「字串相等」，**不陳述「同一個組件」**",
            "筆", Source, FactTrustLevel.Derived, false, at, matched, FactAvailability.Present, null);
    }

    private static string Bit(bool v) => v ? "是" : "否";

    private static IReadOnlyList<HardwareFact> Both(string k1, string k2, DateTimeOffset at,
        FactAvailability availability, string reason) =>
    [
        new(k1, Category, "韌體組件清單存在性", "", "筆", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason),
        new(k2, Category, "Type 45 × 周邊韌體版本對照", "", "筆", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason),
    ];

    /// <summary>預設探針：SmbiosService 只建一次——結構與版本來自同一份表，不讀第二份。</summary>
    private static SmbiosTable? DefaultProbe()
    {
        var s = new SmbiosService();
        if (!s.Available) return null;
        var m = s.VersionText.Length > 7 ? s.VersionText[7..].Split('.') : new[] { "0", "0" };
        return new SmbiosTable(s.Structs,
            byte.TryParse(m.ElementAtOrDefault(0), out var maj) ? maj : (byte)0,
            byte.TryParse(m.ElementAtOrDefault(1), out var min) ? min : (byte)0);
    }

    // ── 解碼 ────────────────────────────────────────────────────────────

    [Flags]
    public enum FwCharacteristics : ushort
    {
        None = 0,
        /// <summary>§7.46.3 bit 0：可更新。</summary>
        Updatable = 0x0001,
        /// <summary>§7.46.3 bit 1：寫入保護。</summary>
        WriteProtect = 0x0002,
    }

    /// <summary>一筆 Type 45 的解碼結果（純函式輸出；字串區沒給的欄位是 <c>null</c>，不代填）。</summary>
    public sealed record FirmwareComponent(
        bool Unusable, int RawLength,
        string? ComponentName, string? FirmwareVersion, byte? VersionFormatRaw,
        string? FirmwareId, string? ReleaseDate, string? Manufacturer, string? LowestSupportedVersion,
        ulong? ImageSizeBytes, FwCharacteristics Characteristics, byte StateRaw,
        IReadOnlyList<ushort> AssociatedHandles);

    /// <summary>§7.46.4 的狀態代碼。收錄表抄 dmidecode（dmi_firmware_state）；未收錄如實帶原碼。</summary>
    internal static string StateName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "已停用", 0x04 => "已啟用",
        0x05 => "不存在", 0x06 => "待機離線", 0x07 => "待機備援", 0x08 => "不可用離線",
        _ => $"0x{code:X2}（規格未收錄）",
    };

    /// <summary>Type 45 → 韌體組件（純函式）。最短需要 0x18；不足＝Unusable，不猜。</summary>
    [SpecRef("SMBIOS Specification 3.5.0 Table 132（§7.46 Firmware Inventory Information），位移核對 dmidecode 3.x "
        + "dmi_decode case 45：0x04 組件名（字串）、0x05 版本（字串）、0x06 版本格式、0x07 韌體 ID（字串）、"
        + "0x08 韌體 ID 格式、0x09 發行日（字串）、0x0A 製造商（字串）、0x0B 最低支援版本（字串）、"
        + "0x0C 映像大小（QWORD bytes，0＝未回報）、0x14 特性（WORD：bit0 Updatable、bit1 Write-Protect）、"
        + "0x16 狀態（BYTE，§7.46.4 代碼 01h–08h）、0x17 關聯組件數（BYTE）、0x18 起逐個 WORD 控制代碼")]
    internal static FirmwareComponent Decode(SmbiosStruct s)
    {
        if (s.Length < 0x18)
            return new FirmwareComponent(true, s.Length, null, null, null, null, null, null, null,
                null, FwCharacteristics.None, 0, []);

        ulong imageSize = s.DwordAt(0x0C) | ((ulong)s.DwordAt(0x10) << 32);
        var characteristics = (FwCharacteristics)s.WordAt(0x14);
        byte state = s.ByteAt(0x16);
        byte assocCount = s.ByteAt(0x17);

        var handles = new List<ushort>();
        for (int i = 0; i < assocCount && 0x18 + (i + 1) * 2 <= s.Length; i++)
            handles.Add(s.WordAt(0x18 + i * 2));

        return new FirmwareComponent(false, s.Length,
            TrimOrNull(s.GetString(s.ByteAt(0x04))),
            TrimOrNull(s.GetString(s.ByteAt(0x05))),
            s.ByteAt(0x06),
            TrimOrNull(s.GetString(s.ByteAt(0x07))),
            TrimOrNull(s.GetString(s.ByteAt(0x09))),
            TrimOrNull(s.GetString(s.ByteAt(0x0A))),
            TrimOrNull(s.GetString(s.ByteAt(0x0B))),
            imageSize, characteristics, state, handles);
    }

    private static string? TrimOrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
