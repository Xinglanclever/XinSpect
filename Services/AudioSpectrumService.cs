using System.Numerics;

namespace XinSpect;

/// <summary>
/// 系統混音輸出的 FFT 頻譜分析服務：以 NAudio WasapiLoopbackCapture 擷取系統音訊，
/// 對 PCM 資料做 FFT 並輸出 32 個頻段的 dB 值（20 Hz – 20 kHz 對數分佈）。
/// </summary>
/// <remarks>
/// <para>
/// 設計考量：
/// <list type="bullet">
///   <item>NAudio 的 WasapiLoopbackCapture 在 CI／無音訊裝置環境下會 throw，
///         所以建構時以 try-catch 包住：失敗則降級為「無音訊裝置」，不會 crash。</item>
///   <item>FFT 計算（<see cref="SpectrumMath"/>）與裝置擷取完全分離，方便單元測試。</item>
///   <item>輸出 32 個頻段以對數頻率分佈：低頻佔較多頻段，符合人耳感知。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class AudioSpectrumService : IDisposable
{
    /// <summary>頻段數量。</summary>
    public const int BandCount = 32;

    private NAudio.Wave.WasapiLoopbackCapture? _capture;
    private float[]? _sampleBuffer;
    private int _samplePos;
    private int _channels;
    private int _sampleRate;
    private readonly object _lock = new();

    /// <summary>目前 32 頻段的 dB 值（-100 到 0）；未擷取時全為 -100。</summary>
    public float[] SpectrumData { get; } = new float[BandCount];

    /// <summary>是否正在擷取。</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>無音訊裝置時為 <c>true</c>；此時 <see cref="Start"/> 不做任何事。</summary>
    public bool NoDevice { get; private set; }

    /// <summary>頻譜資料更新時觸發（在 NAudio 的擷取執行緒上）。</summary>
    public event Action? SpectrumUpdated;

    /// <summary>開始擷取。無音訊裝置時安靜回傳。</summary>
    public void Start()
    {
        if (IsCapturing) return;

        try
        {
            _capture = new NAudio.Wave.WasapiLoopbackCapture();
        }
        catch
        {
            // 無音訊裝置或裝置無法初始化
            NoDevice = true;
            return;
        }

        var fmt = _capture.WaveFormat;
        _channels = fmt.Channels;
        _sampleRate = fmt.SampleRate;

        // FFT 需要 2 的冪次方樣本數；4096 在 48 kHz 下約 85 ms 的窗格
        _sampleBuffer = new float[SpectrumMath.FftSize];
        _samplePos = 0;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        try
        {
            _capture.StartRecording();
            IsCapturing = true;
        }
        catch
        {
            // 裝置在初始化與開始錄音之間被移除等罕見情況
            DisposeCapture();
            NoDevice = true;
        }
    }

    /// <summary>停止擷取。</summary>
    public void Stop()
    {
        if (!IsCapturing) return;
        IsCapturing = false;
        try { _capture?.StopRecording(); } catch { /* 裝置已拔除 */ }
    }

    private void OnDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        if (_sampleBuffer is null) return;

        // PCM 資料為 IEEE float（WasapiLoopbackCapture 預設格式）
        int bytesPerSample = 4; // 32-bit float
        int sampleCount = e.BytesRecorded / bytesPerSample;

        for (int i = 0; i < sampleCount; i += _channels)
        {
            // 混合為單聲道
            float mono = 0;
            for (int ch = 0; ch < _channels && (i + ch) < sampleCount; ch++)
                mono += BitConverter.ToSingle(e.Buffer, (i + ch) * bytesPerSample);
            mono /= _channels;

            lock (_lock)
            {
                _sampleBuffer[_samplePos++] = mono;
                if (_samplePos >= SpectrumMath.FftSize)
                {
                    SpectrumMath.ComputeSpectrum(_sampleBuffer, _sampleRate, SpectrumData);
                    _samplePos = 0;
                    SpectrumUpdated?.Invoke();
                }
            }
        }
    }

    private void OnRecordingStopped(object? sender, NAudio.Wave.StoppedEventArgs e)
    {
        IsCapturing = false;
    }

    private void DisposeCapture()
    {
        if (_capture is null) return;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try { _capture.Dispose(); } catch { /* 裝置已消失 */ }
        _capture = null;
    }

    public void Dispose()
    {
        Stop();
        DisposeCapture();
    }
}

/// <summary>
/// 頻譜分析的純函式（無硬體依賴，可單元測試）。
/// </summary>
/// <remarks>
/// FFT 使用 NAudio 內建的 <see cref="NAudio.Dsp.FastFourierTransform"/>；
/// 結果以 32 個對數頻段（20 Hz – 20 kHz）回傳 dB 值。
/// </remarks>
public static class SpectrumMath
{
    /// <summary>FFT 取樣點數（必須為 2 的冪）。</summary>
    public const int FftSize = 4096;

    /// <summary>log2(4096) = 12。</summary>
    private const int FftExponent = 12;

    /// <summary>32 個頻段的邊界頻率（Hz），從 20 Hz 到 20000 Hz 對數分佈。</summary>
    public static readonly double[] BandEdges = BuildBandEdges(AudioSpectrumService.BandCount);

    /// <summary>計算頻譜：對 <paramref name="samples"/> 做 FFT，結果寫入 <paramref name="output"/>（32 個 dB 值）。</summary>
    /// <param name="samples">時域樣本，長度必須為 <see cref="FftSize"/>。</param>
    /// <param name="sampleRate">取樣率（Hz）。</param>
    /// <param name="output">輸出陣列，長度必須 >= <see cref="AudioSpectrumService.BandCount"/>。</param>
    public static void ComputeSpectrum(float[] samples, int sampleRate, float[] output)
    {
        if (samples is null || samples.Length < FftSize)
        {
            // 空輸入或不足：輸出靜音
            Array.Fill(output, -100f, 0, Math.Min(output.Length, AudioSpectrumService.BandCount));
            return;
        }

        // 複製到 Complex 陣列並套用 Hann 窗
        var fft = new NAudio.Dsp.Complex[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            double window = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            fft[i].X = (float)(samples[i] * window);
            fft[i].Y = 0;
        }

        NAudio.Dsp.FastFourierTransform.FFT(true, FftExponent, fft);

        // 頻率解析度
        double binHz = (double)sampleRate / FftSize;
        int usableBins = FftSize / 2;   // Nyquist 以下

        for (int band = 0; band < AudioSpectrumService.BandCount; band++)
        {
            double loHz = BandEdges[band];
            double hiHz = BandEdges[band + 1];

            int binLo = Math.Max(1, (int)Math.Floor(loHz / binHz));
            int binHi = Math.Min(usableBins - 1, (int)Math.Ceiling(hiHz / binHz));

            double sumMagSq = 0;
            int count = 0;
            for (int bin = binLo; bin <= binHi; bin++)
            {
                double re = fft[bin].X;
                double im = fft[bin].Y;
                sumMagSq += re * re + im * im;
                count++;
            }

            double rms = count > 0 ? Math.Sqrt(sumMagSq / count) : 0;
            // 轉 dB（以 1.0 為 0 dB，靜音底限 -100 dB）
            double db = rms > 1e-10 ? 20.0 * Math.Log10(rms) : -100.0;
            output[band] = (float)Math.Clamp(db, -100.0, 0.0);
        }
    }

    /// <summary>建立 <paramref name="bandCount"/> + 1 個邊界頻率（對數分佈）。</summary>
    public static double[] BuildBandEdges(int bandCount)
    {
        const double MinHz = 20.0;
        const double MaxHz = 20000.0;
        double logMin = Math.Log10(MinHz);
        double logMax = Math.Log10(MaxHz);
        var edges = new double[bandCount + 1];
        for (int i = 0; i <= bandCount; i++)
            edges[i] = Math.Pow(10, logMin + (logMax - logMin) * i / bandCount);
        return edges;
    }
}
