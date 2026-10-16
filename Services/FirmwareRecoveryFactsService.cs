using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 韌體復原路徑與膠囊支援（ULTIMATE-1000 階段二 P2-061）。
///
/// <para>
/// <b>這一項有兩半，而它們的可做程度不同——所以如實分開講。</b>
/// </para>
///
/// <para>
/// <b>可做的一半：膠囊更新的支援宣告。</b>UEFI 的
/// <c>OsIndicationsSupported</c> 變數就是「這台機器的韌體支援哪些 OS 指示」，
/// 而膠囊更新是其中之一。<b>本服務讀那個變數的原始值。</b>
/// </para>
///
/// <para>
/// <b>而位元不命名。</b><c>EFI_OS_INDICATIONS_*</c> 是一組常數
/// （我知道其中幾個），而<b>本專案沒有任何地方收錄那張表</b>——
/// 與 FIT 的 Key Manifest 編號、TPM 的 EV_* 型別名、EFI 變數的屬性位元、
/// TPM_PT_PERMANENT 的編號是同一個判斷。
/// <b>所以這裡只說「值是多少」，不說「哪一個位元代表膠囊」。</b>
/// </para>
///
/// <para>
/// <b>不可做的一半：復原區的辨識——而理由要說清楚。</b>
/// UEFI PI 規格裡<b>「復原區」不是一個有固定 GUID 的東西</b>：
/// 它是<b>平台慣例</b>——有些平台的復原 FV 是一個特定的 GUID
/// （而各家不同），有些靠 FVB 的特定位置。
/// 既有的 <see cref="UefiFv"/> 解的是<b>通用 FV</b>，
/// <b>它不知道哪一個是復原區</b>，而本專案沒有一張「平台慣例表」可以依據。
/// </para>
///
/// <para>
/// <b>所以本服務不辨識復原區，而它把那件事說出來。</b>
/// 這與 <c>ec.boundary</c>、<c>tpm.clear.boundary</c> 是同一個形狀：
/// <b>一個沒說出口的界線，會被讀成「還沒做」而不是「沒有依據可做」。</b>
/// 而這裡的措辭特別重要——<b>它要說「缺的是一張表」，不是「缺的是實作」</b>，
/// 否則下一個人會以為只要寫程式就好。
/// </para>
/// </summary>
public static class FirmwareRecoveryFactsService
{
    private const string Category = "韌體";
    private const string Source = "UEFI 變數 OsIndicationsSupported（唯讀）";

    public const string RecoveryKey = "bt.recovery";

    /// <summary>
    /// 韌體膠囊更新的支援宣告。
    /// <c>null</c> 代表讀不到；<c>0</c> 代表讀到了而值是 0（**兩者不同**）。
    /// </summary>
    public sealed record RecoveryInput(uint? OsIndicationsSupported, IReadOnlyList<string> FvGuids);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, RecoveryInput? input)
    {
        if (input is null)
            return
            [
                new HardwareFact(RecoveryKey, Category, "韌體復原路徑", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                    "沒有讀取結果——本服務不自己去問韌體"
                    + "（那條路徑有它自己的可及性事實：uefi.secure_boot／uefi.setup_mode／bt.ibb）"),
            ];

        string capsule = input.OsIndicationsSupported is { } v
            ? $"OsIndicationsSupported ＝ 0x{v:X16}"
            : "**OsIndicationsSupported 讀不到**";

        return
        [
            new HardwareFact(RecoveryKey, Category, "韌體復原路徑",
                capsule + $"；映像內 {input.FvGuids.Count} 個 FV（**未辨識復原區**）",
                "", Source, FactTrustLevel.Reported, false, at,
                // **讀不到時不帶數字。**這是那個犯了五次的錯誤的同型處置——
                // 「值為 0」與「沒有值」是不同的斷言。
                input.OsIndicationsSupported is { } n ? n : null,
                input.OsIndicationsSupported is null
                    ? FactAvailability.NotSupported : FactAvailability.Present,
                (input.OsIndicationsSupported is null
                    ? "**變數讀不到（可能是 Legacy 開機、未提權，或平台未提供）。**"
                      + "**「讀不到」與「值為 0」是不同的斷言**——"
                      + "後者代表平台明確回報「不支援任何 OS 指示」。"
                    : "**位元不命名，只列原始值。**"
                      + "EFI_OS_INDICATIONS_* 是一組常數（我知道其中幾個），"
                      + "而**本專案沒有任何地方收錄那張表**——"
                      + "與 FIT 的 Key Manifest 編號、TPM 的 EV_* 型別名、"
                      + "EFI 變數的屬性位元、TPM_PT_PERMANENT 的編號是同一個判斷："
                      + "**填錯的位元會讓一個不存在的支援看起來存在。**"
                      + "要補上位元名，需要先對準 UEFI Spec 的 EFI_OS_INDICATIONS_* 表出處"
                      + "（見 docs/ULTIMATE-1000/UNSOURCED-CONSTANTS.md）")
                + "。**而復原區那一半本專案不做，理由不是「還沒寫」而是「沒有依據」**："
                + "UEFI PI 規格裡**「復原區」不是一個有固定 GUID 的東西**——"
                + "它是**平台慣例**（有些平台的復原 FV 是一個特定的 GUID，而各家不同；"
                + "有些靠 FVB 的特定位置）。既有的 UefiFv 解的是**通用 FV**，"
                + "**它不知道哪一個是復原區**，而本專案沒有一張「平台慣例表」可以依據。"
                + "**所以這裡說的是「缺的是一張表」，不是「缺的是實作」**——"
                + "否則下一個人會以為只要寫程式就好"),
        ];
    }
}
