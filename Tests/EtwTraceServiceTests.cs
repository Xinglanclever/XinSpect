using System.IO;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// ETW 軌跡落地的周邊邏輯：檔名組裝／解析、檔頭驗證、容量控管與刪除的誠實回報。
/// </summary>
/// <remarks>
/// 這裡刻意<b>不</b>建立真正的 ETW 工作階段：那需要系統管理員權限，也會在測試機上留下系統層資源。
/// <see cref="EtwTraceService.CreateWritableSession"/> 的產物由實機驗證（見 Service 類別註解），
/// 單元測試只涵蓋可純函式化、且與權限無關的部分。
/// 每個測試都用自己的暫存夾，不碰使用者真正的 %LOCALAPPDATA%。
/// </remarks>
public class EtwTraceServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "XinSpectEtlTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 暫存夾清不掉不算失敗 */ }
    }

    private EtwTraceService New(long max = EtwTraceService.DefaultMaxBytes) => new(_dir, max);

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private string MakeFile(string name, int size) => Write(name, new byte[size]);

    // ── 檔名組裝 ──────────────────────────────────────────────────────────

    [Fact]
    public void FileNameFor_UsesKindTokenAndTimestamp()
    {
        var t = new DateTime(2026, 9, 12, 10, 10, 10);
        Assert.Equal("dpc_20260912_101010.etl", EtwTraceService.FileNameFor(TraceKind.Dpc, t));
        Assert.Equal("frametime_20260912_101010.etl", EtwTraceService.FileNameFor(TraceKind.FrameTime, t));
        Assert.Equal("migration_20260912_101010.etl", EtwTraceService.FileNameFor(TraceKind.Migration, t));
    }

    [Fact]
    public void FileNameFor_ZeroPadsAllFields()
    {
        Assert.Equal("dpc_20260102_030405.etl",
            EtwTraceService.FileNameFor(TraceKind.Dpc, new DateTime(2026, 1, 2, 3, 4, 5)));
    }

    [Fact]
    public void PathFor_PlacesFileInConfiguredFolder()
    {
        var svc = New();
        var p = svc.PathFor(TraceKind.FrameTime, new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal(_dir, Path.GetDirectoryName(p));
        Assert.Equal("frametime_20260102_030405.etl", Path.GetFileName(p));
    }

    [Fact]
    public void Token_And_DisplayName_CoverEveryKind()
    {
        foreach (TraceKind kind in Enum.GetValues<TraceKind>())
        {
            Assert.False(string.IsNullOrEmpty(EtwTraceService.Token(kind)));
            Assert.False(string.IsNullOrEmpty(EtwTraceService.DisplayName(kind)));
        }
    }

    // ── 檔名解析 ──────────────────────────────────────────────────────────

    [Fact]
    public void TryParseName_RecoversKindAndTime()
    {
        Assert.True(EtwTraceService.TryParseName("frametime_20260102_030405.etl", out var kind, out var time));
        Assert.Equal(TraceKind.FrameTime, kind);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), time);
    }

    [Fact]
    public void TryParseName_AcceptsFullPath()
    {
        Assert.True(EtwTraceService.TryParseName(@"C:\some\where\dpc_20260912_101010.etl", out var kind, out var time));
        Assert.Equal(TraceKind.Dpc, kind);
        Assert.Equal(new DateTime(2026, 9, 12, 10, 10, 10), time);
    }

    [Fact]
    public void TryParseName_ExtensionIsCaseInsensitive()
    {
        Assert.True(EtwTraceService.TryParseName("migration_20260912_101010.ETL", out var kind, out _));
        Assert.Equal(TraceKind.Migration, kind);
    }

    [Theory]
    [InlineData("dpc_20260912_101010.txt")]      // 副檔名不對
    [InlineData("unknown_20260912_101010.etl")]  // 類別不認得
    [InlineData("dpc_not-a-time.etl")]           // 時間戳不是時間
    [InlineData("dpc_20260912.etl")]             // 時間戳缺一半
    [InlineData("dpc_20261301_000000.etl")]      // 月份 13 不存在
    [InlineData("20260912_101010.etl")]          // 沒有類別
    [InlineData("dpc_20260912_101010_extra.etl")]// 尾巴多東西
    [InlineData("dpc_.etl")]                     // 空時間戳
    [InlineData("")]
    public void TryParseName_RejectsMalformed(string name)
        => Assert.False(EtwTraceService.TryParseName(name, out _, out _));

    [Fact]
    public void TryParseName_RejectsNull()
        => Assert.False(EtwTraceService.TryParseName(null, out _, out _));

    [Fact]
    public void FileNameFor_ThenTryParseName_RoundTrips()
    {
        var t = new DateTime(2026, 12, 31, 23, 59, 58);
        foreach (TraceKind kind in Enum.GetValues<TraceKind>())
        {
            Assert.True(EtwTraceService.TryParseName(EtwTraceService.FileNameFor(kind, t), out var k, out var rt));
            Assert.Equal(kind, k);
            Assert.Equal(t, rt);
        }
    }

    // ── 檔頭驗證 ──────────────────────────────────────────────────────────

    [Fact]
    public void IsValidEtl_AcceptsSessionHeaderMagic()
    {
        // 工作階段式 ETW 檔頭：00 00 01 00（小端 0x00010000）
        var p = Write("dpc_20260912_101010.etl", [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00]);
        Assert.True(EtwTraceService.IsValidEtl(p));
    }

    [Fact]
    public void IsValidEtl_AcceptsBareFourByteMagic()
    {
        var p = Write("dpc_20260912_101010.etl", [0x00, 0x00, 0x01, 0x00]);
        Assert.True(EtwTraceService.IsValidEtl(p));
    }

    [Fact]
    public void IsValidEtl_AcceptsRealWorldStructuralHeader()
    {
        // 實測系統 .etl：第一個緩衝區大小因來源而異（此處 0x1000），
        // 但 offset 4..7 與 8..11 同值、且版本次號為 2。不得只認 offset 0。
        var bytes = new byte[16];
        bytes[0] = 0x00; bytes[1] = 0x10;                 // BufferSize = 0x00001000，不是魔數
        bytes[4] = 0x50; bytes[5] = 0x02;                 // Version：次號 = 2
        bytes[8] = 0x50; bytes[9] = 0x02;                 // 與前者同值
        var p = Write("frametime_20260912_101010.etl", bytes);
        Assert.True(EtwTraceService.IsValidEtl(p));
    }

    [Fact]
    public void IsValidEtl_RejectsEmptyFile()
    {
        var p = Write("dpc_20260912_101010.etl", []);
        Assert.False(EtwTraceService.IsValidEtl(p));
    }

    [Fact]
    public void IsValidEtl_RejectsShortFile()
    {
        var p = Write("dpc_20260912_101010.etl", [0x00, 0x00, 0x01]);   // 只有 3 位元組
        Assert.False(EtwTraceService.IsValidEtl(p));
    }

    [Fact]
    public void IsValidEtl_RejectsJunkBytes()
    {
        var p = Write("dpc_20260912_101010.etl",
            [0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0]);
        Assert.False(EtwTraceService.IsValidEtl(p));
    }

    [Fact]
    public void IsValidEtl_RejectsAllZeroHeader()
    {
        var p = Write("dpc_20260912_101010.etl", new byte[64]);
        Assert.False(EtwTraceService.IsValidEtl(p));
    }

    [Fact]
    public void IsValidEtl_RejectsMissingFile()
        => Assert.False(EtwTraceService.IsValidEtl(Path.Combine(_dir, "does-not-exist.etl")));

    [Fact]
    public void IsValidEtl_RejectsNullOrEmptyPath()
    {
        Assert.False(EtwTraceService.IsValidEtl(null));
        Assert.False(EtwTraceService.IsValidEtl(""));
    }

    // ── 列舉 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ListTraces_MissingFolder_ReturnsEmpty()
    {
        var svc = New();
        Assert.False(Directory.Exists(_dir));
        Assert.Empty(svc.ListTraces());
        Assert.Equal(0, svc.TotalBytes);
    }

    [Fact]
    public void ListTraces_IgnoresForeignAndMisnamedFiles()
    {
        var svc = New();
        MakeFile("notes.txt", 10);
        MakeFile("random.etl", 10);                       // .etl 但命名不符
        MakeFile("dpc_20260101_000000.etl", 10);
        var list = svc.ListTraces();
        Assert.Single(list);
        Assert.Equal(TraceKind.Dpc, list[0].Kind);
        Assert.Equal("dpc_20260101_000000.etl", list[0].FileName);
    }

    [Fact]
    public void ListTraces_IsNewestFirstAndSumsBytes()
    {
        var svc = New();
        MakeFile("dpc_20260101_000000.etl", 100);
        MakeFile("frametime_20260301_000000.etl", 200);
        MakeFile("migration_20260201_000000.etl", 300);

        var list = svc.ListTraces();
        Assert.Equal(3, list.Count);
        Assert.Equal("frametime_20260301_000000.etl", list[0].FileName);   // 3 月最前
        Assert.Equal("migration_20260201_000000.etl", list[1].FileName);
        Assert.Equal("dpc_20260101_000000.etl", list[2].FileName);
        Assert.Equal(600, svc.TotalBytes);
    }

    [Fact]
    public void TraceInfo_ReportsSizeTextAndLabel()
    {
        var svc = New();
        MakeFile("dpc_20260101_000000.etl", 2048);
        var info = svc.ListTraces()[0];
        Assert.Equal(2048, info.SizeBytes);
        Assert.Equal("2 KB", info.SizeText);
        Assert.Contains("2026-01-01", info.Label);
    }

    [Fact]
    public void EnsureFolder_CreatesAndReturnsFolder()
    {
        var svc = New();
        var f = svc.EnsureFolder();
        Assert.True(Directory.Exists(f));
        Assert.Equal(_dir, f);
    }

    // ── 容量控管 ──────────────────────────────────────────────────────────

    [Fact]
    public void Prune_UnderLimit_DeletesNothing()
    {
        var svc = New();
        MakeFile("dpc_20260101_000000.etl", 100);
        Assert.Equal(0, svc.Prune(1000));
        Assert.Single(svc.ListTraces());
    }

    [Fact]
    public void Prune_ZeroOrNegativeLimit_DoesNothing()
    {
        var svc = New();
        MakeFile("dpc_20260101_000000.etl", 100);
        Assert.Equal(0, svc.Prune(0));
        Assert.Equal(0, svc.Prune(-5));
        Assert.Single(svc.ListTraces());
    }

    [Fact]
    public void Prune_OverLimit_DeletesOldestAndKeepsNewest()
    {
        var svc = New();
        MakeFile("dpc_20260101_000000.etl", 100);   // 最舊
        MakeFile("dpc_20260102_000000.etl", 100);
        MakeFile("dpc_20260103_000000.etl", 100);   // 最新
        Assert.Equal(300, svc.TotalBytes);

        int deleted = svc.Prune(250);               // 需刪 1 個才降到 ≤ 250

        Assert.Equal(1, deleted);
        var left = svc.ListTraces();
        Assert.Equal(2, left.Count);
        Assert.Equal("dpc_20260103_000000.etl", left[0].FileName);
        Assert.False(File.Exists(Path.Combine(_dir, "dpc_20260101_000000.etl")));
        Assert.Equal(200, svc.TotalBytes);
    }

    [Fact]
    public void Prune_KeepsDeletingUntilUnderLimit()
    {
        var svc = New();
        for (int i = 1; i <= 5; i++) MakeFile($"dpc_2026010{i}_000000.etl", 100);
        int deleted = svc.Prune(120);               // 5×100=500 → 只能留 1 個
        Assert.Equal(4, deleted);
        var left = svc.ListTraces();
        Assert.Single(left);
        Assert.Equal("dpc_20260105_000000.etl", left[0].FileName);
    }

    [Fact]
    public void Prune_StopsWhenOldestIsLocked()
    {
        var svc = New();
        var oldest = MakeFile("dpc_20260101_000000.etl", 100);
        MakeFile("dpc_20260102_000000.etl", 100);

        int deleted;
        using (var hold = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // 最舊的被佔用（例如 WPA 開著）：不得為了達標而回頭刪掉更新的檔
            deleted = svc.Prune(150);
        }

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(oldest));
        Assert.True(File.Exists(Path.Combine(_dir, "dpc_20260102_000000.etl")));
    }

    // ── 刪除 ──────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteTrace_RemovesFile()
    {
        var svc = New();
        var p = MakeFile("dpc_20260101_000000.etl", 10);
        Assert.True(svc.DeleteTrace(p));
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void DeleteTrace_MissingFile_IsIdempotentSuccess()
        => Assert.True(New().DeleteTrace(Path.Combine(_dir, "nope.etl")));

    [Fact]
    public void DeleteTrace_EmptyPath_Fails()
    {
        Assert.False(New().DeleteTrace(""));
        Assert.False(New().DeleteTrace(null));
    }

    [Fact]
    public void DeleteTrace_LockedFile_ReportsFailureWithoutThrowing()
    {
        var svc = New();
        var p = MakeFile("dpc_20260101_000000.etl", 10);
        using var hold = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(svc.DeleteTrace(p));       // 被佔用 → 回報失敗，不丟例外
        Assert.True(File.Exists(p));
    }

    // ── 大小文字 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1024 * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(-1, "—")]
    public void SizeText_FormatsBytes(long bytes, string expected)
        => Assert.Equal(expected, EtwTraceService.SizeText(bytes));
}
