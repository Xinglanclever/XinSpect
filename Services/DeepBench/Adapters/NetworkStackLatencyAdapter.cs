using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace XinSpect;

public interface INetworkLatencyEngine
{
    Task<NetworkLatencyMeasurement> MeasureAsync(int probes, int warmup, CancellationToken cancellationToken);
}

public sealed record NetworkLatencyMeasurement(IReadOnlyList<double> RttMicroseconds);

/// <summary>
/// 本機 TCP loopback 64-byte request/response 延遲深測。
/// 只量 127.0.0.1 的 socket 往返；不外連、不宣稱 LAN、Wi-Fi 或 Internet 品質。
/// </summary>
public sealed class NetworkStackLatencyAdapter(INetworkLatencyEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "ux.network-stack-latency";

    private readonly INetworkLatencyEngine _engine = engine ?? new TcpLoopbackLatencyEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int probes, int warmup) = GetWorkload(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.05, $"本機 TCP loopback 暖機 {warmup} 次"));
            NetworkLatencyMeasurement measurement = await _engine.MeasureAsync(
                probes,
                warmup,
                cancellationToken).ConfigureAwait(false);
            ValidateMeasurement(measurement, probes);

            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.96, "整理原始樣本"));
            double[] samples = measurement.RttMicroseconds.ToArray();
            var points = samples.Select((value, index) => new DeepBenchMetricPoint(
                value,
                new Dictionary<string, string>
                {
                    ["probe"] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                [value])).ToArray();
            var metric = new DeepBenchMetric(
                "ux.network-stack.loopback-rtt-us",
                "Loopback request/response RTT",
                "µs",
                false,
                $"127.0.0.1 TCP; {probes} probes after {warmup} warmups; 64-byte payload",
                samples,
                points);

            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                $"127.0.0.1 TCP；{probes} 次量測、{warmup} 次暖機；64-byte 往返",
                [metric],
                [
                    "伺服器與用戶端同在本機；每次樣本從 client write 開始，到收滿 64-byte 回覆結束。",
                    "暖機不計入樣本；原始 µs 樣本全部保留，不合成總分。",
                ],
                [
                    "結果只代表本機 127.0.0.1 socket stack；不是 LAN 延遲、不是 Wi-Fi 延遲、不是實體 NIC 延遲、也不是 Internet 延遲。",
                    "防火牆、安全軟體、背景 I/O、排程與電源狀態都會改變結果。",
                    "不推算頻寬、封包遺失或遠端網路品質。",
                ],
                DeepBenchFailureKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (InvalidNetworkLatencyMeasurementException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static (int Probes, int Warmup) GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => (256, 16),
        DeepBenchRunProfile.Full => (1024, 32),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static void ValidateMeasurement(NetworkLatencyMeasurement measurement, int probes)
    {
        if (measurement.RttMicroseconds.Count != probes)
        {
            throw new InvalidNetworkLatencyMeasurementException($"探測數不符：需要 {probes}，收到 {measurement.RttMicroseconds.Count}。");
        }

        if (measurement.RttMicroseconds.Any(value => !double.IsFinite(value) || value <= 0))
        {
            throw new InvalidNetworkLatencyMeasurementException("網路 stack 樣本出現非有限或非正數延遲；整場拒收，不改成零。");
        }
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後不補量。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不從未完成量測推算。"], kind, error);
}

internal sealed class InvalidNetworkLatencyMeasurementException(string message) : Exception(message);

public sealed class TcpLoopbackLatencyEngine : INetworkLatencyEngine
{
    private const int PayloadBytes = 64;

    public async Task<NetworkLatencyMeasurement> MeasureAsync(
        int probes,
        int warmup,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(probes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(warmup, 0);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task serverTask = ServeAsync(listener, cancellationToken);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await using NetworkStream stream = client.GetStream();
        var buffer = new byte[PayloadBytes];

        for (int i = 0; i < warmup; i++)
        {
            await RoundTripAsync(stream, buffer, i, cancellationToken).ConfigureAwait(false);
        }

        var samples = new List<double>(probes);
        for (int i = 0; i < probes; i++)
        {
            samples.Add(await RoundTripAsync(stream, buffer, i, cancellationToken).ConfigureAwait(false));
        }

        stream.Close();
        await serverTask.ConfigureAwait(false);
        return new NetworkLatencyMeasurement(samples);
    }

    private static async Task<double> RoundTripAsync(
        NetworkStream stream,
        byte[] buffer,
        int sequence,
        CancellationToken cancellationToken)
    {
        buffer[0] = (byte)sequence;
        long timestamp = Stopwatch.GetTimestamp();
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(timestamp).TotalMicroseconds;
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using TcpClient server = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        await using NetworkStream stream = server.GetStream();
        var buffer = new byte[PayloadBytes];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return total;
    }
}
