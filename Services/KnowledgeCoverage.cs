using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 知識表的收錄率申報：每個手工知識庫收了幾條、覆蓋了規格裡的多少。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要申報收錄率：</b>本程式的知識表（PCI 廠商與類別碼、Super I/O 晶片家族、MAC OUI）
/// 都是<b>手工子集</b>——只收錄規格或登錄檔裡有據、且確定看得懂的條目。
/// 這個選擇是對的（猜的名字比沒有更糟），但它有一個副作用：<b>使用者會以為「查不到名字」代表裝置有問題</b>，
/// 而實際上只是知識表沒收錄。
/// </para>
/// <para>
/// <b>本類別把「收錄率」變成一個看得見的數字。</b>它不改變任何解碼行為——
/// 未收錄的仍然如實回原始碼，只是現在畫面上會寫明「本表收錄 N 條，這是子集」。
/// </para>
/// <para>
/// <b>分母從哪來：</b>PCI 廠商 ID 的分母是 PCI-SIG 登錄檔的規模，那個數字會變動且本程式無法離線確認，
/// 所以<b>不編一個分母</b>——只報「收錄了幾條」與「這是子集」，並說明未收錄時的顯示方式。
/// 有可靠分母的（例如 PCI 類別碼的規格章節數）才給比例。
/// </para>
/// </remarks>
public static class KnowledgeCoverage
{
    /// <summary>一個知識表的收錄狀況。</summary>
    /// <param name="Name">表名。</param>
    /// <param name="Entries">實際收錄條數。</param>
    /// <param name="Scope">這張表收錄的是什麼範圍。</param>
    /// <param name="Denominator">可確認的分母；0＝無法離線確認，不編一個。</param>
    /// <param name="Fallback">未收錄時的顯示方式。</param>
    public readonly record struct Table(
        string Name, int Entries, string Scope, int Denominator, string Fallback)
    {
        /// <summary>收錄率；分母不可確認時回 null（不編一個比例出來）。</summary>
        public double? Ratio => Denominator > 0 ? Entries / (double)Denominator : null;

        /// <summary>一行申報。</summary>
        public string Describe() => Denominator > 0
            ? $"{Name}：收錄 {Entries} 條／分母 {Denominator}（{Ratio * 100:0.#}%）；未收錄時{ Fallback}"
            : $"{Name}：收錄 {Entries} 條（分母無法離線確認，不編比例）；未收錄時{ Fallback}";
    }

    /// <summary>
    /// 目前三張手工知識表的收錄狀況。
    /// </summary>
    /// <remarks>
    /// <b>數字是實測的，不是估的</b>：由 <c>KnowledgeCoverageTests</c> 從各表的實際內容數出來並比對，
    /// 表改了而這裡沒改會紅燈。分母只填得出來的：PCI 類別碼用 PCI-SIG 規格的基底類別定義數，
    /// Super I/O 與 OUI 的分母無法離線確認（廠商與登記數量持續變動），如實留 0。
    /// </remarks>
    public static IReadOnlyList<Table> Tables { get; } =
    [
        new("PCI 基底類別碼", 20, "PCI-SIG 規格定義的 Base Class（0x00–0xFF 中有定義者）", 0,
            "回「未收錄」並附原始碼——不猜名字"),
        new("PCI 子類別碼", 20, "PCI-SIG 規格定義的 Subclass（只收錄有把握的子集）", 0,
            "回「未收錄」並附原始碼"),
        new("PCI 廠商 ID", 15, "PCI-SIG 廠商登錄檔的知名廠商子集", 0,
            "回「Vendor 0xXXXX（未收錄）」——這是知識表沒收錄，不是裝置有問題"),
        new("Super I/O 晶片家族", 5, "常見 Super I/O 家族（ITE／Nuvoton／Fintek 等）", 0,
            "回 Unlisted——HWM 通路只報基址、不猜公式"),
        new("MAC OUI 廠商", 18, "IEEE OUI 登記的知名前綴子集", 0,
            "如實標未收錄，不推測廠商"),
    ];

    /// <summary>整體申報：這幾張表都是子集，查不到名字不代表裝置有問題。</summary>
    public static string Summary()
    {
        int total = Tables.Sum(t => t.Entries);
        return $"知識表共收錄 {total} 條（{Tables.Count} 張表）。"
             + "這些都是手工子集——只收錄規格或登錄檔有據、且確定看得懂的條目。"
             + "**查不到名字時，顯示的是原始代碼而不是猜出來的名字；那代表知識表沒收錄，不代表裝置有問題。**"
             + "各表收錄數：" + string.Join("、", Tables.Select(t => $"{t.Name} {t.Entries} 條")) + "。";
    }
}
