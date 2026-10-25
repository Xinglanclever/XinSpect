using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// TPM 兩個視圖的對照（U10-OS-0061／U10-FW-0051 的「對照鍵」部分）：
/// <b>TBS 那一側</b>（TPM 對 <c>TPM2_GetCapability(TPM_PT_FIXED)</c> 的直接回應：family／revision／廠商）
/// × <b>WMI 那一側</b>（Windows 對 <c>Win32_Tpm</c> 的視圖：<c>SpecVersion</c>／<c>ManufacturerIdTxt</c>）。
///
/// <para>
/// <b>背景（這一項為什麼存在）：</b>WMI 備援通路<b>早已存在</b>——
/// <see cref="TpmFactsService"/> 在 TBS 不可用時走 <c>root\cimv2\security\microsofttpm</c> 的
/// <c>Win32_Tpm</c>（第 46 行起），<see cref="TpmSecureBootService"/> 也零特權讀同一個類別。
/// 第 269 輪之前的查重判定把它標成「未實作」，根因是<b>大小寫敏感的 grep</b>
/// （查 <c>Win32_TPM</c>、原始碼寫的是 <c>Win32_Tpm</c>）——詳見
/// <c>docs/ULTIMATE-10000/DEDUP-VERDICTS-2026-10-25.md</c> 檔尾的更正節。
/// 所以這一項<b>不重做通路</b>：它補的是兩個視圖之間的<b>對照事實</b>。
/// </para>
///
/// <para>
/// <b>對照的規則（三種結果，各自如實）：</b>
/// </para>
/// <list type="number">
///   <item><b>兩側都有</b>——逐欄比對（family 對 SpecVersion 第一段、revision 對第三段、
///     廠商對 ManufacturerIdTxt）。一致＝Present；<b>有矛盾也＝Present</b>，
///     但矛盾欄<b>具名列出雙方的原始值，不仲裁誰對</b>——
///     兩個視圖是兩個來源，本服務沒有第三個來源可判誰錯。</item>
///   <item><b>一側讀不到</b>——NotApplicable，理由欄說出<b>缺的是哪一側</b>，
///     並把另一側讀到的值照抄進去（「讀不到」與「沒有 TPM」是不同的斷言，
///     與 <see cref="TpmSecureBootCrossFactsService"/> 同一條線）。</item>
///   <item><b>兩側都讀不到</b>——NotApplicable，只陳述兩條路都不通，不推測。</item>
/// </list>
///
/// <para>
/// <b>界線：</b>WMI 的 <c>SpecVersion</c> 是 Windows 對規格版本的描述（「2.0, 0, 1.16」三段），
/// 三個狀態欄位都帶 <c>_InitialValue</c>——<b>它是 Windows 的視圖，不是 TPM 的直接回應</b>
/// （<see cref="TpmFactsService"/> 的註解已寫明）；TBS 那一側才是 TPM 自己的話。
/// 兩側一致<b>不代表兩次讀取是同一次</b>（這正是本專案在 MeasuredBootFactsService 與
/// UefiVersionCrossFactsService 裡警告過的事），一致只說「兩個視圖此刻說了同一件事」。
/// </para>
/// </summary>
public static class TpmViewCrossFactsService
{
    private const string Category = "信賴根";

    public const string Key = "tpm.view.cross";

    private const string Source =
        "TPM2_GetCapability(TPM_PT_FIXED) 經 TBS × Win32_Tpm（root\\cimv2\\security\\microsofttpm）對照";

    /// <summary>
    /// TBS 那一側讀到的 TPM 自述（TPM_PT_FIXED 的子集）。
    /// <see cref="Family"/> 是四位 ASCII（「2.0」）、<see cref="RevisionText"/> 是規格版號
    /// （「1.16」＝revision×100）、<see cref="Manufacturer"/> 是四位 ASCII 廠商碼（如「IFX」）。
    /// 三者都可能為 null（TPM 沒回報該欄）——<b>null 是「未回報」，不是 0</b>。
    /// </summary>
    public sealed record TbsView(string? Family, string? RevisionText, string? Manufacturer);

    /// <summary>
    /// 收集對照事實。兩個探針都可注入（測試給假視圖）；<c>null</c> 表示走真機路徑：
    /// TBS 側用 <see cref="TpmFactsService"/> 的 internal 通路（同一份 P/Invoke，不寫第二份），
    /// WMI 側自讀 <c>Win32_Tpm</c>（零特權）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<TbsView?>? tbs = null,
        Func<TpmFactsService.TpmWindowsView?>? wmi = null)
    {
        var tbsView = Safe(tbs ?? ReadTbsView);
        var wmiView = Safe(wmi ?? ReadWindowsView);

        if (tbsView is null && wmiView is null)
            return [Unavailable(at, FactAvailability.NotApplicable,
                "兩個視圖都讀不到：TBS 那一側無法取得 TPM_PT_FIXED（無 TPM／TBS 不可用），"
                + "Windows 的 Win32_Tpm 也讀不到——兩條路都不通，如實標，不推測哪一側該負責")];

        if (tbsView is null || wmiView is null)
        {
            bool tbsMissing = tbsView is null;
            string other = tbsMissing ? DescribeWmi(wmiView!) : DescribeTbs(tbsView!);
            return [Unavailable(at, FactAvailability.NotApplicable,
                (tbsMissing
                    ? "TBS 那一側讀不到（無 TPM 或 TBS 不可用），所以無從對照；"
                    : "Windows 的 Win32_Tpm 讀不到（WMI 命名空間不存在或查詢被拒），所以無從對照；")
                + $"另一側讀到的值如實附上：{other}。"
                + "「讀不到一側」與「兩側矛盾」是不同的斷言——缺一側時不比較、不推測缺側的值")];
        }

        // ── 兩側都有：逐欄比對 ──
        var parts = (wmiView.SpecVersion ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? wmiFamily = parts.Length >= 1 ? parts[0] : null;
        string? wmiRevision = parts.Length >= 3 ? parts[2] : null;

        var consistent = new List<string>();
        var contradictions = new List<string>();
        var incomparable = new List<string>();

        Compare(consistent, contradictions, incomparable, "family（規格家族）",
            tbsView.Family, wmiFamily);
        Compare(consistent, contradictions, incomparable, "revision（規格版號）",
            tbsView.RevisionText, wmiRevision);
        Compare(consistent, contradictions, incomparable, "manufacturer（廠商碼）",
            tbsView.Manufacturer, wmiView.ManufacturerIdTxt, caseInsensitive: true);

        if (consistent.Count == 0 && contradictions.Count == 0)
            return [Unavailable(at, FactAvailability.NotApplicable,
                "兩側都有資料，但沒有可比對的欄位：WMI 的 SpecVersion 無法解析出 family／revision"
                + $"（原始值「{wmiView.SpecVersion ?? "（未回報）"}」），ManufacturerIdTxt 也未回報——"
                + "沒有可比對的東西就是沒有，不硬湊結論")];

        string boundary =
            "界線：TBS 那一側是 TPM 對 TPM_PT_FIXED 的直接回應，WMI 那一側是 Windows 的視圖"
            + "（SpecVersion 是三段式描述、狀態欄位帶 _InitialValue）——兩者不是同一次讀取，"
            + "一致只說「兩個視圖此刻說了同一件事」；"
            + "本服務沒有第三個來源可判誰錯，所以矛盾只並列、不仲裁。";

        if (contradictions.Count > 0)
        {
            string value = $"**矛盾**：{string.Join("；", contradictions)}。"
                + $"TBS 側原始值：{DescribeTbs(tbsView)}；WMI 側原始值：{DescribeWmi(wmiView)}。"
                + (consistent.Count > 0 ? $"一致的欄位：{string.Join("、", consistent)}。" : "")
                + boundary;
            return [new HardwareFact(Key, Category, "TPM 兩視圖對照（TBS × Win32_Tpm）",
                value, "", Source, FactTrustLevel.Derived, false, at, 0.0)];
        }

        string ok = $"一致（可比對的 {consistent.Count} 欄全數相符：{string.Join("、", consistent)}）。"
            + $"TBS 側原始值：{DescribeTbs(tbsView)}；WMI 側原始值：{DescribeWmi(wmiView)}。"
            + (incomparable.Count > 0
                ? $"無法比對的欄位（一側未回報）：{string.Join("、", incomparable)}——如實標缺，不以另一側代填。"
                : "")
            + boundary;
        return [new HardwareFact(Key, Category, "TPM 兩視圖對照（TBS × Win32_Tpm）",
            ok, "", Source, FactTrustLevel.Derived, false, at, 1.0)];
    }

    /// <summary>比對一欄：兩側都有值才比，一側缺＝ incomparable，都有的看相等。</summary>
    private static void Compare(List<string> consistent, List<string> contradictions,
        List<string> incomparable, string field, string? tbsValue, string? wmiValue,
        bool caseInsensitive = false)
    {
        if (string.IsNullOrWhiteSpace(tbsValue) || string.IsNullOrWhiteSpace(wmiValue))
        {
            if (tbsValue is not null || wmiValue is not null)
                incomparable.Add($"{field}（TBS＝{tbsValue ?? "未回報"}、WMI＝{wmiValue ?? "未回報"}）");
            return;
        }
        bool same = string.Equals(tbsValue.Trim(), wmiValue.Trim(),
            caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (same) consistent.Add(field);
        else contradictions.Add($"{field}：TBS 說「{tbsValue}」、WMI 說「{wmiValue}」");
    }

    private static string DescribeTbs(TpmViewCrossFactsService.TbsView v)
    {
        static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "未回報" : s;
        return $"family {Or(v.Family)}、revision {Or(v.RevisionText)}、廠商 {Or(v.Manufacturer)}";
    }

    private static string DescribeWmi(TpmFactsService.TpmWindowsView v)
    {
        static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "未回報" : s;
        return $"SpecVersion「{Or(v.SpecVersion)}」、ManufacturerIdTxt「{Or(v.ManufacturerIdTxt)}」"
            + $"、ManufacturerVersion「{Or(v.ManufacturerVersion)}」";
    }

    // ── 真機路徑 ────────────────────────────────────────────────────────

    /// <summary>
    /// TBS 側：讀 TPM_PT_FIXED 一組屬性，取出 family／revision／廠商。
    /// <b>通路用 <see cref="TpmFactsService"/> 的 internal 存取器</b>
    /// （同一份 tbs.dll P/Invoke，與 TpmReplayFactsService 同型）——不寫第二份 P/Invoke。
    /// 任何一步不成（建不了內容、TBS 錯誤、回應格式不符、capability 不符）都回 null，
    /// 讓「讀不到」在上層如實標，不在這裡猜原因。
    /// </summary>
    private static TbsView? ReadTbsView()
    {
        if (!TpmFactsService.TryCreateContextForReplay(out nint ctx)) return null;
        try
        {
            var (result, output, outputLen) = TpmFactsService.RealSubmitForReplay(ctx,
                Tpm2.BuildGetCapabilityCommand(Tpm2.CapTpmProperties, Tpm2.PtFamilyIndicator,
                    Tpm2.FixedPropertyCount));
            if (result != 0) return null;
            var trimmed = outputLen is > 0 and <= int.MaxValue and var n
                ? output[..(int)Math.Min(n, output.Length)] : Array.Empty<byte>();
            var data = Tpm2.GetCapabilityData(trimmed, out _, out uint cap, out _);
            if (data is null || cap != Tpm2.CapTpmProperties) return null;

            var props = new Dictionary<uint, uint>();
            foreach (var p in Tpm2.DecodeTpmProperties(data)) props[p.Property] = p.Value;
            if (props.Count == 0) return null;

            string? family = props.TryGetValue(Tpm2.PtFamilyIndicator, out uint f)
                ? Tpm2.FamilyIndicator(f) : null;
            string? revision = props.TryGetValue(Tpm2.PtRevision, out uint r)
                && Tpm2.Revision(r) is { } rev ? $"{rev.Major}.{rev.Minor}" : null;
            string? manufacturer = props.TryGetValue(Tpm2.PtManufacturer, out uint m)
                ? Tpm2.Ascii4(m).TrimEnd('\0', ' ') : null;
            if (string.IsNullOrWhiteSpace(manufacturer)) manufacturer = null;

            if (family is null && revision is null) return null;
            return new TbsView(family, revision, manufacturer);
        }
        finally
        {
            TpmFactsService.CloseContextForReplay(ctx);
        }
    }

    /// <summary>
    /// WMI 側：讀 <c>Win32_Tpm</c>（零特權）。形狀沿用 <see cref="TpmFactsService"/> 的
    /// <c>TpmWindowsView</c>（同一個 record，不另造形狀）；它自己的 <c>ReadWindowsView</c> 是
    /// private，這裡按它的讀法自讀一份——只挑對照需要的欄位，狀態三欄（Enabled／Activated／Owned）
    /// 不取（對照用不到，而且那些是 Windows 的佈建視圖，不是 TPM 的話）。
    /// </summary>
    private static TpmFactsService.TpmWindowsView? ReadWindowsView()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\cimv2\security\microsofttpm", "SELECT * FROM Win32_Tpm");
            foreach (System.Management.ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    return new TpmFactsService.TpmWindowsView(
                        SpecVersion: o["SpecVersion"] as string,
                        ManufacturerIdTxt: (o["ManufacturerIdTxt"] as string)?.Trim(),
                        ManufacturerVersion: o["ManufacturerVersion"] as string,
                        Enabled: o["IsEnabled_InitialValue"] as bool?,
                        Activated: o["IsActivated_InitialValue"] as bool?,
                        Owned: o["IsOwned_InitialValue"] as bool?);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>探針丟例外＝那條路讀不到（回 null），不讓一側的失敗炸掉整個對照。</summary>
    private static T? Safe<T>(Func<T?> probe) where T : class
    {
        try { return probe(); } catch { return null; }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability,
        string reason) =>
        new(Key, Category, "TPM 兩視圖對照（TBS × Win32_Tpm）", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
