using System.IO;
using System.Security.Cryptography;

namespace XinSpect;

/// <summary>
/// SHA256 雜湊小工具：供內嵌資源落地（超頻橋接程式、Fritz 原版）驗證「磁碟上的檔案就是內嵌正本」。
/// 先前以檔案大小比對判斷是否沿用已存在的副本——長度相同但內容遭竄改的檔案會被當成同版本，
/// 在 requireAdministrator 的行程裡執行；改用內容雜湊即關掉這條提權路徑。
/// </summary>
internal static class FileHash
{
    /// <summary>計算串流內容的 SHA256。不關閉串流；可_seek 時保留原位置，不影響呼叫端後續讀取。</summary>
    public static string Of(Stream s)
    {
        long pos = s.CanSeek ? s.Position : 0;
        try
        {
            if (s.CanSeek) s.Position = 0;
            return Convert.ToHexString(SHA256.HashData(s));
        }
        finally { if (s.CanSeek) s.Position = pos; }
    }

    /// <summary>計算檔案內容的 SHA256；讀不到（不存在／被占用／權限不足）回 null，呼叫端視同「驗證不過」。</summary>
    public static string? Of(string path)
    {
        try { using var fs = File.OpenRead(path); return Of(fs); }
        catch { return null; }
    }
}
