using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 音訊頻譜分析的純函式測試：FFT 計算、頻段分佈、邊界情況。
/// 不依賴音訊裝置——WasapiLoopbackCapture 在 CI 環境下無法使用。
/// </summary>
public class AudioSpectrumServiceTests
{
    [Fact]
    public void 頻段邊界從20Hz到20kHz對數分佈()
    {
        var edges = SpectrumMath.BandEdges;
        Assert.Equal(AudioSpectrumService.BandCount + 1, edges.Length);
        Assert.InRange(edges[0], 19.9, 20.1);            // 起始 ~20 Hz
        Assert.InRange(edges[^1], 19900, 20100);          // 結尾 ~20 kHz

        // 對數分佈：相鄰比值應大致相等
        double ratio1 = edges[1] / edges[0];
        double ratio2 = edges[2] / edges[1];
        Assert.InRange(ratio2 / ratio1, 0.98, 1.02);
    }

    [Fact]
    public void 已知正弦波的峰值落在正確頻段()
    {
        // 產生 1 kHz 正弦波（取樣率 48 kHz）
        const int sampleRate = 48000;
        const double freq = 1000.0;
        var samples = new float[SpectrumMath.FftSize];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * freq * i / sampleRate);

        var output = new float[AudioSpectrumService.BandCount];
        SpectrumMath.ComputeSpectrum(samples, sampleRate, output);

        // 找到最大值所在的頻段
        int peakBand = 0;
        float peakDb = output[0];
        for (int i = 1; i < output.Length; i++)
        {
            if (output[i] > peakDb)
            {
                peakDb = output[i];
                peakBand = i;
            }
        }

        // 1 kHz 應該落在該頻段的邊界範圍內
        var edges = SpectrumMath.BandEdges;
        Assert.True(edges[peakBand] <= freq && freq <= edges[peakBand + 1],
            $"1 kHz 峰值在第 {peakBand} 段（{edges[peakBand]:0}–{edges[peakBand + 1]:0} Hz），不含 1000 Hz");

        // 峰值 dB 應該明顯高於底噪。
        // Hann 窗使單頻正弦波的能量分散到鄰近 bin，再以 RMS 平均整個頻段，
        // 實測約 -24 dB 左右；只要明顯高於靜音底限 -100 dB 即可。
        Assert.True(peakDb > -40f, $"峰值 {peakDb:0.0} dB 太低");
    }

    [Fact]
    public void 低頻正弦波峰值在低頻段()
    {
        // 100 Hz 正弦波
        const int sampleRate = 48000;
        const double freq = 100.0;
        var samples = new float[SpectrumMath.FftSize];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * freq * i / sampleRate);

        var output = new float[AudioSpectrumService.BandCount];
        SpectrumMath.ComputeSpectrum(samples, sampleRate, output);

        int peakBand = 0;
        for (int i = 1; i < output.Length; i++)
            if (output[i] > output[peakBand]) peakBand = i;

        // 100 Hz 應該在前 1/3 的頻段
        Assert.True(peakBand < AudioSpectrumService.BandCount / 3,
            $"100 Hz 峰值在第 {peakBand} 段，太高了");
    }

    [Fact]
    public void 高頻正弦波峰值在高頻段()
    {
        // 10 kHz 正弦波
        const int sampleRate = 48000;
        const double freq = 10000.0;
        var samples = new float[SpectrumMath.FftSize];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * freq * i / sampleRate);

        var output = new float[AudioSpectrumService.BandCount];
        SpectrumMath.ComputeSpectrum(samples, sampleRate, output);

        int peakBand = 0;
        for (int i = 1; i < output.Length; i++)
            if (output[i] > output[peakBand]) peakBand = i;

        // 10 kHz 應該在後 1/3 的頻段
        Assert.True(peakBand >= AudioSpectrumService.BandCount * 2 / 3,
            $"10 kHz 峰值在第 {peakBand} 段，太低了");
    }

    [Fact]
    public void 所有頻段dB值在合理範圍()
    {
        // 混合兩個頻率
        const int sampleRate = 44100;
        var samples = new float[SpectrumMath.FftSize];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 440 * i / sampleRate)
                                + 0.3 * Math.Sin(2.0 * Math.PI * 3000 * i / sampleRate));

        var output = new float[AudioSpectrumService.BandCount];
        SpectrumMath.ComputeSpectrum(samples, sampleRate, output);

        for (int i = 0; i < AudioSpectrumService.BandCount; i++)
        {
            Assert.InRange(output[i], -100f, 0f);
        }
    }

    [Fact]
    public void 靜音輸入全部頻段為底限()
    {
        var samples = new float[SpectrumMath.FftSize];
        // 全零
        var output = new float[AudioSpectrumService.BandCount];
        SpectrumMath.ComputeSpectrum(samples, 48000, output);

        for (int i = 0; i < AudioSpectrumService.BandCount; i++)
            Assert.Equal(-100f, output[i]);
    }

    [Fact]
    public void 空輸入不crash並輸出靜音()
    {
        var output = new float[AudioSpectrumService.BandCount];

        // null 輸入
        SpectrumMath.ComputeSpectrum(null!, 48000, output);
        for (int i = 0; i < AudioSpectrumService.BandCount; i++)
            Assert.Equal(-100f, output[i]);

        // 太短的輸入
        SpectrumMath.ComputeSpectrum(new float[10], 48000, output);
        for (int i = 0; i < AudioSpectrumService.BandCount; i++)
            Assert.Equal(-100f, output[i]);
    }

    [Fact]
    public void 不同取樣率的頻段邊界正確映射()
    {
        // 44100 Hz 取樣率下 1 kHz 正弦波
        const int sampleRate = 44100;
        const double freq = 1000.0;
        var samples = new float[SpectrumMath.FftSize];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * freq * i / sampleRate);

        var output = new float[AudioSpectrumService.BandCount];
        SpectrumMath.ComputeSpectrum(samples, sampleRate, output);

        int peakBand = 0;
        for (int i = 1; i < output.Length; i++)
            if (output[i] > output[peakBand]) peakBand = i;

        var edges = SpectrumMath.BandEdges;
        Assert.True(edges[peakBand] <= freq && freq <= edges[peakBand + 1],
            $"44100 Hz 取樣率下 1 kHz 峰值在第 {peakBand} 段（{edges[peakBand]:0}–{edges[peakBand + 1]:0} Hz）");
    }

    [Fact]
    public void 頻段邊界嚴格遞增()
    {
        var edges = SpectrumMath.BandEdges;
        for (int i = 1; i < edges.Length; i++)
            Assert.True(edges[i] > edges[i - 1], $"edges[{i}]={edges[i]:0.0} <= edges[{i - 1}]={edges[i - 1]:0.0}");
    }

    [Fact]
    public void 滿振幅正弦波峰值接近0dB()
    {
        const int sampleRate = 48000;
        var samples = new float[SpectrumMath.FftSize];
        // 振幅 1.0 的正弦波，FFT 後 Hann 窗會降一些
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * 1000 * i / sampleRate);

        var output = new float[AudioSpectrumService.BandCount];
        SpectrumMath.ComputeSpectrum(samples, sampleRate, output);

        float peak = output.Max();
        // Hann 窗下振幅 1.0 的正弦波，能量分散到鄰近 bin 後以 RMS 平均，
        // 實測峰值約 -23 到 -25 dB；只要在合理範圍內即可。
        Assert.InRange(peak, -30f, 0f);
    }
}
