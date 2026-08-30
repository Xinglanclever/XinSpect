using System.ComponentModel;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// <see cref="DiagViewModel"/>：把降級紀錄搬上畫面的那一層。
/// </summary>
/// <remarks>
/// 這組測試的理由是「白記」這個具體問題：1.5.x 之前 <see cref="Diag"/> 一直在收紀錄，
/// 但沒有任何介面看得到它——使用者看到某欄是「—」時仍然無法分辨是這台機器沒有這個項目、
/// 驅動不給讀，還是程式自己有缺陷。這一層一旦順序反了、上限沒守住、或「沒有紀錄」被寫成
/// 「一切正常」，那條路就等於又斷掉，而畫面上看起來仍然正常。
///
/// 與 <see cref="DiagTests"/> 共用同一個序列化集合：<see cref="Diag"/> 是行程級靜態狀態，
/// 平行跑會互相看到對方的紀錄。
/// </remarks>
[Collection(CrashFolderCollection.Name)]
public class DiagViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _savedFolder;
    private readonly bool _savedSink;

    public DiagViewModelTests()
    {
        _savedFolder = CrashLog.Folder;
        _savedSink = Diag.FileSinkEnabled;
        _dir = Path.Combine(Path.GetTempPath(), "XinSpectDiagVmTests_" + Guid.NewGuid().ToString("N"));
        CrashLog.Folder = _dir;
        Diag.FileSinkEnabled = false;
        Diag.Reset();
    }

    public void Dispose()
    {
        CrashLog.Folder = _savedFolder;
        Diag.FileSinkEnabled = _savedSink;
        Diag.Reset();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    // ── 清單內容與順序 ────────────────────────────────────────────────────

    [Fact]
    public void 建構時就把既有紀錄拉進來_不必等第一拍心跳()
    {
        Diag.Swallow("感測讀取", new Exception("裝置未就緒"), "溫度顯示 —");

        var vm = new DiagViewModel();

        Assert.Single(vm.Recent);
        Assert.True(vm.HasEntries);
        Assert.Equal(1, vm.Count);
    }

    [Fact]
    public void 清單是新的在上_使用者由上往下就是由近而遠()
    {
        // Diag 自己是時間順序（舊的在前）；畫面必須反過來，否則要滑到底才看得到剛剛發生的事
        Diag.Swallow("A", new Exception("較早"));
        Diag.Swallow("B", new Exception("較晚"));

        var vm = new DiagViewModel();

        Assert.Equal("較晚", vm.Recent[0].Message);
        Assert.Equal("較早", vm.Recent[1].Message);
    }

    [Fact]
    public void 畫面只列前幾筆_但累計筆數照實說()
    {
        for (int i = 0; i < DiagViewModel.ShowMax + 20; i++) Diag.Swallow("壓力", new Exception("第 " + i + " 筆"));

        var vm = new DiagViewModel();

        Assert.Equal(DiagViewModel.ShowMax, vm.Recent.Count);
        Assert.Equal(DiagViewModel.ShowMax + 20, vm.Count);
        // 截掉的是最舊的：最新一筆一定在最上面
        Assert.Equal("第 " + (DiagViewModel.ShowMax + 19) + " 筆", vm.Recent[0].Message);
    }

    // ── 每秒心跳的更新條件 ────────────────────────────────────────────────

    [Fact]
    public void 筆數有變才重建_沒變時不發任何屬性變更()
    {
        var vm = new DiagViewModel();
        int notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;

        vm.RefreshIfChanged();
        vm.RefreshIfChanged();

        Assert.Equal(0, notifications);   // 心跳每秒都會叫一次，沒事就不該擾動繫結
    }

    [Fact]
    public void 有新紀錄時心跳會把它帶上畫面()
    {
        var vm = new DiagViewModel();
        Assert.Empty(vm.Recent);

        Diag.Swallow("MSR 讀取", new Exception("存取被拒"), "此欄顯示 —");
        vm.RefreshIfChanged();

        Assert.Equal("MSR 讀取", Assert.Single(vm.Recent).Site);
        Assert.True(vm.HasEntries);
    }

    [Fact]
    public void 重建後會發佈衍生屬性_否則畫面上的筆數與空狀態會定格()
    {
        var vm = new DiagViewModel();
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        Diag.Swallow("WMI 查詢", new Exception("無效類別"));
        vm.RefreshIfChanged();

        Assert.Contains(nameof(DiagViewModel.Count), seen);
        Assert.Contains(nameof(DiagViewModel.HasEntries), seen);
        Assert.Contains(nameof(DiagViewModel.SummaryText), seen);
    }

    // ── 說明文字 ──────────────────────────────────────────────────────────

    [Fact]
    public void 沒有紀錄時不得宣稱一切正常()
    {
        // 「目前 0 筆」很容易被讀成「已驗證無誤」。它只代表這次還沒有功能降級被記下來。
        var vm = new DiagViewModel();
        Assert.False(vm.HasEntries);
        Assert.Contains("還沒被用到", vm.SummaryText);
        Assert.Contains("不是「保證無誤」", vm.SummaryText);   // 明說出來，別讓「0 筆」自己去暗示
    }

    [Fact]
    public void 有紀錄時說明會交代累計與實際列出的筆數()
    {
        Diag.Swallow("A", new Exception("1"));
        Diag.Swallow("B", new Exception("2"));

        var vm = new DiagViewModel();

        Assert.Contains("累計 2 筆", vm.SummaryText);
        Assert.Contains("最近 2 筆", vm.SummaryText);
    }

    [Fact]
    public void 紀錄檔路徑與當機紀錄同一個資料夾()
        => Assert.Equal(Path.Combine(_dir, "diag.log"), new DiagViewModel().FilePath);

    // ── 清空 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 清空只清畫面那份_紀錄檔留著才有東西可以附給別人看()
    {
        Diag.FileSinkEnabled = true;
        Diag.Swallow("設定載入", new Exception("JSON 壞了"), "本次沿用預設值");
        Assert.True(File.Exists(Diag.FilePath));

        var vm = new DiagViewModel();
        vm.Clear();

        Assert.Empty(vm.Recent);
        Assert.Equal(0, vm.Count);
        Assert.True(File.Exists(Diag.FilePath), "diag.log 不該被一起刪掉");
        Assert.Contains("JSON 壞了", File.ReadAllText(Diag.FilePath));
        Assert.Contains("保留不動", vm.HintText);
    }

    [Fact]
    public void 沒有紀錄檔時按開啟會如實說明而不是靜靜沒反應()
    {
        var vm = new DiagViewModel();
        Assert.False(File.Exists(Diag.FilePath));

        vm.OpenFile();

        Assert.Contains("還沒有 diag.log", vm.HintText);
    }
}
