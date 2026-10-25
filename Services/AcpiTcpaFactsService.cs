using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>
/// TCPA 表的事實（U10-AC-0042）：簽章「TCPA」的 ACPI 表——TPM 1.2 時代的
/// 信任線索表（事件日誌位置），與 TPM2 表<b>並存或前後代替</b>。
///
/// <para>
/// <b>出處的誠實聲明：</b>TCPA 表的原典是 TCG 的
/// <b>TPM 1.2 PC Specific Implementation Specification</b>（及其後繼 TCG ACPI Specification）——
/// <b>本機查不到該原文</b>，所以本服務只陳述<b>有對照來源的欄位</b>：
/// 佈局對照的是 Linux <c>include/acpi/actbl3.h</c> 的
/// <c>acpi_table_tcpa_hdr</c>／<c>acpi_table_tcpa_client</c>／<c>acpi_table_tcpa_server</c>
/// （packed，與 <c>FirmwareUpdatableFactsService</c> 用本機 SDK 標頭當對照來源是同一種做法）：
/// </para>
/// <list type="bullet">
/// <item>表頭 36 bytes（標準 ACPI 表頭）＋ <c>platform_class</c> u16 ＠0x24
///   （0＝client、1＝server；Linux 的 ACPI_TCPA_CLIENT_TABLE／SERVER_TABLE）。</item>
/// <item><b>client</b>：事件日誌最小長度 u32 ＠0x26、事件日誌位址 u64 ＠0x2A（表長至少 50 bytes）。</item>
/// <item><b>server</b>：保留 u16 ＠0x26、事件日誌最小長度 u64 ＠0x28、事件日誌位址 u64 ＠0x30、
///   規格版號 u16 ＠0x38（表長至少 100 bytes）——<b>其餘欄位（旗標／中斷／PCI 位置）未成文不展開</b>：
///   本機沒有原文，硬解就是編值。</item>
/// </list>
///
/// <para>
/// <b>界線：</b>這張表講的是「TPM 1.2 的事件日誌放在哪」，<b>不判斷 TPM 是否存在、
/// 也不讀事件日誌內容</b>（log 內容屬 TCG log 那批鍵，且 TBS 的 log 通路是另一個來源）。
/// 本機沒有 TCPA 表＝NotApplicable——那是環境事實（現代平台通常只留 TPM2 表），
/// 與「沒有 TPM」是不同的斷言。
/// </para>
/// </summary>
public static class AcpiTcpaFactsService
{
    private const string Category = "ACPI";
    private const string Source = "ACPI 表（GetSystemFirmwareTable，唯讀）";

    public const string TcpaKey = "ac.tcpa";
    public const string TcpaCrossKey = "ac.tcpa.cross";

    /// <summary>表頭（36）＋ platform_class（2）：判斷 client／server 與後續佈局的最低需求。</summary>
    private const int MinHeaderLength = 38;
    private const int ClientMinLength = 50;   // 38 + u32 最小長度 + u64 位址
    private const int ServerMinLength = 100;  // 38 + u64 + u64 + u16（server 結構其餘未展開）

    /// <summary>讀表（TCPA＋TPM2，給 ac.tcpa.cross 用）。每張各自 try/catch：一張讀不到不連坐。</summary>
    public static IReadOnlyDictionary<string, byte[]> ReadTables(IAcpiTableSource source)
    {
        var wanted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var bytes in source.ReadAll())
            {
                if (bytes.Length < 4) continue;
                string sig = System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
                if (sig == "TCPA" || sig == "TPM2") wanted[sig] = bytes;
            }
        }
        catch (Exception ex)
        {
            Diag.Swallow("TCPA／TPM2 表讀取", ex, "兩張表都讀不到");
        }
        return wanted;
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyDictionary<string, byte[]>? tables = null)
    {
        if (tables is null)
        {
            string reason = "沒有 ACPI 表可讀（GetSystemFirmwareTable 未回表）";
            return
            [
                Fact(at, TcpaKey, FactAvailability.NotSupported, reason),
                Fact(at, TcpaCrossKey, FactAvailability.NotSupported, reason),
            ];
        }

        tables.TryGetValue("TCPA", out var tcpa);
        tables.TryGetValue("TPM2", out var tpm2);
        return [Tcpa(at, tcpa), Cross(at, tcpa, tpm2)];
    }

    private static HardwareFact Tcpa(DateTimeOffset at, byte[]? t)
    {
        if (t is null)
            return Fact(at, TcpaKey, FactAvailability.NotApplicable,
                "沒有 TCPA 表——**「韌體沒有留下 TPM 1.2 時代的信任線索表」與「沒有 TPM」"
                + "是不同的斷言**：現代平台通常只留 TPM2 表，TPM 的存在性由 tpm.*／sb.tpmdev 各自陳述");

        if (t.Length < MinHeaderLength)
            return Fact(at, TcpaKey, FactAvailability.ReadError,
                $"TCPA 表只有 {t.Length} bytes，短於表頭＋platform_class 所需的 {MinHeaderLength}——如實拒解，不猜");

        ushort platformClass = (ushort)(t[36] | (t[37] << 8));
        string className = platformClass switch
        {
            0 => "client（0）",
            1 => "server（1）",
            _ => $"未收錄的類別碼 {platformClass}（原始值如實列出）",
        };

        // 欄位佈局依類別而異（Linux actbl3.h 的 tcpa_client／tcpa_server）；server 的
        // 最小長度按「已展開欄位」算到規格版號為止，其餘未成文不展開。
        int minLen = platformClass == 1 ? ServerMinLength : ClientMinLength;
        if (t.Length < minLen)
            return Fact(at, TcpaKey, FactAvailability.ReadError,
                $"TCPA 表（{className}）只有 {t.Length} bytes，短於已對照欄位所需的 {minLen}——如實拒解，不猜");

        ulong logLength;
        ulong logAddress;
        string layoutNote;
        if (platformClass == 1)
        {
            logLength = U64(t, 0x28);
            logAddress = U64(t, 0x30);
            layoutNote = "server 佈局（最小長度@0x28、位址@0x30、規格版號@0x38）；"
                + "server 表的旗標／中斷／PCI 位置等其餘欄位**未成文，不展開**";
        }
        else
        {
            logLength = (uint)(t[0x26] | (t[0x27] << 8) | (t[0x28] << 16) | (t[0x29] << 24));
            logAddress = U64(t, 0x2A);
            layoutNote = "client 佈局（最小長度@0x26、位址@0x2A）";
        }

        return new HardwareFact(TcpaKey, Category, "TCPA 表（TPM 1.2 信任線索）",
            $"存在：platform class＝{className}；事件日誌最小長度 {logLength} bytes、"
            + $"事件日誌位址 0x{logAddress:X}（{layoutNote}）", "bytes",
            Source + "；佈局對照 Linux include/acpi/actbl3.h（原典 TCG TPM 1.2 PC Specific "
            + "Implementation Specification 本機查不到原文，未成文的欄位不解）",
            FactTrustLevel.Measured, false, at, (double)logLength);
    }

    private static HardwareFact Cross(DateTimeOffset at, byte[]? tcpa, byte[]? tpm2)
    {
        if (tcpa is null)
            return Fact(at, TcpaCrossKey, FactAvailability.NotApplicable,
                "沒有 TCPA 表，兩代信任線索表無並存可言；TPM2 表"
                + (tpm2 is null ? "**也不在**——兩代線索都沒有留下（這本身是如實的環境事實）"
                                : "在——只有 TPM2 是現代平台的正常樣貌"));

        string both = tpm2 is not null
            ? "**兩代 TPM 信任線索表並存**：TCPA（TPM 1.2 時代的事件日誌線索）與 TPM2 "
              + "（TPM 2.0 的控制區域描述）都在 ACPI 表裡——兩個來源各自陳述各自的事，"
              + "不互斥、也不仲裁誰代表「現在的 TPM」"
            : "只留下 TCPA（TPM 1.2 時代），沒有 TPM2 表——兩代線索只存其一；"
              + "TPM 2.0 的可用性由此不代表，應看 tpm.* 與 sb.tpmdev";

        return new HardwareFact(TcpaCrossKey, Category, "TCPA × TPM2 兩代信任線索表對照",
            both + "。對照只陳述「哪幾代表在」，不判斷 TPM 的存在與可用"
            + "（那是 tpm.*／sb.tpmdev 的事）", "",
            Source + "（TCPA／TPM2 表的存在性對照）", FactTrustLevel.Derived, false, at,
            tpm2 is not null ? 1.0 : 0.0);
    }

    private static ulong U64(byte[] t, int off)
    {
        ulong v = 0;
        for (int k = 0; k < 8; k++) v |= (ulong)t[off + k] << (8 * k);
        return v;
    }

    private static HardwareFact Fact(DateTimeOffset at, string key,
        FactAvailability availability, string reason) =>
        new(key, Category, key, "", "", Source, FactTrustLevel.Unknown, false, at, null,
            availability, reason);
}
