using System;
using System.Collections.Generic;
using System.Globalization;

namespace XinSpect;

/// <summary>
/// 階段二規格矩陣與實作的對帳申報（ULTIMATE-1000 階段二 P2-105，鍵 <c>fw.doc.reconcile</c>）。
///
/// <para>
/// <b>這一項回答「這份建置與階段二的規格對到哪裡」，不回答「階段二做完了沒」。</b>
/// 階段的規格是 <c>docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md</c> 裡那張 105 列的矩陣：
/// 每一列宣告一到多把鍵，而最後一欄寫出那幾把鍵現在的狀態
/// （<c>ok:</c> 同名實作、<c>alias:</c> 以既有鍵收錄、<c>todo:</c> 未實作）。
/// 這把鍵把那份對帳的<b>結果</b>收斂成一個使用者讀得到的數字。
/// </para>
///
/// <para>
/// <b>為什麼數字是編譯期常數、不是執行期讀文件：</b>發佈版是單一執行檔，身邊沒有
/// <c>docs/</c>。執行期去讀一份不存在的文件，會得到「0 項、0 把鍵」——
/// 而 0 會被畫面上讀成「全部都完成了」。那正是 <see cref="FactKeyCatalog"/> 為什麼
/// 是編譯期目錄的同一個教訓：<b>掃不到不得冒充沒有問題</b>。
/// 因此這裡的數字是<b>建置時對帳的結論</b>，而它由
/// <c>FirmwareDocReconcileTests</c> 逐列重算矩陣守住——文件改了而這裡沒改就紅燈。
/// </para>
///
/// <para>
/// <b>三態的界線：</b>這把鍵永遠是 <c>Present</c>——它陳述的是「本版建置時的對帳結果」，
/// 而那個結果一定存在（就算全部 todo 也是答案）。
/// 「還不是每項都做完」不是讀取失敗，所以不標三態、也不放進 <c>UnavailableReason</c>。
/// </para>
///
/// <para>
/// <b>它不評分、不給進度百分比。</b>105 項裡有 24 把鍵是「規格換了名字、實作早就在」的別名
/// （<b>這個數字必須等於 <see cref="AliasKeys"/>——它先前寫成 20，而當時常數已經是 24：
/// 一句話裡兩個數字，守門驗得到常數、驗不到註解</b>），
/// 把它們與新寫的實作混成一個百分比，會讓「改名」看起來像「完工」；
/// 三種計數分開列，讀的人自己判斷那個數字對他意味著什麼。
/// </para>
/// </summary>
public static class FirmwareDocReconcileFactsService
{
    /// <summary>本項的鍵。</summary>
    public const string Key = "fw.doc.reconcile";

    private const string Category = "韌體";
    private const string Source = "docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md 的階段二矩陣（建置時對帳）";

    /// <summary>階段二的規格項數（P2-001–P2-105）。</summary>
    public const int Items = 105;

    /// <summary>「實作鍵」欄裡 <c>ok:</c> 的鍵數——規格鍵與實作鍵同名。</summary>
    /// <remarks>
    /// 65 → 68（2026-10-18）：P2-082／P2-089／P2-090 三把鍵從 <c>todo</c> 收口成 <c>ok</c>；
    /// 68 → 74（同日，第 265 輪）：P2-099～P2-104 韌體風險陳述六把（<c>fw.risk.facts.*</c>）落地。
    /// 收口不是「做得出來」——那三把的結論是「平台不提供（沒有列舉 API）」，
    /// 而 <b>說清楚一條路不存在，也是做完這一項該有的結果</b>；
    /// 留著 todo 只會讓帳本繼續宣稱它們在排隊等實作。
    /// </remarks>
    public const int ImplementedKeys = 75;

    /// <summary>
    /// 以既有鍵別名收錄的鍵數（語意相同、名字不同）。
    /// <para>
    /// <b>註解的數字與這裡必須一致：</b>類別註解一度寫著 20 而這裡是 24，
    /// 而 <c>FirmwareDocReconcileTests</c> 逐列重算的是<b>矩陣</b>——它守得住常數，
    /// 守不住散文裡的數字。<b>會被人讀到的數字跟常數一樣要對。</b>
    /// </para>
    /// </summary>
    public const int AliasKeys = 24;

    /// <summary>尚未實作的鍵數。</summary>
    /// <remarks>25 → 22 → 16（2026-10-18；後者＝第 265 輪的六把風險陳述）。剩下的 16 把各自卡在什麼，見
    /// <c>docs/ULTIMATE-1000/STAGE-02-REMAINING-KEYS.md</c>。</remarks>
    public const int TodoKeys = 15;

    /// <summary>宣告鍵總數。<b>不是</b>項數——一列可以宣告多把鍵（例如 P2-014、P2-017）。</summary>
    public const int DeclaredKeys = ImplementedKeys + AliasKeys + TodoKeys;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) => [Fact(at)];

    /// <summary>對帳結果的一把事實。</summary>
    public static HardwareFact Fact(DateTimeOffset at)
    {
        string value = $"階段二規格 {Items} 項、宣告 {DeclaredKeys} 把鍵："
            + $"{ImplementedKeys} 把已實作、{AliasKeys} 把以既有鍵別名收錄、{TodoKeys} 把待辦。"
            + "這是**建置時**對矩陣的結論，不是執行期掃描文件——"
            + "發佈版身邊沒有 docs/，讀不到時回 0 會被誤讀成「全部完成」。";

        return new HardwareFact(Key, Category, "階段二規格與實作對帳", value, "把", Source,
            FactTrustLevel.Derived, false, at, ImplementedKeys, FactAvailability.Present);
    }

    /// <summary>一行摘要（給卡片標題用）；數字來源與 <see cref="Fact"/> 相同。</summary>
    public static string Headline => string.Create(CultureInfo.InvariantCulture,
        $"{ImplementedKeys}／{DeclaredKeys} 把鍵已實作（{AliasKeys} 把別名、{TodoKeys} 把待辦）");
}
