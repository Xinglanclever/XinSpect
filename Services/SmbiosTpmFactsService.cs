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
/// TPM 廠商、規格版本、韌體版本與家族配置特性。
/// </para>
///
/// <para>
/// <b>兩者不一致的每一種組合都有意義：</b>
/// 韌體說有而 TBS 讀不到，可能涉及停用、驅動或存取權限；
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

        // Description@0x12、Characteristics QWORD@0x13，最後一個位元組是 0x1A。
        if (tpm.Length < 0x1B)
            return Na(at,
                $"Type 43 長度 {tpm.Length} bytes，短於本解碼器所需的最小 0x1B——如實拒解，不猜",
                FactAvailability.ReadError);

        string vendor = System.Text.Encoding.ASCII.GetString(tpm.Data, 0x04, 4).TrimEnd('\0', ' ');
        if (tpm.Data.AsSpan(0x04, vendor.Length).ContainsAnyExceptInRange((byte)0x20, (byte)0x7E))
            vendor = "";

        byte major = tpm.ByteAt(0x08);
        byte minor = tpm.ByteAt(0x09);
        bool knownVersion = (major, minor) is (1, 2) or (2, 0);
        uint firmware1 = tpm.DwordAt(0x0A), firmware2 = tpm.DwordAt(0x0E);
        ulong characteristics = BitConverter.ToUInt64(tpm.Data, 0x13);
        var features = new List<string>();
        if ((characteristics & 0x04) != 0)
            features.Add("TPM 裝置特性未提供");
        else
        {
            if ((characteristics & 0x08) != 0) features.Add("TPM 家族可透過韌體更新配置");
            if ((characteristics & 0x10) != 0) features.Add("TPM 家族可透過平台軟體配置");
            if ((characteristics & 0x20) != 0) features.Add("TPM 家族可透過 OEM 專有機制配置");
            if (features.Count == 0) features.Add("未宣告家族配置特性");
        }

        string? description = tpm.ByteAt(0x12) > 0
            ? tpm.GetString(tpm.ByteAt(0x12))
            : null;
        if (description is not null && SmbiosService.IsPlaceholder(description)) description = null;

        var facts = new List<HardwareFact>
        {
            new(SummaryKey, Category, "TPM 裝置（韌體宣告）",
                $"規格 {major}.{minor}；廠商 {vendor}；韌體版本1 0x{firmware1:X8}／版本2 0x{firmware2:X8}"
                + (description is null ? "" : $"；{description}"), "",
                Source + "（廠商 ID@0x04、主要／次要規格版本@0x08／@0x09、"
                + "韌體版本@0x0A／0x0E、描述@0x12、特性@0x13）",
                FactTrustLevel.Reported, false, at, null, FactAvailability.Present,
                "這是韌體宣告，不是 TPM 的回應——與 tpm.present／tpm.version 的差異"
                + "是兩個獨立來源並列的事實，不是哪一個錯了"),

            new(VendorKey, Category, "TPM 廠商（韌體宣告）", vendor.Length > 0 ? vendor : "",
                "", Source + " Vendor ID@0x04（4 bytes ASCII）", FactTrustLevel.Reported, false, at, null,
                vendor.Length > 0 ? FactAvailability.Present : FactAvailability.Unknown,
                vendor.Length > 0 ? null : "廠商 ID 不是可列印的 ASCII——如實陳述，不代為解讀"),

            new(SpecVersionKey, Category, "TPM 規格版本（韌體宣告）",
                $"{major}.{minor}", "", Source + " @0x08／@0x09",
                FactTrustLevel.Reported, false, at,
                knownVersion ? major : null,
                knownVersion ? FactAvailability.Present : FactAvailability.Unknown,
                knownVersion ? null
                    : "韌體宣告的規格版本未收錄；不從特性旗標推測版本"),

            new(StateKey, Category, "TPM 特性（韌體宣告）",
                string.Join("／", features) + $"（原始特性 0x{characteristics:X16}）",
                "", Source + " Characteristics QWORD@0x13（bit2 未提供；bits3–5 家族配置方式）",
                FactTrustLevel.Reported, false, at, null, FactAvailability.Present,
                "Type 43 特性不描述啟用或擁有者狀態；實際狀態需另讀 TPM／TBS 來源"),
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
