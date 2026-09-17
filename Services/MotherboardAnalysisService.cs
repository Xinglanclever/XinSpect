using System;
using System.Management;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// AI 輔助主機板分析服務。
/// 從 WMI 讀取主機板基本資訊，透過 AiService 查詢詳細規格，
/// 並提供圖片搜尋確認流程。
/// </summary>
public class MotherboardAnalysisService : ObservableObject
{
    private readonly AiService _ai;

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    private string _manufacturer = string.Empty;
    public string Manufacturer { get => _manufacturer; set => SetProperty(ref _manufacturer, value); }

    private string _model = string.Empty;
    public string Model { get => _model; set => SetProperty(ref _model, value); }

    private string _serialNumber = string.Empty;
    public string SerialNumber { get => _serialNumber; set => SetProperty(ref _serialNumber, value); }

    private string _aiAnalysis = string.Empty;
    public string AiAnalysis { get => _aiAnalysis; set => SetProperty(ref _aiAnalysis, value); }

    private string _imageSearchUrl = string.Empty;
    public string ImageSearchUrl { get => _imageSearchUrl; set => SetProperty(ref _imageSearchUrl, value); }

    private bool _isConfirmed;
    public bool IsConfirmed { get => _isConfirmed; set => SetProperty(ref _isConfirmed, value); }

    private bool _hasError;
    public bool HasError { get => _hasError; set => SetProperty(ref _hasError, value); }

    private string _errorMessage = string.Empty;
    public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }

    private bool _hasResult;
    public bool HasResult { get => _hasResult; set => SetProperty(ref _hasResult, value); }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public string BoardDisplayName =>
        string.IsNullOrWhiteSpace(Manufacturer) && string.IsNullOrWhiteSpace(Model)
            ? "未偵測到主機板"
            : $"{Manufacturer} {Model}".Trim();

    public MotherboardAnalysisService(AiService ai)
    {
        _ai = ai;
    }

    public void CollectBoardInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementObject obj in searcher.Get())
            {
                Manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? string.Empty;
                Model        = obj["Product"]?.ToString()?.Trim() ?? string.Empty;
                SerialNumber = obj["SerialNumber"]?.ToString()?.Trim() ?? string.Empty;
                break;
            }
            OnPropertyChanged(nameof(BoardDisplayName));
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"WMI 讀取失敗：{ex.Message}";
        }
    }

    public void BuildImageSearchUrl(string? extraTerms = null)
    {
        var query = string.IsNullOrWhiteSpace(extraTerms)
            ? $"{Manufacturer} {Model} motherboard"
            : $"{Manufacturer} {Model} {extraTerms}";
        ImageSearchUrl = $"https://www.bing.com/images/search?q={Uri.EscapeDataString(query.Trim())}";
    }

    public async Task ConfirmImageAsync()
    {
        IsConfirmed = true;
        await AnalyzeAsync();
    }

    public void SearchAgain(string? refinedTerms = null)
    {
        IsConfirmed = false;
        HasResult   = false;
        AiAnalysis  = string.Empty;
        BuildImageSearchUrl(refinedTerms);
    }

    public async Task AnalyzeAsync()
    {
        if (!SharedAiEndpoint.IsConfigured)
        {
            HasError = true;
            ErrorMessage = "AI 服務尚未設定，請先在「設定」頁面配置 API 端點與金鑰。";
            return;
        }
        if (string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(Manufacturer))
        {
            HasError = true;
            ErrorMessage = "無法偵測主機板型號，無法進行 AI 分析。";
            return;
        }

        IsLoading = true;
        HasError  = false;
        HasResult = false;
        StatusMessage = "正在查詢 AI 分析主機板資訊…";

        try
        {
            var prompt =
                $"請詳細介紹這張主機板：{Manufacturer} {Model}。" +
                "包括：晶片組、CPU 插座、記憶體插槽數與支援規格、PCIe 插槽配置、" +
                "M.2 插槽數、供電相數、音效晶片、網路晶片、USB 規格、BIOS 特色。";

            await _ai.SendAsync(prompt);

            // SendAsync 把回覆寫進共用的 Messages 串（AI 頁那一份），這裡讀回來顯示。
            // 不另開一條 API 路徑：使用者的端點、模型與金鑰只有 AiService 知道。
            var reply = _ai.Messages.LastOrDefault(m => m.IsAssistant)?.Text;
            AiAnalysis = reply ?? "";
            HasResult  = AiAnalysis.Length > 0;
            if (!HasResult) ErrorMessage = "AI 沒有產生任何回覆——請確認設定頁的端點、模型與金鑰。";
            HasError = !HasResult;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"AI 分析失敗：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            StatusMessage = string.Empty;
        }
    }
}
