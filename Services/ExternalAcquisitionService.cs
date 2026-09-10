namespace XinSpect;

/// <summary>
/// 外接採集設備的統一介面。具體實作需要廠商 SDK（PicoScope、Saleae、NI 等）。
/// 目前僅提供介面定義與降級路徑——沒有硬體時如實回報「未偵測到裝置」。
/// </summary>
public interface IExternalAcquisition : IDisposable
{
    /// <summary>裝置名稱（偵測到時顯示型號，未偵測到時顯示「無」）。</summary>
    string DeviceName { get; }

    /// <summary>是否已連接可用的採集裝置。</summary>
    bool IsConnected { get; }

    /// <summary>採樣率（Hz）。未連接時為 0。</summary>
    double SampleRateHz { get; }

    /// <summary>通道數。</summary>
    int Channels { get; }

    /// <summary>開始擷取。</summary>
    bool StartCapture();

    /// <summary>停止擷取。</summary>
    void StopCapture();

    /// <summary>讀取最新的緩衝區資料（時域）。未連接時回空陣列。</summary>
    float[] ReadBuffer();

    /// <summary>最後一次操作的狀態訊息。</summary>
    string Status { get; }
}

/// <summary>
/// 無外接裝置時的降級實作。所有方法安全回傳預設值，不拋例外。
/// </summary>
public sealed class NullAcquisition : IExternalAcquisition
{
    public string DeviceName => "未偵測到外接採集裝置";
    public bool IsConnected => false;
    public double SampleRateHz => 0;
    public int Channels => 0;
    public bool StartCapture() => false;
    public void StopCapture() { }
    public float[] ReadBuffer() => [];
    public string Status => "此功能需要外接採集硬體（如 PicoScope、Saleae Logic 等）及對應的廠商 SDK。"
        + "目前未偵測到任何相容裝置。";
    public void Dispose() { }
}
