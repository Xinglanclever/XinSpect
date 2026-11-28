using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace XinSpect;

public interface IDriverStoreBackend
{
    DriverStoreInventory ReadInventory(CancellationToken cancellationToken);
    Task<DriverStoreProcessResult> ExportPackageAsync(string publishedName, string destination, CancellationToken cancellationToken);
}

public sealed record DriverStoreProcessResult(int ExitCode, string Output);

/// <summary>Structured DISM package inventory + WMI device bindings; only export uses PnPUtil.</summary>
/// <remarks>ABI: https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dismdriverpackage-structure
/// Signature scope: https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dismdriversignature-enumeration
/// Export syntax / Windows 10 1607+: https://learn.microsoft.com/windows-hardware/drivers/devtest/pnputil-command-syntax
/// WMI is a device-association report, not proof a package is loaded or safe to remove.</remarks>
public sealed class DriverStoreNativeBackend : IDriverStoreBackend
{
    // DISM global lifecycle is serialized across instances. Cancellation never releases a live native call.
    private static readonly SemaphoreSlim DismGate = new(1, 1);
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";
    private const int MaxPackages = 32768;

    public DriverStoreInventory ReadInventory(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) || !Environment.Is64BitProcess)
            return new() { Availability = FactAvailability.NotSupported, Reason = "需要 64 位元 Windows 10 1607 或更新版本（本程式 win-x64）。" };
        DismGate.Wait(cancellationToken);
        try
        {
            var packages = ReadDism(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bindings = ReadBindings(cancellationToken);
                return AttachBindings(packages, bindings, FactAvailability.Present, "WMI 列舉完成；僅代表 provider 回報的裝置關聯。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TimeoutException or IOException)
            {
                return AttachBindings(packages, [], DriverStoreService.ClassifyException(ex), "WMI 裝置綁定失敗：" + ex.Message);
            }
        }
        finally { DismGate.Release(); }
    }

    internal static DriverStoreInventory AttachBindings(IReadOnlyList<DriverStorePackage> packages,
        IReadOnlyList<DriverStoreBinding> bindings, FactAvailability bindingAvailability, string reason)
    {
        var groups = bindings.GroupBy(b => b.PublishedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DriverStoreBinding>)g.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        return new()
        {
            Availability = FactAvailability.Present, Reason = "DISM 套件列舉完成。",
            BindingAvailability = bindingAvailability, BindingReason = reason,
            Packages = packages.Select(p => p with
            {
                Bindings = bindingAvailability == FactAvailability.Present && groups.TryGetValue(p.PublishedName, out var b) ? b : [],
                BindingAvailability = bindingAvailability, BindingReason = reason,
            }).OrderBy(p => p.InBox).ThenBy(p => p.PublishedName, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static List<DriverStorePackage> ReadDism(CancellationToken token)
    {
        bool initialized = false, opened = false;
        uint session = 0;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            Check(Native.DismInitialize(0, null, null), "DismInitialize");
            initialized = true;
            token.ThrowIfCancellationRequested();
            Check(Native.DismOpenSession(OnlineImage, null, null, out session), "DismOpenSession(Online)");
            opened = true;
            token.ThrowIfCancellationRequested();
            Check(Native.DismGetDrivers(session, true, out buffer, out uint count), "DismGetDrivers");
            if (count > MaxPackages || (count != 0 && buffer == IntPtr.Zero))
                throw new InvalidDataException("DISM 傳回無效或超出預算的套件陣列。");
            var packages = new List<DriverStorePackage>((int)count);
            int stride = Marshal.SizeOf<DismDriverPackage>();
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var n = Marshal.PtrToStructure<DismDriverPackage>(IntPtr.Add(buffer, checked(i * stride)));
                packages.Add(new()
                {
                    PublishedName = Text(n.PublishedName), OriginalFileName = Text(n.OriginalFileName),
                    InBox = n.InBox != 0, BootCritical = n.BootCritical != 0, CatalogFile = Text(n.CatalogFile),
                    ClassName = Text(n.ClassName), ClassGuid = Text(n.ClassGuid), Provider = Text(n.ProviderName),
                    Signature = Enum.IsDefined(typeof(DriverStoreSignature), n.DriverSignature) ? (DriverStoreSignature)n.DriverSignature : DriverStoreSignature.Unknown,
                    Version = $"{n.MajorVersion}.{n.MinorVersion}.{n.Build}.{n.Revision}", DriverDate = n.Date.ToDate(),
                });
            }
            return packages;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Native.DismDelete(buffer);
            if (opened) Native.DismCloseSession(session);
            if (initialized) Native.DismShutdown();
        }
    }

    private static string Text(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p) ?? "";
    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new COMException($"{operation} 失敗（HRESULT 0x{hr:X8}）。", hr);
    }

    private static List<DriverStoreBinding> ReadBindings(CancellationToken token)
    {
        var budget = new DriverStoreBindingReadBudget();
        // Per-call timeout is not an overall deadline. Semisynchronous single-row batches let
        // the separate monotonic budget reject late results between native WMI calls.
        var options = new System.Management.EnumerationOptions
        {
            Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true, Rewindable = false, BlockSize = 1,
        };
        using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
            "SELECT InfName, DeviceID, DeviceName FROM Win32_PnPSignedDriver", options);
        budget.Check(token);
        using var result = searcher.Get();
        return CollectBindings(EnumerateRows(), budget, token);

        IEnumerable<DriverStoreBinding> EnumerateRows()
        {
            foreach (ManagementObject row in result)
                using (row)
                    // Yield every row, including blank INF rows: the budget counts work, not matches.
                    yield return new(row["InfName"] as string ?? "", row["DeviceID"] as string ?? "", row["DeviceName"] as string ?? "");
        }
    }

    internal static List<DriverStoreBinding> CollectBindings(IEnumerable<DriverStoreBinding> rows,
        DriverStoreBindingReadBudget budget, CancellationToken token)
    {
        var bindings = new List<DriverStoreBinding>();
        using var enumerator = rows.GetEnumerator();
        while (true)
        {
            budget.Check(token);
            if (!enumerator.MoveNext()) { budget.Check(token); break; }
            budget.ObserveRow(token);
            var row = enumerator.Current;
            if (row.PublishedName.Length != 0) bindings.Add(row);
        }
        return bindings;
    }

    public async Task<DriverStoreProcessResult> ExportPackageAsync(string publishedName, string destination, CancellationToken token)
    {
        // Internal API remains an export-only primitive even if a future caller bypasses the UI.
        if (!DriverStoreService.IsPublishedOemInf(publishedName)) throw new ArgumentException("僅接受明確的 oem#.inf。", nameof(publishedName));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)) throw new PlatformNotSupportedException("PnPUtil /export-driver 需要 Windows 10 1607。");
        using (var identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("匯出需使用者自行以系統管理員啟動；不會自動提權。");
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        if (!File.Exists(executable)) throw new PlatformNotSupportedException("找不到系統 PnPUtil。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
        };
        start.ArgumentList.Add("/export-driver");
        start.ArgumentList.Add(publishedName);
        start.ArgumentList.Add(destination);
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("無法啟動系統 PnPUtil。");
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* Still wait for actual exit below. */ }
            // Wait until the exporter stops before hashing or reporting a final result.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new TimeoutException("PnPUtil 匯出超過每套件 5 分鐘預算，已停止匯出程序；部分檔案保留。");
        }
        return new(process.ExitCode, (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false)));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            if (text.Length < 16384) text.Append(buffer, 0, Math.Min(read, 16384 - text.Length));
        return text.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DismSystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        public readonly DateTime? ToDate()
        {
            try { return new DateTime(Year, Month, Day); } catch (ArgumentOutOfRangeException) { return null; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DismDriverPackage
    {
        public IntPtr PublishedName, OriginalFileName;
        public int InBox;
        public IntPtr CatalogFile, ClassName, ClassGuid, ClassDescription;
        public int BootCritical, DriverSignature;
        public IntPtr ProviderName;
        public DismSystemTime Date;
        public uint MajorVersion, MinorVersion, Build, Revision;
    }

    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DismApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int DismInitialize(int level, string? logFile, string? scratchDirectory);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DismApi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int DismOpenSession(string image, string? windows, string? drive, out uint session);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DismApi.dll", ExactSpelling = true)]
        internal static extern int DismGetDrivers(uint session, [MarshalAs(UnmanagedType.Bool)] bool allDrivers, out IntPtr packages, out uint count);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DismApi.dll", ExactSpelling = true)] internal static extern int DismDelete(IntPtr memory);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DismApi.dll", ExactSpelling = true)] internal static extern int DismCloseSession(uint session);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DismApi.dll", ExactSpelling = true)] internal static extern int DismShutdown();
    }
}

/// <summary>Bounds all WMI rows and total elapsed time, independently of returned nonblank bindings.</summary>
internal sealed class DriverStoreBindingReadBudget
{
    private readonly int _maxRows;
    private readonly TimeSpan _deadline;
    private readonly Func<TimeSpan> _elapsed;
    private int _rows;
    internal DriverStoreBindingReadBudget(int maxRows = 131072, TimeSpan? deadline = null, Func<TimeSpan>? elapsed = null)
    {
        if (maxRows < 1) throw new ArgumentOutOfRangeException(nameof(maxRows));
        _maxRows = maxRows; _deadline = deadline ?? TimeSpan.FromSeconds(30);
        if (_deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        var stopwatch = Stopwatch.StartNew();
        _elapsed = elapsed ?? (() => stopwatch.Elapsed);
    }
    internal void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_elapsed() >= _deadline) throw new TimeoutException("WMI 裝置綁定列舉超過整體時間預算；拒絕發布部分結果。");
    }
    internal void ObserveRow(CancellationToken token)
    {
        Check(token);
        if (++_rows > _maxRows) throw new InvalidDataException("WMI 總列舉列數超出預算（含空 INF 列）。");
    }
}
