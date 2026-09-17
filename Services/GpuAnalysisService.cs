using System;
using System.Management;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// AI 輔助顯示卡分析服務。
/// 從 WMI 讀取顯示卡基本資訊，透過 AiService 查詢詳細規格，
/// 並提供圖片搜尋確認流程。
/// </summary>
public class GpuAnalysisService : ObservableObject
{
    private readonly AiService _ai;

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    private string _gpuName = string.Empty;
    public string GpuName { get => _gpuName; set => SetProperty(ref _gpuName, value); }

    private string _vram = string.Empty;
    public string Vram { get => _vram; set => SetProperty(ref _vram, value); }

    private string _driverVersion = string.Empty;
    public string DriverVersion { get => _driverVersion; set => SetProperty(ref _driverVersion, value); }

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

    public string GpuDisplayName =>
        string.IsNullOrWhiteSpace(GpuName)
            ? "未偵測到顯示卡"
            : GpuName;

    public GpuAnalysisService(AiService ai)
    {
        _ai = ai;
    }

    public void CollectGpuInfo()
    {
        try
        {
            // PNPDeviceID 要選進來：用來區分真顯卡（PCI\...）與虛擬/遠端顯示配接器，
            // 也用来反查正確的 VRAM 大小。
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion, PNPDeviceID FROM Win32_VideoController");

            string bestName = "", bestPnp = "", bestDriver = "";
            bool found = false;

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                var pnp = obj["PNPDeviceID"]?.ToString()?.Trim() ?? string.Empty;
                if (name.Length == 0) continue;

                // 虛擬／遠端顯示配接器排在真顯卡前面是常態（本機：MuMu、GameViewer 都在 TITAN Xp 之前），
                // 取第一個非 Microsoft 的會抓到虛擬顯示器，然後拿它的名字去問 AI。
                if (IsVirtualDisplay(name) || !pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
                    continue;

                bestName = name;
                bestPnp = pnp;
                bestDriver = obj["DriverVersion"]?.ToString()?.Trim() ?? "";
                found = true;
                break;
            }

            if (!found)
            {
                HasError = true;
                ErrorMessage = "未找到 PCI 實體顯示卡（只有虛擬或遠端顯示配接器）。";
                return;
            }

            GpuName = bestName;
            DriverVersion = bestDriver.Length > 0 ? bestDriver : "未知";
            // AdapterRAM 是 uint32（上限 4 GiB），大於 4 GB 的卡一律被截成 4.0 GB，
            // 所以改讀登錄檔的 qwMemorySize；讀不到才退回 AdapterRAM 並標明是截斷值。
            long? vram = ReadVramBytes(bestName);
            Vram = vram is > 0
                ? $"{vram.Value / (1024.0 * 1024.0 * 1024.0):F1} GB"
                : "—（WMI 的 AdapterRAM 是 32 位元，超過 4 GB 讀不準）";

            OnPropertyChanged(nameof(GpuDisplayName));
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"WMI 讀取失敗：{ex.Message}";
        }
    }

    /// <summary>虛擬、遠端或軟體顯示配接器——不是實體顯示卡。</summary>
    internal static bool IsVirtualDisplay(string name)
    {
        string[] marks =
        [
            "Microsoft", "Basic Display", "Virtual", "Remote", "RDP",
            "MuMu", "GameViewer", "Parsec", "Sunshine", "DisplayLink",
            "TeamViewer", "AnyDesk", "ToDesk", "Meta Virtual",
        ];
        return marks.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>從顯示類別機碼讀真正的 VRAM 位元組數（<c>HardwareInformation.qwMemorySize</c>）。</summary>
    /// <remarks>比對 <c>DriverDesc</c> 與顯卡名稱；找不到回 null，不猜。</remarks>
    internal static long? ReadVramBytes(string gpuName)
    {
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return null;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc")?.ToString()?.Trim() is not { } desc) continue;
                if (!string.Equals(desc, gpuName, StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValue("HardwareInformation.qwMemorySize") is { } qw)
                    return Convert.ToInt64(qw);
            }
        }
        catch { }
        return null;
    }

    public void BuildImageSearchUrl(string? extraTerms = null)
    {
        var query = string.IsNullOrWhiteSpace(extraTerms)
            ? $"{GpuName} graphics card"
            : $"{GpuName} {extraTerms}";
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
        if (string.IsNullOrWhiteSpace(GpuName))
        {
            HasError = true;
            ErrorMessage = "無法偵測顯示卡型號，無法進行 AI 分析。";
            return;
        }

        IsLoading = true;
        HasError  = false;
        HasResult = false;
        StatusMessage = "正在查詢 AI 分析顯示卡資訊…";

        try
        {
            var prompt =
                $"請詳細介紹這張顯示卡：{GpuName}。" +
                "包括：GPU 晶片、製程、CUDA/Stream 核心數、基礎/Boost 時脈、" +
                "記憶體規格與頻寬、TDP、建議電源供應器瓦數、適合的遊戲解析度、同級競品比較。";

            await _ai.SendAsync(prompt);

            // 同主機板分析：回覆在共用的 Messages 串裡，讀回來顯示。
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
