using System.IO;

namespace XinSpect;

/// <summary>
/// 狀態檔（設定／記錄）的原子寫入：先寫同目錄的暫存檔，再以 <see cref="File.Move(string,string,bool)"/> 原子換名。
/// 直接 <c>WriteAllText</c> 到目標檔，行程在寫入中途結束（當機／斷電／被終止）會留下寫一半的檔案，
/// 下次載入失敗就整份回落預設值；暫存＋換名把這個窗口收斂成單一不會「寫一半」的操作。
/// 暫存檔殘留（寫完、未及換名就中斷）無害：它的名字含本次行程與序號，不會被下一次寫入覆寫掉，
/// 也不會被誤認成目標檔。
/// </summary>
internal static class AtomicWrite
{
    private static int _seq;

    /// <summary>
    /// 暫存檔名。<b>每次寫入都不同</b>——這不是為了美觀。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本專案支援多實例（<c>SettingsService</c> 的 <c>AllowMultiInstance</c>）。先前用固定的
    /// <c>path + ".tmp"</c>，兩個實例同時存同一個設定檔時會這樣：
    /// A 寫完 tmp → B 覆寫 tmp → A <c>Move</c> → <b>A 把 B 的半成品搬成正式檔</b>。
    /// </para>
    /// <para>
    /// 固定名的註解原本寫「殘留無害，下次寫入會原路覆寫它」——那句話對<b>殘留</b>成立，
    /// 對<b>併發</b>不成立。加上行程 ID 與遞增序號之後，兩個實例各寫各的，
    /// 換名這一步仍然是原子的，最後落地的一定是**某一次完整的寫入**。
    /// </para>
    /// </remarks>
    private static string TempPath(string path) =>
        $"{path}.{Environment.ProcessId}.{Interlocked.Increment(ref _seq)}.tmp";

    /// <summary>以 UTF-8（無 BOM）寫入，與 <see cref="File.WriteAllText(string,string?)"/> 的預設編碼一致。</summary>
    public static void AllText(string path, string contents)
    {
        string tmp = TempPath(path);
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 換名失敗（例如目標被別的行程鎖住）時，自己的暫存檔要自己收乾淨；
            // 留著會在下一次成功寫入後變成沒人認領的垃圾。
            try { File.Delete(tmp); } catch { /* 刪不掉就算了，不掩蓋原本的例外 */ }
            throw;
        }
    }
}
