namespace XinSpect.Tests;

/// <summary>
/// 目前這一棵樹的專案測試數——README 測試徽章的單一來源。
///
/// <para>
/// 為什麼要有這一個檔案（而不是讓徽章去讀版本沿革）：
/// 沿革記的是「<b>那一版發佈時</b>的數字」，是歷史；徽章講的是「<b>現在</b>的數字」，
/// 是現在式。兩者綁在一起時，只要在兩次發版之間加了測試，徽章就會自動落後——
/// 這正是先前那筆「徽章寫 3195、同一份文件的沿革寫 3524」的成因。
/// 分開之後，加測試的人改這一個數字，守門測試把三份 README 釘住。
/// </para>
///
/// <para>
/// <b>不含未追蹤的本機探針。</b>探針檔（例如 <c>Tests/TempGalleryShot.cs</c>）會被 SDK 的
/// 預設 glob 一起編進測試組件，讓本機跑出來的數字比乾淨 checkout 多。對外的數字要用
/// 乾淨 checkout 也對得上的那一個。
/// </para>
///
/// <para>
/// 刻意放在測試專案而不是主程式：這是一個關於測試的數字，應用程式用不到它，
/// 放進主程式只會平白改動已發佈二進位的位元組數（README 的下載表就沒意義了）。
/// </para>
/// </summary>
internal static class TestSuiteBaseline
{
    /// <summary>
    /// 專案測試數。加測試就改這裡，<c>ChangelogTests</c> 會守著三份 README 的徽章。
    /// </summary>
    public const int ProjectTests = 3563;
}
