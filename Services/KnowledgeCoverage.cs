using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 知識表的收錄率申報：每個手工知識庫收了幾條、覆蓋了規格裡的多少。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要申報收錄率：</b>本程式的知識表<b>沒有一張是完整目錄</b>。有的只收錄規格或登錄檔裡
/// 有據且確定看得懂的條目（手工子集），有的是上游資料的快照（社群維護、會過期、錯誤率不是零）。
/// 這個選擇是對的（猜的名字比沒有更糟），但它有一個副作用：<b>使用者會以為「查不到名字」代表裝置有問題</b>，
/// 而實際上只是知識表沒有那一格。
/// </para>
///
/// <para>
/// <b>第 286 輪把清冊補齊：</b>這一支先前只列了 8 張裡的 8 列，漏掉 SMART 屬性、BugCheck 停止代碼、
/// BugCheck 符號名三張——而它們的值會出現在畫面上（屬性名、藍屏原因）。清冊與
/// <see cref="BuiltinKnowledgeInventory"/>（內部盤點）現在由
/// <c>KnowledgeCoverageInventoryTests</c> 雙向對帳，任何一邊補表而另一邊沒補都會紅燈。
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
    /// 目前所有內建知識表的收錄狀況（張數不寫在這裡——寫死會漂）。
    /// </summary>
    /// <remarks>
    /// <b>數字是實測的，不是估的</b>：由 <c>KnowledgeCoverageTests</c> 從各表的實際內容數出來並比對，
    /// 表改了而這裡沒改會紅燈。分母只填得出來的：PCI 類別碼用 PCI-SIG 規格的基底類別定義數，
    /// Super I/O 與 OUI 的分母無法離線確認（廠商與登記數量持續變動），如實留 0。
    /// </remarks>
    public static IReadOnlyList<Table> Tables { get; } = Build();

    /// <summary>
    /// PCI 三張表的條數<strong>直接問 <see cref="PciKnowledge.Counts()"/></strong>（第 286 輪）。
    ///
    /// <para>
    /// <b>先前這裡是手寫的 20／20／15，而三個數字各多算了 1。</b>原本的守門用
    /// 「segment 裡出現幾個 <c>=&gt;</c> 減掉 default」來數 case，可是那一段是從
    /// <b>方法簽名</b>開始的——運算式主體的簽名本身帶一個 <c>=&gt;</c>
    /// （<c>VendorName(ushort id) =&gt; id switch</c>），於是被算成一個 case。
    /// 三個數字一起偏、守門也一起偏，所以一路是綠的；真正的 19／19／14 直到
    /// 與 <see cref="PciKnowledge.Counts()"/>（逐值列舉，第二種數法）對帳才露出來。
    /// </para>
    /// </summary>
    private static IReadOnlyList<Table> Build()
    {
        var pci = PciKnowledge.Counts();
        return
        [
        new("PCI 基底類別碼", pci.BaseClasses, "PCI-SIG 規格定義的 Base Class（0x00–0xFF 中有定義者）", 0,
            "回「未收錄」並附原始碼——不猜名字"),
        new("PCI 子類別碼", pci.SubClasses, "PCI-SIG 規格定義的 Subclass（只收錄有把握的子集）", 0,
            "回「未收錄」並附原始碼"),
        new("PCI 廠商 ID", pci.Vendors, "PCI-SIG 廠商登錄檔的知名廠商子集", 0,
            "回「Vendor 0xXXXX（未收錄）」——這是知識表沒收錄，不是裝置有問題"),
        // 第 286 輪改單位：先前寫的是「5 個家族」，那是實作分類而不是「查得到幾個名字」。
        // 這一節要回答的是後者，所以報晶片條數（四個家族表的總和），家族數寫在範圍欄。
        new("Super I/O 晶片名稱", SuperIoKnowledge.Count,
            "coreboot superiotool 的晶片 ID 表（ITE／NCT67xx／其他 Nuvoton／Fintek 四家族）", 0,
            "回 Unlisted——HWM 通路只報基址、不猜公式"),
        new("MAC OUI 廠商", 18, "IEEE OUI 登記的知名前綴子集", 0,
            "如實標未收錄，不推測廠商"),
        // 2026-10-27（第 276 輪）：這張表是**唯一分母可確認的一張**——CTA-861 定義的 VIC
        // 只有 1–127 與 193–219 兩個區間（128–192、220–255 保留），合計 154 個編號，
        // 本表全收。分母 154 是有出處的實數，不是估的。
        new("CTA VIC 時序", 154, "CTA-861 定義的 VIC 1–127 與 193–219（128–192、220–255 為保留）", 154,
            "回「VIC n（知識表未收錄）」並附原始編號——不猜時序"),
        // 2026-10-27（第 281 輪）：這兩張的條數**直接取自執行期的資料檔**，不是手抄——
        // 資料檔換了版本，申報跟著動，不會出現「文件說 21,619 條、程式裡是 22,000 條」。
        // 分母留 0：pci.ids 每季都在長，本程式無法離線確認「全世界有幾個 PCI 廠商」。
        new("PCI 廠商 ID（登錄檔快照）", PciIdDatabase.VendorCount,
            $"pci.ids {PciIdDatabase.SnapshotVersion} 的廠商段（社群維護的登錄檔）", 0,
            "回「Vendor 0xXXXX（未收錄）」——這份快照沒有那一格，不代表裝置有問題"),
        new("PCI 裝置 ID（登錄檔快照）", PciIdDatabase.DeviceCount,
            $"pci.ids {PciIdDatabase.SnapshotVersion} 的裝置段（社群維護的登錄檔）", 0,
            "回「Device 0xXXXX（未收錄）」——同上，是知識表的邊界不是裝置的缺陷"),
        // 2026-10-27（第 286 輪）：以下三張先前完全沒列進來，但它們的值都會出現在畫面上
        // （SMART 屬性名、藍屏停止代碼與符號名）。條數一律問表本身，不手抄。
        new("SMART 屬性（六家）", SmartAttributeKnowledge.TotalCount,
            "六家硬碟廠的 SMART 屬性列（raw／normalized 分欄，逐家出處）", 0,
            "顯示屬性 ID 與原始值——名稱未收錄不代表該屬性不存在，也不代表硬碟有問題"),
        new("BugCheck 停止代碼", BugCheck.Count,
            "常見藍屏停止代碼的白話原因提示（子集）", 0,
            "回「（未收錄的停止代碼）」並附原始代碼與上網查詢建議——不猜原因"),
        new("BugCheck 符號名", KernelDumpDecoder.TableCount(),
            "傾印解碼用的停止代碼符號名（Microsoft Learn「Bug Check Code Reference」）", 0,
            "顯示 0xXXXXXXXX 原始停止代碼——符號名未收錄不影響其他欄位的解讀"),
        ];
    }

    /// <summary>整體申報：這幾張表都是子集，查不到名字不代表裝置有問題。</summary>
    public static string Summary()
    {
        int total = Tables.Sum(t => t.Entries);
        return $"知識表共收錄 {total} 條（{Tables.Count} 張表）。"
             + "這些表的擔保等級不同：有的是本專案逐條核對的手工子集，"
             + "有的是上游登錄檔或廠商文件的快照（社群維護、會過期、錯誤率不是零）——逐表說明在本申報裡。"
             + "**查不到名字時，顯示的是原始代碼而不是猜出來的名字；那代表知識表沒收錄，不代表裝置有問題。**"
             + "各表收錄數：" + string.Join("、", Tables.Select(t => $"{t.Name} {t.Entries} 條")) + "。";
    }
}
