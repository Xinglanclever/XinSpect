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
/// <b>它不評分、不給進度百分比。</b>105 項裡有 20 把鍵是「規格換了名字、實作早就在」的別名，
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
    public const int ImplementedKeys = 65;

    /// <summary>以既有鍵別名收錄的鍵數（語意相同、名字不同）。</summary>
    public const int AliasKeys = 24;

    /// <summary>尚未實作的鍵數。</summary>
    public const int TodoKeys = 25;

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
