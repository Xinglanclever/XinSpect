using System.Diagnostics;
using System.Text;
using System.Windows;

namespace XinSpect;

/// <summary>一鍵裝機的單一軟體項目（含 winget 套件 ID 與是否勾選）。</summary>
public sealed class WingetPackage : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Desc { get; init; }
    public bool Recommended { get; init; }

    private bool _selected;
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
}

/// <summary>一鍵裝機的軟體分類。</summary>
public sealed class WingetCategory
{
    public required string Name { get; init; }
    public required IReadOnlyList<WingetPackage> Packages { get; init; }
}

/// <summary>
/// 一鍵裝機：以 Windows 內建的套件管理器 winget 批次安裝使用者勾選的常用軟體。
/// 所有安裝均由官方 winget 來源下載（Microsoft / winget-pkgs），本程式不內含任何安裝檔；
/// 未安裝 winget（App Installer）時停用安裝並導向官方取得。
/// </summary>
public sealed class WingetService : ObservableObject
{
    public IReadOnlyList<WingetCategory> Categories { get; }

    private bool _available;
    public bool WingetAvailable { get => _available; private set { if (SetProperty(ref _available, value)) OnPropertyChanged(nameof(NotAvailable)); } }
    public bool NotAvailable => !_available;

    private bool _installing;
    public bool IsInstalling { get => _installing; private set { if (SetProperty(ref _installing, value)) OnPropertyChanged(nameof(CanInstall)); } }
    public bool CanInstall => _available && !_installing;

    private string _status = "正在偵測 winget…";
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }

    private string _log = "";
    public string Log { get => _log; private set => SetProperty(ref _log, value); }

    public WingetService()
    {
        Categories = BuildCatalog();
        // 任一套件勾選狀態變動時，更新已選數量顯示
        foreach (var p in Categories.SelectMany(c => c.Packages))
            p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WingetPackage.IsSelected)) OnPropertyChanged(nameof(SelectedCount)); };
        _ = DetectAsync();
    }

    // __WINGET_PLACEHOLDER__

    /// <summary>已勾選的套件總數。</summary>
    public int SelectedCount => Categories.SelectMany(c => c.Packages).Count(p => p.IsSelected);

    /// <summary>勾選所有標記為推薦的套件。</summary>
    public void SelectRecommended()
    {
        foreach (var p in Categories.SelectMany(c => c.Packages)) p.IsSelected = p.Recommended;
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>清除所有勾選。</summary>
    public void ClearSelection()
    {
        foreach (var p in Categories.SelectMany(c => c.Packages)) p.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    private static IReadOnlyList<WingetCategory> BuildCatalog() => new List<WingetCategory>
    {
        new() { Name = "瀏覽器", Packages = new List<WingetPackage>
        {
            new() { Id = "Google.Chrome", Name = "Google Chrome", Desc = "Google 瀏覽器", Recommended = true },
            new() { Id = "Mozilla.Firefox", Name = "Firefox", Desc = "火狐瀏覽器", Recommended = true },
            new() { Id = "Microsoft.Edge", Name = "Microsoft Edge", Desc = "微軟瀏覽器" },
            new() { Id = "Brave.Brave", Name = "Brave", Desc = "注重隱私的瀏覽器" },
        } },
        new() { Name = "壓縮工具", Packages = new List<WingetPackage>
        {
            new() { Id = "7zip.7zip", Name = "7-Zip", Desc = "開源檔案壓縮工具", Recommended = true },
            new() { Id = "M2Team.NanaZip", Name = "NanaZip", Desc = "現代化的 7-Zip 分支" },
            new() { Id = "RARLab.WinRAR", Name = "WinRAR", Desc = "經典壓縮解壓軟體" },
            new() { Id = "Bandisoft.Bandizip", Name = "Bandizip", Desc = "輕量壓縮工具" },
        } },
        new() { Name = "影音播放", Packages = new List<WingetPackage>
        {
            new() { Id = "VideoLAN.VLC", Name = "VLC", Desc = "全能開源播放器", Recommended = true },
            new() { Id = "clsid2.mpc-hc", Name = "MPC-HC", Desc = "輕量經典播放器", Recommended = true },
            new() { Id = "Daum.PotPlayer", Name = "PotPlayer", Desc = "功能豐富的播放器" },
        } },
        new() { Name = "通訊社群", Packages = new List<WingetPackage>
        {
            new() { Id = "Tencent.QQ", Name = "QQ", Desc = "騰訊即時通訊" },
            new() { Id = "XPFCC4CD725961", Name = "LINE", Desc = "LINE 桌面版通訊軟體（Microsoft Store）" },
            new() { Id = "Telegram.TelegramDesktop", Name = "Telegram", Desc = "跨平台即時通訊" },
            new() { Id = "Discord.Discord", Name = "Discord", Desc = "語音與社群平台" },
        } },
        new() { Name = "文書與 PDF", Packages = new List<WingetPackage>
        {
            new() { Id = "Kingsoft.WPSOffice", Name = "WPS Office", Desc = "免費文書套裝" },
            new() { Id = "Adobe.Acrobat.Reader.64-bit", Name = "Acrobat Reader", Desc = "PDF 閱讀器", Recommended = true },
            new() { Id = "SumatraPDF.SumatraPDF", Name = "SumatraPDF", Desc = "極輕量 PDF 閱讀器" },
        } },
        new() { Name = "開發工具", Packages = new List<WingetPackage>
        {
            new() { Id = "Microsoft.VisualStudioCode", Name = "VS Code", Desc = "微軟程式碼編輯器", Recommended = true },
            new() { Id = "Git.Git", Name = "Git", Desc = "版本控制工具", Recommended = true },
            new() { Id = "Python.Python.3.12", Name = "Python 3.12", Desc = "Python 執行環境" },
            new() { Id = "OpenJS.NodeJS", Name = "Node.js", Desc = "JavaScript 執行環境" },
        } },
        new() { Name = "系統工具", Packages = new List<WingetPackage>
        {
            new() { Id = "Microsoft.PowerToys", Name = "PowerToys", Desc = "微軟系統增強工具集", Recommended = true },
            new() { Id = "voidtools.Everything", Name = "Everything", Desc = "毫秒級檔名搜尋", Recommended = true },
            new() { Id = "CPUID.CPU-Z", Name = "CPU-Z", Desc = "處理器資訊檢測" },
            new() { Id = "TechPowerUp.GPU-Z", Name = "GPU-Z", Desc = "顯示卡資訊檢測" },
            new() { Id = "CrystalDewWorld.CrystalDiskInfo", Name = "CrystalDiskInfo", Desc = "硬碟健康監控" },
        } },
        new() { Name = "下載工具", Packages = new List<WingetPackage>
        {
            new() { Id = "agalwood.Motrix", Name = "Motrix", Desc = "全能下載工具" },
            new() { Id = "qBittorrent.qBittorrent", Name = "qBittorrent", Desc = "開源 BT 下載" },
        } },
        new() { Name = "截圖錄影", Packages = new List<WingetPackage>
        {
            new() { Id = "ShareX.ShareX", Name = "ShareX", Desc = "開源截圖與錄影", Recommended = true },
            new() { Id = "OBSProject.OBSStudio", Name = "OBS Studio", Desc = "直播與錄影軟體" },
        } },
        new() { Name = "影像設計", Packages = new List<WingetPackage>
        {
            new() { Id = "IrfanSkiljan.IrfanView", Name = "IrfanView", Desc = "輕量看圖工具" },
            new() { Id = "GIMP.GIMP", Name = "GIMP", Desc = "開源影像編輯" },
        } },
        new() { Name = "遠端與虛擬化", Packages = new List<WingetPackage>
        {
            new() { Id = "RustDesk.RustDesk", Name = "RustDesk", Desc = "開源遠端桌面" },
            new() { Id = "Oracle.VirtualBox", Name = "VirtualBox", Desc = "開源虛擬機器" },
        } },
        new() { Name = "終端與 SSH", Packages = new List<WingetPackage>
        {
            new() { Id = "Microsoft.WindowsTerminal", Name = "Windows Terminal", Desc = "微軟現代化終端機", Recommended = true },
            new() { Id = "PuTTY.PuTTY", Name = "PuTTY", Desc = "經典 SSH 用戶端" },
        } },
    };

    /// <summary>偵測本機 winget（App Installer）是否可用；可於「一鍵初始化」重新偵測。</summary>
    public async Task DetectAsync()
    {
        bool ok = await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("winget", "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return false;
                p.WaitForExit(8000);
                return p.HasExited && p.ExitCode == 0;
            }
            catch { return false; }
        });
        WingetAvailable = ok;
        StatusText = ok
            ? "winget 就緒。勾選要安裝的軟體後按「開始安裝」。"
            : "未偵測到 winget（App Installer）。請先於 Microsoft Store 安裝「應用程式安裝程式」。";
    }

    /// <summary>依序安裝所有已勾選的套件，並將 winget 即時輸出附加到記錄。</summary>
    public async Task InstallSelectedAsync()
    {
        if (!CanInstall) return;
        var pending = Categories.SelectMany(c => c.Packages).Where(p => p.IsSelected).ToList();
        if (pending.Count == 0) { StatusText = "尚未勾選任何軟體。"; return; }

        IsInstalling = true;
        Log = "";
        int done = 0, fail = 0;
        try
        {
            foreach (var pkg in pending)
            {
                Append($"── 安裝 {pkg.Name}（{pkg.Id}）──");
                StatusText = $"安裝中（{done + fail + 1}/{pending.Count}）：{pkg.Name}";
                int code = await RunWingetAsync(pkg.Id);
                if (code == 0) { done++; Append($"✓ {pkg.Name} 安裝完成。"); }
                else { fail++; Append($"✗ {pkg.Name} 安裝失敗或已取消（結束碼 {code}）。"); }
            }
            StatusText = $"完成：成功 {done}、失敗 {fail}，共 {pending.Count} 項。";
        }
        finally { IsInstalling = false; }
    }

    private async Task<int> RunWingetAsync(string id)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("winget",
                    $"install --id {id} -e --accept-package-agreements --accept-source-agreements --disable-interactivity")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                if (p is null) { Append("無法啟動 winget。"); return -1; }
                p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Append(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Append(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception ex) { Append("錯誤：" + ex.Message); return -1; }
        });
    }

    // winget 輸出多為進度條刷新；僅保留最後約 200 行，避免記錄無限增長
    private void Append(string line)
    {
        void Do()
        {
            var text = Log.Length == 0 ? line : Log + "\n" + line;
            var lines = text.Split('\n');
            if (lines.Length > 200) text = string.Join("\n", lines[^200..]);
            Log = text;
        }
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess()) Do();
        else disp.BeginInvoke(Do);
    }
}
