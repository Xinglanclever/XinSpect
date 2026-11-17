namespace XinSpect;

/// <summary>Cached validation state. Passive sensor reads never start a CPU load.</summary>
public enum RaplGateVerdict
{
    Unknown = 0,
    Trusted = 1,
    NotResponding = 2,
    NoCounter = 3,
}

/// <summary>
/// Explicit, process-once RAPL diagnostic. No GUI/CLI passive reader starts it.
/// Every explicit entry requires the existing first-level diagnostics gate. The first
/// caller owns cancellation of the shared run; completion includes actual worker drain.
/// </summary>
public static class RaplEnergyGate
{
    private static readonly object Sync = new();
    private static readonly RaplEnergyProbe Probe = new();
    private static RaplProbeResult _result = new(RaplGateVerdict.Unknown, "能量計未驗證；被動讀取不啟動負載診斷。", 0);
    private static Task? _probeTask;

    public static RaplGateVerdict Verdict => Volatile.Read(ref _result).Verdict;
    public static string? Note => Volatile.Read(ref _result).Note;
    public static ulong LastRawCount => Volatile.Read(ref _result).LastRawCount;
    public static bool IsProbing => Volatile.Read(ref _probeTask) is { IsCompleted: false };
    public static string StateText => IsProbing ? "能量計探測中"
        : Verdict == RaplGateVerdict.Trusted ? "" : Note ?? "能量計未驗證";

    /// <summary>Only already-validated power is exposed; this function performs no work or source read.</summary>
    public static double? ValidatedPower(double? reportedPower) => Verdict == RaplGateVerdict.Trusted ? reportedPower : null;

    /// <summary>
    /// Explicit diagnostic request, never called from passive properties. Off policy and
    /// pre-cancelled requests refuse before readers, Task.Run or worker construction.
    /// The process-once task is retained, including faults, so no hidden retry is made.
    /// </summary>
    public static Task EnsureStarted(Func<ulong?> readEnergy, Func<double?> readTemp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readEnergy);
        ArgumentNullException.ThrowIfNull(readTemp);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (!HardwareOperationGate.CanRunAdvancedDiagnostics)
            return Task.FromException(new InvalidOperationException("RAPL 負載未執行：先啟用第一級進階診斷。"));
        lock (Sync)
        {
            return _probeTask ??= Task.Run(() => RunCoreAsync(readEnergy, readTemp, cancellationToken));
        }
    }

    public static Task RunOnceAsync(Func<ulong?> readEnergy, Func<double?> readTemp, CancellationToken cancellationToken = default) =>
        EnsureStarted(readEnergy, readTemp, cancellationToken);

    /// <summary>Explicit console diagnostic only; GUI must await the asynchronous entry.</summary>
    public static void RunOnce(Func<ulong?> readEnergy, Func<double?> readTemp, CancellationToken cancellationToken = default) =>
        EnsureStarted(readEnergy, readTemp, cancellationToken).GetAwaiter().GetResult();

    private static async Task RunCoreAsync(Func<ulong?> readEnergy, Func<double?> readTemp, CancellationToken token)
    {
        try
        {
            RaplProbeResult result = await Probe.RunAsync(readEnergy, readTemp, token).ConfigureAwait(false);
            Volatile.Write(ref _result, result);
        }
        catch (OperationCanceledException error)
        {
            Volatile.Write(ref _result, new(RaplGateVerdict.Unknown, "能量計未驗證：診斷取消或第一級閘門已關閉。" + error.Message, LastRawCount));
            throw;
        }
        catch (Exception error)
        {
            Volatile.Write(ref _result, new(RaplGateVerdict.Unknown, "能量計探測失敗；未驗證：" + error.Message, LastRawCount));
            throw;
        }
    }
}
