using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 硬體信任根與虛擬化安全（VBS）的對照（ULTIMATE-1000 階段二 P2-062）。
///
/// <para>
/// <b>這一項是純交叉：不新增任何讀取、不新增任何編號。</b>
/// 兩邊的輸入都是既有的事實——
/// <c>bt.root</c>（信任根的三種可能性有沒有證據）與
/// <c>virt.vbs</c>（VBS 的四態：關閉／已啟用但未執行／執行中／讀不到）。
/// </para>
///
/// <para>
/// <b>而三種信任根與 VBS 的關係不同——所以不能一句話帶過：</b>
/// </para>
/// <list type="bullet">
///   <item><b>Secure Boot ↔ VBS</b>：<b>這是唯一的真實依賴。</b>
///     VBS 的「安全啟動」保護在 Secure Boot 關閉時不能生效。</item>
///   <item><b>Boot Guard ↔ VBS</b>：<b>沒有直接關係。</b>
///     Boot Guard 是開機時的韌體驗證，而 VBS 是執行期的隔離——
///     兩者在時間軸上不重疊。</item>
///   <item><b>TPM 量測開機 ↔ VBS</b>：<b>沒有直接關係。</b>
///     兩者都依賴 UEFI 開機，但那是共同的**前提**，不是依賴關係。</item>
/// </list>
///
/// <para>
/// <b>所以本服務只對 Secure Boot 那一條做實質陳述，其餘兩條明說沒有直接關係。</b>
/// 而<b>「沒有直接關係」也必須說出來</b>——否則讀者會以為沒說的那兩條有問題。
/// </para>
///
/// <para>
/// <b>界線：這不是判決。</b>
/// 「VBS 執行中而 Secure Boot 關閉」值得注意，但<b>那不代表設定錯誤</b>：
/// VBS 可以在 Secure Boot 關閉時執行（只是少了一層保護）。
/// </para>
/// </summary>
public static class TrustRootVbsCrossFactsService
{
    private const string Category = "信賴根";
    private const string Source = "交叉對照（bt.root × virt.vbs）";

    public const string CrossKey = "bt.cross.vbs";

    /// <summary>VBS 的四態——與既有的 <c>virt.vbs</c> 一字不差。</summary>
    public enum VbsState
    {
        /// <summary>讀不到。</summary>
        Unknown = -1,
        Off = 0,
        EnabledNotRunning = 1,
        Running = 2,
    }

    /// <summary>
    /// 對照的輸入。
    /// <c>SecureBoot</c>／<c>MeasuredBoot</c>／<c>BootGuardAcm</c> 為 <c>null</c> 代表那一邊讀不到。
    /// </summary>
    public sealed record CrossInput(VbsState Vbs, bool? SecureBoot, bool? MeasuredBoot,
        bool? BootGuardAcm);

    /// <summary>從既有事實取出對照輸入。找不到或非 Present 的值一律回 <c>null</c>（不推測）。</summary>
    public static CrossInput FromFacts(IReadOnlyList<HardwareFact> facts)
    {
        var vbs = facts.FirstOrDefault(f => f.Key == "virt.vbs");
        var vbsState = vbs is { Availability: FactAvailability.Present }
            ? vbs.Value switch
            {
                "執行中" => VbsState.Running,
                "已啟用但未執行" => VbsState.EnabledNotRunning,
                "關閉" => VbsState.Off,
                _ => VbsState.Unknown,
            }
            : VbsState.Unknown;

        var root = facts.FirstOrDefault(f => f.Key == "bt.root.types");
        string types = root is { Availability: FactAvailability.Present } ? root.Value : "";

        return new CrossInput(vbsState,
            types.Contains("Secure Boot", StringComparison.Ordinal) ? true : null,
            types.Contains("TPM 度量開機", StringComparison.Ordinal) ? true : null,
            types.Contains("Boot Guard", StringComparison.Ordinal) ? true : null);
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, CrossInput? input)
    {
        if (input is null)
            return
            [
                new HardwareFact(CrossKey, Category, "信任根與 VBS 對照", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                    "沒有輸入就沒有對照可言——本服務不自己去讀信任根或 VBS"
                    + "（那兩條路徑有它們自己的可及性事實：bt.root、virt.vbs）"),
            ];

        if (input.Vbs == VbsState.Unknown)
            return
            [
                new HardwareFact(CrossKey, Category, "信任根與 VBS 對照", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "**VBS 那一邊讀不到，所以無從對照。**"
                    + "**「讀不到」與「關閉」是不同的斷言**——"
                    + "而本服務不拿單邊的值去配信任根"),
            ];

        string vbsText = input.Vbs switch
        {
            VbsState.Running => "VBS 執行中",
            VbsState.EnabledNotRunning => "VBS 已啟用但未執行",
            _ => "VBS 關閉",
        };

        // ── 唯一有實質依賴的那一條：Secure Boot ──────────────────────────
        string secureBootPart = input.SecureBoot switch
        {
            true => "**Secure Boot 開著**——VBS 的安全啟動保護有依據。",
            false or null => "",
        };
        bool vbsOn = input.Vbs is VbsState.Running or VbsState.EnabledNotRunning;
        if (vbsOn && input.SecureBoot != true)
        {
            secureBootPart = input.SecureBoot is null
                ? "**而 Secure Boot 那一邊讀不到**，所以無從判斷 VBS 的安全啟動保護有沒有依據。"
                : "**而 Secure Boot 關著——這是本服務唯一有實質依賴的那一條**："
                  + "VBS 的「安全啟動」保護在 Secure Boot 關閉時不能生效。"
                  + "**而那不代表設定錯誤**：VBS 可以在 Secure Boot 關閉時執行，"
                  + "只是少了一層保護。";
        }
        else if (input.Vbs == VbsState.Off)
        {
            // VBS 關著時沒有實質依賴可講，但仍要說出 Secure Boot 那一邊的狀態，
            // 否則那一段會整個消失、看起來像漏了。
            //
            // **而這裡不能寫成 `vbsOn || …`**：VBS 開著而 Secure Boot 也開著時，
            // 上面那一個 switch 已經給出了「VBS 的安全啟動保護有依據」——
            // 而那正是本項唯一有實質依賴的那一條。寫成 `vbsOn || …` 會把那一句
            // 蓋成只說「Secure Boot 開著」，讓那把鍵看不出它與 VBS 的關係。
            // （那是死碼：`true =>` 那個分支永遠不會被讀到。）
            secureBootPart = input.SecureBoot switch
            {
                true => "**Secure Boot 開著。**",
                false => "**Secure Boot 關著。**",
                _ => "**Secure Boot 那一邊讀不到。**",
            };
        }

        // ── 其餘兩條：明說沒有直接關係 ────────────────────────────────────
        const string others =
            "**而其餘兩種信任根與 VBS 沒有直接關係，而那也要說出來**——"
            + "否則讀者會以為沒說的那兩條有問題："
            + "**Boot Guard** 是開機時的韌體驗證，而 VBS 是執行期的隔離，"
            + "兩者在時間軸上不重疊；"
            + "**TPM 量測開機**與 VBS 都依賴 UEFI 開機，"
            + "但那是共同的**前提**，不是依賴關係。";

        return
        [
            new HardwareFact(CrossKey, Category, "信任根與 VBS 對照",
                vbsText + "。" + secureBootPart + others, "", Source,
                FactTrustLevel.Derived, false, at, (int)input.Vbs, FactAvailability.Present,
                // **Present 的事實不得帶 unavailableReason**（HardwareSnapshotService 的不變式）。
                // 我第一版把 secureBootPart、others 與「不做判決」都放在那裡，
                // 而整合測試把它抓出來（「可用的事實不得帶 unavailableReason」）。
                //
                // **那些話沒有消失——它們移進了 Value**：這個鍵的整個內容
                // 就是「這個組合是什麼意思」，而那正是 Value 該說的事。
                // **`UnavailableReason` 是「為什麼讀不到」的欄位，
                // 而 Present 的事實沒有「讀不到」可言。**
                null),
        ];
    }
}
