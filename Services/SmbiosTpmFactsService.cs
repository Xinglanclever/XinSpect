using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// SMBIOS Type 43（TPM 裝置）的事實產出（ULTIMATE-1000 階段二 P2-029）。
///
/// <para>
/// <b>為什麼它與既有的 <c>tpm.*</c> 是兩件事：</b>
/// 既有的 TPM 事實走 TBS API（<c>Tbsi_Context_Create</c>）——那是<b>跟 TPM 講話</b>，
/// 需要裝置回應。Type 43 是<b>問韌體</b>：表就在那裡，不需要裝置回應就能讀到
/// 「這裡有一個 TPM、它是 2.0 版、目前處於某種狀態」。
/// </para>
///
/// <para>
/// <b>兩者不一致的每一種組合都有意義：</b>
/// 韌體說有而 TBS 讀不到，代表它被停用或有驅動問題；
/// TBS 讀得到而表裡沒列，代表有 TPM 但韌體沒宣告（虛擬機常見）；
/// 兩者都說有但版本不同，那是<b>具體的對帳落差，不是「哪一個錯了」</b>。
/// </para>
///
/// <para>
/// <b>界線：這一項只陳述韌體在表裡寫了什麼。</b>
/// 它不判斷 TPM 是否可用、是否已啟用、是否值得信任——那些要看
/// TBS 的回應與 <c>tpm.*</c> 家族。本服務的責任是把「韌體那一邊的說法」
/// 單獨取出來，讓對帳有兩個獨立的端點。
/// </para>
/// </summary>
public static class SmbiosTpmFactsService
{
    private const string Category = "SMBIOS";
    private const string Source = "SMBIOS Type 43";

    public const string SummaryKey = "sb.tpmdev";
    public const string VendorKey = "sb.tpmdev.vendor";
    public const string SpecVersionKey = "sb.tpmdev.spec";
    public const string StateKey = "sb.tpmdev.state";

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

        var tpm = structs.FirstOrDefault(s => s.Type == 43);
        if (tpm is null)
            return Na(at,
                "SMBIOS 未列 Type 43——韌體沒有宣告 TPM 裝置。這不代表沒有 TPM："
                + "只代表這一個來源沒有陳述（虛擬機與部分筆電不填這一項）",
                FactAvailability.NotApplicable);

        // 最短需要到 0x1F（特性旗標 @0x1A 是 u64 的最後一位元組是 0x21，
        // 而我們只讀 bit2/3/4，所以只要 @0x1A 有效即可；保守取到 0x1B）。
        if (tpm.Length < 0x1B)
            return Na(at,
                $"Type 43 長度 {tpm.Length} bytes，短於本解碼器所需的最小 0x1B——如實拒解，不猜",
                FactAvailability.ReadError);

        // Vendor ID：4 個位元組的整數，高位在前（規格：VendorID 是 u32，值為四個 ASCII）。
        uint vendorId = tpm.DwordAt(0x04);
        string vendor = new([(char)((vendorId >> 24) & 0xFF), (char)((vendorId >> 16) & 0xFF),
            (char)((vendorId >> 8) & 0xFF), (char)(vendorId & 0xFF)]);
        vendor = new string(vendor.Where(c => c >= 0x20 && c < 0x7F).ToArray()).Trim();

        byte major = tpm.ByteAt(0x08);
        byte minor = tpm.ByteAt(0x09);
        ulong firmware = tpm.DwordAt(0x0A) | ((ulong)tpm.DwordAt(0x0E) << 32);
        ulong characteristics = tpm.DwordAt(0x16) | ((ulong)tpm.ByteAt(0x1A) << 32);

        bool enabled = (characteristics & 0x04) != 0;
        bool owned = (characteristics & 0x08) != 0;
        bool supportsTpm2 = (characteristics & 0x10) != 0;

        string? description = tpm.Length > 0x1C && tpm.ByteAt(0x1B) > 0
            ? tpm.GetString(tpm.ByteAt(0x1B))
            : null;
        if (description is not null && SmbiosService.IsPlaceholder(description)) description = null;

        var facts = new List<HardwareFact>
        {
            new(SummaryKey, Category, "TPM 裝置（韌體宣告）",
                $"規格 {major}.{minor}；廠商 {vendor}；韌體版本 0x{firmware:X}"
                + (description is null ? "" : $"；{description}"), "",
                Source + "（廠商 ID@0x04、主要／次要規格版本@0x08／@0x09、"
                + "韌體版本@0x0A、特性@0x16）",
                FactTrustLevel.Measured, false, at, supportsTpm2 ? 2 : 1, FactAvailability.Present,
                "這是韌體宣告，不是 TPM 的回應——與 tpm.present／tpm.version 的差異"
                + "是兩個獨立來源並列的事實，不是哪一個錯了"),

            new(VendorKey, Category, "TPM 廠商（韌體宣告）", vendor.Length > 0 ? vendor : "",
                "", Source + " Vendor ID@0x04", FactTrustLevel.Measured, false, at, null,
                vendor.Length > 0 ? FactAvailability.Present : FactAvailability.Unknown,
                vendor.Length > 0 ? null : "廠商 ID 不是可列印的 ASCII——如實陳述，不代為解讀"),

            new(SpecVersionKey, Category, "TPM 規格版本（韌體宣告）",
                $"{major}.{minor}", "", Source + " @0x08／@0x09",
                FactTrustLevel.Measured, false, at, major,
                major >= 2 ? FactAvailability.Present : FactAvailability.Unknown,
                major >= 2 ? null
                    : "韌體宣告的規格版本低於 2.0——這與 tpm.version 是兩個獨立來源，"
                      + "兩者不同時應以實際 TPM 的回應為準，但差異本身要留下來"),

            new(StateKey, Category, "TPM 狀態（韌體宣告）",
                $"{(enabled ? "已啟用" : "未啟用")}／{(owned ? "已佔有" : "未佔有")}／"
                + $"{(supportsTpm2 ? "支援 TPM 2.0 協定" : "未宣告支援 TPM 2.0 協定")}",
                "", Source + " 特性位元（bit2 啟用、bit3 佔有、bit4 支援 2.0）",
                FactTrustLevel.Measured, false, at, null, FactAvailability.Present,
                "「未啟用」與「未佔有」都是韌體自述——實際能不能用要看 TBS 的回應；"
                + "「未佔有」尤其不代表不能用：它只表示還沒有擁有者"),
        };

        return facts;
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [SummaryKey, VendorKey, SpecVersionKey, StateKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
