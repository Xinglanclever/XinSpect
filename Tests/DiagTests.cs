using System.IO;
using System.Text;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// <see cref="Diag"/> 的行為驗證：被吞掉的例外必須留下痕跡，且診斷紀錄本身不得成為新的失敗來源。
/// 需要碰檔案的測試改指到暫存資料夾；其餘一律關掉檔案落點，只驗記憶體與格式。
/// </summary>
[Collection(CrashFolderCollection.Name)]
public class DiagTests : IDisposable
{
    private readonly string _dir;
    private readonly string _savedFolder;
    private readonly bool _savedSink;

    public DiagTests()
    {
        _savedFolder = CrashLog.Folder;
        _savedSink = Diag.FileSinkEnabled;
        _dir = Path.Combine(Path.GetTempPath(), "XinSpectDiagTests_" + Guid.NewGuid().ToString("N"));
        CrashLog.Folder = _dir;
        Diag.FileSinkEnabled = false;   // 預設不碰檔案系統，個別測試自行打開
        Diag.Reset();
    }

    public void Dispose()
    {
        CrashLog.Folder = _savedFolder;
        Diag.FileSinkEnabled = _savedSink;
        Diag.Reset();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    // ── 記憶體紀錄 ──────────────────────────────────────────────────────────

    [Fact]
    public void Swallow_RecordsSiteTypeMessageAndConsequence()
    {
        Diag.Swallow("設定載入", new InvalidOperationException("JSON 壞了"), "本次沿用預設值");

        var e = Assert.Single(Diag.Entries);
        Assert.Equal("設定載入", e.Site);
        Assert.Equal(nameof(InvalidOperationException), e.Type);
        Assert.Equal("JSON 壞了", e.Message);
        Assert.Equal("本次沿用預設值", e.Consequence);
    }

    [Fact]
    public void Swallow_KeepsChronologicalOrderOldestFirst()
    {
        Diag.Swallow("A", new Exception("第一"));
        Diag.Swallow("B", new Exception("第二"));

        var all = Diag.Entries;
        Assert.Equal(2, all.Count);
        Assert.Equal("第一", all[0].Message);
        Assert.Equal("第二", all[1].Message);
    }

    [Fact]
    public void Swallow_WithNullException_StillLeavesATrace()
    {
        // 只知道「失敗了」也要留一筆——比完全沒紀錄有用，但不能編一個例外型別出來
        Diag.Swallow("WMI 查詢", null, "此欄顯示 —");

        var e = Assert.Single(Diag.Entries);
        Assert.Contains("無例外物件", e.Type);
        Assert.Equal("未提供訊息", e.Message);
    }

    [Fact]
    public void Swallow_WithoutSite_SaysSo()
    {
        Diag.Swallow("   ", new Exception("x"));
        Assert.Equal("未標示位置", Assert.Single(Diag.Entries).Site);
    }

    [Fact]
    public void Swallow_WithoutConsequence_LeavesItEmptyRatherThanGuessing()
    {
        Diag.Swallow("感測讀取", new Exception("x"));
        Assert.Equal("", Assert.Single(Diag.Entries).Consequence);
    }

    [Fact]
    public void Swallow_IncrementsCumulativeCount()
    {
        Diag.Swallow("A", new Exception("1"));
        Diag.Swallow("B", new Exception("2"));
        Diag.Swallow("C", new Exception("3"));
        Assert.Equal(3, Diag.Count);
    }

    [Fact]
    public void Ring_CapsAtMaxEntriesButCountKeepsCounting()
    {
        for (int i = 0; i < Diag.MaxEntries + 25; i++) Diag.Swallow("壓力", new Exception("第 " + i + " 筆"));

        Assert.Equal(Diag.MaxEntries, Diag.Entries.Count);
        Assert.Equal(Diag.MaxEntries + 25, Diag.Count);
        // 丟棄的是最舊的，最新一筆必定還在
        Assert.Equal("第 " + (Diag.MaxEntries + 24) + " 筆", Diag.Entries[^1].Message);
        Assert.DoesNotContain(Diag.Entries, e => e.Message == "第 0 筆");
    }

    [Fact]
    public void Entries_IsASnapshotSoCallersCanEnumerateSafely()
    {
        Diag.Swallow("A", new Exception("1"));
        var snapshot = Diag.Entries;
        Diag.Swallow("B", new Exception("2"));   // 快照取得之後再新增

        Assert.Single(snapshot);                 // 不應在列舉途中被改動
        Assert.Equal(2, Diag.Entries.Count);
    }

    [Fact]
    public void Reset_ClearsBothRingAndCount()
    {
        Diag.Swallow("A", new Exception("1"));
        Diag.Reset();
        Assert.Empty(Diag.Entries);
        Assert.Equal(0, Diag.Count);
    }

    // ── 文字格式 ────────────────────────────────────────────────────────────

    [Fact]
    public void Format_CarriesTimeSiteTypeMessageAndConsequence()
    {
        var e = new DiagEntry(new DateTime(2026, 8, 30, 9, 4, 5), "MSR 讀取", "Win32Exception", "存取被拒", "此欄顯示 —");
        string s = Diag.Format(e);

        Assert.Contains("2026-08-30 09:04:05", s);
        Assert.Contains("MSR 讀取", s);
        Assert.Contains("Win32Exception", s);
        Assert.Contains("存取被拒", s);
        Assert.Contains("後果：此欄顯示 —", s);
    }

    [Fact]
    public void Format_OmitsConsequenceSectionWhenUnannotated()
    {
        var e = new DiagEntry(DateTime.Now, "WMI 查詢", "ManagementException", "無效類別", "");
        Assert.DoesNotContain("後果", Diag.Format(e));
    }

    [Fact]
    public void Format_IsSingleLineSoTheLogStaysGreppable()
    {
        var e = new DiagEntry(DateTime.Now, "感測讀取", "IOException", "裝置未就緒", "溫度顯示 —");
        string s = Diag.Format(e);
        Assert.DoesNotContain('\n', s);
        Assert.DoesNotContain('\r', s);
    }

    [Fact]
    public void OneLine_ShowsSiteTypeMessageAndConsequenceForTheUi()
    {
        var e = new DiagEntry(DateTime.Now, "MSR 讀取", "Win32Exception", "存取被拒", "此欄顯示 —");
        Assert.Contains("MSR 讀取", e.OneLine);
        Assert.Contains("Win32Exception", e.OneLine);
        Assert.Contains("存取被拒", e.OneLine);
        Assert.Contains("→ 此欄顯示 —", e.OneLine);
    }

    [Fact]
    public void OneLine_WithoutConsequence_HasNoDanglingArrow()
    {
        var e = new DiagEntry(DateTime.Now, "WMI 查詢", "ManagementException", "無效類別", "");
        Assert.DoesNotContain("→", e.OneLine);
    }

    [Fact]
    public void TimeText_IsWallClockOnly()
    {
        var e = new DiagEntry(new DateTime(2026, 8, 30, 21, 7, 9), "A", "T", "M", "");
        Assert.Equal("21:07:09", e.TimeText);
    }

    // ── 檔案落點 ────────────────────────────────────────────────────────────

    [Fact]
    public void FilePath_SitsBesideTheCrashLog()
    {
        // 回報問題時一併附上，兩份紀錄不該散在不同地方
        Assert.Equal(Path.Combine(_dir, "diag.log"), Diag.FilePath);
        Assert.Equal(Path.GetDirectoryName(CrashLog.FilePath), Path.GetDirectoryName(Diag.FilePath));
    }

    [Fact]
    public void Swallow_WithSinkEnabled_CreatesFolderAndAppends()
    {
        Diag.FileSinkEnabled = true;
        Diag.Swallow("設定載入", new Exception("第一筆"));
        Diag.Swallow("感測讀取", new Exception("第二筆"));

        string all = File.ReadAllText(Diag.FilePath);
        Assert.Contains("第一筆", all);
        Assert.Contains("第二筆", all);
    }

    [Fact]
    public void Swallow_WritesUtf8BomSoChineseSurvivesInNotepad()
    {
        Diag.FileSinkEnabled = true;
        Diag.Swallow("設定載入", new Exception("中文訊息"));

        var raw = File.ReadAllBytes(Diag.FilePath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, raw.Take(3).ToArray());
    }

    [Fact]
    public void Swallow_WithSinkDisabled_TouchesNoFile()
    {
        Diag.FileSinkEnabled = false;
        Diag.Swallow("設定載入", new Exception("只留記憶體"));

        Assert.False(File.Exists(Diag.FilePath));
        Assert.Single(Diag.Entries);      // 記憶體那一份仍在
    }

    [Fact]
    public void Swallow_TrimsFileOnceItExceedsTheCap()
    {
        Directory.CreateDirectory(_dir);
        var filler = new StringBuilder();
        for (int i = 0; filler.Length < Diag.MaxBytes + 4096; i++) filler.AppendLine($"舊紀錄 {i} ────────────────");
        File.WriteAllText(Diag.FilePath, filler.ToString(), new UTF8Encoding(true));
        long before = new FileInfo(Diag.FilePath).Length;

        Diag.FileSinkEnabled = true;
        Diag.Swallow("設定載入", new Exception("最新一筆"));

        Assert.True(new FileInfo(Diag.FilePath).Length < before, "超過上限後應該瘦身");
        Assert.Contains("最新一筆", File.ReadAllText(Diag.FilePath));
    }

    [Fact]
    public void Swallow_NeverThrowsEvenWhenThePathIsUnusable()
    {
        // 資料夾指到一個「同名檔案已存在」的路徑：CreateDirectory 必定失敗。
        // 診斷管道自己炸掉，會讓原本只是降級的功能變成真的當掉。
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocker, "x");
        CrashLog.Folder = blocker;
        Diag.FileSinkEnabled = true;

        Diag.Swallow("設定載入", new Exception("寫不進去"));

        Assert.Single(Diag.Entries);      // 檔案寫不進去，記憶體那一份還是要留住
    }
}
