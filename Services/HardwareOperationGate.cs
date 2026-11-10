using System.Threading;

namespace XinSpect;

/// <summary>Two-level runtime gate for diagnostics and state-changing hardware operations.</summary>
public static class HardwareOperationGate
{
    private static SettingsService? _settings;
    private static readonly AsyncLocal<int> OperationDepth = new();

    public static bool CanRunAdvancedDiagnostics => Volatile.Read(ref _settings)?.AdvancedDiagnosticsEnabled == true;

    public static bool CanWriteHardware =>
        Volatile.Read(ref _settings)?.CanUseHighRiskHardwareOperations == true || OperationDepth.Value > 0;

    internal static void Bind(SettingsService? settings) => Volatile.Write(ref _settings, settings);

    /// <summary>Holds permission through a multi-write operation so cleanup can restore its original state.</summary>
    public static IDisposable? TryBeginHighRiskOperation()
    {
        if (Volatile.Read(ref _settings)?.CanUseHighRiskHardwareOperations != true && OperationDepth.Value == 0)
            return null;
        return EnterOperation();
    }

    /// <summary>Allows explicitly restorative writes after the high-risk mode has been turned off.</summary>
    internal static IDisposable BeginRecoveryOperation() => EnterOperation();

    private static IDisposable EnterOperation()
    {
        int prior = OperationDepth.Value;
        OperationDepth.Value = prior + 1;
        return new OperationScope(prior);
    }

    private sealed class OperationScope(int prior) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                OperationDepth.Value = prior;
        }
    }
}
