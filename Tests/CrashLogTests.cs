using System.IO;
using System.Text;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// <see cref="CrashLog"/> 的行為驗證：紀錄格式要能讓人回報問題（時間、來源、型別、訊息、堆疊、內層例外），
/// 而寫檔本身不得成為新的當機來源。所有測試都改指到暫存資料夾，不動使用者真正的 crash.log。
/// </summary>
public class CrashLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _saved;

    public CrashLogTests()
    {
        _saved = CrashLog.Folder;
        _dir = Path.Combine(Path.GetTempPath(), "XinSpectCrashTests_" + Guid.NewGuid().ToString("N"));
        CrashLog.Folder = _dir;
    }

    public void Dispose()
    {
        CrashLog.Folder = _saved;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static Exception Thrown(Action a)
    {
        try { a(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("預期會擲出例外");
    }

    // ── 紀錄格式 ────────────────────────────────────────────────────────────

    [Fact]
    public void Format_CarriesTimeSourceAndVersion()
    {
        string s = CrashLog.Format("UI 執行緒", new InvalidOperationException("壞了"), new DateTime(2026, 8, 29, 14, 5, 3));
        Assert.Contains("2026-08-29 14:05:03", s);
        Assert.Contains("UI 執行緒", s);
        Assert.Contains("版本 " + AppInfo.Version, s);
    }

    [Fact]
    public void Format_CarriesExceptionTypeAndMessage()
    {
        string s = CrashLog.Format("背景工作", new InvalidOperationException("讀不到感測器"), DateTime.Now);
        Assert.Contains("System.InvalidOperationException", s);
        Assert.Contains("讀不到感測器", s);
    }

    [Fact]
    public void Format_IncludesStackTraceWhenPresent()
    {
        var ex = Thrown(() => throw new ArgumentOutOfRangeException("index"));
        string s = CrashLog.Format("測試", ex, DateTime.Now);
        Assert.Contains(nameof(CrashLogTests), s);      // 堆疊中應看得到擲出的位置
    }

    [Fact]
    public void Format_UnwrapsInnerExceptions()
    {
        var inner = new FileNotFoundException("history.bin 不存在");
        var outer = new InvalidOperationException("查詢歷史失敗", inner);
        string s = CrashLog.Format("測試", outer, DateTime.Now);
        Assert.Contains("查詢歷史失敗", s);
        Assert.Contains("內層", s);
        Assert.Contains("history.bin 不存在", s);
    }

    [Fact]
    public void Format_HandlesNullExceptionHonestly()
    {
        string s = CrashLog.Format("背景執行緒（致命）", null, DateTime.Now);
        // 沒有例外物件時要說「沒取得」，不能編一個型別出來
        Assert.Contains("沒有取得例外物件", s);
        Assert.DoesNotContain("Exception：", s);
    }

    [Fact]
    public void Format_WithoutSource_SaysSo()
    {
        string s = CrashLog.Format("  ", new Exception("x"), DateTime.Now);
        Assert.Contains("未標示來源", s);
    }

    // ── 寫檔 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_CreatesFolderAndAppendsEntry()
    {
        Assert.True(CrashLog.Write("測試", new Exception("第一筆")));
        Assert.True(File.Exists(CrashLog.FilePath));
        Assert.Contains("第一筆", File.ReadAllText(CrashLog.FilePath));
    }

    [Fact]
    public void Write_AppendsWithoutLosingEarlierEntries()
    {
        CrashLog.Write("測試", new Exception("第一筆"));
        CrashLog.Write("測試", new Exception("第二筆"));
        string all = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("第一筆", all);
        Assert.Contains("第二筆", all);
    }

    [Fact]
    public void Write_UsesUtf8BomSoChineseSurvivesInNotepad()
    {
        CrashLog.Write("測試", new Exception("中文訊息"));
        var raw = File.ReadAllBytes(CrashLog.FilePath);
        Assert.True(raw.Length > 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, raw.Take(3).ToArray());
    }

    [Fact]
    public void Write_IncrementsCount()
    {
        int before = CrashLog.Count;
        CrashLog.Write("測試", new Exception("x"));
        CrashLog.Write("測試", new Exception("y"));
        Assert.Equal(before + 2, CrashLog.Count);
    }

    [Fact]
    public void Write_TrimsFileOnceItExceedsTheCap()
    {
        Directory.CreateDirectory(_dir);
        // 先塞一份超過上限的舊紀錄
        var filler = new StringBuilder();
        for (int i = 0; filler.Length < CrashLog.MaxBytes + 4096; i++) filler.AppendLine($"舊紀錄 {i} ────────────────");
        File.WriteAllText(CrashLog.FilePath, filler.ToString(), new UTF8Encoding(true));
        long before = new FileInfo(CrashLog.FilePath).Length;

        CrashLog.Write("測試", new Exception("最新一筆"));

        long after = new FileInfo(CrashLog.FilePath).Length;
        Assert.True(after < before, "超過上限後應該瘦身");
        // 瘦身保留較近期的內容，且新寫入的一筆必定在裡面
        Assert.Contains("最新一筆", File.ReadAllText(CrashLog.FilePath));
    }

    [Fact]
    public void Write_ReturnsFalseInsteadOfThrowingWhenPathIsUnusable()
    {
        // 把資料夾指到一個「同名檔案已存在」的路徑：CreateDirectory 必定失敗
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocker, "x");
        CrashLog.Folder = blocker;

        Assert.False(CrashLog.Write("測試", new Exception("寫不進去")));
    }

    [Fact]
    public void FilePath_LivesUnderTheConfiguredFolder()
    {
        Assert.Equal(Path.Combine(_dir, "crash.log"), CrashLog.FilePath);
    }
}
