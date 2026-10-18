using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>
/// EFI 變數面的事實（ULTIMATE-1000 階段二 P2-082／P2-089／P2-090）：變數盤點、
/// 韌體膠囊痕跡、NVRAM 用量。
///
/// <para>
/// <b>這三把鍵的共同結論是「平台不提供」，而那是這三項的答案，不是它們做不出來。</b>
/// Windows 給的是<b>按名字</b>讀寫 EFI 變數的公開 API
/// （<c>GetFirmwareEnvironmentVariable</c>／<c>SetFirmwareEnvironmentVariable</c>），
/// <b>沒有列舉 API</b>——要列出「這台機器上有哪些變數」只能走未公開的
/// <c>NtEnumerateSystemEnvironmentValuesEx</c>，而本專案不使用未公開介面
/// （同一條線畫在 <see cref="WiringDecisions"/> 的登記表上）。
/// 沒有列舉就沒有總數、沒有逐變數大小、也就找不到 <c>Capsule####</c> 那幾個變數。
/// </para>
///
/// <para>
/// <b>為什麼還是把它們做成事實，而不是留著 todo：</b>留著 todo 會讓畫面與帳本繼續假裝
/// 「這三把還在排隊等實作」。實際上它們卡的是平台，不是進度——階段二矩陣把 todo 讀成
/// 「還沒做」，而這一項真正的狀態是「**這條路不存在**」。三態裡的
/// <c>NotSupported</c> 就是為了陳述這件事而存在的：不是錯誤、不是缺權限，
/// 是這台機器的作業系統沒有那條路。
/// </para>
///
/// <para>
/// <b>與 <c>ev.var.attrs</c>（P2-083，已實作）的界線：</b>那一把陳述的是「按名字讀到的
/// 那一個變數的屬性」，它不需要列舉；這三把要的是「全部變數」，那是列舉問題。
/// 兩者常被混為一談，於是把「讀得到某個變數」誤讀成「盤點得了」。
/// </para>
/// </summary>
public static class FirmwareVariableSurfaceFactsService
{
    private const string Category = "韌體";
    private const string Source = "Windows 公開 API 的可列舉面（建置時的介面盤點）";

    /// <summary>P2-082：變數名稱、大小與屬性的盤點。</summary>
    public const string CountKey = "ev.var.count";

    /// <summary>P2-089：已套用的韌體膠囊（Capsule#### 變數）。</summary>
    public const string CapsuleKey = "fw.capsule.history";

    /// <summary>P2-090：變數儲存（NVRAM）用量與上限。</summary>
    public const string NvramKey = "ev.nvram.usage";

    /// <summary>沒有列舉 API 的說明：三把鍵共用同一句根因，不各自表述。</summary>
    private const string NoEnumeration =
        "Windows 只有按名字讀寫 EFI 變數的公開 API（GetFirmwareEnvironmentVariable／"
        + "SetFirmwareEnvironmentVariable），沒有列舉 API；列出「有哪些變數」在公開介面上不存在，"
        + "本專案不使用未公開的 NtEnumerateSystemEnvironmentValuesEx。";

    /// <summary>讀單一變數的額外前提（與列舉是兩件事，分開寫）。</summary>
    private const string PrivilegeBoundary =
        "而即使按名字讀，也需要行程權杖啟用 SeSystemEnvironmentPrivilege——那是第二個前提，"
        + "與列舉 API 不存在不是同一件事。";

    /// <summary>
    /// 收集三把事實。<paramref name="firmwareType"/> 由呼叫端提供
    /// （<see cref="FirmwareModeFactsService.Read"/> 的欄位，0 = 未知、1 = BIOS、2 = UEFI）
    /// ——<b>不自己再讀一次</b>，同一輪的兩份事實必須來自同一次讀取。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, int? firmwareType)
    {
        return
        [
            Blocked(at, CountKey, "EFI 變數盤點（名稱／大小／屬性）", firmwareType,
                "有列舉 API 之後，這一項是逐變數讀取；現在的答案是：這條路不存在。"),
            Blocked(at, CapsuleKey, "韌體膠囊與更新痕跡（Capsule####）", firmwareType,
                "Capsule#### 的編號本身要有變數清單才找得到——沒有列舉就不知道有幾顆膠囊，"
                + "也不知道它們叫什麼名字。"),
            Blocked(at, NvramKey, "變數儲存用量與上限", firmwareType,
                "用量需要逐變數大小再相加；上限（可變儲存區）同樣要靠列舉才數得出來。"),
        ];
    }

    private static HardwareFact Blocked(DateTimeOffset at, string key, string name,
        int? firmwareType, string whatItWouldTake)
    {
        // Legacy BIOS 開機時，這台機器上根本沒有 EFI 變數——那是「不適用」，不是「不支援」。
        // 兩種「沒有」在畫面上必須分開，這是本專案六態守恆的同一條線。
        if (firmwareType == 1)
            return new HardwareFact(key, Category, name, "", "", Source,
                FactTrustLevel.Derived, false, at, null, FactAvailability.NotApplicable,
                "這台機器以 Legacy BIOS（CSM）開機，韌體介面不是 UEFI——沒有 EFI 變數這種東西可以盤點"
                + "（這與「有 UEFI 但列舉不了」是不同的狀態）");

        string reason = NoEnumeration + " " + PrivilegeBoundary + " " + whatItWouldTake;
        if (firmwareType is null or 0)
            reason = "韌體型別也未能確認，但這一項的界線不在型別：" + reason;

        return new HardwareFact(key, Category, name, "", "", Source,
            FactTrustLevel.Derived, false, at, null, FactAvailability.NotSupported, reason);
    }
}
