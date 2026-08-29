using System.IO;
using System.Text;

namespace XinSpect;

/// <summary>
/// 未預期例外的落地紀錄：把當機／異常的來源、型別、訊息與堆疊寫進
/// <c>%APPDATA%\XinSpect\crash.log</c>，供事後回報與排查。
/// </summary>
/// <remarks>
/// 這一層存在的理由與感測讀值的合理性閘門相同——<b>不隱瞞</b>。
/// 全域例外處理器很容易寫成「吞掉例外讓畫面看起來沒事」，那等於把壞掉的狀態偽裝成正常；
/// 因此本專案的作法是：先落地一筆可追查的紀錄，再讓使用者看到「剛才有東西壞了、紀錄在哪裡」，
/// 而不是無聲無息地繼續跑。
///
/// 寫檔本身也不得成為新的當機來源：所有 I/O 都以 try/catch 包住，失敗就放棄這一筆。
/// </remarks>
public static class CrashLog
{
    /// <summary>紀錄檔的大小上限；超過時只保留後半，避免長年累積吃掉磁碟。</summary>
    public const long MaxBytes = 512 * 1024;

    private static string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");

    /// <summary>紀錄檔所在資料夾（單元測試可改指到暫存目錄）。</summary>
    public static string Folder
    {
        get => _folder;
        set => _folder = value;
    }

    public static string FilePath => Path.Combine(_folder, "crash.log");

    /// <summary>本次執行期間已記錄的例外筆數（供畫面判斷是否要提示）。</summary>
    public static int Count { get; private set; }

    /// <summary>
    /// 組出一筆紀錄的文字。與寫檔分離，才能在不觸碰檔案系統的情況下驗證格式。
    /// </summary>
    /// <param name="source">例外的來源標籤（如「UI 執行緒」、「背景執行緒」）。</param>
    /// <param name="ex">例外本體；為 null 時仍會產生一筆可辨識的紀錄。</param>
    /// <param name="localTime">發生時間（當地時間）。</param>
    public static string Format(string source, Exception? ex, DateTime localTime)
    {
        var sb = new StringBuilder();
        sb.Append("──── ").Append(localTime.ToString("yyyy-MM-dd HH:mm:ss"))
          .Append("　").Append(string.IsNullOrWhiteSpace(source) ? "未標示來源" : source)
          .Append("　版本 ").Append(AppInfo.Version)
          .Append("　").Append(Environment.OSVersion.VersionString)
          .Append(Environment.Is64BitProcess ? "　64 位元行程" : "　32 位元行程")
          .AppendLine();

        if (ex is null)
        {
            sb.AppendLine("（沒有取得例外物件，僅知發生了未處理的錯誤）");
            return sb.ToString();
        }

        // 內層例外一併展開：真正的原因通常在最裡面那一層
        for (var e = ex; e is not null; e = e.InnerException)
        {
            sb.Append(e == ex ? "" : "└ 內層：").Append(e.GetType().FullName)
              .Append("：").AppendLine(e.Message);
            if (!string.IsNullOrWhiteSpace(e.StackTrace)) sb.AppendLine(e.StackTrace!.TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>
    /// 記錄一筆例外。回傳是否成功寫入檔案——寫不進去（權限、磁碟滿）時回 false，
    /// 呼叫端據此改口說「這次沒能存下紀錄」，不假裝已經留存。
    /// </summary>
    public static bool Write(string source, Exception? ex)
    {
        Count++;
        try
        {
            Directory.CreateDirectory(_folder);
            Trim();
            File.AppendAllText(FilePath, Format(source, ex, DateTime.Now), new UTF8Encoding(true));
            return true;
        }
        catch { return false; }
    }

    // 超過上限時只留後半段（保留較近期的紀錄）。失敗時不影響本次寫入。
    private static void Trim()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length <= MaxBytes) return;
            var lines = File.ReadAllLines(FilePath);
            File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2), new UTF8Encoding(true));
        }
        catch { }
    }
}
