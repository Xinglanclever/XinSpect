using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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

    private bool _installed;
    /// <summary>本機已經裝過（由 <c>winget export</c> 的清單比對得知）。已裝的不再列入「勾選推薦」。</summary>
    public bool IsInstalled { get => _installed; set { if (SetProperty(ref _installed, value)) OnPropertyChanged(nameof(StateText)); } }
    public string StateText => _installed ? "已安裝" : "";
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
        // 任一套件勾選狀態變動時，更新已選數量與安裝按鈕文字
        foreach (var p in Categories.SelectMany(c => c.Packages))
            p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WingetPackage.IsSelected)) RaiseSelectionChanged(); };
        _ = DetectAsync();
    }

    /// <summary>winget 已知的已安裝項目數（僅供狀態列顯示）。</summary>
    private int _installedCount;
    public int InstalledCount { get => _installedCount; private set => SetProperty(ref _installedCount, value); }

    /// <summary>
    /// 以 <c>winget export</c> 取本機已安裝清單，把清單裡已經有的套件標成「已安裝」。
    /// </summary>
    /// <remarks>
    /// 誠實界線：<c>winget export</c> 只寫得出「來源可追溯」的套件。msstore 來源（例如 LINE 的
    /// <c>XPFCC4CD725961</c>）以及手動安裝、非 winget 裝的軟體都不會出現在匯出檔裡——
    /// 所以<b>沒有「已安裝」標記不等於沒裝</b>，只代表 winget 不認得它。這個標記純粹是提示，
    /// 不會阻止使用者自己勾選重裝（重裝時 winget 會自行判斷要不要升級）。
    /// </remarks>
    public async Task DetectInstalledAsync()
    {
        var ids = await Task.Run(ExportInstalledIds);
        if (ids.Count == 0) return;
        int hit = 0;
        foreach (var p in Categories.SelectMany(c => c.Packages))
        {
            p.IsInstalled = ids.Contains(p.Id);
            if (p.IsInstalled) hit++;
        }
        InstalledCount = hit;
    }

    private static HashSet<string> ExportInstalledIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xinspect-winget-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo("winget",
                $"export -o \"{path}\" --accept-source-agreements --disable-interactivity")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return new(StringComparer.OrdinalIgnoreCase);
            p.WaitForExit(90_000);
            // 匯出時若有套件找不到來源，winget 會回非 0 但<b>照樣寫出檔案</b>，
            // 因此以「檔案有沒有生出來」為準，不看結束碼。
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            return ParseExportedIds(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Diag.Swallow("winget 已安裝清單匯出", ex, "不顯示任何「已安裝」標記");
            return new(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 暫存檔刪不掉不影響功能 */ }
        }
    }

    /// <summary>從 <c>winget export</c> 的 JSON 取出所有 PackageIdentifier（純函式，單元測試涵蓋）。</summary>
    public static HashSet<string> ParseExportedIds(string json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return set;
            if (!doc.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
                return set;
            foreach (var src in sources.EnumerateArray())
            {
                if (src.ValueKind != JsonValueKind.Object) continue;
                if (!src.TryGetProperty("Packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array) continue;
                foreach (var pkg in pkgs.EnumerateArray())
                {
                    if (pkg.ValueKind != JsonValueKind.Object) continue;
                    if (pkg.TryGetProperty("PackageIdentifier", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var s = id.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 匯出檔格式不如預期就當成「不知道」，不要讓一鍵裝機整頁失效
        }
        return set;
    }

    /// <summary>已勾選的套件總數。</summary>
    public int SelectedCount => Categories.SelectMany(c => c.Packages).Count(p => p.IsSelected);

    /// <summary>
    /// 安裝按鈕上的文字。<b>必須是字串屬性</b>：<c>Button.Content</c> 的型別是 <see cref="object"/>，
    /// WPF 在目標為 object 時會忽略繫結上的 <c>StringFormat</c>，直接把數字本身丟上去顯示成「0」。
    /// </summary>
    public string InstallButtonText => InstallLabel(SelectedCount);

    /// <summary>安裝按鈕文字的產生規則（沒勾任何項目時不顯示括號裡的 0）。</summary>
    internal static string InstallLabel(int selected) => selected > 0 ? $"安裝（{selected}）" : "安裝";

    /// <summary>勾選狀態變動後一併更新數量與按鈕文字。</summary>
    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    /// <summary>勾選所有標記為推薦、且 winget 認為還沒裝的套件（已裝的不重複勾）。</summary>
    public void SelectRecommended()
    {
        foreach (var p in Categories.SelectMany(c => c.Packages)) p.IsSelected = p.Recommended && !p.IsInstalled;
        RaiseSelectionChanged();
    }

    /// <summary>清除所有勾選。</summary>
    public void ClearSelection()
    {
        foreach (var p in Categories.SelectMany(c => c.Packages)) p.IsSelected = false;
        RaiseSelectionChanged();
    }

    /// <summary>
    /// 軟體清單。<b>刪去的原則（1.7.0 整理）</b>：
    /// ①「曦覽自己就做這件事」的檢測工具（CPU-Z／GPU-Z／CrystalDiskInfo）——裝了只是多一份重複的答案；
    /// ②系統內建、用 winget 裝沒有意義的（Edge、Windows Terminal）；
    /// ③同一類裡功能重疊的第三、四個（NanaZip 是 7-Zip 的分支、Brave 與 Chrome 同引擎、PotPlayer 與 MPC-HC 同位）。
    /// 每一類只留「真的互相不能取代」的那幾個。
    /// </summary>
    private static IReadOnlyList<WingetCategory> BuildCatalog() => Catalog();

    /// <summary>建出軟體清單（純函式，單元測試涵蓋：無重複 ID、不含已被曦覽自己取代或系統內建的項目）。</summary>
    public static IReadOnlyList<WingetCategory> Catalog() => new List<WingetCategory>

    {
        new() { Name = "瀏覽器", Packages = new List<WingetPackage>
        {
            // Edge 是系統內建；Brave 與 Chrome 同為 Chromium，留兩個不同引擎（Blink／Gecko）就夠
            new() { Id = "Google.Chrome", Name = "Google Chrome", Desc = "Google 瀏覽器", Recommended = true },
            new() { Id = "Mozilla.Firefox", Name = "Firefox", Desc = "火狐瀏覽器（Gecko 引擎，非 Chromium）", Recommended = true },
        } },
        new() { Name = "壓縮工具", Packages = new List<WingetPackage>
        {
            // NanaZip 是 7-Zip 的分支、Bandizip 與兩者同位，留開源與商用各一
            new() { Id = "7zip.7zip", Name = "7-Zip", Desc = "開源檔案壓縮工具", Recommended = true },
            new() { Id = "RARLab.WinRAR", Name = "WinRAR", Desc = "經典壓縮解壓軟體（RAR 專有格式）" },
        } },
        new() { Name = "影音播放", Packages = new List<WingetPackage>
        {
            // PotPlayer 與 MPC-HC 定位重疊且為閉源，移除
            new() { Id = "VideoLAN.VLC", Name = "VLC", Desc = "全能開源播放器", Recommended = true },
            new() { Id = "clsid2.mpc-hc", Name = "MPC-HC", Desc = "輕量經典播放器" },
        } },
        new() { Name = "通訊社群", Packages = new List<WingetPackage>
        {
            // 四個不同的網路，彼此無法取代，全部保留
            new() { Id = "Tencent.QQ", Name = "QQ", Desc = "騰訊即時通訊" },
            new() { Id = "XPFCC4CD725961", Name = "LINE", Desc = "LINE 桌面版通訊軟體（Microsoft Store）" },
            new() { Id = "Telegram.TelegramDesktop", Name = "Telegram", Desc = "跨平台即時通訊" },
            new() { Id = "Discord.Discord", Name = "Discord", Desc = "語音與社群平台" },
        } },
        new() { Name = "文書與 PDF", Packages = new List<WingetPackage>
        {
            // 瀏覽器本身就能看 PDF，Acrobat Reader 只是多兩個常駐服務；閱讀留極輕量的 SumatraPDF
            new() { Id = "Kingsoft.WPSOffice", Name = "WPS Office", Desc = "免費文書套裝" },
            new() { Id = "SumatraPDF.SumatraPDF", Name = "SumatraPDF", Desc = "極輕量 PDF 閱讀器", Recommended = true },
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
            // CPU-Z／GPU-Z／CrystalDiskInfo 全部移除：曦覽的處理器／顯示卡／儲存裝置頁就是在做同一件事
            new() { Id = "Microsoft.PowerToys", Name = "PowerToys", Desc = "微軟系統增強工具集", Recommended = true },
            new() { Id = "voidtools.Everything", Name = "Everything", Desc = "毫秒級檔名搜尋", Recommended = true },
            // 原「終端機與 SSH」類：Windows Terminal 已是系統內建，只剩 PuTTY，併入此類
            new() { Id = "PuTTY.PuTTY", Name = "PuTTY", Desc = "SSH／序列埠用戶端（內建 ssh 沒有的儲存工作階段與序列埠）" },
        } },
        new() { Name = "下載工具", Packages = new List<WingetPackage>
        {
            // 一個 HTTP／一個 BT，用途不同
            new() { Id = "agalwood.Motrix", Name = "Motrix", Desc = "全能下載工具（HTTP／磁力）" },
            new() { Id = "qBittorrent.qBittorrent", Name = "qBittorrent", Desc = "開源 BT 下載" },
        } },
        new() { Name = "截圖錄影", Packages = new List<WingetPackage>
        {
            new() { Id = "ShareX.ShareX", Name = "ShareX", Desc = "開源截圖與錄影", Recommended = true },
            new() { Id = "OBSProject.OBSStudio", Name = "OBS Studio", Desc = "直播與錄影軟體" },
        } },
        new() { Name = "影像設計", Packages = new List<WingetPackage>
        {
            // 一個看圖一個編修，不重複
            new() { Id = "IrfanSkiljan.IrfanView", Name = "IrfanView", Desc = "輕量看圖工具" },
            new() { Id = "GIMP.GIMP", Name = "GIMP", Desc = "開源影像編輯" },
        } },
        new() { Name = "遠端與虛擬化", Packages = new List<WingetPackage>
        {
            new() { Id = "RustDesk.RustDesk", Name = "RustDesk", Desc = "開源遠端桌面" },
            new() { Id = "Oracle.VirtualBox", Name = "VirtualBox", Desc = "開源虛擬機器" },
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
        if (!ok) return;

        // 已安裝清單要另外跑一次 winget export，比 --version 慢得多，所以放在偵測成功之後才做，
        // 而且不擋畫面：標記晚幾秒出現沒關係，一開始就把安裝鈕鎖住才是問題。
        await DetectInstalledAsync();
        if (InstalledCount > 0)
            StatusText = $"winget 就緒；其中 {InstalledCount} 項本機已安裝（winget 已知的部分，msstore 與手動安裝的不會列入）。";
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
