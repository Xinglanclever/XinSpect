using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// 機器識別與資產的<b>讀取</b>層：把 SMBIOS 的識別欄位收成 <see cref="AssetRecord"/>。
/// </summary>
/// <remarks>
/// 全部唯讀、usermode 零特權。三個來源：<c>Win32_ComputerSystemProduct</c>（Type 1）、
/// <c>Win32_BaseBoard</c>（Type 2）、<c>Win32_SystemEnclosure</c>（Type 3）。
/// 「是不是預設字串」的判定沿用 <see cref="SmbiosService.IsPlaceholder"/> 的同一份清單——
/// 兩處各寫一份遲早會漂移。
/// </remarks>
public static class AssetFactsService
{
    private const string Category = "識別與資產";

    /// <summary>收集識別事實。測試以注入記錄取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<AssetRecord>? probe = null)
    {
        AssetRecord record;
        try { record = (probe ?? Read)(); }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact("asset.identify", Category, "機器識別", "", "",
                    "SMBIOS Type 1／2／3", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "讀取失敗：" + ex.Message),
            ];
        }

        var v = AssetJudge.Judge(record);
        var list = new List<HardwareFact>
        {
            new("asset.identify", Category, "機器識別", v.Headline, "",
                "SMBIOS Type 1／2／3 的識別欄位", FactTrustLevel.Measured, false, at, v.UsableFields,
                v.CanIdentify ? FactAvailability.Present : FactAvailability.NotApplicable,
                v.CanIdentify ? null : "所有識別欄位都是空值或韌體預設字串——本機無法用這些欄位唯一識別"),
            new("asset.identify.evidence", Category, "機器識別依據", v.Evidence, "",
                "SMBIOS Type 1／2／3", FactTrustLevel.Derived, false, at, null, FactAvailability.Present),
        };

        foreach (var f in record.Fields)
        {
            bool usable = f.Value.Length > 0 && !f.IsPlaceholder;
            list.Add(new HardwareFact($"asset.field.{f.Label}", Category, f.Label,
                f.Value.Length > 0 ? f.Value + SmbiosService.PlaceholderNote(f.Value) : "—（讀不到）",
                "", f.Source, FactTrustLevel.Reported, false, at, null,
                usable ? FactAvailability.Present : FactAvailability.NotApplicable,
                usable ? null : "韌體未填或讀不到——請勿當序號使用"));
        }

        if (record.ChassisTypeName.Length > 0)
            list.Add(new HardwareFact("asset.chassis", Category, "機箱類型",
                record.ChassisTypeName, "", "SMBIOS Type 3 位移 0x05",
                FactTrustLevel.Reported, false, at, record.ChassisTypeCode));

        if (record.Uuid.Length > 0)
            list.Add(new HardwareFact("asset.uuid", Category, "系統 UUID", record.Uuid, "",
                "SMBIOS Type 1 位移 0x04–0x13", FactTrustLevel.Reported, false, at, null));

        return list;
    }

    /// <summary>WMI 通路：讀三份資料湊成一組識別記錄。個別欄位缺漏以空字串表達。</summary>
    internal static AssetRecord Read()
    {
        var fields = new List<AssetField>();
        byte chassisCode = 0;
        string chassisName = "";
        string uuid = "";

        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Vendor, Name, Version, IdentifyingNumber, SKUNumber, UUID FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    Add(fields, "系統製造商", Str(o, "Vendor"), "SMBIOS Type 1 位移 0x04");
                    Add(fields, "系統型號", Str(o, "Name"), "SMBIOS Type 1 位移 0x05");
                    Add(fields, "系統序號", Str(o, "IdentifyingNumber"), "SMBIOS Type 1 位移 0x07");
                    Add(fields, "SKU", Str(o, "SKUNumber"), "SMBIOS Type 1 位移 0x19");
                    uuid = Str(o, "UUID");
                    break;
                }
        }
        catch (Exception ex) { Diag.Swallow("系統識別查詢", ex, "系統識別欄位讀不到"); }

        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    Add(fields, "主機板製造商", Str(o, "Manufacturer"), "SMBIOS Type 2 位移 0x04");
                    Add(fields, "主機板型號", Str(o, "Product"), "SMBIOS Type 2 位移 0x05");
                    Add(fields, "主機板序號", Str(o, "SerialNumber"), "SMBIOS Type 2 位移 0x07");
                    break;
                }
        }
        catch (Exception ex) { Diag.Swallow("主機板識別查詢", ex, "主機板識別欄位讀不到"); }

        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Manufacturer, ChassisTypes, SerialNumber, SMBIOSAssetTag FROM Win32_SystemEnclosure");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    Add(fields, "機箱製造商", Str(o, "Manufacturer"), "SMBIOS Type 3 位移 0x04");
                    Add(fields, "機箱序號", Str(o, "SerialNumber"), "SMBIOS Type 3 位移 0x07");
                    Add(fields, "資產標籤", Str(o, "SMBIOSAssetTag"), "SMBIOS Type 3 位移 0x08");
                    if (o["ChassisTypes"] is Array types && types.Length > 0)
                    {
                        chassisCode = Convert.ToByte(types.GetValue(0) is { } first ? first : 0);
                        chassisName = SmbiosService.ChassisTypeName((byte)(chassisCode & 0x7F));
                    }
                    break;
                }
        }
        catch (Exception ex) { Diag.Swallow("機箱識別查詢", ex, "機箱識別欄位讀不到"); }

        return new AssetRecord(fields, chassisCode, chassisName, uuid,
                               AssetJudge.IsServerChassisType((byte)(chassisCode & 0x7F)));
    }

    private static void Add(List<AssetField> list, string label, string value, string source)
        => list.Add(new AssetField(label, value, source, SmbiosService.IsPlaceholder(value)));

    private static string Str(ManagementObject o, string prop) => o[prop] as string ?? "";
}
