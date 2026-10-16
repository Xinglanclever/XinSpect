using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// TPM2 表與 WPBT 表的事實產出（ULTIMATE-1000 階段二 P2-017）。
///
/// <para>
/// <b>這兩張表放一起是因為它們都是「韌體告訴 OS 去哪裡找東西」的表：</b>
/// </para>
/// <list type="bullet">
///   <item><b>TPM2</b>——韌體描述 TPM 的<b>位置與啟動方式</b>
///     （控制區域位址、起始方法、平台類別）。這是「怎麼跟它講話」的權威陳述。</item>
///   <item><b>WPBT</b>——Windows Platform Binary Table，韌體要求 OS
///     <b>在開機時執行一段它提供的程式</b>。這是<b>安全性上值得注意的一件事</b>，
///     而它只需要「存在與否」就足以回答最重要的問題。</item>
/// </list>
///
/// <para>
/// <b>界線，分兩句：</b>
/// </para>
/// <list type="number">
///   <item>TPM2 表<b>只描述位置</b>，不判斷 TPM 是否可用——那是 <c>tpm.*</c>
///     與 <c>sb.tpmdev</c> 的事。三者的差異是三個獨立來源並列的事實。</item>
///   <item>WPBT 的內容是一段可執行映像，而<b>本服務不解析它</b>——
///     只陳述「有這張表」與它的長度。要判斷那段程式做了什麼需要
///     檔案系統層的線索（那是另一件事）。</item>
/// </list>
/// </summary>
public static class AcpiTpm2WpbtFactsService
{
    private const string Category = "ACPI";
    private const string Source = "ACPI 表（GetSystemFirmwareTable，唯讀）";

    public const string Tpm2Key = "ac.tpm2";
    public const string Tpm2MethodKey = "ac.tpm2.method";
    public const string WpbtKey = "ac.wpbt";

    /// <summary>ACPI TPM2 表的固定部分長度（表頭 36 ＋ 平台類別 2 ＋ 保留 2 ＋ 位址 8 ＋ 長度 4）。</summary>
    private const int Tpm2MinLength = 52;

    /// <summary>TPM2 表的起始方法（Starts at @0x30，u32）。</summary>
    public static string StartMethodName(uint method) => method switch
    {
        0 => "未指定",
        1 => "記憶體映射（MMIO）",
        2 => "命令列（CMD）",
        3 => "韌體 TPM 介面（FIFO）",
        4 => "CRB（命令回應緩衝區）",
        5 => "FIFO ＋ CRB 兩者皆可",
        6 => "FIFO 優先、可退回 CRB",
        7 => "CRB 優先、可退回 FIFO",
        8 => "FIFO 8042（僅限 TPM 1.2）",
        9 => "CRB 搭配 ACPI 啟動",
        10 => "CRB 搭配 ACPI 啟動（無 8042）",
        11 => "FIFO 搭配 ACPI 啟動",
        12 => "FIFO 搭配 ACPI 啟動（無 8042）",
        13 => "MMIO 搭配 ACPI 啟動",
        14 => "MMIO 搭配 ACPI 啟動（無 8042）",
        15 => "CRB 搭配 5.0 版介面",
        _ => $"方法 {method}",
    };

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyDictionary<string, byte[]>? tables = null)
    {
        if (tables is null)
            return Na(at, "沒有 ACPI 表可讀（GetSystemFirmwareTable 未回表）",
                FactAvailability.NotSupported);

        byte[]? tpm2 = Find(tables, "TPM2");
        byte[]? wpbt = Find(tables, "WPBT");

        return
        [
            Tpm2(at, tpm2),
            Tpm2Method(at, tpm2),
            Wpbt(at, wpbt),
        ];
    }

    private static HardwareFact Tpm2(DateTimeOffset at, byte[]? t)
    {
        if (t is null)
            return new HardwareFact(Tpm2Key, Category, "TPM2 表", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有 TPM2 表——**「韌體沒有描述 TPM 的位置」與「沒有 TPM」是不同的斷言**："
                + "有些平台的 TPM 由 OS 透過其他途徑找到（例如直接探測常見的 MMIO 位址）");

        if (t.Length < Tpm2MinLength)
            return new HardwareFact(Tpm2Key, Category, "TPM2 表", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                $"TPM2 表只有 {t.Length} bytes，短於固定部分所需的 {Tpm2MinLength}"
                + "——如實拒解，不猜");

        ushort platformClass = (ushort)(t[36] | (t[37] << 8));
        ulong address = 0;
        for (int k = 0; k < 8; k++) address |= (ulong)t[40 + k] << (8 * k);
        uint length = (uint)(t[48] | (t[49] << 8) | (t[50] << 16) | (t[51] << 24));

        string className = platformClass switch
        {
            0 => "客戶端（Client）",
            1 => "伺服器（Server）",
            _ => $"類別 {platformClass}",
        };

        return new HardwareFact(Tpm2Key, Category, "TPM2 表",
            $"平台類別 {className}；控制區域 0x{address:X}，長度 {length} bytes", "",
            Source + "（平台類別@0x24、控制區域位址@0x28、長度@0x30）",
            FactTrustLevel.Measured, false, at, address, FactAvailability.Present,
            "**這張表只描述「TPM 在哪、怎麼跟它講話」**，不判斷它是否可用——"
            + "那是 tpm.*（問 TPM 本身）與 sb.tpmdev（問韌體在 SMBIOS 裡寫了什麼）的事。"
            + "**三者的差異是三個獨立來源並列的事實，不是哪一個錯了**");
    }

    private static HardwareFact Tpm2Method(DateTimeOffset at, byte[]? t)
    {
        if (t is null || t.Length < Tpm2MinLength)
            return new HardwareFact(Tpm2MethodKey, Category, "TPM2 起始方法", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                t is null ? "沒有 TPM2 表" : "TPM2 表長度不足");

        // 起始方法在 @0x30 之後——但本服務已用 @0x30 當控制區域長度。
        // 依規格：平台類別@0x24、保留@0x26、控制區域位址@0x28、長度@0x30、
        // 控制區域最後位址@0x34、起始方法@0x38（u32）。
        if (t.Length < 0x3C)
            return new HardwareFact(Tpm2MethodKey, Category, "TPM2 起始方法", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                $"TPM2 表只有 {t.Length} bytes，短於起始方法所在的 0x38——如實拒解");

        uint method = (uint)(t[0x38] | (t[0x39] << 8) | (t[0x3A] << 16) | (t[0x3B] << 24));
        return new HardwareFact(Tpm2MethodKey, Category, "TPM2 起始方法",
            StartMethodName(method), "", Source + " 起始方法@0x38",
            FactTrustLevel.Measured, false, at, method, FactAvailability.Present,
            "起始方法是**韌體宣告的溝通協定**——它決定 OS 要用哪一種介面去跟 TPM 講話；"
            + "協定宣告與實際可用是兩件事（實際可用要看 TBS 的回應）");
    }

    private static HardwareFact Wpbt(DateTimeOffset at, byte[]? t)
    {
        if (t is null)
            return new HardwareFact(WpbtKey, Category, "WPBT 表", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有 WPBT 表——**那是一件好事**：這張表的作用是要求 OS "
                + "在開機時執行韌體提供的一段程式");

        // WPBT 的最小長度：表頭 36 ＋ 內容長度 4 ＋ 內容位址 8。
        if (t.Length < 48)
            return new HardwareFact(WpbtKey, Category, "WPBT 表", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                $"WPBT 表只有 {t.Length} bytes，短於固定部分所需的 48——如實拒解");

        uint length = (uint)(t[36] | (t[37] << 8) | (t[38] << 16) | (t[39] << 24));
        ulong address = 0;
        for (int k = 0; k < 8; k++) address |= (ulong)t[40 + k] << (8 * k);

        return new HardwareFact(WpbtKey, Category, "WPBT 表",
            $"存在；映像位於 0x{address:X}，長度 {length} bytes", "", Source
            + "（內容長度@0x24、內容位址@0x28）",
            FactTrustLevel.Measured, true, at, length, FactAvailability.Present,
            "**這張表的作用是要求 OS 在開機時執行韌體提供的一段程式。**"
            + "它本身不是弱點（合法的用途包括防毒與管理代理），"
            + "但它是一條**由韌體發起、在使用者登入前執行的程式碼路徑**，"
            + "所以值得知道它存在。本服務**不解析那段映像的內容**——"
            + "要判斷它做了什麼需要檔案系統層的線索（那是另一件事）");
    }

    private static byte[]? Find(IReadOnlyDictionary<string, byte[]> tables, string signature)
        => tables.TryGetValue(signature, out var v) ? v : null;

    /// <summary>
    /// 從表來源取出需要的表。<b>只取這兩張</b>——不複製整份表列，
    /// 因為那會讓記憶體用量與表數成正比，而那些表已經由其他服務處理。
    /// <b>每一張各自 try/catch</b>：一張讀不到不該讓另一張也變成沒有。
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> ReadTables(IAcpiTableSource source)
    {
        var wanted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var bytes in source.ReadAll())
            {
                if (bytes.Length < 4) continue;
                string sig = System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
                if (sig == "TPM2" || sig == "WPBT") wanted[sig] = bytes;
            }
        }
        catch (Exception ex)
        {
            Diag.Swallow("TPM2／WPBT 表讀取", ex, "兩張表都讀不到");
        }
        return wanted;
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [Tpm2Key, Tpm2MethodKey, WpbtKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
