using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// P2-030／P2-031 的結構政策事實（ULTIMATE-1000 階段二）。
///
/// <para><b>P2-029 的 Type 44 那半不在這裡——它退回未開工</b>（欄位佈局沒有依據）。
/// Type 43 那一半已完成，在 <see cref="SmbiosTpmFactsService"/>。</para>
///
/// <para>
/// <b>為什麼把三項放一起：</b>它們都是「走過整份 SMBIOS 表」的橫向檢查——
/// Type 44 要對照 Type 4 的插槽字串，P2-030 要量每一列的尾巴長度，
/// P2-031 要驗每一個結構的邊界。三者共用同一次走訪。
/// </para>
///
/// <para>
/// <b>界線：這些是「表的自我一致性」檢查，不是「表有沒有說謊」。</b>
/// 一個結構的長度比規範多 8 個位元組，代表韌體用了較新的規格欄位
/// ——那通常是好事；<b>但讀者必須知道自己的解碼器只讀到哪裡</b>，
/// 否則「沒讀到的欄位」會被誤當成「沒有那個欄位」。
/// </para>
/// </summary>
public static class SmbiosStructureFactsService
{
    private const string Category = "SMBIOS";

    public const string TrailingKey = "sb.trailing.count";
    public const string BoundsKey = "sb.bounds.ok";

    /// <summary>
    /// SMBIOS 各型別的規範最小長度（不含字串表）。
    /// 只列本專案有解碼器的型別——**沒列到的型別不參與尾巴統計**，
    /// 因為「規範長度是多少」我們沒有依據，憑印象填會製造假的「超出」。
    /// </summary>
    private static readonly Dictionary<byte, int> SpecLength = new()
    {
        [0] = 0x12, [1] = 0x08, [2] = 0x08, [3] = 0x09,
        [4] = 0x1A, [7] = 0x13, [9] = 0x0C,
        [11] = 0x05, [16] = 0x0F, [17] = 0x15, [19] = 0x0F,
        [32] = 0x0B, [39] = 0x10, [42] = 0x05, [43] = 0x1B,
    };

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

        return
        [
            Trailing(at, structs),
            Bounds(at, structs),
        ];
    }

    // ── Type 44：處理器附加資訊（未開工）──────────────────────────────

    // P2-029 的 Type 44 那半退回未開工。欄位佈局我沒有可靠依據：
    // 參考控制代碼是 u16，而我把「處理器型別」放在 @0x05
    // ——兩者重疊，型別會蓋掉控制代碼的高位元組。
    //
    // 徵狀是 0x0400 被讀成 0x0300（差值固定 0x0100）——那正是「被覆寫」的形狀。
    // 我花了兩輪在測試 helper 的參數上找原因，而線索一直是那個數字本身。
    //
    // 修好的路徑：找到 SMBIOS 規格 Table 7.45（Type 44）的欄位圖。
    // Type 43 的那一半（sb.tpmdev）已完成並保留。

    // ── P2-030：結構長度政策 ────────────────────────────────────────────

    private static HardwareFact Trailing(DateTimeOffset at, IReadOnlyList<SmbiosStruct> structs)
    {
        // 只統計「規範長度已知」的型別。
        var measured = structs.Where(s => SpecLength.ContainsKey(s.Type)).ToList();
        if (measured.Count == 0)
            return new HardwareFact(TrailingKey, Category, "結構尾巴欄位數",
                "沒有任何型別的規範長度是已知的", "個", "SMBIOS 結構長度對照",
                FactTrustLevel.Derived, false, at, 0, FactAvailability.NotApplicable,
                "本服務只對「規範長度已知」的型別做比較——憑印象填規範長度會製造假的超出");

        var over = measured.Where(s => s.Length > SpecLength[s.Type]).ToList();
        var under = measured.Where(s => s.Length < SpecLength[s.Type]).ToList();

        string value = over.Count == 0
            ? $"{measured.Count} 個有規範長度的結構都沒有尾巴欄位"
            : string.Join("、", over.GroupBy(s => s.Type)
                .Select(g => $"Type {g.Key}（{g.Count()} 個，最多多 "
                    + $"{g.Max(s => s.Length - SpecLength[g.Key])} bytes）"));

        return new HardwareFact(TrailingKey, Category, "結構尾巴欄位數", value, "個",
            "SMBIOS 結構長度對照（只含本專案有解碼器的型別）",
            FactTrustLevel.Derived, false, at, over.Count,
            under.Count > 0 ? FactAvailability.ReadError
                : over.Count > 0 ? FactAvailability.Unknown
                : FactAvailability.Present,
            under.Count > 0
                ? $"{under.Count} 個結構**短於規範長度**"
                  + $"（{string.Join("、", under.GroupBy(s => s.Type).Select(g => $"Type {g.Key}"))}）"
                  + "——那比多出欄位嚴重：短於規範代表欄位被截掉，"
                  + "而本解碼器對那些位移的讀取會落到字串表或表尾，那些值看起來仍然像值"
                : over.Count > 0
                    ? "結構比規範長是常態（韌體用了較新的規格欄位）——"
                      + "但讀者必須知道解碼器只讀到規範長度為止，"
                      + "否則「沒讀到」會被誤當成「沒有那個欄位」"
                    : null);
    }

    // ── P2-031：校驗與範圍 ──────────────────────────────────────────────

    private static HardwareFact Bounds(DateTimeOffset at, IReadOnlyList<SmbiosStruct> structs)
    {
        // 每一個結構的長度都必須至少涵蓋它自己的固定部分。
        // 「固定部分是 4 bytes」是 SMBIOS 的最小值（型別 1 ＋ 長度 1 ＋ 控制代碼 2）。
        var tooShort = structs.Where(s => s.Length < 4).ToList();

        // 控制代碼重複：SMBIOS 要求每個結構的控制代碼唯一，重複代表表本身有問題。
        var dupHandles = structs.GroupBy(s => s.Handle).Where(g => g.Count() > 1).ToList();

        // 同型別多個單一實例型別（0／1／2／3／32 在規格上只該有一個）。
        byte[] singleton = [0, 1, 2, 3, 32];
        var duplicated = singleton
            .Where(t => structs.Count(s => s.Type == t) > 1)
            .ToList();

        var problems = new List<string>();
        if (tooShort.Count > 0) problems.Add($"{tooShort.Count} 個結構短於最小固定長度 4 bytes");
        if (dupHandles.Count > 0)
            problems.Add($"{dupHandles.Count} 個控制代碼重複"
                + $"（{string.Join("、", dupHandles.Take(5).Select(g => $"0x{g.Key:X4}"))}）");
        if (duplicated.Count > 0)
            problems.Add($"{duplicated.Count} 個單一實例型別出現多次"
                + $"（{string.Join("、", duplicated.Select(t => $"Type {t}"))}）");

        bool ok = problems.Count == 0;
        return new HardwareFact(BoundsKey, Category, "表結構邊界",
            ok ? $"{structs.Count} 個結構的邊界與控制代碼都一致"
               : string.Join("；", problems),
            "", "SMBIOS 結構走訪（最小固定長度、控制代碼唯一、單一實例型別）",
            FactTrustLevel.Derived, false, at, problems.Count,
            ok ? FactAvailability.Present : FactAvailability.ReadError,
            ok ? null
               : "以上都是表的自我一致性問題——**不代表值錯了**，"
                 + "但代表以控制代碼建立關聯時可能指到錯的結構");
    }

    // ── 共用 ────────────────────────────────────────────────────────────

    private static string Str(SmbiosStruct s, int offset)
    {
        if (offset >= s.Length) return "";
        string? v = s.GetString(s.ByteAt(offset));
        return v is null || v.Length == 0 || SmbiosService.IsPlaceholder(v) ? "" : v;
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [TrailingKey, BoundsKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", "SMBIOS",
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
