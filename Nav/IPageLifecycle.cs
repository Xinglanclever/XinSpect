namespace XinSpect;

/// <summary>
/// 檢視層的顯示／隱藏生命週期。外殼在切頁時呼叫，讓昂貴的頁面（歷史繪圖、瀏覽器、終端、
/// 風扇控制）在看不見時真正停工，而非常駐每秒消耗。
/// </summary>
/// <remarks>
/// <see cref="OnActivated"/> 必須是可重入的（idempotent）：感測引擎於背景晚到時，外殼會
/// 對當前頁再次重放啟用通知，實作不得假設只會被呼叫一次。
/// </remarks>
public interface IPageLifecycle
{
    /// <summary>本頁成為當前頁（或當前頁的相依資源就緒時重放）。</summary>
    void OnActivated();

    /// <summary>本頁離開顯示。應停止計時器、暫停動畫、釋放可重建的暫存。</summary>
    void OnDeactivated();
}
