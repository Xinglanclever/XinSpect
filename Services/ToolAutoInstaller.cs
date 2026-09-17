using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;

namespace XinSpect;

/// <summary>工具箱的單一可安裝項目（透過 winget 安裝）。</summary>
public sealed class ToolInstallItem : ObservableObject
{
    public required string Name { get; init; }
    public required string WingetId { get; init; }
    public required string Category { get; init; }

    private bool _installed;
    public bool IsInstalled
    {
        get => _installed;
        set
        {
            if (SetProperty(ref _installed, value))
            {
                OnPropertyChanged(nameof(ShowInstallButton));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private bool _installing;
    public bool IsInstalling
    {
        get => _installing;
        set
        {
            if (SetProperty(ref _installing, value))
                OnPropertyChanged(nameof(ShowInstallButton));
        }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _installed ? "已安裝" : _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>安裝按鈕只在「尚未安裝且不在安裝中」時顯示。</summary>
    public bool ShowInstallButton => !_installed && !_installing;
}

/// <summary>
/// 工具箱一鍵下載安裝服務：以 winget 逐項安裝硬體診斷常用工具。
/// 清單裡的工具全部來自 winget 官方來源（winget-pkgs），本程式不內含任何安裝檔。
/// </summary>
public sealed class ToolAutoInstaller : ObservableObject
{
    public ObservableCollection<ToolInstallItem> Items { get; } = new(BuildItems());

    private bool _isAnyInstalling;
    /// <summary>是否有任何項目正在安裝（供 UI 停用全部安裝按鈕）。</summary>
    public bool IsAnyInstalling { get => _isAnyInstalling; private set => SetProperty(ref _isAnyInstalling, value); }

    private string _status = "";
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }

    private string _log = "";
    public string Log { get => _log; private set => SetProperty(ref _log, value); }

    /// <summary>建出預設工具清單（純函式，便於單元測試）。</summary>
    public static IReadOnlyList<ToolInstallItem> BuildItems() => new List<ToolInstallItem>
    {
        // ── 處理器工具 ──
        new() { Name = "CPU-Z",            WingetId = "CPUID.CPU-Z",                     Category = "處理器工具" },
        new() { Name = "Core Temp",        WingetId = "ALCPU.CoreTemp",                   Category = "處理器工具" },
        // ── 顯示卡工具 ──
        new() { Name = "GPU-Z",            WingetId = "TechPowerUp.GPU-Z",                Category = "顯示卡工具" },
        new() { Name = "FurMark",          WingetId = "Geeks3D.FurMark",                  Category = "烤機與測試" },
        // ── 硬碟工具 ──
        new() { Name = "CrystalDiskInfo",  WingetId = "CrystalDewWorld.CrystalDiskInfo",   Category = "硬碟工具" },
        new() { Name = "CrystalDiskMark", WingetId = "CrystalDewWorld.CrystalDiskMark",  Category = "硬碟工具" },
        // ── 綜合檢測 ──
        new() { Name = "HWiNFO",           WingetId = "REALiX.HWiNFO",                    Category = "綜合檢測" },
        // ── 系統工具 ──
        new() { Name = "7-Zip",            WingetId = "7zip.7zip",                         Category = "系統工具" },
        new() { Name = "Notepad++",        WingetId = "Notepad++.Notepad++",               Category = "系統工具" },
        new() { Name = "Everything",       WingetId = "voidtools.Everything",              Category = "系統工具" },
    };

    /// <summary>
    /// 以 <c>winget list --id X</c> 逐項偵測哪些工具已安裝，並更新 <see cref="ToolInstallItem.IsInstalled"/>。
    /// </summary>
    public async Task CheckAllInstalledAsync()
    {
        StatusText = "正在偵測已安裝的工具…";
        foreach (var item in Items)
        {
            bool found = await Task.Run(() => IsInstalledViaWinget(item.WingetId));
            item.IsInstalled = found;
        }
        int installed = Items.Count(i => i.IsInstalled);
        StatusText = installed > 0
            ? $"偵測完成：{installed}/{Items.Count} 項已安裝。"
            : "偵測完成：尚無已安裝的項目。";
    }

    /// <summary>安裝所有尚未安裝的工具（依序）。</summary>
    public async Task InstallAllAsync()
    {
        var pending = Items.Where(i => !i.IsInstalled && !i.IsInstalling).ToList();
        if (pending.Count == 0) { StatusText = "所有工具皆已安裝。"; return; }

        IsAnyInstalling = true;
        Log = "";
        int done = 0, fail = 0;
        try
        {
            foreach (var item in pending)
            {
                await InstallCoreAsync(item, done + fail + 1, pending.Count);
                if (item.IsInstalled) done++; else fail++;
            }
            StatusText = $"全部完成：成功 {done}、失敗 {fail}，共 {pending.Count} 項。";
        }
        finally { IsAnyInstalling = false; }
    }

    /// <summary>安裝單一工具。</summary>
    public async Task InstallSingleAsync(ToolInstallItem item)
    {
        if (item.IsInstalled || item.IsInstalling) return;
        IsAnyInstalling = true;
        try { await InstallCoreAsync(item, 1, 1); }
        finally { IsAnyInstalling = false; }
    }

    // ── 內部 ──────────────────────────────────────────────────────────

    private async Task InstallCoreAsync(ToolInstallItem item, int index, int total)
    {
        item.IsInstalling = true;
        item.StatusText = "安裝中…";
        StatusText = $"安裝中（{index}/{total}）：{item.Name}";
        Append($"── 安裝 {item.Name}（{item.WingetId}）──");

        int code = await Task.Run(() => RunWingetInstall(item.WingetId));
        item.IsInstalling = false;

        if (code == 0)
        {
            item.IsInstalled = true;
            Append($"✓ {item.Name} 安裝完成。");
        }
        else
        {
            item.StatusText = $"安裝失敗（結束碼 {code}）";
            Append($"✗ {item.Name} 安裝失敗或已取消（結束碼 {code}）。");
        }
    }

    /// <summary>以 <c>winget list --id</c> 判斷是否已安裝（結束碼 0 表示有找到）。</summary>
    private static bool IsInstalledViaWinget(string wingetId)
    {
        try
        {
            var psi = new ProcessStartInfo("winget",
                $"list --id {wingetId} -e --accept-source-agreements --disable-interactivity")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(30_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private int RunWingetInstall(string wingetId)
    {
        try
        {
            var psi = new ProcessStartInfo("winget",
                $"install --id {wingetId} -e --accept-package-agreements --accept-source-agreements --disable-interactivity -h")
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
    }

    /// <summary>附加一行到記錄（保留最後 200 行，跨執行緒安全）。</summary>
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
