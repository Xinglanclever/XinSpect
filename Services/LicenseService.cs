using System.Management;

namespace XinSpect;

/// <summary>
/// Windows 授權狀態：是不是正版、重裝會不會掉、能不能移到新電腦。
/// </summary>
/// <remarks>
/// 資料取自 Windows 自己的授權服務（WMI 的 <c>SoftwareLicensingProduct</c> 與
/// <c>SoftwareLicensingService</c>），全程唯讀。
/// <para>
/// <b>本頁不提供任何啟用、變更或移除授權的動作</b>——那是 Windows 設定裡的事，
/// 一個硬體資訊工具不該碰。完整金鑰預設遮蔽，要看得自己按一下；按了也只顯示在畫面上，
/// 不寫入任何檔案、不放進診斷紀錄、不隨報告匯出。
/// </para>
/// </remarks>
public sealed class LicenseService : ObservableObject
{
    /// <summary>Windows 本身的授權應用程式識別碼（其他值是 Office 等產品，不在本頁範圍）。</summary>
    private const string WindowsAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    private string _edition = "—";
    public string EditionText { get => _edition; private set => SetProperty(ref _edition, value); }

    private string _partial = "—";
    public string PartialKeyText { get => _partial; private set => SetProperty(ref _partial, value); }

    private string _channel = "—";
    public string ChannelText { get => _channel; private set => SetProperty(ref _channel, value); }

    private LicenseVerdict _verdict = new()
    {
        Headline = "尚未讀取", Severity = Severity.Neutral,
        Detail = "第一次進入本頁時會讀一次 Windows 的授權狀態（唯讀）。",
    };
    public LicenseVerdict Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    /// <summary>韌體內嵌金鑰的遮蔽形式；使用者按下「顯示完整金鑰」後才換成真值。</summary>
    private string _keyText = "—";
    public string KeyText { get => _keyText; private set => SetProperty(ref _keyText, value); }

    private string? _fullKey;
    public bool HasFullKey => !string.IsNullOrWhiteSpace(_fullKey);

    private bool _revealed;
    public bool IsRevealed { get => _revealed; private set => SetProperty(ref _revealed, value); }

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }

    public bool CanRefresh => !_busy;

    private string _status = "按「重新讀取」查詢 Windows 授權狀態。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool _loaded;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    /// <summary>顯示或收回完整金鑰。金鑰只存在記憶體裡，收回後畫面立刻恢復遮蔽。</summary>
    public void ToggleReveal()
    {
        if (!HasFullKey) return;
        IsRevealed = !IsRevealed;
        KeyText = IsRevealed ? _fullKey! : LicenseDecoder.MaskKey(_fullKey);
    }

    public void Refresh()
    {
        if (_busy) return;
        IsBusy = true;
        IsRevealed = false;
        _ = Task.Run(Collect).ContinueWith(t =>
        {
            var r = t.Result;
            EditionText = r.Edition;
            PartialKeyText = LicenseDecoder.PartialKeyText(r.Partial);
            ChannelText = LicenseDecoder.ChannelText(r.Description);
            _fullKey = r.FirmwareKey;
            KeyText = LicenseDecoder.MaskKey(r.FirmwareKey);
            Verdict = LicenseDecoder.Judge(r.Status, r.Description, HasFullKey);
            Status = r.Status is 0 && r.Edition == "—"
                ? "讀不到授權資訊：這個 Windows 版本或權限不允許查詢授權服務。讀不到就是讀不到，本頁不猜。"
                : "以上取自 Windows 自己的授權服務，全程唯讀。本頁不提供啟用或變更授權的功能。";
            OnPropertyChanged(nameof(HasFullKey));
            IsBusy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private readonly record struct Snapshot(string Edition, string? Partial, string? Description,
                                           uint Status, string? FirmwareKey);

    private static Snapshot Collect()
    {
        string edition = "—";
        string? partial = null, description = null, firmware = null;
        uint status = 0;

        try
        {
            // 只取有部分金鑰的那一筆：沒有金鑰的是未安裝的授權範本，列出來只會混淆
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, Description, LicenseStatus, PartialProductKey FROM SoftwareLicensingProduct "
                + $"WHERE ApplicationID='{WindowsAppId}' AND PartialProductKey IS NOT NULL");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    edition = Str(o, "Name") is { Length: > 0 } n ? n : edition;
                    description = Str(o, "Description");
                    partial = Str(o, "PartialProductKey");
                    try { status = Convert.ToUInt32(o["LicenseStatus"]); } catch { }
                    break;
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("LicenseService.Product", ex, "Windows 授權狀態讀不到，本頁顯示為讀不到。");
        }

        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT OA3xOriginalProductKey FROM SoftwareLicensingService");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    firmware = Str(o, "OA3xOriginalProductKey");
                    break;
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("LicenseService.Firmware", ex, "韌體內嵌金鑰讀不到，該欄顯示為讀不到。");
        }

        return new Snapshot(edition, partial, description, status,
                            string.IsNullOrWhiteSpace(firmware) ? null : firmware);
    }

    private static string Str(ManagementObject o, string p)
    {
        try { return o[p]?.ToString()?.Trim() ?? ""; } catch { return ""; }
    }
}
