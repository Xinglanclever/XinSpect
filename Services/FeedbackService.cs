using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 「留言建議」：把使用者寫的建議送到作者的中轉端點（與免費共用額度同一個 Worker，
/// 見 <c>cloudflare/ai-proxy/README.md</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只送使用者自己打的字。</b>沒有硬體規格、沒有機器識別碼、沒有記錄檔、不夾任何自動收集的內容——
/// 留言框裡看得到什麼就只送出什麼（外加一個選填的聯絡方式與版本號，版本號是為了知道這則建議
/// 是對哪一版說的）。這一點寫在介面上，也寫在這裡：日後要加東西進去，得先改介面上的說明。
/// </para>
/// <para>
/// 沒有網路時整張卡片停用（灰色），不是按了才失敗。判斷用
/// <see cref="NetworkInterface.GetIsNetworkAvailable"/>——它只回答「這台機器有沒有一條可用的網卡」，
/// 不保證連得到外網（例如只連上區網、或被防火牆擋住）。這是刻意的：偵測真正的連通性得先發一次
/// 請求出去，為了把按鈕變灰而偷偷連外並不划算；真的送不出去時，狀態列會如實說明失敗原因。
/// </para>
/// </remarks>
public sealed class FeedbackService : ObservableObject
{
    /// <summary>留言長度上限（與中轉那側的上限一致；太長的內容中轉會拒收）。</summary>
    public const int MaxLength = 2000;

    /// <summary>聯絡方式長度上限（選填）。</summary>
    public const int MaxContactLength = 120;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private string _text = "";
    /// <summary>使用者輸入的建議內容。</summary>
    public string Text
    {
        get => _text;
        set { if (SetProperty(ref _text, value)) OnPropertyChanged(nameof(CanSend)); }
    }

    private string _contact = "";
    /// <summary>選填的聯絡方式（Email／GitHub 帳號等），留空就是匿名。</summary>
    public string Contact { get => _contact; set => SetProperty(ref _contact, value); }

    private bool _isSending;
    public bool IsSending
    {
        get => _isSending;
        private set { if (SetProperty(ref _isSending, value)) OnPropertyChanged(nameof(CanSend)); }
    }

    private string _status = "";
    /// <summary>送出結果或錯誤說明（沒有動作時為空字串）。</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>這台機器目前有沒有可用的網路連線。</summary>
    public static bool HasNetwork
    {
        get { try { return NetworkInterface.GetIsNetworkAvailable(); } catch { return false; } }
    }

    /// <summary>作者是否已啟用收件端點（沒有就整張卡片停用）。</summary>
    public static bool IsConfigured => SharedAiEndpoint.IsConfigured;

    /// <summary>整張卡片是否可用（有端點、有網路）。</summary>
    public bool IsAvailable => IsConfigured && HasNetwork;

    /// <summary>「上傳」是否可按。</summary>
    public bool CanSend => IsAvailable && !IsSending && Text.Trim().Length > 0;

    /// <summary>卡片停用時顯示的原因（可用時為空字串）——按鈕變灰一定要說得出為什麼。</summary>
    public string UnavailableReason =>
        !IsConfigured ? "這個版本還沒有收件端點，暫時無法直接上傳；請改用「GitHub 開源」那張卡片的連結提交 Issue。"
        : !HasNetwork ? "目前沒有網路連線，無法上傳。你寫的內容不會消失，接上網路後再按上傳即可。"
        : "";

    /// <summary>重新評估網路狀態（供介面在載入或使用者按重試時呼叫）。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(HasNetwork));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(UnavailableReason));
        OnPropertyChanged(nameof(CanSend));
    }

    /// <summary>送出留言。成功後清空輸入框；失敗時把內容留著，並如實說明原因。</summary>
    public async Task SendAsync(string appVersion)
    {
        if (!CanSend) { Status = UnavailableReason; return; }

        string text = Text.Trim();
        if (text.Length > MaxLength)
        {
            Status = $"內容太長（{text.Length} 字），請精簡到 {MaxLength} 字以內。";
            return;
        }

        IsSending = true;
        Status = "正在上傳…";
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["message"] = text,
                ["contact"] = Contact.Trim() is { Length: > 0 } c
                    ? (c.Length > MaxContactLength ? c[..MaxContactLength] : c) : null,
                ["version"] = appVersion,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, FeedbackUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync();
                Status = (int)resp.StatusCode == 429
                    ? "上傳太頻繁，請稍後再試（同一個來源每小時有次數上限）。"
                    : $"上傳失敗：HTTP {(int)resp.StatusCode}"
                      + (body.Length > 0 ? $"（{(body.Length > 160 ? body[..160] + "…" : body)}）" : "");
                return;
            }
            Text = "";
            Status = "已送出，謝謝你的建議。";
        }
        catch (TaskCanceledException)
        {
            Status = "上傳逾時，請稍後再試。你寫的內容還留在框裡。";
        }
        catch (Exception ex)
        {
            Status = "上傳失敗：" + ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>收件網址：與共用額度同一個中轉，路徑 <c>/feedback</c>。</summary>
    internal static string FeedbackUrl => BuildFeedbackUrl(SharedAiEndpoint.BaseUrl);

    /// <summary>從中轉的 Base URL 推出 /feedback（Base URL 可能帶或不帶結尾的 /v1）。</summary>
    internal static string BuildFeedbackUrl(string baseUrl)
    {
        string b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b = b[..^3].TrimEnd('/');
        return b + "/feedback";
    }
}
