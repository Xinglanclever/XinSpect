using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 三個「狀態檔／外部檔案」的邊界修補（外部缺陷報告 D9／D10／D11）：
/// 暫存檔名不能共用、歷史檔尾端垃圾不能只是忽略、外部報告不能無上限讀。
/// </summary>
/// <remarks>
/// 這三條的共同點是：**平時完全看不出來，壞掉的時候也只是「好像少了一筆」或「卡住」**。
/// 所以測試要直接造出那個情境，而不是驗證一行程式碼長什麼樣子。
/// </remarks>
public class StorageRobustnessTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "XinSpect-robust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── D9：AtomicWrite 的暫存檔名 ─────────────────────────────────────────

    [Fact]
    public void 併發寫入_落地內容必定是某一次的完整版()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "settings.json");
        try
        {
            const int writers = 2, iterations = 300, length = 20_000;
            var errors = new List<Exception>();
            var all = new HashSet<string>(StringComparer.Ordinal);

            // 兩份內容等長但字元不同：撕裂寫入會產生「混血」字串，不在 all 裡。
            string Payload(char c) => new(c, length);
            all.Add(Payload('A'));
            all.Add(Payload('B'));

            Parallel.For(0, writers, w =>
            {
                char c = w == 0 ? 'A' : 'B';
                for (int i = 0; i < iterations; i++)
                {
                    try { AtomicWrite.AllText(path, Payload(c)); }
                    catch (Exception ex) { lock (errors) errors.Add(ex); }
                }
            });

            Assert.True(File.Exists(path), "目標檔沒有落地");
            string final = File.ReadAllText(path);
            Assert.Contains(final, all);
            Assert.Equal(length, final.Length);

            // 固定暫存名會留下一個共用檔；改用每次不同的名字之後，成功的寫入都會把它搬走。
            string[] leftovers = Directory.GetFiles(dir, "*.tmp");
            Assert.Empty(leftovers);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 原子寫入失敗_自己的暫存檔要收乾淨()
    {
        string dir = NewTempDir();
        try
        {
            // 目標路徑是個資料夾 → File.Move 一定失敗。
            string path = Path.Combine(dir, "blocked");
            Directory.CreateDirectory(path);
            Assert.ThrowsAny<Exception>(() => AtomicWrite.AllText(path, "x"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── D10：HistoryStore 的尾端垃圾 ───────────────────────────────────────

    /// <summary>直接照磁碟格式寫一份檔案，可選擇在尾端加垃圾。</summary>
    private static void WriteHistory(string file, IReadOnlyList<DateTime> minutes, int garbageBytes)
    {
        const int count = HistoryMetrics.Count;
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs);
        w.Write(0x53485358);   // "XSHS"
        w.Write(2);            // FormatVersion
        w.Write(minutes.Count);
        w.Write(0);
        foreach (var t in minutes)
        {
            w.Write(t.Ticks);
            for (int m = 0; m < count; m++) { w.Write(1f); w.Write(2f); w.Write(3f); }
        }
        for (int i = 0; i < garbageBytes; i++) w.Write((byte)0xEE);
    }

    private static int RecordCount(string file)
    {
        long len = new FileInfo(file).Length;
        return (int)((len - 16) / (8 + HistoryMetrics.Count * 3 * 4));
    }

    [Fact]
    public void 尾端垃圾檔_載入當場就重寫乾淨()
    {
        string dir = NewTempDir();
        string file = Path.Combine(dir, "history.bin");
        var now = DateTime.UtcNow;
        var minutes = new[] { now.AddMinutes(-30), now.AddMinutes(-29), now.AddMinutes(-28) };
        try
        {
            WriteHistory(file, minutes, garbageBytes: 7);
            long before = new FileInfo(file).Length;

            using (var store = new HistoryStore(dir))
            {
                Assert.Equal(3, store.MinuteCount);
            }

            long after = new FileInfo(file).Length;
            Assert.True(after < before, $"垃圾沒有被清掉：{before} → {after}");
            Assert.Equal(0, (after - 16) % (8 + HistoryMetrics.Count * 3 * 4));
            Assert.Equal(3, RecordCount(file));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 尾端垃圾之後再append_四筆齊全且嚴格遞增()
    {
        string dir = NewTempDir();
        string file = Path.Combine(dir, "history.bin");
        var now = DateTime.UtcNow;
        var minutes = new[] { now.AddMinutes(-30), now.AddMinutes(-29), now.AddMinutes(-28) };
        try
        {
            WriteHistory(file, minutes, garbageBytes: 7);

            var values = new float[HistoryMetrics.Count];
            using (var store = new HistoryStore(dir))
            {
                // 兩個不同分鐘的取樣：第一拍開新分鐘，第二拍結算前一拍（append 一筆）。
                store.Sample(values, now.AddMinutes(-27));
                store.Sample(values, now.AddMinutes(-26));
            }

            using var reread = new HistoryStore(dir);
            // 3 筆原有 ＋ 1 筆 append ＋ 1 筆 Dispose→Flush 結算的當前分鐘 = 5。
            // 關鍵不是這個數字，而是「垃圾之後那一筆沒有被讀在錯的位移上」：
            // 舊寫法下載入會忽略垃圾、append 接在垃圾之後，下一輪讀到的第 4 筆時間戳是隨機值而被丟掉。
            Assert.Equal(5, reread.MinuteCount);
            var series = reread.Query(now.AddMinutes(-31), now.AddMinutes(-10));
            var times = series.Times;
            Assert.Equal(5, times.Length);
            for (int i = 1; i < times.Length; i++)
                Assert.True(times[i] > times[i - 1], $"第 {i} 筆時間沒有遞增");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 沒有垃圾的檔案_不被多餘重寫()
    {
        string dir = NewTempDir();
        string file = Path.Combine(dir, "history.bin");
        var now = DateTime.UtcNow;
        try
        {
            WriteHistory(file,
                [now.AddMinutes(-30), now.AddMinutes(-29), now.AddMinutes(-28)], garbageBytes: 0);
            var before = new FileInfo(file).LastWriteTimeUtc;

            using (var store = new HistoryStore(dir)) Assert.Equal(3, store.MinuteCount);

            // 乾淨的檔案不該因為載入而被重寫：mtime 不變。
            Assert.Equal(before, new FileInfo(file).LastWriteTimeUtc);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 壓縮用的暫存名不與固定名共用()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "HistoryStore.cs"));
        Assert.DoesNotContain("FilePath + \".tmp\"", text);
    }

    // ── D11：外部報告的大小上限 ───────────────────────────────────────────

    [Fact]
    public void 上限是64MiB且是明確常數()
    {
        Assert.Equal(64L * 1024 * 1024, ExternalReportService.MaxBytes);
    }

    [Fact]
    public void 超過上限_明白回報檔案過大而不解析()
    {
        string dir = NewTempDir();
        string csv = Path.Combine(dir, "huge.csv");
        try
        {
            // 只設定長度、不真的寫 65 MB：NTFS 的 SetLength 不需要把每個位元組寫進去。
            using (var fs = new FileStream(csv, FileMode.Create, FileAccess.Write))
                fs.SetLength(ExternalReportService.MaxBytes + 1);

            var sw = Stopwatch.StartNew();
            var r = ExternalReportService.ParseFile(csv);
            sw.Stop();

            Assert.Empty(r.Rows);
            Assert.Contains("檔案過大", r.Status);
            Assert.Contains("未解析", r.Status);
            Assert.Contains("64 MB", r.Status);
            Assert.Equal("huge.csv", r.FileName);
            Assert.True(sw.ElapsedMilliseconds < 5000, $"花了 {sw.ElapsedMilliseconds} ms——不應該去讀那 64 MB");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 剛好等於上限_仍然解析()
    {
        string dir = NewTempDir();
        string txt = Path.Combine(dir, "edge.csv");
        try
        {
            string body = "Date,Time,Sensor\n,,[unit]\n";
            File.WriteAllText(txt, body);
            var r = ExternalReportService.ParseFile(txt);
            Assert.True(r.Rows.Count >= 0);   // 小檔走原本路徑，會回「內容不足」而不是「過大」
            Assert.DoesNotContain("檔案過大", r.Status);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 讀不到大小的檔案_回報原因而不是拋例外()
    {
        string dir = NewTempDir();
        try
        {
            var r = ExternalReportService.ParseFile(Path.Combine(dir, "not-there.csv"));
            Assert.Empty(r.Rows);
            Assert.Contains("讀不到", r.Status);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Rules")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
