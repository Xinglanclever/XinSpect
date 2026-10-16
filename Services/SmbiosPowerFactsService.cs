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
            string group = Str(s, 0x04);
            string location = Str(s, 0x07);
            string device = Str(s, 0x06);
            string revision = Str(s, 0x0B);

            if (device.Length > 0) fields.Add(device);
            if (location.Length > 0) fields.Add(location);
            if (group.Length > 0) fields.Add($"群組 {group}");
            if (revision.Length > 0) fields.Add($"版本 {revision}");

            ushort chars = s.WordAt(0x0E);
            ushort power = s.WordAt(0x0C);
            fields.Add((power & 0x8000) != 0 ? "功率未回報" : $"{power} W");
            fields.Add((chars & 0x0002) != 0 ? "在位" : "不在位");
            fields.Add((chars & 0x0004) == 0 ? "已插電" : "未插電");
            if ((chars & 0x0001) != 0) fields.Add("有熱插拔");
            if ((chars & 0x0008) != 0) fields.Add("有電源表");
            if ((chars & 0x0010) != 0) fields.Add("有解除警告");
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
        // 只算「有回報功率」的。0x8000 是「未回報」旗標，不是 32768 W。
        var reported = psus.Where(s => (s.WordAt(0x0C) & 0x8000) == 0).ToList();
        if (psus.Count == 0 || reported.Count == 0)
            return new HardwareFact(PsuPowerKey, Category, "電源供應器額定功率",
                "", "W", "SMBIOS Type 39 @0x0C", FactTrustLevel.Measured, false, at, null,
                FactAvailability.NotApplicable,
                psus.Count == 0
                    ? "SMBIOS 未列 Type 39"
                    : $"{psus.Count} 個電源供應器都沒有回報功率"
                      + "（旗標 0x8000 代表未回報）——那是「沒說」而不是「0 瓦」");

        int total = reported.Sum(s => s.WordAt(0x0C));
        string detail = string.Join("＋", reported.Select(s => $"{s.WordAt(0x0C)} W"));
        return new HardwareFact(PsuPowerKey, Category, "電源供應器額定功率",
            $"{detail}（合計 {total} W）", "W", "SMBIOS Type 39 @0x0C",
            FactTrustLevel.Derived, false, at, total, FactAvailability.Present,
            reported.Count == psus.Count
                ? "這是額定功率的總和，**不是可用功率**——轉換效率、各路輸出上限、"
                  + "以及同時啟動的峰值都不在這個數字裡"
                : $"{psus.Count} 個之中只有 {reported.Count} 個回報功率，"
                  + "合計只涵蓋回報的那些——**未回報的不以 0 計入**");
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
        byte[] deviceTypes = onboard.Select(s => s.ByteAt(0x04)).ToArray();
        for (int i = 0; i < onboard.Count; i++)
        {
            var s = onboard[i];
            var fields = new List<string> { $"[{i}] {DeviceTypeName(deviceTypes[i])}" };

            if (s.Length >= 0x0D)
            {
                ushort segments = s.WordAt(0x0B);
                if (segments > 0) fields.Add($"{segments} 個段群組");
            }

            // 參考指定：每個 4 bytes（參考型別 u8 ＋ 參考值 u16 ＋ 保留 u8）。
            int refs = 0;
            int p = 0x0D;
            while (p + 4 <= s.Length)
            {
                if (s.ByteAt(p) == 0) break;   // 參考型別 0 = 結束
                refs++;
                p += 4;
            }
            if (refs > 0) fields.Add($"{refs} 筆參考指定");

            string status = OnboardStatus(s);
            if (status.Length > 0) fields.Add(status);

            parts.Add(string.Join("／", fields));
        }

        return new HardwareFact(OnboardKey, Category, "內建裝置明細",
            string.Join("；", parts), "", "SMBIOS Type 41（裝置型別@0x04、狀態@0x09、"
            + "段群組@0x0B、參考指定@0x0D 起每筆 4 bytes）",
            FactTrustLevel.Measured, false, at, onboard.Count, FactAvailability.Present,
            "「參考指定」指向的是其他結構——本服務只計數，不追進去看，"
            + "因為那需要跨結構的關聯解析（列在另一項）");
    }

    /// <summary>
    /// 狀態：長度至少要到 0x0B 才讀得到（@0x09 是 u8，但 @0x0A 起是 0x0B 的裝置型別
    /// ——這裡只讀 @0x09 的單一位元組，所以門檻是 0x0A）。
    /// </summary>
    private static string OnboardStatus(SmbiosStruct s)
    {
        if (s.Length < 0x0A) return "";
        byte status = s.ByteAt(0x09);
        string state = (status & 0x80) != 0 ? "已啟用" : "已停用";
        return status == 0 ? "" : $"{state}（0x{status:X2}）";
    }

    private static string DeviceTypeName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "顯示", 0x04 => "SCSI 控制器",
        0x05 => "乙太網路", 0x06 => "Token Ring", 0x07 => "音效",
        0x08 => "PCMCIA 控制器", 0x09 => "VLB 控制器", 0x0A => "內部磁碟控制器",
        0x0B => "SATA 控制器", 0x0C => "內部磁碟", 0x0D => "光碟",
        0x0E => "磁帶", 0x0F => "快閃記憶體", 0x10 => "SAS 控制器",
        0x11 => "1394 控制器", 0x12 => "USB 控制器", 0x13 => "光纖通道",
        0x14 => "iSCSI", 0x15 => "SATA 埠", 0x16 => "SAS 埠", 0x17 => "NVM Express",
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
