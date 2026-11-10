using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// SMBIOS Type 39（電源供應器）與 Type 41（內建裝置）的事實產出
/// （ULTIMATE-1000 階段二 P2-026／P2-027）。
///
/// <para>
/// <b>P2-026 是「既有實作的家族化」：</b><see cref="SmbiosDeepFactsService"/> 已經有
/// <c>sb.psu</c> 這一項（單一鍵），而規格要的是 <c>sb.psu.*</c> 家族。
/// 本服務把它的欄位拆成可查詢的個別鍵，<b>但不重做那個既有的鍵</b>——
/// 鍵是已發佈的契約，改名會破壞既有使用者的匯出與查詢。
/// </para>
///
/// <para>
/// <b>界線：Type 39 是「電源供應器自己在說什麼」，不是量測。</b>
/// 一個 PSU 宣告「1000 W」代表它的額定，不代表現在的輸出；
/// 「已插電」是旗標，不是瓦數。本服務只陳述欄位內容，
/// 不換算、不推估、不判斷夠不夠用。
/// </para>
/// </summary>
public static class SmbiosPowerFactsService
{
    private const string Category = "SMBIOS";

    public const string PsuCountKey = "sb.psu.count";
    public const string PsuDetailKey = "sb.psu.detail";
    public const string PsuPowerKey = "sb.psu.power";
    public const string OnboardKey = "sb.onboard";
    public const string OnboardCountKey = "sb.onboard.count";

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
        var psus = byType.TryGetValue(39, out var s39) ? s39.Where(s => s.Length >= 0x10).ToList() : [];
        var onboard = byType.TryGetValue(41, out var s41) ? s41.Where(s => s.Length >= 0x0B).ToList() : [];

        return
        [
            PsuCount(at, psus),
            PsuDetail(at, psus),
            PsuPower(at, psus),
            OnboardCount(at, onboard),
            OnboardDetail(at, onboard),
        ];
    }

    // ── Type 39 ─────────────────────────────────────────────────────────

    private static HardwareFact PsuCount(DateTimeOffset at, IReadOnlyList<SmbiosStruct> psus)
    {
        if (psus.Count == 0)
            return new HardwareFact(PsuCountKey, Category, "SMBIOS 宣告的電源供應器數",
                "", "個", "SMBIOS Type 39", FactTrustLevel.Measured, false, at, 0,
                FactAvailability.NotApplicable,
                "SMBIOS 未列 Type 39——多半只有整機廠填，DIY 與伺服器常缺；"
                + "缺席不代表沒有電源供應器");

        return new HardwareFact(PsuCountKey, Category, "SMBIOS 宣告的電源供應器數",
            psus.Count.ToString(CultureInfo.InvariantCulture), "個", "SMBIOS Type 39",
            FactTrustLevel.Measured, false, at, psus.Count, FactAvailability.Present,
            "這一項與既有的 sb.psu 是同一個結構的另一種切法——"
            + "sb.psu 是給人看的摘要，這幾把鍵是給查詢與對帳用的個別欄位");
    }

    private static HardwareFact PsuDetail(DateTimeOffset at, IReadOnlyList<SmbiosStruct> psus)
    {
        if (psus.Count == 0)
            return Missing(PsuDetailKey, "電源供應器明細", at, "SMBIOS 未列 Type 39");

        var parts = psus.Select((s, i) =>
        {
            var fields = new List<string> { $"[{i}]" };
            byte group = s.ByteAt(0x04);
            string location = Str(s, 0x05);
            string device = Str(s, 0x06);
            string manufacturer = Str(s, 0x07);
            string revision = Str(s, 0x0B);

            if (device.Length > 0) fields.Add(device);
            if (location.Length > 0) fields.Add(location);
            if (group > 0) fields.Add($"群組 {group}");
            if (manufacturer.Length > 0) fields.Add($"製造商 {manufacturer}");
            if (revision.Length > 0) fields.Add($"版本 {revision}");

            ushort chars = s.WordAt(0x0E);
            ushort power = s.WordAt(0x0C);
            fields.Add(power == 0x8000 ? "功率未回報" : $"{power} W");
            fields.Add((chars & 0x0002) != 0 ? "在位" : "不在位");
            fields.Add((chars & 0x0004) == 0 ? "已插電" : "未插電");
            if ((chars & 0x0001) != 0) fields.Add("有熱插拔");
            fields.Add("輸入電壓切換：" + (((chars >> 3) & 0x0F) switch
            {
                1 => "其他", 2 => "未知", 3 => "手動", 4 => "自動",
                5 => "寬範圍", 6 => "不適用", var code => $"代碼 {code}",
            }));
            return string.Join("／", fields);
        });

        return new HardwareFact(PsuDetailKey, Category, "電源供應器明細",
            string.Join("；", parts), "", "SMBIOS Type 39（序號依 L1 原則不列入）",
            FactTrustLevel.Measured, false, at, psus.Count, FactAvailability.Present,
            "這些是電源供應器自己的宣告——額定瓦數不等於現在輸出，"
            + "「已插電」是旗標不是功率");
    }

    private static HardwareFact PsuPower(DateTimeOffset at, IReadOnlyList<SmbiosStruct> psus)
    {
        var present = psus.Where(s => (s.WordAt(0x0E) & 0x0002) != 0).ToList();
        var reported = present.Where(s => s.WordAt(0x0C) != 0x8000).ToList();
        if (present.Count == 0)
            return new HardwareFact(PsuPowerKey, Category, "電源供應器額定功率",
                "", "W", "SMBIOS Type 39 @0x0C", FactTrustLevel.Measured, false, at, null,
                FactAvailability.NotApplicable,
                psus.Count == 0
                    ? "SMBIOS 未列 Type 39"
                    : "SMBIOS 未宣告任何電源供應器在位；空槽不計入額定功率");

        if (reported.Count != present.Count)
            return new HardwareFact(PsuPowerKey, Category, "電源供應器額定功率",
                $"{present.Count} 個在位供應器中只有 {reported.Count} 個回報功率，無法確認合計；未回報的不以 0 計入",
                "W", "SMBIOS Type 39 @0x0C", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "0x8000 代表未回報——那是「沒說」而不是「0 瓦」");

        long total = reported.Sum(s => (long)s.WordAt(0x0C));
        string detail = string.Join("＋", reported.Select(s => $"{s.WordAt(0x0C)} W"));
        return new HardwareFact(PsuPowerKey, Category, "電源供應器額定功率",
            $"{detail}（合計 {total} W）", "W", "SMBIOS Type 39 @0x0C",
            FactTrustLevel.Derived, false, at, total, FactAvailability.Present,
            "這是在位供應器的額定功率總和，**不是可用功率**——轉換效率、各路輸出上限、"
            + "以及同時啟動的峰值都不在這個數字裡");
    }

    // ── Type 41 ─────────────────────────────────────────────────────────

    private static HardwareFact OnboardCount(DateTimeOffset at, IReadOnlyList<SmbiosStruct> onboard)
    {
        if (onboard.Count == 0)
            return Missing(OnboardCountKey, "SMBIOS 宣告的內建裝置數", at,
                "SMBIOS 未列 Type 41——多數消費級主機板不填這一項"
                + "（它是給伺服器與嵌入式平台描述「焊在板上的裝置」用的）");

        return new HardwareFact(OnboardCountKey, Category, "SMBIOS 宣告的內建裝置數",
            onboard.Count.ToString(CultureInfo.InvariantCulture), "個", "SMBIOS Type 41",
            FactTrustLevel.Measured, false, at, onboard.Count, FactAvailability.Present,
            "內建裝置是「焊在板上的」而不是插上去的——這一項只在有 Type 41 的平台有意義");
    }

    private static HardwareFact OnboardDetail(DateTimeOffset at, IReadOnlyList<SmbiosStruct> onboard)
    {
        if (onboard.Count == 0)
            return Missing(OnboardKey, "內建裝置明細", at, "SMBIOS 未列 Type 41");

        var parts = new List<string>();
        for (int i = 0; i < onboard.Count; i++)
        {
            var s = onboard[i];
            var fields = new List<string> { $"[{i}] {DeviceTypeName((byte)(s.ByteAt(0x05) & 0x7F))}" };
            string designation = Str(s, 0x04);
            if (designation.Length > 0) fields.Add(designation);
            if (s.ByteAt(0x06) > 0) fields.Add($"實例 {s.ByteAt(0x06)}");
            ushort segment = s.WordAt(0x07);
            byte bus = s.ByteAt(0x09), devFunc = s.ByteAt(0x0A);
            fields.Add(segment == 0xFFFF || bus == 0xFF || devFunc == 0xFF
                ? "PCI 位址未提供"
                : $"PCI {segment:X4}:{bus:X2}:{devFunc >> 3:X2}.{devFunc & 7}");

            string status = OnboardStatus(s);
            if (status.Length > 0) fields.Add(status);

            parts.Add(string.Join("／", fields));
        }

        return new HardwareFact(OnboardKey, Category, "內建裝置明細",
            string.Join("；", parts), "", "SMBIOS Type 41（參考名稱@0x04、型別與啟用位@0x05、"
            + "實例@0x06、PCI 段@0x07、匯流排@0x09、裝置／功能@0x0A）",
            FactTrustLevel.Measured, false, at, onboard.Count, FactAvailability.Present,
            "這是韌體宣告的裝置型別、啟用旗標與 PCI 位址，不代表 OS 驅動已正常啟動");
    }

    /// <summary>
    /// Type 41 的裝置型別低七位與啟用旗標共用 @0x05。
    /// </summary>
    private static string OnboardStatus(SmbiosStruct s)
    {
        if (s.Length < 0x06) return "";
        byte status = s.ByteAt(0x05);
        string state = (status & 0x80) != 0 ? "已啟用" : "已停用";
        return $"{state}（0x{status:X2}）";
    }

    private static string DeviceTypeName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "顯示", 0x04 => "SCSI 控制器",
        0x05 => "乙太網路", 0x06 => "Token Ring", 0x07 => "音效",
        0x08 => "PATA 控制器", 0x09 => "SATA 控制器", 0x0A => "SAS 控制器",
        0x0B => "無線網路", 0x0C => "藍牙", 0x0D => "WWAN",
        0x0E => "eMMC", 0x0F => "NVM Express", 0x10 => "UFS 控制器",
        _ => $"型別 0x{code:X2}",
    };

    // ── 共用 ────────────────────────────────────────────────────────────

    private static string Str(SmbiosStruct s, int offset)
    {
        if (offset >= s.Length) return "";
        string? v = s.GetString(s.ByteAt(offset));
        return v is null || v.Length == 0 || SmbiosService.IsPlaceholder(v) ? "" : v;
    }

    private static HardwareFact Missing(string key, string name, DateTimeOffset at, string reason) =>
        new(key, Category, name, "", "", "SMBIOS", FactTrustLevel.Unknown, false, at,
            null, FactAvailability.NotApplicable, reason);

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [PsuCountKey, PsuDetailKey, PsuPowerKey, OnboardCountKey, OnboardKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", "SMBIOS",
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
