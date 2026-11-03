using System.Diagnostics;
using System.Management;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「WMI 卡住也不能讓啟動卡住」的防回歸閘。
/// </summary>
/// <remarks>
/// 2026-10-31 的現場：整機 WMI 的提供者行程卡死，曦覽在主視窗顯示<b>之前</b>同步查
/// <c>Win32_PhysicalMemory</c>，五份行程活著、視窗一個也沒出現，使用者看到的是「雙擊沒反應」。
/// 這裡把當時的兩條路徑釘住：查詢必須有上界，而且建構子必須馬上交回控制權。
/// </remarks>
public class WmiGuardTests
{
    [Fact]
    public void 逾時就放手並標記TimedOut()
    {
        var sw = Stopwatch.StartNew();
        var (value, timedOut) = WmiGuard.Probe<object>(
            () => { Thread.Sleep(TimeSpan.FromSeconds(5)); return new object(); }, timeoutMs: 150);
        sw.Stop();

        Assert.Null(value);
        Assert.True(timedOut);
        Assert.True(sw.ElapsedMilliseconds < 4_000, $"等了 {sw.ElapsedMilliseconds} ms，逾時保護沒有生效");
    }

    [Fact]
    public void 快速失敗的例外不算逾時()
    {
        var (value, timedOut) = WmiGuard.Probe<List<string>>(
            () => throw new ManagementException("沒有這個命名空間"), timeoutMs: 5_000);

        Assert.Null(value);
        // 權限不足、類別不存在是毫秒級的結果，不該被講成「WMI 沒回應」。
        Assert.False(timedOut);
    }

    [Fact]
    public void 執行緒集區被塞滿時_快速失敗仍不算逾時()
    {
        // 這一條抓的是 2026-11-03 全套測試滿載時看到的誤報：探針原本用 Task.Run，
        // 工作要先排進執行緒集區才開始跑；集區被其他測試佔滿時，一個「立刻丟例外」的假讀取
        // 在時間內根本沒被執行，於是被講成「WMI 無回應」——明明它沒有卡住，只是還沒輪到。
        // WmiGuard 改用 LongRunning 的專用執行緒後，探針一定是馬上開始，逾時才代表真的逾時。
        int hog = Math.Max(ThreadPool.ThreadCount, 4) * 4;
        using var release = new ManualResetEventSlim(false);
        for (int i = 0; i < hog; i++)
            ThreadPool.QueueUserWorkItem(_ => release.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            var (value, timedOut) = WmiGuard.Probe<List<string>>(
                () => throw new ManagementException("沒有這個命名空間"), timeoutMs: 1_500);

            Assert.Null(value);
            Assert.False(timedOut);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void 正常查詢回值且不標逾時()
    {
        var (value, timedOut) = WmiGuard.Probe(() => new List<int> { 1, 2, 3 }, timeoutMs: 5_000);

        Assert.NotNull(value);
        Assert.Equal(3, value!.Count);
        Assert.False(timedOut);
    }

    [Fact]
    public void 逾時之後遲到的例外被吃掉()
    {
        // 逾時放手後，被丢下的工作若自己丟例外就沒有人 await；不處理會在 GC 時變成
        // 未觀察例外（App 會寫進 CrashLog，最壞情況直接帶走行程）。
        WmiGuard.Probe<object>(
            () => { Thread.Sleep(200); throw new InvalidOperationException("遲到的例外"); },
            timeoutMs: 50);

        Thread.Sleep(600);   // 讓背景工作真的把例外丟出來；沒把行程帶走就算過
    }
}

/// <summary>DIMM 參考資料的啟動路徑：建構子不查 WMI，查詢有上界。</summary>
public class DimmReferenceStartupTests
{
    [Fact]
    public void 建構子不等WMI查詢()
    {
        var sw = Stopwatch.StartNew();
        _ = new DimmReferenceService();
        sw.Stop();

        // 建構子只把查詢丢到背景；WMI 再怎麼卡，這裡都必須在眨眼之間回來。
        Assert.True(sw.ElapsedMilliseconds < 1_000, $"建構子花了 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void WMI逾時時如實回報無回應()
    {
        var svc = new DimmReferenceService(autoDetect: false);
        svc.DetectAndLoad(() => { Thread.Sleep(TimeSpan.FromSeconds(5)); return null; }, timeoutMs: 150);

        Assert.Equal("—（WMI 無回應）", svc.DetectedType);
        Assert.False(svc.IsKnownType);
    }

    [Fact]
    public void WMI查詢失敗時維持原本的文案()
    {
        var svc = new DimmReferenceService(autoDetect: false);
        svc.DetectAndLoad(() => throw new ManagementException("查詢無效"), timeoutMs: 5_000);

        Assert.Equal("—（WMI 查詢失敗）", svc.DetectedType);
    }

    [Fact]
    public void 沒有回傳模組時如實說明()
    {
        var svc = new DimmReferenceService(autoDetect: false);
        svc.DetectAndLoad(() => [], timeoutMs: 5_000);

        Assert.Equal("—（WMI 未回傳記憶體模組）", svc.DetectedType);
    }

    [Fact]
    public void SMBIOS型別碼優先於CIM碼()
    {
        var svc = new DimmReferenceService(autoDetect: false);
        // SMBIOS 0x1A＝DDR4；CIM 20 若被誤用會說成 DDR——兩套編號不可混（見服務內的註解）。
        svc.DetectAndLoad(() => [new DimmReferenceService.DimmModuleRow(0x1A, 20, 8)], timeoutMs: 5_000);

        Assert.Equal("DDR4", svc.DetectedType);
        Assert.True(svc.IsKnownType);
        Assert.Equal("DIMM", svc.FormFactor);
        Assert.Equal("DIMM: 288 pin (key 偏左)", svc.PinCountText);
    }

    [Fact]
    public void 沒有SMBIOS碼時才用CIM碼()
    {
        var svc = new DimmReferenceService(autoDetect: false);
        svc.DetectAndLoad(() => [new DimmReferenceService.DimmModuleRow(0, 26, 12)], timeoutMs: 5_000);

        Assert.Equal("DDR4", svc.DetectedType);   // CIM 26＝DDR4
        Assert.Equal("SODIMM", svc.FormFactor);   // CIM 12＝SODIMM
    }

    [Fact]
    public void 未知型別碼如實顯示原始碼()
    {
        var svc = new DimmReferenceService(autoDetect: false);
        svc.DetectAndLoad(() => [new DimmReferenceService.DimmModuleRow(0, 99, 0)], timeoutMs: 5_000);

        Assert.Equal("—（未回報型別碼 0x63）", svc.DetectedType);
    }
}


/// <summary>
/// 磁碟容量：<see cref="System.IO.DriveInfo"/> 的 <c>IsReady</c>／<c>TotalSize</c> 和 WMI 一樣是
/// 「問裝置」的呼叫——壞碟、休眠中的機械碟、失聯的網路磁碟機都能讓它不回來，而這個服務的建構子
/// 就在主視窗顯示前的 UI 執行緒上。
/// </summary>
public class VolumeProbeStartupTests
{
    [Fact]
    public void 建構子不等逐碟探測()
    {
        var sw = Stopwatch.StartNew();
        _ = new VolumeService(_ => { Thread.Sleep(TimeSpan.FromSeconds(5)); return null; });
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1_000, $"建構子花了 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void 單顆碟卡住時整批仍在時限內回來()
    {
        var sw = Stopwatch.StartNew();
        var readings = VolumeService.Collect(d =>
        {
            if (d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return null;
            }
            return new VolumeService.VolumeReading(d.Name.TrimEnd('\\'), d.Name, "TEST", "NTFS", 100, 40);
        }, timeoutMs: 200);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 3_000, $"收集花了 {sw.ElapsedMilliseconds} ms，上界沒有生效");
        // 一定有的系統碟這一輪拿不到讀數（它卡住了），其餘顆照樣回來。
        Assert.DoesNotContain(readings, r => r.Name.Equals("C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 預設探測在真實磁碟上讀得到數字()
    {
        // 前面兩條都走注入的假探測；這一條走真正的那條路——收斂後仍要量得到系統碟。
        var svc = new VolumeService();
        svc.Refresh();

        Assert.True(svc.Volumes.Count > 0, "一顆固定磁碟都沒讀到，量法有問題");
        Assert.True(svc.TotalBytes > 0, "總量是 0，量法有問題");
        Assert.True(svc.FreeBytes > 0 && svc.FreeBytes <= svc.TotalBytes, "可用空間不合理");
    }

    [Fact]
    public void 某一輪逾時不會把已知的總量打成零()
    {
        bool hang = false;
        var svc = new VolumeService(d => hang
            ? Hang()
            : new VolumeService.VolumeReading(d.Name.TrimEnd('\\'), d.Name, "TEST", "NTFS", 1_000, 400));

        svc.Refresh();                       // 先建立基線（測試環境沒有 Application，建構子那輪不會套用）
        double before = svc.TotalBytes;
        Assert.True(before > 0, "這一輪完全沒有讀到讀數，後面的比較沒有意義");

        hang = true;
        svc.Refresh();                       // 全部卡住：這一輪仍然要有上界，且不能把既有數字清零

        Assert.Equal(before, svc.TotalBytes);

        static VolumeService.VolumeReading? Hang()
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return null;
        }
    }
}
