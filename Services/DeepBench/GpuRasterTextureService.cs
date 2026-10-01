using System.Diagnostics;

namespace XinSpect;

public sealed record GpuRasterSample(double GigapixelsPerSecond, double FrameTimeMs);

public sealed record GpuRasterRun(string AdapterName, uint FeatureLevel, int Width, int Height, IReadOnlyList<GpuRasterSample> Samples, uint ReadbackChecksum);

public sealed record GpuRasterWorkload(
    int Width,
    int Height,
    int TextureSize,
    int FramesPerSample,
    int WarmupSamples,
    int MeasureSamples);

public sealed record GpuRasterScenarioResult(string ScenarioId, GpuRasterRun Run);

public sealed record GpuRasterMeasurement(IReadOnlyList<GpuRasterScenarioResult> Scenarios);

public sealed record GpuRasterContext(
    GpuRasterWorkload Workload,
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface IGpuRasterEngine
{
    Task<GpuRasterMeasurement> MeasureAsync(GpuRasterContext context, CancellationToken cancellationToken);
}

public sealed class GpuRasterValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// GPU 光柵／紋理深測：D3D11 硬體裝置上以全螢幕三角形量純填充率、單取樣與八取樣紋理貼圖吞吐。
/// 只量本行程 render target 的實際 raster 輸出，含逐場景 readback 完整性檢查；WARP 一律拒收。
/// </summary>
public sealed class GpuRasterTextureService(IGpuRasterEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "gpu.raster-texture";

    public static string[] Limitations { get; } =
    [
        "量的是本程式全螢幕三角形在 render target 上的光柵輸出吞吐；實際遊戲的深度測試、混合、幾何負載與解析度都會大幅改變結果，不外推成遊戲效能。",
        "填充率場景以固定色全覆蓋輸出，readback 逐位元組對 CPU 參考驗證；紋理場景以 alpha 全 255 與像素變異確認 raster 真的執行，不宣稱與驅動內部計時一致。",
        "計時為 CPU 端 Draw 批次加 Flush 的牆鐘時間，不是 GPU timestamp；批次內含提交與同步開銷。",
        "WARP 與軟體渲染一律拒收；裝置移除即整場失敗，不推算替代值。",
        "三個場景並列輸出原始樣本與明示比率；不合成單一總分。",
    ];

    private readonly IGpuRasterEngine _engine = engine ?? new D3D11RasterEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "建立 D3D11 硬體裝置"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuRasterWorkload workload = GetWorkload(context.Profile);
            var engineContext = new GpuRasterContext(workload, context.Profile, context.Progress, cancellationToken);
            GpuRasterMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement, workload);
            return CreateResult(context, started, workload, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (GpuUnsupportedException exception)
        {
            return Unsupported(context, started, exception.Message);
        }
        catch (GpuRasterValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static GpuRasterWorkload GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(1280, 720, 2048, 120, 1, 3),
        DeepBenchRunProfile.Full => new(1280, 720, 2048, 240, 2, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static void Validate(GpuRasterMeasurement measurement, GpuRasterWorkload workload)
    {
        string[] expected = ["fill", "texture-single", "texture-8tap"];
        if (measurement.Scenarios.Count != expected.Length
            || measurement.Scenarios.Select(scenario => scenario.ScenarioId).SequenceEqual(expected) == false)
            throw new GpuRasterValidationException("光柵情境不完整或順序不一致；不推算缺失情境。");

        foreach (GpuRasterScenarioResult scenario in measurement.Scenarios)
        {
            GpuRasterRun run = scenario.Run;
            if (run.Width != workload.Width || run.Height != workload.Height)
                throw new GpuRasterValidationException($"{scenario.ScenarioId} 解析度與規劃不一致。");
            if (run.Samples.Count != workload.MeasureSamples)
                throw new GpuRasterValidationException($"{scenario.ScenarioId} 樣本數不一致；不推算缺失樣本。");
            if (run.Samples.Any(sample => !double.IsFinite(sample.GigapixelsPerSecond) || sample.GigapixelsPerSecond <= 0))
                throw new GpuRasterValidationException($"{scenario.ScenarioId} 吞吐出現非有限或非正數樣本；整場拒收。");
            if (run.ReadbackChecksum == 0)
                throw new GpuRasterValidationException($"{scenario.ScenarioId} readback checksum 為零；raster 可能沒有實際執行，整場拒收。");
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        GpuRasterWorkload workload,
        GpuRasterMeasurement measurement)
    {
        var scenarioMetrics = new (string Id, string Title, string Unit, GpuRasterScenarioResult Scenario)[]
        {
            ("gpu.raster.fill.gpix", "純填充率（全螢幕三角形）", "Gpix/s", measurement.Scenarios[0]),
            ("gpu.raster.texture-single.gpix", "紋理單取樣", "Gpix/s", measurement.Scenarios[1]),
            ("gpu.raster.texture-8tap.gpix", "紋理八取樣", "Gpix/s", measurement.Scenarios[2]),
        };

        List<DeepBenchMetric> metrics = [];
        foreach ((string id, string title, string unit, GpuRasterScenarioResult scenario) in scenarioMetrics)
        {
            metrics.Add(new(
                id,
                title,
                unit,
                true,
                $"{workload.Width}×{workload.Height} render target；{workload.FramesPerSample} 幀／樣本",
                scenario.Run.Samples.Select(sample => sample.GigapixelsPerSecond).ToArray(),
                []));
        }

        double singleMedian = Median(measurement.Scenarios[1].Run.Samples.Select(sample => sample.GigapixelsPerSecond).ToArray());
        double multiMedian = Median(measurement.Scenarios[2].Run.Samples.Select(sample => sample.GigapixelsPerSecond).ToArray());
        metrics.Add(new(
            "gpu.raster.texture-8tap-over-single.ratio",
            "八取樣相對單取樣成本",
            "ratio",
            false,
            "median(8tap) / median(single)；越接近 1 表示取樣壓力越不吃滿",
            [multiMedian / singleMedian],
            []));

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{workload.Width}×{workload.Height}；紋理 {workload.TextureSize}²；{workload.MeasureSamples} 樣本 × {workload.FramesPerSample} 幀",
            metrics,
            [
                $"配接器：{measurement.Scenarios[0].Run.AdapterName}；Feature Level 0x{measurement.Scenarios[0].Run.FeatureLevel:X4}；HLSL vs_5_0／ps_5_0。",
                $"三場景 readback checksum：{string.Join("、", measurement.Scenarios.Select(scenario => $"{scenario.ScenarioId}=0x{scenario.Run.ReadbackChecksum:X8}"))}。",
                "填充率場景逐位元組對 CPU 參考（uniform 色與 alpha=255）驗證；紋理場景以 alpha 全 255 與像素變異確認執行。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    internal static double Median(double[] samples)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string reason) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [],
            Limitations, DeepBenchFailureKind.Unsupported, reason);

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分場景補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失場景。"], kind, error);

    public const string HlslSource = """
        Texture2D SourceTex : register(t0);
        SamplerState TexSampler : register(s0);

        struct VSOutput
        {
            float4 Position : SV_Position;
            float2 UV : TEXCOORD0;
        };

        VSOutput VSMain(uint vertexId : SV_VertexID)
        {
            VSOutput output;
            float2 uv = float2((vertexId << 1u) & 2u, vertexId & 2u);
            output.Position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            output.UV = float2(uv.x, 1.0 - uv.y);
            return output;
        }

        float4 PSFill(VSOutput input) : SV_Target
        {
            return float4(0.25, 0.5, 0.75, 1.0);
        }

        float4 PSTextureSingle(VSOutput input) : SV_Target
        {
            return SourceTex.Sample(TexSampler, input.UV);
        }

        float4 PSTextureMulti(VSOutput input) : SV_Target
        {
            float4 sum = float4(0.0, 0.0, 0.0, 0.0);
            [unroll]
            for (int index = 0; index < 8; index++)
            {
                float2 offset = float2(index * 0.0031, index * -0.0017);
                sum += SourceTex.Sample(TexSampler, frac(input.UV + offset));
            }
            return sum / 8.0;
        }
        """;
}

/// <summary>D3D11 硬體引擎；細節槽位與裝置管理在 D3D11Native。</summary>
public sealed class D3D11RasterEngine : IGpuRasterEngine
{
    public Task<GpuRasterMeasurement> MeasureAsync(GpuRasterContext context, CancellationToken cancellationToken)
        => D3D11Native.MeasureRasterAsync(context, cancellationToken);
}
