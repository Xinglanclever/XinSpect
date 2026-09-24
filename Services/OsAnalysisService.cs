using System.Collections.ObjectModel;
using System.Management;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>作業系統分析的一列：一項事實加上它的判定嚴重度。</summary>
public sealed class OsFactRow
{
    public OsFactRow(string key, string value, Severity severity = Severity.Neutral, string note = "")
    { Key = key; Value = value; Sev = severity; Note = note; }
    public string Key { get; }
    public string Value { get; }
    public Severity Sev { get; }
    public string Note { get; }
}

/// <summary>
/// 作業系統分析：版本、組建、授權、更新、安全態勢（Secure Boot／VBS／HVCI／UAC）、開機時間。
/// 全程唯讀——WMI、登錄檔與 UEFI 韌體變數。不改動任何系統設定。
/// </summary>
/// <remarks>
/// 這一頁把散在各處的 OS 事實收攏成一個地方：SystemInfoService（版本／安裝日期）、
/// FirmwareService（Secure Boot／VBS）與 LicenseService（授權）本來各自為政，
/// 使用者要看「這台機器的作業系統整體狀態」得翻三個頁面。
/// </remarks>
public sealed class OsAnalysisService : ObservableObject
{
    public ObservableCollection<OsFactRow> Identity { get; } = [];
    public ObservableCollection<OsFactRow> Activation { get; } = [];
    public ObservableCollection<OsFactRow> Updates { get; } = [];
    public ObservableCollection<OsFactRow> Security { get; } = [];

    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。按「重新掃描」讀取作業系統資訊（唯讀）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _summary = "—";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "讀取作業系統資訊中…";
        try
        {
            var result = await Task.Run(ScanAll);
            Apply(Identity, result.Identity);
            Apply(Activation, result.Activation);
            Apply(Updates, result.Updates);
            Apply(Security, result.Security);
            Summary = result.Summary;
            Status = "讀取完成。";
        }
        catch (Exception ex)
        {
            Diag.Swallow("OsAnalysis.Refresh", ex, "作業系統分析讀取失敗");
            Status = "讀取失敗：" + ex.Message;
        }
        finally { IsLoading = false; }
    }

    private static void Apply(ObservableCollection<OsFactRow> target, List<OsFactRow> rows)
    {
        target.Clear();
        foreach (var r in rows) target.Add(r);
    }

    private sealed record ScanResult(
        List<OsFactRow> Identity, List<OsFactRow> Activation,
        List<OsFactRow> Updates, List<OsFactRow> Security, string Summary);

    private static ScanResult ScanAll()
    {
        var id = ReadIdentity();
        var act = ReadActivation();
        var upd = ReadUpdates();
        var sec = ReadSecurity();

        int concerns = act.Concat(sec).Count(r => r.Sev is Severity.Warning or Severity.Serious or Severity.Critical);
        string summary = concerns == 0
            ? "作業系統狀態正常：已啟用、安全機制到位。"
            : $"有 {concerns} 項需要留意（見下方標示）。";

        return new ScanResult(id, act, upd, sec, summary);
    }

    // ── 身分：版本、組建、安裝、開機 ──────────────────────────
    private static List<OsFactRow> ReadIdentity()
    {
        var rows = new List<OsFactRow>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
            foreach (var o in s.Get())
            {
                rows.Add(new OsFactRow("作業系統", Str(o, "Caption")));
                rows.Add(new OsFactRow("版本", $"{Str(o, "Version")}（組建 {Str(o, "BuildNumber")}）"));
                rows.Add(new OsFactRow("架構", Str(o, "OSArchitecture")));

                var install = DateOrNull(o, "InstallDate");
                if (install is DateTime it)
                    rows.Add(new OsFactRow("安裝日期", it.ToString("yyyy-MM-dd HH:mm")));

                var boot = DateOrNull(o, "LastBootUpTime");
                if (boot is DateTime bt)
                {
                    var up = DateTime.Now - bt;
                    rows.Add(new OsFactRow("上次開機", bt.ToString("yyyy-MM-dd HH:mm")));
                    rows.Add(new OsFactRow("已開機時長",
                        $"{(int)up.TotalDays} 天 {up.Hours} 小時 {up.Minutes} 分",
                        up.TotalDays >= 30 ? Severity.Warning : Severity.Neutral,
                        up.TotalDays >= 30 ? "超過一個月沒重開機，累積的更新可能尚未生效" : ""));
                }
                break;
            }
        }
        catch (Exception ex) { Diag.Swallow("OsAnalysis.Identity", ex, "OS 身分讀不到"); }

        // 顯示版本名（21H2/22H2/24H2）與 UBR 從登錄檔補
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k is not null)
            {
                if (k.GetValue("DisplayVersion") is string dv && dv.Length > 0)
                    rows.Add(new OsFactRow("功能版本", dv));
                if (k.GetValue("UBR") is int ubr)
                    rows.Add(new OsFactRow("修訂版號 (UBR)", ubr.ToString()));
                if (k.GetValue("RegisteredOwner") is string ro && ro.Length > 0)
                    rows.Add(new OsFactRow("註冊擁有者", ro));
            }
        }
        catch (Exception ex) { Diag.Swallow("OsAnalysis.RegVer", ex, "登錄檔版本資訊讀不到"); }

        return rows;
    }

    // ── 授權 ──────────────────────────────────────────────────
    private static List<OsFactRow> ReadActivation()
    {
        var rows = new List<OsFactRow>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2",
                "SELECT Name, Description, LicenseStatus, PartialProductKey FROM SoftwareLicensingProduct "
                + "WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL");
            foreach (var o in s.Get())
            {
                uint status = 0;
                try { status = Convert.ToUInt32(o["LicenseStatus"]); } catch { }
                var (text, sev) = status switch
                {
                    1 => ("已啟用", Severity.Good),
                    0 => ("未啟用", Severity.Warning),
                    2 => ("寬限期內", Severity.Warning),
                    3 => ("額外寬限期", Severity.Warning),
                    5 => ("未授權（通知模式）", Severity.Serious),
                    _ => ($"授權狀態碼 {status}", Severity.Neutral),
                };
                rows.Add(new OsFactRow("啟用狀態", text, sev));
                string desc = Str(o, "Description");
                if (desc.Contains("OEM", StringComparison.OrdinalIgnoreCase))
                    rows.Add(new OsFactRow("授權通道", "OEM（隨機出廠，綁定主機板，重裝不掉但無法轉移到新機）"));
                else if (desc.Contains("Retail", StringComparison.OrdinalIgnoreCase))
                    rows.Add(new OsFactRow("授權通道", "零售（可轉移到新電腦）"));
                else if (desc.Contains("Volume", StringComparison.OrdinalIgnoreCase))
                    rows.Add(new OsFactRow("授權通道", "大量授權（企業／教育）"));
                string pk = Str(o, "PartialProductKey");
                if (pk.Length > 0) rows.Add(new OsFactRow("產品金鑰末五碼", "*****-" + pk));
                break;
            }
        }
        catch (Exception ex) { Diag.Swallow("OsAnalysis.Activation", ex, "授權狀態讀不到"); }

        if (rows.Count == 0) rows.Add(new OsFactRow("啟用狀態", "讀不到（需要授權服務）", Severity.Neutral));
        return rows;
    }

    // ── 更新 ──────────────────────────────────────────────────
    private static List<OsFactRow> ReadUpdates()
    {
        var rows = new List<OsFactRow>();
        // 最近安裝的修補程式
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
            DateTime? latest = null;
            int count = 0;
            foreach (var o in s.Get())
            {
                count++;
                if (o["InstalledOn"] is string ds && DateTime.TryParse(ds, out var d))
                    if (latest is null || d > latest) latest = d;
            }
            rows.Add(new OsFactRow("已安裝修補程式", $"{count} 項"));
            if (latest is DateTime lt)
            {
                var age = (DateTime.Now - lt).TotalDays;
                rows.Add(new OsFactRow("最近更新", lt.ToString("yyyy-MM-dd"),
                    age >= 60 ? Severity.Warning : Severity.Neutral,
                    age >= 60 ? "距上次更新已超過兩個月" : ""));
            }
        }
        catch (Exception ex) { Diag.Swallow("OsAnalysis.Hotfix", ex, "修補程式清單讀不到"); }

        // 待重開機旗標
        try
        {
            bool pending =
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null
                || Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null;
            rows.Add(new OsFactRow("待重開機", pending ? "是（有更新等待重開機生效）" : "否",
                pending ? Severity.Warning : Severity.Good));
        }
        catch (Exception ex) { Diag.Swallow("OsAnalysis.Reboot", ex, "重開機旗標讀不到"); }

        return rows;
    }

    // ── 安全態勢 ──────────────────────────────────────────────
    private static List<OsFactRow> ReadSecurity()
    {
        var rows = new List<OsFactRow>();

        // UAC
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (k?.GetValue("EnableLUA") is int lua)
                rows.Add(new OsFactRow("使用者帳戶控制 (UAC)", lua != 0 ? "已啟用" : "已停用",
                    lua != 0 ? Severity.Good : Severity.Serious,
                    lua != 0 ? "" : "UAC 關閉時所有程式以完整權限執行，是重大安全風險"));
        }
        catch (Exception ex) { Diag.Swallow("OsAnalysis.UAC", ex, "UAC 狀態讀不到"); }

        // VBS / HVCI（DeviceGuard）
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (var o in s.Get())
            {
                uint vbs = 0;
                try { vbs = Convert.ToUInt32(o["VirtualizationBasedSecurityStatus"]); } catch { }
                rows.Add(new OsFactRow("虛擬化型安全 (VBS)",
                    vbs == 2 ? "執行中" : vbs == 1 ? "已啟用但未執行" : "未啟用",
                    vbs == 2 ? Severity.Good : Severity.Neutral));

                var running = o["SecurityServicesRunning"] as uint[] ?? [];
                rows.Add(new OsFactRow("記憶體完整性 (HVCI)",
                    running.Contains(2u) ? "執行中" : "未執行",
                    running.Contains(2u) ? Severity.Good : Severity.Neutral));
                rows.Add(new OsFactRow("認證保護 (Credential Guard)",
                    running.Contains(1u) ? "執行中" : "未執行",
                    running.Contains(1u) ? Severity.Good : Severity.Neutral));
                break;
            }
        }
        catch (Exception ex) { Diag.Swallow("OsAnalysis.VBS", ex, "VBS 狀態讀不到（需要 DeviceGuard WMI 命名空間）"); }

        // Defender 即時保護
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
                "SELECT RealTimeProtectionEnabled, AntivirusEnabled FROM MSFT_MpComputerStatus");
            foreach (var o in s.Get())
            {
                bool rtp = o["RealTimeProtectionEnabled"] as bool? ?? false;
                rows.Add(new OsFactRow("Defender 即時保護", rtp ? "開啟" : "關閉",
                    rtp ? Severity.Good : Severity.Warning,
                    rtp ? "" : "可能被第三方防毒接管，或被關閉"));
                break;
            }
        }
        catch (Exception ex) { Diag.Swallow("OsAnalysis.Defender", ex, "Defender 狀態讀不到"); }

        return rows;
    }

    private static string Str(ManagementBaseObject o, string k)
    { try { return o[k]?.ToString()?.Trim() ?? "—"; } catch { return "—"; } }

    private static DateTime? DateOrNull(ManagementBaseObject o, string k)
    {
        try { return ManagementDateTimeConverter.ToDateTime(o[k]?.ToString()); }
        catch { return null; }
    }
}
