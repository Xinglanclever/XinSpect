using System.IO;

namespace XinSpect;

/// <summary>
/// 狀態檔（設定／記錄）的原子寫入：先寫同目錄的暫存檔，再以 <see cref="File.Move(string,string,bool)"/> 原子換名。
/// 直接 <c>WriteAllText</c> 到目標檔，行程在寫入中途結束（當機／斷電／被終止）會留下寫一半的檔案，
/// 下次載入失敗就整份回落預設值；暫存＋換名把這個窗口收斂成單一不會「寫一半」的操作。
/// 暫存檔殘留（寫完、未及換名就中斷）無害：下次寫入會原路覆寫它。
/// </summary>
internal static class AtomicWrite
{
    /// <summary>以 UTF-8（無 BOM）寫入，與 <see cref="File.WriteAllText(string,string?)"/> 的預設編碼一致。</summary>
    public static void AllText(string path, string contents)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
