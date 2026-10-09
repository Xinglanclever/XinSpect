namespace XinSpect;

/// <summary>
/// 服務接線判定的顯式登記表——「做了但刻意不接線」必須是寫下來的決定，不是沉默的遺忘。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-10 的實測（主綱 P1-1／P1-2）掃出 7 支生產引用為 0 的服務：其中 4 支是單純
/// 「做了沒接」，3 支的語意其實不該自動接（寫入路徑、使用者觸發量測、刻意不實作的上傳通路）。
/// 兩類混在一起的代價是：下一次掃孤兒時，沒人分得出「還沒修的缺陷」和「已經想清楚的決定」，
/// 於是清單永遠清不完。這一版起：<b>孤兒服務要么不存在，要么在這裡有名字與理由</b>；
/// 由 <c>Tests/ServiceOrphanGateTests.cs</c> 逐條核對，登記過時（真的接上了）也會紅燈。
/// </para>
/// </remarks>
public static class WiringDecisions
{
    /// <summary>
    /// 類別名 → 判定。<b>值必須說清楚「為什麼不接」與「什么时候才該接」</b>，
    /// 長度門檻與占位詞禁令由守門測試釘住。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Deferred =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AmdSmuService"] =
                "SMN 信箱的讀取協定本身就是 PCI 寫入（index 埠寫位址、data 埠讀值）——在統一寫入閘門（主綱 §5.3）" +
                "落地、能回答『誰在什麼時候寫了哪個暫存器』之前，這條路徑刻意不接進自動事實收集。接線時只接讀取面，" +
                "且 Intel 平台必須整組如實 NotApplicable。",
            ["LoopbackSignalService"] =
                "擷取層以 WASAPI loopback 取樣系統音訊輸出——這是一次主動捕獲，不適合放進啟動即跑的自動管線；" +
                "純數學部分已由金標測試釘死。要接的是使用者明確觸發的量測入口（音訊頁『量一次』按鈕），不是事實流水線。",
            ["CorpusUploadService"] =
                "貢獻包建構（WP48）只做遮蔽版序列化的骨架；上傳通路依資料主權原則（事實蒐集零網路）刻意不實作。" +
                "接線等於把『半成品的外傳』擺在畫面上，與本專案的隱私宣稱直接衝突——要動這一支需要使用者的產品決定。",
            ["FakeCapacityTestService"] =
                "假容量驗證是寫入操作（寫滿→讀回→刪檔），必須留在有同意閘門的互動路徑裡；" +
                "自動接進事實收集等於未經同意就往磁碟寫資料，違反本專案的同意閘門慣例（WP22/WP27 同款處理）。",
            ["LocalApiHandler"] =
                "本機 API 的請求處理是純函式且有完整單元測試；刻意不啟動的只有 HTTP 監聽殼——" +
                "開監聽＝新增一個本機攻擊面與資料外洩路徑，屬需要使用者點頭的產品決定，不是漏接線。" +
                "README 功能節已如實標「HTTP 監聽殼未自動啟動」；事實的頭less 出口由 CLI（--json）承擔，" +
                "重新啟動的條件＝使用者明確要 API 入口並同意 loopback 綁定與 token 政策。",
            // 註：Services/ExternalAcquisitionService.cs 是接縫檔（interface IExternalAcquisition＋
            // NullAcquisition），檔內沒有 *Service 類別，不在本表掃描範圍——它属于「實作未存在」
            // 的預留接縫，主綱 §5.11 能力矩陣那一輪再處置。
        };
}
